namespace EnergyTracker.Application;

/// <summary>Thrown when a presented invite token does not match any invite. Carries no token: it must never reach a response or a log (Story 11.9 AC #11).</summary>
public class HouseholdInviteNotFoundException() : Exception("No HouseholdInvite found for the presented token.");
