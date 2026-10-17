using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface IHouseholdRepository
{
    Task<HouseholdMember?> FindMemberAsync(string externalIssuer, string externalSubjectId, CancellationToken cancellationToken);

    Task<Household?> FindByIdAsync(Guid householdId, CancellationToken cancellationToken);

    /// <summary>Persists the new Household and its creating HouseholdMember as a single unit of work.</summary>
    Task AddAsync(Household household, HouseholdMember creator, CancellationToken cancellationToken);

    Task AddInviteAsync(HouseholdInvite invite, CancellationToken cancellationToken);

    /// <summary>Finds an invite by its plaintext token: the SHA-256 hash first, the legacy plaintext column second (AD-26). Null for an unknown or over-long token.</summary>
    Task<HouseholdInvite?> FindInviteByTokenAsync(string token, CancellationToken cancellationToken);

    /// <summary>The one by-id member lookup, scoped to <paramref name="householdId"/> (AD-26, AD-3 exception): null for another Household's member.</summary>
    Task<HouseholdMember?> FindMemberInHouseholdAsync(Guid householdId, Guid memberId, CancellationToken cancellationToken);

    Task<IReadOnlyList<HouseholdMember>> ListMembersAsync(Guid householdId, CancellationToken cancellationToken);

    /// <summary>Invites of the Household that are neither consumed nor expired at <paramref name="nowUtc"/>.</summary>
    Task<IReadOnlyList<HouseholdInvite>> ListOpenInvitesAsync(Guid householdId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the membership, expires every open invite of the Household and bumps <c>Household.Version</c> in one unit of work.
    /// Throws <see cref="HouseholdMemberNotFoundException"/>, <see cref="LastHouseholdMemberException"/> or <see cref="HouseholdConcurrencyConflictException"/> on a lost race.
    /// </summary>
    Task RemoveMemberAsync(Guid householdId, Guid memberId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>Expires an open invite of the Household in place; throws <see cref="HouseholdInviteNotOpenException"/> when it is not an open invite of that Household.</summary>
    Task RevokeInviteAsync(Guid householdId, Guid inviteId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>Marks the invite consumed and adds the new member as a single unit of work, returning the joined Household.</summary>
    Task<Household> AcceptInviteAsync(HouseholdInvite invite, HouseholdMember newMember, CancellationToken cancellationToken);

    /// <summary>Updates the Yearly Baseline under AD-4 optimistic concurrency, throwing <see cref="HouseholdConcurrencyConflictException"/> on a lost race.</summary>
    Task<Household> UpdateYearlyBaselineAsync(Guid householdId, decimal yearlyBaselineKwh, int expectedVersion, CancellationToken cancellationToken);

    /// <summary>Updates the Locale only — deliberately no expected version and no <c>Version</c> bump (Story 8.11 concurrency decision): last write wins and an unrelated in-flight edit is not invalidated.</summary>
    Task<Household> UpdateLocaleAsync(Guid householdId, string locale, CancellationToken cancellationToken);

    /// <summary>Updates the AI Wattage Plausibility on/off toggle under AD-4 optimistic concurrency, throwing <see cref="HouseholdConcurrencyConflictException"/> on a lost race.</summary>
    Task<Household> UpdateAiPlausibilityEnabledAsync(Guid householdId, bool enabled, int expectedVersion, CancellationToken cancellationToken);
}
