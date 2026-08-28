using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

// Real HouseholdRecomputeLock + real StatusRecomputeService against a real (Testcontainers)
// database — the Acceptance Criteria explicitly require this against the real lock, not a mocked
// one. GetCurrentStatus's own ports (IHouseholdRepository/IMeterReadingRepository/
// IMeterRegressionPromptRepository/ISmartPlugCoverageSignal) stay mocked — this file's job is
// proving the lock genuinely serializes RecomputeAsync's read-then-write body, not re-testing
// GetCurrentStatus's own calculation logic (GetCurrentStatusTests already owns that).
public class StatusRecomputeServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private sealed class FixedHouseholdAccessor(Guid householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;

        public Guid? HouseholdMemberId => null;
    }

    private async Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));

        var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
        await dbContext.Database.MigrateAsync(cancellationToken);
        return dbContext;
    }

    private static async Task SeedHouseholdAsync(EnergyTrackerDbContext dbContext, Guid householdId, CancellationToken cancellationToken)
    {
        // Only needed to satisfy StatusSnapshot's FK to Household — the actual "read" side of
        // GetCurrentStatus (household config, readings, prompts) is mocked below, independent of
        // this row.
        dbContext.Households.Add(new Household
        {
            Id = householdId,
            Locale = "en-US",
            Currency = "USD",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static (IHouseholdRepository HouseholdRepository, IMeterReadingRepository ReadingRepository, IMeterRegressionPromptRepository RegressionPromptRepository, ISmartPlugCoverageSignal SmartPlugCoverageSignal)
        NewMockedPorts(Guid householdId, Guid mainMeterId)
    {
        var householdRepository = Substitute.For<IHouseholdRepository>();
        var readingRepository = Substitute.For<IMeterReadingRepository>();
        var regressionPromptRepository = Substitute.For<IMeterRegressionPromptRepository>();
        var smartPlugCoverageSignal = Substitute.For<ISmartPlugCoverageSignal>();

        regressionPromptRepository.GetOpenForHouseholdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((MeterRegressionPrompt?)null);
        regressionPromptRepository.GetResolvedForMainMeterAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((IReadOnlyList<MeterRegressionPrompt>)[]);
        readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(new MainMeter { Id = mainMeterId, HouseholdId = householdId, CreatedAtUtc = DateTimeOffset.UtcNow });
        readingRepository.GetRecentByMainMeterAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(
        [
            new MeterReading { Id = Guid.NewGuid(), HouseholdId = householdId, MainMeterId = mainMeterId, KwhValue = 1000m, ReadingTimestamp = DateTimeOffset.UtcNow.AddDays(-10), IdempotencyKey = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow },
            new MeterReading { Id = Guid.NewGuid(), HouseholdId = householdId, MainMeterId = mainMeterId, KwhValue = 1100m, ReadingTimestamp = DateTimeOffset.UtcNow, IdempotencyKey = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow },
        ]);

        return (householdRepository, readingRepository, regressionPromptRepository, smartPlugCoverageSignal);
    }

    [Fact]
    public async Task Two_concurrent_RecomputeAsync_calls_for_the_same_household_never_overlap_and_both_writes_land()
    {
        // Deterministic proof of serialization, mirroring HouseholdRecomputeLockTests: the second
        // call's household lookup can only ever be recorded as "second-entered" after the first
        // call's "first-exited" — that ordering is a hard guarantee of the real lock the body is
        // wrapped in (await using), not a timing assumption. If the wrap were ever removed or
        // broken, "second-entered" could race ahead of "first-exited".
        var householdId = Guid.NewGuid();
        var mainMeterId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);

        var (householdRepository, readingRepository, regressionPromptRepository, smartPlugCoverageSignal) = NewMockedPorts(householdId, mainMeterId);

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
        var invocationCount = 0;

        async Task<Household?> RecordAndReturnHouseholdAsync(NSubstitute.Core.CallInfo _)
        {
            var invocation = Interlocked.Increment(ref invocationCount);
            if (invocation == 1)
            {
                Record("first-entered");
                firstEntered.SetResult();
                await releaseFirst.Task;
                Record("first-exited");
            }
            else
            {
                Record("second-entered");
            }

            return new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow, YearlyBaselineKwh = 3650m };
        }

        householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(RecordAndReturnHouseholdAsync);

        var recomputeLock = new HouseholdRecomputeLock();
        var getCurrentStatus = new GetCurrentStatus(householdRepository, readingRepository, regressionPromptRepository, smartPlugCoverageSignal);
        var sut = new StatusRecomputeService(getCurrentStatus, dbContext, recomputeLock, NullLogger<StatusRecomputeService>.Instance);

        var firstTask = sut.RecomputeAsync(householdId, TestContext.Current.CancellationToken);
        await firstEntered.Task;

        var secondTask = sut.RecomputeAsync(householdId, TestContext.Current.CancellationToken);

        // Sanity-only: not the proof itself (see comment above) — gives a broken implementation a
        // real chance to let "second-entered" race ahead before we release the first call.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        releaseFirst.SetResult();
        await Task.WhenAll(firstTask, secondTask);

        events.ShouldBe(["first-entered", "first-exited", "second-entered"]);

        var snapshots = await dbContext.StatusSnapshots
            .Where(s => s.HouseholdId == householdId)
            .OrderBy(s => s.ComputedAtUtc)
            .ToListAsync(TestContext.Current.CancellationToken);
        snapshots.Count.ShouldBe(2);
        snapshots[0].Id.ShouldNotBe(snapshots[1].Id);
    }

    [Fact]
    public async Task RecomputeAsync_calls_for_two_different_households_never_block_each_other()
    {
        var householdA = Guid.NewGuid();
        var householdB = Guid.NewGuid();
        var mainMeterA = Guid.NewGuid();
        var mainMeterB = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdA, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdA, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdB, TestContext.Current.CancellationToken);

        var (householdRepositoryA, readingRepositoryA, regressionPromptRepositoryA, smartPlugCoverageSignalA) = NewMockedPorts(householdA, mainMeterA);
        var (householdRepositoryB, readingRepositoryB, regressionPromptRepositoryB, smartPlugCoverageSignalB) = NewMockedPorts(householdB, mainMeterB);

        var householdAEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHouseholdA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Household?> RecordAndReturnHouseholdAAsync(NSubstitute.Core.CallInfo _)
        {
            householdAEntered.SetResult();
            await releaseHouseholdA.Task;
            return new Household { Id = householdA, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow, YearlyBaselineKwh = 3650m };
        }

        householdRepositoryA.FindByIdAsync(householdA, Arg.Any<CancellationToken>()).Returns(RecordAndReturnHouseholdAAsync);
        householdRepositoryB.FindByIdAsync(householdB, Arg.Any<CancellationToken>())
            .Returns(new Household { Id = householdB, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow, YearlyBaselineKwh = 3650m });

        var recomputeLock = new HouseholdRecomputeLock();
        var sutA = new StatusRecomputeService(
            new GetCurrentStatus(householdRepositoryA, readingRepositoryA, regressionPromptRepositoryA, smartPlugCoverageSignalA),
            dbContext, recomputeLock, NullLogger<StatusRecomputeService>.Instance);
        var sutB = new StatusRecomputeService(
            new GetCurrentStatus(householdRepositoryB, readingRepositoryB, regressionPromptRepositoryB, smartPlugCoverageSignalB),
            dbContext, recomputeLock, NullLogger<StatusRecomputeService>.Instance);

        var taskA = sutA.RecomputeAsync(householdA, TestContext.Current.CancellationToken);
        await householdAEntered.Task;

        // Household A's recompute is deliberately blocked mid-flight and not yet released. If the
        // per-household lock wrongly serialized across households, Household B's RecomputeAsync
        // would hang until Household A's own lock timeout (~10s default) or releaseHouseholdA
        // fires — bounding this call makes that failure mode a clear, fast test failure.
        using var householdBDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sutB.RecomputeAsync(householdB, householdBDeadline.Token);

        releaseHouseholdA.SetResult();
        await taskA;

        var snapshotHouseholdIds = await dbContext.StatusSnapshots
            .IgnoreQueryFilters()
            .Where(s => s.HouseholdId == householdA || s.HouseholdId == householdB)
            .Select(s => s.HouseholdId)
            .ToListAsync(TestContext.Current.CancellationToken);
        snapshotHouseholdIds.ShouldContain(householdA);
        snapshotHouseholdIds.ShouldContain(householdB);
    }
}
