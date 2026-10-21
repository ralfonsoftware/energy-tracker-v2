namespace EnergyTracker.Application;

// Stable codes that travel in BackgroundJob.ErrorMessage and are translated by the client's
// catalogs (AD-18); exact string equality is how the client recognises one.
public static class JobFailureCodes
{
    public const string Interrupted = "job-interrupted";

    // Emitted by the Azure queue consumer (Story 11.3); translated already by Story 11.2.
    public const string RetriesExhausted = "job-retries-exhausted";

    public const string UploadMissing = "upload-missing";

    // Story 11.4: a smart plug file's device tag exceeds SmartPlugReading.DeviceNameMaxLength.
    public const string DeviceTagTooLong = "device-tag-too-long";

    // Story 11.10: a spreadsheet decompresses past the parser's size or row limits (audit S4).
    public const string SpreadsheetTooLarge = "spreadsheet-too-large";
}
