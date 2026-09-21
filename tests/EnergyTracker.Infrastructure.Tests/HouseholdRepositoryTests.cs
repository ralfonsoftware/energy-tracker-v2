using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

// Story 6.3 — Household.AiPlausibilityEnabled is the first column this repository's
// UpdateAiPlausibilityEnabledAsync touches to get a real dual-provider DB round-trip test (AD-2):
// SetYearlyBaselineTests only ever mocks IHouseholdRepository, so nothing in this codebase
// previously proved UpdateYearlyBaselineAsync's identical AD-4 mechanics against a real database
// either — this file covers the new method's own behavior, not a retrofit of the older one.
public abstract class HouseholdRepositoryTestsBase
{
    // Household has no AD-3 query filter on itself (it's the tenant root) — this accessor's value
    // is never consulted by anything under test here, only required by EnergyTrackerDbContext's
    // constructor signature.
    protected sealed class NoOpCurrentHouseholdAccessor : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId => null;

        public Guid? HouseholdMemberId => null;
    }

    protected abstract Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(CancellationToken cancellationToken);

    protected abstract EnergyTrackerDbContext OpenFreshDbContext();

    private static Household NewHousehold(Guid id) => new()
    {
        Id = id,
        Locale = "en-US",
        Currency = "USD",
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task UpdateAiPlausibilityEnabledAsync_persists_true_and_survives_a_fresh_read()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(TestContext.Current.CancellationToken);
        dbContext.Households.Add(NewHousehold(householdId));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new HouseholdRepository(dbContext);

        var updated = await repository.UpdateAiPlausibilityEnabledAsync(householdId, true, 0, TestContext.Current.CancellationToken);

        updated.AiPlausibilityEnabled.ShouldBeTrue();
        updated.Version.ShouldBe(1);

        await using var freshDbContext = OpenFreshDbContext();
        var reloaded = await freshDbContext.Households.SingleAsync(h => h.Id == householdId, TestContext.Current.CancellationToken);
        reloaded.AiPlausibilityEnabled.ShouldBeTrue();
        reloaded.Version.ShouldBe(1);
    }

    [Fact]
    public async Task UpdateAiPlausibilityEnabledAsync_can_toggle_back_to_false()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(TestContext.Current.CancellationToken);
        dbContext.Households.Add(NewHousehold(householdId));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new HouseholdRepository(dbContext);
        await repository.UpdateAiPlausibilityEnabledAsync(householdId, true, 0, TestContext.Current.CancellationToken);

        var updated = await repository.UpdateAiPlausibilityEnabledAsync(householdId, false, 1, TestContext.Current.CancellationToken);

        updated.AiPlausibilityEnabled.ShouldBeFalse();

        await using var freshDbContext = OpenFreshDbContext();
        var reloaded = await freshDbContext.Households.SingleAsync(h => h.Id == householdId, TestContext.Current.CancellationToken);
        reloaded.AiPlausibilityEnabled.ShouldBeFalse();
    }

    // AD-4: a stale expectedVersion (someone else's concurrent write already bumped it) must be
    // rejected rather than silently overwriting.
    [Fact]
    public async Task UpdateAiPlausibilityEnabledAsync_throws_on_a_stale_expectedVersion()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(TestContext.Current.CancellationToken);
        dbContext.Households.Add(NewHousehold(householdId));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new HouseholdRepository(dbContext);
        await repository.UpdateAiPlausibilityEnabledAsync(householdId, true, 0, TestContext.Current.CancellationToken);

        // dbContext's own tracked instance now sits at Version 1 — a stale caller who still thinks
        // it's 0 (as if they'd read the household before the update above landed) must be rejected.
        await Should.ThrowAsync<HouseholdConcurrencyConflictException>(() =>
            repository.UpdateAiPlausibilityEnabledAsync(householdId, false, 0, TestContext.Current.CancellationToken));
    }
}

public class PostgresHouseholdRepositoryTests : HouseholdRepositoryTestsBase, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    protected override async Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));
        var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new NoOpCurrentHouseholdAccessor());
        await dbContext.Database.MigrateAsync(cancellationToken);
        return dbContext;
    }

    protected override EnergyTrackerDbContext OpenFreshDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString());
        return new EnergyTrackerDbContext(optionsBuilder.Options, new NoOpCurrentHouseholdAccessor());
    }
}

public class SqlServerHouseholdRepositoryTests : HouseholdRepositoryTestsBase, IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    protected override async Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer"));
        var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new NoOpCurrentHouseholdAccessor());
        await dbContext.Database.MigrateAsync(cancellationToken);
        return dbContext;
    }

    protected override EnergyTrackerDbContext OpenFreshDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString());
        return new EnergyTrackerDbContext(optionsBuilder.Options, new NoOpCurrentHouseholdAccessor());
    }
}
