namespace EnergyTracker.Domain;

// Tracks one uploaded Smart Plug export file end to end. AwaitingPowerPointMapping is a
// well-defined terminal state for THIS story (Story 3.2 owns building the create/map prompt UI
// that resolves it) — not a transient in-between state.
public class SmartPlugImport
{
    public required Guid Id { get; init; }

    // Denormalized, matching MeterReading/MeterRegressionPrompt/BackgroundJob's AD-3 pattern.
    public required Guid HouseholdId { get; init; }

    public required Guid BackgroundJobId { get; init; }

    public required SmartPlugVendorFormat VendorFormat { get; init; }

    public required string OriginalFileName { get; init; }

    public required SmartPlugImportStatus Status { get; set; }

    // The device/room tag parsed from the file, used to attempt a Power Point match by exact
    // name (Task 3). Kept even after a successful match so a failed/awaiting import still shows
    // what the file identified itself as.
    public required string DeviceTag { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public enum SmartPlugVendorFormat
{
    EveHome,
    Meross,
}

public enum SmartPlugImportStatus
{
    Processing,
    AwaitingPowerPointMapping,
    Completed,
    Failed,

    // A terminal state distinct from Completed/Failed — the file parsed successfully (no
    // exception), but every date in its own covered range came back with zero readings (AC #7,
    // FR-24). Stored as a plain int (no HasConversion on this enum), so adding this member needs
    // no migration by itself.
    FlaggedForReview,
}
