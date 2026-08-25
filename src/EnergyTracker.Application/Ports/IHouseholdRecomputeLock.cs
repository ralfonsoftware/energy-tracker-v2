namespace EnergyTracker.Application.Ports;

/// <summary>Serializes StatusRecomputeService.RecomputeAsync per Household so its concurrent trigger call sites never race and leave the latest StatusSnapshot stale (AD-7).</summary>
public interface IHouseholdRecomputeLock
{
    // Throws TimeoutException if the wait exceeds the adapter's configured acquisition timeout —
    // never blocks indefinitely, since this holds a pooled DB connection open while waiting.
    Task<IAsyncDisposable> AcquireAsync(Guid householdId, CancellationToken cancellationToken);
}
