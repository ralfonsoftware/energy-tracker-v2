namespace EnergyTracker.Domain;

public class PowerPoint
{
    public required Guid Id { get; init; }

    public required Guid HouseholdId { get; init; }

    // Immutable — this story does not support re-parenting a Power Point to a different Room.
    public required Guid RoomId { get; init; }

    public required string Name { get; set; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? ArchivedAt { get; set; }
}
