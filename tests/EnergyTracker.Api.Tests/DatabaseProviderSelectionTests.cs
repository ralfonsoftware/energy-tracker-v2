using EnergyTracker.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class DatabaseProviderSelectionTests
{
    [Theory]
    [InlineData("Postgres", "Npgsql.EntityFrameworkCore.PostgreSQL")]
    [InlineData("SqlServer", "Microsoft.EntityFrameworkCore.SqlServer")]
    public async Task DbContext_is_registered_with_the_configured_provider(string provider, string expectedProviderName)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", provider);
        });

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();

        dbContext.Database.ProviderName.ShouldBe(expectedProviderName);
    }
}
