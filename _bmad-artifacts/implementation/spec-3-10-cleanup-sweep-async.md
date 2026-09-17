---
title: 'Move SmartPlugImportJobs sweep off the synchronous GET poll path'
type: 'bugfix'
created: '2026-09-17'
status: 'done'
review_loop_iteration: 1
context: []
baseline_commit: '146c6d6afcbb93790fcfd4acc5b7c95075960c8f'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `ListSmartPlugImportJobs.ExecuteAsync` calls `SweepExpiredAsync` synchronously and inline on every `GET /api/smart-plug-import-jobs` call — the same request `job-history-list.tsx` polls every 8 seconds. Since `spec-3-10-cleanup-per-import-detach` (2026-09-12) made cleanup delete each large import's readings via hundreds of small sequential batched `UPDATE`s (`DetachReadingsForImportAsync`), a sweep that catches a terminal-state import old enough *and* large enough now runs that whole batched loop inline inside a GET request. This reintroduces, through a second, unaddressed entry point, the exact "operation too slow for a synchronous HTTP request" failure mode that `spec-3-10-cleanup-async-job` (round 3) was built to eliminate for the manual "clean up everything" button. Flagged **High priority** by adversarial review of `spec-3-10-cleanup-per-import-detach`; not yet a confirmed live incident, but the mechanism matches three of this feature's four confirmed incidents, and this entry point fires far more often than a manual click.

**Approach:** Stop doing deletion work inline on the read path. **Decision (human-approved 2026-09-17): bounded per-call chunking (approach b).** `SweepExpiredAsync` does at most one `DeleteBatchSize`/`DeleteReadingVolumeThreshold`-sized chunk of eligible work per invocation, then returns; the next poll (≤8s later) re-queries eligibility and continues where the prior call left off. No single GET request's inline work is ever unbounded, and the existing "swept row never appears in the same response that triggered its own sweep" guarantee is preserved (no relaxation, unlike approach a).

## Boundaries & Constraints

**Always:** `ListSmartPlugImportJobs.ExecuteAsync`'s response latency must not scale with an eligible import's reading count. FR-32/AD-6's six-state retention behavior (Success/Error/Flagged for Review fade out 30 days after completion; Waiting/Processing/Needs Mapping never auto-clear) must be preserved exactly — this is a where-the-sweep-runs change, not a what-gets-swept change. Each call's bounded chunk must make deterministic forward progress (process the oldest-eligible rows first) so a steady trickle of newly-eligible rows can never starve an older backlog.

**Ask First:** None remaining — approach (b) is confirmed; the "swept row never reappears" guarantee is preserved under it, so no further tradeoff sign-off is required.

**Never:** Do not restore an all-or-nothing synchronous transaction that processes every eligible row in one call. Do not touch `DeleteJobsAsync`'s (manual "clean up everything") call path — it's already off the synchronous path; this spec is scoped to `SweepExpiredAsync`'s remaining exposure only. Do not introduce a persisted cursor/checkpoint — partial progress is re-derived from the eligibility query on every call, since that query already re-runs on every call.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| GET poll, nothing eligible | No terminal-state import older than 30 days | Response returns at current baseline latency; no chunk work performed | N/A |
| GET poll, one small eligible import | Terminal-state import needing few detach batches (under both thresholds) | Entire import cleared in this single call's one chunk | N/A |
| GET poll, one pathologically large eligible import (100k+ readings) | Terminal-state import alone exceeds `DeleteReadingVolumeThreshold` | This call's one chunk contains just that import and still runs its full detach loop for it (an oversized single import can't be split further — same accepted residual as today's manual path); response latency for *this* call is bounded by that one import's detach cost, not by every other eligible import queued behind it | N/A |
| GET poll, many small eligible imports exceeding one chunk's capacity | Eligible set spans more than `DeleteBatchSize` imports or `DeleteReadingVolumeThreshold` total readings | Only the oldest-eligible chunk clears this call; remaining eligible rows are picked up by the next poll(s) | N/A |
| Concurrent polls from multiple open tabs of the same household | Two GETs arrive back-to-back | Each call re-queries eligibility and processes its own chunk against current DB state; no double-processing of rows already deleted by a prior call | N/A |

