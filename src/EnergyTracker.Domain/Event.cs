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

    // Story 6.3 (AC #1, #3, #4) — both null means "no correlation" (AC #3), the simplest
    // representation for data that's 1:1-owned by this Event with no independent lifecycle. Set
    // once, together, by CorrelateEvent's background job and never recomputed at render time
    // (AD-10's "derive once, read back forever" discipline extends here too). Plain "Bump"|"Dip"
    // string column, not an enum, matching TaggedEntityType's own discriminator-column precedent —
    // mutable (not init), since it's written well after the Event row itself is created.
    public string? CorrelationDirection { get; set; }

    public DateTimeOffset? CorrelationComputedAtUtc { get; set; }
}
