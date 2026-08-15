namespace EnergyTracker.Application;

/// <summary>Thrown when Meter Reading creation input (kWh value) fails validation.</summary>
public class MeterReadingValidationException(string message) : Exception(message);