</frozen-after-approval>

## Code Map

- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:660` -- `SweepExpiredAsync`: add deterministic ordering (oldest-eligible first, with a tiebreaker) to its eligibility query, push the `DeleteBatchSize` bound into the query itself (not a client-side `.Take()` after `ToListAsync`), acquire the per-household advisory lock below before doing any bounded delete work, and bound deletion to one chunk instead of delegating to the full-sweep `DeleteEligibleAsync`.
- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:886` -- `DeleteEligibleAsync`: extract the gaps-delete/detach-readings/imports-delete steps of its per-chunk body into one shared private helper (`DeleteImportChunkAsync`) used by both this method's existing multi-chunk loop and the new bounded path below. The jobs-delete step deliberately stays separate at each call site -- see Task 3's own annotation below for why folding it in would be an artificial coupling, not an oversight.
- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:807` -- `DetachReadingsForImportAsync`: reused unchanged inside the shared per-chunk delete helper.
- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` (`ChunkImportIdsByReadingVolume`, `DeleteBatchSize`, `DeleteReadingVolumeThreshold`) -- reused unchanged; the bounded path only takes the *first* chunk this helper yields instead of iterating all of them.
- `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs` -- existing `SweepExpiredAsync_*` tests to extend with multi-call/bounded-progress, mixed-row, and concurrent-poll coverage.

## Tasks & Acceptance

