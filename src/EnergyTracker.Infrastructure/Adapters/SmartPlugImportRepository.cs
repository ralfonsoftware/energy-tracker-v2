using System.Data.Common;
using EFCore.BulkExtensions;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

public class SmartPlugImportRepository(
    EnergyTrackerDbContext dbContext, IAuditCorrectionRecorder auditCorrectionRecorder,
    ILogger<SmartPlugImportRepository> logger) : ISmartPlugImportRepository
{
    // Ties UpsertAwaitingMappingReadingsAsync's chunk size to BuildAwaitingMappingValuesClause's
    // own column count and each provider's per-statement parameter ceiling (Story 3.9 review fix)
    // — previously two independent magic numbers (9 columns, 5000/200-row chunk sizes) with
    // nothing tying them together, so a future column added to BuildAwaitingMappingValuesClause
    // could silently reintroduce the exact "blew past the provider's parameter limit" bug this
    // chunking exists to fix. Postgres's hard per-statement limit; SQL Server's practical ceiling.
    private const int PostgresMaxStatementParameters = 65_535;
    private const int SqlServerPracticalMaxStatementParameters = 2_100;
    private const int AwaitingMappingColumnsPerRow = 9;


    // AD-23: replaces the old AnyExistingReadingAtSameKeyAsync pre-check / AddRangeAsync fast path
    // / AddWithPerRowConflictToleranceAsync per-row fallback with two set-based write paths, chosen
    // by the same known-vs-unmapped-PowerPoint condition ProcessSmartPlugImport already branches
    // on. Explicit transaction: BulkInsertOrUpdateAsync/ExecuteSqlRawAsync don't participate in
    // SaveChangesAsync's pipeline, so without this, "no partial import observable" would silently
    // disappear (mirrors Story 3.8's own spike-verified cancellation/rollback shape).
    public async Task AddAsync(
        SmartPlugImport import, IReadOnlyList<SmartPlugReading> readings, CancellationToken cancellationToken,
        SmartPlugReadingCorrection? boundaryCorrection = null)
    {
        // Snapshot everything already tracked on this shared scoped DbContext before this call adds
        // anything of its own — the caller (BackgroundJobProcessor) tracks its own BackgroundJob
        // entity across this same call, and the failure-cleanup below must never touch it.
        var trackedBeforeCall = new HashSet<object>(dbContext.ChangeTracker.Entries().Select(e => e.Entity));

        try
        {
            await AddAsyncCore(import, readings, cancellationToken, boundaryCorrection);
        }
        catch
        {
            // Real end-to-end verification (Story 3.9, dev-story session) surfaced this: on any
            // failure partway through, the transaction rolls back at the DB level (the `await
            // using` below), but the DbContext's own change tracker still holds `import` from
            // earlier in this same call — EF Core does not untrack entities on transaction
            // rollback. Left tracked, the caller's own failure-handling path
            // (ProcessSmartPlugImport.PersistFailedImportAsync, which reuses this same scoped
            // DbContext to AddAsync a *new* SmartPlugImport with the same Id) throws a second,
            // unrelated "already being tracked" InvalidOperationException that masks the real one —
            // confirmed by reproducing this exact failure against a real, ~118k-row Eve Home export
            // in a live browser walkthrough.
            //
            // Incident fix (confirmed live in production, 2026-09-05): this used to be a blanket
            // dbContext.ChangeTracker.Clear(), which also silently detached whatever OTHER entity
            // the caller had tracked on this same scoped DbContext — specifically
            // BackgroundJobProcessor's own tracked BackgroundJob row, whose subsequent
            // Status = Failed mutation then targeted a detached entity and got dropped by a no-op
            // SaveChangesAsync, permanently orphaning the job at Status = Processing with its queue
            // message already deleted (reproduced and manually corrected against the live
            // production row this same day).
            //
            // Detach only entities newly tracked by this call — compared against the trackedBeforeCall
            // snapshot, NOT filtered by current State: `import`'s own AddAsync+SaveChangesAsync above
            // (and, when boundaryCorrection is set, auditCorrectionRecorder.RecordAsync's internal
            // AuditCorrection insert — no reference to that entity surfaces back here) already
            // succeeds and advances to Unchanged *inside this still-open transaction*, well before a
            // LATER step (the readings bulk-write) fails and rolls the whole transaction back at the
            // DB level — so a State == Added filter would silently miss both. Comparing against the
            // snapshot instead catches anything this call newly tracked, regardless of what state it
            // settled into, and can't be defeated by a future write AddAsyncCore gains being forgotten
            // in a hand-written list.
            foreach (var entry in dbContext.ChangeTracker.Entries().Where(e => !trackedBeforeCall.Contains(e.Entity)).ToList())
            {
                entry.State = EntityState.Detached;
            }

            throw;
        }
    }

    private async Task AddAsyncCore(
        SmartPlugImport import, IReadOnlyList<SmartPlugReading> readings, CancellationToken cancellationToken,
        SmartPlugReadingCorrection? boundaryCorrection)
    {
        // spec-db-command-timeout-scope: the app-wide 30s->120s CommandTimeout bump this incident
        // fix originally lived in Program.cs's ConfigureDbContext (SqlServer branch — confirmed live
        // in production 2026-09-05 against Basic-tier Azure SQL hitting 100% DTU during a large
        // SmartPlugReadings import; the Postgres branch never had its own incident, it just mirrored
        // the same number precautionarily) has been narrowed to here, the one path that actually
        // needed the headroom. Set first, before anything else in this method runs, so every
        // EF-generated command this job's scoped dbContext issues from here on — the boundary
        // correction's ExecuteUpdateAsync/RecordAsync below, this method's own SaveChangesAsync, and
        // (AD-6: one job, one DI scope, one worker) CompleteSmartPlugImportProcessing's later
        // AddGapsAsync/RecomputeAsync calls on this same instance — gets the full 120s. Same
        // per-call pattern UpdateMappingAsync already uses above (that path keeps its own separate
        // 180s; it is not affected by or a source of this 120s).
        dbContext.Database.SetCommandTimeout(TimeSpan.FromSeconds(120));

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        if (boundaryCorrection is not null)
        {
            // AD-22/AD-11 (Story 3.9 review fix): the narrow KwhValue correction and its audit
            // record now commit inside this same transaction as the rest of the import, instead of
            // independently before AddAsync was ever called — if the write below fails, the
            // correction and its audit trail roll back with it rather than surviving on an import
            // that ultimately persists as Failed. Same set-based ExecuteUpdateAsync idiom
            // UpdateMappingAsync already uses in this class; the affected-row count is checked so a
            // concurrent deletion of the target row (there is no such path today, but the check is
            // free) never produces an audit record for a correction that didn't actually happen.
            var affected = await dbContext.SmartPlugReadings
                .Where(r => r.Id == boundaryCorrection.ReadingId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.KwhValue, boundaryCorrection.NewKwhValue), cancellationToken);

            if (affected > 0)
            {
                await auditCorrectionRecorder.RecordAsync(
                    boundaryCorrection.HouseholdId,
                    "SmartPlugReading",
                    boundaryCorrection.ReadingId,
                    "KwhValue",
                    boundaryCorrection.OldValueFormatted,
                    boundaryCorrection.NewValueFormatted,
                    cancellationToken);
            }
        }

        await dbContext.SmartPlugImports.AddAsync(import, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (readings.Count > 0)
        {
            // Task 7's own flagged, previously-unresolved risk — confirmed empirically against a
            // real Postgres instance: BOTH the primary path's ON CONFLICT (BulkInsertOrUpdateAsync)
            // and the AwaitingPowerPointMapping path's own ON CONFLICT (the raw-SQL upsert) throw
            // "ON CONFLICT DO UPDATE command cannot affect row a second time" when two rows in the
            // SAME incoming batch share the same match key (a genuine DST-fold pair, not the
            // watermark-boundary case AD-22 already filters upstream) — the library does not
            // silently keep one or double-apply both, it throws. De-duplicated here, before either
            // write path, using the same "first-encountered in parse order wins" discipline
            // AD-22's own DST-fold handling and the old per-row-fallback both already established.
            var deduplicatedReadings = DeduplicateByMatchKey(readings, import.Id);

            // The batch is homogeneous by construction (ProcessSmartPlugImport fills in the same
            // matchedPowerPoint, or none, for every reading in one call) — the first row's
            // PowerPointId decides which path the whole batch takes.
            if (deduplicatedReadings[0].PowerPointId is not null)
            {
                var config = new BulkConfig
                {
                    // Story 3.8 spike Finding #1: PropertiesToExcludeOnUpdate, not the blanket
                    // PropertiesToExclude — SmartPlugReading.Id is a client-generated Guid, not
                    // a DB-generated/IDENTITY column, so PropertiesToExclude (which omits a
                    // column from both insert AND update) throws a NOT NULL violation on a
                    // genuinely-new row.
                    PropertiesToExcludeOnUpdate = [nameof(SmartPlugReading.Id)],
                    UpdateByProperties = [nameof(SmartPlugReading.PowerPointId), nameof(SmartPlugReading.IntervalStart)],
                    // Incident fix (confirmed live in production, 2026-09-05): this bulk copy is
                    // exactly the operation that hit "Execution Timeout Expired" on Basic-tier Azure
                    // SQL (100% DTU for ~2 minutes). It runs via SqlBulkCopy/COPY under the hood,
                    // governed by BulkConfig.BulkCopyTimeout — EFCore.BulkExtensions never reads
                    // Program.cs's DbContextOptionsBuilder.CommandTimeout as a fallback (verified
                    // against the installed package's assemblies: no reference to CommandTimeout
                    // anywhere in EFCore.BulkExtensions.Core/SqlServer). That Program.cs bump alone
                    // covers ordinary EF-generated commands (the `import` row insert above,
                    // ExecuteUpdateAsync, UpsertAwaitingMappingReadingsAsync's raw SQL) but does
                    // nothing for this specific path — this is the setting that actually matters for
                    // the incident. Kept at the same 120s value as Program.cs's CommandTimeout for
                    // one consistent headroom number, not because the two settings are otherwise
                    // related.
                    BulkCopyTimeout = 120,
                };
                if (dbContext.Database.IsNpgsql())
                {
                    // Story 3.9 finding (confirmed empirically via Postgres server-side statement
                    // logging, not documented anywhere in EFCore.BulkExtensions.PostgreSql 10.0.1):
                    // its own pg_constraint lookup — the one that lets it skip building a temp
                    // CREATE INDEX CONCURRENTLY helper index when a real unique constraint already
                    // covers the match key (see the migration this depends on) — resolves the
                    // schema to an empty string on this call path, not "public" as its own
                    // ReconfigureTableInfo/default-schema logic should produce; the lookup then
                    // matches zero rows in pg_constraint and falls back to the CONCURRENTLY path
                    // regardless of the migration. Forcing the schema explicitly via
                    // CustomDestinationTableName (undocumented for this purpose, but the only
                    // BulkConfig property that can override Schema before that lookup runs) is the
                    // workaround, not a genuine table-name customization — TableName still resolves
                    // to plain "SmartPlugReadings". Postgres-only: SQL Server's own default-schema
                    // resolution ("dbo") doesn't go through this buggy lookup at all.
                    config.CustomDestinationTableName = "public.SmartPlugReadings";
                }
                else if (dbContext.Database.IsSqlServer())
                {
                    // Incident fix (production, 2026-09-05, energy-tracker-rg): without this, the
                    // MERGE below stages through a permanent table it creates itself via
                    // `SELECT ... INTO [dbo].[SmartPlugReadingsTemp...]` — a DDL operation. AD-21's
                    // Container App runtime identity is deliberately granted only db_datareader/
                    // db_datawriter (infra/sql/grant-entra-db-users.sql — "no schema-change
                    // rights"), so that CREATE TABLE throws "CREATE TABLE permission denied" the
                    // first time a mapped-PowerPoint import runs in Azure (every existing test
                    // authenticates as the container's admin login, which is why this never
                    // surfaced before it hit production). UseTempDB stages through a `#`-prefixed
                    // genuine SQL Server local temp table instead, which needs no schema-level
                    // grant on the target database. EFCore.BulkExtensions.Core 10.0.1 requires this
                    // to run inside an explicit transaction (else it throws) — already satisfied
                    // here by AddAsyncCore's own ambient BeginTransactionAsync, reused below via
                    // UnderlyingTransaction. Postgres-only branch above needs no equivalent: its own
                    // merge strategy never creates a permanent staging table this way.
                    config.UseTempDB = true;
                }
                // Standard EFCore.BulkExtensions idiom for ambient-transaction participation
                // (mirrors Story 3.8's own spike harness, which verified this exact combination's
                // cancellation/rollback behavior against real Postgres) — BulkInsertOrUpdateAsync
                // does not auto-join an ambient EF Core transaction on its own, and its Postgres
                // strategy's own internal `CREATE INDEX CONCURRENTLY` step cannot run inside any
                // transaction that isn't the one this call itself is told to reuse.
                config.UnderlyingConnection = _ => dbContext.Database.GetDbConnection();
                config.UnderlyingTransaction = _ => dbContext.Database.CurrentTransaction!.GetDbTransaction();

                await dbContext.BulkInsertOrUpdateAsync(deduplicatedReadings, config, cancellationToken: cancellationToken);
            }
            else
            {
                await UpsertAwaitingMappingReadingsAsync(deduplicatedReadings, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    // Confirmed empirically (Story 3.9): both write paths' own ON CONFLICT throws
    // "cannot affect row a second time" when two rows in one incoming batch share the same
    // (PowerPointId, IntervalStart) match key — Postgres's own restriction on ON CONFLICT DO
    // UPDATE, not a library quirk. First-encountered-in-parse-order wins, same discipline as
    // AD-22's own DST-fold handling in ProcessSmartPlugImport and the deleted per-row-fallback's
    // "earlier-processed reading always wins" rule.
    private IReadOnlyList<SmartPlugReading> DeduplicateByMatchKey(IReadOnlyList<SmartPlugReading> readings, Guid smartPlugImportId)
    {
        var seenKeys = new HashSet<(Guid? PowerPointId, DateTimeOffset IntervalStart)>();
        var deduplicated = new List<SmartPlugReading>(readings.Count);
        var duplicateCount = 0;

        foreach (var reading in readings)
        {
            if (seenKeys.Add((reading.PowerPointId, reading.IntervalStart)))
            {
                deduplicated.Add(reading);
            }
            else
            {
                duplicateCount++;
            }
        }

        if (duplicateCount > 0)
        {
            logger.LogWarning(
                "Import {SmartPlugImportId}: dropped {DuplicateCount} row(s) sharing a match key with an earlier " +
                "row in the same batch (possibly a DST fall-back duplicate local timestamp) — the first-encountered " +
                "row at each key was kept.",
                smartPlugImportId, duplicateCount);
        }

        return deduplicated;
    }

    // AD-2 [AMENDED 2026-09-04, Story 3.9] — the one narrow, named exception to "provider chosen
    // once, at the composition root, never branched on elsewhere": Story 3.8's spike (Finding #2)
    // proved BulkInsertOrUpdateAsync's UpdateByProperties cannot safely target
    // IX_SmartPlugReadings_HouseholdId_IntervalStart_WhenPowerPointIdNull's partial-index predicate
    // on either provider — it throws on both rather than silently corrupting a row, but the
    // originally-specified single-mechanism design doesn't work for this one path. A hand-written,
    // provider-native raw-SQL upsert instead.
    //
    // [Story 3.9, post-implementation finding from a real end-to-end browser walkthrough] A single
    // multi-row statement, unchunked, does NOT scale to a realistic first-ever/unrecognized-device
    // full-history import: a real ~118k-row Eve Home export whose device tag matched no Power
    // Point blew straight past Postgres's hard 65535-parameter-per-statement protocol limit (9
    // parameters/row here) — confirmed live, not a theoretical concern. SQL Server's own practical
    // parameter ceiling (~2100) is tighter still. Chunked below into provider-sized batches, each
    // still one multi-row statement (never a per-row loop) — this preserves the "no partial row
    // survives cancellation" guarantee via the caller's own ambient transaction (AddAsync's
    // BeginTransactionAsync/commit), which spans every chunk, not just one.
    private async Task UpsertAwaitingMappingReadingsAsync(IReadOnlyList<SmartPlugReading> readings, CancellationToken cancellationToken)
    {
        // 9 parameters/row. Postgres's hard limit is 65535/statement (headroom to ~7281 rows) —
        // 5000 stays comfortably under it and matches AD-20's own assumed ~5000-row sizing for
        // this path in the ordinary case. SQL Server's practical ceiling is far tighter (~2100),
        // so it gets its own, much smaller chunk size. The guard below fails fast (any provider,
        // any build configuration — never a no-op Debug.Assert) if these literals are ever changed
        // out of sync with AwaitingMappingColumnsPerRow.
        var chunkSize = dbContext.Database.IsNpgsql() ? 5_000 : 200;
        var maxStatementParameters = dbContext.Database.IsNpgsql()
            ? PostgresMaxStatementParameters
            : SqlServerPracticalMaxStatementParameters;
        if (chunkSize * AwaitingMappingColumnsPerRow > maxStatementParameters)
        {
            throw new InvalidOperationException(
                $"AwaitingPowerPointMapping upsert chunk size ({chunkSize} rows x {AwaitingMappingColumnsPerRow} " +
                $"columns = {chunkSize * AwaitingMappingColumnsPerRow} parameters) exceeds this provider's own " +
                $"per-statement parameter ceiling ({maxStatementParameters}) — reduce the chunk size above.");
        }

        for (var offset = 0; offset < readings.Count; offset += chunkSize)
        {
            var chunk = readings.Skip(offset).Take(chunkSize).ToList();
            if (dbContext.Database.IsNpgsql())
            {
                await UpsertAwaitingMappingReadingsPostgresAsync(chunk, cancellationToken);
            }
            else if (dbContext.Database.IsSqlServer())
            {
                await UpsertAwaitingMappingReadingsSqlServerAsync(chunk, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException(
                    $"No AwaitingPowerPointMapping raw-SQL upsert is defined for database provider '{dbContext.Database.ProviderName}'.");
            }
        }
    }

    private Task UpsertAwaitingMappingReadingsPostgresAsync(IReadOnlyList<SmartPlugReading> readings, CancellationToken cancellationToken)
    {
        var (valuesClause, parameters) = BuildAwaitingMappingValuesClause(readings);

        // ON CONFLICT's own (HouseholdId, IntervalStart) WHERE PowerPointId IS NULL predicate must
        // match IX_SmartPlugReadings_HouseholdId_IntervalStart_WhenPowerPointIdNull's own predicate
        // exactly (Postgres arbiter-index inference) — same predicate text as that index's own
        // migration SQL. PowerPointId is never listed in the insert column list, so every new row
        // lands with PowerPointId NULL, matching the batch this method is only ever called for.
        // HouseholdId is filtered nowhere else in this statement (AD-3: raw SQL bypasses the global
        // query filter) — every incoming row already carries its own HouseholdId column value, so
        // no separate WHERE HouseholdId=... clause is needed the way a SELECT would need one.
        var sql = $"""
            INSERT INTO "SmartPlugReadings" ("Id", "HouseholdId", "SmartPlugImportId", "RoomName", "PowerPointName", "DeviceName", "IntervalStart", "IntervalEnd", "KwhValue")
            VALUES {valuesClause}
            ON CONFLICT ("HouseholdId", "IntervalStart") WHERE "PowerPointId" IS NULL
            DO UPDATE SET
                "SmartPlugImportId" = EXCLUDED."SmartPlugImportId",
                "RoomName" = EXCLUDED."RoomName",
                "PowerPointName" = EXCLUDED."PowerPointName",
                "DeviceName" = EXCLUDED."DeviceName",
                "IntervalEnd" = EXCLUDED."IntervalEnd",
                "KwhValue" = EXCLUDED."KwhValue";
            """;

        return dbContext.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
    }

    private Task UpsertAwaitingMappingReadingsSqlServerAsync(IReadOnlyList<SmartPlugReading> readings, CancellationToken cancellationToken)
    {
        var (valuesClause, parameters) = BuildAwaitingMappingValuesClause(readings);

        // The ON clause's own target.[PowerPointId] IS NULL requirement scopes every match to the
        // same row subset IX_SmartPlugReadings_HouseholdId_IntervalStart_WhenPowerPointIdNull
        // covers — a target row with a non-null PowerPointId can never match, closing exactly the
        // cross-Power-Point collision Story 3.8 spike Finding #2 found with UpdateByProperties.
        // WITH (HOLDLOCK) prevents a concurrent MERGE against the same key from racing this one
        // into a duplicate insert under READ COMMITTED — standard guidance for MERGE upserts.
        var sql = $"""
            MERGE INTO [SmartPlugReadings] WITH (HOLDLOCK) AS target
            USING (VALUES {valuesClause}) AS source ([Id], [HouseholdId], [SmartPlugImportId], [RoomName], [PowerPointName], [DeviceName], [IntervalStart], [IntervalEnd], [KwhValue])
            ON target.[HouseholdId] = source.[HouseholdId] AND target.[IntervalStart] = source.[IntervalStart] AND target.[PowerPointId] IS NULL
            WHEN MATCHED THEN UPDATE SET
                target.[SmartPlugImportId] = source.[SmartPlugImportId],
                target.[RoomName] = source.[RoomName],
                target.[PowerPointName] = source.[PowerPointName],
                target.[DeviceName] = source.[DeviceName],
                target.[IntervalEnd] = source.[IntervalEnd],
                target.[KwhValue] = source.[KwhValue]
            WHEN NOT MATCHED THEN INSERT ([Id], [HouseholdId], [SmartPlugImportId], [PowerPointId], [RoomName], [PowerPointName], [DeviceName], [IntervalStart], [IntervalEnd], [KwhValue])
            VALUES (source.[Id], source.[HouseholdId], source.[SmartPlugImportId], NULL, source.[RoomName], source.[PowerPointName], source.[DeviceName], source.[IntervalStart], source.[IntervalEnd], source.[KwhValue]);
            """;

        return dbContext.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
    }

    // Shared by both providers — same column order/values, only the wrapping statement differs.
    // ExecuteSqlRawAsync's own {n} placeholders are rewritten to each provider's native parameter
    // syntax by EF Core itself (never string-concatenated values, AD-2's exception bullet).
    private static (string ValuesClause, object[] Parameters) BuildAwaitingMappingValuesClause(IReadOnlyList<SmartPlugReading> readings)
    {
        // Column order matches both provider statements' "(Id, HouseholdId, SmartPlugImportId,
        // RoomName, PowerPointName, DeviceName, IntervalStart, IntervalEnd, KwhValue)" lists above
        // — AwaitingMappingColumnsPerRow (class-level) must change alongside this column count.
        var parameters = new object[readings.Count * AwaitingMappingColumnsPerRow];
        var rowClauses = new string[readings.Count];

        for (var i = 0; i < readings.Count; i++)
        {
            var reading = readings[i];
            var baseIndex = i * AwaitingMappingColumnsPerRow;
            parameters[baseIndex + 0] = reading.Id;
            parameters[baseIndex + 1] = reading.HouseholdId;
            parameters[baseIndex + 2] = (object?)reading.SmartPlugImportId ?? DBNull.Value;
            parameters[baseIndex + 3] = reading.RoomName;
            parameters[baseIndex + 4] = reading.PowerPointName;
            parameters[baseIndex + 5] = reading.DeviceName;
            parameters[baseIndex + 6] = reading.IntervalStart;
            parameters[baseIndex + 7] = reading.IntervalEnd;
            parameters[baseIndex + 8] = reading.KwhValue;
            rowClauses[i] = "(" + string.Join(", ", Enumerable.Range(baseIndex, AwaitingMappingColumnsPerRow).Select(idx => "{" + idx + "}")) + ")";
        }

        return (string.Join(", ", rowClauses), parameters);
    }

    public Task<SmartPlugImport?> FindByBackgroundJobIdAsync(Guid backgroundJobId, CancellationToken cancellationToken) =>
        dbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.BackgroundJobId == backgroundJobId, cancellationToken);

    public Task<SmartPlugImport?> FindByIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken) =>
        dbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.Id == smartPlugImportId, cancellationToken);

    public async Task<IReadOnlyList<SmartPlugReading>> ListReadingsByImportIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugReadings
            .AsNoTracking()
            .Where(r => r.SmartPlugImportId == smartPlugImportId)
            .ToListAsync(cancellationToken);

    // AD-23's own explicit carve-out (verbatim reasoning) — NOT migrated to BulkInsertOrUpdateAsync
    // alongside AddAsync above, and this is deliberate, not an oversight: "It operates on a
    // fundamentally different, inherently small and bounded volume — one already-persisted
    // import's rows, already validated, being re-tagged with a new Power Point/Room (a metadata
    // re-tag), not a bulk insert-or-upsert-by-content decision over a fresh, potentially huge
    // parsed batch. The throughput problem this AD exists to solve doesn't apply there, so it
    // deliberately stays on its existing mechanism rather than being forced onto
    // BulkInsertOrUpdateAsync."
    //
    // [AMENDED 2026-09-18] The "small/bounded volume" half of that reasoning was invalidated by a
    // production incident (a full-history re-export colliding on ~100% of an existing Power
    // Point's rows) — see invariants-rules.md's AD-23 section for the full amendment. The
    // conflict-tolerant fallback below is now internally set-based too, but the carve-out's
    // conclusion (never BulkInsertOrUpdateAsync/EFCore.BulkExtensions for this method) is
    // unchanged: this is still a metadata re-tag over already-persisted, already-validated rows,
    // not a bulk insert-or-upsert-by-content decision over a fresh parsed batch.
    public async Task UpdateMappingAsync(
        SmartPlugImport import, Guid powerPointId, string powerPointName, string? roomName, CancellationToken cancellationToken)
    {
        // The default 30s ADO.NET command timeout is tuned for point queries, not a set-based
        // UPDATE across a full import's rows on Basic-tier Azure SQL (5 DTU) — a large Eve Home
        // export (tens of thousands of rows) reliably exceeded it in production ("Execution Timeout
        // Expired" surfaced to the caller as a 500). Raised for the rest of this scoped DbContext's
        // request too, since the readback in MapSmartPlugImportToPowerPoint.ExecuteAsync right
        // after this call reads the same row count under the same DTU ceiling.
        dbContext.Database.SetCommandTimeout(TimeSpan.FromSeconds(180));

        if (await AnyMappingConflictAsync(import.Id, powerPointId, cancellationToken))
        {
            // Story 3.4 Dev Notes Open Question #4: at least one of this import's readings already
            // collides with an already-mapped reading at the same IntervalStart for the target
            // Power Point — skip the doomed single-statement ExecuteUpdateAsync attempt (avoids a
            // wasted round trip on a large import) and go straight to the set-based conflict
            // classification fallback.
            await UpdateMappingSetBasedWithConflictToleranceAsync(import.Id, powerPointId, powerPointName, roomName, cancellationToken);
        }
        else
        {
            try
            {
                // One set-based UPDATE server-side — no loading/tracking/diffing hundreds of
                // thousands of rows for a large import (see this method's doc comment on the port
                // interface), in the common case where no reading collides with the new
                // (PowerPointId, IntervalStart) unique constraint (AD-20).
                await dbContext.SmartPlugReadings
                    .Where(r => r.SmartPlugImportId == import.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.PowerPointId, powerPointId)
                        .SetProperty(r => r.PowerPointName, powerPointName)
                        .SetProperty(r => r.RoomName, r => roomName ?? r.RoomName),
                        cancellationToken);
            }
            catch (Exception ex) when (ex is DbUpdateException or DbException)
            {
                // The pre-check above already ruled out every conflict it could see — only
                // reachable via a genuine race that appeared after that check ran.
                // ExecuteUpdateAsync is a bulk operation that bypasses the change-tracker
                // SaveChanges pipeline entirely — unlike AddAsync's SaveChangesAsync, it does NOT
                // wrap the provider's native ADO.NET exception (Npgsql's PostgresException/
                // SqlClient's SqlException) in a DbUpdateException, so the portable base type
                // (System.Data.Common.DbException, AD-2 — never a provider-specific exception type
                // in shared Infrastructure code) must be caught here too, confirmed empirically
                // against a real Postgres constraint violation during dev-story activation.
                await UpdateMappingSetBasedWithConflictToleranceAsync(import.Id, powerPointId, powerPointName, roomName, cancellationToken);
            }
        }

        // import is already tracked by this same scoped DbContext (loaded via FindByIdAsync
        // earlier in the same request) — only its Status/CompletedAtUtc changed, so
        // SaveChangesAsync alone is enough. Also flushes any import-row change that a per-row
        // fallback above left pending if every one of its own per-reading saves happened to
        // collide.
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> AnyMappingConflictAsync(Guid importId, Guid powerPointId, CancellationToken cancellationToken)
    {
        var hasAnyExistingForPowerPoint = await dbContext.SmartPlugReadings.AnyAsync(r => r.PowerPointId == powerPointId, cancellationToken);
        if (!hasAnyExistingForPowerPoint)
        {
            return false;
        }

        var intervalStarts = await dbContext.SmartPlugReadings
            .Where(r => r.SmartPlugImportId == importId)
            .Select(r => r.IntervalStart)
            .ToListAsync(cancellationToken);

        return await dbContext.SmartPlugReadings.AnyAsync(
            r => r.PowerPointId == powerPointId && intervalStarts.Contains(r.IntervalStart), cancellationToken);
    }

    // Incident fix (2026-09-18, this bugfix): replaces the former per-row loop
    // (UpdateMappingPerRowWithConflictToleranceAsync — one SaveChangesAsync per reading, plus an
    // extra SELECT+conditional-ExecuteDeleteAsync per collision) whose O(n) round trips took ~4
    // minutes against a full-history Eve Home re-export that collided on ~100% of its rows,
    // exceeding Azure Container Apps' ~240s ingress timeout (HTTP 499) even though the
    // classification logic itself (Story 3.7) was correct. Classification is now done once, in
    // memory, from a single bulk-fetched result set, then applied via exactly two set-based
    // statements — never load full SmartPlugReading entities into the change tracker for this
    // path (both queries below are AsNoTracking projections).
    private async Task UpdateMappingSetBasedWithConflictToleranceAsync(
        Guid smartPlugImportId, Guid powerPointId, string powerPointName, string? roomName, CancellationToken cancellationToken)
    {
        // HouseholdId scoping is implicit for every query below, same as the deleted per-row
        // fallback: AD-3's global query filter (wired once in EnergyTrackerDbContext.OnModelCreating)
        // applies to any LINQ query against SmartPlugReadings through this DbContext, projected
        // bulk queries and ExecuteUpdateAsync/ExecuteDeleteAsync included — not just full-entity loads.
        var importReadings = await dbContext.SmartPlugReadings
            .AsNoTracking()
            .Where(r => r.SmartPlugImportId == smartPlugImportId)
            .Select(r => new { r.Id, r.IntervalStart, r.DeviceName, r.KwhValue, r.IntervalEnd })
            .ToListAsync(cancellationToken);

        if (importReadings.Count == 0)
        {
            return;
        }

        // One bulk fetch of every already-mapped reading at the target Power Point whose
        // IntervalStart could collide with this import's readings — a dictionary keyed by
        // IntervalStart built from a single result set (Design Notes: safe even at large N,
        // unlike a per-row query). EF Core 10 translates List<Guid>/List<DateTimeOffset>.Contains
        // as a JSON-array parameter (verified for both SQL Server's OPENJSON and Npgsql's
        // ANY(@array) translation), not one parameter per value (Design Notes), so this doesn't
        // hit the 2100-parameter ceiling even for very large imports.
        //
        // Review finding (2026-09-18, this bugfix's own review loop): MUST exclude this import's
        // own readings (`r.SmartPlugImportId != smartPlugImportId`) — without it, a concurrent or
        // duplicate mapping request for the SAME import (e.g. a user double-clicking "Map" while
        // an earlier, still-processing request is slow) can see this import's OWN just-committed
        // rows here after the first request commits. Since a reading trivially matches itself on
        // DeviceName/KwhValue/IntervalEnd, the second request would classify every one of its own
        // already-correctly-mapped rows as an "exact duplicate" of itself and delete them all —
        // silently destroying a fully-succeeded import. The old per-row loop never had this
        // failure mode: it updated each row by its own Id (a no-op re-write when already correct),
        // never re-classified a row's relationship to itself.
        var intervalStarts = importReadings.Select(r => r.IntervalStart).ToList();
        var existingByIntervalStart = await dbContext.SmartPlugReadings
            .AsNoTracking()
            .Where(r => r.PowerPointId == powerPointId
                && r.SmartPlugImportId != smartPlugImportId
                && intervalStarts.Contains(r.IntervalStart))
            .Select(r => new { r.IntervalStart, r.DeviceName, r.KwhValue, r.IntervalEnd })
            .ToDictionaryAsync(r => r.IntervalStart, cancellationToken);

        var noConflictIds = new List<Guid>();
        var exactDuplicates = new List<(Guid Id, DateTimeOffset IntervalStart)>();

        foreach (var reading in importReadings)
        {
            if (!existingByIntervalStart.TryGetValue(reading.IntervalStart, out var existing))
            {
                noConflictIds.Add(reading.Id);
                continue;
            }

            // Story 3.7 AC #1/#2 classification, unchanged from the deleted per-row fallback:
            // DeviceName must be part of the exact-duplicate match — a Power Point can receive
            // manually-mapped readings from more than one distinct SmartPlugImport/device over
            // time (MapSmartPlugImportToPowerPoint imposes no device-identity constraint), so two
            // different devices' readings could otherwise coincide on
            // IntervalStart/KwhValue/IntervalEnd without actually being the same duplicate.
            if (existing.DeviceName == reading.DeviceName
                && existing.KwhValue == reading.KwhValue
                && existing.IntervalEnd == reading.IntervalEnd)
            {
                exactDuplicates.Add((reading.Id, reading.IntervalStart));
            }
            else
            {
                // AC #2: genuinely divergent data at the same key (e.g. a DST fall-back duplicate
                // local timestamp) — never silently discard data that might actually differ; leave
                // the reading unmapped (PowerPointId stays null via the ExecuteUpdateAsync below,
                // which only ever touches noConflictIds) and log it straight from this in-memory
                // classification — no per-row DB round trip for the log path.
                logger.LogWarning(
                    "Skipped mapping SmartPlugReading {SmartPlugReadingId} (import {SmartPlugImportId}) to PowerPointId={PowerPointId}: " +
                    "a reading already exists at IntervalStart={IntervalStart:O} for that Power Point (unique-constraint conflict, " +
                    "possibly a DST fall-back duplicate local timestamp).",
                    reading.Id, smartPlugImportId, powerPointId, reading.IntervalStart);
            }
        }

        if (noConflictIds.Count > 0)
        {
            // Same set-based idiom as UpdateMappingAsync's own fast path above, scoped to just the
            // non-colliding subset of this import's rows via their projected Id.
            await dbContext.SmartPlugReadings
                .Where(r => noConflictIds.Contains(r.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.PowerPointId, powerPointId)
                    .SetProperty(r => r.PowerPointName, powerPointName)
                    .SetProperty(r => r.RoomName, r => roomName ?? r.RoomName),
                    cancellationToken);
        }

        if (exactDuplicates.Count > 0)
        {
            // AC #1: dead data now that the mapped row is authoritative — delete every
            // exact-duplicate colliding row in one statement instead of one DELETE per row
            // (Story 3.4 Dev Notes Open Question #4's AD-20 gap, confirmed live in production at
            // 179,324-row scale — see this story's Context).
            var exactDuplicateIds = exactDuplicates.Select(d => d.Id).ToList();
            var deletedCount = await dbContext.SmartPlugReadings
                .Where(r => exactDuplicateIds.Contains(r.Id))
                .ExecuteDeleteAsync(cancellationToken);

            // Review finding (2026-09-18): log what was actually classified/attempted, not a
            // per-row "Deleted" claim we can't back up from a single batched DELETE's row count
            // alone. The I/O matrix's concurrent-delete race (a row already gone by the time this
            // DELETE runs — e.g. the 30-day sweep, or another concurrent mapping request) still
            // isn't an error, but it's no longer silently misreported as "Deleted duplicate X" for
            // a row that may not have still been there.
            if (deletedCount < exactDuplicateIds.Count)
            {
                logger.LogWarning(
                    "Import {SmartPlugImportId}: {ClassifiedCount} SmartPlugReading(s) classified as exact duplicates for " +
                    "PowerPointId={PowerPointId}, but only {DeletedCount} were still present to delete — the rest were already " +
                    "removed by a concurrent operation before this batch's DELETE ran.",
                    smartPlugImportId, exactDuplicateIds.Count, powerPointId, deletedCount);
            }

            foreach (var (id, intervalStart) in exactDuplicates)
            {
                logger.LogWarning(
                    "Resolved SmartPlugReading {SmartPlugReadingId} (import {SmartPlugImportId}) as a duplicate of an already-mapped " +
                    "reading instead of mapping it to PowerPointId={PowerPointId}: identical DeviceName/KwhValue/IntervalEnd already " +
                    "exists at IntervalStart={IntervalStart:O} for that Power Point; removed if still present.",
                    id, smartPlugImportId, powerPointId, intervalStart);
            }
        }
    }

    public async Task<SmartPlugReadingWatermark?> FindLatestReadingWatermarkByPowerPointAsync(Guid powerPointId, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugReadings
            .Where(r => r.PowerPointId == powerPointId)
            .OrderByDescending(r => r.IntervalStart)
            .Select(r => new SmartPlugReadingWatermark(r.Id, r.IntervalStart, r.KwhValue))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SmartPlugReading>> ListPriorReadingsByPowerPointAsync(
        Guid powerPointId, Guid excludeSmartPlugImportId, DateOnly sinceDate, CancellationToken cancellationToken)
    {
        // AD-9: SmartPlugReading.IntervalStart is a local-time date encoded with a zero UTC offset
        // — match that encoding here rather than comparing against a real-offset instant.
        var sinceInstant = new DateTimeOffset(sinceDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return await dbContext.SmartPlugReadings
            .Where(r => r.PowerPointId == powerPointId
                && r.SmartPlugImportId != excludeSmartPlugImportId
                && r.IntervalStart >= sinceInstant)
            .OrderBy(r => r.IntervalStart)
            .ToListAsync(cancellationToken);
    }

    public async Task<DateOnly?> FindFirstReadingDateByPowerPointAsync(Guid powerPointId, CancellationToken cancellationToken)
    {
        var first = await dbContext.SmartPlugReadings
            .Where(r => r.PowerPointId == powerPointId)
            .OrderBy(r => r.IntervalStart)
            .Select(r => (DateTimeOffset?)r.IntervalStart)
            .FirstOrDefaultAsync(cancellationToken);
        return first is { } value ? DateOnly.FromDateTime(value.DateTime) : null;
    }

    public async Task AddGapsAsync(IReadOnlyList<SmartPlugImportGap> gaps, CancellationToken cancellationToken)
    {
        await dbContext.SmartPlugImportGaps.AddRangeAsync(gaps, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SmartPlugImportGap>> ListGapsByImportIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugImportGaps
            .Where(g => g.SmartPlugImportId == smartPlugImportId)
            .OrderBy(g => g.StartDate)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SmartPlugImportGap>> ListGapsByImportIdsAsync(
        IReadOnlyList<Guid> smartPlugImportIds, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugImportGaps
            .AsNoTracking()
            .Where(g => smartPlugImportIds.Contains(g.SmartPlugImportId))
            .OrderBy(g => g.StartDate)
            .ToListAsync(cancellationToken);

    public async Task AddFlaggedForReviewAsync(SmartPlugImport import, SmartPlugImportGap gap, CancellationToken cancellationToken)
    {
        await dbContext.SmartPlugImports.AddAsync(import, cancellationToken);
        await dbContext.SmartPlugImportGaps.AddAsync(gap, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SmartPlugImport>> FindAllByBackgroundJobIdsAsync(
        IReadOnlyList<Guid> backgroundJobIds, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugImports
            .AsNoTracking()
            .Where(i => backgroundJobIds.Contains(i.BackgroundJobId))
            .ToListAsync(cancellationToken);

    public async Task SweepExpiredAsync(Guid householdId, DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        // Eligible-for-deletion rule (Story 3.6/AD-6 extension): the job reached a terminal,
        // resolved state — Error (BackgroundJobStatus.Failed) or Success/Flagged for Review
        // (BackgroundJobStatus.Completed with the joined SmartPlugImport.Status Completed or
        // FlaggedForReview) — before the cutoff. Needs Mapping (AwaitingPowerPointMapping) is
        // deliberately excluded here even though the background job itself is Completed — the
        // import is still unresolved (AC #7).
        //
        // LEFT JOIN (not inner) — review-round-2 patch: a Failed job can have no paired
        // SmartPlugImport row at all (e.g. an unknown JobType, or a JSON-deserialize failure
        // inside BackgroundJobProcessor before ProcessSmartPlugImport.ExecuteAsync's own
        // paired-row-on-failure logic ever runs). An inner join silently excluded that class of
        // job from the sweep forever.
        //
        // The cutoff compares against the import row's own CompletedAtUtc when one exists, not
        // the BackgroundJob row's — review-round-2 patch: MapSmartPlugImportToPowerPoint updates
        // only the import's CompletedAtUtc when a Needs Mapping job is later resolved, so
        // comparing against the job's original (parse-time) CompletedAtUtc would sweep a
        // just-resolved import on the very next list read whenever the original parse happened
        // more than 30 days ago.
        //
        // CAP-6 (2026-09-17): ordered oldest-eligible-first, with a `job.Id` tiebreaker for
        // determinism when two rows share the exact same completedAtUtc (plausible for imports
        // parsed in the same batch) — and bounded to DeleteBatchSize rows at the QUERY level via
        // `Take` before `ToListAsync`, not a client-side `.Take()` on an already-materialized list.
        // This sweep runs inline on every GET poll, so even the SELECT that decides what's eligible
        // must stay bounded regardless of total backlog size, not just the delete work that follows.
        var boundedEligible = await (
            from job in dbContext.BackgroundJobs
            where job.HouseholdId == householdId && job.JobType == JobTypes.ProcessSmartPlugImport
            join import in dbContext.SmartPlugImports on job.Id equals import.BackgroundJobId into importGroup
            from import in importGroup.DefaultIfEmpty()
            let completedAtUtc = import != null ? import.CompletedAtUtc : job.CompletedAtUtc
            where completedAtUtc != null && completedAtUtc < cutoffUtc
                && (job.Status == BackgroundJobStatus.Failed
                    || (job.Status == BackgroundJobStatus.Completed && import != null
                        && (import.Status == SmartPlugImportStatus.Completed || import.Status == SmartPlugImportStatus.FlaggedForReview)))
            orderby completedAtUtc, job.Id
            select new EligibleRow(job.Id, import == null ? null : import.Id)
        ).Take(DeleteBatchSize).ToListAsync(cancellationToken);

        if (boundedEligible.Count == 0)
        {
            return;
        }

        // Unlike DeleteJobsAsync/DeleteEligibleAsync below (which process every eligible row in one
        // call), only this already-query-bounded take is even considered for deletion this call —
        // any remainder is left for the next poll's fresh eligibility query. No persisted cursor is
        // needed for that resume: a row this call deletes simply stops matching the query next time.
        await DeleteBoundedChunkAsync(householdId, boundedEligible, cancellationToken);
    }

    private readonly record struct EligibleRow(Guid BackgroundJobId, Guid? SmartPlugImportId);

    // CAP-6: bounded counterpart to DeleteEligibleAsync, used only by SweepExpiredAsync's per-call-
    // chunked path above. Guarded by a non-blocking, transaction-scoped per-household advisory lock:
    // without it, two concurrent GET polls for the same household (e.g. two open tabs) could both
    // select the same oldest-eligible chunk and race their deletes, with the loser blocking on DB row
    // locks for the duration of the winner's still-in-flight DetachReadingsForImportAsync loop —
    // bounded by one chunk's own duration, but still a real, avoidable latency spike for exactly the
    // population this spec protects. If the lock isn't acquired, another poll for this household is
    // already mid-chunk; this call no-ops and the next poll (≤8s later) retries against then-current
    // state.
    //
    // Round-2 review finding (Blind Hunter): the lock is acquired FIRST, before measuring reading
    // counts or packing a chunk — a losing poller must short-circuit as cheaply as possible. The
    // reading-count GROUP BY below is bounded by import count (≤DeleteBatchSize, inherited from the
    // caller's query-level Take) but not by reading volume, so running it unconditionally — even for
    // a call about to lose the lock race — could scan a large volume inline on every contended poll.
    // Acquiring the lock up front means only the winner ever pays that cost.
    private async Task DeleteBoundedChunkAsync(Guid householdId, IReadOnlyList<EligibleRow> boundedEligible, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        if (!await TryAcquireHouseholdSweepLockAsync(householdId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        var boundedImportIds = boundedEligible.Where(x => x.SmartPlugImportId is not null).Select(x => x.SmartPlugImportId!.Value).ToList();

        var readingCounts = boundedImportIds.Count > 0
            ? await dbContext.SmartPlugReadings
                .Where(r => r.SmartPlugImportId != null && boundedImportIds.Contains(r.SmartPlugImportId!.Value))
                .GroupBy(r => r.SmartPlugImportId!.Value)
                .ToDictionaryAsync(g => g.Key, g => g.Count(), cancellationToken)
            : new Dictionary<Guid, int>();

        // Reproduces exactly what DeleteEligibleAsync's own full-sweep packer would yield as its
        // first chunk: the greedy packer's first boundary depends only on the prefix of importIds up
        // to whichever cap trips first, never on anything after it — so feeding it just this
        // already-bounded prefix and taking its first chunk is equivalent, without measuring or
        // packing a potentially large remaining backlog.
        var importIdChunk = ChunkImportIdsByReadingVolume(boundedImportIds, readingCounts, DeleteReadingVolumeThreshold, DeleteBatchSize)
            .FirstOrDefault() ?? [];
        var importIdChunkSet = importIdChunk.ToHashSet();

        // Import-less Failed jobs (no paired SmartPlugImport row) carry zero reading cost, so every
        // one in the bounded take clears this call regardless of the import chunk boundary above —
        // this can, in principle, clear a newer bare-Failed-job row while an older, volume-deferred
        // import waits for a later call. Accepted: every row still clears within a bounded number of
        // calls (no permanent starvation), and forcing bare jobs to wait behind volume-bound imports
        // would only delay free work for no correctness benefit.
        var jobIdChunk = boundedEligible
            .Where(x => x.SmartPlugImportId is null || importIdChunkSet.Contains(x.SmartPlugImportId!.Value))
            .Select(x => x.BackgroundJobId)
            .ToList();

        await DeleteImportChunkAsync(importIdChunk, cancellationToken);

        // BackgroundJobs last — SmartPlugImport.BackgroundJobId's FK is Restrict, so any paired
        // import row must already be gone before this delete can succeed. Already bounded to at most
        // DeleteBatchSize rows by the caller, so unlike DeleteEligibleAsync's own jobIds loop below,
        // no further internal chunking is needed here.
        await dbContext.BackgroundJobs
            .Where(j => jobIdChunk.Contains(j.Id))
            .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    // Non-blocking, transaction-scoped advisory lock keyed by household, used only by
    // DeleteBoundedChunkAsync above. Scoped to the CURRENT transaction on both providers so it
    // releases automatically on commit or rollback — no separate unlock call to forget, no risk from
    // connection pooling returning a connection to the pool while a session-scoped lock is still
    // held. Provider-specific because neither primitive has a portable EF Core equivalent — same
    // reason UpsertAwaitingMappingReadingsAsync above branches by provider.
    private Task<bool> TryAcquireHouseholdSweepLockAsync(Guid householdId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsNpgsql())
        {
            return TryAcquireHouseholdSweepLockPostgresAsync(householdId, cancellationToken);
        }

        if (dbContext.Database.IsSqlServer())
        {
            return TryAcquireHouseholdSweepLockSqlServerAsync(householdId, cancellationToken);
        }

        throw new InvalidOperationException(
            $"No advisory-lock implementation is defined for database provider '{dbContext.Database.ProviderName}'.");
    }

    private async Task<bool> TryAcquireHouseholdSweepLockPostgresAsync(Guid householdId, CancellationToken cancellationToken)
    {
        // pg_try_advisory_xact_lock takes a bigint key; hashtextextended() derives one from the
        // household Guid's string form using the full 64-bit output (there's no direct Guid-keyed
        // advisory lock function) — chosen over the plain hashtext()'s 32-bit output for the same
        // cost, to keep collision probability as low as the primitive allows. A hash collision
        // between two different households' lock keys would only cause one to transiently no-op this
        // call's chunk (it self-heals on the next poll), never incorrect data — an acceptable,
        // extremely low-probability residual, the same class of accepted risk as this file's other
        // starting-value/evidence-based tuning choices. Parameterized via string interpolation (EF
        // Core rewrites the hole to a real parameter, never concatenates it — AD-2).
        return await dbContext.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({householdId.ToString()}, 0)) AS \"Value\"")
            .SingleAsync(cancellationToken);
    }

    private async Task<bool> TryAcquireHouseholdSweepLockSqlServerAsync(Guid householdId, CancellationToken cancellationToken)
    {
        // sp_getapplock is SQL Server's closest non-blocking, transaction-scoped equivalent to
        // Postgres's pg_try_advisory_xact_lock: @LockTimeout = 0 makes acquisition non-blocking,
        // @LockOwner = 'Transaction' releases it automatically on commit/rollback. Return code 0 or 1
        // means acquired; any negative code (timeout, cancel, deadlock, parameter error) means not
        // acquired — treated the same as "another poll is already mid-chunk" either way.
        //
        // Round-2 review finding: this batch (DECLARE/EXEC before the final SELECT) is non-composable
        // SQL, and SqlQuery<T>().SingleAsync() tries to compose (wrap it as a subquery) to apply its
        // own TOP/LIMIT — throwing InvalidOperationException on every call. ToListAsync() executes the
        // raw batch as-is with no composition; taking Single() over the materialized (always
        // one-row) result client-side avoids it. Caught only once this file gained its first-ever
        // SqlServer test exercising this method — this exact call would otherwise have thrown on
        // every SweepExpiredAsync invocation against the real production (SqlServer) database.
        var lockResults = await dbContext.Database
            .SqlQuery<int>($"""
                DECLARE @LockResult int;
                EXEC @LockResult = sp_getapplock
                    @Resource = {householdId.ToString()},
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 0;
                SELECT @LockResult AS "Value";
                """)
            .ToListAsync(cancellationToken);
        return lockResults.Single() >= 0;
    }

    public async Task<int> DeleteJobsAsync(Guid householdId, DateTimeOffset? cutoffUtc, CancellationToken cancellationToken)
    {
        // Story 3.10: a manual, user-triggered counterpart to SweepExpiredAsync above — deliberately
        // NOT restricted to terminal states (Waiting/Processing/Needs Mapping are eligible here,
        // unlike the automatic sweep) and cutoffUtc is optional ("clean up everything" = null).
        // Kept as its own eligibility query rather than folding an "include active states" flag
        // into SweepExpiredAsync itself, so the automatic sweep's Story 3.6 behavior can never
        // regress via this method's changes.
        //
        // effectiveAgeUtc fallback chain: import.CompletedAtUtc (set at initial parse time for
        // BOTH Completed and AwaitingPowerPointMapping — see ProcessSmartPlugImport.cs:113-124, so
        // Needs Mapping rows already have a usable value here) -> job.CompletedAtUtc (Failed jobs
        // with no paired import row) -> job.CreatedAtUtc (the only timestamp a Queued/Processing
        // job has — both of the prior two are null for those).
        var eligible = await (
            from job in dbContext.BackgroundJobs
            where job.HouseholdId == householdId && job.JobType == JobTypes.ProcessSmartPlugImport
            join import in dbContext.SmartPlugImports on job.Id equals import.BackgroundJobId into importGroup
            from import in importGroup.DefaultIfEmpty()
            let effectiveAgeUtc = (import != null ? import.CompletedAtUtc : null) ?? job.CompletedAtUtc ?? job.CreatedAtUtc
            where cutoffUtc == null || effectiveAgeUtc < cutoffUtc
            select new { BackgroundJobId = job.Id, SmartPlugImportId = (Guid?)(import == null ? null : import.Id) }
        ).ToListAsync(cancellationToken);

        if (eligible.Count == 0)
        {
            return 0;
        }

        var importIds = eligible.Where(x => x.SmartPlugImportId is not null).Select(x => x.SmartPlugImportId!.Value).ToList();
        var jobIds = eligible.Select(x => x.BackgroundJobId).ToList();
        await DeleteEligibleAsync(jobIds, importIds, cancellationToken);

        return eligible.Count;
    }

    // Incident fix (2026-09-12 prod): bounds the *typical* per-command row/log volume of each
    // chunked delete below. This is NOT a hard bound on every possible case: a single import can
    // itself carry hundreds of thousands of SmartPlugReading rows (a full-history Eve Home export
    // — see AddAsyncCore's BulkCopyTimeout comment above), and chunking by import COUNT doesn't
    // bound one pathologically large import's own SetNull cascade — only the number of imports
    // touched per command. This fix reduces risk for the incident's actual shape (many accumulated
    // jobs/imports over time saturating Azure SQL Basic-tier's 5-DTU log-write throughput within
    // the 120s CommandTimeout on "clean up everything" / DeleteJobsAsync with cutoffUtc: null) —
    // it does not claim to solve the different edge case of one extreme-sized individual import
    // (accepted residual risk, tracked in deferred-work.md, same as this fix's other known limit:
    // no bound on total wall-clock time across every chunk). 200 is a starting value from the
    // incident's own DTU/Log-IO evidence, not a benchmarked optimum. It's also comfortably under
    // SQL Server's ~2100-parameter ceiling — confirmed to actually matter for this query shape, not
    // a theoretical concern: the 2026-09-12 incident's own captured DbCommand log shows this exact
    // `Contains(...)` predicate translating to one named parameter per id (`@importIds1` ...
    // `@importIds60` observed live), not a single array/JSON parameter — but that ceiling still
    // wasn't the actual constraint the incident hit (the cascade's row/log volume was).
    internal const int DeleteBatchSize = 200;

    // Incident fix round 2 (2026-09-12 prod, same day): DeleteBatchSize alone didn't fix the
    // incident it was built for — a household with only 51-60 eligible rows (well under 200)
    // never triggers count-based chunking at all, yet 5 of its 51 imports individually carried
    // 57,171/66,238/112,005/114,077/122,158 SmartPlugReading rows (487,380 total), and the single
    // resulting DELETE FROM SmartPlugImports command still saturated Azure SQL Basic-tier and hit
    // CommandTimeout. This bounds the importIds loop by cumulative READING count too, not just
    // import count — see ChunkImportIdsByReadingVolume below. 20,000 is roughly 24x smaller than
    // the volume that saturated Basic-tier for ~2 minutes in the confirmed incident; a starting
    // value from that evidence, not a benchmarked optimum. A single import whose own reading count
    // alone exceeds this still forms its own (unsplittable) chunk — accepted, not solved here; see
    // deferred-work.md.
    internal const int DeleteReadingVolumeThreshold = 20_000;

    // Incident fix round 4 (2026-09-12 20:40 prod, confirmed via Container App logs + Azure
    // Monitor DTU/Log-IO): CAP-4's own round-3 spec accepted "a single import whose own reading
    // count exceeds DeleteReadingVolumeThreshold still can't be split further" as low-stakes
    // residual risk, reasoning the household's largest single import (122,158 rows) was "well
    // within a reasonable chunk threshold on its own." That assumption was never actually load-
    // tested against a live SetNull cascade and was wrong: isolating that one 122,158-row import
    // alone in its own chunk (exactly what CAP-4 does) still blew Azure SQL Basic-tier's 120s
    // CommandTimeout — confirmed by the exact failing command in production logs
    // (`Failed executing DbCommand (120,205ms) [Parameters=[..., @importIdBatch1=...]`, a single-
    // import chunk) and matching DTU (94.5%)/Log IO (93%) saturation. Round 3's async move (CAP-5)
    // only removed the *HTTP*-ingress timeout ceiling; it did nothing about the *SQL* CommandTimeout
    // a single command's own cascade can still hit. DetachReadingsForImportAsync below closes this
    // for good: instead of relying on the FK's SetNull cascade to update however many rows a given
    // import has in one opaque, unboundable server-side operation, every import's own readings are
    // explicitly detached in DeleteBatchSize-sized batches *before* that import is deleted — so the
    // DELETE FROM SmartPlugImports that follows always matches zero children and costs nothing
    // regardless of how large the import was. This makes ChunkImportIdsByReadingVolume's own
    // reading-volume bound no longer necessary to prevent a timeout (every import's cascade cost is
    // now always ~0), but it's left in place unchanged — still harmless, and safer to leave working,
    // already-tested chunking logic alone than to remove it under incident pressure.
    //
    // Round-4 review findings (Blind Hunter): this trades one unboundable command for roughly
    // readingCount/DeleteBatchSize round trips (~1,200 SELECT+UPDATE pairs for a 122,158-row
    // import) — an unbenchmarked aggregate wall-clock cost, same "starting value from evidence, not
    // a benchmarked optimum" caveat DeleteBatchSize/DeleteReadingVolumeThreshold's own comments
    // already carry, not a new gap. Two residual risks this doesn't close, tracked in
    // deferred-work.md: (1) an import still Processing/AwaitingPowerPointMapping — both eligible for
    // DeleteJobsAsync by design — can keep receiving new SmartPlugReading rows via AddAsyncCore
    // concurrently with this loop; a row inserted after this loop already passed its offset is
    // missed by this detach pass and falls back to the FK's own SetNull cascade at delete time (this
    // narrows that pre-existing TOCTOU's blast radius to just the race-window rows, it does not
    // close it); (2) the automatic SweepExpiredAsync path (unlike the manual "clean up everything"
    // endpoint) was never moved off the synchronous HTTP request by CAP-5, so this loop's aggregate
    // duration is now also paid synchronously, inline, by whichever GET /api/smart-plug-import-jobs
    // request happens to trigger the sweep for a large aging import.
    private async Task DetachReadingsForImportAsync(Guid importId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var readingIds = await dbContext.SmartPlugReadings
                .Where(r => r.SmartPlugImportId == importId)
                .Select(r => r.Id)
                .Take(DeleteBatchSize)
                .ToListAsync(cancellationToken);
            if (readingIds.Count == 0)
            {
                return;
            }

            await dbContext.SmartPlugReadings
                .Where(r => readingIds.Contains(r.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.SmartPlugImportId, (Guid?)null), cancellationToken);
        }
    }

    // Greedily packs importIds into chunks bounded by BOTH cumulative reading count and import
    // count, whichever is hit first — pure and DB-free so the packing logic itself gets direct
    // unit coverage (SmartPlugImportRepositoryChunkingTests.cs) without seeding real rows. An id
    // missing from readingCountByImportId (no readings) is treated as zero. A single id whose own
    // count already exceeds maxReadingsPerChunk still lands alone in its own chunk — chunks are
    // never empty and every input id is assigned to exactly one chunk.
    internal static IEnumerable<Guid[]> ChunkImportIdsByReadingVolume(
        IReadOnlyList<Guid> importIds, IReadOnlyDictionary<Guid, int> readingCountByImportId,
        int maxReadingsPerChunk, int maxImportsPerChunk)
    {
        var currentChunk = new List<Guid>();
        var currentReadingCount = 0;
        foreach (var importId in importIds)
        {
            var readingCount = readingCountByImportId.GetValueOrDefault(importId);
            if (currentChunk.Count > 0 &&
                (currentChunk.Count >= maxImportsPerChunk || currentReadingCount + readingCount > maxReadingsPerChunk))
            {
                yield return currentChunk.ToArray();
                currentChunk = [];
                currentReadingCount = 0;
            }

            currentChunk.Add(importId);
            currentReadingCount += readingCount;
        }

        if (currentChunk.Count > 0)
        {
            yield return currentChunk.ToArray();
        }
    }

    // Shared by SweepExpiredAsync (automatic, terminal-states-only) and DeleteJobsAsync (manual,
    // all-states, Story 3.10) — set-based, in FK-dependency order (UpdateMappingAsync's own doc
    // comment establishes the same discipline for this table) — never load-then-remove, these
    // tables can hold hundreds of thousands of rows.
    //
    // Code-review fix (2026-09-11): wrapped in one explicit transaction — three independent
    // ExecuteDeleteAsync calls with no transaction risked a partial delete (gaps/imports gone,
    // BackgroundJob rows left behind) surviving a cancellation or transient failure between calls,
    // the same class of bug this codebase already fixed once for Power Point mapping (fa77aef).
    //
    // Incident fix (2026-09-12): each of the three deletes is now chunked into DeleteBatchSize-id
    // batches, each its own ExecuteDeleteAsync command, still inside this same outer transaction.
    // CommandTimeout (Program.cs) bounds a single command's execution, not the transaction's total
    // duration, so chunking removes the per-command timeout risk without weakening the all-or-
    // nothing guarantee above — a failure on any chunk still rolls back every chunk, including ones
    // that already ran, because the transaction is never committed. Per-chunk transactions were
    // considered and rejected: they would weaken that guarantee for no timeout benefit
    // CommandTimeout doesn't already give per-command. The two loops below chunk for two distinct
    // reasons sharing one constant: the importIds loop bounds a downstream SetNull cascade (see
    // DeleteBatchSize's own comment); the jobIds loop has no such cascade to bound at all
    // (SmartPlugImport.BackgroundJobId's FK is Restrict, and nothing else references
    // BackgroundJobs) — it's chunked purely to bound that delete statement's own direct row/log
    // volume. Unlike UpsertAwaitingMappingReadingsAsync's provider-specific chunk sizes (5000
    // Postgres / 200 SqlServer, driven by each provider's differing per-statement parameter
    // ceiling), the bottleneck here is cascade/log-volume, not parameter count — not meaningfully
    // asymmetric across providers, so one shared constant for both loops is deliberate.
    private async Task DeleteEligibleAsync(IReadOnlyList<Guid> jobIds, IReadOnlyList<Guid> importIds, CancellationToken cancellationToken)
    {
        // Incident fix round 2: measured once, up front — the cost driver is how many
        // SmartPlugReading rows each import's SetNull cascade will touch, not how many imports
        // there are. Missing from this dictionary (queried only for ids in importIds) means zero
        // readings; ChunkImportIdsByReadingVolume treats it that way.
        //
        // Round-2 review finding (Blind Hunter + Edge Case Hunter, independently): a single
        // GROUP BY against the full, unchunked importIds list reuses the exact Contains(...) shape
        // DeleteBatchSize's own comment confirmed translates to one SQL parameter per id — for a
        // household with enough eligible imports, that alone risks SQL Server's ~2100-parameter
        // ceiling before the (correctly chunked) delete loop even starts. Measuring in
        // DeleteBatchSize-sized chunks too reuses that already-proven-safe parameter count instead
        // of introducing a second, unbounded query shape.
        //
        // Round-3 review finding (Blind Hunter): DeleteBatchSize's own value was chosen for a
        // different reason (cascade/log-volume per delete command, see its own comment) than what
        // this measurement query actually needs (parameter-count safety per read command) — the
        // two constraints are independent and DeleteBatchSize's value happens to satisfy both, not
        // because it was derived for this purpose. A deliberate reuse, not an oversight: introducing
        // a second constant here would add a knob with no evidence it needs to differ in practice.
        var importReadingCounts = new Dictionary<Guid, int>();
        foreach (var measurementBatch in importIds.Chunk(DeleteBatchSize))
        {
            var batchCounts = await dbContext.SmartPlugReadings
                .Where(r => r.SmartPlugImportId != null && measurementBatch.Contains(r.SmartPlugImportId!.Value))
                .GroupBy(r => r.SmartPlugImportId!.Value)
                .Select(g => new { ImportId = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);
            foreach (var x in batchCounts)
            {
                importReadingCounts[x.ImportId] = x.Count;
            }
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        foreach (var importIdBatch in ChunkImportIdsByReadingVolume(importIds, importReadingCounts, DeleteReadingVolumeThreshold, DeleteBatchSize))
        {
            await DeleteImportChunkAsync(importIdBatch, cancellationToken);
        }

        // BackgroundJobs last — SmartPlugImport.BackgroundJobId's FK is Restrict, so any paired
        // import row must already be gone before this delete can succeed. Chunked independently from
        // the importIds loop above (own DeleteBatchSize-sized batches over the full jobIds list, not
        // one batch per importIdBatch): jobIds also includes bare Failed jobs with no paired import
        // at all, which never appear in any importIdBatch (ChunkImportIdsByReadingVolume only ever
        // packs importIds), so this loop's chunk boundaries are structurally independent of the
        // import-volume-based ones above and can't be folded into the same per-chunk unit.
        foreach (var jobIdBatch in jobIds.Chunk(DeleteBatchSize))
        {
            await dbContext.BackgroundJobs
                .Where(j => jobIdBatch.Contains(j.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    // CAP-6: extracted from DeleteEligibleAsync's own per-chunk loop body so SweepExpiredAsync's
    // bounded path (DeleteBoundedChunkAsync above) reuses the exact same cascade-bounding delete
    // order instead of drifting independently — this is the part worth sharing (gaps -> detach ->
    // imports, the reading-volume-bounded cascade this file's whole incident-fix history is about);
    // the jobs delete step deliberately stays out of this helper and lives separately in each caller
    // (see the comment on DeleteEligibleAsync's own jobIds loop above for why).
    private async Task DeleteImportChunkAsync(IReadOnlyList<Guid> importIds, CancellationToken cancellationToken)
    {
        await dbContext.SmartPlugImportGaps
            .Where(g => importIds.Contains(g.SmartPlugImportId))
            .ExecuteDeleteAsync(cancellationToken);

        // Incident fix round 4: explicitly detach each import's own SmartPlugReading rows in bounded
        // batches (see DetachReadingsForImportAsync above) before deleting the import itself — for a
        // terminal-state import this leaves nothing for the FK's SetNull behavior (Task 3/Story 3.6,
        // AD-20) to do at delete time. It can still be the one doing real work for a still-
        // Processing/AwaitingPowerPointMapping import (both eligible for DeleteJobsAsync by design): a
        // new SmartPlugReading row inserted concurrently via AddAsyncCore, after this loop's last
        // batch already observed zero remaining, is missed by this pass and falls back to the FK
        // cascade — same pre-existing TOCTOU already tracked in deferred-work.md, narrowed to just
        // the race-window rows, not closed by this fix.
        foreach (var importId in importIds)
        {
            await DetachReadingsForImportAsync(importId, cancellationToken);
        }

        await dbContext.SmartPlugImports
            .Where(i => importIds.Contains(i.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
