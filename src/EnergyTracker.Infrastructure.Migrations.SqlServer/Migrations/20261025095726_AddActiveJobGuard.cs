using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.SqlServer.Migrations
{
    // Story 11.14, AD-6, audit C10. One filtered unique index: at most one Queued/Processing
    // RestoreHouseholdData and one Queued/Processing CleanUpSmartPlugImportJobs row per Household. The
    // endpoints' check-then-enqueue stays as the friendly fast path; this index is the arbiter of a race.
    //
    // Expand-only, safe for the previous image: the index only forbids what the application already
    // forbids. The previous image's check-then-enqueue can at worst turn a lost race into an unhandled
    // 500 instead of a 409 while a health-check rollback has it running against this schema.
    //
    // Declared only here, not in the model (SmartPlugReadingConfiguration.cs, AD-2): a filter is dialect
    // SQL and the model is shared by both providers, so the model snapshot does not change. Other job
    // types (ProcessSmartPlugImport, CorrelateEvent) are not constrained. JobTypes.ExclusivePerHousehold
    // and ActiveJobGuardIsDeclaredTests keep this list and the status integers in step.
    //
    /// <inheritdoc />
    public partial class AddActiveJobGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Make the index creatable on a database that already holds duplicates: for every
            // (HouseholdId, JobType) with more than one active row, keep the newest (CreatedAtUtc, then Id)
            // and fail the others as job-interrupted, the code FailStaleAsync would give them anyway.
            // History is kept, nothing is deleted, CompletedAtUtc is set because the cleanup sweeps read it.
            // Status 0 = Processing, 3 = Queued (active), 2 = Failed (BackgroundJobStatus). Expected to
            // update zero rows: the old check-then-enqueue made a duplicate a narrow race.
            migrationBuilder.Sql("""
                UPDATE j
                SET [Status] = 2, [ErrorMessage] = N'job-interrupted', [CompletedAtUtc] = SYSDATETIMEOFFSET()
                FROM [BackgroundJobs] AS j
                WHERE j.[Status] IN (0, 3)
                  AND j.[JobType] IN (N'RestoreHouseholdData', N'CleanUpSmartPlugImportJobs')
                  AND EXISTS (
                      SELECT 1 FROM [BackgroundJobs] AS newer
                      WHERE newer.[HouseholdId] = j.[HouseholdId]
                        AND newer.[JobType] = j.[JobType]
                        AND newer.[Status] IN (0, 3)
                        AND (newer.[CreatedAtUtc] > j.[CreatedAtUtc]
                             OR (newer.[CreatedAtUtc] = j.[CreatedAtUtc] AND newer.[Id] > j.[Id])));
                """);

            // 2. The guard. Active = Processing (0) or Queued (3).
            migrationBuilder.Sql("""
                CREATE UNIQUE NONCLUSTERED INDEX [UX_BackgroundJobs_ActiveExclusive]
                ON [BackgroundJobs] ([HouseholdId], [JobType])
                WHERE [JobType] IN (N'RestoreHouseholdData', N'CleanUpSmartPlugImportJobs')
                  AND [Status] IN (0, 3);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback is the restore-point procedure (Story 10.1, infra/README.md), not Down(). The rows
            // failed by step 1 of Up() are not reverted.
            migrationBuilder.Sql("""DROP INDEX [UX_BackgroundJobs_ActiveExclusive] ON [BackgroundJobs];""");
        }
    }
}
