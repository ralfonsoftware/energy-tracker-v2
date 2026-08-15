namespace EnergyTracker.Application;

/// <summary>Thrown when a Household update (e.g. Yearly Baseline) loses an AD-4 concurrency race.</summary>
public class HouseholdConcurrencyConflictException(Guid householdId)
    : Exception($"Household '{householdId}' was updated by someone else. Refresh and try again.")
{
    public Guid HouseholdId { get; } = householdId;
}
