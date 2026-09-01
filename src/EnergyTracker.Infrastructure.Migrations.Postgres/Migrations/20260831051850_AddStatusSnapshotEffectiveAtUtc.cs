using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddStatusSnapshotEffectiveAtUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Story 4.3: added nullable first, backfilled from the existing ComputedAtUtc (every
            // pre-existing row's EffectiveAtUtc equals its ComputedAtUtc — they only ever diverge
            // for a correction's superseding row, which can't exist before this migration runs),
            // then made NOT NULL — a bare non-nullable AddColumn would fail against the live rows
            // already present in every existing environment (local dev, self-host, Azure).
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EffectiveAtUtc",
                table: "StatusSnapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "StatusSnapshots" SET "EffectiveAtUtc" = "ComputedAtUtc"
                """);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "EffectiveAtUtc",
                table: "StatusSnapshots",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EffectiveAtUtc",
                table: "StatusSnapshots");
        }
    }
}
