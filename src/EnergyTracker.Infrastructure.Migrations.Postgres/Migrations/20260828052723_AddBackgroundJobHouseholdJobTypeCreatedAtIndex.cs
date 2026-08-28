using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddBackgroundJobHouseholdJobTypeCreatedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_HouseholdId_JobType_CreatedAtUtc",
                table: "BackgroundJobs",
                columns: new[] { "HouseholdId", "JobType", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BackgroundJobs_HouseholdId_JobType_CreatedAtUtc",
                table: "BackgroundJobs");
        }
    }
}
