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
}
