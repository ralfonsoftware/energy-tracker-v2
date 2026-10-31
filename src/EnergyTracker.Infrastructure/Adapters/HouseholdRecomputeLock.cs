using EnergyTracker.Application.Ports;

namespace EnergyTracker.Infrastructure.Adapters;

// Singleton, in-process per-Household async lock backing StatusRecomputeService's serialization
// (AD-7). Process-local on purpose: AD-24 (single instance). A second instance would not see
// these locks and would need a Postgres/SqlServer-portable distributed lock instead.
//
// Entries are reference-counted (holder plus waiters, changed only under _gate) and removed when
// the count reaches 0, so the dictionary is bounded by Households in use right now, not by
// Households ever seen (AD-24).
public class HouseholdRecomputeLock(TimeSpan? acquisitionTimeout = null) : IHouseholdRecomputeLock
{
    // ~10s, not 30s — this is a worst-case ceiling that holds a pooled DB connection open while a
    // caller waits, so it's kept tight (review-round-2 finding). Overridable only for tests that
    // need a short, deterministic timeout.
    private static readonly TimeSpan DefaultAcquisitionTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _acquisitionTimeout = acquisitionTimeout ?? DefaultAcquisitionTimeout;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    internal int TrackedHouseholdCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public async Task<IAsyncDisposable> AcquireAsync(Guid householdId, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(householdId, out entry!))
            {
                entry = new Entry();
                _entries[householdId] = entry;
            }

            entry.References++;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_acquisitionTimeout);

        try
        {
            await entry.Semaphore.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Return(householdId, entry);

            // The linked token fired but the caller's own token didn't — the acquisition timeout,
            // not caller cancellation, is what tripped this.
            throw new TimeoutException(
                $"Timed out after {_acquisitionTimeout.TotalSeconds:0}s waiting to acquire the recompute lock for Household {householdId}.");
        }
        catch
        {
            Return(householdId, entry);
            throw;
        }

        return new Releaser(this, householdId, entry);
    }

    private void Return(Guid householdId, Entry entry)
    {
        lock (_gate)
        {
            if (--entry.References == 0)
            {
                _entries.Remove(householdId);
            }
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int References;
    }

    private sealed class Releaser(HouseholdRecomputeLock owner, Guid householdId, Entry entry) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            // Defensive against a double-dispose releasing the semaphore twice (would incorrectly
            // let a third waiter in early) — IAsyncDisposable callers are expected to dispose
            // exactly once (`await using`), but this costs nothing to guard.
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                entry.Semaphore.Release();
                owner.Return(householdId, entry);
            }

            return ValueTask.CompletedTask;
        }
    }
}
