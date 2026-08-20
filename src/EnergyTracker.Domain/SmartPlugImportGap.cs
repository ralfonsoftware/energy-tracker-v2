namespace EnergyTracker.Domain;

// A separate entity from SmartPlugReading, not a flag column on it — keeps "is this row measured"
// unambiguous everywhere SmartPlugReading is already queried, and keeps a gap's own lifecycle
// (detected once at import-completion time, never re-evaluated later) independent of the readings
// it sits alongside (FR-24, Story 3.3 Dev Notes).
public class SmartPlugImportGap
{
    public required Guid Id { get; init; }

    // Denormalized, matching SmartPlugReading/SmartPlugImport's AD-3 pattern.
    public required Guid HouseholdId { get; init; }

    public required Guid SmartPlugImportId { get; init; }

    // Null only for the AC #7 whole-file FlaggedForReview case, which never resolves a Power
    // Point — every Estimated/Missing gap always has one (gap detection only ever runs once a
    // Power Point is known, AD-10).
    public Guid? PowerPointId { get; init; }

    // Calendar-date granularity — gap detection walks dates, not timestamps (see
    // SmartPlugGapDetector).
    public required DateOnly StartDate { get; init; }

    public required DateOnly EndDate { get; init; }

    public required SmartPlugImportGapTreatment Treatment { get; init; }

    // Only set when Treatment == Estimated.
    public decimal? EstimatedTotalKwh { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }
}

public enum SmartPlugImportGapTreatment
{
    Estimated,
    Missing,
    FlaggedForReview,
}
