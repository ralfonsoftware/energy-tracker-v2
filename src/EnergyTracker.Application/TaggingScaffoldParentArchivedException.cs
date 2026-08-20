namespace EnergyTracker.Application;

/// <summary>
/// Thrown when creating a Power Point under an archived Room, or a Device under an archived Power
/// Point. Also reused by MapSmartPlugImportToPowerPoint for the narrower case of the referenced
/// entity itself being archived (not a parent-of-a-created-child) — the generic
/// "{parentType} '{parentId}' is archived" message and 409 mapping both still fit that case.
/// </summary>
public class TaggingScaffoldParentArchivedException(string parentType, Guid parentId) : Exception($"{parentType} '{parentId}' is archived.");
