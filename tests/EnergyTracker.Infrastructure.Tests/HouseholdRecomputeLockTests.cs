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

    [Fact]
    public async Task Idle_entries_are_evicted_after_many_distinct_households_acquire_and_release()
    {
        var sut = new HouseholdRecomputeLock();

        for (var i = 0; i < 1000; i++)
        {
            await using var handle = await sut.AcquireAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        }

        sut.TrackedHouseholdCount.ShouldBe(0);
    }

    [Fact]
    public async Task The_entry_exists_while_held_and_is_gone_after_dispose()
    {
        var sut = new HouseholdRecomputeLock();
        var handle = await sut.AcquireAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        sut.TrackedHouseholdCount.ShouldBe(1);

        await handle.DisposeAsync();

        sut.TrackedHouseholdCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_waiting_caller_keeps_the_entry_alive_across_the_holders_release()
    {
        var sut = new HouseholdRecomputeLock();
        var householdId = Guid.NewGuid();
        var order = new List<string>();

        var holder = await sut.AcquireAsync(householdId, TestContext.Current.CancellationToken);
        var waiterTask = Task.Run(async () =>
        {
            await using var handle = await sut.AcquireAsync(householdId, TestContext.Current.CancellationToken);
            lock (order)
            {
                order.Add("waiter-acquired");
            }

            sut.TrackedHouseholdCount.ShouldBe(1);
        }, TestContext.Current.CancellationToken);

        // Sanity-only: lets the waiter reach WaitAsync; the order assertion below does not depend on it.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        lock (order)
        {
            order.Add("holder-released");
        }

        await holder.DisposeAsync();
        await waiterTask;

        order.ShouldBe(["holder-released", "waiter-acquired"]);
        sut.TrackedHouseholdCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_timed_out_waiter_returns_its_reference()
    {
        var sut = new HouseholdRecomputeLock(TimeSpan.FromMilliseconds(50));
        var householdId = Guid.NewGuid();
        var holder = await sut.AcquireAsync(householdId, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<TimeoutException>(
            () => sut.AcquireAsync(householdId, TestContext.Current.CancellationToken));

        sut.TrackedHouseholdCount.ShouldBe(1);

        await holder.DisposeAsync();

        sut.TrackedHouseholdCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_caller_cancelled_before_the_wait_returns_its_reference()
    {
        var sut = new HouseholdRecomputeLock();
        var householdId = Guid.NewGuid();
        var holder = await sut.AcquireAsync(householdId, TestContext.Current.CancellationToken);

        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => sut.AcquireAsync(householdId, callerCts.Token));

        sut.TrackedHouseholdCount.ShouldBe(1);

        await holder.DisposeAsync();

        sut.TrackedHouseholdCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_caller_cancelled_mid_wait_returns_its_reference()
    {
        var sut = new HouseholdRecomputeLock();
        var householdId = Guid.NewGuid();
        var holder = await sut.AcquireAsync(householdId, TestContext.Current.CancellationToken);

        using var callerCts = new CancellationTokenSource();
        var waiter = sut.AcquireAsync(householdId, callerCts.Token);
        waiter.IsCompleted.ShouldBeFalse();

        await callerCts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => waiter);
        sut.TrackedHouseholdCount.ShouldBe(1);

        await holder.DisposeAsync();

        sut.TrackedHouseholdCount.ShouldBe(0);
    }

    [Fact]
    public async Task Disposing_a_handle_twice_neither_releases_twice_nor_returns_two_references()
    {
        var sut = new HouseholdRecomputeLock(TimeSpan.FromMilliseconds(100));
        var householdId = Guid.NewGuid();

        var first = await sut.AcquireAsync(householdId, TestContext.Current.CancellationToken);
        await first.DisposeAsync();
        var second = await sut.AcquireAsync(householdId, TestContext.Current.CancellationToken);

        await first.DisposeAsync();

        sut.TrackedHouseholdCount.ShouldBe(1);
        // The second handle still holds the lock: a third caller must not get in.
        await Should.ThrowAsync<TimeoutException>(
            () => sut.AcquireAsync(householdId, TestContext.Current.CancellationToken));

        await second.DisposeAsync();

        sut.TrackedHouseholdCount.ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_churn_on_few_households_never_lets_two_holders_in_and_ends_empty()
    {
        var sut = new HouseholdRecomputeLock(TimeSpan.FromSeconds(60));
        var householdIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var holders = new int[householdIds.Length];
        var maxHolders = 0;

        var tasks = Enumerable.Range(0, 64).Select(worker => Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                var index = (worker + i) % householdIds.Length;
                await using var handle = await sut.AcquireAsync(householdIds[index], TestContext.Current.CancellationToken);
                var now = Interlocked.Increment(ref holders[index]);
                InterlockedMax(ref maxHolders, now);
                await Task.Yield();
                Interlocked.Decrement(ref holders[index]);
            }
        }, TestContext.Current.CancellationToken)).ToArray();

        await Task.WhenAll(tasks);

        maxHolders.ShouldBe(1);
        sut.TrackedHouseholdCount.ShouldBe(0);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value
               && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
