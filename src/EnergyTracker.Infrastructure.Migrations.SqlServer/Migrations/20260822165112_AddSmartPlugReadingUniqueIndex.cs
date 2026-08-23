using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddSmartPlugReadingUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SmartPlugReadings_PowerPointId",
                table: "SmartPlugReadings");

            // Story 3.4 AC #8/#9: one-time dedup cleanup — runs BEFORE either unique index below
            // is created, so index creation never fails against data already duplicated by
            // pre-this-story imports. Scoped to PowerPointId IS NOT NULL (mapped readings) —
            // AwaitingPowerPointMapping rows aren't meaningfully comparable duplicates by this
            // key, cleaned up separately below. "Most recently-created import wins" (AC #9) —
            // SmartPlugReading has no CreatedAtUtc of its own, so this joins to SmartPlugImports.
            migrationBuilder.Sql("""
                ;WITH Duplicates AS (
                    SELECT r.[Id],
                           ROW_NUMBER() OVER (
                               PARTITION BY r.[PowerPointId], r.[IntervalStart]
                               ORDER BY i.[CreatedAtUtc] DESC, r.[Id] DESC
                           ) AS rn
                    FROM [SmartPlugReadings] r
                    JOIN [SmartPlugImports] i ON i.[Id] = r.[SmartPlugImportId]
                    WHERE r.[PowerPointId] IS NOT NULL
                )
                DELETE FROM [SmartPlugReadings]
                WHERE [Id] IN (SELECT [Id] FROM Duplicates WHERE rn > 1);
                """);

            // EF Core's SqlServer provider automatically filters a unique index over a nullable
            // column to `WHERE [PowerPointId] IS NOT NULL` (this migration's own scaffolded
            // `filter:` argument below) — so, contrary to Dev Notes Open Question #3's assumption
            // that SQL Server's raw composite-unique-index behavior already protects
            // AwaitingPowerPointMapping rows for free, it does NOT: this index excludes
            // PowerPointId IS NULL rows on both providers identically. Verified empirically via
            // this project's own `dotnet ef migrations add` output, not just documentation — the
            // gap is symmetric across providers, not Postgres-only. Closed below with the same
            // filtered-index approach on both providers.
            migrationBuilder.CreateIndex(
                name: "IX_SmartPlugReadings_PowerPointId_IntervalStart",
                table: "SmartPlugReadings",
                columns: new[] { "PowerPointId", "IntervalStart" },
                unique: true,
                filter: "[PowerPointId] IS NOT NULL");

            // Dev Notes Open Question #3 ("fix it now", confirmed with Ralf during dev-story
            // activation): closes the gap above via a second unique index scoped to exactly the
            // rows the composite index excludes. Keyed by (HouseholdId, IntervalStart), not
            // IntervalStart alone — PowerPointId already carries Household scoping implicitly (a
            // PowerPointId belongs to exactly one Household) for the composite index above, but a
            // NULL PowerPointId loses that, so HouseholdId must be explicit here or two different
            // Households' unmapped readings sharing a timestamp would collide (caught empirically
            // via this story's own Api.Tests: two different Households both parsing the same Eve
            // Home sample file — identical timestamps by construction — deadlocked every
            // subsequent unmatched-import job once this index's first version, keyed on
            // IntervalStart alone, rejected the second Household's insert). First, the matching
            // one-time dedup cleanup for that same subset (mirrors the PowerPointId IS NOT NULL
            // cleanup above).
            migrationBuilder.Sql("""
                ;WITH DuplicatesNull AS (
                    SELECT r.[Id],
                           ROW_NUMBER() OVER (
                               PARTITION BY r.[HouseholdId], r.[IntervalStart]
                               ORDER BY i.[CreatedAtUtc] DESC, r.[Id] DESC
                           ) AS rn
                    FROM [SmartPlugReadings] r
                    JOIN [SmartPlugImports] i ON i.[Id] = r.[SmartPlugImportId]
                    WHERE r.[PowerPointId] IS NULL
                )
                DELETE FROM [SmartPlugReadings]
                WHERE [Id] IN (SELECT [Id] FROM DuplicatesNull WHERE rn > 1);
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE NONCLUSTERED INDEX [IX_SmartPlugReadings_HouseholdId_IntervalStart_WhenPowerPointIdNull]
                ON [SmartPlugReadings] ([HouseholdId], [IntervalStart])
                WHERE [PowerPointId] IS NULL;
                """);
        }

        /// <inheritdoc />
        // Rolling back only drops the indexes above — it does NOT restore the rows Up()'s two
        // dedup DELETEs removed. Those deletions are irreversible; Down() exists to make the
        // schema rollback-able, not to undo the one-time cleanup.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX [IX_SmartPlugReadings_HouseholdId_IntervalStart_WhenPowerPointIdNull] ON [SmartPlugReadings];
                """);

            migrationBuilder.DropIndex(
                name: "IX_SmartPlugReadings_PowerPointId_IntervalStart",
                table: "SmartPlugReadings");

            migrationBuilder.CreateIndex(
                name: "IX_SmartPlugReadings_PowerPointId",
                table: "SmartPlugReadings",
                column: "PowerPointId");
        }
    }
}
