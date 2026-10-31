using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Lists the caller's Household's invites that are neither consumed nor expired, newest first (AC #1, #7).</summary>
public class ListOpenHouseholdInvites(IHouseholdRepository repository, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<HouseholdInvite>> ExecuteAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var invites = await repository.ListOpenInvitesAsync(householdId, timeProvider.GetUtcNow(), cancellationToken);
        return invites.OrderByDescending(i => i.CreatedAtUtc).ThenBy(i => i.Id).ToList();
    }
}
