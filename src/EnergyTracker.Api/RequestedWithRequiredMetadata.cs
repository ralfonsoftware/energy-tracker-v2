namespace EnergyTracker.Api;

/// <summary>Marks an endpoint that sits behind <see cref="RequestedWithFilter"/>, so a guard test can find it (Story 11.8, AC #7).</summary>
internal sealed record RequestedWithRequiredMetadata
{
    public static RequestedWithRequiredMetadata Instance { get; } = new();
}
