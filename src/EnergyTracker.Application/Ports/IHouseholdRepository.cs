using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface IHouseholdRepository
{
    Task<HouseholdMember?> FindMemberAsync(string externalIssuer, string externalSubjectId, CancellationToken cancellationToken);

    /// <summary>Persists the new Household and its creating HouseholdMember as a single unit of work.</summary>
    Task AddAsync(Household household, HouseholdMember creator, CancellationToken cancellationToken);
}
