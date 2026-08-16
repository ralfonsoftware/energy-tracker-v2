namespace EnergyTracker.Application;

/// <summary>Thrown when resolving a MeterRegressionPrompt that is already resolved, or isn't currently the earliest-unresolved (open) one for its Main Meter (AD-12).</summary>
public class MeterRegressionPromptNotOpenException(string message) : Exception(message);
