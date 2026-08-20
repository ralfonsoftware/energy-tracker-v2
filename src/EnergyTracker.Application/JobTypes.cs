namespace EnergyTracker.Application;

// Job-type vocabulary shared between the enqueuing endpoint (SmartPlugImportEndpoints) and the
// dispatch loop that reads it back off the queue (Infrastructure's BackgroundJobProcessor) — one
// constant per async job type this codebase knows about (AD-6).
public static class JobTypes
{
    public const string ProcessSmartPlugImport = "ProcessSmartPlugImport";
}
