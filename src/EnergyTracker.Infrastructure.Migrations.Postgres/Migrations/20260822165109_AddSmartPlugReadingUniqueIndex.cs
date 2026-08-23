using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.Postgres.Migrations
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
                DELETE FROM "SmartPlugReadings" AS r
                USING (
                    SELECT sr."Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY sr."PowerPointId", sr."IntervalStart"
                               ORDER BY si."CreatedAtUtc" DESC, sr."Id" DESC
                           ) AS rn
                    FROM "SmartPlugReadings" sr
                    JOIN "SmartPlugImports" si ON si."Id" = sr."SmartPlugImportId"
                    WHERE sr."PowerPointId" IS NOT NULL
                ) AS dup
                WHERE r."Id" = dup."Id" AND dup.rn > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SmartPlugReadings_PowerPointId_IntervalStart",
                table: "SmartPlugReadings",
                columns: new[] { "PowerPointId", "IntervalStart" },
                unique: true);

            // Dev Notes Open Question #3 ("fix it now", confirmed with Ralf during dev-story
            // activation): the composite unique index above only rejects a duplicate when
            // PowerPointId is non-null on BOTH rows — Postgres never treats NULL as equal to
            // NULL for uniqueness, even in a composite key, so two AwaitingPowerPointMapping
            // rows (PowerPointId IS NULL) sharing an IntervalStart are never caught by it. Closed
            // here via a Postgres partial unique index scoped to exactly the rows the composite
            // index above excludes. Keyed by (HouseholdId, IntervalStart), not IntervalStart
            // alone — PowerPointId already carries Household scoping implicitly (a PowerPointId
            // belongs to exactly one Household) for the composite index above, but a NULL
            // PowerPointId loses that, so HouseholdId must be explicit here or two different
            // Households' unmapped readings sharing a timestamp would collide (caught empirically
            // via this story's own Api.Tests: two different Households both parsing the same Eve
            // Home sample file — identical timestamps by construction — deadlocked every
            // subsequent unmatched-import job once this index's first version, keyed on
            // IntervalStart alone, rejected the second Household's insert). First, the matching
            // one-time dedup cleanup for that same subset (mirrors the PowerPointId IS NOT NULL
            // cleanup above).
            migrationBuilder.Sql("""
                DELETE FROM "SmartPlugReadings" AS r
                USING (
                    SELECT sr."Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY sr."HouseholdId", sr."IntervalStart"
                               ORDER BY si."CreatedAtUtc" DESC, sr."Id" DESC
                           ) AS rn
                    FROM "SmartPlugReadings" sr
                    JOIN "SmartPlugImports" si ON si."Id" = sr."SmartPlugImportId"
                    WHERE sr."PowerPointId" IS NULL
                ) AS dup
                WHERE r."Id" = dup."Id" AND dup.rn > 1;
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_SmartPlugReadings_HouseholdId_IntervalStart_WhenPowerPointIdNull"
                ON "SmartPlugReadings" ("HouseholdId", "IntervalStart")
                WHERE "PowerPointId" IS NULL;
                """);
        }

        /// <inheritdoc />
        // Rolling back only drops the indexes above — it does NOT restore the rows Up()'s two
        // dedup DELETEs removed. Those deletions are irreversible; Down() exists to make the
        // schema rollback-able, not to undo the one-time cleanup.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_SmartPlugReadings_HouseholdId_IntervalStart_WhenPowerPointIdNull";
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
