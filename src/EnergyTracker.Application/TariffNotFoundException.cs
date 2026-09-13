namespace EnergyTracker.Application;

/// <summary>Thrown when a Tariff id does not match any existing entry for the caller's Household.</summary>
public class TariffNotFoundException(Guid tariffId) : Exception($"No Tariff found for id '{tariffId}'.");
