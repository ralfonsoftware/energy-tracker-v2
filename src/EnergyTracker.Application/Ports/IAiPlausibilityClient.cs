using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

// AD-8: one port, one real adapter (OpenAiCompatibleClient) plus a no-op — config-selected once at
// the composition root, exactly like the DB provider/job queue. Classification only, never prose
// generation (UX-DR17) — the caller supplies which direction(s) were actually observed, and the
// client answers which one (if any) the event description plausibly explains.
public interface IAiPlausibilityClient
{
    /// <summary>
    /// Never throws (NFR14) — any timeout, malformed response, or non-2xx status resolves to
    /// <c>null</c> ("no plausible match"), the same result an unconfigured/no-op client always
    /// returns. A flaky AI backend must degrade to "no correlation", never a job failure.
    /// </summary>
    Task<AiPlausibilityDirection?> ClassifyAsync(
        string eventDescription,
        IReadOnlyCollection<AiPlausibilityDirection> observedDirections,
        CancellationToken cancellationToken);
}
