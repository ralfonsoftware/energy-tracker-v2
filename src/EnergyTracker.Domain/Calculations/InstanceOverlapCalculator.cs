namespace EnergyTracker.Domain.Calculations;

// Pure, static, no clock and no I/O. AD-24 (observe, never block): how long two instances were both
// running, from their heartbeat rows alone. It trusts both instances' clocks; replicas of one image
// in one environment agree within seconds and the warn threshold is 15 minutes, so skew is irrelevant.
public static class InstanceOverlapCalculator
{
    // min(last seen) - max(started); zero when they never overlapped, never negative (a last-seen
    // before its own start is clock skew, not a negative duration).
    public static TimeSpan Overlap(
        DateTimeOffset selfStartedAtUtc, DateTimeOffset selfLastSeenAtUtc,
        DateTimeOffset otherStartedAtUtc, DateTimeOffset otherLastSeenAtUtc)
    {
        var from = selfStartedAtUtc > otherStartedAtUtc ? selfStartedAtUtc : otherStartedAtUtc;
        var to = selfLastSeenAtUtc < otherLastSeenAtUtc ? selfLastSeenAtUtc : otherLastSeenAtUtc;
        var overlap = to - from;
        return overlap > TimeSpan.Zero ? overlap : TimeSpan.Zero;
    }
}
