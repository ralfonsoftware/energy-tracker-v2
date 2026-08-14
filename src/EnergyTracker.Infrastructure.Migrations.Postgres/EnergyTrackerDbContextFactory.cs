using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EnergyTracker.Infrastructure.Migrations.Postgres;

public class EnergyTrackerDbContextFactory : IDesignTimeDbContextFactory<EnergyTrackerDbContext>
{
    public EnergyTrackerDbContext CreateDbContext(string[] args)
    {
        // dotnet ef only needs a syntactically valid connection string to build the model at
        // design time — it does not need to actually connect. Prefer the real environment
        // variable (same shape the app reads at runtime) so this never ships a credential-shaped
        // literal in the compiled assembly; the local fallback below never touches a live database.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Database=energytracker_design";

        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            o => o.MigrationsAssembly(typeof(EnergyTrackerDbContextFactory).Assembly.GetName().Name));

        // ICurrentHouseholdAccessor is never actually used at design time — Room/PowerPoint/
        // Device's AD-3 filter only resolves it when a query executes, and `dotnet ef migrations
        // add` only needs to build the model, never run a query.
        return new EnergyTrackerDbContext(optionsBuilder.Options, null!);
    }
}
