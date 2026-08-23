namespace EnergyTracker.Application;

/// <summary>Thrown when a MeterReading id does not match any existing reading for the caller's Household.</summary>
public class MeterReadingNotFoundException(Guid readingId) : Exception($"No MeterReading found for id '{readingId}'.");
