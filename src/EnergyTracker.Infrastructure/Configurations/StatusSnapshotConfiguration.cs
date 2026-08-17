using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class StatusSnapshotConfiguration : IEntityTypeConfiguration<StatusSnapshot>
{
    public void Configure(EntityTypeBuilder<StatusSnapshot> builder)
    {
        builder.ToTable("StatusSnapshots");

        builder.HasKey(s => s.Id);

        // Portable relational subset (AD-2) — explicit precision so Postgres and SQL Server store
        // identical precision/scale, same discipline as MeterReading.KwhValue.
        builder.Property(s => s.PaceToDateKwh)
            .HasPrecision(18, 2);

        builder.Property(s => s.BaselineToDateKwh)
            .HasPrecision(18, 2);

        builder.Property(s => s.ComputedAtUtc)
            .IsRequired();

        // Restrict, not Cascade — same AD-10 reasoning as every other FK to Household in this
        // codebase.
        builder.HasOne<Household>()
            .WithMany()
            .HasForeignKey(s => s.HouseholdId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // AD-3's query filter runs on every StatusSnapshot query — index the column it filters on.
        builder.HasIndex(s => s.HouseholdId);

        // No unique index — multiple snapshots per Household over time are expected (one per
        // recompute).

        // AD-3's standard query filter is wired in EnergyTrackerDbContext.OnModelCreating.
    }
}
