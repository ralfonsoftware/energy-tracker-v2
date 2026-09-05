using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    // Story 3.9/AD-23 — this migration's SQL Server side is a documented no-op (AD-2 convention:
    // a migration is added to both provider projects in the same commit, even when only one
    // provider's schema actually needs a change). The matching Postgres migration promotes
    // IX_SmartPlugReadings_PowerPointId_IntervalStart to a genuine unique CONSTRAINT so
    // EFCore.BulkExtensions.PostgreSql can use ON CONFLICT without first creating its own
    // CREATE INDEX CONCURRENTLY helper index (which cannot run inside a transaction block).
    // SQL Server's own IX_SmartPlugReadings_PowerPointId_IntervalStart unique index already
    // satisfies its MERGE-based bulk-write path directly — no equivalent restriction exists here.
    public partial class AddSmartPlugReadingPowerPointIntervalStartUniqueConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
