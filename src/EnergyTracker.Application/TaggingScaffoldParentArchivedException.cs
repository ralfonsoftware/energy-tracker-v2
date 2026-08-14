namespace EnergyTracker.Application;

/// <summary>Thrown when creating a Power Point under an archived Room, or a Device under an archived Power Point.</summary>
public class TaggingScaffoldParentArchivedException(string parentType, Guid parentId) : Exception($"{parentType} '{parentId}' is archived.");
