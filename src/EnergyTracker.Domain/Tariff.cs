namespace EnergyTracker.Domain;

// AD-11's shared audit-trail table anticipated this exact entity (see AuditCorrection.cs's own
// comment) — Tariff.Currency is its own required column, separate from Household.Currency
// (consistency-conventions.md's authoritative resolution of a real tension in the source docs).
public class Tariff
{
    public required Guid Id { get; init; }

    public required Guid HouseholdId { get; init; }

    public required decimal MonthlyBaseFee { get; set; }

    public required decimal PricePerKwh { get; set; }

    // ISO 4217 code, e.g. "EUR"/"USD" — never hardcoded, always required (AC #5, NFR6).
    public required string Currency { get; set; }

    // DateTimeOffset, not DateOnly — AD-2's portable relational subset doesn't include DateOnly.
    // Mirrors MeterReading.ReadingTimestamp's precedent.
    public required DateTimeOffset ContractStartDate { get; set; }

    public required int ContractPeriodMonths { get; set; }

    // Server insert time — breaks ties when two entries share a ContractStartDate, same role as
    // MeterReading.CreatedAtUtc.
    public required DateTimeOffset CreatedAtUtc { get; init; }

    // Portable EF Core concurrency token (AD-4) — mirrors MeterReading.Version/Household.Version.
    public int Version { get; set; }
}
