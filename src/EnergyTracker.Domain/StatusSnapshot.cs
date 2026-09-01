namespace EnergyTracker.Domain;

// Immutable, insert-only — NFR9's recomputation policy requires that a later Yearly
// Baseline/threshold edit never rewrites a past snapshot's value. Unlike MeterReading/Household
// there is deliberately no Version column: nothing ever updates a row after it's inserted.
//
// Only ever written for a definite (non-null) Status — the recompute path skips the write
// entirely when the live computation is undefined (fewer than two Readings, or no Yearly
// Baseline), so Status here is non-nullable.
//
// EffectiveAtUtc (Story 4.3) vs. ComputedAtUtc: EffectiveAtUtc is the trend-timeline point this
// row represents; ComputedAtUtc stays a pure write-audit timestamp ("when this row was actually
// inserted"). For every normal write-triggered snapshot (a Meter Reading save, Smart Plug import/
// mapping completion) the two are identical. They diverge only for a correction's *superseding*
// snapshot (IStatusRecomputeService.RecomputeForwardFromAsync): EffectiveAtUtc is backdated to
// the historical point being corrected, ComputedAtUtc is the (later) moment the correction ran.
// This is how a correction "updates" history without ever mutating a row: the read path
// (StatusSnapshotRepository.GetForHouseholdAsync) picks the row with the greatest ComputedAtUtc
// per distinct EffectiveAtUtc, so a fresher row simply outranks the stale one at read time —
// immutability (above) is preserved, not relaxed.
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

    public required DateTimeOffset EffectiveAtUtc { get; init; }
}
