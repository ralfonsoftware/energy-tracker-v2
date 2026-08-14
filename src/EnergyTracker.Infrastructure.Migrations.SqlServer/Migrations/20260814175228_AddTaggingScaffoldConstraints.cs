using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddTaggingScaffoldConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PowerPoints_RoomId",
                table: "PowerPoints");

            migrationBuilder.DropIndex(
                name: "IX_Devices_PowerPointId",
                table: "Devices");

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_HouseholdId",
                table: "Rooms",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_HouseholdId_Name",
                table: "Rooms",
                columns: new[] { "HouseholdId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PowerPoints_HouseholdId",
                table: "PowerPoints",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_PowerPoints_RoomId_Name",
                table: "PowerPoints",
                columns: new[] { "RoomId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_HouseholdId",
                table: "Devices",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_PowerPointId_Name",
                table: "Devices",
                columns: new[] { "PowerPointId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Devices_Households_HouseholdId",
                table: "Devices",
                column: "HouseholdId",
                principalTable: "Households",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PowerPoints_Households_HouseholdId",
                table: "PowerPoints",
                column: "HouseholdId",
                principalTable: "Households",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Rooms_Households_HouseholdId",
                table: "Rooms",
                column: "HouseholdId",
                principalTable: "Households",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Devices_Households_HouseholdId",
                table: "Devices");

            migrationBuilder.DropForeignKey(
                name: "FK_PowerPoints_Households_HouseholdId",
                table: "PowerPoints");

            migrationBuilder.DropForeignKey(
                name: "FK_Rooms_Households_HouseholdId",
                table: "Rooms");

            migrationBuilder.DropIndex(
                name: "IX_Rooms_HouseholdId",
                table: "Rooms");

            migrationBuilder.DropIndex(
                name: "IX_Rooms_HouseholdId_Name",
                table: "Rooms");

            migrationBuilder.DropIndex(
                name: "IX_PowerPoints_HouseholdId",
                table: "PowerPoints");

            migrationBuilder.DropIndex(
                name: "IX_PowerPoints_RoomId_Name",
                table: "PowerPoints");

            migrationBuilder.DropIndex(
                name: "IX_Devices_HouseholdId",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_Devices_PowerPointId_Name",
                table: "Devices");

            migrationBuilder.CreateIndex(
                name: "IX_PowerPoints_RoomId",
                table: "PowerPoints",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_PowerPointId",
                table: "Devices",
                column: "PowerPointId");
        }
    }
}
