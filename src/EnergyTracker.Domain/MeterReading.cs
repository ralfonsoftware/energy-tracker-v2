namespace EnergyTracker.Domain;

public class MeterReading
{
    public required Guid Id { get; init; }

    // Denormalized, matching the Room/PowerPoint/Device AD-3 pattern — not a join through MainMeter.
    public required Guid HouseholdId { get; init; }

    public required Guid MainMeterId { get; init; }

    public required decimal KwhValue { get; set; }

    // The meter's own read time — user-editable/backfillable, distinct from CreatedAtUtc below.
    public required DateTimeOffset ReadingTimestamp { get; init; }

    // AD-16: client-generated before any network attempt; unique-indexed so a retried request
    // upserts as a no-op instead of inserting a duplicate row.
    public required Guid IdempotencyKey { get; init; }

    // Server insert time — doubles as the "entry order" signal Story 2.3's regression detection
    // needs to distinguish from ReadingTimestamp order.
    public required DateTimeOffset CreatedAtUtc { get; init; }

    // Portable EF Core concurrency token (AD-4) — guards a Meter Reading edit (Story 2.8) against
    // a second concurrent edit of the same reading. Mirrors Household.Version's exact shape.
    public int Version { get; set; }
}
