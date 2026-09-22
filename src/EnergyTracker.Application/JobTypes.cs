namespace EnergyTracker.Application;

// Job-type vocabulary shared between the enqueuing endpoint (SmartPlugImportEndpoints) and the
// dispatch loop that reads it back off the queue (Infrastructure's BackgroundJobProcessor) — one
// constant per async job type this codebase knows about (AD-6).
public static class JobTypes
{
    public const string ProcessSmartPlugImport = "ProcessSmartPlugImport";

    // Story 3.10 incident fix, round 3 (2026-09-12): "clean up everything" moved off the
    // synchronous DELETE request onto this same async pattern — a household's total cleanup work
    // can exceed Azure Container Apps' ~240s HTTP ingress ceiling even with every DB command
    // individually bounded (CAP-1/CAP-4). No chunk-size tuning fixes a wall-clock ceiling.
    public const string CleanUpSmartPlugImportJobs = "CleanUpSmartPlugImportJobs";

    // Story 6.3 — CreateEvent unconditionally enqueues this; CorrelateEvent is the one place that
    // checks Household.AiPlausibilityEnabled/whether a real IAiPlausibilityClient is configured
    // (AD-8's anti-hard-branch rule).
    public const string CorrelateEvent = "CorrelateEvent";

    // Story 7.2 — the wholesale delete+insert restore, same async-job shape as
    // CleanUpSmartPlugImportJobs above (Epic 6 Retro Action Item #3: this is the same
    // "large bulk write" incident shape, just across all 12 entity categories).
    public const string RestoreHouseholdData = "RestoreHouseholdData";
}
