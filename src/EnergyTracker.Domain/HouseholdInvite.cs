namespace EnergyTracker.Domain;

public class HouseholdInvite
{
    public required Guid Id { get; init; }

    public required Guid HouseholdId { get; init; }

    // Opaque bearer credential embedded in the shareable /join/{token} URL. Generated as
    // Guid.NewGuid().ToString("N") — real entropy, not a human-typeable code, because this
    // token grants full, permanent, equal-access Household membership (energy-consumption
    // data is treated as sensitive per the PRD's Constraints — a proxy for occupancy patterns).
    public required string Token { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public DateTimeOffset? ConsumedAtUtc { get; set; }

    // Portable EF Core concurrency token (AD-4) — guards two concurrent accepts of the same
    // single-use invite from both succeeding.
    public int Version { get; set; }
}
