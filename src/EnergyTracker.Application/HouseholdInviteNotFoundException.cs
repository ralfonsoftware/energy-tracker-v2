namespace EnergyTracker.Application;

/// <summary>Thrown when a HouseholdInvite token does not match any existing invite.</summary>
public class HouseholdInviteNotFoundException(string token) : Exception($"No HouseholdInvite found for token '{token}'.");
