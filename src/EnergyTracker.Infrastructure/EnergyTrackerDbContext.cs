using EnergyTracker.Domain;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure;

public class EnergyTrackerDbContext(DbContextOptions<EnergyTrackerDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Household> Households => Set<Household>();

    public DbSet<HouseholdMember> HouseholdMembers => Set<HouseholdMember>();

    public DbSet<HouseholdInvite> HouseholdInvites => Set<HouseholdInvite>();

    // Backs PersistKeysToDbContext (AC #4) — Data Protection keys survive a scale-to-zero cold
    // start instead of being regenerated in memory (AD-17).
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EnergyTrackerDbContext).Assembly);
    }
}
