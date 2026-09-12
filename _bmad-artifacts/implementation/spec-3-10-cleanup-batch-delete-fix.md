---
title: 'Batch DeleteEligibleAsync to fix "delete everything" 500'
type: 'bugfix'
created: '2026-09-12'
status: 'done'
review_loop_iteration: 1
baseline_commit: '4f4df701caff52d89fc46bf1dc0d5fe25b14ec94'
context: ['{project-root}/_bmad-artifacts/specs/spec-job-cleanup-bulk-delete-timeout/SPEC.md']
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `DELETE /api/smart-plug-import-jobs?deleteAll=true` 500s in production: `DeleteEligibleAsync` (`SmartPlugImportRepository.cs:749-773`) deletes/cascades every eligible row in one unbatched transaction, and on a household with enough history that single command's log volume saturates Azure SQL Basic-tier and exceeds the 120s `CommandTimeout`. Full root cause + evidence in the linked canonical spec.

**Approach:** Chunk `DeleteEligibleAsync`'s three `ExecuteDeleteAsync` calls into fixed-size ID batches, looping inside the *same* existing outer transaction. `CommandTimeout` is per-command, not per-transaction, so bounding each command's row count fixes the timeout without weakening atomicity or needing per-chunk transactions.

## Boundaries & Constraints

