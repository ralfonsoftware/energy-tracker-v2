using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddStatusSnapshotAndHouseholdThresholds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LowConfidenceGapDays",
                table: "Households",
                type: "integer",
                nullable: false,
                defaultValue: 45);

            migrationBuilder.AddColumn<decimal>(
                name: "TrendingThresholdKwh",
                table: "Households",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 100m);

            migrationBuilder.CreateTable(
                name: "StatusSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PaceToDateKwh = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BaselineToDateKwh = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsLowConfidence = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusSnapshots_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StatusSnapshots_HouseholdId",
                table: "StatusSnapshots",
                column: "HouseholdId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StatusSnapshots");

            migrationBuilder.DropColumn(
                name: "LowConfidenceGapDays",
                table: "Households");

            migrationBuilder.DropColumn(
                name: "TrendingThresholdKwh",
                table: "Households");
        }
    }
}
