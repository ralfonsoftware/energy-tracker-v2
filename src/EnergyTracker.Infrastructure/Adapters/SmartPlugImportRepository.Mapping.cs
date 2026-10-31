using System.Data.Common;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

public partial class SmartPlugImportRepository
{
    // AD-23 carve-out: a metadata re-tag of persisted rows stays off BulkInsertOrUpdateAsync. The fallback is set-based
    // itself since the 2026-09-18 incident (a full-history re-export colliding on ~100% of its rows). ADR 0006.
    public async Task UpdateMappingAsync(
        SmartPlugImport import, Guid powerPointId, string powerPointName, string? roomName, CancellationToken cancellationToken)
    {
        // 30s is too short for a set-based UPDATE over a whole import on Basic-tier Azure SQL; the readback after this call
        // reads the same row count, so the 180s applies to the rest of the request.
        dbContext.Database.SetCommandTimeout(TimeSpan.FromSeconds(180));

        if (await AnyMappingConflictAsync(import.Id, powerPointId, cancellationToken))
        {
            // A collision is already known: skip the doomed single UPDATE and classify set-based straight away.
            await UpdateMappingSetBasedWithConflictToleranceAsync(import.Id, powerPointId, powerPointName, roomName, cancellationToken);
        }
        else
        {
            try
            {
                // One set-based UPDATE when no reading collides with the (PowerPointId, IntervalStart) unique constraint (AD-20).
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
                // Only a race after the pre-check reaches here. ExecuteUpdateAsync does not wrap the provider's exception in
                // DbUpdateException, so catch the portable DbException (AD-2).
                await UpdateMappingSetBasedWithConflictToleranceAsync(import.Id, powerPointId, powerPointName, roomName, cancellationToken);
            }
        }

        // The import is already tracked by this scoped context, so SaveChangesAsync alone persists its Status/CompletedAtUtc.
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

    // Classify once in memory from one bulk fetch, then apply two set-based statements (ADR 0006).
    // Both queries below are AsNoTracking projections.
    private async Task UpdateMappingSetBasedWithConflictToleranceAsync(
        Guid smartPlugImportId, Guid powerPointId, string powerPointName, string? roomName, CancellationToken cancellationToken)
    {
        // AD-3's global query filter scopes every query below, including the projected and the bulk ones.
        var importReadings = await dbContext.SmartPlugReadings
            .AsNoTracking()
            .Where(r => r.SmartPlugImportId == smartPlugImportId)
            .Select(r => new { r.Id, r.IntervalStart, r.DeviceName, r.KwhValue, r.IntervalEnd })
            .ToListAsync(cancellationToken);

        if (importReadings.Count == 0)
        {
            return;
        }

        // One bulk fetch of the already-mapped readings that could collide, keyed by IntervalStart. Contains(...) is one JSON array
        // parameter on both providers. It must exclude this import's own readings, or a repeated request deletes them (ADR 0006).
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

            // DeviceName is part of the exact-duplicate match: a Power Point can hold readings of several devices.
            if (existing.DeviceName == reading.DeviceName
                && existing.KwhValue == reading.KwhValue
                && existing.IntervalEnd == reading.IntervalEnd)
            {
                exactDuplicates.Add((reading.Id, reading.IntervalStart));
            }
            else
            {
                // Divergent data at the same key (e.g. a DST fall-back) is never discarded: the reading stays unmapped and is logged here.
                logger.LogWarning(
                    "Skipped mapping SmartPlugReading {SmartPlugReadingId} (import {SmartPlugImportId}) to PowerPointId={PowerPointId}: " +
                    "a reading already exists at IntervalStart={IntervalStart:O} for that Power Point (unique-constraint conflict, " +
                    "possibly a DST fall-back duplicate local timestamp).",
                    reading.Id, smartPlugImportId, powerPointId, reading.IntervalStart);
            }
        }

        if (noConflictIds.Count > 0)
        {
            // Same set-based idiom as the fast path, for the non-colliding subset.
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
            // Exact duplicates are dead data now that the mapped row is authoritative: delete them in one statement.
            var exactDuplicateIds = exactDuplicates.Select(d => d.Id).ToList();
            var deletedCount = await dbContext.SmartPlugReadings
                .Where(r => exactDuplicateIds.Contains(r.Id))
                .ExecuteDeleteAsync(cancellationToken);

            // Log what was classified, not a per-row "Deleted" claim: a row already gone (sweep, concurrent request) is no error.
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
