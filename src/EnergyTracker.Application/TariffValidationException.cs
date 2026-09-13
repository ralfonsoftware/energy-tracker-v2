namespace EnergyTracker.Application;

/// <summary>Thrown when Tariff input fails validation, or when a locked-field edit is submitted without the required override confirmation.</summary>
public class TariffValidationException(string message) : Exception(message);
