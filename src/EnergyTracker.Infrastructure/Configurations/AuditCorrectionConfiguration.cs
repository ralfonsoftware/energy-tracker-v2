using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class AuditCorrectionConfiguration : IEntityTypeConfiguration<AuditCorrection>
{
    public void Configure(EntityTypeBuilder<AuditCorrection> builder)
    {
        builder.ToTable("AuditCorrections");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.EntityType)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(a => a.FieldName)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(a => a.OldValue)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(a => a.NewValue)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(a => a.CorrectedAtUtc)
            .IsRequired();

        // AD-3's query filter runs on every AuditCorrection query — index the column it filters on.
        builder.HasIndex(a => a.HouseholdId);

        // The lookup path GetLatestForEntitiesAsync's batch query needs.
        builder.HasIndex(a => new { a.EntityType, a.EntityId });

        // No FK from EntityId to MeterReading — EntityId is polymorphic across future entity
        // types (Tariff, per AD-11's own binding list), so a real FK constraint can only ever
        // target one table; this is a deliberate omission, not a missed constraint.
    }
}
