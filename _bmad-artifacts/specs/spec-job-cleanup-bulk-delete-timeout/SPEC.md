---
id: SPEC-job-cleanup-bulk-delete-timeout
companions: []
sources: []
---

> **Canonical contract.** This SPEC and the files in `companions:` are the complete, preservation-validated contract for what to build, test, and validate. Source documents listed in frontmatter are for traceability only — consult them only if you need narrative rationale or prose color this contract intentionally omits.

# Batch the Job-History "Delete Everything" Cleanup

## Why

A pain to solve, confirmed live in production on 2026-09-12: `DELETE /api/smart-plug-import-jobs?deleteAll=true` (story 3.10's "clean up everything" option) returns HTTP 500 for a household member trying to use it. Root-cause investigation (Container App logs + Azure Monitor SQL metrics) found `DeleteJobsAsync(cutoffUtc: null)` selects every `BackgroundJob`/`SmartPlugImport` row for the household with no age bound, and the shared `DeleteEligibleAsync` helper deletes/cascades all of them inside one unbatched transaction. On a household with enough accumulated history, that single transaction's log-write volume saturates Azure SQL Basic-tier (5 DTU) — confirmed via `DTU ~99%` / `Log IO ~95-99%` exactly during the two failed requests, `deleteAll=false` succeeding in the same window — and the command exceeds the existing 120s `CommandTimeout`. This is the same failure class a 2026-09-05 incident already documented in `Program.cs`, which explicitly rules out a further timeout bump as the durable fix for an operation whose size is unbounded by design. This spec fixes the "delete everything" path so it stops timing out regardless of how much history a household has accumulated.

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

## Constraints

- Dual-provider (AD-2): the batching implementation must work correctly and identically on both Npgsql/Postgres and Microsoft.Data.SqlClient/SqlServer — no provider-specific batching primitive.
- Must not change `DeleteJobsAsync`'s or `SweepExpiredAsync`'s eligibility semantics (which rows qualify) — only how `DeleteEligibleAsync` executes the deletes for whatever set it's handed.
- Chunk size must keep a single command's log-write volume safely within Azure SQL Basic-tier (5 DTU) throughput inside the existing 120s `CommandTimeout` — a capacity constraint, not a style choice; document the reasoning at the constant's definition site the way `Program.cs:140-159` already documents its own timeout/batch-size reasoning.

## Non-goals

- Moving "delete everything" to the existing async background job queue (AD-6) — considered and deliberately deferred in favor of this smaller, targeted batching fix.
- An Azure SQL tier upgrade — batching is the chosen mitigation; a tier upgrade remains a possible future lever but isn't part of this fix.
- Changing `SweepExpiredAsync`'s or `DeleteJobsAsync`'s eligibility query logic, or the frontend cleanup dialog/UX from story 3.10.

## Success signal

A household with years of accumulated Smart Plug import history clicks "clean up everything" in the Job Status & History list and it completes successfully (200, all eligible rows gone) instead of returning HTTP 500 — reproduced by an integration test with an eligible-row count exceeding the production incident's scale, against both database providers.

## Assumptions

- No data corruption occurred in the 2026-09-12 incident: the delete was wrapped in one explicit transaction, so the client-side timeout/disconnect rolled it back cleanly. Inferred from the failure pattern (repeated identical timeouts, no partial-state symptoms reported) — not confirmed by direct row-count/state inspection, since ad hoc DB access from the investigating session was blocked by the SQL server firewall.

## Open Questions

- Exact chunk size (e.g. 500 vs 1000 IDs per batch) is left to the implementer, bounded by SQL Server's ~2100 parameter limit (the same constraint `Program.cs:144-146` documents for `MaxBatchSize`) and by keeping per-chunk log volume well under what saturated Basic tier in the observed incident.
- Whether each chunk shares the existing outer transaction or uses its own per-chunk transaction: sharing preserves the strongest atomicity guarantee (CAP-2) but keeps one long-running transaction's log growth; per-chunk transactions bound that further but weaken all-or-nothing semantics. Needs an explicit call from the implementer/reviewer, documented at the point of decision.
