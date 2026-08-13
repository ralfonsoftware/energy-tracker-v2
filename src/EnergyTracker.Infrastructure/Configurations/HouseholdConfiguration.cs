using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class HouseholdConfiguration : IEntityTypeConfiguration<Household>
{
    public void Configure(EntityTypeBuilder<Household> builder)
    {
        builder.ToTable("Households");

        builder.HasKey(h => h.Id);

        builder.Property(h => h.Locale)
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(h => h.Currency)
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(h => h.CreatedAtUtc)
            .IsRequired();

        // Household is the tenant root, not Household-scoped data — no AD-3 HasQueryFilter here;
        // there is no HouseholdId on Household to filter by.
        builder.HasMany(h => h.Members)
            .WithOne()
            .HasForeignKey(m => m.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
