using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>
/// Accepts a HouseholdInvite by token, joining the invited principal to the invite's Household
/// as a full, equal-access member (AC #1, #2). Plain class with a constructor-injected
/// repository port, matching CreateHousehold's shape.
/// </summary>
public class AcceptHouseholdInvite(IHouseholdRepository repository)
{
    public async Task<Household> ExecuteAsync(
        string token,
        string externalIssuer,
        string externalSubjectId,
        CancellationToken cancellationToken)
    {
        var invite = await repository.FindInviteByTokenAsync(token, cancellationToken)
            ?? throw new HouseholdInviteNotFoundException(token);

        if (invite.ConsumedAtUtc is not null || invite.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new HouseholdInviteExpiredOrConsumedException(token);
        }

        var existingMember = await repository.FindMemberAsync(externalIssuer, externalSubjectId, cancellationToken);
        if (existingMember is not null)
        {
            // Same invariant, same type CreateHousehold already uses for "this principal already
            // belongs to a Household" — there's no lesser "guest" tier to fall back to (AC #2).
            throw new HouseholdAlreadyExistsException(existingMember.HouseholdId);
        }

        var newMember = new HouseholdMember
        {
            Id = Guid.NewGuid(),
            HouseholdId = invite.HouseholdId,
            ExternalIssuer = externalIssuer,
            ExternalSubjectId = externalSubjectId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        return await repository.AcceptInviteAsync(invite, newMember, cancellationToken);
    }
}
