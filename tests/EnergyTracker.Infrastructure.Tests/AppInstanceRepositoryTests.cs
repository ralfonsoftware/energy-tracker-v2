using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

// Story 11.13 (AD-24, AC #6): the instance heartbeat table against real Postgres and SQL Server. The
// table is instance-level operational data, not Household data (AD-3 exemption), so there is no
// HouseholdId and no query filter. Timestamps are given with a non-zero offset on purpose: the
// global UtcDateTimeOffsetConverter must normalise them (Npgsql accepts only offset 0).
public abstract class AppInstanceRepositoryTestsBase
{
    protected abstract Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(CancellationToken cancellationToken);

    protected abstract EnergyTrackerDbContext OpenFreshDbContext();

    private static readonly TimeSpan Berlin = TimeSpan.FromHours(2);
    private static readonly DateTimeOffset Started = new(2026, 10, 24, 14, 0, 0, Berlin); // 12:00 UTC

    // Migrates the database on first use (the migration is itself the test subject), then runs the call
    // on a fresh DbContext, the way a heartbeat scope would.
    private async Task<TResult> WithRepositoryAsync<TResult>(Func<AppInstanceRepository, Task<TResult>> call)
    {
        await using (await OpenMigratedDbContextAsync(TestContext.Current.CancellationToken))
        {
        }

        await using var dbContext = OpenFreshDbContext();
        return await call(new AppInstanceRepository(dbContext));
    }

    private Task WithRepositoryAsync(Func<AppInstanceRepository, Task> call) =>
        WithRepositoryAsync<object?>(async repository =>
        {
            await call(repository);
            return null;
        });

