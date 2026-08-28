using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface ISmartPlugImportRepository
{
    // Persists the import and all of its parsed readings (if any) as a single unit — a partially
    // persisted import (row without its readings, or vice versa) is never a valid state to
    // observe from Story 3.2/3.3's later reads.
    Task AddAsync(SmartPlugImport import, IReadOnlyList<SmartPlugReading> readings, CancellationToken cancellationToken);

    // Lets GET /api/jobs/{id} surface the import's own sub-status (e.g. AwaitingPowerPointMapping)
    // alongside the generic BackgroundJob status, since "Completed" alone doesn't tell the client
    // whether the import fully attached to a Power Point.
    Task<SmartPlugImport?> FindByBackgroundJobIdAsync(Guid backgroundJobId, CancellationToken cancellationToken);

    // HTTP-context lookup for Story 3.2's mapping flow (relies on EnergyTrackerDbContext's AD-3
    // query filter, same as ITaggingScaffoldRepository's Find methods) — distinct from
    // FindByBackgroundJobIdAsync, which is job-context (Story 3.1's ProcessSmartPlugImport).
    Task<SmartPlugImport?> FindByIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken);

    // Read-only from here on (AsNoTracking) — the only caller (MapSmartPlugImportToPowerPoint)
    // used to mutate and re-persist this same list, but UpdateMappingAsync now writes
    // PowerPointId/PowerPointName/RoomName via a set-based UPDATE instead, so nothing downstream
    // needs these entities tracked.
    Task<IReadOnlyList<SmartPlugReading>> ListReadingsByImportIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken);

    // A single set-based UPDATE (EF Core's ExecuteUpdateAsync) against every SmartPlugReading row
    // for this import, plus the import row's own Status/CompletedAtUtc. Deliberately NOT the
    // load-every-row-then-track-then-diff pattern AddAsync/other methods here use — a large Eve
    // Home import can carry hundreds of thousands of readings, and loading + change-tracking that
    // many rows blew the default 30s SQL command timeout on Basic-tier Azure SQL on every mapping
    // attempt. The readings UPDATE and the import row's SaveChangesAsync are two separate
    // round trips (not one transaction like AddAsync) — a crash between them leaves readings
    // already correctly attributed but the import row still AwaitingPowerPointMapping, which a
    // retry safely repeats (the UPDATE is idempotent).
    Task UpdateMappingAsync(
        SmartPlugImport import, Guid powerPointId, string powerPointName, string? roomName, CancellationToken cancellationToken);

    // "Prior" readings for the same Power Point, across all of ITS OTHER imports, bounded to
    // `sinceDate` and later (excludes `excludeSmartPlugImportId` — the import currently being
    // completed, whose own readings are passed separately into SmartPlugGapDetector) — the
    // cross-import trailing-average lookup Task 2's algorithm needs. Bounded rather than the
    // Power Point's full history, since only the trailing window immediately before the current
    // import's own covered range can ever be read by SmartPlugGapDetector.BuildGap. Ordered by
    // IntervalStart (Story 3.3).
    Task<IReadOnlyList<SmartPlugReading>> ListPriorReadingsByPowerPointAsync(
        Guid powerPointId, Guid excludeSmartPlugImportId, DateOnly sinceDate, CancellationToken cancellationToken);

    // The Power Point's single earliest-ever SmartPlugReading date (a cheap indexed MIN lookup,
    // not a full history scan) — feeds SmartPlugGapDetector's "has a genuine full preceding week
    // elapsed since first-ever reading" rule (AC #6) without needing ListPriorReadingsByPowerPointAsync
    // to load unbounded history just to compute the same thing. `null` only when the Power Point has
    // no persisted reading at all yet.
    Task<DateOnly?> FindFirstReadingDateByPowerPointAsync(Guid powerPointId, CancellationToken cancellationToken);

    // Story 3.4: the Power Point's latest stored SmartPlugReading.IntervalStart — the watermark
    // ProcessSmartPlugImport passes into ISmartPlugParser.Parse so a repeat import only reads/
    // persists genuinely new rows (AC #1, #3). Mirrors FindFirstReadingDateByPowerPointAsync's
    // exact shape, just OrderByDescending. `null` only when the Power Point has no persisted
    // reading at all yet (AC #4 — parse the full file).
    Task<DateTimeOffset?> FindLatestReadingIntervalStartByPowerPointAsync(Guid powerPointId, CancellationToken cancellationToken);

    // Single SaveChangesAsync — one transaction, mirroring every other method here. Gaps are
    // insert-only (immutable after creation, AD-7/NFR9's precedent) — never called to update an
    // existing gap row.
    Task AddGapsAsync(IReadOnlyList<SmartPlugImportGap> gaps, CancellationToken cancellationToken);

    // Lets GET /api/jobs/{id} surface an import's detected gaps alongside its own status (Task 5).
    Task<IReadOnlyList<SmartPlugImportGap>> ListGapsByImportIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken);

    // Batch-load, keyed by SmartPlugImportId — review-round-2 patch: lets ListSmartPlugImportJobs
    // resolve every Flagged for Review row's gap in one query instead of N+1, mirroring
    // FindAllByBackgroundJobIdsAsync's batching for the same method.
    Task<IReadOnlyList<SmartPlugImportGap>> ListGapsByImportIdsAsync(
        IReadOnlyList<Guid> smartPlugImportIds, CancellationToken cancellationToken);

    // AC #7: an import whose file parsed to zero rows at all — persists the SmartPlugImport
    // (FlaggedForReview, no readings) and its single whole-file gap row together, one transaction,
    // mirroring AddAsync's "no partially persisted import" discipline.
    Task AddFlaggedForReviewAsync(SmartPlugImport import, SmartPlugImportGap gap, CancellationToken cancellationToken);

    // Batch-load, keyed by BackgroundJobId — lets ListSmartPlugImportJobs (Story 3.6) resolve
    // each Completed job's own SmartPlugImport.Status in one query instead of N+1.
    Task<IReadOnlyList<SmartPlugImport>> FindAllByBackgroundJobIdsAsync(
        IReadOnlyList<Guid> backgroundJobIds, CancellationToken cancellationToken);

    // Story 3.6/AD-6 extension: deletes every SmartPlugImport (+ its SmartPlugImportGap rows, +
    // its BackgroundJob row) that completed before cutoffUtc AND reached a terminal, resolved
    // state — Success (SmartPlugImportStatus.Completed) or Error (BackgroundJobStatus.Failed) or
    // Flagged for Review (SmartPlugImportStatus.FlaggedForReview). Needs Mapping
    // (AwaitingPowerPointMapping) is explicitly excluded regardless of age — the background job
    // finished, but the import itself is still unresolved. SmartPlugReading rows are never
    // deleted, only detached via the SetNull FK (AD-20).
    Task SweepExpiredAsync(Guid householdId, DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}
