using EnergyTracker.Application.Ports;

namespace EnergyTracker.Application;

/// <summary>Revokes an open invite of the caller's Household by expiring it in place (AC #7, #9, #10).</summary>
public class RevokeHouseholdInvite(IHouseholdRepository repository)
{
    public Task ExecuteAsync(Guid householdId, Guid inviteId, CancellationToken cancellationToken) =>
        repository.RevokeInviteAsync(householdId, inviteId, DateTimeOffset.UtcNow, cancellationToken);
}
