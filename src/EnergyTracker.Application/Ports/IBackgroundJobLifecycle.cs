namespace EnergyTracker.Application.Ports;

// AD-6 (amended 2026-10-10): the one place a BackgroundJob's Status changes. Every method is a
// single conditional update (WHERE Id AND Status IN expected, plus the StartedAtUtc ownership token
// when leaving Processing), so a terminal status is never overwritten and "zero rows affected"
// tells the caller it no longer owns the job. Enforced by BackgroundJobStatusHasOneWriterTests.
public interface IBackgroundJobLifecycle
{
    // Queued -> Processing, or a Processing row with a stale heartbeat -> Processing (redelivery of
    // an orphan). Returns the new ownership token (StartedAtUtc), or null when the row is missing,
    // terminal, or still owned by a live run.
    Task<DateTimeOffset?> TryStartAsync(Guid jobId, CancellationToken cancellationToken);

    Task<bool> TryHeartbeatAsync(Guid jobId, DateTimeOffset token, CancellationToken cancellationToken);

    Task<bool> TryCompleteAsync(Guid jobId, DateTimeOffset token, CancellationToken cancellationToken);

    Task<bool> TryFailAsync(Guid jobId, DateTimeOffset token, string? errorMessage, CancellationToken cancellationToken);

    // Read-path check: fails this Household's Processing rows whose heartbeat is stale with
    // "job-interrupted". Queued rows are never failed for age. Returns the count.
    Task<int> FailStaleAsync(Guid householdId, CancellationToken cancellationToken);

    // Startup sweep (in-process queue only, AD-24): fails this Household's Queued/Processing rows
    // created before this process started — the channel they were sent through died with it.
    Task<int> FailInterruptedBeforeAsync(Guid householdId, DateTimeOffset processStartedAtUtc, CancellationToken cancellationToken);
}
