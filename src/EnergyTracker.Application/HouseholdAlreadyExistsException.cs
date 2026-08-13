namespace EnergyTracker.Application;

/// <summary>Thrown when the authenticated principal already has a HouseholdMember row and attempts to create another Household.</summary>
public class HouseholdAlreadyExistsException(Guid existingHouseholdId)
    : Exception($"Principal already belongs to Household '{existingHouseholdId}'.")
{
    public Guid ExistingHouseholdId { get; } = existingHouseholdId;
}
