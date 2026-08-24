using EnergyTracker.Infrastructure.Adapters;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

public class HouseholdRecomputeLockTests
{
    [Fact]
    public async Task Two_acquisitions_for_the_same_household_never_overlap()
    {
        // Deterministic proof, not a timing guess: SemaphoreSlim.WaitAsync literally cannot
        // complete until the first holder's Release() runs, so — regardless of scheduling — the
        // second acquisition's event can only ever be recorded after the first holder's own
        // "released" event. The order assertion below holds unconditionally when the lock
        // actually serializes; it does NOT depend on the sanity-only delay in the middle.
        var sut = new HouseholdRecomputeLock();
        var householdId = Guid.NewGuid();
        var events = new List<string>();
        var eventsGate = new object();
        void Record(string e)
        {
            lock (eventsGate)
            {
                events.Add(e);
            }
        }

        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstTask = Task.Run(async () =>
        {
            await using var handle = await sut.AcquireAsync(householdId, TestContext.Current.CancellationToken);
            Record("first-acquired");
            firstEntered.SetResult();
            await releaseFirst.Task;
            Record("first-released");
        }, TestContext.Current.CancellationToken);

        await firstEntered.Task;

        var secondTask = Task.Run(async () =>
        {
            await using var handle = await sut.AcquireAsync(householdId, TestContext.Current.CancellationToken);
            Record("second-acquired");
        }, TestContext.Current.CancellationToken);

        // Sanity-only: gives a broken (non-serializing) implementation a real chance to let
        // "second-acquired" happen before we release the first holder, so a regression fails
        // reliably rather than by a lucky scheduling race. The actual proof is the exact `events`
        // order asserted below, which holds regardless of this delay's length.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        releaseFirst.SetResult();
        await Task.WhenAll(firstTask, secondTask);

        events.ShouldBe(["first-acquired", "first-released", "second-acquired"]);
    }

    [Fact]
    public async Task Acquisitions_for_different_households_never_block_each_other()
    {
        var sut = new HouseholdRecomputeLock();
        var householdA = Guid.NewGuid();
        var householdB = Guid.NewGuid();

        var householdAEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHouseholdA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var householdATask = Task.Run(async () =>
        {
            await using var handle = await sut.AcquireAsync(householdA, TestContext.Current.CancellationToken);
            householdAEntered.SetResult();
            await releaseHouseholdA.Task;
        }, TestContext.Current.CancellationToken);

        await householdAEntered.Task;

        // Household A's lock is held and deliberately NOT released yet. If HouseholdRecomputeLock
        // wrongly shared one semaphore across households, this would hang until releaseHouseholdA
        // completes; bounding it with a short cancellation deadline turns that hang into a clear
        // test failure instead of a stuck test run.
        using var householdBDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var householdBHandle = await sut.AcquireAsync(householdB, householdBDeadline.Token);

        releaseHouseholdA.SetResult();
        await householdATask;
    }

    [Fact]
    public async Task Acquire_throws_TimeoutException_when_the_configured_wait_is_exceeded()
    {
        var sut = new HouseholdRecomputeLock(TimeSpan.FromMilliseconds(50));
        var householdId = Guid.NewGuid();

        // Hold the lock open (never released within this test) so the second acquisition has no
        // way to succeed within the short configured timeout above.
        await sut.AcquireAsync(householdId, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<TimeoutException>(
            () => sut.AcquireAsync(householdId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Acquire_propagates_the_callers_own_cancellation_instead_of_a_TimeoutException()
    {
        // A caller-driven cancellation (e.g. the request itself was aborted) must surface as
        // OperationCanceledException, not be mistaken for the lock's own acquisition timeout.
        var sut = new HouseholdRecomputeLock(TimeSpan.FromSeconds(10));
        var householdId = Guid.NewGuid();
        await sut.AcquireAsync(householdId, TestContext.Current.CancellationToken);

        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => sut.AcquireAsync(householdId, callerCts.Token));
    }

    [Fact]
    public async Task Disposing_the_acquired_handle_releases_the_lock_for_the_next_acquisition()
    {
        var sut = new HouseholdRecomputeLock();
        var householdId = Guid.NewGuid();

        var handle = await sut.AcquireAsync(householdId, TestContext.Current.CancellationToken);
        await handle.DisposeAsync();

        // Must complete promptly — the household-scoped semaphore was released, not left held.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var secondHandle = await sut.AcquireAsync(householdId, deadline.Token);
    }
}
