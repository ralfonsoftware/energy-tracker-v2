using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

// Story 11.14 (audit C10, AD-6): at most one Queued/Processing RestoreHouseholdData and one
// CleanUpSmartPlugImportJobs row per Household, enforced by a filtered unique index that exists only in
// the provider migrations. The databases here are migrated with the real migrations (EnsureCreated
// would not create the raw index), and the guard is exercised through the real
// BackgroundJobEnqueueRecorder, the one place a Queued row is inserted.
public abstract class ActiveJobGuardTestsBase
{
    protected const string IndexName = "UX_BackgroundJobs_ActiveExclusive";

    protected abstract DbContextOptions<EnergyTrackerDbContext> Options { get; }

    // 1 when the index exists, is unique and is filtered.
    protected abstract Task<int> CountUniqueFilteredIndexAsync(EnergyTrackerDbContext dbContext, CancellationToken cancellationToken);

    // The Household the ambient scope resolves, exactly what the HTTP request's accessor would say.
    private sealed class AmbientHousehold : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; set; }

        public Guid? HouseholdMemberId => null;
    }

    private bool _migrated;

    private EnergyTrackerDbContext NewDbContext(Guid? householdId = null) =>
        new(Options, new FixedAccessor(householdId));

    private sealed class FixedAccessor(Guid? householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;

        public Guid? HouseholdMemberId => null;
    }

    // ambientHouseholdId is what the recorder's fresh scope resolves through its accessor; it defaults to the
    // Household being enqueued for, as in a real request.
    private async Task<(BackgroundJobEnqueueRecorder Recorder, Guid HouseholdId)> NewRecorderAsync(
        CancellationToken ct, Guid? householdId = null, Guid? ambientHouseholdId = null, Action<int>? onCreateScope = null)
    {
        var id = householdId ?? Guid.NewGuid();
        await using (var migrate = NewDbContext())
        {
            if (!_migrated)
            {
                await migrate.Database.MigrateAsync(ct);
                _migrated = true;
            }

            if (!await migrate.Households.IgnoreQueryFilters().AnyAsync(h => h.Id == id, ct))
            {
                migrate.Households.Add(new Household { Id = id, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
                await migrate.SaveChangesAsync(ct);
            }
        }

        var services = new ServiceCollection();
        services.AddSingleton<ICurrentHouseholdAccessor>(new AmbientHousehold { HouseholdId = ambientHouseholdId ?? id });
        services.AddSingleton(Options);
        services.AddScoped<EnergyTrackerDbContext>();
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return (new BackgroundJobEnqueueRecorder(onCreateScope is null ? scopeFactory : new HookedScopeFactory(scopeFactory, onCreateScope), TimeProvider.System), id);
    }

    // Calls the hook with the 1-based number of the scope being created, before the scope is handed out:
    // the recorder opens one scope per insert attempt and one per probe, in that order.
    private sealed class HookedScopeFactory(IServiceScopeFactory inner, Action<int> onCreateScope) : IServiceScopeFactory
    {
        private int _count;

        public IServiceScope CreateScope()
        {
            onCreateScope(++_count);
            return inner.CreateScope();
        }
    }

    private static JobEnvelope<string> Envelope(Guid householdId, string jobType, Guid? jobId = null) =>
        new(jobId ?? Guid.NewGuid(), householdId, jobType, "payload");

    private async Task SetStatusAsync(Guid householdId, Guid jobId, BackgroundJobStatus status, CancellationToken ct)
    {
        await using var dbContext = NewDbContext(householdId);
        await dbContext.BackgroundJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, status)
            .SetProperty(j => j.CompletedAtUtc, status is BackgroundJobStatus.Completed or BackgroundJobStatus.Failed ? DateTimeOffset.UtcNow : null), ct);
    }

    private async Task<List<BackgroundJob>> ActiveRowsAsync(Guid householdId, string jobType, CancellationToken ct)
    {
        await using var dbContext = NewDbContext(householdId);
        return await dbContext.BackgroundJobs.AsNoTracking()
            .Where(j => j.HouseholdId == householdId && j.JobType == jobType
                && (j.Status == BackgroundJobStatus.Queued || j.Status == BackgroundJobStatus.Processing))
            .ToListAsync(ct);
    }

    [Fact]
    public async Task The_index_exists_after_migrating_and_is_unique_and_filtered()
    {
        var ct = TestContext.Current.CancellationToken;
        await NewRecorderAsync(ct);
        await using var dbContext = NewDbContext();

        (await CountUniqueFilteredIndexAsync(dbContext, ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData(JobTypes.RestoreHouseholdData)]
    [InlineData(JobTypes.CleanUpSmartPlugImportJobs)]
    public async Task A_second_active_row_of_a_guarded_type_is_rejected_carrying_the_first_jobs_id(string jobType)
    {
        var ct = TestContext.Current.CancellationToken;
        var (recorder, householdId) = await NewRecorderAsync(ct);
        var first = Envelope(householdId, jobType);
        await recorder.RecordAsync(first, ct);

        var thrown = await Should.ThrowAsync<ActiveJobAlreadyExistsException>(() => recorder.RecordAsync(Envelope(householdId, jobType), ct));

        thrown.JobType.ShouldBe(jobType);
        thrown.ActiveJobId.ShouldBe(first.JobId);
        (await ActiveRowsAsync(householdId, jobType, ct)).ShouldHaveSingleItem().Id.ShouldBe(first.JobId);
    }

    [Fact]
    public async Task A_Processing_row_still_holds_the_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var (recorder, householdId) = await NewRecorderAsync(ct);
        var first = Envelope(householdId, JobTypes.RestoreHouseholdData);
        await recorder.RecordAsync(first, ct);
        await SetStatusAsync(householdId, first.JobId, BackgroundJobStatus.Processing, ct);

        var thrown = await Should.ThrowAsync<ActiveJobAlreadyExistsException>(
            () => recorder.RecordAsync(Envelope(householdId, JobTypes.RestoreHouseholdData), ct));

        thrown.ActiveJobId.ShouldBe(first.JobId);
    }

    [Fact]
    public async Task A_different_Household_is_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (recorder, first) = await NewRecorderAsync(ct);
        await recorder.RecordAsync(Envelope(first, JobTypes.RestoreHouseholdData), ct);
        var (otherRecorder, second) = await NewRecorderAsync(ct);

        await otherRecorder.RecordAsync(Envelope(second, JobTypes.RestoreHouseholdData), ct);

        (await ActiveRowsAsync(first, JobTypes.RestoreHouseholdData, ct)).Count.ShouldBe(1);
        (await ActiveRowsAsync(second, JobTypes.RestoreHouseholdData, ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_restore_and_a_cleanup_may_be_active_together_for_one_Household()
    {
        var ct = TestContext.Current.CancellationToken;
        var (recorder, householdId) = await NewRecorderAsync(ct);

        await recorder.RecordAsync(Envelope(householdId, JobTypes.RestoreHouseholdData), ct);
        await recorder.RecordAsync(Envelope(householdId, JobTypes.CleanUpSmartPlugImportJobs), ct);

        (await ActiveRowsAsync(householdId, JobTypes.RestoreHouseholdData, ct)).Count.ShouldBe(1);
        (await ActiveRowsAsync(householdId, JobTypes.CleanUpSmartPlugImportJobs, ct)).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(JobTypes.ProcessSmartPlugImport)]
    [InlineData(JobTypes.CorrelateEvent)]
    public async Task An_unguarded_job_type_may_have_several_active_rows_per_Household(string jobType)
    {
        var ct = TestContext.Current.CancellationToken;
        var (recorder, householdId) = await NewRecorderAsync(ct);

        await recorder.RecordAsync(Envelope(householdId, jobType), ct);
        await recorder.RecordAsync(Envelope(householdId, jobType), ct);
        await recorder.RecordAsync(Envelope(householdId, jobType), ct);

        (await ActiveRowsAsync(householdId, jobType, ct)).Count.ShouldBe(3);
    }

    [Theory]
    [InlineData(BackgroundJobStatus.Completed)]
    [InlineData(BackgroundJobStatus.Failed)]
    public async Task The_same_type_is_accepted_again_once_the_first_job_is_terminal(BackgroundJobStatus terminal)
    {
        var ct = TestContext.Current.CancellationToken;
        var (recorder, householdId) = await NewRecorderAsync(ct);
        var first = Envelope(householdId, JobTypes.RestoreHouseholdData);
        await recorder.RecordAsync(first, ct);
        await SetStatusAsync(householdId, first.JobId, terminal, ct);

        await recorder.RecordAsync(Envelope(householdId, JobTypes.RestoreHouseholdData), ct);

        (await ActiveRowsAsync(householdId, JobTypes.RestoreHouseholdData, ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task TryFailNonTerminalAsync_frees_the_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var (recorder, householdId) = await NewRecorderAsync(ct);
        var first = Envelope(householdId, JobTypes.CleanUpSmartPlugImportJobs);
        await recorder.RecordAsync(first, ct);
        await using (var dbContext = NewDbContext(householdId))
        {
            (await new BackgroundJobLifecycle(dbContext, JobLifecycleTimings.Default, TimeProvider.System).TryFailNonTerminalAsync(first.JobId, JobFailureCodes.Interrupted, ct)).ShouldBeTrue();
        }

        await recorder.RecordAsync(Envelope(householdId, JobTypes.CleanUpSmartPlugImportJobs), ct);
    }

    [Fact]
    public async Task FailStaleAsync_frees_the_slot_of_a_silent_Processing_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var (recorder, householdId) = await NewRecorderAsync(ct);
        var first = Envelope(householdId, JobTypes.RestoreHouseholdData);
        await recorder.RecordAsync(first, ct);
        await using (var dbContext = NewDbContext(householdId))
        {
            var longAgo = DateTimeOffset.UtcNow.AddHours(-1);
            await dbContext.BackgroundJobs.Where(j => j.Id == first.JobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, BackgroundJobStatus.Processing)
                .SetProperty(j => j.StartedAtUtc, longAgo)
                .SetProperty(j => j.HeartbeatAtUtc, longAgo), ct);
            (await new BackgroundJobLifecycle(dbContext, JobLifecycleTimings.Default, TimeProvider.System).FailStaleAsync(householdId, ct)).ShouldBe(1);
        }

        await recorder.RecordAsync(Envelope(householdId, JobTypes.RestoreHouseholdData), ct);
    }

    [Fact]
    public async Task The_startup_sweep_statement_frees_the_slot_of_an_interrupted_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var (recorder, householdId) = await NewRecorderAsync(ct);
        await recorder.RecordAsync(Envelope(householdId, JobTypes.RestoreHouseholdData), ct);
        await using (var dbContext = NewDbContext(householdId))
        {
            (await new BackgroundJobLifecycle(dbContext, JobLifecycleTimings.Default, TimeProvider.System)
                .FailInterruptedBeforeAsync(householdId, DateTimeOffset.UtcNow.AddMinutes(1), ct)).ShouldBe(1);
        }

        await recorder.RecordAsync(Envelope(householdId, JobTypes.RestoreHouseholdData), ct);
    }

    [Fact]
    public async Task The_database_itself_rejects_a_second_active_row_inserted_around_the_recorder()
    {
        var ct = TestContext.Current.CancellationToken;
        var (recorder, householdId) = await NewRecorderAsync(ct);
        await recorder.RecordAsync(Envelope(householdId, JobTypes.RestoreHouseholdData), ct);
        await using var dbContext = NewDbContext(householdId);
        dbContext.BackgroundJobs.Add(new BackgroundJob
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, JobType = JobTypes.RestoreHouseholdData,
            Status = BackgroundJobStatus.Queued, CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        await Should.ThrowAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(ct));
    }

    // Only a lost race becomes ActiveJobAlreadyExistsException. Any other database failure must reach the
    // caller unchanged: here the Id of a finished job is reused, a primary-key violation with no active
    // row behind it.
    [Fact]
    public async Task A_database_error_with_no_active_row_behind_it_propagates_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var (recorder, householdId) = await NewRecorderAsync(ct);
        var first = Envelope(householdId, JobTypes.RestoreHouseholdData);
        await recorder.RecordAsync(first, ct);
        await SetStatusAsync(householdId, first.JobId, BackgroundJobStatus.Completed, ct);

        await Should.ThrowAsync<DbUpdateException>(
            () => recorder.RecordAsync(Envelope(householdId, JobTypes.RestoreHouseholdData, first.JobId), ct));
    }

    // Code review 2026-10-25: a cleanup can finish in milliseconds, so the winner may be terminal by the time
    // the probe runs. Scope 1 is the rejected insert, scope 2 the probe (the winner completes just before
    // it), scope 3 the one retry, which now succeeds instead of surfacing the raw DbUpdateException.
    [Fact]
    public async Task A_slot_freed_between_the_rejected_insert_and_the_probe_is_taken_by_one_retry()
    {
        var ct = TestContext.Current.CancellationToken;
        var (seed, householdId) = await NewRecorderAsync(ct);
        var first = Envelope(householdId, JobTypes.CleanUpSmartPlugImportJobs);
        await seed.RecordAsync(first, ct);
        var (recorder, _) = await NewRecorderAsync(ct, householdId, onCreateScope: number =>
        {
            if (number == 2)
            {
                SetStatusAsync(householdId, first.JobId, BackgroundJobStatus.Completed, ct).GetAwaiter().GetResult();
            }
        });
        var second = Envelope(householdId, JobTypes.CleanUpSmartPlugImportJobs);

        await recorder.RecordAsync(second, ct);

        (await ActiveRowsAsync(householdId, JobTypes.CleanUpSmartPlugImportJobs, ct)).ShouldHaveSingleItem().Id.ShouldBe(second.JobId);
    }

    [Fact]
    public async Task A_database_error_for_an_unguarded_type_propagates_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var (recorder, householdId) = await NewRecorderAsync(ct);
        var first = Envelope(householdId, JobTypes.ProcessSmartPlugImport);
        await recorder.RecordAsync(first, ct);

        await Should.ThrowAsync<DbUpdateException>(() => recorder.RecordAsync(first, ct));
    }

    // The probe carries its own HouseholdId predicate. Here the ambient scope resolves Household B, which has
    // an active restore, while the enqueue is for Household A and fails on a reused primary key: without the
    // explicit predicate the global filter alone would report B's job as A's.
    [Fact]
    public async Task The_probe_never_reports_another_Households_active_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var (recorderB, householdB) = await NewRecorderAsync(ct);
        await recorderB.RecordAsync(Envelope(householdB, JobTypes.RestoreHouseholdData), ct);
        var (seedA, householdA) = await NewRecorderAsync(ct);
        var finished = Envelope(householdA, JobTypes.RestoreHouseholdData);
        await seedA.RecordAsync(finished, ct);
        await SetStatusAsync(householdA, finished.JobId, BackgroundJobStatus.Completed, ct);
        var (recorderAUnderB, _) = await NewRecorderAsync(ct, householdA, ambientHouseholdId: householdB);

        await Should.ThrowAsync<DbUpdateException>(
            () => recorderAUnderB.RecordAsync(Envelope(householdA, JobTypes.RestoreHouseholdData, finished.JobId), ct));
    }
}

