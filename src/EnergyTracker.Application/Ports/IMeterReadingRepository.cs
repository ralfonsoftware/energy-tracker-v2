using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface IMeterReadingRepository
{
    Task<MeterReading?> FindByIdempotencyKeyAsync(Guid idempotencyKey, CancellationToken cancellationToken);

    Task<MainMeter> GetOrCreateMainMeterAsync(Guid householdId, CancellationToken cancellationToken);

    Task<MeterReading> AddAsync(MeterReading reading, CancellationToken cancellationToken);

    // The reading with the greatest ReadingTimestamp strictly less than the given timestamp, for
    // the same Main Meter. Used by regression detection — "immediately preceding" is always by
    // chronological ReadingTimestamp, never CreatedAtUtc/entry order (AC #4).
    Task<MeterReading?> FindImmediatelyPrecedingAsync(Guid mainMeterId, DateTimeOffset readingTimestamp, CancellationToken cancellationToken);

    Task<MeterReading?> FindByIdAsync(Guid readingId, CancellationToken cancellationToken);

    // Read-only — deliberately does NOT create a MainMeter when none exists yet (unlike
    // GetOrCreateMainMeterAsync above), so a pure Status read never has the side effect of
    // inserting a row for a Household that has never logged a single reading.
    Task<MainMeter?> FindMainMeterByHouseholdAsync(Guid householdId, CancellationToken cancellationToken);

    // Full ordered sequence for one Main Meter, needed by Story 2.4's gap-tolerant pace walk.
    // Ordered by ReadingTimestamp then Id — the same deterministic tiebreak on identical
    // timestamps used by FindImmediatelyPrecedingAsync/GetOpenForHouseholdAsync, so the sequence
    // walk never disagrees with regression detection's own ordering.
    Task<IReadOnlyList<MeterReading>> GetAllByMainMeterAsync(Guid mainMeterId, CancellationToken cancellationToken);

    // One page of a Main Meter's Meter Readings, most-recent-first (ReadingTimestamp descending,
    // then Id descending as the deterministic tiebreak — mirrors FindImmediatelyPrecedingAsync's
    // tiebreak pattern). Descending is this story's own explicit choice for a browsable history
    // list (Story 2.8) — nothing in FR-31 mandates a direction, only that the sort key is
    // timestamp, not entry order.
    Task<(IReadOnlyList<MeterReading> Items, int TotalCount)> GetPageForMainMeterAsync(Guid mainMeterId, int page, int pageSize, CancellationToken cancellationToken);

    // Optimistic-concurrency-guarded value edit (AD-4, Story 2.8). Throws
    // MeterReadingConcurrencyConflictException on a Version mismatch — mirrors
    // HouseholdRepository.UpdateYearlyBaselineAsync's exact mechanics.
    Task<MeterReading> UpdateKwhValueAsync(Guid readingId, decimal kwhValue, int expectedVersion, CancellationToken cancellationToken);
}
