namespace EnergyTracker.Domain;

// First enum in this codebase — mapped to/from a plain string in endpoint DTOs (see
// MeterRegressionPromptEndpoints), not via a global JsonStringEnumConverter. Don't add one.
public enum MeterRegressionClassification
{
    Reset,
    Rollover,
}
