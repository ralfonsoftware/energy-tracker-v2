using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class MainMeterConfiguration : IEntityTypeConfiguration<MainMeter>
{
    public void Configure(EntityTypeBuilder<MainMeter> builder)
    {
        builder.ToTable("MainMeters");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.CreatedAtUtc)
            .IsRequired();

        // Restrict, not Cascade — same AD-10 reasoning as Room/PowerPoint/Device's FK to Household.
        builder.HasOne<Household>()
            .WithMany()
            .HasForeignKey(m => m.HouseholdId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // Enforces the v2 single-Main-Meter-per-Household rule at the DB level (deferred.md), not
        // just via GetOrCreateMainMeterAsync's application-level get-or-create logic.
        builder.HasIndex(m => m.HouseholdId)
            .IsUnique();

        // AD-3's standard query filter is wired in EnergyTrackerDbContext.OnModelCreating.
    }
}