**Always:**
- Same outer `BeginTransactionAsync`/`CommitAsync` wraps every chunk (preserves the existing atomicity guarantee, no per-chunk transactions).
- Preserve FK-dependency order (`SmartPlugImportGaps` → `SmartPlugImports` → `BackgroundJobs`), within and across chunks.
- Only `DeleteEligibleAsync`'s execution changes — `SweepExpiredAsync`/`DeleteJobsAsync` eligibility queries untouched.
- Chunk size is a named constant with a doc comment stating why (mirrors `Program.cs:140-159`'s style).

**Ask First:** none — both decisions the canonical spec left open (chunk size, transaction scope) are resolved below.

**Never:** per-chunk transactions; changes to `CleanUpSmartPlugImportJobs.cs` or the API endpoint; async-job-queue or DB-tier changes (canonical spec's non-goals).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Small eligible set | Below one chunk | Same result as today, one loop iteration | N/A |
| Large eligible set | Thousands eligible, `cutoffUtc: null` | All deleted across multiple chunks; no single command exceeds chunk size; no `CommandTimeout` | N/A |
| Failure mid-batch | Forced failure after chunk 1 executes | Whole operation rolls back, including chunk 1 | Exception propagates, zero rows deleted |

</frozen-after-approval>

## Code Map

- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:749-773` -- `DeleteEligibleAsync` -- chunk the 3 `ExecuteDeleteAsync` calls
- `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs` -- add cases below (Postgres-only Testcontainers, matching this file's existing convention for portable EF Core LINQ — see Design Notes)
- `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryDeleteJobsSqlServerTests.cs` (new) -- one focused SqlServer Testcontainers test proving the multi-batch delete against the actual production provider (see Design Notes: round-1 review finding)

## Tasks & Acceptance

**Execution:**
- [x] `SmartPlugImportRepository.cs` -- add `internal const int DeleteBatchSize = 200;` above `DeleteEligibleAsync`. (`internal`, not `private`, so the test project — already covered by the existing `InternalsVisibleTo` in `EnergyTracker.Infrastructure.csproj` — can reference it directly instead of duplicating the literal.)
- [x] `SmartPlugImportRepository.cs` -- rewrote `DeleteEligibleAsync`: kept the outer transaction; `foreach (var batch in importIds.Chunk(DeleteBatchSize))` → gaps delete then imports delete scoped to `batch`; `foreach (var batch in jobIds.Chunk(DeleteBatchSize))` → jobs delete scoped to `batch`
- [x] `SmartPlugImportRepository.cs` -- rewrite the `DeleteBatchSize`/`DeleteEligibleAsync` doc comments per round-1 review: drop the parameter-ceiling non-sequitur (state the SQL Server ~2100 limit only as an incidental secondary margin, not the reason for 200); explicitly distinguish the *import* chunk loop's rationale (bounds the downstream `SmartPlugImports` → `SmartPlugReading` `SetNull` cascade) from the *job* chunk loop's rationale (bounds the delete's own direct row/log volume — `BackgroundJobs` has no downstream cascade at all, `SmartPlugImport.BackgroundJobId`'s FK is `Restrict`); acknowledge (don't silently omit) that a single import with an extreme reading count is a known, still-accepted residual risk this fix does not fully close, distinct from and in addition to the wall-clock/ingress-timeout risk already noted; note the deliberate departure from `UpsertAwaitingMappingReadingsAsync`'s provider-specific batch size (5000 Postgres / 200 SqlServer) — that precedent exists because a hard per-statement *parameter* ceiling differs by provider; here the bottleneck is cascade/log-volume, not parameter count, so one constant is deliberate, not an oversight
- [x] `SmartPlugImportRepositoryTests.cs` -- `DeleteJobsAsync_with_no_cutoff_deletes_every_eligible_row_across_multiple_batches`: seeded `DeleteBatchSize + 1` eligible Completed imports (each with a gap), asserts all gone and count matches
- [x] `SmartPlugImportRepositoryTests.cs` -- same test: also seed 1-2 `SmartPlugReading` rows per import (round-1 finding: no test exercised the `SetNull` cascade at all) and assert every reading survives with `SmartPlugImportId == null` after the multi-batch delete
- [x] `SmartPlugImportRepositoryTests.cs` -- `DeleteJobsAsync_rolls_back_every_chunk_when_a_later_chunk_fails`: seeded 2 batches' worth via a plain migrated context, then a `DbCommandInterceptor` (`FailAfterCommandCountInterceptor`) lets exactly the first chunk's commands complete and blocks the next one from starting, proving the whole operation rolls back (zero rows removed) via a fresh verification `DbContext`. Required a small test-only addition, `OpenDbContextWithInterceptor` (no `MigrateAsync` call, so the interceptor never sees migration DDL) alongside the existing `OpenMigratedDbContextAsync`.
- [x] `SmartPlugImportRepositoryTests.cs` -- same test: also seed a `SmartPlugImportGap` per import (round-1 finding: rollback was never verified for the gaps table, the first one touched in each chunk) and assert gaps survive too; rework `FailAfterCommandCountInterceptor` to count in `NonQueryExecutedAsync` (commands that actually *completed*) and block in `NonQueryExecutingAsync`, so "chunk 1 already ran" is an observed fact, not an assumption about exactly which/how many commands a chunk emits
- [x] `SmartPlugImportRepositoryTests.cs` -- new test `DeleteJobsAsync_with_no_cutoff_deletes_every_eligible_Failed_job_with_no_paired_import_across_multiple_batches` (round-1 finding, Edge Case Hunter): seed `DeleteBatchSize + 1` Failed jobs with **no** paired `SmartPlugImport` row, proving the `jobIds` chunking loop independently of the `importIds` loop (the two existing multi-batch tests always pair one job to one import 1:1, so a jobs-only chunk boundary was never exercised)
- [x] Small-scale/no-op-batching coverage (CAP-3): confirmed via the pre-existing single-row `DeleteJobsAsync_*`/`SweepExpiredAsync_*` tests in `SmartPlugImportRepositoryTests.cs` (all seed exactly 1 eligible row, well under `DeleteBatchSize`, and all still pass unchanged) — no new dedicated small-scale test needed since chunking a 1-element sequence is trivially one chunk and this is already exercised on every existing single-item test (round-2 review finding: this satisfaction wasn't explicitly noted in the Tasks list, only implied)
- [x] `SmartPlugImportRepositoryDeleteJobsSqlServerTests.cs` (new) -- one test mirroring the Postgres multi-batch test (`MsSqlContainer`/`Testcontainers.MsSql`, `EnergyTracker.Infrastructure.Migrations.SqlServer`, pattern from `SmartPlugImportRepositoryAddAsyncMinimalSqlServerPermissionsTests.cs`) proving the batched delete works against the actual production provider. Deliberately just this one scenario, not a full duplicate suite — the chunking/atomicity logic is portable EF Core LINQ with no provider-specific path (this file's own established convention for when Postgres-only suffices), but the canonical spec (`_bmad-artifacts/specs/spec-job-cleanup-bulk-delete-timeout/SPEC.md`, CAP-1) explicitly required verification against both providers and this incident specifically hit the SqlServer-hosted production database, so one real smoke-test against it closes that gap rather than relying on portability-by-inspection alone.

**Acceptance Criteria:**
- Given an eligible set spanning 3+ chunks, when `DeleteJobsAsync` runs, then every eligible row is deleted, returned count matches, and no single `ExecuteDeleteAsync` call exceeds `DeleteBatchSize` ids — verified against both Postgres and SqlServer.
- Given a forced failure after ≥1 chunk has actually completed, when the exception propagates, then no rows from any chunk (jobs, imports, or gaps) remain deleted.
- Given a jobs-only eligible set (no paired imports) spanning multiple chunks, when `DeleteJobsAsync` runs, then every job is deleted.
- Given existing `SweepExpiredAsync`/`DeleteJobsAsync`-with-cutoff tests, when run against the batched code, then all pass unchanged.

## Spec Change Log

- **Round 1 (bad_spec, root cause in this spec's own Design Notes/Tasks wording, no Ralf renegotiation needed):** Blind Hunter + Edge Case Hunter review of the round-1 implementation surfaced two spec-level gaps (both outside `<frozen-after-approval>`): (1) the `DeleteBatchSize` doc comment claimed to bound the exact cascade scenario it cited as motivation (a single import with hundreds of thousands of readings) when chunking by *import count* does not actually bound that — it bounds typical/average cascade volume across a batch, not a single pathologically large import within one; the comment also justified 200 against SQL Server's parameter ceiling immediately after stating that ceiling wasn't the real constraint, a non sequitur. (2) the Design Notes unilaterally decided "Postgres-only, no SqlServer twin needed" citing this file's existing convention, without logging that as a change against the canonical spec's (`spec-job-cleanup-bulk-delete-timeout/SPEC.md`) explicit CAP-1 success criterion requiring verification against both providers — a real gap given this incident specifically hit the SqlServer-hosted production database. **Resolution:** corrected the doc comments to honestly scope what chunking does and doesn't bound (single-huge-import remains an accepted residual risk, not silently claimed as solved); added one targeted SqlServer Testcontainers test satisfying the canonical spec's literal requirement without duplicating the full Postgres test suite (this file's established convention for genuinely-portable-EF-LINQ code still holds — the gap was "zero SqlServer proof for a SqlServer-provider incident," not "the convention itself is wrong"). **KEEP:** the chunking approach itself (import-count-based, shared outer transaction, no per-chunk transactions) — round 1's mechanism was correct, only the comment's claims and the test-coverage decision needed fixing.
- **Round 1 patches (folded into the re-derivation above, not separate loopbacks):** no `SmartPlugReading` rows seeded in either new test, so the `SetNull` cascade itself was never actually exercised; the rollback test never seeded or verified `SmartPlugImportGaps`; the rollback test's interceptor counted *attempted* commands rather than *completed* ones, making "chunk 1 already ran" an assumption rather than an observed fact; no test exercised the `jobIds` chunking loop independently of the `importIds` loop (both existing tests paired jobs 1:1 with imports).
- **Round 1 deferred (not this fix's problem — see `deferred-work.md`):** no upper bound on total chunk count per call (an extremely large household could still fail to complete "delete everything" within an HTTP/ingress timeout even after batching, since atomicity across all chunks is intentionally preserved per CAP-2 — relaxing that is an explicit non-goal here); a long-held single transaction blocks Postgres autovacuum on every table it touches for the transaction's full duration (currently inert — the deployed database is SqlServer — but real if this ever runs against Postgres); the eligibility query (computed once, before the now-longer delete loop) has a wider TOCTOU window than before; `DeleteBatchSize = 200` has no benchmarked basis and no telemetry to detect if it needs tuning later.
- **Round 2 (patch, no loopback — Blind Hunter + Edge Case Hunter found only patch/reject-tier issues):** the new SqlServer test's comment claimed to mirror the Postgres test "exactly" but omitted the `SmartPlugReading` seeding/assertion the Postgres test had just gained — the one provider-specific test skipped the exact FK cascade the fix exists to bound; fixed by adding the same reading seed + assertion there too. The rollback-test interceptor's synchronous `NonQueryExecuting` override gated on `_completed` but nothing incremented it on the sync path (only `NonQueryExecutedAsync` did) — an inconsistency that would silently reintroduce round-1's "attempted vs. completed" bug the moment any sync command path is ever exercised (none is, today); fixed by adding a matching sync `NonQueryExecuted` override. The `DeleteBatchSize` comment's "~2100-parameter ceiling" framing was flagged as an unverified assumption about how EF Core translates `Contains(...)` for this query shape; resolved with real evidence already in hand — the 2026-09-12 incident's own captured `DbCommand` log shows this exact predicate translating to one named parameter per id (`@importIds1` … `@importIds60`), not a single array/JSON parameter, so the comment now cites that directly instead of a general claim. The rollback test still seeded zero `SmartPlugReading` rows, so it couldn't prove a `SetNull` detach inside a rolled-back chunk itself rolls back; fixed by seeding a reading per import there too and asserting survivors keep their original `SmartPlugImportId`. The rollback test's `Should.ThrowAsync<InvalidOperationException>` checked only the exception type; added a message assertion so an unrelated `InvalidOperationException` couldn't false-pass it. The `DeleteBatchSize` comment claims the single-huge-import residual risk is "tracked in deferred-work.md," but no such entry existed yet; added it. **Rejected as noise or out of scope:** narrative duplicated across code comments/spec/deferred-work.md (matches this project's own established documentation convention); no test directly inspects generated SQL parameter counts (would mean testing BCL `Chunk()` correctness, not this fix's logic); the SqlServer test file duplicates small scaffolding already in `SmartPlugImportRepositoryAddAsyncMinimalSqlServerPermissionsTests.cs` (matches this test project's existing per-file convention for standalone Testcontainers classes); no `SweepExpiredAsync`-specific multi-batch test (redundant — `DeleteEligibleAsync` is fully shared with no caller-specific branching, so `DeleteJobsAsync`'s multi-batch coverage already exercises 100% of the chunking logic `SweepExpiredAsync` also relies on); a theoretical "`jobIds` empty → chunk loop no-ops instead of issuing a harmless no-op delete" behavior nuance (both current call sites guarantee `jobIds` is non-empty before calling; no observable behavior difference either way).

## Design Notes

**Shared transaction, not per-chunk:** `CommandTimeout` bounds one command's execution, not the transaction's total duration — chunking removes the per-command risk while keeping full atomicity. Per-chunk transactions were rejected: weaker atomicity, no timeout benefit `CommandTimeout` doesn't already give per-command.

**`DeleteBatchSize = 200`:** bounds the *typical* per-chunk cascade, not a worst case — a single import within a chunk can itself carry hundreds of thousands of `SmartPlugReading` rows (Story 3.3/`Program.cs:140-146`), and no amount of import-count-based chunking bounds that single import's own cascade size. This fix reduces risk for the reported incident's actual shape (many accumulated jobs/imports over time) but does not claim to solve the different edge case of one pathologically large individual import — see Residual risk below and the round-1 Spec Change Log entry. 200 is a starting value from the incident's evidence (not a benchmarked optimum), chosen well under SQL Server's ~2100-parameter ceiling as an incidental secondary margin — that ceiling is not why 200 was chosen (the cascade, not the parameter count, is the actual constraint that mattered in the incident).

**Two distinct chunking rationales, one constant:** the `importIds` loop chunks to bound a *downstream* cascade (`SmartPlugImports` delete → `SmartPlugReading` `SetNull` via FK); the `jobIds` loop chunks for a different reason — `BackgroundJobs` has no downstream cascade at all (`SmartPlugImport.BackgroundJobId`'s FK is `Restrict`, and nothing else references it), so that loop is only bounding the delete statement's own direct row/log volume. Both happen to use the same `DeleteBatchSize`; unlike `UpsertAwaitingMappingReadingsAsync`'s provider-specific chunk sizes (5000 Postgres / 200 SqlServer, driven by each provider's differing per-statement *parameter* ceiling), the bottleneck here is cascade/log-volume, which isn't meaningfully asymmetric across providers — so one shared constant is a deliberate choice, not an oversight.

**Dual-provider:** the chunking/transaction logic itself is pure portable EF Core LINQ (`Where(...).Contains(...).ExecuteDeleteAsync()`, `BeginTransactionAsync`/`CommitAsync`), no raw SQL or provider branch — matching this file's own established convention that Postgres-only Testcontainers coverage suffices for genuinely-portable EF behavior (reserving a real SqlServer twin for paths that are provider-divergent under the hood, e.g. `AddAsync`'s `SqlBulkCopy`/`COPY` internals). This incident specifically hit the SqlServer-hosted production database, though, and the canonical spec explicitly asked for both-provider verification — so round 1 adds exactly one SqlServer Testcontainers test (the multi-batch scenario) as a targeted proof against the real production provider, rather than either skipping it (round-1's original gap) or fully duplicating every scenario (unnecessary given the code is genuinely portable).

**Residual, non-blocking risks (accepted, not solved by this fix):** (1) chunking bounds each *command*, not total wall-clock time — an extreme backlog could still take several minutes end-to-end and hit a different ceiling (HTTP/ingress idle timeout), not exercised by this incident; (2) a single individual import with an extreme reading count is not bounded by import-count-based chunking (see above); (3) the shared transaction now stays open longer in wall-clock terms, which would hold locks / block Postgres autovacuum for that duration if this ever runs against Postgres (currently inert on the SqlServer-hosted production deployment). All out of scope per the canonical spec's non-goals (no async-job-queue move, no DB-tier change); see `deferred-work.md` for the full list flagged during round-1 review.

## Verification

**Commands:**
- `dotnet test --filter-class "*SmartPlugImportRepositoryTests*"` -- all pass, including new/updated cases
- `dotnet test --filter-class "*SmartPlugImportRepositoryDeleteJobsSqlServerTests*"` -- new SqlServer test passes
- `dotnet test` -- full suite green, no regression elsewhere
- `dotnet build` (Debug + Release) -- clean

## Suggested Review Order

**The fix itself**

- Entry point: the batched, still-atomic delete — start here to see the whole design in one place
  [`SmartPlugImportRepository.cs:785`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L785)
- The chunk-size constant and its (twice-corrected) reasoning — what it bounds, what it doesn't, why 200
  [`SmartPlugImportRepository.cs:757`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L757)

**Regression proof — multi-chunk correctness**

- Proves every eligible row (jobs, imports, gaps, readings) is deleted across a chunk boundary, on Postgres
  [`SmartPlugImportRepositoryTests.cs:920`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs#L920)
- Same proof against the real production provider (SqlServer) — this is the test round-1 review flagged as missing
  [`SmartPlugImportRepositoryDeleteJobsSqlServerTests.cs:73`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryDeleteJobsSqlServerTests.cs#L73)
- Proves the jobs-only chunking loop independently of the imports loop (no paired import row)
  [`SmartPlugImportRepositoryTests.cs:975`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs#L975)

**Regression proof — atomicity under a mid-batch failure**

- Proves a chunk that already completed still rolls back, including its SetNull detach — the CAP-2 guarantee
  [`SmartPlugImportRepositoryTests.cs:1058`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs#L1058)
- The interceptor making "already completed" an observed fact, not an assumption
  [`SmartPlugImportRepositoryTests.cs:1013`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs#L1013)
