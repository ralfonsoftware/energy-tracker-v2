using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EnergyTracker.Infrastructure.Migrations.SqlServer;

public class EnergyTrackerDbContextFactory : IDesignTimeDbContextFactory<EnergyTrackerDbContext>
{
    public EnergyTrackerDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(
            "Server=localhost;Database=energytracker_design;Trusted_Connection=True;TrustServerCertificate=True",
            o => o.MigrationsAssembly(typeof(EnergyTrackerDbContextFactory).Assembly.GetName().Name));

        return new EnergyTrackerDbContext(optionsBuilder.Options);
    }
}
