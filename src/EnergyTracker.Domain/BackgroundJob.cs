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

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public enum BackgroundJobStatus
{
    Processing,
    Completed,
    Failed,
}
