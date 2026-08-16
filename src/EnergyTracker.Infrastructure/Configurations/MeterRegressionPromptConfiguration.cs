using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class MeterRegressionPromptConfiguration : IEntityTypeConfiguration<MeterRegressionPrompt>
{
    public void Configure(EntityTypeBuilder<MeterRegressionPrompt> builder)
    {
        builder.ToTable("MeterRegressionPrompts");

        builder.HasKey(p => p.Id);

        // Portable relational subset (AD-2), same precision discipline as MeterReading.KwhValue.
        builder.Property(p => p.DigitCapacityKwh)
            .HasPrecision(18, 2);

        builder.Property(p => p.CreatedAtUtc)
            .IsRequired();

        // Restrict, not Cascade — same AD-10 reasoning as Room/PowerPoint/Device's FK to Household.
        builder.HasOne<Household>()
            .WithMany()
            .HasForeignKey(p => p.HouseholdId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MainMeter>()
            .WithMany()
            .HasForeignKey(p => p.MainMeterId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // Two distinct FKs to MeterReading — explicit constraint names since EF's auto-naming
        // would otherwise collide on the shared target table.
        builder.HasOne<MeterReading>()
            .WithMany()
            .HasForeignKey(p => p.MeterReadingId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_MeterRegressionPrompts_MeterReadings_MeterReadingId");

        builder.HasOne<MeterReading>()
            .WithMany()
            .HasForeignKey(p => p.PreviousMeterReadingId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_MeterRegressionPrompts_MeterReadings_PreviousMeterReadingId");

        // AD-3's query filter runs on every MeterRegressionPrompt query — index the column it filters on.
        builder.HasIndex(p => p.HouseholdId);

        // Race-safety guard (mirrors MeterReading.IdempotencyKey's unique index): two concurrent
        // requests reaching the regression-detection step for the same winning reading must not
        // both succeed in inserting a prompt for it.
        builder.HasIndex(p => p.MeterReadingId)
            .IsUnique();

        // AD-3's standard query filter is wired in EnergyTrackerDbContext.OnModelCreating.
    }
}
