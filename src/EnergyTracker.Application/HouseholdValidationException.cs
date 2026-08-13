namespace EnergyTracker.Application;

/// <summary>Thrown when Household-creation input (locale/currency) fails validation.</summary>
public class HouseholdValidationException(string message) : Exception(message);
