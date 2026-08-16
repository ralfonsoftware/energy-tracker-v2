using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Creates a Meter Reading for the caller's own Household under AD-16 idempotency-key upsert, and raises a regression-classification prompt when it's lower than the immediately preceding reading (AC #1, #4, #6).</summary>
public class CreateMeterReading(IMeterReadingRepository repository, IMeterRegressionPromptRepository regressionPromptRepository)
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

        // Capture the persisted instance, not the local `reading` — a concurrent idempotency-key
        // race in repository.AddAsync can return a *different*, already-persisted reading than
        // this request constructed (see AddAsync's own catch-detach-requery comment). Regression
        // detection must run against whichever reading actually won, since two concurrent
        // requests can both reach this point for the same winning reading.
        var persistedReading = await repository.AddAsync(reading, cancellationToken);

        // Only the immediately-preceding comparison is in scope — a backfill is never
        // retroactively re-checked against its chronological successor.
        var preceding = await repository.FindImmediatelyPrecedingAsync(mainMeter.Id, persistedReading.ReadingTimestamp, cancellationToken);
        if (preceding is not null && persistedReading.KwhValue < preceding.KwhValue)
        {
            await regressionPromptRepository.AddAsync(
                new MeterRegressionPrompt
                {
                    Id = Guid.NewGuid(),
                    HouseholdId = householdId,
                    MainMeterId = mainMeter.Id,
                    MeterReadingId = persistedReading.Id,
                    PreviousMeterReadingId = preceding.Id,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    Classification = null,
                    ResolvedAtUtc = null,
                },
                cancellationToken);
        }

        return persistedReading;
    }
}
