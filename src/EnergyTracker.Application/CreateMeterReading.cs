using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Creates a Meter Reading for the caller's own Household under AD-16 idempotency-key upsert, raises a regression-classification prompt when it's lower than the immediately preceding reading, and recomputes Status (AC #1, #4, #6, #7).</summary>
public class CreateMeterReading(
    IMeterReadingRepository repository,
    IMeterRegressionPromptRepository regressionPromptRepository,
    IStatusRecomputeService statusRecomputeService)
{
    // Small clock-skew allowance, not a real "reading from the future" — a client's local clock
    // can legitimately be a few minutes off from the server's.
    private static readonly TimeSpan MaxFutureClockSkew = TimeSpan.FromMinutes(5);

    // Deliberately not "no smart meters existed before X" — a generous floor that only exists to
    // catch obviously-wrong input (e.g. a client-side date-parsing bug landing on year 1) before
    // it corrupts Story 2.4's gap/pace/elapsed-time math, which relies on timestamp ordering
    // (Task 8, `deferred-work.md` from Story 2.2's review).
    private static readonly DateTimeOffset MinReadingTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task<MeterReading> ExecuteAsync(
        Guid householdId,
        decimal kwhValue,
        DateTimeOffset readingTimestamp,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Fast-path no-op per AD-16 — checked before validation so a retry of an
        // already-persisted reading always returns the existing record via the idempotency-key
        // match, even if a validation rule added after that reading was created would otherwise
        // reject it. The actual uniqueness guarantee is the IdempotencyKey unique index enforced
        // in IMeterReadingRepository.AddAsync; this check just avoids a redundant MainMeter
        // lookup/insert attempt on the common case (a retried request landing after its original
        // attempt already committed).
        var existing = await repository.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        MeterReadingValidation.ValidateKwhValue(kwhValue);

        var latestAllowedTimestamp = DateTimeOffset.UtcNow.Add(MaxFutureClockSkew);
        if (readingTimestamp > latestAllowedTimestamp)
        {
            throw new MeterReadingValidationException(
                $"Reading timestamp '{readingTimestamp}' is too far in the future.");
        }

        if (readingTimestamp < MinReadingTimestamp)
        {
            throw new MeterReadingValidationException(
                $"Reading timestamp '{readingTimestamp}' is unreasonably far in the past.");
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

        // AC #7: every save recomputes Status immediately, unconditionally — regardless of
        // whether a regression prompt was also opened above (AD-7 names this handler as one of
        // exactly two call sites; the other, Smart-Plug-import-completion, is Epic 3's job).
        await statusRecomputeService.RecomputeAsync(householdId, cancellationToken);

        return persistedReading;
    }
}
