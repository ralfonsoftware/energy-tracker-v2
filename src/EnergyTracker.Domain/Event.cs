namespace EnergyTracker.Domain;

public class Event
{
    public required Guid Id { get; init; }

    public required Guid HouseholdId { get; init; }

    // init, not set — an Event is an append-only dated occurrence. A settable Description would
    // invite an edit path that bypasses AD-11's AuditCorrection + IAuditCorrectionRecorder.
    public required string Description { get; init; }

    // User-entered/backfillable — mirrors MeterReading.ReadingTimestamp, not "Utc"-suffixed.
    public required DateTimeOffset OccurredAt { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    // Null when untagged. "Room" | "PowerPoint" | "Device" — plain discriminator, not an enum,
    // matching AuditCorrection.EntityType's precedent (a 4th taggable type is a data addition,
    // not a schema change). Structurally guarantees "at most one tag type" without an app-level
    // invariant three separate nullable columns would need.
    public string? TaggedEntityType { get; init; }

    public Guid? TaggedEntityId { get; init; }

    // AD-10 by-value snapshot, captured once at write time — never re-derived or overwritten,
    // even after the tagged item is renamed, archived, or re-parented (Story 2.6).
    public string? TaggedEntityName { get; init; }
}
