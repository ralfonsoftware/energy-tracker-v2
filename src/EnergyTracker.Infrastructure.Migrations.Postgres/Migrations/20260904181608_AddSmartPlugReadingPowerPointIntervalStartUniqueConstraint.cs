using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    // Story 3.9/AD-23 (post-implementation finding, not covered by Story 3.8's spike): promotes
    // the existing IX_SmartPlugReadings_PowerPointId_IntervalStart unique INDEX to a genuine
    // Postgres unique CONSTRAINT, without rebuilding it (ADD CONSTRAINT ... UNIQUE USING INDEX
    // reuses the index in place — no new index, no table scan). EFCore.BulkExtensions.PostgreSql's
    // own merge implementation only skips its temp-index-creation step (CREATE UNIQUE INDEX
    // CONCURRENTLY, which cannot run inside a transaction block) when it finds a genuine
    // pg_constraint entry for the match-key columns — a plain unique index doesn't satisfy that
    // check on its own, confirmed empirically against a real Postgres instance during Story 3.9's
    // AddAsync rewrite. Without this, AD-23's required single-transaction parent+child atomicity
    // for the primary [PowerPointId, IntervalStart] bulk-write path is unreachable on Postgres.
    // SQL Server is unaffected — MERGE has no equivalent restriction, so its own migration below
    // is a documented no-op.
    public partial class AddSmartPlugReadingPowerPointIntervalStartUniqueConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "SmartPlugReadings"
                ADD CONSTRAINT "IX_SmartPlugReadings_PowerPointId_IntervalStart"
                UNIQUE USING INDEX "IX_SmartPlugReadings_PowerPointId_IntervalStart";
                """);
        }

        /// <inheritdoc />
        // Dropping the constraint recreates a plain unique index under the same name (Postgres
        // does not let a unique index disappear entirely mid-rollback without either dropping it
        // outright or reverting to a plain index) — DROP CONSTRAINT alone would also drop the
        // backing index, silently losing AD-20's duplicate-safety guarantee on rollback.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "SmartPlugReadings"
                DROP CONSTRAINT "IX_SmartPlugReadings_PowerPointId_IntervalStart";
                """);
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_SmartPlugReadings_PowerPointId_IntervalStart"
                ON "SmartPlugReadings" ("PowerPointId", "IntervalStart");
                """);
        }
    }
}
