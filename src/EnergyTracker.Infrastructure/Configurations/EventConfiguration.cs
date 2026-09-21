using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("Events");

        builder.HasKey(e => e.Id);

        // TaggingScaffoldNameValidator.MaxNameLength (200) is sized for a short entity Name, not a
        // free-text note like "away for 2 weeks visiting family, cooked almost nothing at home the
        // whole time" — 500 is a generous, round cap with no other significance.
        builder.Property(e => e.Description)
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(e => e.OccurredAt)
            .IsRequired();

        builder.Property(e => e.CreatedAtUtc)
            .IsRequired();

        // Matches AuditCorrectionConfiguration's discriminator column.
        builder.Property(e => e.TaggedEntityType)
            .HasMaxLength(64);

        builder.Property(e => e.TaggedEntityName)
            .HasMaxLength(200);

        // "Bump"|"Dip" — plenty of headroom without pinning to the exact enum member length.
        builder.Property(e => e.CorrelationDirection)
            .HasMaxLength(16);

        // Restrict, not Cascade — same AD-10 reasoning as MeterReading's FK to Household.
        builder.HasOne<Household>()
            .WithMany()
            .HasForeignKey(e => e.HouseholdId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // AD-3's query filter runs on every Event query. The composite leads with HouseholdId (the
        // filtered column) and trails with OccurredAt + CreatedAtUtc (GetPageForHouseholdAsync's
        // sort), so the read path is fully index-ordered on both providers; the single-column index
        // this replaces is now strictly redundant.
        builder.HasIndex(e => new { e.HouseholdId, e.OccurredAt, e.CreatedAtUtc });

        // No FK from TaggedEntityId to Room/PowerPoint/Device — it's polymorphic across the three
        // discriminated types, so a real FK constraint can only ever target one table; this is a
        // deliberate omission, matching AuditCorrectionConfiguration's EntityId precedent.

        // AD-3's standard query filter is wired in EnergyTrackerDbContext.OnModelCreating.
    }
}
