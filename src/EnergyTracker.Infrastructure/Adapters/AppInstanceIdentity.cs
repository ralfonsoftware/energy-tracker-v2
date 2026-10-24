namespace EnergyTracker.Infrastructure.Adapters;

// "This process" for the AD-24 instance heartbeat: one id and one start time per process start,
// created once at the composition root. A new id on every start is what lets two overlapping
// revisions tell their rows apart.
public sealed record AppInstanceIdentity(Guid Id, DateTimeOffset StartedAtUtc);
