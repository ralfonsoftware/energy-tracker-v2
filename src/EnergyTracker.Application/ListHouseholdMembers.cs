using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Lists the members of the caller's Household in join order (AC #1, #7).</summary>
public class ListHouseholdMembers(IHouseholdRepository repository)
{
    public async Task<IReadOnlyList<HouseholdMember>> ExecuteAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var members = await repository.ListMembersAsync(householdId, cancellationToken);
        return members.OrderBy(m => m.CreatedAtUtc).ThenBy(m => m.Id).ToList();
    }
}
