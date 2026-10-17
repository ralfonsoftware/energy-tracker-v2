namespace EnergyTracker.Application;

/// <summary>Thrown when removing or leaving would leave the Household without any member (AD-26).</summary>
public class LastHouseholdMemberException(Guid householdId) : Exception($"Household '{householdId}' has only one member left.")
{
    public Guid HouseholdId { get; } = householdId;
}
