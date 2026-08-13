namespace EnergyTracker.Domain;

public class HouseholdMember
{
    public required Guid Id { get; init; }

    public required Guid HouseholdId { get; init; }

    // OIDC `iss` and `sub` claims, stored separately: `sub` alone is only guaranteed unique
    // within one issuer, and the provider must be swappable via config with no code change
    // (NFR3) without risking two different real people colliding onto the same row.
    public required string ExternalIssuer { get; init; }

    public required string ExternalSubjectId { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }
}
