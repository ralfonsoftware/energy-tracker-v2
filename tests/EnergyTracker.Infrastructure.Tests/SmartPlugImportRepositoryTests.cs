using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

public class SmartPlugImportRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private sealed class FixedHouseholdAccessor(Guid householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;
    }

    [Fact]
    public async Task UpdateMappingAsync_raises_the_command_timeout_past_the_30s_ADO_NET_default()
    {
        var householdId = Guid.NewGuid();
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));

        await using var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
        await dbContext.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var import = new SmartPlugImport
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            BackgroundJobId = Guid.NewGuid(),
            VendorFormat = SmartPlugVendorFormat.EveHome,
            OriginalFileName = "export.xlsx",
            Status = SmartPlugImportStatus.AwaitingPowerPointMapping,
            DeviceTag = "Kitchen Plug",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var repository = new SmartPlugImportRepository(dbContext);

        // A large Eve Home export's set-based mapping UPDATE reliably exceeded the ADO.NET
        // default 30s command timeout against Basic-tier Azure SQL in production, surfacing as an
        // unhandled 500 on POST /api/smart-plug-imports/{id}/power-point-mapping. This asserts the
        // timeout the repository configures, not the query plan/duration itself — reproducing a
        // real multi-minute Basic-tier timeout in a fast test isn't practical (root cause is a
        // resource-tier/config mismatch, not app logic verifiable via a small dataset).
        await repository.UpdateMappingAsync(
            import, Guid.NewGuid(), "Fridge", "Kitchen", TestContext.Current.CancellationToken);

        dbContext.Database.GetCommandTimeout().ShouldBe(180);
    }
}
