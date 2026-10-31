namespace EnergyTracker.TestSupport;

/// <summary>A controllable clock for tests: time moves only when the test says so.</summary>
public sealed class ManualTimeProvider(DateTimeOffset? start = null) : TimeProvider
{
    private DateTimeOffset _utcNow = start ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan by) => _utcNow += by;

    public void Set(DateTimeOffset value) => _utcNow = value;
}
