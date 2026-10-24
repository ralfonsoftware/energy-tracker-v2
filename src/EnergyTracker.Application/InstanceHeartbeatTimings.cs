namespace EnergyTracker.Application;

// AD-24: operational constants, not Household configuration (AD-15 does not apply), held in one place
// like JobLifecycleTimings; a test injects milliseconds instead of waiting a minute.
// Interval matches the job heartbeat of AD-6. AliveAfter: a row last seen longer ago than this belongs
// to a stopped instance. OverlapWarnAfter: a deploy swap lasts seconds to a few minutes, so only an
// overlap beyond this is worth a warning. RetainFor: rows older than this are purged.
public sealed record InstanceHeartbeatTimings(TimeSpan Interval, TimeSpan AliveAfter, TimeSpan OverlapWarnAfter, TimeSpan RetainFor)
{
    // Upper bound for removing this process's own row at graceful stop, so a stuck database can never
    // delay shutdown; a test shortens it with `with`.
    public TimeSpan ShutdownDeleteBound { get; init; } = TimeSpan.FromSeconds(5);

    public static InstanceHeartbeatTimings Default { get; } = new(
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(15), TimeSpan.FromHours(24));
}
