namespace EnergyTracker.Domain;

// AD-11's shared audit-trail table — Story 2.8 is the first consumer (MeterReading.KwhValue
// corrections), a future Tariff-editing story is expected to reuse this unmodified.
public class AuditCorrection
{
    public required Guid Id { get; init; }

    public required Guid HouseholdId { get; init; }

    // Plain discriminator string, not an enum — a future entity type (e.g. "Tariff") is a data
    // addition, not a schema change.
    public required string EntityType { get; init; }

    public required Guid EntityId { get; init; }

    public required string FieldName { get; init; }

    // Locale-neutral storage (AD-18) — decimal.ToString(CultureInfo.InvariantCulture), never the
    // ambient/household locale. Only display formatting is locale-aware.
    public required string OldValue { get; init; }

    public required string NewValue { get; init; }

    public required DateTimeOffset CorrectedAtUtc { get; init; }
}
