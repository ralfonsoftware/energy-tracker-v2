namespace EnergyTracker.Domain;

public class Device
{
    public required Guid Id { get; init; }

    public required Guid HouseholdId { get; init; }

    // Immutable — this story does not support re-parenting a Device to a different Power Point.
    public required Guid PowerPointId { get; init; }

    public required string Name { get; set; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? ArchivedAt { get; set; }
}
