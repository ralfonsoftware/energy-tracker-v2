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

    // Nullable — a Household may not have set one yet (AC #1: presets are suggestions, never a
    // silently-applied default).
    public decimal? YearlyBaselineKwh { get; set; }

    // Story 2.4 AC #4/#5 — unlike YearlyBaselineKwh above, this default IS silently applied at
    // creation (AD-15: household-scoped config, never a code literal, but the PRD's own FR-6
    // wording gives it a real default value up front, unlike the Yearly Baseline preset).
    public decimal TrendingThresholdKwh { get; set; } = 100m;

    // Story 2.4 AC #3 — "unusually long gap since the last reading" has no numeric default
    // anywhere in the PRD/epics; FR-3 only says qualitatively "hasn't logged in months". 45 days
    // is this story's placeholder, confirmed with Ralf during dev-story activation — see
    // Completion Notes.
    public int LowConfidenceGapDays { get; set; } = 45;

    // Portable EF Core concurrency token (AD-4) — guards two concurrent Yearly Baseline edits
    // from both succeeding. Household's first Version column; see HouseholdInvite.cs for the
    // established precedent this copies. Also covers the two Story 2.4 columns above — no new
    // concurrency token needed for them.
    public int Version { get; set; }

    public ICollection<HouseholdMember> Members { get; init; } = new List<HouseholdMember>();
}
