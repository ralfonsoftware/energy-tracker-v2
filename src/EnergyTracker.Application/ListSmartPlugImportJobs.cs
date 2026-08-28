using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

// The six states FR-32/UX-DR21 require, never folded into one another or a generic pending/done.
public enum SmartPlugImportJobState
{
    Waiting,
    Processing,
    Success,
    Error,
    NeedsMapping,
    FlaggedForReview,
}

public record SmartPlugImportJobResult(
    Guid JobId,
    string? FileName,
    SmartPlugImportJobState State,
    string? QueuedByDisplayName,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ErrorMessage,
    Guid? SmartPlugImportId,
    string? DeviceTag,
    IReadOnlyList<SmartPlugImportGap> Gaps);

/// <summary>Lists every Smart Plug import job queued by any member of the caller's Household, deriving each row's six-state value and sweeping expired terminal-state records first (AC #1, #2, #5, #6, #7, #8).</summary>
public class ListSmartPlugImportJobs(IBackgroundJobRepository backgroundJobRepository, ISmartPlugImportRepository smartPlugImportRepository)
{
    // FR-32/AD-6 extension: Success/Error/Flagged for Review records fade out 30 days after
    // completion; Waiting/Processing/Needs Mapping never auto-clear (AC #6, #7).
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(30);

    public async Task<IReadOnlyList<SmartPlugImportJobResult>> ExecuteAsync(Guid householdId, CancellationToken cancellationToken)
    {
        // Lazy, read-triggered sweep (AD-7) — runs first so a swept row never appears in the same
        // response that triggered its own sweep.
        await smartPlugImportRepository.SweepExpiredAsync(householdId, DateTimeOffset.UtcNow - RetentionWindow, cancellationToken);

        var jobs = await backgroundJobRepository.ListByJobTypeAsync(householdId, JobTypes.ProcessSmartPlugImport, cancellationToken);

        var completedJobIds = jobs.Where(j => j.Status == BackgroundJobStatus.Completed).Select(j => j.Id).ToList();
        var imports = completedJobIds.Count > 0
            ? await smartPlugImportRepository.FindAllByBackgroundJobIdsAsync(completedJobIds, cancellationToken)
            : [];
        var importsByJobId = imports.ToDictionary(i => i.BackgroundJobId);

        var memberIds = jobs
            .Where(j => j.QueuedByHouseholdMemberId is not null)
            .Select(j => j.QueuedByHouseholdMemberId!.Value)
            .Distinct()
            .ToList();
        var members = memberIds.Count > 0
            ? await backgroundJobRepository.FindMembersByIdsAsync(memberIds, cancellationToken)
            : [];
        var displayNamesByMemberId = members.ToDictionary(m => m.Id, m => m.DisplayName);

        // Review-round-2 patch: batched the same way as imports/members above instead of one
        // ListGapsByImportIdAsync call per Flagged for Review row inside the loop below (N+1).
        // A FlaggedForReview import always carries exactly one gap row (Story 3.3/3.7's own
        // PersistFlaggedForReviewImportAsync precedent).
        var flaggedImportIds = imports.Where(i => i.Status == SmartPlugImportStatus.FlaggedForReview).Select(i => i.Id).ToList();
        var gaps = flaggedImportIds.Count > 0
            ? await smartPlugImportRepository.ListGapsByImportIdsAsync(flaggedImportIds, cancellationToken)
            : [];
        var gapsByImportId = gaps.GroupBy(g => g.SmartPlugImportId).ToDictionary(g => g.Key, g => (IReadOnlyList<SmartPlugImportGap>)g.ToList());

        var results = new List<SmartPlugImportJobResult>(jobs.Count);
        foreach (var job in jobs)
        {
            importsByJobId.TryGetValue(job.Id, out var import);
            var state = DeriveState(job, import);

            var jobGaps = state == SmartPlugImportJobState.FlaggedForReview && import is not null
                && gapsByImportId.TryGetValue(import.Id, out var importGaps)
                ? importGaps
                : [];

            var displayName = job.QueuedByHouseholdMemberId is { } memberId && displayNamesByMemberId.TryGetValue(memberId, out var name)
                ? name
                : null;

            results.Add(new SmartPlugImportJobResult(
                job.Id,
                job.OriginalFileName,
                state,
                displayName,
                job.CreatedAtUtc,
                job.CompletedAtUtc,
                job.ErrorMessage,
                import?.Id,
                import?.DeviceTag,
                jobGaps));
        }

        return results;
    }

    private static SmartPlugImportJobState DeriveState(BackgroundJob job, SmartPlugImport? import) => job.Status switch
    {
        BackgroundJobStatus.Queued => SmartPlugImportJobState.Waiting,
        BackgroundJobStatus.Processing => SmartPlugImportJobState.Processing,
        BackgroundJobStatus.Failed => SmartPlugImportJobState.Error,
        // SmartPlugImportStatus.Failed never co-occurs with BackgroundJobStatus.Completed —
        // PersistFailedImportAsync and the processor's failure branch always set both to their
        // respective failed states together (ProcessSmartPlugImport.cs's catch block) — so this
        // combination doesn't need its own case here.
        BackgroundJobStatus.Completed => import?.Status switch
        {
            SmartPlugImportStatus.AwaitingPowerPointMapping => SmartPlugImportJobState.NeedsMapping,
            SmartPlugImportStatus.FlaggedForReview => SmartPlugImportJobState.FlaggedForReview,
            // Review-round-2 patch: a Completed job with no paired SmartPlugImport row (legacy
            // data, or a future JobType with no import concept) used to fall through to Success
            // here — silently misreporting a data-integrity gap as a clean success instead of
            // surfacing it.
            null => SmartPlugImportJobState.Error,
            _ => SmartPlugImportJobState.Success,
        },
        _ => throw new InvalidOperationException($"Unexpected BackgroundJobStatus '{job.Status}' for job {job.Id}."),
    };
}
