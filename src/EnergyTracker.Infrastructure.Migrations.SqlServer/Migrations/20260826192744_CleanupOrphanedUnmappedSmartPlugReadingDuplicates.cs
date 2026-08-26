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
            migrationBuilder.Sql("""
                DELETE u
                FROM [SmartPlugReadings] u
                JOIN [SmartPlugReadings] m
                  ON m.[HouseholdId] = u.[HouseholdId]
                  AND CAST(m.[DeviceName] AS varbinary(900)) = CAST(u.[DeviceName] AS varbinary(900))
                  AND m.[IntervalStart] = u.[IntervalStart]
                  AND m.[IntervalEnd] = u.[IntervalEnd]
                  AND m.[KwhValue] = u.[KwhValue]
                WHERE u.[PowerPointId] IS NULL
                  AND m.[PowerPointId] IS NOT NULL;
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
