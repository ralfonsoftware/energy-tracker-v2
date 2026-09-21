namespace EnergyTracker.Application;

// Deployment-wide, read once at the composition root from AiPlausibility:BaseUrl/BackendLabel
// (Program.cs) and registered as a singleton — the "what's always visible" half of AC #5 that
// isn't Household-scoped. Label is a plain human-set deploy-time value (e.g. "Local (LMStudio)" or
// "OpenAI"), never a heuristic guess derived from the URL.
public record AiPlausibilityBackendOptions(bool Configured, string? Label);
