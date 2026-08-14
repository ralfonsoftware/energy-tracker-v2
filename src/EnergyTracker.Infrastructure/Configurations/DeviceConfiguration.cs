using EnergyTracker.Application;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("Devices");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Name)
            .HasMaxLength(TaggingScaffoldNameValidator.MaxNameLength)
            .IsRequired();

        builder.Property(d => d.CreatedAtUtc)
            .IsRequired();

        // Restrict, not Cascade — same AD-10 reasoning as PowerPointConfiguration's FK to Room.
        builder.HasOne<PowerPoint>()
            .WithMany()
            .HasForeignKey(d => d.PowerPointId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // Same Restrict reasoning, one level up — a Household hard-delete must never cascade.
        builder.HasOne<Household>()
            .WithMany()
            .HasForeignKey(d => d.HouseholdId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // AD-3's query filter runs on every Device query — index the column it filters on.
        builder.HasIndex(d => d.HouseholdId);

        // No two Devices on the same Power Point may share a Name (Review finding, decided
        // 2026-08-14). See RoomConfiguration for why this isn't filtered to active rows only.
        builder.HasIndex(d => new { d.PowerPointId, d.Name })
            .IsUnique();

        // AD-3's standard query filter is wired in EnergyTrackerDbContext.OnModelCreating.
    }
}
