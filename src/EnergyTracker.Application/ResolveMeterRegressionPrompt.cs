using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Resolves an open MeterRegressionPrompt as reset or rollover for the caller's own Household, enforcing AD-12's one-open-at-a-time ordering (AC #2, #3, #5, #6).</summary>
public class ResolveMeterRegressionPrompt(IMeterRegressionPromptRepository repository)
{
    private const decimal MaxDigitCapacityKwh = 1_000_000_000_000_000m; // 10^15, one order below 10^16 overflow — same bound as CreateMeterReading.MaxKwhValue.

    public async Task<MeterRegressionPrompt> ExecuteAsync(
        Guid householdId,
        Guid promptId,
        MeterRegressionClassification classification,
        decimal? digitCapacityKwh,
        CancellationToken cancellationToken)
    {
        var prompt = await repository.FindByIdAsync(householdId, promptId, cancellationToken)
            ?? throw new MeterRegressionPromptNotFoundException(promptId);

        if (prompt.ResolvedAtUtc is not null)
        {
            throw new MeterRegressionPromptNotOpenException($"MeterRegressionPrompt '{promptId}' is already resolved.");
        }

        var openPrompt = await repository.GetOpenForHouseholdAsync(householdId, cancellationToken);
        if (openPrompt?.Id != prompt.Id)
        {
            throw new MeterRegressionPromptNotOpenException(
                $"MeterRegressionPrompt '{promptId}' is not the current open prompt for this Household — resolve the earlier one first.");
        }

        if (classification == MeterRegressionClassification.Rollover)
        {
            var effectiveCapacity = digitCapacityKwh ?? await repository.GetMainMeterDigitCapacityAsync(prompt.MainMeterId, cancellationToken);
            if (effectiveCapacity is null || effectiveCapacity <= 0)
            {
                throw new MeterRegressionValidationException(
                    "A positive digit capacity (kWh) is required to classify a reading as a rollover.");
            }

            if (effectiveCapacity >= MaxDigitCapacityKwh)
            {
                throw new MeterRegressionValidationException(
                    $"Digit capacity (kWh) must be less than {MaxDigitCapacityKwh}, got '{effectiveCapacity}'.");
            }

            prompt.DigitCapacityKwh = effectiveCapacity;
            await repository.SetMainMeterDigitCapacityIfUnsetAsync(prompt.MainMeterId, effectiveCapacity.Value, cancellationToken);
        }

        prompt.Classification = classification;
        prompt.ResolvedAtUtc = DateTimeOffset.UtcNow;

        var resolved = await repository.ResolveAsync(prompt, cancellationToken);
        if (!resolved)
        {
            // Lost the race: a concurrent request resolved this same prompt between our read above
            // and this write. Surface it the same way as "not the open prompt" — the caller should
            // re-fetch rather than assume its classification was the one that stuck.
            throw new MeterRegressionPromptNotOpenException($"MeterRegressionPrompt '{promptId}' was already resolved by a concurrent request.");
        }

        return prompt;
    }
}
