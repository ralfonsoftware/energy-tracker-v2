namespace EnergyTracker.Application;

/// <summary>Thrown when an invite has already been consumed, has expired or was revoked. Carries no token (Story 11.9 AC #11).</summary>
public class HouseholdInviteExpiredOrConsumedException() : Exception("This HouseholdInvite is expired or already consumed.");
