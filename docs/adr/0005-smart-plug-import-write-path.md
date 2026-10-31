# 0005: Smart Plug import write path

Source: AD-23, AD-2 (amended 2026-09-04), AD-20 (amended 2026-10-10), AD-21, Stories 3.8, 3.9 and 11.4, production incidents of 2026-09-05.
Code: `SmartPlugImportRepository.Import.cs`.

## Context

A full-history Eve Home export holds hundreds of thousands of `SmartPlugReading` rows. The import has to be set-based (`BulkInsertOrUpdateAsync` for readings with a Power Point, a provider-native raw-SQL upsert for readings still awaiting a mapping), and "no partial import is observable" has to hold when the import is cancelled or fails halfway.

Several facts were found by running it, not by reading documentation:

- `BulkInsertOrUpdateAsync` and `ExecuteSqlRawAsync` do not take part in `SaveChangesAsync`'s pipeline and do not join an ambient transaction on their own. Its Postgres strategy runs `CREATE INDEX CONCURRENTLY`, which cannot run in any transaction other than the one it is told to reuse.
- On a failure the transaction rolls back in the database, but EF's change tracker keeps `import`. The failure path of `ProcessSmartPlugImport` adds a new `SmartPlugImport` with the same Id on the same scoped context and then fails a second time with "already being tracked", which hides the real error.
- A blanket `ChangeTracker.Clear()` was the first fix. It also detached the caller's own `BackgroundJob`, whose `Failed` status was then lost and left the job in `Processing` (production, 2026-09-05).
- A `State == Added` filter is not enough: `import` and the audit row reach `Unchanged` inside the still-open transaction before a later step fails.
- `EFCore.BulkExtensions` never reads `CommandTimeout`; the bulk copy has its own `BulkCopyTimeout`. Basic-tier Azure SQL hit 100% DTU for about two minutes, so 30 s was too short.
- With the SQL Server merge strategy the library stages through a permanent table (`SELECT ... INTO`), which needs `CREATE TABLE`. The runtime identity only has `db_datareader` and `db_datawriter` (AD-21), so this failed in Azure while every test, which logs in as admin, passed.
- The Postgres bulk path looks up the unique constraint with an empty schema name and falls back to `CREATE INDEX CONCURRENTLY` even when the constraint exists.
- Two rows with the same match key in one batch make `ON CONFLICT DO UPDATE` throw "cannot affect row a second time".
- `UpdateByProperties` cannot target the partial unique index (`WHERE PowerPointId IS NULL`) on either provider (Story 3.8 spike, finding 2).
- One unchunked multi-row upsert blew through Postgres's 65,535 parameter limit on a real 118k-row export (9 parameters per row). SQL Server's practical ceiling is about 2,100.

## Decision

- `AddAsync` opens one explicit transaction that spans the correction, the import row, the dedupe and every chunk of the readings write.
- On any failure it detaches only the entries that were not tracked before the call (a snapshot compare), then rethrows.
- The 120 s `CommandTimeout` is set on the scoped context inside `AddAsyncCore`, not app-wide. `BulkCopyTimeout` carries the same value. `UpdateMappingAsync` keeps its own 180 s.
- SQL Server stages through `UseTempDB` (a `#` temp table) and reuses the ambient transaction. Postgres names the schema explicitly through `CustomDestinationTableName`.
- Duplicate match keys in one batch are removed before either write path, first in parse order wins. The key is `(PowerPointId, IntervalStart)` mapped and `(DeviceName, IntervalStart)` unmapped.
- Unmapped readings use a hand-written upsert in chunks of 5,000 rows (Postgres) or 200 (SQL Server). `AwaitingMappingColumnsPerRow` ties the chunk size to the parameter limits and a guard fails fast if they drift.

## Consequences

- AD-2's "provider chosen once, at the composition root" has this one named exception.
- A new write added to `AddAsyncCore` is covered by the rollback and the snapshot cleanup without being listed anywhere.
- A package upgrade of `EFCore.BulkExtensions` can change the staging or schema behaviour: re-run the Infrastructure tests on both providers.
- The 120 s value applies to the whole job's scoped context from `AddAsyncCore` on (one job, one scope, one worker, AD-6).
