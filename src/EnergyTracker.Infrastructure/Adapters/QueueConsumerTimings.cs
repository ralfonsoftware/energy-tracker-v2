namespace EnergyTracker.Infrastructure.Adapters;

// AD-6: operational constants of the Azure queue consumer, not Household configuration. Held in one
// record so a test injects millisecond values instead of waiting out a real backoff.
public sealed record QueueConsumerTimings(TimeSpan PollInterval, TimeSpan InitialBackoff, TimeSpan MaxBackoff)
{
    public static QueueConsumerTimings Default { get; } = new(
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));
}
