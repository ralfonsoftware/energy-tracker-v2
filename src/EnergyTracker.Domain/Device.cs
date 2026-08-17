namespace EnergyTracker.Domain;

public class Device
{
    public required Guid Id { get; init; }

    public required Guid HouseholdId { get; init; }

    // Mutable via MoveDevice only — not a general-purpose setter.
    public required Guid PowerPointId { get; set; }

    public required string Name { get; set; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? ArchivedAt { get; set; }
}
