using EnergyTracker.Application;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class PowerPointConfiguration : IEntityTypeConfiguration<PowerPoint>
{
    public void Configure(EntityTypeBuilder<PowerPoint> builder)
    {
        builder.ToTable("PowerPoints");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name)
            .HasMaxLength(TaggingScaffoldNameValidator.MaxNameLength)
            .IsRequired();

        builder.Property(p => p.CreatedAtUtc)
            .IsRequired();

        // Restrict, not Cascade — AD-10 requires Room deletion to be soft (ArchivedAt), so the FK
        // must never let a hard-delete cascade even accidentally; Restrict makes a hard-delete
        // attempt fail loudly instead of silently cascading.
        builder.HasOne<Room>()
            .WithMany()
            .HasForeignKey(p => p.RoomId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // Same Restrict reasoning, one level up — a Household hard-delete must never cascade.
        builder.HasOne<Household>()
            .WithMany()
            .HasForeignKey(p => p.HouseholdId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // AD-3's query filter runs on every PowerPoint query — index the column it filters on.
        builder.HasIndex(p => p.HouseholdId);

        // No two Power Points under the same Room may share a Name (Review finding, decided
        // 2026-08-14). See RoomConfiguration for why this isn't filtered to active rows only.
        builder.HasIndex(p => new { p.RoomId, p.Name })
            .IsUnique();

        // AD-3's standard query filter is wired in EnergyTrackerDbContext.OnModelCreating.
    }
}
