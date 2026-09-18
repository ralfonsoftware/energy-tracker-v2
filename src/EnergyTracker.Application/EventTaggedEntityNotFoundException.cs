namespace EnergyTracker.Application;

/// <summary>
/// Thrown when an Event's tag target does not exist in the caller's own Household — a bogus id, or
/// one belonging to another Household (AD-3's query filter makes a foreign row read as missing, so
/// the two are indistinguishable here by design and neither leaks the other Household's data).
/// A 404, matching the codebase's *NotFoundException convention, rather than the 409 reserved for a
/// target that genuinely exists but is archived.
/// </summary>
public class EventTaggedEntityNotFoundException(string taggedEntityType, Guid taggedEntityId)
    : Exception($"{taggedEntityType} '{taggedEntityId}' was not found.")
{
    /// <summary>Stable machine-readable code for the client's localized message (AD-18).</summary>
    public string ErrorCode => "event.tag_not_found";
}
