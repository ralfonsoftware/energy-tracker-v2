namespace EnergyTracker.Application;

/// <summary>Thrown when an invite id is not an open invite of the caller's Household (unknown, other Household's, consumed, expired or revoked).</summary>
public class HouseholdInviteNotOpenException() : Exception("No open HouseholdInvite with that id exists in this Household.");