public class PostgresActiveJobGuardTests : ActiveJobGuardTestsBase, IAsyncLifetime
{
    private readonly PostgresTestDatabase _container = new();

    private DbContextOptions<EnergyTrackerDbContext>? _options;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    protected override DbContextOptions<EnergyTrackerDbContext> Options => _options ??= new DbContextOptionsBuilder<EnergyTrackerDbContext>()
        .UseNpgsql(_container.GetConnectionString(), o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"))
        .Options;

    protected override Task<int> CountUniqueFilteredIndexAsync(EnergyTrackerDbContext dbContext, CancellationToken cancellationToken) =>
        dbContext.Database.SqlQueryRaw<int>(
            $"""SELECT COUNT(*)::int AS "Value" FROM pg_indexes WHERE indexname = '{IndexName}' AND indexdef LIKE 'CREATE UNIQUE INDEX%' AND indexdef LIKE '% WHERE %'""")
            .SingleAsync(cancellationToken);
}

public class SqlServerActiveJobGuardTests : ActiveJobGuardTestsBase, IAsyncLifetime
{
    private readonly SqlServerTestDatabase _container = new();

    private DbContextOptions<EnergyTrackerDbContext>? _options;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    protected override DbContextOptions<EnergyTrackerDbContext> Options => _options ??= new DbContextOptionsBuilder<EnergyTrackerDbContext>()
        .UseSqlServer(_container.GetConnectionString(), o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer"))
        .Options;

    protected override Task<int> CountUniqueFilteredIndexAsync(EnergyTrackerDbContext dbContext, CancellationToken cancellationToken) =>
        dbContext.Database.SqlQueryRaw<int>(
            $"SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = '{IndexName}' AND is_unique = 1 AND has_filter = 1")
            .SingleAsync(cancellationToken);
}
