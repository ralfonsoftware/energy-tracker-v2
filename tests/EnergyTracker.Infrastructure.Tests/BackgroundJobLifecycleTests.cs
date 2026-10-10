using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

// Story 11.2 / AD-6 (amended 2026-10-10): every BackgroundJob status change is one conditional
// update. An NSubstitute-mocked lifecycle cannot prove that, so these run against real engines on
// both providers (AD-2) — the token round trip in particular is provider-sensitive (Postgres
// timestamptz keeps microseconds, SQL Server datetimeoffset(7) keeps ticks).
public abstract class BackgroundJobLifecycleTestsBase
{
    protected abstract Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken);

    protected sealed class FixedHouseholdAccessor(Guid householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;

        public Guid? HouseholdMemberId => null;
    }

    private static readonly JobLifecycleTimings Timings = JobLifecycleTimings.Default;

    private static BackgroundLifecycleFixture NewFixture(EnergyTrackerDbContext dbContext) => new(dbContext);

    private sealed class BackgroundLifecycleFixture(EnergyTrackerDbContext dbContext)
    {
        public BackgroundJobLifecycle Sut { get; } = new(dbContext, Timings);

        public EnergyTrackerDbContext DbContext { get; } = dbContext;
    }

    private static Household NewHousehold(Guid householdId) => new()
    {
        Id = householdId,
        Locale = "en-US",
        Currency = "USD",
        CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static async Task<BackgroundJob> SeedJobAsync(
        EnergyTrackerDbContext dbContext, Guid householdId, BackgroundJobStatus status,
        DateTimeOffset? createdAt = null, DateTimeOffset? startedAt = null, DateTimeOffset? heartbeatAt = null,
        string jobType = "UnknownJobType", CancellationToken cancellationToken = default)
    {
        var job = new BackgroundJob
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            JobType = jobType,
            Status = status,
            CreatedAtUtc = createdAt ?? DateTimeOffset.UtcNow,
            StartedAtUtc = startedAt,
            HeartbeatAtUtc = heartbeatAt,
            CompletedAtUtc = status is BackgroundJobStatus.Completed or BackgroundJobStatus.Failed ? DateTimeOffset.UtcNow : null,
        };
        dbContext.BackgroundJobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
        return job;
    }

    private static Task<BackgroundJob> ReloadAsync(EnergyTrackerDbContext dbContext, Guid jobId, CancellationToken cancellationToken) =>
        dbContext.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == jobId, cancellationToken);

    private async Task<(EnergyTrackerDbContext DbContext, Guid HouseholdId)> OpenWithHouseholdAsync(CancellationToken cancellationToken)
    {
        var householdId = Guid.NewGuid();
        var dbContext = await OpenMigratedDbContextAsync(householdId, cancellationToken);
        dbContext.Households.Add(NewHousehold(householdId));
        await dbContext.SaveChangesAsync(cancellationToken);
        return (dbContext, householdId);
    }

    // ---- TryStart ----

    [Fact]
    public async Task TryStart_moves_a_Queued_row_to_Processing_and_returns_the_ownership_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dbContext, householdId) = await OpenWithHouseholdAsync(ct);
        await using var disposableContext = dbContext;
        var job = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Queued, cancellationToken: ct);
        var sut = NewFixture(dbContext).Sut;

        var token = await sut.TryStartAsync(job.Id, ct);

        token.ShouldNotBeNull();
        var reloaded = await ReloadAsync(dbContext, job.Id, ct);
        reloaded.Status.ShouldBe(BackgroundJobStatus.Processing);
        reloaded.StartedAtUtc.ShouldBe(token);
        reloaded.HeartbeatAtUtc.ShouldBe(token);
    }

    [Theory]
    [InlineData(BackgroundJobStatus.Completed)]
    [InlineData(BackgroundJobStatus.Failed)]
    public async Task TryStart_returns_null_and_leaves_a_terminal_row_unchanged(BackgroundJobStatus terminal)
    {
        var ct = TestContext.Current.CancellationToken;
        var (dbContext, householdId) = await OpenWithHouseholdAsync(ct);
        await using var disposableContext = dbContext;
        var job = await SeedJobAsync(dbContext, householdId, terminal, cancellationToken: ct);
        var sut = NewFixture(dbContext).Sut;

        (await sut.TryStartAsync(job.Id, ct)).ShouldBeNull();

        var reloaded = await ReloadAsync(dbContext, job.Id, ct);
        reloaded.Status.ShouldBe(terminal);
        reloaded.StartedAtUtc.ShouldBeNull();
    }

    [Fact]
    public async Task TryStart_returns_null_for_a_missing_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dbContext, _) = await OpenWithHouseholdAsync(ct);
        await using var disposableContext = dbContext;

        (await NewFixture(dbContext).Sut.TryStartAsync(Guid.NewGuid(), ct)).ShouldBeNull();
    }

    [Fact]
    public async Task TryStart_returns_null_for_a_Processing_row_with_a_fresh_heartbeat_because_the_owner_is_live()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dbContext, householdId) = await OpenWithHouseholdAsync(ct);
        await using var disposableContext = dbContext;
        var started = DateTimeOffset.UtcNow.AddMinutes(-30);
        var heartbeat = DateTimeOffset.UtcNow.AddSeconds(-20);
        var job = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Processing,
            createdAt: started, startedAt: started, heartbeatAt: heartbeat, cancellationToken: ct);

        (await NewFixture(dbContext).Sut.TryStartAsync(job.Id, ct)).ShouldBeNull();

        var reloaded = await ReloadAsync(dbContext, job.Id, ct);
        reloaded.Status.ShouldBe(BackgroundJobStatus.Processing);
        reloaded.StartedAtUtc!.Value.ShouldBe(started, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task TryStart_restarts_a_Processing_row_with_a_stale_heartbeat_under_a_new_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dbContext, householdId) = await OpenWithHouseholdAsync(ct);
        await using var disposableContext = dbContext;
        var oldStart = DateTimeOffset.UtcNow.AddMinutes(-20);
        var job = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Processing,
            createdAt: oldStart, startedAt: oldStart, heartbeatAt: DateTimeOffset.UtcNow.AddMinutes(-6), cancellationToken: ct);

        var token = await NewFixture(dbContext).Sut.TryStartAsync(job.Id, ct);

        token.ShouldNotBeNull();
        token.Value.ShouldBeGreaterThan(oldStart);
        var reloaded = await ReloadAsync(dbContext, job.Id, ct);
        reloaded.Status.ShouldBe(BackgroundJobStatus.Processing);
        reloaded.StartedAtUtc.ShouldBe(token);
        reloaded.HeartbeatAtUtc.ShouldBe(token);
    }

    [Fact]
    public async Task TryStart_two_concurrent_calls_on_one_Queued_row_give_exactly_one_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dbContext, householdId) = await OpenWithHouseholdAsync(ct);
        await using var disposableContext = dbContext;
        var job = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Queued, cancellationToken: ct);
        // Two independent contexts: a shared DbContext is not safe for concurrent use.
        await using var second = await OpenMigratedDbContextAsync(householdId, ct);

        var results = await Task.WhenAll(
            new BackgroundJobLifecycle(dbContext, Timings).TryStartAsync(job.Id, ct),
            new BackgroundJobLifecycle(second, Timings).TryStartAsync(job.Id, ct));

        results.Count(t => t is not null).ShouldBe(1);
    }

    // ---- Token round trip (provider-sensitive) ----

    [Fact]
    public async Task The_token_returned_by_TryStart_is_accepted_by_heartbeat_and_complete_after_a_database_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dbContext, householdId) = await OpenWithHouseholdAsync(ct);
        await using var disposableContext = dbContext;
        var job = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Queued, cancellationToken: ct);
        var sut = NewFixture(dbContext).Sut;

        var token = (await sut.TryStartAsync(job.Id, ct)).ShouldNotBeNull();

        (await sut.TryHeartbeatAsync(job.Id, token, ct)).ShouldBeTrue();
        (await sut.TryCompleteAsync(job.Id, token, ct)).ShouldBeTrue();
        var reloaded = await ReloadAsync(dbContext, job.Id, ct);
        reloaded.Status.ShouldBe(BackgroundJobStatus.Completed);
        reloaded.CompletedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_token_returned_by_TryStart_is_accepted_by_fail_after_a_database_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dbContext, householdId) = await OpenWithHouseholdAsync(ct);
        await using var disposableContext = dbContext;
        var job = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Queued, cancellationToken: ct);
        var sut = NewFixture(dbContext).Sut;

        var token = (await sut.TryStartAsync(job.Id, ct)).ShouldNotBeNull();

        (await sut.TryFailAsync(job.Id, token, "some message", ct)).ShouldBeTrue();
        var reloaded = await ReloadAsync(dbContext, job.Id, ct);
        reloaded.Status.ShouldBe(BackgroundJobStatus.Failed);
        reloaded.ErrorMessage.ShouldBe("some message");
        reloaded.CompletedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_wrong_token_is_rejected_by_heartbeat_complete_and_fail_and_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dbContext, householdId) = await OpenWithHouseholdAsync(ct);
        await using var disposableContext = dbContext;
        var job = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Queued, cancellationToken: ct);
        var sut = NewFixture(dbContext).Sut;
        var token = (await sut.TryStartAsync(job.Id, ct)).ShouldNotBeNull();
        var wrong = token.AddMilliseconds(-5);

        (await sut.TryHeartbeatAsync(job.Id, wrong, ct)).ShouldBeFalse();
        (await sut.TryCompleteAsync(job.Id, wrong, ct)).ShouldBeFalse();
        (await sut.TryFailAsync(job.Id, wrong, "x", ct)).ShouldBeFalse();

        var reloaded = await ReloadAsync(dbContext, job.Id, ct);
        reloaded.Status.ShouldBe(BackgroundJobStatus.Processing);
        reloaded.ErrorMessage.ShouldBeNull();
        reloaded.CompletedAtUtc.ShouldBeNull();
        reloaded.HeartbeatAtUtc.ShouldBe(token);
    }

    [Fact]
    public async Task TryHeartbeat_advances_HeartbeatAtUtc_for_the_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dbContext, householdId) = await OpenWithHouseholdAsync(ct);
        await using var disposableContext = dbContext;
        var started = DateTimeOffset.UtcNow.AddMinutes(-3);
        var token = new DateTimeOffset(started.Ticks - started.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var job = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Processing,
            createdAt: token, startedAt: token, heartbeatAt: token, cancellationToken: ct);

        (await NewFixture(dbContext).Sut.TryHeartbeatAsync(job.Id, token, ct)).ShouldBeTrue();

        var reloaded = await ReloadAsync(dbContext, job.Id, ct);
        reloaded.HeartbeatAtUtc.ShouldNotBeNull();
        reloaded.HeartbeatAtUtc!.Value.ShouldBeGreaterThan(token.AddMinutes(2));
    }

    // ---- TryComplete / TryFail never overwrite a terminal status ----

    [Fact]
    public async Task TryFail_does_not_overwrite_a_Completed_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dbContext, householdId) = await OpenWithHouseholdAsync(ct);
        await using var disposableContext = dbContext;
        var token = DateTimeOffset.UtcNow.AddMinutes(-1);
        token = new DateTimeOffset(token.Ticks - token.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var job = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Completed, startedAt: token, cancellationToken: ct);

        (await NewFixture(dbContext).Sut.TryFailAsync(job.Id, token, "late", ct)).ShouldBeFalse();

        var reloaded = await ReloadAsync(dbContext, job.Id, ct);
        reloaded.Status.ShouldBe(BackgroundJobStatus.Completed);
        reloaded.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public async Task TryComplete_does_not_overwrite_a_Failed_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dbContext, householdId) = await OpenWithHouseholdAsync(ct);
        await using var disposableContext = dbContext;
        var token = DateTimeOffset.UtcNow.AddMinutes(-1);
        token = new DateTimeOffset(token.Ticks - token.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var job = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Failed, startedAt: token, cancellationToken: ct);

        (await NewFixture(dbContext).Sut.TryCompleteAsync(job.Id, token, ct)).ShouldBeFalse();

        (await ReloadAsync(dbContext, job.Id, ct)).Status.ShouldBe(BackgroundJobStatus.Failed);
    }

    // ---- FailStale ----

    [Fact]
    public async Task FailStale_fails_only_Processing_rows_whose_heartbeat_is_older_than_five_minutes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dbContext, householdId) = await OpenWithHouseholdAsync(ct);
        await using var disposableContext = dbContext;
        var now = DateTimeOffset.UtcNow;
        var stale = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Processing,
            createdAt: now.AddMinutes(-30), startedAt: now.AddMinutes(-30), heartbeatAt: now.AddMinutes(-6), cancellationToken: ct);
        // Headline "long but live job": created hours ago, still heartbeating.
        var longButLive = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Processing,
            createdAt: now.AddHours(-3), startedAt: now.AddHours(-3), heartbeatAt: now.AddSeconds(-30), cancellationToken: ct);
        var queuedForHours = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Queued, createdAt: now.AddHours(-5), cancellationToken: ct);
        var completed = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Completed, createdAt: now.AddHours(-5), cancellationToken: ct);
        // Written by the previous image: no StartedAtUtc / HeartbeatAtUtc -> falls back to CreatedAtUtc.
        var legacyOrphan = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Processing, createdAt: now.AddMinutes(-10), cancellationToken: ct);
        var legacyFresh = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Processing, createdAt: now.AddMinutes(-1), cancellationToken: ct);

        var count = await NewFixture(dbContext).Sut.FailStaleAsync(householdId, ct);

        count.ShouldBe(2);
        var staleAfter = await ReloadAsync(dbContext, stale.Id, ct);
        staleAfter.Status.ShouldBe(BackgroundJobStatus.Failed);
        staleAfter.ErrorMessage.ShouldBe("job-interrupted");
        staleAfter.CompletedAtUtc.ShouldNotBeNull();
        (await ReloadAsync(dbContext, legacyOrphan.Id, ct)).Status.ShouldBe(BackgroundJobStatus.Failed);
        (await ReloadAsync(dbContext, longButLive.Id, ct)).Status.ShouldBe(BackgroundJobStatus.Processing);
        (await ReloadAsync(dbContext, queuedForHours.Id, ct)).Status.ShouldBe(BackgroundJobStatus.Queued);
        (await ReloadAsync(dbContext, completed.Id, ct)).Status.ShouldBe(BackgroundJobStatus.Completed);
        (await ReloadAsync(dbContext, legacyFresh.Id, ct)).Status.ShouldBe(BackgroundJobStatus.Processing);
    }

    [Fact]
    public async Task FailStale_never_touches_another_Households_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dbContext, householdA) = await OpenWithHouseholdAsync(ct);
        await using var disposableContext = dbContext;
        var householdB = Guid.NewGuid();
        dbContext.Households.Add(NewHousehold(householdB));
        await dbContext.SaveChangesAsync(ct);
        var now = DateTimeOffset.UtcNow;

        // dbContext is scoped to Household A by its accessor; Household B's row is written through a context for B.
        await using var contextB = await OpenMigratedDbContextAsync(householdB, ct);
        var staleB = await SeedJobAsync(contextB, householdB, BackgroundJobStatus.Processing,
            createdAt: now.AddHours(-1), startedAt: now.AddHours(-1), heartbeatAt: now.AddMinutes(-30), cancellationToken: ct);
        var staleA = await SeedJobAsync(dbContext, householdA, BackgroundJobStatus.Processing,
            createdAt: now.AddHours(-1), startedAt: now.AddHours(-1), heartbeatAt: now.AddMinutes(-30), cancellationToken: ct);
        var sut = NewFixture(dbContext).Sut;

        // Even when asked about B, the AD-3 query filter keeps Household A's context from reaching B's rows.
        (await sut.FailStaleAsync(householdB, ct)).ShouldBe(0);
        (await ReloadAsync(contextB, staleB.Id, ct)).Status.ShouldBe(BackgroundJobStatus.Processing);

        (await sut.FailStaleAsync(householdA, ct)).ShouldBe(1);
        (await ReloadAsync(dbContext, staleA.Id, ct)).Status.ShouldBe(BackgroundJobStatus.Failed);
        (await ReloadAsync(contextB, staleB.Id, ct)).Status.ShouldBe(BackgroundJobStatus.Processing);
    }

    // ---- FailInterruptedBefore ----

    [Fact]
    public async Task FailInterruptedBefore_fails_Queued_and_Processing_rows_created_before_the_cutoff_whatever_their_heartbeat()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dbContext, householdId) = await OpenWithHouseholdAsync(ct);
        await using var disposableContext = dbContext;
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-2);
        var oldQueued = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Queued, createdAt: now.AddMinutes(-10), cancellationToken: ct);
        var oldProcessingFresh = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Processing,
            createdAt: now.AddMinutes(-10), startedAt: now.AddMinutes(-10), heartbeatAt: now.AddSeconds(-5), cancellationToken: ct);
        var newQueued = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Queued, createdAt: now.AddMinutes(-1), cancellationToken: ct);
        var newProcessing = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Processing, createdAt: now, cancellationToken: ct);
        var oldCompleted = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Completed, createdAt: now.AddMinutes(-10), cancellationToken: ct);
        var oldFailed = await SeedJobAsync(dbContext, householdId, BackgroundJobStatus.Failed, createdAt: now.AddMinutes(-10), cancellationToken: ct);

        var count = await NewFixture(dbContext).Sut.FailInterruptedBeforeAsync(householdId, cutoff, ct);

        count.ShouldBe(2);
        foreach (var swept in new[] { oldQueued, oldProcessingFresh })
        {
            var reloaded = await ReloadAsync(dbContext, swept.Id, ct);
            reloaded.Status.ShouldBe(BackgroundJobStatus.Failed);
            reloaded.ErrorMessage.ShouldBe("job-interrupted");
            reloaded.CompletedAtUtc.ShouldNotBeNull();
        }

        (await ReloadAsync(dbContext, newQueued.Id, ct)).Status.ShouldBe(BackgroundJobStatus.Queued);
        (await ReloadAsync(dbContext, newProcessing.Id, ct)).Status.ShouldBe(BackgroundJobStatus.Processing);
        (await ReloadAsync(dbContext, oldCompleted.Id, ct)).Status.ShouldBe(BackgroundJobStatus.Completed);
        (await ReloadAsync(dbContext, oldFailed.Id, ct)).ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public async Task FailInterruptedBefore_never_touches_another_Households_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dbContext, householdA) = await OpenWithHouseholdAsync(ct);
        await using var disposableContext = dbContext;
        var householdB = Guid.NewGuid();
        dbContext.Households.Add(NewHousehold(householdB));
        await dbContext.SaveChangesAsync(ct);
        var now = DateTimeOffset.UtcNow;
        await using var contextB = await OpenMigratedDbContextAsync(householdB, ct);
        var queuedB = await SeedJobAsync(contextB, householdB, BackgroundJobStatus.Queued, createdAt: now.AddHours(-1), cancellationToken: ct);
        var queuedA = await SeedJobAsync(dbContext, householdA, BackgroundJobStatus.Queued, createdAt: now.AddHours(-1), cancellationToken: ct);
        var sut = NewFixture(dbContext).Sut;

        (await sut.FailInterruptedBeforeAsync(householdB, now, ct)).ShouldBe(0);
        (await ReloadAsync(contextB, queuedB.Id, ct)).Status.ShouldBe(BackgroundJobStatus.Queued);

        (await sut.FailInterruptedBeforeAsync(householdA, now, ct)).ShouldBe(1);
        (await ReloadAsync(dbContext, queuedA.Id, ct)).Status.ShouldBe(BackgroundJobStatus.Failed);
        (await ReloadAsync(contextB, queuedB.Id, ct)).Status.ShouldBe(BackgroundJobStatus.Queued);
    }
}

public class PostgresBackgroundJobLifecycleTests : BackgroundJobLifecycleTestsBase, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    protected override async Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));
        var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
        await dbContext.Database.MigrateAsync(cancellationToken);
        return dbContext;
    }
}

public class SqlServerBackgroundJobLifecycleTests : BackgroundJobLifecycleTestsBase, IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    protected override async Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer"));
        var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
        await dbContext.Database.MigrateAsync(cancellationToken);
        return dbContext;
    }
}
