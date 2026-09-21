namespace EnergyTracker.Application;

// Deployment-wide, read once at the composition root from
// AiPlausibility:BaseUrl/BackendLabel/Model (Program.cs) and registered as a singleton —
// Configured/Label are the "what's always visible" half of AC #5 that isn't Household-scoped;
// Model is consumed only by OpenAiCompatibleClient itself, never exposed in the API response.
// Label is a plain human-set deploy-time value (e.g. "Local (LMStudio)" or "OpenAI"), never a
// heuristic guess derived from the URL. Model defaults to "default" (LMStudio ignores the field
// entirely) — a real cloud provider such as OpenAI requires an explicit, valid model id here,
// since it rejects an unrecognized one outright.
public record AiPlausibilityBackendOptions(bool Configured, string? Label, string Model);
