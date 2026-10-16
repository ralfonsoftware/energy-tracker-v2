using EnergyTracker.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EnergyTracker.Api.Tests;

// Builds real API hosts against one Postgres Testcontainer so the Data Protection tests can start
// several "instances" (cold starts) that share only the database. Hosts run as Production, the
// environment where fail-closed applies.
internal static class DataProtectionPostgresHost
{
    public static WebApplicationFactory<Program> Create(string connectionString, Dictionary<string, string?> dataProtectionSettings)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Database:Provider", "Postgres");
            builder.UseSetting("ConnectionStrings:Default", connectionString);
            foreach (var (key, value) in dataProtectionSettings)
            {
                builder.UseSetting($"DataProtection:{key}", value);
            }
        });
    }

    public static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var factory = Create(connectionString, new() { ["AllowUnprotectedKeys"] = "true" });
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>().Database.MigrateAsync(cancellationToken);
    }

    // Counts only: key XML is never read into a message, a log or a failure output.
    public static async Task<KeyRingSummary> SummarizeAsync(WebApplicationFactory<Program> factory, CancellationToken cancellationToken)
    {
        using var scope = factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>()
            .DataProtectionKeys
            .AsNoTracking()
            .Select(k => k.Xml ?? string.Empty)
            .ToListAsync(cancellationToken);

        return new KeyRingSummary(
            Rows: rows.Count,
            PlaintextMasterKeyRows: rows.Count(x => x.Contains("<masterKey", StringComparison.Ordinal)),
            EncryptedRows: rows.Count(x => x.Contains("EncryptedData", StringComparison.Ordinal)),
            RevocationRows: rows.Count(x => x.Contains("<revocation", StringComparison.Ordinal)));
    }

    public sealed record KeyRingSummary(int Rows, int PlaintextMasterKeyRows, int EncryptedRows, int RevocationRows);
}
