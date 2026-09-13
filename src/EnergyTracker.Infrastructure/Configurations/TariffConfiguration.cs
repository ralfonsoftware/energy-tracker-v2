using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class TariffConfiguration : IEntityTypeConfiguration<Tariff>
{
    public void Configure(EntityTypeBuilder<Tariff> builder)
    {
        builder.ToTable("Tariffs");

        builder.HasKey(t => t.Id);

        // Matches the mockup's 2-decimal base-fee display (e.g. €12.50).
        builder.Property(t => t.MonthlyBaseFee)
            .HasPrecision(18, 2);

        // Deliberately (18, 4), not the (18, 2) every other decimal column in this codebase uses —
        // the mockup shows 4-decimal-place tariff pricing (€0.3200) that (18, 2) would truncate.
        builder.Property(t => t.PricePerKwh)
            .HasPrecision(18, 4);

        builder.Property(t => t.Currency)
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(t => t.ContractStartDate)
            .IsRequired();

        builder.Property(t => t.CreatedAtUtc)
            .IsRequired();

        builder.Property(t => t.Version)
            .IsConcurrencyToken();

        // Restrict, not Cascade — same AD-10-adjacent reasoning MeterReadingConfiguration uses.
        builder.HasOne<Household>()
            .WithMany()
            .HasForeignKey(t => t.HouseholdId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // AD-3's query filter runs on every Tariff query.
        builder.HasIndex(t => t.HouseholdId);

        // Backs the history-ordering query and the "current Tariff" lookup (latest
        // ContractStartDate <= now).
        builder.HasIndex(t => new { t.HouseholdId, t.ContractStartDate });

        // AD-3's standard query filter is wired in EnergyTrackerDbContext.OnModelCreating.
    }
}
