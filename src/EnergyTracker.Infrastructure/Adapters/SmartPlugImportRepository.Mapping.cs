using System.Data.Common;
using EFCore.BulkExtensions;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

public partial class SmartPlugImportRepository
{
    // AD-23's own explicit carve-out (verbatim reasoning) — NOT migrated to BulkInsertOrUpdateAsync
    // alongside AddAsync above, and this is deliberate, not an oversight: "It operates on a
    // fundamentally different, inherently small and bounded volume — one already-persisted
    // import's rows, already validated, being re-tagged with a new Power Point/Room (a metadata
    // re-tag), not a bulk insert-or-upsert-by-content decision over a fresh, potentially huge
    // parsed batch. The throughput problem this AD exists to solve doesn't apply there, so it
    // deliberately stays on its existing mechanism rather than being forced onto
    // BulkInsertOrUpdateAsync."
    //
    // [AMENDED 2026-09-18] The "small/bounded volume" half of that reasoning was invalidated by a
    // production incident (a full-history re-export colliding on ~100% of an existing Power
    // Point's rows) — see invariants-rules.md's AD-23 section for the full amendment. The
    // conflict-tolerant fallback below is now internally set-based too, but the carve-out's
    // conclusion (never BulkInsertOrUpdateAsync/EFCore.BulkExtensions for this method) is
    // unchanged: this is still a metadata re-tag over already-persisted, already-validated rows,
    // not a bulk insert-or-upsert-by-content decision over a fresh parsed batch.
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
            // Power Point — skip the doomed single-statement ExecuteUpdateAsync attempt (avoids a
            // wasted round trip on a large import) and go straight to the set-based conflict
            // classification fallback.
            await UpdateMappingSetBasedWithConflictToleranceAsync(import.Id, powerPointId, powerPointName, roomName, cancellationToken);
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
                await UpdateMappingSetBasedWithConflictToleranceAsync(import.Id, powerPointId, powerPointName, roomName, cancellationToken);
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

