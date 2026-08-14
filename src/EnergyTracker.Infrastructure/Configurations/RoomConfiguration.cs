using EnergyTracker.Application;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class RoomConfiguration : IEntityTypeConfiguration<Room>
{
    public void Configure(EntityTypeBuilder<Room> builder)
    {
        builder.ToTable("Rooms");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Name)
            .HasMaxLength(TaggingScaffoldNameValidator.MaxNameLength)
            .IsRequired();

        builder.Property(r => r.CreatedAtUtc)
            .IsRequired();

        // Restrict, not Cascade — same AD-10 reasoning as PowerPoint's FK to Room: a Household
        // hard-delete must never silently cascade into its Rooms.
        builder.HasOne<Household>()
            .WithMany()
            .HasForeignKey(r => r.HouseholdId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // AD-3's query filter runs on every Room query — index the column it filters on.
        builder.HasIndex(r => r.HouseholdId);

        // No two Rooms in the same Household may share a Name (Review finding — no AC addressed
        // this, decided 2026-08-14 to enforce it). Scoped to HouseholdId, not filtered to active
        // rows only, so the index stays provider-agnostic (no raw-SQL filter predicate needed
        // across both the Postgres and SqlServer migration projects).
        builder.HasIndex(r => new { r.HouseholdId, r.Name })
            .IsUnique();

        // AD-3's standard query filter is wired in EnergyTrackerDbContext.OnModelCreating — it
        // needs a per-request ICurrentHouseholdAccessor instance the static Configure signature
        // here doesn't receive.
    }
}
