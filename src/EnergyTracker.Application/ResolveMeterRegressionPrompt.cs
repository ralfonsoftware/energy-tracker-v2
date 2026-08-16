using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Resolves an open MeterRegressionPrompt as reset or rollover for the caller's own Household, enforcing AD-12's one-open-at-a-time ordering (AC #2, #3, #5, #6).</summary>
public class ResolveMeterRegressionPrompt(IMeterRegressionPromptRepository repository)
{
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

            prompt.DigitCapacityKwh = effectiveCapacity;
            await repository.SetMainMeterDigitCapacityIfUnsetAsync(prompt.MainMeterId, effectiveCapacity.Value, cancellationToken);
        }

        prompt.Classification = classification;
        prompt.ResolvedAtUtc = DateTimeOffset.UtcNow;

        return await repository.ResolveAsync(prompt, cancellationToken);
    }
}
