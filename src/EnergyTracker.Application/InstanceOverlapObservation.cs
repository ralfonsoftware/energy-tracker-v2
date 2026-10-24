namespace EnergyTracker.Application;

// One other instance that has been running alongside this one for longer than the warn threshold.
public sealed record InstanceOverlapObservation(Guid OtherInstanceId, DateTimeOffset OtherStartedAtUtc, TimeSpan Overlap);
