using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    // Story 3.7 AC #3: one-time cleanup of SmartPlugReading rows left permanently orphaned by
    // UpdateMappingPerRowWithConflictToleranceAsync's pre-this-story behavior (Story 3.4 Dev
    // Notes Open Question #4's AD-20 gap) — an unmapped reading that was skipped because it
    // collided with an already-mapped reading at the same (PowerPointId, IntervalStart), leaving
    // an exact duplicate behind with PowerPointId still NULL. Confirmed live in production
    // (2026-08-26 audit) at 179,324-row scale across two Power Points in one household. Pure DML
    // — no schema change, unlike 20260822165109_AddSmartPlugReadingUniqueIndex which paired its
    // cleanup with a new index.
    public partial class CleanupOrphanedUnmappedSmartPlugReadingDuplicates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Perf fix (production incident 2026-08-26, SQL Server side — see the SqlServer
            // migrations project's identically-named migration for the full incident writeup):
            // the join below has no supporting index — IX_SmartPlugReadings_HouseholdId alone
            // means m's candidate set is the entire household's mapped rows per unmapped row, not
            // just the matching IntervalStart. Mirrored here for AD-2 parity even though this
            // provider's path didn't fail in CI, since the same unindexed join would hit the same
            // wall on a large enough self-hosted install. Temporary — dropped immediately after
            // the DELETE, so this migration is still pure DML, no persisted schema change.
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Temp_SmartPlugReadings_CleanupOrphanedDuplicates"
                ON "SmartPlugReadings" ("HouseholdId", "IntervalStart")
                INCLUDE ("DeviceName", "IntervalEnd", "KwhValue")
                WHERE "PowerPointId" IS NOT NULL;
                """);

            // Deletes only unmapped rows (PowerPointId IS NULL) that have an exact mapped twin —
            // same HouseholdId/DeviceName/IntervalStart/IntervalEnd/KwhValue, differing only in
            // PowerPointId/RoomName/PowerPointName/SmartPlugImportId. An unmapped row with no such
            // twin (e.g. a device tag still genuinely AwaitingPowerPointMapping) is untouched.
            // HouseholdId/IntervalStart lead the join (matching the temp index's key order) so the
            // optimizer seeks rather than scans.
            migrationBuilder.Sql("""
                DELETE FROM "SmartPlugReadings" AS u
                USING "SmartPlugReadings" AS m
                WHERE u."PowerPointId" IS NULL
                  AND m."PowerPointId" IS NOT NULL
                  AND m."HouseholdId" = u."HouseholdId"
                  AND m."IntervalStart" = u."IntervalStart"
                  AND m."DeviceName" = u."DeviceName"
                  AND m."IntervalEnd" = u."IntervalEnd"
                  AND m."KwhValue" = u."KwhValue";
                """);

            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_Temp_SmartPlugReadings_CleanupOrphanedDuplicates";
                """);
        }

        /// <inheritdoc />
        // This cleanup's deletions are irreversible — Down() intentionally does not attempt to
        // restore the deleted rows (same precedent as 20260822165109_AddSmartPlugReadingUniqueIndex's
        // Down()). There is no schema change to roll back either, so this is a no-op.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
