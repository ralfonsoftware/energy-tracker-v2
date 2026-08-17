namespace EnergyTracker.Domain;

// Immutable, insert-only — NFR9's recomputation policy requires that a later Yearly
// Baseline/threshold edit never rewrites a past snapshot's value. Unlike MeterReading/Household
// there is deliberately no Version column: nothing ever updates a row after it's inserted.
//
// Only ever written for a definite (non-null) Status — the recompute path skips the write
// entirely when the live computation is undefined (fewer than two Readings, or no Yearly
// Baseline), so Status here is non-nullable.
public class StatusSnapshot
{
    public required Guid Id { get; init; }

    // Denormalized, matching MeterReading/MeterRegressionPrompt's AD-3 pattern — not a join
    // through MainMeter.
    public required Guid HouseholdId { get; init; }

    public required Status Status { get; init; }

    public required decimal PaceToDateKwh { get; init; }

    public required decimal BaselineToDateKwh { get; init; }

    public required bool IsLowConfidence { get; init; }

    public required DateTimeOffset ComputedAtUtc { get; init; }
}
