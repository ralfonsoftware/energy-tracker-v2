using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    // Story 3.7 AC #3: one-time cleanup of SmartPlugReading rows left permanently orphaned by
    // UpdateMappingPerRowWithConflictToleranceAsync's pre-this-story behavior (Story 3.4 Dev
    // Notes Open Question #4's AD-20 gap) — an unmapped reading that was skipped because it
    // collided with an already-mapped reading at the same (PowerPointId, IntervalStart), leaving
    // an exact duplicate behind with PowerPointId still NULL. Confirmed live in production
    // (2026-08-26 audit) at 179,324-row scale across two Power Points in one household. Pure DML
    // — no schema change, unlike 20260822165112_AddSmartPlugReadingUniqueIndex which paired its
    // cleanup with a new index.
    public partial class CleanupOrphanedUnmappedSmartPlugReadingDuplicates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Perf fix (production incident 2026-08-26): the join below has no supporting index —
            // IX_SmartPlugReadings_HouseholdId alone means m's candidate set is the *entire*
            // household's mapped rows per unmapped row, not just the matching IntervalStart. At
            // the confirmed 179,324-row production scale, that unindexed nested loop timed out
            // both the 300s SQL command timeout and the 10-minute app-deploy.yml step timeout
            // against Basic-tier Azure SQL (5 DTU). This temporary index narrows the per-row
            // candidate set to an index seek; it's dropped immediately after the DELETE since it
            // exists only to make this one-time cleanup affordable, not as a lasting index — this
            // migration is still pure DML, no persisted schema change.
            migrationBuilder.Sql("""
                CREATE NONCLUSTERED INDEX [IX_Temp_SmartPlugReadings_CleanupOrphanedDuplicates]
                ON [SmartPlugReadings] ([HouseholdId], [IntervalStart])
                INCLUDE ([DeviceName], [IntervalEnd], [KwhValue])
                WHERE [PowerPointId] IS NOT NULL;
                """);

            // Deletes only unmapped rows (PowerPointId IS NULL) that have an exact mapped twin —
            // same HouseholdId/DeviceName/IntervalStart/IntervalEnd/KwhValue, differing only in
            // PowerPointId/RoomName/PowerPointName/SmartPlugImportId. An unmapped row with no such
            // twin (e.g. a device tag still genuinely AwaitingPowerPointMapping) is untouched.
            // DeviceName compares as CAST(... AS varbinary) rather than plain `=` — review finding
            // (Edge Case Hunter): SQL Server's default collation is typically case-insensitive
            // (unlike Postgres's byte-exact `=` on text), so a plain `=` here could delete a
            // different set of rows than the identically-worded Postgres migration for
            // case-varying DeviceName data. The varbinary cast forces byte-exact comparison on
            // both engines without depending on knowing this server's configured collation name.
            // HouseholdId/IntervalStart lead the join (matching the temp index's key order) so the
            // optimizer seeks rather than scans; DeviceName/IntervalEnd/KwhValue are residual
            // filters served straight from the index's INCLUDE columns.
            migrationBuilder.Sql("""
                DELETE u
                FROM [SmartPlugReadings] u
                JOIN [SmartPlugReadings] m
                  ON m.[HouseholdId] = u.[HouseholdId]
                  AND m.[IntervalStart] = u.[IntervalStart]
                  AND CAST(m.[DeviceName] AS varbinary(900)) = CAST(u.[DeviceName] AS varbinary(900))
                  AND m.[IntervalEnd] = u.[IntervalEnd]
                  AND m.[KwhValue] = u.[KwhValue]
                WHERE u.[PowerPointId] IS NULL
                  AND m.[PowerPointId] IS NOT NULL;
                """);

            migrationBuilder.Sql("""
                DROP INDEX [IX_Temp_SmartPlugReadings_CleanupOrphanedDuplicates] ON [SmartPlugReadings];
                """);
        }

        /// <inheritdoc />
        // This cleanup's deletions are irreversible — Down() intentionally does not attempt to
        // restore the deleted rows (same precedent as 20260822165112_AddSmartPlugReadingUniqueIndex's
        // Down()). There is no schema change to roll back either, so this is a no-op.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
