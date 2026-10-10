using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddBackgroundJobStartedAndHeartbeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HeartbeatAtUtc",
                table: "BackgroundJobs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAtUtc",
                table: "BackgroundJobs",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HeartbeatAtUtc",
                table: "BackgroundJobs");

            migrationBuilder.DropColumn(
                name: "StartedAtUtc",
                table: "BackgroundJobs");
        }
    }
}
