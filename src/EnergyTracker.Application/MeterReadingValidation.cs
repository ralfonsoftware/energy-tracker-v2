namespace EnergyTracker.Application;

// Shared kWh-value validation bound, extracted from CreateMeterReading so it and EditMeterReading
// can't silently drift on the same business rule (Story 2.7's Task 4 precedent for the
// pace/baseline difference sign logic).
internal static class MeterReadingValidation
{
    // A meter reading is a cumulative lifetime total, not a small human-entered figure like
    // Yearly Baseline — no low arbitrary business cap. The bound here exists only to keep values
    // inside the decimal(18,2) column's range so an out-of-range submission fails validation
    // (400) instead of a provider-level overflow (500).
    public const decimal MaxKwhValue = 1_000_000_000_000_000m; // 10^15, one order below 10^16 overflow.

    public static void ValidateKwhValue(decimal kwhValue)
    {
        if (kwhValue <= 0 || kwhValue >= MaxKwhValue)
        {
            throw new MeterReadingValidationException(
                $"kWh value must be a positive number less than {MaxKwhValue}, got '{kwhValue}'.");
        }
    }
}