**Execution:**
- [x] `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` -- add deterministic ordering to `SweepExpiredAsync`'s eligibility query: `orderby completedAtUtc, job.Id` (the `job.Id` tiebreaker guarantees a stable order even when two rows share the exact same `completedAtUtc`, which the retention window's `AddDays(-30)` cutoff makes plausible for imports parsed in the same batch) -- push `DeleteBatchSize` into the query itself (e.g. `.Take(DeleteBatchSize)` before `ToListAsync`, not a client-side `.Take()` on the already-materialized list) -- so a large backlog no longer costs a full-backlog-sized `SELECT` on every poll, only the query result actually bounds work. (Verified server-side, not just by row count -- see Task 5's added regression test.)
- [x] `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` -- add a per-household concurrency guard around the bounded delete: inside the chunk's own transaction, acquire a transaction-scoped Postgres advisory lock via `pg_try_advisory_xact_lock(hashtextextended(householdId::text, 0))` (non-blocking; `hashtextextended`'s full 64-bit output over plain `hashtext`'s 32-bit, per loop-2 review) before doing any delete work; if not acquired, another poll for this household is already mid-chunk -- roll back and return immediately without deleting anything (the next poll, ≤8s later, tries again). No manual unlock needed -- `pg_try_advisory_xact_lock` releases automatically at the transaction's commit or rollback, avoiding any connection-lifecycle/pooling risk from a manually-paired lock/unlock. This closes the gap the review surfaced: without it, two concurrent GETs (e.g. two open tabs) can both select the same oldest-eligible chunk and the second's `DELETE` blocks on row locks held by the first's still-in-flight `DetachReadingsForImportAsync` loop -- bounded by one chunk's own duration, but still a real, avoidable latency spike for a scenario this spec's own I/O matrix explicitly requires handling. (Implemented for both supported providers -- this repo also runs on SQL Server, which has no `pg_try_advisory_xact_lock` equivalent; `sp_getapplock` with `@LockOwner='Transaction', @LockTimeout=0` provides the same non-blocking, transaction-scoped semantics there. Loop-2 review: the lock acquisition was reordered to run *before* measuring reading counts/packing the chunk, not after, so a losing poller pays only for the eligibility query, not the reading-volume `GROUP BY` too. Loop-2 review also caught a real bug this reordering's own first SQL-Server test exposed: the `sp_getapplock` batch's `DECLARE`/`EXEC`/`SELECT` is non-composable SQL, and `SqlQuery<T>().SingleAsync()` throws trying to compose it -- fixed by switching to `.ToListAsync()` + client-side `.Single()`. This method had zero SQL Server test coverage before loop 2; see Task 5.)
- [x] `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` -- extract the shared per-chunk delete body (gaps delete, detach-readings loop, imports delete, jobs delete, same FK-dependency order as today) into one private helper called from both `DeleteEligibleAsync`'s existing multi-chunk loop and the new bounded path -- avoids the two independently duplicating that same four-step sequence. (Extracted as `DeleteImportChunkAsync` covering gaps/detach/imports -- the three steps that share the reading-volume-bounded cascade logic this file's incident-fix history is about. The jobs-delete step stays separate in each caller: `DeleteEligibleAsync`'s own jobIds loop chunks independently by count over its full jobIds list -- which also includes bare Failed jobs that never appear in any import-volume chunk -- while the bounded path's jobIdChunk is already small enough for one delete; folding it into the shared helper would have forced an artificial coupling between two structurally unrelated chunk boundaries. Documented inline at both call sites.)
- [x] `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` -- add the bounded delete path used only by `SweepExpiredAsync`: from the (now query-bounded) eligible rows, measure reading counts for the import-bearing ones, run `ChunkImportIdsByReadingVolume` over them and take only its first yielded chunk, then run the shared per-chunk delete helper for that chunk's imports plus any import-less Failed jobs in the same bounded take, inside one transaction scoped to that chunk only.
- [x] `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` -- point `SweepExpiredAsync` at the new bounded path instead of `DeleteEligibleAsync`; `DeleteJobsAsync` keeps calling `DeleteEligibleAsync` unchanged.
- [x] `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs` -- add tests for: (a) eligible set larger than one chunk requires two `SweepExpiredAsync` calls to fully clear, oldest first, extended to a third call (two full chunks + a remainder) so an off-by-one in the ordering/skip logic can't hide behind a two-call test; (b) a single import whose reading count alone exceeds `DeleteReadingVolumeThreshold` is still fully cleared, alone, in one call; (c) a bounded take that mixes import-less Failed jobs with import-bearing eligible rows still processes both correctly in one call; (d) two concurrent `SweepExpiredAsync` calls for the same household (simulated via two repository instances against two DB contexts racing on the advisory lock) result in exactly one of them performing the delete and the other returning a no-op, with no error and no duplicate-processing. (Loop-2 review additions: (e) a dedicated regression test asserting the eligibility query's generated SQL actually contains a server-side `LIMIT`, not just that the right row count clears -- a row-count-only assertion can't distinguish a pushed-down bound from a client-side `.Take()` on a fully materialized list, which was Loop 1's own finding; (f) `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryDeleteJobsSqlServerTests.cs` -- the `sp_getapplock` lock path had zero SQL Server coverage anywhere, despite this test project's own documented convention that provider-specific code requires dedicated SQL Server verification (see that file's own header comment) -- added the SQL Server mirror of test (d)'s no-op-under-contention scenario, which is what surfaced the composability bug fixed in Task 2.)

**Acceptance Criteria:**
- Given a household whose terminal-state Smart Plug import jobs include one requiring many detach batches, when `GET /api/smart-plug-import-jobs` is polled, then the response returns within this endpoint's existing baseline latency, bounded by at most one chunk's worth of detach work, never by the full eligible backlog.
- Given the FR-32/AD-6 six-state retention rule, when the sweep runs across one or more calls, then Success/Error/Flagged for Review rows still fade out at exactly 30 days post-completion and Waiting/Processing/Needs Mapping rows are never auto-cleared.
- Given an eligible backlog spanning multiple chunks, when consecutive polls each trigger `SweepExpiredAsync`, then every eligible row is eventually cleared within a bounded number of calls, with no row permanently skipped or double-processed -- oldest-eligible import-bearing rows are prioritized, though a bounded take may clear a newer, zero-cost import-less Failed job in the same call as an older import still deferred by the reading-volume cap (accepted: see Design Notes).
- Given two concurrent polls for the same household while one is already mid-chunk, when the second's `SweepExpiredAsync` call runs, then on Postgres it does not block on DB locks held by the first and does not issue a delete against the same rows -- it no-ops and lets the next poll retry. (On SQL Server, the delete phase carries the same guarantee; the eligibility `SELECT` itself may still transiently block under default locking Read Committed -- documented residual risk, see deferred-work.md.)

