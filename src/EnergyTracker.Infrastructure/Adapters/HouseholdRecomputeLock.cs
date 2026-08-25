using System.Collections.Concurrent;
using EnergyTracker.Application.Ports;

namespace EnergyTracker.Infrastructure.Adapters;

// Singleton, in-process per-Household async lock backing StatusRecomputeService's serialization
// (AD-7). Safe ONLY because infra/modules/container-app.bicep hardcodes maxReplicas = 1 — this
// app never runs more than one instance. If that coupling ever changes, this lock stops being
// sufficient and needs a Postgres/SqlServer-portable distributed lock instead.
//
// No eviction/cleanup for the dictionary below — Household count is small and bounded, so an
// entry per Household living for the app's lifetime is an accepted tradeoff (spec's own
// constraint), not an oversight.
public class HouseholdRecomputeLock(TimeSpan? acquisitionTimeout = null) : IHouseholdRecomputeLock
{
    // ~10s, not 30s — this is a worst-case ceiling that holds a pooled DB connection open while a
    // caller waits, so it's kept tight (review-round-2 finding). Overridable only for tests that
    // need a short, deterministic timeout.
    private static readonly TimeSpan DefaultAcquisitionTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _acquisitionTimeout = acquisitionTimeout ?? DefaultAcquisitionTimeout;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _semaphoresByHouseholdId = new();

    public async Task<IAsyncDisposable> AcquireAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var semaphore = _semaphoresByHouseholdId.GetOrAdd(householdId, static _ => new SemaphoreSlim(1, 1));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_acquisitionTimeout);

        try
        {
            await semaphore.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The linked token fired but the caller's own token didn't — the acquisition timeout,
            // not caller cancellation, is what tripped this.
            throw new TimeoutException(
                $"Timed out after {_acquisitionTimeout.TotalSeconds:0}s waiting to acquire the recompute lock for Household {householdId}.");
        }

        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            // Defensive against a double-dispose releasing the semaphore twice (would incorrectly
            // let a third waiter in early) — IAsyncDisposable callers are expected to dispose
            // exactly once (`await using`), but this costs nothing to guard.
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
