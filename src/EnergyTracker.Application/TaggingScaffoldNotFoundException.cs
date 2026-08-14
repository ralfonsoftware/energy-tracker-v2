namespace EnergyTracker.Application;

/// <summary>Thrown when a Room/PowerPoint/Device id does not match any existing row (shared across all three entity types).</summary>
public class TaggingScaffoldNotFoundException(string entityType, Guid id) : Exception($"{entityType} '{id}' not found.");
