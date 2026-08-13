using EnergyTracker.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Api.Tests;

public class DataProtectionColdStartTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task A_value_protected_by_one_host_instance_can_be_unprotected_by_a_second_freshly_constructed_instance()
    {
        // Simulates an Azure Container Apps scale-to-zero cold start: the second instance never
        // shares in-memory state with the first — only the database. If PersistKeysToDbContext
        // weren't wired up (or Data Protection fell back to its in-memory default, AD-17's exact
        // gotcha), each instance would generate its own incompatible key ring and unprotecting a
        // value created by the other would throw.
        const string plaintext = "story-1.5-cold-start-check";

        string protectedValue;
        await using (var firstInstance = CreateFactory())
        {
            using var scope = firstInstance.Services.CreateScope();
            var provider = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
            protectedValue = provider.CreateProtector("EnergyTracker.Tests.ColdStart").Protect(plaintext);
        }

        await using var secondInstance = CreateFactory();
        using var secondScope = secondInstance.Services.CreateScope();
        var secondProvider = secondScope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        var unprotectedValue = secondProvider.CreateProtector("EnergyTracker.Tests.ColdStart").Unprotect(protectedValue);

        unprotectedValue.ShouldBe(plaintext);
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Postgres");
            builder.UseSetting("ConnectionStrings:Default", _container.GetConnectionString());
        });
}
