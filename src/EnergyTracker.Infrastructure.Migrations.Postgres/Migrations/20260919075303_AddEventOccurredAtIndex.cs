using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddEventOccurredAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Events_HouseholdId",
                table: "Events");

            migrationBuilder.CreateIndex(
                name: "IX_Events_HouseholdId_OccurredAt_CreatedAtUtc",
                table: "Events",
                columns: new[] { "HouseholdId", "OccurredAt", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Events_HouseholdId_OccurredAt_CreatedAtUtc",
                table: "Events");

            migrationBuilder.CreateIndex(
                name: "IX_Events_HouseholdId",
                table: "Events",
                column: "HouseholdId");
        }
    }
}
