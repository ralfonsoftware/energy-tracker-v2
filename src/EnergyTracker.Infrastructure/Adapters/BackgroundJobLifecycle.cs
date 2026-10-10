using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

// AD-6 (amended 2026-10-10): the only writer of BackgroundJob.Status after the enqueue-time Queued
// insert. ExecuteUpdateAsync only (no tracked entities, AD-2 portable LINQ, no raw SQL). Household
// isolation is the standard AD-3 query filter, which resolves the Household from JobHouseholdContext
// on the job path; the two bulk methods that take a householdId (FailStale, FailInterruptedBefore)
// also state it as an explicit predicate, the per-job methods are keyed by Id (+ status and token).
// Callers must run FailStaleAsync before reading the same rows in a scope: ExecuteUpdateAsync
// bypasses the change tracker.
public class BackgroundJobLifecycle(EnergyTrackerDbContext dbContext, JobLifecycleTimings timings) : IBackgroundJobLifecycle
{
    public async Task<DateTimeOffset?> TryStartAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var token = NewToken();
        var staleBefore = DateTimeOffset.UtcNow - timings.StaleAfter;

        // A Processing row written by the previous image has no heartbeat or start time; falling
        // back to CreatedAtUtc makes it restartable (or failable) after 5 minutes instead of never.
        var affected = await dbContext.BackgroundJobs
            .Where(j => j.Id == jobId
                && (j.Status == BackgroundJobStatus.Queued
                    || (j.Status == BackgroundJobStatus.Processing && (j.HeartbeatAtUtc ?? j.StartedAtUtc ?? j.CreatedAtUtc) < staleBefore)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, BackgroundJobStatus.Processing)
                .SetProperty(j => j.StartedAtUtc, token)
                .SetProperty(j => j.HeartbeatAtUtc, token), cancellationToken);

        return affected > 0 ? token : null;
    }

    public async Task<bool> TryHeartbeatAsync(Guid jobId, DateTimeOffset token, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var affected = await dbContext.BackgroundJobs
            .Where(j => j.Id == jobId && j.Status == BackgroundJobStatus.Processing && j.StartedAtUtc == token)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.HeartbeatAtUtc, now), cancellationToken);
        return affected > 0;
    }

    public async Task<bool> TryCompleteAsync(Guid jobId, DateTimeOffset token, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var affected = await dbContext.BackgroundJobs
            .Where(j => j.Id == jobId && j.Status == BackgroundJobStatus.Processing && j.StartedAtUtc == token)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, BackgroundJobStatus.Completed)
                .SetProperty(j => j.CompletedAtUtc, now), cancellationToken);
        return affected > 0;
    }

    public async Task<bool> TryFailAsync(Guid jobId, DateTimeOffset token, string? errorMessage, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var affected = await dbContext.BackgroundJobs
            .Where(j => j.Id == jobId && j.Status == BackgroundJobStatus.Processing && j.StartedAtUtc == token)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, BackgroundJobStatus.Failed)
                .SetProperty(j => j.ErrorMessage, errorMessage)
                .SetProperty(j => j.CompletedAtUtc, now), cancellationToken);
        return affected > 0;
    }

    public async Task<int> FailStaleAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now - timings.StaleAfter;
        return await dbContext.BackgroundJobs
            .Where(j => j.HouseholdId == householdId
                && j.Status == BackgroundJobStatus.Processing
                && (j.HeartbeatAtUtc ?? j.StartedAtUtc ?? j.CreatedAtUtc) < staleBefore)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, BackgroundJobStatus.Failed)
                .SetProperty(j => j.ErrorMessage, JobFailureCodes.Interrupted)
                .SetProperty(j => j.CompletedAtUtc, now), cancellationToken);
    }

    public async Task<int> FailInterruptedBeforeAsync(Guid householdId, DateTimeOffset processStartedAtUtc, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await dbContext.BackgroundJobs
            .Where(j => j.HouseholdId == householdId
                && (j.Status == BackgroundJobStatus.Queued || j.Status == BackgroundJobStatus.Processing)
                && j.CreatedAtUtc < processStartedAtUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, BackgroundJobStatus.Failed)
                .SetProperty(j => j.ErrorMessage, JobFailureCodes.Interrupted)
                .SetProperty(j => j.CompletedAtUtc, now), cancellationToken);
    }

    // The token is StartedAtUtc and must compare equal after a database round trip: .NET ticks are
    // 100 ns, Postgres timestamptz keeps microseconds and would silently truncate, so an untruncated
    // token would never match on the later UPDATE ... WHERE StartedAtUtc = @token. Whole
    // milliseconds survive both providers (SQL Server datetimeoffset(7) keeps ticks).
    private static DateTimeOffset NewToken()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
    }
}
