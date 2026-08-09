using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EnergyTracker.Infrastructure.Migrations.Postgres;

public class EnergyTrackerDbContextFactory : IDesignTimeDbContextFactory<EnergyTrackerDbContext>
{
    public EnergyTrackerDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=energytracker_design;Username=postgres;Password=postgres",
            o => o.MigrationsAssembly(typeof(EnergyTrackerDbContextFactory).Assembly.GetName().Name));

        return new EnergyTrackerDbContext(optionsBuilder.Options);
    }
}
