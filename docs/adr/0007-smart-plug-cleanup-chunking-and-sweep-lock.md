# 0007: Smart Plug cleanup, chunked deletes and the sweep lock

Source: AD-6 (extension), AD-20, AD-2, Stories 3.6 and 3.10, CAP-4 to CAP-6, production incidents of 2026-09-12 (rounds 1 to 4).
Code: `SmartPlugImportRepository.Cleanup.cs`. Open risks: `_bmad-artifacts/implementation/deferred-work.md`.

## Context

An import can carry hundreds of thousands of `SmartPlugReading` rows (the largest one seen had 122,158). Deleting jobs and imports has to keep every database command small on Basic-tier Azure SQL (5 DTU), where one large command saturated DTU and log IO for about two minutes and hit the 120 s `CommandTimeout`.

What the incidents showed:

1. Many accumulated jobs in one `DELETE` command saturated the log write rate. Chunking by id count (`DeleteBatchSize` = 200) fixed the typical case. A 200 id chunk is also well below SQL Server's parameter ceiling: `Contains(...)` translates to one named parameter per id here.
2. A household with only 51 to 60 eligible rows never reached count based chunking, yet five of its imports held 57k to 122k readings each. The cost driver is the readings touched by the `SetNull` cascade, so chunks are also bounded by cumulative reading count (`DeleteReadingVolumeThreshold` = 20,000, `ChunkImportIdsByReadingVolume`).
3. A single import of 122,158 readings, alone in its chunk, still timed out. The cascade cannot be bounded from the outside.
4. The automatic sweep runs inline on every `GET /api/smart-plug-import-jobs` poll, so even its eligibility `SELECT` has to be bounded by a query level `Take`, not a client side one.
5. Two open tabs polling at once chose the same oldest chunk and raced their deletes on row locks.

Both limits are starting values taken from the incident evidence, not benchmarked optima.

## Decision

- Each import's readings are detached explicitly, in `DeleteBatchSize` batches, before the import row is deleted (`DetachReadingsForImportAsync`). The `DELETE` that follows then has no children to cascade. The reading volume chunking stays, because it is tested and harmless.
- Gaps, then imports, then jobs are deleted in FK order, set based, never load then remove (`SmartPlugImport.BackgroundJobId` is `Restrict`). The jobs loop is chunked on its own: it also holds bare `Failed` jobs that have no import.
- One explicit transaction wraps all chunks of `DeleteEligibleAsync`. `CommandTimeout` bounds one command, not the transaction, so chunking does not weaken all or nothing. Per chunk transactions were rejected for that reason.
- `SweepExpiredAsync` takes at most one bounded chunk per call, oldest first with a `job.Id` tiebreaker. The remainder is picked up by the next poll, no cursor needed: a deleted row stops matching.
- Eligibility: terminal jobs only (`Failed`, or `Completed` with an import in `Completed` or `FlaggedForReview`). A job awaiting a mapping is not eligible. The join to the import is a `LEFT JOIN` (a `Failed` job can have no import) and the cutoff uses the import's own `CompletedAtUtc` when it exists, because mapping updates that value only.
- The manual cleanup (`DeleteJobsAsync`, Story 3.10) has its own eligibility query and may delete active states. Its age is `import.CompletedAtUtc`, else `job.CompletedAtUtc`, else `job.CreatedAtUtc`.
- A non-blocking, transaction scoped per Household advisory lock guards the bounded chunk and is taken first, so a losing poller does no reading count work. On Postgres: `pg_try_advisory_xact_lock(hashtextextended(householdId, 0))`. A hash collision only makes one poll skip a chunk. On SQL Server: `sp_getapplock` with `@LockTimeout = 0` and `@LockOwner = 'Transaction'`; the batch is non composable, so it runs through `ToListAsync()` and takes the single row client side. Both release on commit or rollback.
- A bare `Failed` job in the bounded take is deleted even when an older import waits for a later chunk. It is free work and every row still clears in a bounded number of calls.

## Consequences

- Residual risks, tracked in `deferred-work.md`: an import still receiving rows during the detach loop leaves a few rows to the FK cascade; the sweep pays the detach loop's wall clock inline on the poll that triggers it; no bound on the total time across chunks.
- Changing `DeleteBatchSize` also changes the parameter count of the reading count query, which was sized with it.
- A new provider needs its own lock primitive.