    private async Task<List<AppInstance>> AllRowsAsync()
    {
        await using var verify = OpenFreshDbContext();
        return await verify.AppInstances.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private Task BeatAsync(Guid id, DateTimeOffset startedAtUtc, DateTimeOffset nowUtc) =>
        WithRepositoryAsync(repository => repository.BeatAsync(id, startedAtUtc, nowUtc, TestContext.Current.CancellationToken));

    private Task DeleteAsync(Guid id) =>
        WithRepositoryAsync(repository => repository.DeleteAsync(id, TestContext.Current.CancellationToken));

    [Fact]
    public async Task The_AppInstances_table_exists_after_migrating_and_starts_empty()
    {
        await using var dbContext = await OpenMigratedDbContextAsync(TestContext.Current.CancellationToken);

        (await dbContext.AppInstances.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task The_first_beat_inserts_the_row_with_the_real_start_and_the_beat_time()
    {
        var id = Guid.NewGuid();

        await BeatAsync(id, Started, Started.AddMinutes(1));

        var row = (await AllRowsAsync()).ShouldHaveSingleItem();
        row.Id.ShouldBe(id);
        row.StartedAtUtc.ShouldBe(Started);
        row.LastSeenAtUtc.ShouldBe(Started.AddMinutes(1));
        row.StartedAtUtc.Offset.ShouldBe(TimeSpan.Zero);
        row.LastSeenAtUtc.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task The_second_beat_updates_only_LastSeenAtUtc_and_keeps_StartedAtUtc()
    {
        var id = Guid.NewGuid();
        await BeatAsync(id, Started, Started.AddMinutes(1));

        // A different started value on the second call proves the update never rewrites StartedAtUtc.
        await BeatAsync(id, Started.AddHours(5), Started.AddMinutes(2));

        var row = (await AllRowsAsync()).ShouldHaveSingleItem();
        row.StartedAtUtc.ShouldBe(Started);
        row.LastSeenAtUtc.ShouldBe(Started.AddMinutes(2));
    }

    [Fact]
    public async Task A_beat_after_another_process_deleted_the_row_inserts_it_again_with_the_given_start()
    {
        var id = Guid.NewGuid();
        await BeatAsync(id, Started, Started.AddMinutes(1));
        await DeleteAsync(id);

        await BeatAsync(id, Started, Started.AddMinutes(9));

        var row = (await AllRowsAsync()).ShouldHaveSingleItem();
        row.StartedAtUtc.ShouldBe(Started);
        row.LastSeenAtUtc.ShouldBe(Started.AddMinutes(9));
    }

    [Fact]
    public async Task ListAliveOthersAsync_excludes_self_and_rows_last_seen_before_the_cutoff()
    {
        var self = Guid.NewGuid();
        var aliveOther = Guid.NewGuid();
        var staleOther = Guid.NewGuid();
        var now = Started.AddHours(1);
        await BeatAsync(self, Started, now);
        await BeatAsync(aliveOther, Started.AddMinutes(-30), now.AddSeconds(-30));
        await BeatAsync(staleOther, Started.AddMinutes(-90), now.AddMinutes(-10));

        var others = await WithRepositoryAsync(repository =>
            repository.ListAliveOthersAsync(self, now.AddMinutes(-3), TestContext.Current.CancellationToken));

        var other = others.ShouldHaveSingleItem();
        other.Id.ShouldBe(aliveOther);
        other.StartedAtUtc.ShouldBe(Started.AddMinutes(-30));
        other.LastSeenAtUtc.ShouldBe(now.AddSeconds(-30));
    }

    [Fact]
    public async Task ListAliveOthersAsync_includes_a_row_last_seen_exactly_at_the_cutoff()
    {
        var self = Guid.NewGuid();
        var other = Guid.NewGuid();
        var cutoff = Started.AddHours(1);
        await BeatAsync(other, Started, cutoff);

        var others = await WithRepositoryAsync(repository =>
            repository.ListAliveOthersAsync(self, cutoff, TestContext.Current.CancellationToken));

        others.ShouldHaveSingleItem().Id.ShouldBe(other);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_removes_only_rows_strictly_older_than_the_cutoff()
    {
        var older = Guid.NewGuid();
        var exactlyAtCutoff = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var cutoff = Started.AddHours(30);
        await BeatAsync(older, Started, cutoff.AddSeconds(-1));
        await BeatAsync(exactlyAtCutoff, Started, cutoff);
        await BeatAsync(newer, Started, cutoff.AddSeconds(1));

        await WithRepositoryAsync(repository => repository.DeleteOlderThanAsync(cutoff, TestContext.Current.CancellationToken));

        (await AllRowsAsync()).Select(r => r.Id).ShouldBe([exactlyAtCutoff, newer], ignoreOrder: true);
    }

    [Fact]
    public async Task DeleteAsync_removes_only_the_given_instance()
    {
        var gone = Guid.NewGuid();
        var kept = Guid.NewGuid();
        await BeatAsync(gone, Started, Started.AddMinutes(1));
        await BeatAsync(kept, Started, Started.AddMinutes(1));

        await DeleteAsync(gone);

        (await AllRowsAsync()).ShouldHaveSingleItem().Id.ShouldBe(kept);
    }

    [Fact]
    public async Task DeleteAsync_for_an_unknown_instance_is_not_an_error()
    {
        await Should.NotThrowAsync(() => DeleteAsync(Guid.NewGuid()));
    }
}

public class PostgresAppInstanceRepositoryTests : AppInstanceRepositoryTestsBase, IAsyncLifetime
{
    private readonly PostgresTestDatabase _container = new();
    private bool _migrated;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    protected override async Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));
        var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new NoOpAccessor());
        if (!_migrated)
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
            _migrated = true;
        }

        return dbContext;
    }

    protected override EnergyTrackerDbContext OpenFreshDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString());
        return new EnergyTrackerDbContext(optionsBuilder.Options, new NoOpAccessor());
    }

    private sealed class NoOpAccessor : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId => null;

        public Guid? HouseholdMemberId => null;
    }
}

public class SqlServerAppInstanceRepositoryTests : AppInstanceRepositoryTestsBase, IAsyncLifetime
{
    private readonly SqlServerTestDatabase _container = new();
    private bool _migrated;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    protected override async Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer"));
        var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new NoOpAccessor());
        if (!_migrated)
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
            _migrated = true;
        }

        return dbContext;
    }

    protected override EnergyTrackerDbContext OpenFreshDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString());
        return new EnergyTrackerDbContext(optionsBuilder.Options, new NoOpAccessor());
    }

    private sealed class NoOpAccessor : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId => null;

        public Guid? HouseholdMemberId => null;
    }
}
