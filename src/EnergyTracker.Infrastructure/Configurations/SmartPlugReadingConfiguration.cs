using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class SmartPlugReadingConfiguration : IEntityTypeConfiguration<SmartPlugReading>
{
    public void Configure(EntityTypeBuilder<SmartPlugReading> builder)
    {
        builder.ToTable("SmartPlugReadings");

        builder.HasKey(r => r.Id);

        // Portable relational subset (AD-2). Scale 2 (MeterReading.KwhValue's discipline) is wrong
        // here: Eve Home readings are Wh/1000 (e.g. 0.00082) and even Meross's coarser daily
        // aggregates carry 3 fractional digits (e.g. 1.492) — scale 2 would round both to 0.00/1.49
        // on persistence. Scale 6 preserves Eve Home's full fractional precision with margin.
        builder.Property(r => r.KwhValue)
            .HasPrecision(18, 6);

        builder.Property(r => r.RoomName)
            .IsRequired();

        builder.Property(r => r.PowerPointName)
            .IsRequired();

        builder.Property(r => r.DeviceName)
            .IsRequired();

        // Restrict, not Cascade — same AD-10 reasoning as every other FK to Household.
        builder.HasOne<Household>()
            .WithMany()
            .HasForeignKey(r => r.HouseholdId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SmartPlugImport>()
            .WithMany()
            .HasForeignKey(r => r.SmartPlugImportId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // Nullable — an unmatched reading (AwaitingPowerPointMapping) has no Power Point yet.
        // Restrict, not Cascade: Power Point rows are soft-deleted (ArchivedAt), never hard-
        // deleted (AD-10), so this FK never needs to react to a delete. RoomName/PowerPointName
        // above are the by-value snapshot AD-10 requires — this FK exists only for optional
        // future filtering, never for re-deriving the display fields via a join.
        builder.HasOne<PowerPoint>()
            .WithMany()
            .HasForeignKey(r => r.PowerPointId)
            .OnDelete(DeleteBehavior.Restrict);

        // AD-3's query filter runs on every SmartPlugReading query — index the column it filters on.
        builder.HasIndex(r => r.HouseholdId);

        // Story 3.2/3.3 will read "all readings for one import" repeatedly.
        builder.HasIndex(r => r.SmartPlugImportId);

        // AD-3's standard query filter is wired in EnergyTrackerDbContext.OnModelCreating.
    }
}
