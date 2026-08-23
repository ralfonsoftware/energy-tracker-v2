using System.Data.Common;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

public class SmartPlugImportRepository(
    EnergyTrackerDbContext dbContext, ILogger<SmartPlugImportRepository> logger) : ISmartPlugImportRepository
{
    public async Task AddAsync(SmartPlugImport import, IReadOnlyList<SmartPlugReading> readings, CancellationToken cancellationToken)
    {
        if (readings.Count > 0 && await AnyExistingReadingAtSameKeyAsync(readings, cancellationToken))
        {
            // A pre-existing reading already occupies at least one of this batch's keys — go
            // straight to the per-row-tolerant fallback instead of a bulk save we already know
            // will fail. Story 3.4 Dev Notes Open Question #2: without this check, a 100k+-row
            // first-time import degraded to one row per round-trip the instant any single row
            // collided, undercutting AC #7's own performance goal. The common case (a genuinely
            // new Power Point/Household with zero prior readings) never even reaches the per-key
            // query below.
            await AddWithPerRowConflictToleranceAsync(import, readings, cancellationToken);
            return;
        }

        await dbContext.SmartPlugImports.AddAsync(import, cancellationToken);
        if (readings.Count > 0)
        {
            await dbContext.SmartPlugReadings.AddRangeAsync(readings, cancellationToken);
        }

        try
        {
            // Single SaveChangesAsync — one transaction, so a partially persisted import (row
            // without its readings) is never observable by a later read (Story 3.2/3.3), in the
            // overwhelmingly common case where no reading collides with the new
            // (PowerPointId, IntervalStart) unique constraint (AD-20).
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The pre-check above already ruled out every conflict it could see — only reachable
            // via a genuine race that appeared after that check ran (e.g. a concurrent completion
            // for the same Power Point). Start clean and fall back to per-row tolerance for the
            // whole batch.
            dbContext.ChangeTracker.Clear();
            await AddWithPerRowConflictToleranceAsync(import, readings, cancellationToken);
        }
    }

    private async Task<bool> AnyExistingReadingAtSameKeyAsync(IReadOnlyList<SmartPlugReading> readings, CancellationToken cancellationToken)
    {
        var powerPointId = readings[0].PowerPointId;
        var householdId = readings[0].HouseholdId;

        var hasAnyExistingForKey = powerPointId is { } id
            ? await dbContext.SmartPlugReadings.AnyAsync(r => r.PowerPointId == id, cancellationToken)
            : await dbContext.SmartPlugReadings.AnyAsync(r => r.PowerPointId == null && r.HouseholdId == householdId, cancellationToken);

        if (!hasAnyExistingForKey)
        {
            return false;
        }

        var intervalStarts = readings.Select(r => r.IntervalStart).ToList();
        return powerPointId is { } matchedId
            ? await dbContext.SmartPlugReadings.AnyAsync(
                r => r.PowerPointId == matchedId && intervalStarts.Contains(r.IntervalStart), cancellationToken)
            : await dbContext.SmartPlugReadings.AnyAsync(
                r => r.PowerPointId == null && r.HouseholdId == householdId && intervalStarts.Contains(r.IntervalStart),
                cancellationToken);
    }

    private async Task AddWithPerRowConflictToleranceAsync(
        SmartPlugImport import, IReadOnlyList<SmartPlugReading> readings, CancellationToken cancellationToken)
    {
        // The failed SaveChangesAsync above leaves every entity it touched in an indeterminate
        // tracked state — start clean so the steps below don't re-attempt already-tracked entries.
        dbContext.ChangeTracker.Clear();

        // Step 1: persist the SmartPlugImport row alone, on its own SaveChangesAsync, before any
        // reading is touched. If this were combined with the per-reading loop below, the first
        // colliding reading's failed save would roll back the still-pending import insert too —
        // silently losing the whole import (row + every reading) on one collision, worse than the
        // duplication bug this story fixes.
        await dbContext.SmartPlugImports.AddAsync(import, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // ProcessSmartPlugImport's cancellation handler assumes no SmartPlugImport row
            // survives a cancelled import (it deliberately skips persisting a Failed row on
            // cancellation) — but this Id comes from the caller's queue message payload and is
            // reused verbatim on redelivery. If this insert had already committed, a retry's
            // AddAsync would collide on the same primary key and cascade into an unhandled
            // exception instead of resuming. Nothing to clean up here (the insert above never
            // completed), but see the matching cleanup after Step 2 below for the case where it did.
            throw;
        }

        // Step 2: one reading at a time, isolating and skipping only the genuinely conflicting
        // row(s) — the literal "ignore-on-conflict" behavior AC #6 asks for, at the cost of
        // splitting what's normally one atomic import+readings transaction for this rare
        // fallback case only.
        try
        {
            foreach (var reading in readings)
            {
                await dbContext.SmartPlugReadings.AddAsync(reading, cancellationToken);
                try
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    dbContext.Entry(reading).State = EntityState.Detached;

                    // Confirm this failure is actually the expected (PowerPointId, IntervalStart)
                    // unique-constraint conflict rather than something unrelated (AD-2 forbids
                    // inspecting provider-specific error codes here, so re-querying the portable
                    // way is how this gets isolated) — an unrelated failure must surface, not
                    // silently vanish a reading with no audit trail. A same-timestamp collision
                    // most commonly means two readings landed on the same local wall-clock
                    // IntervalStart (e.g. a DST fall-back fold under AD-9's zero-offset local
                    // time) — the earlier-processed reading above always wins, this one is dropped.
                    var conflictConfirmed = reading.PowerPointId is { } conflictPowerPointId
                        ? await dbContext.SmartPlugReadings.AsNoTracking().AnyAsync(
                            r => r.PowerPointId == conflictPowerPointId && r.IntervalStart == reading.IntervalStart, cancellationToken)
                        : await dbContext.SmartPlugReadings.AsNoTracking().AnyAsync(
                            r => r.PowerPointId == null && r.HouseholdId == reading.HouseholdId && r.IntervalStart == reading.IntervalStart,
                            cancellationToken);

                    if (!conflictConfirmed)
                    {
                        throw;
                    }

                    logger.LogWarning(
                        "Skipped SmartPlugReading for import {SmartPlugImportId}: a reading already exists at " +
                        "PowerPointId={PowerPointId} IntervalStart={IntervalStart:O} (unique-constraint conflict, " +
                        "possibly a DST fall-back duplicate local timestamp).",
                        import.Id, reading.PowerPointId, reading.IntervalStart);
                }
            }
        }
        catch (Exception)
        {
            // Review-round-2 patch: not just OperationCanceledException — a genuine (non-conflict)
            // DbUpdateException rethrown from the inner catch above can also leave Step 1's
            // SmartPlugImport row, and any readings already saved earlier in this loop, committed.
            // Clean up on any failure here so a retry (or the caller's own Failed-import write,
            // which reuses this same Id) doesn't collide on a half-persisted import.
            await DeletePartiallyPersistedImportAsync(import.Id, cancellationToken);
            throw;
        }
    }

    private async Task DeletePartiallyPersistedImportAsync(Guid smartPlugImportId, CancellationToken cancellationToken)
    {
        // Best-effort cleanup on a failed fallback: delete whatever this import managed to
        // commit so a retry (which reuses the same SmartPlugImportId from the queue payload) can
        // insert cleanly instead of colliding on the primary key. Runs with CancellationToken.None
        // — cancellation is exactly why this cleanup is often needed, so it must not itself be
        // cancelled. Review-round-2 patch: wrapped in its own try/catch — a cleanup failure (e.g.
        // the DB connection already closing during shutdown) must never replace/mask the original
        // exception this method was called to clean up after.
        try
        {
            dbContext.ChangeTracker.Clear();
            await dbContext.SmartPlugReadings
                .Where(r => r.SmartPlugImportId == smartPlugImportId)
                .ExecuteDeleteAsync(CancellationToken.None);
            await dbContext.SmartPlugImports
                .Where(i => i.Id == smartPlugImportId)
                .ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception cleanupException)
        {
            logger.LogWarning(cleanupException,
                "Best-effort cleanup of partially persisted SmartPlugImport {SmartPlugImportId} failed; " +
                "a retry reusing this Id may collide on the surviving row(s).", smartPlugImportId);
        }
    }

    public Task<SmartPlugImport?> FindByBackgroundJobIdAsync(Guid backgroundJobId, CancellationToken cancellationToken) =>
        dbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.BackgroundJobId == backgroundJobId, cancellationToken);

    public Task<SmartPlugImport?> FindByIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken) =>
        dbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.Id == smartPlugImportId, cancellationToken);

    public async Task<IReadOnlyList<SmartPlugReading>> ListReadingsByImportIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugReadings
            .AsNoTracking()
            .Where(r => r.SmartPlugImportId == smartPlugImportId)
            .ToListAsync(cancellationToken);

    public async Task UpdateMappingAsync(
        SmartPlugImport import, Guid powerPointId, string powerPointName, string? roomName, CancellationToken cancellationToken)
    {
        // The default 30s ADO.NET command timeout is tuned for point queries, not a set-based
        // UPDATE across a full import's rows on Basic-tier Azure SQL (5 DTU) — a large Eve Home
        // export (tens of thousands of rows) reliably exceeded it in production ("Execution Timeout
        // Expired" surfaced to the caller as a 500). Raised for the rest of this scoped DbContext's
        // request too, since the readback in MapSmartPlugImportToPowerPoint.ExecuteAsync right
        // after this call reads the same row count under the same DTU ceiling.
        dbContext.Database.SetCommandTimeout(TimeSpan.FromSeconds(180));

        if (await AnyMappingConflictAsync(import.Id, powerPointId, cancellationToken))
        {
            // Story 3.4 Dev Notes Open Question #4: at least one of this import's readings already
            // collides with an already-mapped reading at the same IntervalStart for the target
            // Power Point — skip the doomed set-based attempt (avoids a wasted round trip on a
            // large import) and go straight to the bounded per-row fallback.
            await UpdateMappingPerRowWithConflictToleranceAsync(import.Id, powerPointId, powerPointName, roomName, cancellationToken);
        }
        else
        {
            try
            {
                // One set-based UPDATE server-side — no loading/tracking/diffing hundreds of
                // thousands of rows for a large import (see this method's doc comment on the port
                // interface), in the common case where no reading collides with the new
                // (PowerPointId, IntervalStart) unique constraint (AD-20).
                await dbContext.SmartPlugReadings
                    .Where(r => r.SmartPlugImportId == import.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.PowerPointId, powerPointId)
                        .SetProperty(r => r.PowerPointName, powerPointName)
                        .SetProperty(r => r.RoomName, r => roomName ?? r.RoomName),
                        cancellationToken);
            }
            catch (Exception ex) when (ex is DbUpdateException or DbException)
            {
                // The pre-check above already ruled out every conflict it could see — only
                // reachable via a genuine race that appeared after that check ran.
                // ExecuteUpdateAsync is a bulk operation that bypasses the change-tracker
                // SaveChanges pipeline entirely — unlike AddAsync's SaveChangesAsync, it does NOT
                // wrap the provider's native ADO.NET exception (Npgsql's PostgresException/
                // SqlClient's SqlException) in a DbUpdateException, so the portable base type
                // (System.Data.Common.DbException, AD-2 — never a provider-specific exception type
                // in shared Infrastructure code) must be caught here too, confirmed empirically
                // against a real Postgres constraint violation during dev-story activation.
                await UpdateMappingPerRowWithConflictToleranceAsync(import.Id, powerPointId, powerPointName, roomName, cancellationToken);
            }
        }

        // import is already tracked by this same scoped DbContext (loaded via FindByIdAsync
        // earlier in the same request) — only its Status/CompletedAtUtc changed, so
        // SaveChangesAsync alone is enough. Also flushes any import-row change that a per-row
        // fallback above left pending if every one of its own per-reading saves happened to
        // collide.
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> AnyMappingConflictAsync(Guid importId, Guid powerPointId, CancellationToken cancellationToken)
    {
        var hasAnyExistingForPowerPoint = await dbContext.SmartPlugReadings.AnyAsync(r => r.PowerPointId == powerPointId, cancellationToken);
        if (!hasAnyExistingForPowerPoint)
        {
            return false;
        }

        var intervalStarts = await dbContext.SmartPlugReadings
            .Where(r => r.SmartPlugImportId == importId)
            .Select(r => r.IntervalStart)
            .ToListAsync(cancellationToken);

        return await dbContext.SmartPlugReadings.AnyAsync(
            r => r.PowerPointId == powerPointId && intervalStarts.Contains(r.IntervalStart), cancellationToken);
    }

    private async Task UpdateMappingPerRowWithConflictToleranceAsync(
        Guid smartPlugImportId, Guid powerPointId, string powerPointName, string? roomName, CancellationToken cancellationToken)
    {
        var readings = await dbContext.SmartPlugReadings
            .Where(r => r.SmartPlugImportId == smartPlugImportId)
            .ToListAsync(cancellationToken);

        foreach (var reading in readings)
        {
            var previousPowerPointId = reading.PowerPointId;
            reading.PowerPointId = powerPointId;
            reading.PowerPointName = powerPointName;
            reading.RoomName = roomName ?? reading.RoomName;

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                dbContext.Entry(reading).State = EntityState.Detached;

                // Confirm this is really the (PowerPointId, IntervalStart) unique-constraint
                // conflict this fallback exists for (AD-2 — no provider-specific error inspection)
                // rather than an unrelated failure that would otherwise vanish silently.
                var conflictConfirmed = await dbContext.SmartPlugReadings.AsNoTracking().AnyAsync(
                    r => r.PowerPointId == powerPointId && r.IntervalStart == reading.IntervalStart, cancellationToken);
                if (!conflictConfirmed)
                {
                    reading.PowerPointId = previousPowerPointId;
                    throw;
                }

                logger.LogWarning(
                    "Skipped mapping SmartPlugReading {SmartPlugReadingId} (import {SmartPlugImportId}) to PowerPointId={PowerPointId}: " +
                    "a reading already exists at IntervalStart={IntervalStart:O} for that Power Point (unique-constraint conflict, " +
                    "possibly a DST fall-back duplicate local timestamp).",
                    reading.Id, smartPlugImportId, powerPointId, reading.IntervalStart);
            }
        }
    }

    public async Task<DateTimeOffset?> FindLatestReadingIntervalStartByPowerPointAsync(Guid powerPointId, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugReadings
            .Where(r => r.PowerPointId == powerPointId)
            .OrderByDescending(r => r.IntervalStart)
            .Select(r => (DateTimeOffset?)r.IntervalStart)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SmartPlugReading>> ListPriorReadingsByPowerPointAsync(
        Guid powerPointId, Guid excludeSmartPlugImportId, DateOnly sinceDate, CancellationToken cancellationToken)
    {
        // AD-9: SmartPlugReading.IntervalStart is a local-time date encoded with a zero UTC offset
        // — match that encoding here rather than comparing against a real-offset instant.
        var sinceInstant = new DateTimeOffset(sinceDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return await dbContext.SmartPlugReadings
            .Where(r => r.PowerPointId == powerPointId
                && r.SmartPlugImportId != excludeSmartPlugImportId
                && r.IntervalStart >= sinceInstant)
            .OrderBy(r => r.IntervalStart)
            .ToListAsync(cancellationToken);
    }

    public async Task<DateOnly?> FindFirstReadingDateByPowerPointAsync(Guid powerPointId, CancellationToken cancellationToken)
    {
        var first = await dbContext.SmartPlugReadings
            .Where(r => r.PowerPointId == powerPointId)
            .OrderBy(r => r.IntervalStart)
            .Select(r => (DateTimeOffset?)r.IntervalStart)
            .FirstOrDefaultAsync(cancellationToken);
        return first is { } value ? DateOnly.FromDateTime(value.DateTime) : null;
    }

    public async Task AddGapsAsync(IReadOnlyList<SmartPlugImportGap> gaps, CancellationToken cancellationToken)
    {
        await dbContext.SmartPlugImportGaps.AddRangeAsync(gaps, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SmartPlugImportGap>> ListGapsByImportIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugImportGaps
            .Where(g => g.SmartPlugImportId == smartPlugImportId)
            .OrderBy(g => g.StartDate)
            .ToListAsync(cancellationToken);

    public async Task AddFlaggedForReviewAsync(SmartPlugImport import, SmartPlugImportGap gap, CancellationToken cancellationToken)
    {
        await dbContext.SmartPlugImports.AddAsync(import, cancellationToken);
        await dbContext.SmartPlugImportGaps.AddAsync(gap, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
