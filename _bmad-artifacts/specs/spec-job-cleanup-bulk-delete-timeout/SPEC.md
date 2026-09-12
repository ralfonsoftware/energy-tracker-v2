---
id: SPEC-job-cleanup-bulk-delete-timeout
companions: []
sources: []
---

> **Canonical contract.** This SPEC and the files in `companions:` are the complete, preservation-validated contract for what to build, test, and validate. Source documents listed in frontmatter are for traceability only — consult them only if you need narrative rationale or prose color this contract intentionally omits.

# Batch the Job-History "Delete Everything" Cleanup

## Why

A pain to solve, confirmed live in production across two incidents. On 2026-09-12, `DELETE /api/smart-plug-import-jobs?deleteAll=true` (story 3.10's "clean up everything" option) returned HTTP 500. Root-cause investigation found `DeleteJobsAsync(cutoffUtc: null)` selects every `BackgroundJob`/`SmartPlugImport` row for the household with no age bound, and the shared `DeleteEligibleAsync` helper deletes/cascades all of them inside one unbatched transaction, saturating Azure SQL Basic-tier (5 DTU) and exceeding the 120s `CommandTimeout`. A first fix (batching the delete by **import count**, shipped) did not resolve it: after deploying, the same household hit the same HTTP 500 again. Fresh evidence showed why — this household has only 51-60 eligible rows, well under the 200-per-batch threshold, so count-based chunking never split anything. Direct measurement (via a temporary, since-removed read-only DB access) confirmed the real driver: 5 of the 51 imports individually carry 57k-122k `SmartPlugReading` rows each, summing to 487,380 total — one single `DELETE FROM SmartPlugImports` command tries to `SetNull`-cascade all of them at once. This spec now fixes the cleanup so it stops timing out regardless of import count *or* the reading-row volume behind those imports.

## Capabilities

- **CAP-1: Batched bulk delete**
  - **intent:** `DeleteEligibleAsync` processes the eligible `jobIds`/`importIds` in bounded-size chunks, each executed as its own `ExecuteDeleteAsync` call(s), so a single database command's log-write volume no longer scales with total accumulated household history.
  - **success:** `DeleteJobsAsync(cutoffUtc: null)` against a household with an eligible-row count an order of magnitude beyond what caused the production timeout completes without hitting `CommandTimeout`, verified against both Postgres and SqlServer.
- **CAP-2: Atomicity preserved**
  - **intent:** the delete-everything-eligible operation stays all-or-nothing from the caller's perspective, matching the guarantee the 2026-09-11 code-review fix (explicit transaction) established — a failure partway through must not leave some chunks committed and others not.
  - **success:** an integration test that forces a failure/cancellation after at least one chunk has executed shows the database left in its original pre-delete state.
- **CAP-3: No regression for existing callers**
  - **intent:** `SweepExpiredAsync` (automatic, terminal-states-only) and the manual 30-day-cutoff `DeleteJobsAsync` path behave exactly as today after `DeleteEligibleAsync` is batched — same eligibility results, same FK-dependency delete order.
  - **success:** existing `SmartPlugImportRepositoryTests` and `CleanUpSmartPlugImportJobsTests` for both paths pass unchanged, plus a new small-eligible-set test (fewer rows than one batch) proves batching is a no-op at small scale.
- **CAP-4: Row-volume-aware import chunking**
  - **intent:** the `importIds` loop chunks by both cumulative `SmartPlugReading` count and import count (whichever bound is hit first) — measured via one `GROUP BY` query before chunking — so a small number of eligible imports with large individual reading counts can no longer bypass batching by fitting under the import-count threshold alone.
  - **success:** `DeleteJobsAsync(cutoffUtc: null)` against a household shaped like the confirmed incident (order of 50-60 imports, several individually carrying 50k-120k+ readings, ~487k total) completes without hitting `CommandTimeout`, verified against SqlServer (the actual production provider in both incidents) and Postgres.

## Constraints

- Dual-provider (AD-2): the batching implementation must work correctly and identically on both Npgsql/Postgres and Microsoft.Data.SqlClient/SqlServer — no provider-specific batching primitive.
- Must not change `DeleteJobsAsync`'s or `SweepExpiredAsync`'s eligibility semantics (which rows qualify) — only how `DeleteEligibleAsync` executes the deletes for whatever set it's handed.
- Chunk sizing (both the import-count bound and the new reading-count bound) must keep a single command's row/log-write volume safely within Azure SQL Basic-tier (5 DTU) throughput inside the existing 120s `CommandTimeout` — a capacity constraint, not a style choice; document the reasoning at the constant's definition site the way `Program.cs:140-159` already documents its own timeout/batch-size reasoning.
- The `jobIds` loop (bounding `BackgroundJobs`' own direct delete row/log volume — no downstream cascade) is unaffected by CAP-4 and keeps its existing import-count-based chunking unchanged; only the `importIds` loop's strategy changes.
- Measuring reading counts costs one extra `GROUP BY` query per `DeleteJobsAsync`/`SweepExpiredAsync` call — acceptable since this is a manual, low-frequency cleanup operation, not a hot path.

## Non-goals

- Moving "delete everything" to the existing async background job queue (AD-6) — considered and deliberately deferred in favor of this smaller, targeted batching fix.
- An Azure SQL tier upgrade — batching is the chosen mitigation; a tier upgrade remains a possible future lever but isn't part of this fix.
- Changing `SweepExpiredAsync`'s or `DeleteJobsAsync`'s eligibility query logic, or the frontend cleanup dialog/UX from story 3.10.
- A single individual import whose *own* reading count alone exceeds the row-volume threshold cannot be split further (an import is the atomic unit for the `SmartPlugImportGaps`+`SmartPlugImports` delete pair) — named explicitly as still-not-fully-solved, though the confirmed incident data (largest single import: 122,158 rows) shows this isn't what caused either incident; both were caused by the *sum* across several large imports in one uncapped batch, which row-volume-bounded packing directly fixes.

## Success signal

A household with years of accumulated Smart Plug import history — including a handful of individually large imports — clicks "clean up everything" in the Job Status & History list and it completes successfully (200, all eligible rows gone) instead of returning HTTP 500 — reproduced by an integration test shaped like the confirmed incident (order of 50-60 imports, several carrying 50k-120k+ readings each), against both database providers, with particular emphasis on SqlServer since that's the provider both real incidents occurred against.

## Assumptions

- No data corruption occurred in either incident: the delete was wrapped in one explicit transaction, so the client-side timeout/disconnect rolled it back cleanly. Originally inferred only from the failure pattern; now corroborated by the round-2 investigation's direct row-count queries (via a temporary, since-removed read-only DB connection), which found no orphaned or partially-deleted state.

## Open Questions

- Exact reading-count (row-volume) threshold per chunk is left to the implementer, informed by the confirmed incident's own numbers (487,380 total readings across 51 imports saturated Basic-tier for ~2 minutes in one command) — should be set well under that scale, with the reasoning documented at the constant's definition site, same discipline as the existing import-count `DeleteBatchSize`.
- Whether each chunk shares the existing outer transaction or uses its own per-chunk transaction: sharing preserves the strongest atomicity guarantee (CAP-2) but keeps one long-running transaction's log growth; per-chunk transactions bound that further but weaken all-or-nothing semantics. Needs an explicit call from the implementer/reviewer, documented at the point of decision. (Round 1 resolved this toward "shared transaction" for its own chunking dimension; CAP-4's row-volume dimension should follow the same resolution unless new evidence argues otherwise.)
