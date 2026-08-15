using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class MeterReadingConfiguration : IEntityTypeConfiguration<MeterReading>
{
    public void Configure(EntityTypeBuilder<MeterReading> builder)
    {
        builder.ToTable("MeterReadings");

        builder.HasKey(r => r.Id);

        // Portable relational subset (AD-2) — explicit precision so Postgres and SQL Server store
        // identical precision/scale. Story 2.1's review found this mismatch the hard way.
        builder.Property(r => r.KwhValue)
            .HasPrecision(18, 2);

        builder.Property(r => r.ReadingTimestamp)
            .IsRequired();

        builder.Property(r => r.IdempotencyKey)
            .IsRequired();

        builder.Property(r => r.CreatedAtUtc)
            .IsRequired();

        // Restrict, not Cascade — same AD-10 reasoning as Room/PowerPoint/Device's FK to Household.
        builder.HasOne<Household>()
            .WithMany()
            .HasForeignKey(r => r.HouseholdId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MainMeter>()
            .WithMany()
            .HasForeignKey(r => r.MainMeterId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // AD-3's query filter runs on every MeterReading query — index the column it filters on.
        builder.HasIndex(r => r.HouseholdId);

        // AD-16: makes the upsert real, not just a check-then-act read — a retried insert with the
        // same key hits this constraint and is caught/re-queried as a no-op, never a duplicate row.
        builder.HasIndex(r => r.IdempotencyKey)
            .IsUnique();

        // AD-3's standard query filter is wired in EnergyTrackerDbContext.OnModelCreating.
    }
}
