using EnergyTracker.Domain;
using EnergyTracker.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Api.Tests;

/// <summary>
/// A real Postgres-backed host (via Testcontainers) with the OIDC handshake swapped for
/// <see cref="TestAuthHandler"/> — no bundled local OIDC provider exists for dev/test, so
/// AC verification uses a principal with known iss/sub claims instead of a live handshake.
/// </summary>
public class EnergyTrackerApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Provider", "Postgres");
        builder.UseSetting("ConnectionStrings:Default", _container.GetConnectionString());

        builder.ConfigureTestServices(services =>
        {
            services
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    public HttpClient CreateAuthenticatedClient(string subject, string? issuer = null, string? name = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, subject);
        client.DefaultRequestHeaders.Add(TestAuthHandler.IssuerHeader, issuer ?? TestAuthHandler.DefaultIssuer);
        if (name is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, name);
        }

        return client;
    }

    // AD-3's query filter needs an HttpContext-bound CurrentHouseholdAccessor, absent in this raw
    // scope — IgnoreQueryFilters is required to count/read directly against the table. Shared here
    // (not duplicated per test class) since both StatusEndpointsTests and MeterReadingEndpointsTests
    // need it.
    public async Task<int> CountStatusSnapshotRowsAsync(Guid householdId)
    {
        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        return await dbContext.StatusSnapshots.IgnoreQueryFilters()
            .CountAsync(s => s.HouseholdId == householdId, TestContext.Current.CancellationToken);
    }

    public async Task<StatusSnapshot> GetLatestStatusSnapshotAsync(Guid householdId)
    {
        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        return await dbContext.StatusSnapshots.IgnoreQueryFilters()
            .Where(s => s.HouseholdId == householdId)
            .OrderByDescending(s => s.ComputedAtUtc)
            .FirstAsync(TestContext.Current.CancellationToken);
    }
}
