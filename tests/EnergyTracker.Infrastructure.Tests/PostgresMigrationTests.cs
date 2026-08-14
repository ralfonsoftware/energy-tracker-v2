using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

public class PostgresMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task Postgres_migrations_apply_cleanly_to_a_real_database()
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));

        await using var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, null!);

        await dbContext.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var appliedMigrations = await dbContext.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        appliedMigrations.ShouldContain(m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
        appliedMigrations.ShouldContain(m => m.EndsWith("_AddHouseholdAndDataProtectionKeys", StringComparison.Ordinal));
    }
}
