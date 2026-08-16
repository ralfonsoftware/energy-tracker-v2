using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

public record OpenMeterRegressionPromptDetails(
    MeterRegressionPrompt Prompt,
    MeterReading Reading,
    MeterReading PreviousReading,
    decimal? MainMeterDigitCapacityKwh);

/// <summary>Reads the caller's own Household's currently open MeterRegressionPrompt, if any, enriched with both readings' data (AC #1, #6, #7).</summary>
public class GetOpenMeterRegressionPrompt(IMeterRegressionPromptRepository promptRepository, IMeterReadingRepository readingRepository)
{
    public async Task<OpenMeterRegressionPromptDetails?> ExecuteAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var prompt = await promptRepository.GetOpenForHouseholdAsync(householdId, cancellationToken);
        if (prompt is null)
        {
            return null;
        }

        var reading = await readingRepository.FindByIdAsync(prompt.MeterReadingId, cancellationToken);
        var previousReading = await readingRepository.FindByIdAsync(prompt.PreviousMeterReadingId, cancellationToken);
        var mainMeterDigitCapacityKwh = await promptRepository.GetMainMeterDigitCapacityAsync(prompt.MainMeterId, cancellationToken);

        return new OpenMeterRegressionPromptDetails(prompt, reading!, previousReading!, mainMeterDigitCapacityKwh);
    }
}
