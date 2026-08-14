namespace EnergyTracker.Application;

/// <summary>Thrown when a Room/PowerPoint/Device Name fails validation (blank or over-length).</summary>
public class TaggingScaffoldValidationException(string message) : Exception(message);
