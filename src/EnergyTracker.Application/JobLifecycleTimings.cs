namespace EnergyTracker.Application;

// AD-6 (amended 2026-10-10): operational constants, not Household configuration (AD-15 does not
// apply). Held in one place so the processor's heartbeat and the adapter's staleness check can
// never drift apart; a test injects a short HeartbeatInterval instead of sleeping a minute.
public sealed record JobLifecycleTimings(TimeSpan HeartbeatInterval, TimeSpan StaleAfter)
{
    // Base delay between attempts at a lifecycle statement that hit a transient database error
    // (attempt n waits n times this); a test shortens it with `with`.
    public TimeSpan TransitionRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    // A Queued row older than this is failed as "job-interrupted" on read (AD-6, amended 2026-10-11).
    // Longer than a serial batch of imports plus five 60-minute redelivery cycles of the Azure queue,
    // so a message that is still alive is never failed from under its consumer; it exists for rows
    // whose queue message is gone (lost send, purged queue, database rolled back to a restore point).
    public TimeSpan QueuedExpiresAfter { get; init; } = TimeSpan.FromHours(12);

    public static JobLifecycleTimings Default { get; } = new(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
}
