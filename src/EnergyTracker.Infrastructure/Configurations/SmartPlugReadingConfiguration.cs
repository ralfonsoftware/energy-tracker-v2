using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class SmartPlugReadingConfiguration : IEntityTypeConfiguration<SmartPlugReading>
{
    public void Configure(EntityTypeBuilder<SmartPlugReading> builder)
    {
        builder.ToTable("SmartPlugReadings");

        builder.HasKey(r => r.Id);

        // Portable relational subset (AD-2). Scale 2 (MeterReading.KwhValue's discipline) is wrong
        // here: Eve Home readings are Wh/1000 (e.g. 0.00082) and even Meross's coarser daily
        // aggregates carry 3 fractional digits (e.g. 1.492) — scale 2 would round both to 0.00/1.49
        // on persistence. Scale 6 preserves Eve Home's full fractional precision with margin.
        builder.Property(r => r.KwhValue)
            .HasPrecision(18, 6);

        builder.Property(r => r.RoomName)
            .IsRequired();

        builder.Property(r => r.PowerPointName)
            .IsRequired();

        builder.Property(r => r.DeviceName)
            .IsRequired();

        // Restrict, not Cascade — same AD-10 reasoning as every other FK to Household.
        builder.HasOne<Household>()
            .WithMany()
            .HasForeignKey(r => r.HouseholdId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SmartPlugImport>()
            .WithMany()
            .HasForeignKey(r => r.SmartPlugImportId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // Nullable — an unmatched reading (AwaitingPowerPointMapping) has no Power Point yet.
        // Restrict, not Cascade: Power Point rows are soft-deleted (ArchivedAt), never hard-
        // deleted (AD-10), so this FK never needs to react to a delete. RoomName/PowerPointName
        // above are the by-value snapshot AD-10 requires — this FK exists only for optional
        // future filtering, never for re-deriving the display fields via a join.
        builder.HasOne<PowerPoint>()
            .WithMany()
            .HasForeignKey(r => r.PowerPointId)
            .OnDelete(DeleteBehavior.Restrict);

        // AD-3's query filter runs on every SmartPlugReading query — index the column it filters on.
        builder.HasIndex(r => r.HouseholdId);

        // Story 3.2/3.3 will read "all readings for one import" repeatedly.
        builder.HasIndex(r => r.SmartPlugImportId);

        // AD-20/Story 3.4 AC #6: the DB-level guarantee against a duplicate SmartPlugReading row
        // — protects paths the application-layer watermark filter can't reach (e.g. a first-ever
        // parse racing a concurrent completion for the same Power Point). Also doubles as
        // FindLatestReadingIntervalStartByPowerPointAsync's and
        // FindFirstReadingDateByPowerPointAsync's query-optimization index (PowerPointId leading).
        // A NULL PowerPointId (AwaitingPowerPointMapping) is never caught by this index on either
        // provider — Postgres never treats NULL as equal to NULL in a composite unique index, and
        // EF Core's SqlServer provider auto-filters a unique index over a nullable column to
        // `WHERE [PowerPointId] IS NOT NULL` for the identical reason (verified empirically via
        // this project's own `dotnet ef migrations add` output, correcting Dev Notes Open
        // Question #3's original assumption that SQL Server protected this "for free"). Closed
        // instead by a second, hand-added `(HouseholdId, IntervalStart) WHERE PowerPointId IS
        // NULL` partial unique index in both providers' migrations.
        builder.HasIndex(r => new { r.PowerPointId, r.IntervalStart }).IsUnique();

        // Story 3.4: the `(HouseholdId, IntervalStart) WHERE PowerPointId IS NULL` unique index
        // that closes the gap above (see the block comment) is declared ONLY as raw SQL in each
        // provider's migration (Postgres 20260822165109/SqlServer 20260822165112's Up()) — NOT
        // here. A `HasFilter(...)` predicate is raw dialect SQL text (Postgres double-quoted
        // identifiers vs. SQL Server brackets), and this class is shared across both provider
        // migration projects (AD-2) with no provider check available at this configuration
        // scope — a single filter string here would be syntactically wrong for one provider.
        // EF's model is therefore intentionally unaware of this constraint; do not "fix" that by
        // adding a filtered index here without first solving the dual-provider syntax problem.

        // AD-3's standard query filter is wired in EnergyTrackerDbContext.OnModelCreating.
    }
}
