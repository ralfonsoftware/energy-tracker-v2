using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

// StatusSnapshotRepository.GetForHouseholdAsync's GroupBy-then-pick-latest-per-group dedupe query
// (Story 4.3) is exactly the kind of query shape that can translate differently between Npgsql and
// SQL Server — every scenario here runs against BOTH real providers (AD-2), mirroring
// MeterReadingRepositoryTests.cs's identical justification for its own query-shape risk.
public abstract class StatusSnapshotRepositoryTestsBase
{
    private sealed class FixedHouseholdAccessor(Guid householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;

        public Guid? HouseholdMemberId => null;
    }

    protected abstract Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken);

    protected static EnergyTrackerDbContext NewDbContext(DbContextOptions<EnergyTrackerDbContext> options, Guid householdId) =>
        new(options, new FixedHouseholdAccessor(householdId));

    private static async Task SeedHouseholdAsync(EnergyTrackerDbContext dbContext, Guid householdId, CancellationToken cancellationToken)
    {
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static StatusSnapshot NewSnapshot(
        Guid householdId, DateTimeOffset effectiveAtUtc, DateTimeOffset computedAtUtc, decimal paceToDateKwh, Status status = Status.WithinRange) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        Status = status,
        PaceToDateKwh = paceToDateKwh,
        BaselineToDateKwh = 100m,
        IsLowConfidence = false,
        ComputedAtUtc = computedAtUtc,
        EffectiveAtUtc = effectiveAtUtc,
    };

    [Fact]
    public async Task Two_snapshots_sharing_an_EffectiveAtUtc_return_only_the_one_with_the_greater_ComputedAtUtc()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var effectiveAt = DateTimeOffset.UtcNow.AddDays(-10);
        var stale = NewSnapshot(householdId, effectiveAt, computedAtUtc: effectiveAt, paceToDateKwh: 100m);
        var superseding = NewSnapshot(householdId, effectiveAt, computedAtUtc: DateTimeOffset.UtcNow, paceToDateKwh: 150m);
        dbContext.StatusSnapshots.AddRange(stale, superseding);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new StatusSnapshotRepository(dbContext);

        var result = await repository.GetForHouseholdAsync(householdId, TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(superseding.Id);
        result[0].PaceToDateKwh.ShouldBe(150m);
    }

    [Fact]
    public async Task Snapshots_with_distinct_EffectiveAtUtc_all_return_ordered_ascending()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var earliest = NewSnapshot(householdId, now.AddDays(-20), computedAtUtc: now.AddDays(-20), paceToDateKwh: 10m);
        var middle = NewSnapshot(householdId, now.AddDays(-10), computedAtUtc: now.AddDays(-10), paceToDateKwh: 20m);
        var latest = NewSnapshot(householdId, now, computedAtUtc: now, paceToDateKwh: 30m);
        // Deliberately inserted out of chronological order to prove the result is ordered by
        // EffectiveAtUtc, not insertion/Id order.
        dbContext.StatusSnapshots.AddRange(latest, earliest, middle);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new StatusSnapshotRepository(dbContext);

        var result = await repository.GetForHouseholdAsync(householdId, TestContext.Current.CancellationToken);

        result.Select(s => s.Id).ShouldBe([earliest.Id, middle.Id, latest.Id]);
    }
}

public class PostgresStatusSnapshotRepositoryTests : StatusSnapshotRepositoryTestsBase, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    protected override async Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));
        var dbContext = NewDbContext(optionsBuilder.Options, householdId);
        await dbContext.Database.MigrateAsync(cancellationToken);
        return dbContext;
    }
}

public class SqlServerStatusSnapshotRepositoryTests : StatusSnapshotRepositoryTestsBase, IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    protected override async Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer"));
        var dbContext = NewDbContext(optionsBuilder.Options, householdId);
        await dbContext.Database.MigrateAsync(cancellationToken);
        return dbContext;
    }
}
