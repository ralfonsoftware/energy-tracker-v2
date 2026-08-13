using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.MsSql;

namespace EnergyTracker.Infrastructure.Tests;

public class SqlServerMigrationTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task SqlServer_migrations_apply_cleanly_to_a_real_database()
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer"));

        await using var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options);

        await dbContext.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var appliedMigrations = await dbContext.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        appliedMigrations.ShouldContain(m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
        appliedMigrations.ShouldContain(m => m.EndsWith("_AddHouseholdAndDataProtectionKeys", StringComparison.Ordinal));
    }
}
