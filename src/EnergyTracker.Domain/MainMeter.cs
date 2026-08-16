namespace EnergyTracker.Domain;

// v2 scopes multi-meter UI/logic out entirely (deferred.md) — exactly one Main Meter per
// Household, enforced by a unique index on HouseholdId (MainMeterConfiguration). No name/label
// field and no meter-selection UI anywhere in the product for this reason.
public class MainMeter
{
    public required Guid Id { get; init; }

    public required Guid HouseholdId { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    // Nullable — unset until a Story 2.3 rollover classification captures it the first time
    // (AD-15: no hardcoded household-specific values). Mutable, like Household.YearlyBaselineKwh.
    public decimal? DigitCapacityKwh { get; set; }
}
