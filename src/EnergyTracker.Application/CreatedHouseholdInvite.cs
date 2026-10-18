namespace EnergyTracker.Application;

/// <summary>A freshly created invite: the plaintext token exists only here, once, because only its hash is stored.</summary>
public record CreatedHouseholdInvite(string Token, DateTimeOffset ExpiresAtUtc);
