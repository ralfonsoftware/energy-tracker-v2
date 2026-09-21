using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Sets a Household's AI Wattage Plausibility on/off toggle under AD-4 optimistic concurrency (AC #5, #6).</summary>
public class SetAiPlausibilityEnabled(IHouseholdRepository repository)
{
    public Task<Household> ExecuteAsync(Guid householdId, bool enabled, int expectedVersion, CancellationToken cancellationToken) =>
        repository.UpdateAiPlausibilityEnabledAsync(householdId, enabled, expectedVersion, cancellationToken);
}
