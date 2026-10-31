using System.Data.Common;
using EFCore.BulkExtensions;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

public partial class SmartPlugImportRepository
{
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
        // anything of its own — the failure-cleanup below must detach only what this call tracked,
        // never an entity the caller already had on the context.
        var trackedBeforeCall = new HashSet<object>(dbContext.ChangeTracker.Entries().Select(e => e.Entity));

        try
        {
            await AddAsyncCore(import, readings, boundaryCorrection, cancellationToken);
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
            // the caller had tracked on this same scoped DbContext — then BackgroundJobProcessor's
            // own tracked BackgroundJob row, whose Status = Failed mutation targeted a detached
            // entity and was dropped, orphaning the job at Status = Processing. Since Story 11.2 the
            // processor no longer tracks its job (every status change is a conditional update through
            // IBackgroundJobLifecycle), but the narrower cleanup below is still the right shape.
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
        SmartPlugImport import, IReadOnlyList<SmartPlugReading> readings,
        SmartPlugReadingCorrection? boundaryCorrection, CancellationToken cancellationToken)
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
    // match key — Postgres's own restriction on ON CONFLICT DO UPDATE, not a library quirk. The
    // key mirrors each path's own: a mapped reading is (PowerPointId, IntervalStart); an unmapped
    // one is (DeviceName, IntervalStart) (AD-20 amended 2026-10-10, Story 11.4 — DeviceName is
    // part of the unmapped key, so two plugs' rows at the same interval are not duplicates).
    // First-encountered-in-parse-order wins, same discipline as AD-22's own DST-fold handling in
    // ProcessSmartPlugImport and the deleted per-row-fallback's "earlier-processed reading
    // always wins" rule.
    private List<SmartPlugReading> DeduplicateByMatchKey(IReadOnlyList<SmartPlugReading> readings, Guid smartPlugImportId)
    {
        var seenKeys = new HashSet<(Guid? PowerPointId, string? DeviceName, DateTimeOffset IntervalStart)>();
        var deduplicated = new List<SmartPlugReading>(readings.Count);
        var duplicateCount = 0;

        foreach (var reading in readings)
        {
            var deviceName = reading.PowerPointId is null ? reading.DeviceName : null;
            if (seenKeys.Add((reading.PowerPointId, deviceName, reading.IntervalStart)))
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
    // proved BulkInsertOrUpdateAsync's UpdateByProperties cannot safely target the unmapped
    // partial unique index's predicate (WHERE PowerPointId IS NULL) on either provider — it throws
    // on both rather than silently corrupting a row, but the originally-specified single-mechanism
    // design doesn't work for this one path. A hand-written, provider-native raw-SQL upsert instead.
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
    private async Task UpsertAwaitingMappingReadingsAsync(List<SmartPlugReading> readings, CancellationToken cancellationToken)
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

    private Task<int> UpsertAwaitingMappingReadingsPostgresAsync(List<SmartPlugReading> readings, CancellationToken cancellationToken)
    {
        var (valuesClause, parameters) = BuildAwaitingMappingValuesClause(readings);

        // ON CONFLICT's own (HouseholdId, DeviceName, IntervalStart) WHERE PowerPointId IS NULL
        // predicate must match IX_SmartPlugReadings_Household_Device_IntervalStart_Unmapped's own
        // columns and predicate exactly (Postgres arbiter-index inference) — same predicate text as
        // that index's own migration SQL (AD-20 amended 2026-10-10, Story 11.4: DeviceName is part
        // of the key so two plugs' files covering the same period keep their own rows). The old
        // (HouseholdId, IntervalStart) index, which let a different plug's overlapping row conflict
        // loudly (23505) during deploy N, was dropped by deploy N+1. DeviceName is not in the DO UPDATE
        // list: it is part of the key, so the assignment would be a no-op. PowerPointId is never
        // listed in the insert column list, so every new row lands with PowerPointId NULL,
        // matching the batch this method is only ever called for.
        // HouseholdId is filtered nowhere else in this statement (AD-3: raw SQL bypasses the global
        // query filter) — every incoming row already carries its own HouseholdId column value, so
        // no separate WHERE HouseholdId=... clause is needed the way a SELECT would need one.
        var sql = $"""
            INSERT INTO "SmartPlugReadings" ("Id", "HouseholdId", "SmartPlugImportId", "RoomName", "PowerPointName", "DeviceName", "IntervalStart", "IntervalEnd", "KwhValue")
            VALUES {valuesClause}
            ON CONFLICT ("HouseholdId", "DeviceName", "IntervalStart") WHERE "PowerPointId" IS NULL
            DO UPDATE SET
                "SmartPlugImportId" = EXCLUDED."SmartPlugImportId",
                "RoomName" = EXCLUDED."RoomName",
                "PowerPointName" = EXCLUDED."PowerPointName",
                "IntervalEnd" = EXCLUDED."IntervalEnd",
                "KwhValue" = EXCLUDED."KwhValue";
            """;

        return dbContext.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
    }

    private Task<int> UpsertAwaitingMappingReadingsSqlServerAsync(List<SmartPlugReading> readings, CancellationToken cancellationToken)
    {
        var (valuesClause, parameters) = BuildAwaitingMappingValuesClause(readings);

        // The ON clause's own target.[PowerPointId] IS NULL requirement scopes every match to the
        // same row subset IX_SmartPlugReadings_Household_Device_IntervalStart_Unmapped covers — a
        // target row with a non-null PowerPointId can never match, closing exactly the
        // cross-Power-Point collision Story 3.8 spike Finding #2 found with UpdateByProperties.
        // DeviceName is part of the match (AD-20 amended 2026-10-10, Story 11.4), so a different
        // plug's row at the same interval is NOT MATCHED and is inserted (during deploy N the old
        // (HouseholdId, IntervalStart) unique index rejected it loudly; deploy N+1 dropped that index). DeviceName is not in the UPDATE SET list: it is part
        // of the key, and the default collation is case- and trailing-space-insensitive, so an
        // assignment could rewrite the by-value DeviceName snapshot (AD-10) of the row it matched.
        // WITH (HOLDLOCK) prevents a concurrent MERGE against the same key from racing this one
        // into a duplicate insert under READ COMMITTED — standard guidance for MERGE upserts.
        var sql = $"""
            MERGE INTO [SmartPlugReadings] WITH (HOLDLOCK) AS target
            USING (VALUES {valuesClause}) AS source ([Id], [HouseholdId], [SmartPlugImportId], [RoomName], [PowerPointName], [DeviceName], [IntervalStart], [IntervalEnd], [KwhValue])
            ON target.[HouseholdId] = source.[HouseholdId] AND target.[DeviceName] = source.[DeviceName] AND target.[IntervalStart] = source.[IntervalStart] AND target.[PowerPointId] IS NULL
            WHEN MATCHED THEN UPDATE SET
                target.[SmartPlugImportId] = source.[SmartPlugImportId],
                target.[RoomName] = source.[RoomName],
                target.[PowerPointName] = source.[PowerPointName],
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
    private static (string ValuesClause, object[] Parameters) BuildAwaitingMappingValuesClause(List<SmartPlugReading> readings)
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
}
