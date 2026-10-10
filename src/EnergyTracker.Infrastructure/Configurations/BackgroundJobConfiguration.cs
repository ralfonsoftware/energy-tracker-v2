using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class BackgroundJobConfiguration : IEntityTypeConfiguration<BackgroundJob>
{
    public void Configure(EntityTypeBuilder<BackgroundJob> builder)
    {
        builder.ToTable("BackgroundJobs");

        builder.HasKey(j => j.Id);

        builder.Property(j => j.JobType)
            .IsRequired();

        builder.Property(j => j.CreatedAtUtc)
            .IsRequired();

        // Restrict, not Cascade — same AD-10 reasoning as every other FK to Household in this
        // codebase.
        builder.HasOne<Household>()
            .WithMany()
            .HasForeignKey(j => j.HouseholdId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // Optional FK — no .IsRequired(), unlike every other FK to Household in this file.
        // SetNull, not Restrict (changed in Story 7.2, when restore still deleted and reinserted
        // HouseholdMember rows). Restore no longer touches members at all (AD-25, Story 11.1), but
        // this stays SetNull: member removal (Story 11.9) will delete members while BackgroundJob
        // rows (never deleted by it, per docs/data-import-restore.md) still reference them, and
        // Restrict would make that removal fail with a foreign-key violation. SetNull
        // mirrors SmartPlugReading.SmartPlugImportId's own precedent: job/reading history survives
        // independently of the row it was attributed to at creation time — QueuedByDisplayName's
        // own "null renders a generic fallback, never fabricate a name" convention (UX-DR21)
        // already assumes this field can legitimately be null.
        builder.HasOne<HouseholdMember>()
            .WithMany()
            .HasForeignKey(j => j.QueuedByHouseholdMemberId)
            .OnDelete(DeleteBehavior.SetNull);

        // AD-3's query filter runs on every BackgroundJob query — index the column it filters on.
        builder.HasIndex(j => j.HouseholdId);

        // Review-round-2 patch: covers ListByJobTypeAsync (filters HouseholdId+JobType, orders by
        // CreatedAtUtc) and SweepExpiredAsync's eligibility query (filters HouseholdId+JobType,
        // reads CompletedAtUtc) — both are hot paths hit on every Smart Plug Import screen open,
        // against a table this story's own Dev Notes call capable of holding hundreds of
        // thousands of rows.
        builder.HasIndex(j => new { j.HouseholdId, j.JobType, j.CreatedAtUtc });

        // AD-3's standard query filter is wired in EnergyTrackerDbContext.OnModelCreating.
    }
}
