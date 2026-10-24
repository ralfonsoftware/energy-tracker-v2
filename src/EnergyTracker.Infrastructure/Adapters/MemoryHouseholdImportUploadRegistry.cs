using EnergyTracker.Application.Ports;

namespace EnergyTracker.Infrastructure.Adapters;

// Singleton in-memory registry backing Task 5's opaque-token requirement. Deliberately plain
// dictionaries behind one lock, not a new IMemoryCache package dependency — a per-entry TTL checked
// lazily on Consume is enough for this narrow validate-then-confirm window. A container restart
// between validate and confirm loses the token, requiring a fresh upload — a disclosed trade-off
// (docs/data-import-restore.md), not a silent correctness gap: see IHouseholdImportUploadRegistry's
// own IDOR-guard contract. Process-local on purpose: AD-24 (single instance).
public class MemoryHouseholdImportUploadRegistry : IHouseholdImportUploadRegistry
{
    // Generous relative to how long a member actually takes to read the validation summary and
    // click "replace all data" — long enough that a slow reader never has to re-upload a large
    // file, short enough that an abandoned upload's orphaned temp file doesn't linger indefinitely.
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    private readonly TimeSpan _ttl;

    // Story 11.10 (audit S4): "at most one pending upload per Household" is a rule over two
    // indexes, so a plain ConcurrentDictionary cannot hold it atomically. One lock guards both;
    // files are always deleted after it is released.
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Dictionary<Guid, Guid> _tokenByHousehold = [];

    public MemoryHouseholdImportUploadRegistry() : this(DefaultTtl)
    {
    }

    internal MemoryHouseholdImportUploadRegistry(TimeSpan ttl) => _ttl = ttl;

    private readonly record struct Entry(HouseholdImportUploadReference Reference, DateTimeOffset ExpiresAtUtc);

    public void Register(Guid token, HouseholdImportUploadReference reference)
    {
        var droppedFiles = new List<string>();
        lock (_gate)
        {
            // An abandoned upload (validated, never confirmed) previously sat in _entries forever —
            // Consume was the only removal path, and it only ever runs on confirm. Sweeping here
            // piggybacks on the one natural trigger this registry already sees regularly, deleting
            // each expired entry's orphaned temp file too, not just dropping the dictionary entry (Code
            // Review, Story 7.2 Pass 1). No IHostedService/Timer: this app's scale-to-zero Container
            // Apps hosting doesn't reliably run one (same reasoning as AD-7's compute-at-read-time
            // convention).
            var now = DateTimeOffset.UtcNow;
            foreach (var (expiredToken, expired) in _entries.Where(e => e.Value.ExpiresAtUtc < now).ToList())
            {
                Remove(expiredToken, expired, droppedFiles);
            }

            // Only an entry the registry still holds is replaced: a token that was already consumed
            // belongs to a queued or running restore job and is no longer in either index.
            if (_tokenByHousehold.TryGetValue(reference.HouseholdId, out var previousToken)
                && _entries.TryGetValue(previousToken, out var previous))
            {
                Remove(previousToken, previous, droppedFiles);
            }

            if (_entries.TryGetValue(token, out var sameToken))
            {
                Remove(token, sameToken, droppedFiles);
            }

            _entries[token] = new Entry(reference, now.Add(_ttl));
            _tokenByHousehold[reference.HouseholdId] = token;
        }

        foreach (var path in droppedFiles.Where(p => p != reference.TempFilePath))
        {
            TryDeleteTempFile(path);
        }
    }

    private void Remove(Guid token, Entry entry, List<string> droppedFiles)
    {
        _entries.Remove(token);
        if (_tokenByHousehold.TryGetValue(entry.Reference.HouseholdId, out var indexed) && indexed == token)
        {
            _tokenByHousehold.Remove(entry.Reference.HouseholdId);
        }

        droppedFiles.Add(entry.Reference.TempFilePath);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup only — a locked/already-gone file here must never block a new
            // upload; the next sweep (or the OS temp-dir's own eventual cleanup) gets another try.
        }
    }

    // Atomic lookup+remove — a token can only ever be consumed once, closing a double-confirm
    // race. Expired-or-wrong-Household both return null so the caller (the confirm endpoint) can't
    // distinguish "not found" from "not yours" (AD-3 IDOR-guard convention). A refused entry is
    // already out of the registry, so its file would be orphaned: it is deleted here (Story 11.10).
    public HouseholdImportUploadReference? Consume(Guid token, Guid householdId)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(token, out entry))
            {
                return null;
            }

            Remove(token, entry, []);
        }

        if (entry.ExpiresAtUtc >= DateTimeOffset.UtcNow && entry.Reference.HouseholdId == householdId)
        {
            return entry.Reference;
        }

        TryDeleteTempFile(entry.Reference.TempFilePath);
        return null;
    }
}
