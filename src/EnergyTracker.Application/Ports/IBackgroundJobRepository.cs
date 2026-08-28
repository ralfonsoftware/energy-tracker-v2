using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface IBackgroundJobRepository
{
    Task<BackgroundJob?> FindByIdAsync(Guid householdId, Guid jobId, CancellationToken cancellationToken);

    // Household-wide (AD-3's query filter still scopes to one Household), ordered newest-first —
    // the household-wide Job Status & History list (Story 3.6/FR-32), not just the caller's own
    // jobs.
    Task<IReadOnlyList<BackgroundJob>> ListByJobTypeAsync(Guid householdId, string jobType, CancellationToken cancellationToken);

    // Batch-load, for resolving BackgroundJob.QueuedByHouseholdMemberId into a "Queued by
    // {member}" display name (Story 3.6/UX-DR21) without an N+1 query per job row.
    Task<IReadOnlyList<HouseholdMember>> FindMembersByIdsAsync(IReadOnlyList<Guid> memberIds, CancellationToken cancellationToken);
}
