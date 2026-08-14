using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure;

/// <summary>
/// A second, minimal DbContext over the same HouseholdMembers table, used only by
/// CurrentHouseholdAccessor for its own HouseholdId lookup. Deliberately not
/// EnergyTrackerDbContext: that type's Room/PowerPoint/Device AD-3 query filter needs
/// ICurrentHouseholdAccessor, so if CurrentHouseholdAccessor depended on EnergyTrackerDbContext
/// there would be a circular DI dependency — and even routing around that with a factory or
/// lazy wrapper would leave CurrentHouseholdAccessor's lookup running on the very same
/// EnergyTrackerDbContext instance that might be mid-query for Room/PowerPoint/Device, risking a
/// nested synchronous operation on that context. A separate, unrelated context type over the
/// same table sidesteps both problems structurally. It never runs migrations of its own —
/// EnergyTrackerDbContext's migrations own this table — so no MigrationsAssembly/design-time
/// factory is needed for it.
/// </summary>
public class HouseholdMembershipDbContext(DbContextOptions<HouseholdMembershipDbContext> options) : DbContext(options)
{
    public DbSet<HouseholdMember> HouseholdMembers => Set<HouseholdMember>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new HouseholdMemberConfiguration());
    }
}
