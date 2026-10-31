namespace EnergyTracker.Domain;

public class HouseholdInvite
{
    public required Guid Id { get; init; }

    public required Guid HouseholdId { get; init; }

    // Lowercase hex SHA-256 of the token in the shareable /join/{token} URL (AD-26).
    // The only credential material stored: 256 random bits, so a plain hash suffices.
    public string? TokenHash { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    // Mutable: revoking an invite and removing a member expire open invites in place (Story 11.9).
    public required DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset? ConsumedAtUtc { get; set; }

    // Portable EF Core concurrency token (AD-4) — guards two concurrent accepts of the same
    // single-use invite from both succeeding, and an accept racing a revoke.
    public int Version { get; set; }
}
