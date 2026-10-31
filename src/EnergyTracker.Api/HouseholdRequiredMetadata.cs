namespace EnergyTracker.Api;

/// <summary>Marks an endpoint that sits behind <see cref="HouseholdRequiredFilter"/>, so a guard test can find it.</summary>
internal sealed record HouseholdRequiredMetadata
{
    public static HouseholdRequiredMetadata Instance { get; } = new();
}
