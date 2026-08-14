namespace EnergyTracker.Domain;

public class Room
{
    public required Guid Id { get; init; }

    public required Guid HouseholdId { get; init; }

    public required string Name { get; set; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? ArchivedAt { get; set; }
}
