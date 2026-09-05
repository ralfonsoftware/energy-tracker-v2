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
            // Power Point — skip the doomed set-based attempt (avoids a wasted round trip on a
            // large import) and go straight to the bounded per-row fallback.
            await UpdateMappingPerRowWithConflictToleranceAsync(import.Id, powerPointId, powerPointName, roomName, cancellationToken);
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
                await UpdateMappingPerRowWithConflictToleranceAsync(import.Id, powerPointId, powerPointName, roomName, cancellationToken);
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

    private async Task UpdateMappingPerRowWithConflictToleranceAsync(
        Guid smartPlugImportId, Guid powerPointId, string powerPointName, string? roomName, CancellationToken cancellationToken)
    {
        var readings = await dbContext.SmartPlugReadings
            .Where(r => r.SmartPlugImportId == smartPlugImportId)
            .ToListAsync(cancellationToken);

        foreach (var reading in readings)
        {
            var previousPowerPointId = reading.PowerPointId;
            reading.PowerPointId = powerPointId;
            reading.PowerPointName = powerPointName;
            reading.RoomName = roomName ?? reading.RoomName;

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                dbContext.Entry(reading).State = EntityState.Detached;

                // Confirm this is really the (PowerPointId, IntervalStart) unique-constraint
                // conflict this fallback exists for (AD-2 — no provider-specific error inspection)
                // rather than an unrelated failure that would otherwise vanish silently. Story 3.7
                // AC #1/#2: also pull the colliding row's DeviceName/KwhValue/IntervalEnd here so
                // an exact duplicate can be resolved (deleted) instead of left orphaned forever.
                // FirstOrDefaultAsync, not SingleOrDefaultAsync — AD-20's own rationale is "don't
                // over-trust the DB constraint alone"; a genuine (PowerPointId, IntervalStart)
                // uniqueness violation must fall through to the historical "unrelated failure,
                // rethrow" path below rather than crash this fallback with an unhandled
                // InvalidOperationException.
                var conflictingReading = await dbContext.SmartPlugReadings.AsNoTracking()
                    .Where(r => r.PowerPointId == powerPointId && r.IntervalStart == reading.IntervalStart)
                    .Select(r => new { r.DeviceName, r.KwhValue, r.IntervalEnd })
                    .FirstOrDefaultAsync(cancellationToken);
                if (conflictingReading is null)
                {
                    reading.PowerPointId = previousPowerPointId;
                    throw;
                }

                // AC #1: an exact duplicate — same DeviceName/KwhValue/IntervalEnd as the
                // already-mapped reading (HouseholdId equality is implicit: both rows are read
                // through this same request-scoped DbContext, so AD-3's global query filter
                // already scopes both to the current household) — is dead data now that the
                // mapped row is authoritative; delete it instead of leaving it behind with
                // PowerPointId still NULL forever (Story 3.4 Dev Notes Open Question #4's AD-20
                // gap, confirmed live in production at 179,324-row scale — see this story's
                // Context). DeviceName must be part of the match: a Power Point can receive
                // manually-mapped readings from more than one distinct SmartPlugImport/device
                // over time (MapSmartPlugImportToPowerPoint imposes no device-identity
                // constraint), so two different devices' readings could otherwise coincide on
                // IntervalStart/KwhValue/IntervalEnd without actually being the same duplicate.
                // Set-based, same idiom as the ExecuteUpdateAsync fast path above; `reading` is
                // already detached so this can't be done via the change tracker.
                if (conflictingReading.DeviceName == reading.DeviceName
                    && conflictingReading.KwhValue == reading.KwhValue
                    && conflictingReading.IntervalEnd == reading.IntervalEnd)
                {
                    var deletedCount = await dbContext.SmartPlugReadings
                        .Where(r => r.Id == reading.Id)
                        .ExecuteDeleteAsync(cancellationToken);

                    // deletedCount can be 0 if a concurrent operation already removed this exact
                    // row between the conflict-confirmation read above and this delete — don't
                    // claim a deletion that didn't happen.
                    if (deletedCount > 0)
                    {
                        logger.LogWarning(
                            "Deleted duplicate SmartPlugReading {SmartPlugReadingId} (import {SmartPlugImportId}) instead of mapping it to " +
                            "PowerPointId={PowerPointId}: an already-mapped reading with identical DeviceName/KwhValue/IntervalEnd already " +
                            "exists at IntervalStart={IntervalStart:O} for that Power Point.",
                            reading.Id, smartPlugImportId, powerPointId, reading.IntervalStart);
                    }
                    else
                    {
                        logger.LogWarning(
                            "SmartPlugReading {SmartPlugReadingId} (import {SmartPlugImportId}) was already removed by the time its " +
                            "duplicate-mapping conflict against PowerPointId={PowerPointId} at IntervalStart={IntervalStart:O} was resolved " +
                            "— no delete was needed.",
                            reading.Id, smartPlugImportId, powerPointId, reading.IntervalStart);
                    }
                }
                else
                {
                    // AC #2: genuinely divergent data at the same key (e.g. a DST fall-back
                    // duplicate local timestamp) — never silently discard data that might
                    // actually differ. Same tolerant behavior as before this story: leave the
                    // reading unmapped, just log it.
                    logger.LogWarning(
                        "Skipped mapping SmartPlugReading {SmartPlugReadingId} (import {SmartPlugImportId}) to PowerPointId={PowerPointId}: " +
                        "a reading already exists at IntervalStart={IntervalStart:O} for that Power Point (unique-constraint conflict, " +
                        "possibly a DST fall-back duplicate local timestamp).",
                        reading.Id, smartPlugImportId, powerPointId, reading.IntervalStart);
                }
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
        var eligible = await (
            from job in dbContext.BackgroundJobs
            where job.HouseholdId == householdId && job.JobType == JobTypes.ProcessSmartPlugImport
            join import in dbContext.SmartPlugImports on job.Id equals import.BackgroundJobId into importGroup
            from import in importGroup.DefaultIfEmpty()
            let completedAtUtc = import != null ? import.CompletedAtUtc : job.CompletedAtUtc
            where completedAtUtc != null && completedAtUtc < cutoffUtc
                && (job.Status == BackgroundJobStatus.Failed
                    || (job.Status == BackgroundJobStatus.Completed && import != null
                        && (import.Status == SmartPlugImportStatus.Completed || import.Status == SmartPlugImportStatus.FlaggedForReview)))
            select new { BackgroundJobId = job.Id, SmartPlugImportId = (Guid?)(import == null ? null : import.Id) }
        ).ToListAsync(cancellationToken);

        if (eligible.Count == 0)
        {
            return;
        }

        // Set-based, in FK-dependency order (UpdateMappingAsync's own doc comment establishes the
        // same discipline for this table) — never load-then-remove, these tables can hold
        // hundreds of thousands of rows.
        var importIds = eligible.Where(x => x.SmartPlugImportId is not null).Select(x => x.SmartPlugImportId!.Value).ToList();
        var jobIds = eligible.Select(x => x.BackgroundJobId).ToList();

        if (importIds.Count > 0)
        {
            await dbContext.SmartPlugImportGaps
                .Where(g => importIds.Contains(g.SmartPlugImportId))
                .ExecuteDeleteAsync(cancellationToken);

            // Task 3's SetNull FK detaches (never deletes) the matching SmartPlugReading rows
            // automatically at the database level (AD-20).
            await dbContext.SmartPlugImports
                .Where(i => importIds.Contains(i.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        // BackgroundJobs last — SmartPlugImport.BackgroundJobId's FK is Restrict, so any paired
        // import row must already be gone before this delete can succeed.
        await dbContext.BackgroundJobs
            .Where(j => jobIds.Contains(j.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
