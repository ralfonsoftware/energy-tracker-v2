using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure;

/// <summary>
/// A second, minimal DbContext over the same HouseholdMembers table, used only by
/// HouseholdMembershipResolver for its own-row lookup (the membership middleware calls it once per /api
/// request and CurrentHouseholdAccessor reads the cached result). Deliberately not
/// EnergyTrackerDbContext: that type's Room/PowerPoint/Device AD-3 query filter needs
/// ICurrentHouseholdAccessor, so a lookup on EnergyTrackerDbContext would be circular (the filter needs
/// the Household to answer the question that finds the Household) and would run on the very same
/// EnergyTrackerDbContext instance that might be mid-query for Room/PowerPoint/Device. A separate,
/// unrelated context type over the same table sidesteps both problems structurally. It never runs
/// migrations of its own — EnergyTrackerDbContext's migrations own this table — so no
/// MigrationsAssembly/design-time factory is needed for it.
/// </summary>
public class HouseholdMembershipDbContext(DbContextOptions<HouseholdMembershipDbContext> options) : DbContext(options)
{
    public DbSet<HouseholdMember> HouseholdMembers => Set<HouseholdMember>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new HouseholdMemberConfiguration());
    }
}
