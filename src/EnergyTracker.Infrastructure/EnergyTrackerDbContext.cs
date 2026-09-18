using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Converters;
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

    public DbSet<MainMeter> MainMeters => Set<MainMeter>();

    public DbSet<MeterReading> MeterReadings => Set<MeterReading>();

    public DbSet<MeterRegressionPrompt> MeterRegressionPrompts => Set<MeterRegressionPrompt>();

    public DbSet<StatusSnapshot> StatusSnapshots => Set<StatusSnapshot>();

    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();

    public DbSet<SmartPlugImport> SmartPlugImports => Set<SmartPlugImport>();

    public DbSet<SmartPlugReading> SmartPlugReadings => Set<SmartPlugReading>();

    public DbSet<SmartPlugImportGap> SmartPlugImportGaps => Set<SmartPlugImportGap>();

    public DbSet<AuditCorrection> AuditCorrections => Set<AuditCorrection>();

    public DbSet<Tariff> Tariffs => Set<Tariff>();

    public DbSet<Event> Events => Set<Event>();

    // Backs PersistKeysToDbContext (AC #4) — Data Protection keys survive a scale-to-zero cold
    // start instead of being regenerated in memory (AD-17).
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // AD-2: normalizes every DateTimeOffset write to UTC, in this one shared context, for both
        // providers — see UtcDateTimeOffsetConverter for why (Npgsql's offset-0-only rule). Both
        // nullable and non-nullable must be registered; a nullable-only registration would silently
        // leave columns like ArchivedAt/EffectiveUntil exposed.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

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
        modelBuilder.Entity<MainMeter>().HasQueryFilter(e => e.HouseholdId == CurrentHouseholdId);
        modelBuilder.Entity<MeterReading>().HasQueryFilter(e => e.HouseholdId == CurrentHouseholdId);
        modelBuilder.Entity<MeterRegressionPrompt>().HasQueryFilter(e => e.HouseholdId == CurrentHouseholdId);
        modelBuilder.Entity<StatusSnapshot>().HasQueryFilter(e => e.HouseholdId == CurrentHouseholdId);
        modelBuilder.Entity<BackgroundJob>().HasQueryFilter(e => e.HouseholdId == CurrentHouseholdId);
        modelBuilder.Entity<SmartPlugImport>().HasQueryFilter(e => e.HouseholdId == CurrentHouseholdId);
        modelBuilder.Entity<SmartPlugReading>().HasQueryFilter(e => e.HouseholdId == CurrentHouseholdId);
        modelBuilder.Entity<SmartPlugImportGap>().HasQueryFilter(e => e.HouseholdId == CurrentHouseholdId);
        modelBuilder.Entity<AuditCorrection>().HasQueryFilter(e => e.HouseholdId == CurrentHouseholdId);
        modelBuilder.Entity<Tariff>().HasQueryFilter(e => e.HouseholdId == CurrentHouseholdId);
        modelBuilder.Entity<Event>().HasQueryFilter(e => e.HouseholdId == CurrentHouseholdId);
    }
}
