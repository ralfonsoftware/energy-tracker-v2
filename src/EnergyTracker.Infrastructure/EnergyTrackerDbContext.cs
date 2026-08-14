using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure;

public class EnergyTrackerDbContext(DbContextOptions<EnergyTrackerDbContext> options, ICurrentHouseholdAccessor currentHouseholdAccessor)
    : DbContext(options), IDataProtectionKeyContext
{
    // Room/PowerPoint/Device's standard AD-3 query filter (below) needs the current Household,
    // resolved via ICurrentHouseholdAccessor. CurrentHouseholdAccessor resolves its own lookup
    // through IDbContextFactory<EnergyTrackerDbContext> rather than this DbContext type directly,
    // so there is no circular DI dependency here (DbContext -> accessor -> DbContextFactory, never
    // back to this same DbContext instance) — plain constructor injection is safe.
    private Guid? CurrentHouseholdId => currentHouseholdAccessor.HouseholdId;

    public DbSet<Household> Households => Set<Household>();

    public DbSet<HouseholdMember> HouseholdMembers => Set<HouseholdMember>();

    public DbSet<HouseholdInvite> HouseholdInvites => Set<HouseholdInvite>();

    public DbSet<Room> Rooms => Set<Room>();

    public DbSet<PowerPoint> PowerPoints => Set<PowerPoint>();

    public DbSet<Device> Devices => Set<Device>();

    // Backs PersistKeysToDbContext (AC #4) — Data Protection keys survive a scale-to-zero cold
    // start instead of being regenerated in memory (AD-17).
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EnergyTrackerDbContext).Assembly);

        // The standard, non-exempt AD-3 case — Room/PowerPoint/Device are the first entities in
        // this codebase to get it (HouseholdMember/HouseholdInvite are documented exceptions).
        // Wired here rather than in each IEntityTypeConfiguration<T> because the filter needs a
        // per-request service instance that the static Configure(EntityTypeBuilder<T>) signature
        // doesn't receive.
        modelBuilder.Entity<Room>().HasQueryFilter(e => e.HouseholdId == CurrentHouseholdId);
        modelBuilder.Entity<PowerPoint>().HasQueryFilter(e => e.HouseholdId == CurrentHouseholdId);
        modelBuilder.Entity<Device>().HasQueryFilter(e => e.HouseholdId == CurrentHouseholdId);
    }
}
