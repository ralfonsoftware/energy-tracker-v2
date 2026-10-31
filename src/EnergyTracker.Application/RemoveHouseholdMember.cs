using System.Diagnostics.CodeAnalysis;
using EnergyTracker.Application.Ports;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Application;

/// <summary>
/// Removes a member from the caller's Household, or lets the caller leave it (AC #2, AD-26).
/// No role check: every member is fully and equally trusted (AC #8).
/// </summary>
public class RemoveHouseholdMember(IHouseholdRepository repository, ILogger<RemoveHouseholdMember> logger, TimeProvider timeProvider)
{
    [SuppressMessage("Performance", "CA1873:Avoid potentially expensive logging", Justification = "Reads of already-computed values on an always-enabled log level; the arguments cost nothing to evaluate.")]
    public async Task ExecuteAsync(Guid householdId, Guid actingMemberId, Guid targetMemberId, CancellationToken cancellationToken)
    {
        await repository.RemoveMemberAsync(householdId, targetMemberId, timeProvider.GetUtcNow(), cancellationToken);

        // Ids only: no display name, e-mail, issuer or subject (project-context "Security").
        logger.LogInformation(
            "Household member removed. HouseholdId={HouseholdId} RemovedMemberId={RemovedMemberId} RemovedByMemberId={RemovedByMemberId} Leaving={Leaving}",
            householdId,
            targetMemberId,
            actingMemberId,
            targetMemberId == actingMemberId);
    }
}
