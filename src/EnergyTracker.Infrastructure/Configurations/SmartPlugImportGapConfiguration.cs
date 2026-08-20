using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class SmartPlugImportGapConfiguration : IEntityTypeConfiguration<SmartPlugImportGap>
{
    public void Configure(EntityTypeBuilder<SmartPlugImportGap> builder)
    {
        builder.ToTable("SmartPlugImportGaps");

        builder.HasKey(g => g.Id);

        // Matches SmartPlugReading.KwhValue's scale (SmartPlugReadingConfiguration.cs), not
        // MeterReading's scale-2 — Eve Home's Wh/1000 figures need more than 2 decimal places.
        builder.Property(g => g.EstimatedTotalKwh)
            .HasPrecision(18, 6);

        builder.Property(g => g.CreatedAtUtc)
            .IsRequired();

        // Restrict, not Cascade — same AD-10 reasoning as every other FK to Household.
        builder.HasOne<Household>()
            .WithMany()
            .HasForeignKey(g => g.HouseholdId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SmartPlugImport>()
            .WithMany()
            .HasForeignKey(g => g.SmartPlugImportId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // Nullable — the AC #7 whole-file FlaggedForReview case never resolves a Power Point.
        builder.HasOne<PowerPoint>()
            .WithMany()
            .HasForeignKey(g => g.PowerPointId)
            .OnDelete(DeleteBehavior.Restrict);

        // AD-3's query filter runs on every SmartPlugImportGap query — index the column it
        // filters on.
        builder.HasIndex(g => g.HouseholdId);

        // GetBackgroundJobStatus loads "all gaps for one import" every time an import's status is
        // polled (Task 5).
        builder.HasIndex(g => g.SmartPlugImportId);

        // AD-3's standard query filter is wired in EnergyTrackerDbContext.OnModelCreating.
    }
}
