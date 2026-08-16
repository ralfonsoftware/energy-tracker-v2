namespace EnergyTracker.Application;

/// <summary>Thrown when resolving a MeterRegressionPrompt as rollover with no usable digit capacity (neither passed in nor already stored on MainMeter), or a non-positive one.</summary>
public class MeterRegressionValidationException(string message) : Exception(message);
