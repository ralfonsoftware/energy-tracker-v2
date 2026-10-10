namespace EnergyTracker.Application;

// Stable codes that travel in BackgroundJob.ErrorMessage and are translated by the client's
// catalogs (AD-18); exact string equality is how the client recognises one.
public static class JobFailureCodes
{
    public const string Interrupted = "job-interrupted";

    // Emitted by the Azure queue consumer (Story 11.3); translated already by Story 11.2.
    public const string RetriesExhausted = "job-retries-exhausted";

    public const string UploadMissing = "upload-missing";
}
