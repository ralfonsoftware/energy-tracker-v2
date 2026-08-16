namespace EnergyTracker.Application;

/// <summary>Thrown when a MeterRegressionPrompt id does not match any existing prompt for the caller's Household.</summary>
public class MeterRegressionPromptNotFoundException(Guid promptId) : Exception($"No MeterRegressionPrompt found for id '{promptId}'.");
