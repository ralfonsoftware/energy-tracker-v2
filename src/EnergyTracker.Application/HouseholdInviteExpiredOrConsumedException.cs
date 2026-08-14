namespace EnergyTracker.Application;

/// <summary>Thrown when a HouseholdInvite token has already been consumed or has expired.</summary>
public class HouseholdInviteExpiredOrConsumedException(string token) : Exception($"HouseholdInvite for token '{token}' is expired or already consumed.");
