using Microsoft.EntityFrameworkCore;

namespace BulkWriteThroughputSpike;

public enum SpikeProvider
{
    Postgres,
    SqlServer,
}

// Config-driven provider selection, one shared context — deliberately echoes AD-2's shape (one
// DbContext, provider chosen once) even though AD-1/AD-2 don't bind this throwaway harness. No
// EF migrations are used: schema is created/dropped via raw SQL in SchemaSql.cs so the exact
// index syntax proven in the real migrations can be reused verbatim.
public class SpikeDbContext : DbContext
{
    public SpikeProvider Provider { get; }
    public string ConnectionString { get; }

    public SpikeDbContext(SpikeProvider provider, string connectionString)
    {
        Provider = provider;
        ConnectionString = connectionString;
    }

    public DbSet<SpikeSmartPlugImport> SpikeSmartPlugImports => Set<SpikeSmartPlugImport>();
    public DbSet<SpikeSmartPlugReading> SpikeSmartPlugReadings => Set<SpikeSmartPlugReading>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // SPIKE FINDING: Azure SQL Basic tier (5 DTU) did not complete a 120,000-row
        // BulkInsertOrUpdateAsync within EFCore.BulkExtensions' own default 30-second
        // SqlBulkCopy timeout (confirmed against the real instance — "Execution Timeout
        // Expired"). A long CommandTimeout here covers this context's own non-bulk commands
        // (schema DDL, truncate, parent-row SaveChangesAsync) which can be slow on Basic tier
        // too (Story 3.7's own precedent: minutes for a large join). BulkCopyTimeout itself is
        // set separately per call in Scenarios.BaseConfig() — SqlBulkCopy has its own timeout,
        // independent of the DbContext's CommandTimeout.
        switch (Provider)
        {
            case SpikeProvider.SqlServer:
                optionsBuilder.UseSqlServer(ConnectionString, o => o.CommandTimeout(1800));
                break;
            case SpikeProvider.Postgres:
                optionsBuilder.UseNpgsql(ConnectionString, o => o.CommandTimeout(1800));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Provider));
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SpikeSmartPlugImport>(b =>
        {
            b.ToTable("Spike_SmartPlugImport");
            b.HasKey(x => x.Id);
        });

        modelBuilder.Entity<SpikeSmartPlugReading>(b =>
        {
            b.ToTable("Spike_SmartPlugReading");
            b.HasKey(x => x.Id);

            b.Property(x => x.KwhValue).HasPrecision(18, 6);
            b.Property(x => x.RoomName).IsRequired().HasMaxLength(DataGenerator.RoomNameLength);
            b.Property(x => x.PowerPointName).IsRequired().HasMaxLength(DataGenerator.PowerPointNameLength);
            b.Property(x => x.DeviceName).IsRequired().HasMaxLength(DataGenerator.DeviceNameLength);

            // Real FK to the spike parent table (AC #2). Nullable + Restrict, mirroring
            // production's current SmartPlugReading.SmartPlugImportId shape.
            b.HasOne<SpikeSmartPlugImport>()
                .WithMany()
                .HasForeignKey(x => x.SmartPlugImportId)
                .OnDelete(DeleteBehavior.Restrict);

            // The two AD-23/AD-20 unique indexes are created via raw SQL in SchemaSql.cs (exact
            // per-provider syntax from the real migrations) rather than EF's HasIndex/HasFilter —
            // same reason SmartPlugReadingConfiguration.cs itself gives: the partial-filter SQL
            // text differs per provider and this model class is shared across both.
        });
    }
}