## Design Notes

Reuse over reinvention: `ChunkImportIdsByReadingVolume` already greedily packs by both import count (`DeleteBatchSize`) and reading volume (`DeleteReadingVolumeThreshold`) and stops a chunk boundary as soon as either cap trips. Feeding it only the query-bounded (≤`DeleteBatchSize`) prefix of eligible importIds and taking its first yielded chunk reproduces exactly what a full run's first chunk would have been — the greedy packer's first boundary only ever depends on that prefix, never on rows after it. This avoids inventing a second chunking algorithm.

No persisted cursor needed: because the eligibility query is re-run from scratch on every call and ordered oldest-first (with a tiebreaker), "resume where the last call left off" falls out for free — the rows this call already deleted simply no longer match the eligibility query on the next call.

Ordering is best-effort, not a strict global guarantee: import-less Failed jobs (zero reading cost) always clear within the bounded take regardless of whether the reading-volume cap deferred an older import-bearing row to a later call. This means a newer bare-job row can clear before an older, volume-deferred import — a deliberate tradeoff (forcing free rows to wait behind expensive ones buys no correctness benefit) that still satisfies the real requirement: every row clears within a bounded number of calls, none is starved forever.

Advisory lock, not a blocking lock or a distributed lock service: `pg_try_advisory_xact_lock` is transaction-scoped, non-blocking, and needs no schema change or new infrastructure — it's the smallest fix that closes the concurrent-poll gap without reintroducing blocking latency for the loser, and it rides the same transaction the bounded delete already opens, so it's released automatically on commit or rollback with no separate unlock call to forget.

## Spec Change Log

