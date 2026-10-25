using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.Extensions.DependencyInjection;

namespace EnergyTracker.Api.Tests;

// Story 11.14 (audit C10, AD-6): makes a lost race on the active-job index real in an Api test host.
// Two things have to hold. (1) Both requests get past the endpoint's early check, which is only the
// friendly fast path: ListByJobTypeAsync for the guarded type waits until every caller has arrived and
// then answers "nothing active", so each caller goes on to enqueue. (2) The winner's job stays active
// while the loser enqueues: TryStartAsync is held, so the row remains Queued instead of finishing in a
// few milliseconds. Both are decorators over the real adapters; nothing else is replaced.
public sealed class ParallelEnqueueGate(string guardedJobType, int callers)
{
    private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _jobsMayStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public string GuardedJobType { get; } = guardedJobType;

    // Called by the blind early check; returns once every caller has reached it.
    public async Task ArriveAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _arrived) >= callers)
        {
            _allArrived.TrySetResult();
        }

        await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }

    // Lets a test act (for example upload the second file) once the first caller is parked at the check.
    public async Task WaitForArrivalsAsync(int count, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Volatile.Read(ref _arrived) < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Only {Volatile.Read(ref _arrived)} of {count} callers reached the early check.");
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    public Task WaitUntilJobsMayStartAsync(CancellationToken cancellationToken) => _jobsMayStart.Task.WaitAsync(cancellationToken);

    public void ReleaseJobs() => _jobsMayStart.TrySetResult();

    public void Install(IServiceCollection services)
    {
        services.AddScoped<IBackgroundJobRepository>(sp =>
            new BlindEarlyCheckRepository(ActivatorUtilities.CreateInstance<BackgroundJobRepository>(sp), this));
        services.AddScoped<IBackgroundJobLifecycle>(sp =>
            new HeldStartLifecycle(ActivatorUtilities.CreateInstance<BackgroundJobLifecycle>(sp), this));
    }

    private sealed class BlindEarlyCheckRepository(IBackgroundJobRepository inner, ParallelEnqueueGate gate) : IBackgroundJobRepository
    {
        public Task<BackgroundJob?> FindByIdAsync(Guid householdId, Guid jobId, CancellationToken cancellationToken) =>
            inner.FindByIdAsync(householdId, jobId, cancellationToken);

        public async Task<IReadOnlyList<BackgroundJob>> ListByJobTypeAsync(Guid householdId, string jobType, CancellationToken cancellationToken)
        {
            if (jobType != gate.GuardedJobType)
            {
                return await inner.ListByJobTypeAsync(householdId, jobType, cancellationToken);
            }

            await gate.ArriveAsync(cancellationToken);
            return [];
        }

        public Task<IReadOnlyList<HouseholdMember>> FindMembersByIdsAsync(Guid householdId, IReadOnlyList<Guid> memberIds, CancellationToken cancellationToken) =>
            inner.FindMembersByIdsAsync(householdId, memberIds, cancellationToken);

        public Task<int> DeleteTerminalByJobTypeAsync(Guid householdId, string jobType, DateTimeOffset? completedBeforeUtc, CancellationToken cancellationToken) =>
            inner.DeleteTerminalByJobTypeAsync(householdId, jobType, completedBeforeUtc, cancellationToken);
    }

    private sealed class HeldStartLifecycle(IBackgroundJobLifecycle inner, ParallelEnqueueGate gate) : IBackgroundJobLifecycle
    {
        public async Task<DateTimeOffset?> TryStartAsync(Guid jobId, CancellationToken cancellationToken)
        {
            await gate.WaitUntilJobsMayStartAsync(cancellationToken);
            return await inner.TryStartAsync(jobId, cancellationToken);
        }

        public Task<bool> TryHeartbeatAsync(Guid jobId, DateTimeOffset token, CancellationToken cancellationToken) =>
            inner.TryHeartbeatAsync(jobId, token, cancellationToken);

        public Task<bool> TryCompleteAsync(Guid jobId, DateTimeOffset token, CancellationToken cancellationToken) =>
            inner.TryCompleteAsync(jobId, token, cancellationToken);

        public Task<bool> TryFailAsync(Guid jobId, DateTimeOffset token, string? errorMessage, CancellationToken cancellationToken) =>
            inner.TryFailAsync(jobId, token, errorMessage, cancellationToken);

        public Task<bool> TryFailNonTerminalAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken) =>
            inner.TryFailNonTerminalAsync(jobId, errorMessage, cancellationToken);

        public Task<int> FailStaleAsync(Guid householdId, CancellationToken cancellationToken) =>
            inner.FailStaleAsync(householdId, cancellationToken);

        public Task<int> FailInterruptedBeforeAsync(Guid householdId, DateTimeOffset processStartedAtUtc, CancellationToken cancellationToken) =>
            inner.FailInterruptedBeforeAsync(householdId, processStartedAtUtc, cancellationToken);
    }
}
