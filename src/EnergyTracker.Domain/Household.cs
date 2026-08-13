namespace EnergyTracker.Domain;

public class Household
{
    public required Guid Id { get; init; }

    // Launch-Locale string (de-DE/en-US for now). A later Locale is a resource-file addition
    // (AD-18), not a code change, so this is intentionally a string, not an enum.
    public required string Locale { get; set; }

    // ISO 4217 currency code (e.g. "EUR", "USD"). Amounts elsewhere use decimal; this is just the code.
    public required string Currency { get; set; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public ICollection<HouseholdMember> Members { get; init; } = new List<HouseholdMember>();
}
