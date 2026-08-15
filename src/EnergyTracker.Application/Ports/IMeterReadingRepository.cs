using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface IMeterReadingRepository
{
    Task<MeterReading?> FindByIdempotencyKeyAsync(Guid idempotencyKey, CancellationToken cancellationToken);

    Task<MainMeter> GetOrCreateMainMeterAsync(Guid householdId, CancellationToken cancellationToken);

    Task<MeterReading> AddAsync(MeterReading reading, CancellationToken cancellationToken);
}
