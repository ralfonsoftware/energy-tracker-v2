namespace EnergyTracker.Application;

/// <summary>
/// Thrown when creating an Event tagged to a Room/PowerPoint/Device that is archived, either
/// directly or through an archived ancestor. A 409, not a 400 (TaggingScaffoldParentArchivedException's
/// established convention for this class of error — a race where the client's view is stale — not a
/// 400-mapped EventValidationException). A tag target that does not exist at all is a distinct
/// EventTaggedEntityNotFoundException (404), not this.
/// </summary>
public class EventTaggedEntityArchivedException : Exception
{
    public EventTaggedEntityArchivedException(string taggedEntityType, Guid taggedEntityId, bool viaAncestor = false)
        : base(viaAncestor
            ? $"{taggedEntityType} '{taggedEntityId}' belongs to an archived parent and is no longer available for tagging."
            : $"{taggedEntityType} '{taggedEntityId}' is archived.")
        => ErrorCode = viaAncestor ? "event.tag_parent_archived" : "event.tag_archived";

    /// <summary>Stable machine-readable code for the client's localized message (AD-18).</summary>
    public string ErrorCode { get; }
}
