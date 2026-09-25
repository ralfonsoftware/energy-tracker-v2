using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddSmartPlugReadingHouseholdIntervalStartIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SmartPlugReadings_HouseholdId_IntervalStart_Id",
                table: "SmartPlugReadings",
                columns: new[] { "HouseholdId", "IntervalStart", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SmartPlugReadings_HouseholdId_IntervalStart_Id",
                table: "SmartPlugReadings");
        }
    }
}
