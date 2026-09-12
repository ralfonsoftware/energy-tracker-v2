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
}
