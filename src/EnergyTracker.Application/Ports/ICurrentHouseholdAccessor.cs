namespace EnergyTracker.Application.Ports;

/// <summary>
/// Resolves the Household of the current authenticated principal (AD-3's HTTP-request
/// resolution path). Null means the principal is authenticated but has not created or
/// joined a Household yet — the caller must route them into Household creation, not treat
/// this as "no Household exists system-wide" (a deployment may hold more than one).
/// </summary>
public interface ICurrentHouseholdAccessor
{
    Guid? HouseholdId { get; }

    // The caller's own HouseholdMember row id — null under the exact same conditions HouseholdId
    // is null. Feeds BackgroundJob.QueuedByHouseholdMemberId (Story 3.6/AD-6 extension) so a
    // queued job records who queued it, not just which Household.
    Guid? HouseholdMemberId { get; }
}
