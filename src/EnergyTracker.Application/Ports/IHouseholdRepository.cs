using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface IHouseholdRepository
{
    Task<HouseholdMember?> FindMemberAsync(string externalIssuer, string externalSubjectId, CancellationToken cancellationToken);

    /// <summary>Persists the new Household and its creating HouseholdMember as a single unit of work.</summary>
    Task AddAsync(Household household, HouseholdMember creator, CancellationToken cancellationToken);

    Task AddInviteAsync(HouseholdInvite invite, CancellationToken cancellationToken);

    Task<HouseholdInvite?> FindInviteByTokenAsync(string token, CancellationToken cancellationToken);

    /// <summary>Marks the invite consumed and adds the new member as a single unit of work, returning the joined Household.</summary>
    Task<Household> AcceptInviteAsync(HouseholdInvite invite, HouseholdMember newMember, CancellationToken cancellationToken);
}
