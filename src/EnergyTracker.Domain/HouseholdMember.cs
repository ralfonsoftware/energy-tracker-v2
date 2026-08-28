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

    // Captured from the OIDC `name` claim at membership-creation time (household creation /
    // invite acceptance) — nullable, since a pre-existing member or a provider that never returns
    // a name claim both legitimately have none; never fabricate one (Story 3.6/UX-DR21).
    public string? DisplayName { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }
}
