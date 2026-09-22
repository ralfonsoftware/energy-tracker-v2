using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SmartPlugImportGapPowerPointSetNull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SmartPlugImportGaps_PowerPoints_PowerPointId",
                table: "SmartPlugImportGaps");

            migrationBuilder.AddForeignKey(
                name: "FK_SmartPlugImportGaps_PowerPoints_PowerPointId",
                table: "SmartPlugImportGaps",
                column: "PowerPointId",
                principalTable: "PowerPoints",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SmartPlugImportGaps_PowerPoints_PowerPointId",
                table: "SmartPlugImportGaps");

            migrationBuilder.AddForeignKey(
                name: "FK_SmartPlugImportGaps_PowerPoints_PowerPointId",
                table: "SmartPlugImportGaps",
                column: "PowerPointId",
                principalTable: "PowerPoints",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
