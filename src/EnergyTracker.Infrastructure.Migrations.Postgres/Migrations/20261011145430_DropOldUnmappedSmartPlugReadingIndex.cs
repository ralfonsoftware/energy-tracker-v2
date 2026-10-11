using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.Postgres.Migrations
{
    // Story 11.4, AD-20 (amended 2026-10-10), audit C9. DEPLOY N+1: the contract half of the expand/contract
    // pair. Deploy N (NarrowSmartPlugReadingDeviceNameKeyUnmappedByDevice) added the
    // (HouseholdId, DeviceName, IntervalStart) WHERE PowerPointId IS NULL index and switched the code to it,
    // keeping the old (HouseholdId, IntervalStart) WHERE PowerPointId IS NULL index so the previous image and
    // a rollback still had their ON CONFLICT / MERGE arbiter. Deploy N is verified in production, so the old
    // index, which rejects a second plug's overlapping unmapped rows, is dropped here.
    //
    // No model change: the filtered index is raw SQL only (SmartPlugReadingConfiguration), so the snapshot
    // is unchanged. After this migration a rollback to an image OLDER than deploy N is no longer possible by
    // image redeploy alone (its ON CONFLICT arbiter is gone); that case is the Story 10.1 restore-point
    // procedure (infra/README.md "Rolling back a bad migration"). See invariants-rules.md#AD-20.
    //
    /// <inheritdoc />
    public partial class DropOldUnmappedSmartPlugReadingIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_SmartPlugReadings_HouseholdId_IntervalStart_WhenPowerPointIdNull";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback is the restore-point procedure (Story 10.1), not Down(). This can fail if two
            // devices now share an interval among the unmapped readings.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_SmartPlugReadings_HouseholdId_IntervalStart_WhenPowerPointIdNull"
                ON "SmartPlugReadings" ("HouseholdId", "IntervalStart")
                WHERE "PowerPointId" IS NULL;
                """);
        }
    }
}
