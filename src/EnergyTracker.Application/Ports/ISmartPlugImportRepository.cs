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

    Task<IReadOnlyList<SmartPlugReading>> ListReadingsByImportIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken);

    // Mirrors AddAsync's single-SaveChangesAsync pattern — one transaction, so a partially
    // updated import/readings set is never observable by a later read.
    Task UpdateMappingAsync(SmartPlugImport import, IReadOnlyList<SmartPlugReading> readings, CancellationToken cancellationToken);

    // "Prior" readings for the same Power Point, across all of ITS OTHER imports (excludes
    // `excludeSmartPlugImportId` — the import currently being completed, whose own readings are
    // passed separately into SmartPlugGapDetector) — the cross-import trailing-average lookup
    // Task 2's algorithm needs. Ordered by IntervalStart (Story 3.3).
    Task<IReadOnlyList<SmartPlugReading>> ListPriorReadingsByPowerPointAsync(
        Guid powerPointId, Guid excludeSmartPlugImportId, CancellationToken cancellationToken);

    // Single SaveChangesAsync — one transaction, mirroring every other method here. Gaps are
    // insert-only (immutable after creation, AD-7/NFR9's precedent) — never called to update an
    // existing gap row.
    Task AddGapsAsync(IReadOnlyList<SmartPlugImportGap> gaps, CancellationToken cancellationToken);

    // Lets GET /api/jobs/{id} surface an import's detected gaps alongside its own status (Task 5).
    Task<IReadOnlyList<SmartPlugImportGap>> ListGapsByImportIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken);

    // AC #7: an import whose file parsed to zero rows at all — persists the SmartPlugImport
    // (FlaggedForReview, no readings) and its single whole-file gap row together, one transaction,
    // mirroring AddAsync's "no partially persisted import" discipline.
    Task AddFlaggedForReviewAsync(SmartPlugImport import, SmartPlugImportGap gap, CancellationToken cancellationToken);
}
