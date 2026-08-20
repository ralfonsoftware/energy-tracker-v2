using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class SmartPlugImportConfiguration : IEntityTypeConfiguration<SmartPlugImport>
{
    public void Configure(EntityTypeBuilder<SmartPlugImport> builder)
    {
        builder.ToTable("SmartPlugImports");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.OriginalFileName)
            .IsRequired();

        builder.Property(i => i.DeviceTag)
            .IsRequired();

        builder.Property(i => i.CreatedAtUtc)
            .IsRequired();

        // Restrict, not Cascade — same AD-10 reasoning as every other FK to Household.
        builder.HasOne<Household>()
            .WithMany()
            .HasForeignKey(i => i.HouseholdId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict, not Cascade — a BackgroundJob row is never deleted while an import still
        // references it.
        builder.HasOne<BackgroundJob>()
            .WithMany()
            .HasForeignKey(i => i.BackgroundJobId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // AD-3's query filter runs on every SmartPlugImport query — index the column it filters on.
        builder.HasIndex(i => i.HouseholdId);

        // AD-3's standard query filter is wired in EnergyTrackerDbContext.OnModelCreating.
    }
}
