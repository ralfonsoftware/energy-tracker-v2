using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Sets a Household's Yearly Baseline under AD-4 optimistic concurrency (AC #1, #2, #3, #4).</summary>
public class SetYearlyBaseline(IHouseholdRepository repository)
{
    // No real household plausibly exceeds this; also keeps the value well inside the
    // decimal(18,2) column's range so an out-of-range submission fails validation (400)
    // instead of a provider-level overflow (500).
    private const decimal MaxYearlyBaselineKwh = 1_000_000m;

    public async Task<Household> ExecuteAsync(
        Guid householdId,
        decimal yearlyBaselineKwh,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        if (yearlyBaselineKwh <= 0 || yearlyBaselineKwh > MaxYearlyBaselineKwh)
        {
            throw new HouseholdValidationException(
                $"Yearly Baseline must be a positive number of kWh no greater than {MaxYearlyBaselineKwh}, got '{yearlyBaselineKwh}'.");
        }

        return await repository.UpdateYearlyBaselineAsync(householdId, yearlyBaselineKwh, expectedVersion, cancellationToken);
    }
}
