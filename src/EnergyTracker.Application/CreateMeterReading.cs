using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Creates a Meter Reading for the caller's own Household under AD-16 idempotency-key upsert (AC #1, #2, #3, #4, #6, #7).</summary>
public class CreateMeterReading(IMeterReadingRepository repository)
{
    // A meter reading is a cumulative lifetime total, not a small human-entered figure like
    // Yearly Baseline — no low arbitrary business cap. The bound here exists only to keep values
    // inside the decimal(18,2) column's range so an out-of-range submission fails validation
    // (400) instead of a provider-level overflow (500).
    private const decimal MaxKwhValue = 1_000_000_000_000_000m; // 10^15, one order below 10^16 overflow.

    public async Task<MeterReading> ExecuteAsync(
        Guid householdId,
        decimal kwhValue,
        DateTimeOffset readingTimestamp,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (kwhValue <= 0 || kwhValue >= MaxKwhValue)
        {
            throw new MeterReadingValidationException(
                $"kWh value must be a positive number less than {MaxKwhValue}, got '{kwhValue}'.");
        }

        // Fast-path no-op per AD-16 — the actual guarantee is the IdempotencyKey unique index
        // enforced in IMeterReadingRepository.AddAsync; this check just avoids a redundant
        // MainMeter lookup/insert attempt on the common case (a retried request landing after its
        // original attempt already committed).
        var existing = await repository.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var mainMeter = await repository.GetOrCreateMainMeterAsync(householdId, cancellationToken);

        var reading = new MeterReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = mainMeter.Id,
            KwhValue = kwhValue,
            ReadingTimestamp = readingTimestamp,
            IdempotencyKey = idempotencyKey,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        return await repository.AddAsync(reading, cancellationToken);
    }
}
