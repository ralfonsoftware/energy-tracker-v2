using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EnergyTracker.Infrastructure.Migrations.SqlServer;

public class EnergyTrackerDbContextFactory : IDesignTimeDbContextFactory<EnergyTrackerDbContext>
{
    public EnergyTrackerDbContext CreateDbContext(string[] args)
    {
        // dotnet ef only needs a syntactically valid connection string to build the model at
        // design time — it does not need to actually connect. Prefer the real environment
        // variable (same shape the app reads at runtime) so this never ships a credential-shaped
        // literal in the compiled assembly; the local fallback below never touches a live database.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Server=localhost;Database=energytracker_design;TrustServerCertificate=True";

        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(
            connectionString,
            o => o.MigrationsAssembly(typeof(EnergyTrackerDbContextFactory).Assembly.GetName().Name));

        return new EnergyTrackerDbContext(optionsBuilder.Options);
    }
}
