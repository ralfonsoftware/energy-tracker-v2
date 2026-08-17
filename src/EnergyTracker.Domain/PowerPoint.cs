namespace EnergyTracker.Domain;

public class PowerPoint
{
    public required Guid Id { get; init; }

    public required Guid HouseholdId { get; init; }

    // Mutable via MovePowerPoint only — not a general-purpose setter.
    public required Guid RoomId { get; set; }

    public required string Name { get; set; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? ArchivedAt { get; set; }
}
