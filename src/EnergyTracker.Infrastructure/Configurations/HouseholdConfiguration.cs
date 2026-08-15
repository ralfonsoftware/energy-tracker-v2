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

        // Portable relational subset (AD-2) — explicit precision so Postgres (default unbounded
        // "numeric") and SQL Server (default "decimal(18,2)") store identical precision/scale
        // instead of silently diverging per provider.
        builder.Property(h => h.YearlyBaselineKwh)
            .HasPrecision(18, 2);

        builder.Property(h => h.Version)
            .IsConcurrencyToken();

        // Household is the tenant root, not Household-scoped data — no AD-3 HasQueryFilter here;
        // there is no HouseholdId on Household to filter by.
        builder.HasMany(h => h.Members)
            .WithOne()
            .HasForeignKey(m => m.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
