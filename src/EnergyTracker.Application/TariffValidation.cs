namespace EnergyTracker.Application;

// Shared Tariff-field validation, mirrors MeterReadingValidation's shape.
internal static class TariffValidation
{
    // Bounds exist only to keep values inside their decimal column's range so an out-of-range
    // submission fails validation (400) instead of a provider-level overflow (500) — same
    // reasoning as MeterReadingValidation.MaxKwhValue.
    // MonthlyBaseFee is decimal(18,2): true overflow is ~10^16, one order below that is 10^15.
    public const decimal MaxMonthlyBaseFee = 1_000_000_000_000_000m;

    // PricePerKwh is decimal(18,4): true overflow is ~10^14, one order below that is 10^13.
    public const decimal MaxPricePerKwh = 10_000_000_000_000m;

    public const int MaxContractPeriodMonths = 1200; // 100 years — generous upper bound, not a business rule.

    // Generous sanity bounds, not a business rule — mainly guards against a missing/omitted
    // ContractStartDate silently binding to default(DateTimeOffset) (0001-01-01) and being
    // accepted as a valid, immediately-"current" Tariff entry.
    public static readonly DateTimeOffset MinContractStartDate = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset MaxContractStartDate = new(2200, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void ValidateMonthlyBaseFee(decimal monthlyBaseFee)
    {
        if (monthlyBaseFee < 0 || monthlyBaseFee >= MaxMonthlyBaseFee)
        {
            throw new TariffValidationException(
                $"Monthly base fee must be a non-negative number less than {MaxMonthlyBaseFee}, got '{monthlyBaseFee}'.");
        }
    }

    public static void ValidatePricePerKwh(decimal pricePerKwh)
    {
        if (pricePerKwh <= 0 || pricePerKwh >= MaxPricePerKwh)
        {
            throw new TariffValidationException(
                $"Price per kWh must be a positive number less than {MaxPricePerKwh}, got '{pricePerKwh}'.");
        }
    }

    // Same "not blank, not obviously wrong" shape as CreateHousehold.IsPlausibleCurrencyCode —
    // full ISO 4217 membership validation isn't required for MVP.
    public static void ValidateCurrency(string? currency)
    {
        if (string.IsNullOrEmpty(currency) || currency.Length != 3 || !currency.All(c => c is >= 'A' and <= 'Z'))
        {
            throw new TariffValidationException(
                $"Invalid currency '{currency}'. Expected a 3-letter ISO 4217-shaped code (e.g. 'EUR').");
        }
    }

    public static void ValidateContractStartDate(DateTimeOffset contractStartDate)
    {
        if (contractStartDate < MinContractStartDate || contractStartDate > MaxContractStartDate)
        {
            throw new TariffValidationException(
                $"Contract start date must be between {MinContractStartDate:yyyy-MM-dd} and {MaxContractStartDate:yyyy-MM-dd}, got '{contractStartDate:O}'.");
        }
    }

    public static void ValidateContractPeriodMonths(int contractPeriodMonths)
    {
        if (contractPeriodMonths <= 0 || contractPeriodMonths > MaxContractPeriodMonths)
        {
            throw new TariffValidationException(
                $"Contract Period must be between 1 and {MaxContractPeriodMonths} months, got '{contractPeriodMonths}'.");
        }
    }
}
