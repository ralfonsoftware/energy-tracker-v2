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
    // AD-23: two set-based write paths inside one explicit transaction, so no partial import is observable.
    // ADR 0005: why the transaction is explicit and how a failed call is cleaned up.
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
            // The rollback leaves `import` tracked, which would break the caller's failure path with a second error.
            // Detach only what this call tracked (snapshot compare, not a State filter or Clear()). ADR 0005.
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
        // The 120s command timeout is scoped to this import path, not app-wide. ADR 0005.
        dbContext.Database.SetCommandTimeout(TimeSpan.FromSeconds(120));

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        if (boundaryCorrection is not null)
        {
            // AD-22/AD-11: the KwhValue correction and its audit record commit with the import or not at all.
            // The affected-row check keeps a vanished target row from producing an audit record.
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
            // Two rows with the same match key in one batch make ON CONFLICT throw, so the first in parse order wins (AD-22).
            var deduplicatedReadings = DeduplicateByMatchKey(readings, import.Id);

            // The batch is homogeneous by construction (ProcessSmartPlugImport fills in the same
            // matchedPowerPoint, or none, for every reading in one call) — the first row's
            // PowerPointId decides which path the whole batch takes.
            if (deduplicatedReadings[0].PowerPointId is not null)
            {
                var config = new BulkConfig
                {
                    // PropertiesToExcludeOnUpdate, not PropertiesToExclude: Id is client-generated, so excluding it from the insert breaks NOT NULL.
                    PropertiesToExcludeOnUpdate = [nameof(SmartPlugReading.Id)],
                    UpdateByProperties = [nameof(SmartPlugReading.PowerPointId), nameof(SmartPlugReading.IntervalStart)],
                    // BulkCopyTimeout is separate from CommandTimeout, which EFCore.BulkExtensions never reads. Same 120s (ADR 0005).
                    BulkCopyTimeout = 120,
                };
                if (dbContext.Database.IsNpgsql())
                {
                    // Postgres only: BulkExtensions looks up the unique constraint with an empty schema and falls back to
                    // CREATE INDEX CONCURRENTLY; naming the schema avoids it. ADR 0005.
                    config.CustomDestinationTableName = "public.SmartPlugReadings";
                }
                else if (dbContext.Database.IsSqlServer())
                {
                    // SQL Server only: UseTempDB stages in a # temp table because the runtime identity has no CREATE TABLE right (AD-21).
                    // It needs the explicit transaction of AddAsyncCore. ADR 0005.
                    config.UseTempDB = true;
                }
                // BulkInsertOrUpdateAsync does not join an ambient transaction, so hand it this call's connection and transaction.
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

    // Duplicate match keys in one batch make both ON CONFLICT paths throw. The key mirrors each path's own:
    // (PowerPointId, IntervalStart) mapped, (DeviceName, IntervalStart) unmapped (AD-20). First in parse order wins.
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

    // AD-2 amended: the one provider-native raw-SQL path, because UpdateByProperties cannot target the partial
    // unique index's predicate. Chunked below each provider's parameter limit; one transaction spans every chunk. ADR 0005.
    private async Task UpsertAwaitingMappingReadingsAsync(List<SmartPlugReading> readings, CancellationToken cancellationToken)
    {
        // 5000 rows x 9 parameters stays under Postgres's 65535 limit; SQL Server's practical ceiling (~2100) needs 200.
        // The guard below fails fast when these literals drift from AwaitingMappingColumnsPerRow.
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

        // ON CONFLICT must match the unmapped unique index's columns and predicate exactly (AD-20). DeviceName is
        // part of the key, so it stays out of DO UPDATE. Raw SQL bypasses the query filter; each row carries its own HouseholdId (AD-3).
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

        // target.[PowerPointId] IS NULL scopes the match to the unmapped index's rows. DeviceName is part of the match, not of
        // UPDATE SET: the collation could rewrite the AD-10 snapshot. HOLDLOCK stops concurrent MERGEs from racing into a duplicate.
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
