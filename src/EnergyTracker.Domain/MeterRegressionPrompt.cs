namespace EnergyTracker.Domain;

// AD-12: at most one open MeterRegressionPrompt per Main Meter. "Open" is computed (earliest
// unresolved by MeterReading.ReadingTimestamp), never a persisted flag — see IMeterRegressionPromptRepository.
public class MeterRegressionPrompt
{
    public required Guid Id { get; init; }

    // Denormalized, matching MeterReading's AD-3 pattern — not a join through MainMeter.
    public required Guid HouseholdId { get; init; }

    public required Guid MainMeterId { get; init; }

    // The flagged/lower reading that triggered this prompt.
    public required Guid MeterReadingId { get; init; }

    // The reading it regressed against (immediately preceding by ReadingTimestamp).
    public required Guid PreviousMeterReadingId { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? ResolvedAtUtc { get; set; }

    public MeterRegressionClassification? Classification { get; set; }

    // Only ever set when Classification == Rollover.
    public decimal? DigitCapacityKwh { get; set; }
}
