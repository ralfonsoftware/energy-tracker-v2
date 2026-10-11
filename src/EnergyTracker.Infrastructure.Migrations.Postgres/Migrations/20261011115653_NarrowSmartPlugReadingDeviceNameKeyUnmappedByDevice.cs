using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.Postgres.Migrations
{
    // Story 11.4, AD-20 (amended 2026-10-10), audit C9. DEPLOY N of a two-deploy expand/contract pair.
    //
    // Bounds SmartPlugReadings.DeviceName to 200 and adds the partial unique index
    // (HouseholdId, DeviceName, IntervalStart) WHERE PowerPointId IS NULL beside the existing
    // (HouseholdId, IntervalStart) one, so two different plugs' unmapped files covering the same period
    // keep their own readings. Deploy N+1 (DropOldUnmappedSmartPlugReadingIndex) drops the old index.
    // Between the two, a second plug's overlapping unmapped import fails loudly on the old index; it never
    // silently overwrites.
    //
    // JUSTIFIED EXCEPTION to Story 10.1's expand/contract rule (Retro 10 note): this migration narrows a
    // column in the same deploy that ships the code relying on the narrowed shape. SQL Server cannot use
    // nvarchar(max) as an index key and this migration creates the index, so the narrowing cannot wait.
    // Safeguards: (1) the first statement checks the stored maximum and fails the migration, and so the
    // deploy before "Deploy new revision", instead of truncating; (2) the parsers and the restore validator
    // enforce the same bound in the same release; (3) the old index is kept, so the previous image and a
    // rollback to it still have their ON CONFLICT / MERGE arbiter; (4) the Story 10.1 restore point and the
    // infra/README.md "Rolling back a bad migration" runbook.
    // See invariants-rules.md#AD-20.
    //
    /// <inheritdoc />
    public partial class NarrowSmartPlugReadingDeviceNameKeyUnmappedByDevice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Pre-check, first statement: never truncate. A value over 200 characters fails the
            // migration (the whole Up() rolls back) with a message that says what to do.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    longest integer;
                BEGIN
                    SELECT MAX(length("DeviceName")) INTO longest FROM "SmartPlugReadings";
                    IF longest > 200 THEN
                        RAISE EXCEPTION 'Cannot bound SmartPlugReadings.DeviceName to 200 characters: the longest stored value has % characters. Shorten or delete the readings whose DeviceName is longer than 200 characters, then redeploy. Nothing was truncated.', longest;
                    END IF;
                END $$;
                """);

            // 2. The scaffolded column change.
            migrationBuilder.AlterColumn<string>(
                name: "DeviceName",
                table: "SmartPlugReadings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            // 3. The new unmapped-reading key, after the column is bounded. The old
            // (HouseholdId, IntervalStart) WHERE PowerPointId IS NULL index is deliberately NOT touched
            // (deploy N+1 drops it). Raw SQL for the same reason as the old index: a filtered index is
            // dialect SQL and EF's shared model does not declare it (SmartPlugReadingConfiguration).
            // The name is 60 characters: Postgres silently truncates identifiers over 63 bytes, so a
            // longer name could collide with a prefix-sharing one. Creation cannot fail on existing
            // data: the new key is strictly looser than the old unique key.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_SmartPlugReadings_Household_Device_IntervalStart_Unmapped"
                ON "SmartPlugReadings" ("HouseholdId", "DeviceName", "IntervalStart")
                WHERE "PowerPointId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback is the restore-point procedure (Story 10.1, infra/README.md), not Down().
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_SmartPlugReadings_Household_Device_IntervalStart_Unmapped";""");

            migrationBuilder.AlterColumn<string>(
                name: "DeviceName",
                table: "SmartPlugReadings",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);
        }
    }
}