- **Loop 1 (bad_spec, 2026-09-17):** Edge Case Hunter review of the first implementation found two real gaps neither the frozen intent nor the original Tasks addressed: (1) two concurrent `GET /api/smart-plug-import-jobs` polls for the same household (explicitly an in-scope scenario per this spec's own I/O matrix — e.g. multiple open tabs) could both select the same oldest-eligible chunk and race to delete it, with the loser blocking on DB row locks for the duration of the winner's in-flight `DetachReadingsForImportAsync` loop — bounded by one chunk's own duration, but still a real, avoidable latency spike for the exact population (concurrent-tab users) this spec exists to protect; (2) the eligibility query was bounded only client-side (`eligible.Take(DeleteBatchSize)` after `ToListAsync`), so it still pulled the *entire* backlog into memory on every poll before truncating. Blind Hunter separately flagged (3) missing tie-breaking on the new `orderby completedAtUtc` when two rows share a timestamp, (4) duplicated per-chunk delete logic between `DeleteEligibleAsync` and the new bounded path, (5) no test coverage for a bounded take mixing import-less Failed jobs with import-bearing rows, and (6) no test proving forward progress across a third call. **Amended:** Code Map and Tasks & Acceptance above now call for a `pg_try_advisory_xact_lock` concurrency guard, a query-level (not client-side) `Take(DeleteBatchSize)` with an `orderby completedAtUtc, job.Id` tiebreaker, a shared per-chunk delete helper reused by both `DeleteEligibleAsync` and the bounded path, and four tests (multi-call-to-a-third-call, oversized-single-import, mixed import-less/import-bearing take, concurrent-poll no-op). Added a fourth Acceptance Criterion for the concurrency guard. **Known-bad state avoided:** a concurrent-tab household experiencing bounded-but-nontrivial GET latency spikes whenever two polls race on the same chunk, and an eligibility `SELECT` whose cost silently re-scales with total backlog size even after the *delete* work was bounded. **KEEP:** the overall bounded-per-call-chunking design (approach b, unchanged), reusing `ChunkImportIdsByReadingVolume`/`DetachReadingsForImportAsync` as-is, the no-persisted-cursor reasoning (still valid — re-verify it holds once the advisory-lock early-return path is added, since that path also just relies on re-querying next poll), and the original two test scenarios (multi-call chunk boundary via import count, single oversized import in one call) — both should be recreated, the first extended to a third call per finding (6).

## Verification

**Commands:**
- `dotnet test tests/EnergyTracker.Infrastructure.Tests --filter-class "*SmartPlugImportRepositoryTests"` -- expected: existing sweep/delete regression tests still pass, plus new multi-call/bounded-progress/pushdown/concurrency tests pass. (Note: this repo's xUnit v3/Microsoft Testing Platform runner uses `--filter-class`, not `--filter`.)
- `dotnet test tests/EnergyTracker.Infrastructure.Tests --filter-class "*SmartPlugImportRepositoryDeleteJobsSqlServerTests"` -- expected: existing SQL Server regression tests still pass, plus the new advisory-lock no-op test passes (this is the only coverage of `TryAcquireHouseholdSweepLockSqlServerAsync`'s `sp_getapplock` path -- it caught a real non-composable-SQL bug in loop 2, see Task 2's annotation).
- `dotnet test tests/EnergyTracker.Application.Tests --filter-class "*ListSmartPlugImportJobsTests"` -- expected: existing tests covering the "swept row never reappears in the same response" guarantee still pass unmodified.

## Suggested Review Order

**Bounded per-call chunking (the core fix)**

- Entry point: eligibility now ordered oldest-first with a tiebreaker and bounded at the query level, not client-side.
  [`SmartPlugImportRepository.cs:660`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L660)

- Lock acquired *before* measuring reading counts, so a losing poller pays only for the cheap eligibility query.
  [`SmartPlugImportRepository.cs:732`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L732)

**Concurrency guard (added in review loop 1, fixed in loop 2)**

- Provider dispatch for the non-blocking, transaction-scoped per-household lock.
  [`SmartPlugImportRepository.cs:790`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L790)

- Postgres: `pg_try_advisory_xact_lock` over `hashtextextended`'s full 64-bit keyspace.
  [`SmartPlugImportRepository.cs:806`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L806)

- SQL Server: `sp_getapplock` via `ToListAsync()` + client-side `Single()` — `SingleAsync()` threw on this non-composable batch until loop 2's own first SQL Server test caught it.
  [`SmartPlugImportRepository.cs:822`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L822)

**Shared delete logic (dedup between the bounded path and the existing manual-cleanup path)**

- `DeleteEligibleAsync`'s multi-chunk loop now calls the same per-chunk helper the bounded path uses.
  [`SmartPlugImportRepository.cs:1033`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L1033)

- Extracted gaps/detach/imports helper — jobs-delete deliberately stays separate at each call site (see its own comment for why).
  [`SmartPlugImportRepository.cs:1098`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L1098)

**Tests**

- Multi-call forward progress: a backlog spanning three calls clears oldest-first with no skip/double-process.
  [`SmartPlugImportRepositoryTests.cs:705`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs#L705)

- Proves the query bound is server-side (`LIMIT` in the generated SQL), not a client-side `.Take()`.
  [`SmartPlugImportRepositoryTests.cs:784`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs#L784)

- Mixed bounded take: import-less Failed jobs and import-bearing rows both clear correctly in one call.
  [`SmartPlugImportRepositoryTests.cs:872`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs#L872)

- Concurrency no-op (Postgres): a held advisory lock makes the second poll no-op instead of blocking or double-deleting.
  [`SmartPlugImportRepositoryTests.cs:923`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs#L923)

- Concurrency no-op (SQL Server): the one test exercising `sp_getapplock` at all — found the composability bug above.
  [`SmartPlugImportRepositoryDeleteJobsSqlServerTests.cs:311`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryDeleteJobsSqlServerTests.cs#L311)
