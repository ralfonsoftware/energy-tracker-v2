namespace EnergyTracker.Domain;

// Deliberately no Version/concurrency-token column here (AD-4) — that binds Meter Reading for
// future *edit* conflicts (Story 4.3's correction flow), not creation. Add it only when a story
// actually implements edits, mirroring how Household.Version was added by Story 2.1.
public class MeterReading
{
    public required Guid Id { get; init; }

    // Denormalized, matching the Room/PowerPoint/Device AD-3 pattern — not a join through MainMeter.
    public required Guid HouseholdId { get; init; }

    public required Guid MainMeterId { get; init; }

    public required decimal KwhValue { get; init; }

    // The meter's own read time — user-editable/backfillable, distinct from CreatedAtUtc below.
    public required DateTimeOffset ReadingTimestamp { get; init; }

    // AD-16: client-generated before any network attempt; unique-indexed so a retried request
    // upserts as a no-op instead of inserting a duplicate row.
    public required Guid IdempotencyKey { get; init; }

    // Server insert time — doubles as the "entry order" signal Story 2.3's regression detection
    // needs to distinguish from ReadingTimestamp order.
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
