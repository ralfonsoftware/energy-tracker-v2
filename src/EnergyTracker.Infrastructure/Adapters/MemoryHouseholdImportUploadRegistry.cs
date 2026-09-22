using System.Collections.Concurrent;
using EnergyTracker.Application.Ports;

namespace EnergyTracker.Infrastructure.Adapters;

// Singleton in-memory registry backing Task 5's opaque-token requirement. Deliberately a plain
// ConcurrentDictionary, not a new IMemoryCache package dependency — a per-entry TTL checked lazily
// on Consume is enough for this narrow validate-then-confirm window, and this app runs API+worker
// as one process/one container (AD-6) with no evidence of horizontal scale-out elsewhere in this
// codebase. A container restart between validate and confirm loses the token, requiring a fresh
// upload — a disclosed trade-off (docs/data-import-restore.md), not a silent correctness gap: see
// IHouseholdImportUploadRegistry's own IDOR-guard contract.
public class MemoryHouseholdImportUploadRegistry : IHouseholdImportUploadRegistry
{
    // Generous relative to how long a member actually takes to read the validation summary and
    // click "replace all data" — long enough that a slow reader never has to re-upload a large
    // file, short enough that an abandoned upload's orphaned temp file doesn't linger indefinitely.
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    private readonly record struct Entry(HouseholdImportUploadReference Reference, DateTimeOffset ExpiresAtUtc);

    public void Register(Guid token, HouseholdImportUploadReference reference)
    {
        // An abandoned upload (validated, never confirmed) previously sat in _entries forever —
        // Consume was the only removal path, and it only ever runs on confirm. Sweeping here
        // piggybacks on the one natural trigger this registry already sees regularly, deleting
        // each expired entry's orphaned temp file too, not just dropping the dictionary entry (Code
        // Review, Story 7.2 Pass 1). No IHostedService/Timer: this app's scale-to-zero Container
        // Apps hosting doesn't reliably run one (same reasoning as AD-7's compute-at-read-time
        // convention).
        SweepExpiredEntries();
        _entries[token] = new Entry(reference, DateTimeOffset.UtcNow.Add(Ttl));
    }

    private void SweepExpiredEntries()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (token, entry) in _entries)
        {
            if (entry.ExpiresAtUtc < now && _entries.TryRemove(token, out var removed))
            {
                TryDeleteTempFile(removed.Reference.TempFilePath);
            }
        }
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only — a locked/already-gone file here must never block a new
            // upload; the next sweep (or the OS temp-dir's own eventual cleanup) gets another try.
        }
    }

    // Atomic lookup+remove (TryRemove) — a token can only ever be consumed once, closing a
    // double-confirm race. Expired-or-wrong-Household both return null so the caller (the confirm
    // endpoint) can't distinguish "not found" from "not yours" (AD-3 IDOR-guard convention).
    public HouseholdImportUploadReference? Consume(Guid token, Guid householdId)
    {
        if (!_entries.TryRemove(token, out var entry))
        {
            return null;
        }

        return entry.ExpiresAtUtc >= DateTimeOffset.UtcNow && entry.Reference.HouseholdId == householdId ? entry.Reference : null;
    }
}
