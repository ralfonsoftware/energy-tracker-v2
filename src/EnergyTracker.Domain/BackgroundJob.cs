namespace EnergyTracker.Domain;

// DB-persisted (not in-memory) — Azure Container Apps can scale to zero between a client
// enqueuing a job and its next poll, so job state must survive that gap (AD-6).
public class BackgroundJob
{
    public required Guid Id { get; init; }

    // Denormalized, matching MeterReading/MeterRegressionPrompt's AD-3 pattern.
    public required Guid HouseholdId { get; init; }

    public required string JobType { get; init; }

    public required BackgroundJobStatus Status { get; set; }

    public string? ErrorMessage { get; set; }

    // Captured at enqueue time (Story 3.6/AD-6 extension), before a SmartPlugImport row exists —
    // ProcessSmartPlugImport only creates one once parsing finishes, but Waiting/Processing rows
    // in the household-wide job list still need a filename/attribution to render.
    public string? OriginalFileName { get; init; }

    public Guid? QueuedByHouseholdMemberId { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public enum BackgroundJobStatus
{
    Processing,
    Completed,
    Failed,

    // Story 3.6/AD-6 extension: a job enqueued but not yet dequeued (cold start, or a later file
    // in a multi-file queue). Stored as a plain int (no HasConversion on this enum) — appended at
    // the end, never inserted before Processing, or every already-persisted Processing/Completed/
    // Failed row would be silently reinterpreted the moment this migration runs (same discipline
    // Story 3.3 established for SmartPlugImportStatus.FlaggedForReview).
    Queued,
}
