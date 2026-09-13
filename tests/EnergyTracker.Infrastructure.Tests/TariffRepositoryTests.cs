using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

// The AD-4 concurrency conflict (DbUpdateConcurrencyException -> TariffConcurrencyConflictException)
// is only provable against a real engine — an in-memory EF provider doesn't enforce the
// concurrency-token check the same way — so every scenario here runs against BOTH real providers
// (AD-2), mirroring MeterReadingRepositoryTests' own precedent.
public abstract class TariffRepositoryTestsBase
{
    private sealed class FixedHouseholdAccessor(Guid householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;

        public Guid? HouseholdMemberId => null;
    }

    protected abstract Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken);

    protected static EnergyTrackerDbContext NewDbContext(DbContextOptions<EnergyTrackerDbContext> options, Guid householdId) =>
        new(options, new FixedHouseholdAccessor(householdId));

    protected static async Task SeedHouseholdAsync(EnergyTrackerDbContext dbContext, Guid householdId, CancellationToken cancellationToken)
    {
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    protected static Tariff NewTariff(Guid householdId, decimal monthlyBaseFee, decimal pricePerKwh, string currency, DateTimeOffset contractStartDate, int contractPeriodMonths = 12) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        MonthlyBaseFee = monthlyBaseFee,
        PricePerKwh = pricePerKwh,
        Currency = currency,
        ContractStartDate = contractStartDate,
        ContractPeriodMonths = contractPeriodMonths,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task AddAsync_persists_a_new_Tariff_entry()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new TariffRepository(dbContext);
        var tariff = NewTariff(householdId, 12.50m, 0.3200m, "EUR", DateTimeOffset.UtcNow);

        await repository.AddAsync(tariff, TestContext.Current.CancellationToken);

        var reloaded = await repository.FindByIdAsync(tariff.Id, TestContext.Current.CancellationToken);
        reloaded.ShouldNotBeNull();
        reloaded.PricePerKwh.ShouldBe(0.3200m);
    }

    [Fact]
    public async Task GetHistoryForHouseholdAsync_orders_by_ContractStartDate_descending_and_paginates()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var newest = NewTariff(householdId, 15m, 0.35m, "EUR", now);
        var middle = NewTariff(householdId, 12m, 0.33m, "EUR", now.AddMonths(-12));
        var oldest = NewTariff(householdId, 10m, 0.30m, "EUR", now.AddMonths(-24));
        dbContext.Tariffs.AddRange(newest, middle, oldest);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new TariffRepository(dbContext);

        var (items, totalCount) = await repository.GetHistoryForHouseholdAsync(householdId, 1, 2, TestContext.Current.CancellationToken);

        totalCount.ShouldBe(3);
        items.Select(t => t.Id).ShouldBe([newest.Id, middle.Id]);
    }

    [Fact]
    public async Task UpdateAsync_applies_only_the_non_null_fields_and_increments_Version()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var tariff = NewTariff(householdId, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow);
        dbContext.Tariffs.Add(tariff);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new TariffRepository(dbContext);

        var updated = await repository.UpdateAsync(
            tariff.Id, monthlyBaseFee: 20m, pricePerKwh: null, currency: null, contractStartDate: null, contractPeriodMonths: null,
            expectedVersion: 0, TestContext.Current.CancellationToken);

        updated.MonthlyBaseFee.ShouldBe(20m);
        updated.PricePerKwh.ShouldBe(0.32m); // untouched
        updated.Currency.ShouldBe("EUR"); // untouched
        updated.Version.ShouldBe(1);
    }

    [Fact]
    public async Task UpdateAsync_with_a_stale_Version_throws_TariffConcurrencyConflictException()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var tariff = NewTariff(householdId, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow);
        dbContext.Tariffs.Add(tariff);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new TariffRepository(dbContext);

        // A second writer holding a Version that's already been superseded.
        await repository.UpdateAsync(
            tariff.Id, monthlyBaseFee: 20m, pricePerKwh: null, currency: null, contractStartDate: null, contractPeriodMonths: null,
            expectedVersion: 0, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<TariffConcurrencyConflictException>(() =>
            repository.UpdateAsync(
                tariff.Id, monthlyBaseFee: 30m, pricePerKwh: null, currency: null, contractStartDate: null, contractPeriodMonths: null,
                expectedVersion: 0, TestContext.Current.CancellationToken));

        // The first writer's committed value survives, not silently overwritten by the rejected
        // second write. A fresh DbContext, not the one the failed write ran through — EF leaves a
        // DbUpdateConcurrencyException's attempted (never-committed) property values sitting on
        // the tracked in-memory instance, so re-reading through the SAME context would return the
        // rejected 30 rather than the real, persisted 20.
        await using var freshDbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var reloaded = await freshDbContext.Tariffs.SingleAsync(t => t.Id == tariff.Id, TestContext.Current.CancellationToken);
        reloaded.MonthlyBaseFee.ShouldBe(20m);
    }
}

public class PostgresTariffRepositoryTests : TariffRepositoryTestsBase, IAsyncLifetime
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

    [Fact]
    public async Task PricePerKwh_preserves_4_decimal_places_not_truncated_to_2()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new TariffRepository(dbContext);
        var tariff = NewTariff(householdId, 12.50m, 0.3256m, "EUR", DateTimeOffset.UtcNow);

        await repository.AddAsync(tariff, TestContext.Current.CancellationToken);

        // Fresh DbContext, forcing a real round-trip through the (18,4) column rather than reading
        // back the tracked in-memory instance.
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString());
        await using var freshDbContext = NewDbContext(optionsBuilder.Options, householdId);
        var reloaded = await freshDbContext.Tariffs.SingleAsync(t => t.Id == tariff.Id, TestContext.Current.CancellationToken);
        reloaded.PricePerKwh.ShouldBe(0.3256m);
    }
}

public class SqlServerTariffRepositoryTests : TariffRepositoryTestsBase, IAsyncLifetime
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