    // Incident fix (2026-09-18, this bugfix): replaces the former per-row loop
    // (UpdateMappingPerRowWithConflictToleranceAsync — one SaveChangesAsync per reading, plus an
    // extra SELECT+conditional-ExecuteDeleteAsync per collision) whose O(n) round trips took ~4
    // minutes against a full-history Eve Home re-export that collided on ~100% of its rows,
    // exceeding Azure Container Apps' ~240s ingress timeout (HTTP 499) even though the
    // classification logic itself (Story 3.7) was correct. Classification is now done once, in
    // memory, from a single bulk-fetched result set, then applied via exactly two set-based
    // statements — never load full SmartPlugReading entities into the change tracker for this
    // path (both queries below are AsNoTracking projections).
    private async Task UpdateMappingSetBasedWithConflictToleranceAsync(
        Guid smartPlugImportId, Guid powerPointId, string powerPointName, string? roomName, CancellationToken cancellationToken)
    {
        // HouseholdId scoping is implicit for every query below, same as the deleted per-row
        // fallback: AD-3's global query filter (wired once in EnergyTrackerDbContext.OnModelCreating)
        // applies to any LINQ query against SmartPlugReadings through this DbContext, projected
        // bulk queries and ExecuteUpdateAsync/ExecuteDeleteAsync included — not just full-entity loads.
        var importReadings = await dbContext.SmartPlugReadings
            .AsNoTracking()
            .Where(r => r.SmartPlugImportId == smartPlugImportId)
            .Select(r => new { r.Id, r.IntervalStart, r.DeviceName, r.KwhValue, r.IntervalEnd })
            .ToListAsync(cancellationToken);

        if (importReadings.Count == 0)
        {
            return;
        }

        // One bulk fetch of every already-mapped reading at the target Power Point whose
        // IntervalStart could collide with this import's readings — a dictionary keyed by
        // IntervalStart built from a single result set (Design Notes: safe even at large N,
        // unlike a per-row query). EF Core 10 translates List<Guid>/List<DateTimeOffset>.Contains
        // as a JSON-array parameter (verified for both SQL Server's OPENJSON and Npgsql's
        // ANY(@array) translation), not one parameter per value (Design Notes), so this doesn't
        // hit the 2100-parameter ceiling even for very large imports.
        //
        // Review finding (2026-09-18, this bugfix's own review loop): MUST exclude this import's
        // own readings (`r.SmartPlugImportId != smartPlugImportId`) — without it, a concurrent or
        // duplicate mapping request for the SAME import (e.g. a user double-clicking "Map" while
        // an earlier, still-processing request is slow) can see this import's OWN just-committed
        // rows here after the first request commits. Since a reading trivially matches itself on
        // DeviceName/KwhValue/IntervalEnd, the second request would classify every one of its own
        // already-correctly-mapped rows as an "exact duplicate" of itself and delete them all —
        // silently destroying a fully-succeeded import. The old per-row loop never had this
        // failure mode: it updated each row by its own Id (a no-op re-write when already correct),
        // never re-classified a row's relationship to itself.
        var intervalStarts = importReadings.Select(r => r.IntervalStart).ToList();
        var existingByIntervalStart = await dbContext.SmartPlugReadings
            .AsNoTracking()
            .Where(r => r.PowerPointId == powerPointId
                && r.SmartPlugImportId != smartPlugImportId
                && intervalStarts.Contains(r.IntervalStart))
            .Select(r => new { r.IntervalStart, r.DeviceName, r.KwhValue, r.IntervalEnd })
            .ToDictionaryAsync(r => r.IntervalStart, cancellationToken);

        var noConflictIds = new List<Guid>();
        var exactDuplicates = new List<(Guid Id, DateTimeOffset IntervalStart)>();

        foreach (var reading in importReadings)
        {
            if (!existingByIntervalStart.TryGetValue(reading.IntervalStart, out var existing))
            {
                noConflictIds.Add(reading.Id);
                continue;
            }

            // Story 3.7 AC #1/#2 classification, unchanged from the deleted per-row fallback:
            // DeviceName must be part of the exact-duplicate match — a Power Point can receive
            // manually-mapped readings from more than one distinct SmartPlugImport/device over
            // time (MapSmartPlugImportToPowerPoint imposes no device-identity constraint), so two
            // different devices' readings could otherwise coincide on
            // IntervalStart/KwhValue/IntervalEnd without actually being the same duplicate.
            if (existing.DeviceName == reading.DeviceName
                && existing.KwhValue == reading.KwhValue
                && existing.IntervalEnd == reading.IntervalEnd)
            {
                exactDuplicates.Add((reading.Id, reading.IntervalStart));
            }
            else
            {
                // AC #2: genuinely divergent data at the same key (e.g. a DST fall-back duplicate
                // local timestamp) — never silently discard data that might actually differ; leave
                // the reading unmapped (PowerPointId stays null via the ExecuteUpdateAsync below,
                // which only ever touches noConflictIds) and log it straight from this in-memory
                // classification — no per-row DB round trip for the log path.
                logger.LogWarning(
                    "Skipped mapping SmartPlugReading {SmartPlugReadingId} (import {SmartPlugImportId}) to PowerPointId={PowerPointId}: " +
                    "a reading already exists at IntervalStart={IntervalStart:O} for that Power Point (unique-constraint conflict, " +
                    "possibly a DST fall-back duplicate local timestamp).",
                    reading.Id, smartPlugImportId, powerPointId, reading.IntervalStart);
            }
        }

        if (noConflictIds.Count > 0)
        {
            // Same set-based idiom as UpdateMappingAsync's own fast path above, scoped to just the
            // non-colliding subset of this import's rows via their projected Id.
            await dbContext.SmartPlugReadings
                .Where(r => noConflictIds.Contains(r.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.PowerPointId, powerPointId)
                    .SetProperty(r => r.PowerPointName, powerPointName)
                    .SetProperty(r => r.RoomName, r => roomName ?? r.RoomName),
                    cancellationToken);
        }

        if (exactDuplicates.Count > 0)
        {
            // AC #1: dead data now that the mapped row is authoritative — delete every
            // exact-duplicate colliding row in one statement instead of one DELETE per row
            // (Story 3.4 Dev Notes Open Question #4's AD-20 gap, confirmed live in production at
            // 179,324-row scale — see this story's Context).
            var exactDuplicateIds = exactDuplicates.Select(d => d.Id).ToList();
            var deletedCount = await dbContext.SmartPlugReadings
                .Where(r => exactDuplicateIds.Contains(r.Id))
                .ExecuteDeleteAsync(cancellationToken);

            // Review finding (2026-09-18): log what was actually classified/attempted, not a
            // per-row "Deleted" claim we can't back up from a single batched DELETE's row count
            // alone. The I/O matrix's concurrent-delete race (a row already gone by the time this
            // DELETE runs — e.g. the 30-day sweep, or another concurrent mapping request) still
            // isn't an error, but it's no longer silently misreported as "Deleted duplicate X" for
            // a row that may not have still been there.
            if (deletedCount < exactDuplicateIds.Count)
            {
                logger.LogWarning(
                    "Import {SmartPlugImportId}: {ClassifiedCount} SmartPlugReading(s) classified as exact duplicates for " +
                    "PowerPointId={PowerPointId}, but only {DeletedCount} were still present to delete — the rest were already " +
                    "removed by a concurrent operation before this batch's DELETE ran.",
                    smartPlugImportId, exactDuplicateIds.Count, powerPointId, deletedCount);
            }

            foreach (var (id, intervalStart) in exactDuplicates)
            {
                logger.LogWarning(
                    "Resolved SmartPlugReading {SmartPlugReadingId} (import {SmartPlugImportId}) as a duplicate of an already-mapped " +
                    "reading instead of mapping it to PowerPointId={PowerPointId}: identical DeviceName/KwhValue/IntervalEnd already " +
                    "exists at IntervalStart={IntervalStart:O} for that Power Point; removed if still present.",
                    id, smartPlugImportId, powerPointId, intervalStart);
            }
        }
    }
}
