namespace EnergyTracker.Application;

/// <summary>Thrown when a member id is not a member of the caller's Household; never distinguishes another Household's member (AD-26).</summary>
public class HouseholdMemberNotFoundException(Guid memberId) : Exception($"HouseholdMember '{memberId}' is not a member of this Household.")
{
    public Guid MemberId { get; } = memberId;
}
