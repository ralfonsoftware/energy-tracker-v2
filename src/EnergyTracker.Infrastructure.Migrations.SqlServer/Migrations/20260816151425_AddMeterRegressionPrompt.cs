using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddMeterRegressionPrompt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DigitCapacityKwh",
                table: "MainMeters",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MeterRegressionPrompts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MainMeterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MeterReadingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousMeterReadingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Classification = table.Column<int>(type: "int", nullable: true),
                    DigitCapacityKwh = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeterRegressionPrompts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeterRegressionPrompts_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MeterRegressionPrompts_MainMeters_MainMeterId",
                        column: x => x.MainMeterId,
                        principalTable: "MainMeters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MeterRegressionPrompts_MeterReadings_MeterReadingId",
                        column: x => x.MeterReadingId,
                        principalTable: "MeterReadings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MeterRegressionPrompts_MeterReadings_PreviousMeterReadingId",
                        column: x => x.PreviousMeterReadingId,
                        principalTable: "MeterReadings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeterRegressionPrompts_HouseholdId",
                table: "MeterRegressionPrompts",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_MeterRegressionPrompts_MainMeterId",
                table: "MeterRegressionPrompts",
                column: "MainMeterId");

            migrationBuilder.CreateIndex(
                name: "IX_MeterRegressionPrompts_MeterReadingId",
                table: "MeterRegressionPrompts",
                column: "MeterReadingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeterRegressionPrompts_PreviousMeterReadingId",
                table: "MeterRegressionPrompts",
                column: "PreviousMeterReadingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MeterRegressionPrompts");

            migrationBuilder.DropColumn(
                name: "DigitCapacityKwh",
                table: "MainMeters");
        }
    }
}
