---
id: SPEC-job-cleanup-bulk-delete-timeout
companions: []
sources: []
---

> **Canonical contract.** This SPEC and the files in `companions:` are the complete, preservation-validated contract for what to build, test, and validate. Source documents listed in frontmatter are for traceability only — consult them only if you need narrative rationale or prose color this contract intentionally omits.

# Fix the Job-History "Delete Everything" Cleanup

## Why

A pain to solve, confirmed live in production across three incidents. On 2026-09-12, `DELETE /api/smart-plug-import-jobs?deleteAll=true` (story 3.10's "clean up everything" option) returned HTTP 500 — one unbatched transaction saturated Azure SQL Basic-tier and exceeded the 120s `CommandTimeout`. A first fix (batching by **import count**) didn't resolve it: the affected household's eligible-row count was under the batch threshold, so chunking never triggered — the real driver was a handful of imports individually carrying 57k-122k `SmartPlugReading` rows each. A second fix (batching by **cumulative reading volume** too) correctly bounded every individual database command — no more `CommandTimeout` — but the same household then hit a *third* failure: the total work now legitimately takes longer than Azure Container Apps' ~240s synchronous HTTP ingress ceiling, so the connection is aborted (HTTP 499) partway through, even though every chunk was succeeding. No amount of chunk-size tuning fixes a wall-clock ceiling — the operation itself must stop running synchronously inside one HTTP request. This spec now also covers moving the cleanup to the existing async background-job pattern this codebase already uses for Smart Plug imports.

## Capabilities

- **CAP-1: Batched bulk delete**
  - **intent:** `DeleteEligibleAsync` processes the eligible `jobIds`/`importIds` in bounded-size chunks, each executed as its own `ExecuteDeleteAsync` call(s), so a single database command's log-write volume no longer scales with total accumulated household history.
  - **success:** `DeleteJobsAsync(cutoffUtc: null)` against a household with an eligible-row count an order of magnitude beyond what caused the production timeout completes without hitting `CommandTimeout`, verified against both Postgres and SqlServer. — **Done** (PR #47).
- **CAP-2: Atomicity preserved**
  - **intent:** the delete-everything-eligible operation stays all-or-nothing from the caller's perspective, matching the guarantee the 2026-09-11 code-review fix (explicit transaction) established — a failure partway through must not leave some chunks committed and others not.
  - **success:** an integration test that forces a failure/cancellation after at least one chunk has executed shows the database left in its original pre-delete state. — **Done** (PR #47).
- **CAP-3: No regression for existing callers**
  - **intent:** `SweepExpiredAsync` (automatic, terminal-states-only) and the manual 30-day-cutoff `DeleteJobsAsync` path behave exactly as today after `DeleteEligibleAsync` is batched.
  - **success:** existing `SmartPlugImportRepositoryTests` and `CleanUpSmartPlugImportJobsTests` for both paths pass unchanged, plus a small-eligible-set case proves batching is a no-op at small scale. — **Done** (PR #47).
- **CAP-4: Row-volume-aware import chunking**
  - **intent:** the `importIds` loop chunks by both cumulative `SmartPlugReading` count and import count (whichever bound is hit first) — measured via a chunked `GROUP BY` query before chunking — so a small number of eligible imports with large individual reading counts can no longer bypass batching by fitting under the import-count threshold alone.
  - **success:** `DeleteJobsAsync(cutoffUtc: null)` against a household shaped like the confirmed incident completes without hitting `CommandTimeout`, verified against SqlServer and Postgres, with an exact command-count assertion proving the chunking is genuinely wired, not coincidentally correct. — **Done** (PR #48).
- **CAP-5: Async cleanup execution**
  - **intent:** "clean up everything"/"older than 30 days" runs as a background job (new `JobTypes.CleanUpSmartPlugImportJobs`, reusing `IBackgroundJobQueue`/`JobEnvelope<T>`/`BackgroundJobProcessor`'s existing dispatch and `BackgroundJob` status machinery) instead of executing synchronously inside the `DELETE` request — the endpoint enqueues and returns `202`+`jobId` immediately, matching the existing `POST /smart-plug-imports` upload pattern; the client polls `GET /api/jobs/{id}` for completion the same way the existing upload flow already does.
  - **success:** a household whose total cleanup work exceeds the ~240s synchronous HTTP ceiling (reproducing the confirmed 2026-09-12 16:42 incident's data shape) still completes "clean up everything" successfully end-to-end via polling, with no HTTP request held open longer than the existing POST-and-poll upload flow's own request duration. — **Done** (implementation spec `spec-3-10-cleanup-async-job.md`, review round 1 clean).

## Constraints

- Dual-provider (AD-2): all batching/chunking logic (CAP-1/CAP-4) must work correctly and identically on both Npgsql/Postgres and Microsoft.Data.SqlClient/SqlServer.
- Must not change `DeleteJobsAsync`'s or `SweepExpiredAsync`'s eligibility semantics.
- `DeleteJobsAsync`/`DeleteEligibleAsync`'s own chunking (CAP-1/CAP-4) is unchanged and still required under CAP-5 — moving to async removes the HTTP-timeout ceiling but does not remove the value of bounding individual DB command duration; both layers apply together.
- The new job type's `BackgroundJob` rows must not appear in the existing Smart Plug import job history list or become eligible for cleanup by `DeleteJobsAsync`'s own eligibility query themselves — both already filter strictly on `JobType == JobTypes.ProcessSmartPlugImport`, so a distinct `JobTypes.CleanUpSmartPlugImportJobs` constant naturally satisfies this with no additional filtering logic needed.
- The frontend cannot reuse the existing `use-smart-plug-import-job.ts` polling hook as-is — its state machine branches on import-specific `importStatus`/gaps semantics that don't apply to a cleanup job. Implemented as an inline poll loop in `job-history-list.tsx`'s existing `handleConfirmCleanup`, not a new hook file — the cleanup dialog has exactly one caller, so a one-caller abstraction was judged not worth it; the loop mirrors `use-smart-plug-import-job.ts`'s own `MAX_CONSECUTIVE_POLL_FAILURES` transient-failure tolerance.

## Non-goals

- Changing `DeleteJobsAsync`/`DeleteEligibleAsync`'s chunking algorithm or thresholds (CAP-1/CAP-4 stand as already fixed and verified) — CAP-5 changes only how/when that already-correct logic is invoked, not the logic itself.
- A general-purpose async-job UI framework — reuse the existing `GET /api/jobs/{id}` polling infrastructure and status shape as-is; only the minimum new plumbing (job type, payload, dispatcher case, one new frontend hook) is in scope.
- An Azure SQL tier upgrade — remains a possible future lever but isn't part of this fix.
- A single individual import whose *own* reading count alone exceeds the CAP-4 row-volume threshold still can't be split further (an import is the atomic unit for the delete) — unchanged from CAP-4, still an accepted residual risk, now less consequential since CAP-5 removes the synchronous-HTTP-timeout pressure around it.

**Superseded:** round-1's non-goal "moving delete-everything to the async job queue (AD-6) — considered and deliberately deferred" is retracted by CAP-5 above. Two rounds of synchronous-request chunk-tuning correctly bounded every individual database command but could not bound total wall-clock time for a household whose accumulated data genuinely exceeds the platform's synchronous-HTTP ceiling — confirmed live, not theoretical, on 2026-09-12.

## Success signal

A household with years of accumulated Smart Plug import history — including enough total data that full cleanup exceeds the ~240s synchronous HTTP ceiling — clicks "clean up everything," gets an immediate response, and sees the cleanup complete via polling instead of the request itself failing with HTTP 499/500, reproduced by a test shaped like the confirmed 2026-09-12 incident.

## Assumptions

- No data corruption occurred in any of the three incidents: CAP-1/CAP-2's transaction guarantee held throughout, including the round-3 (499) failure — the client-side abort happened between successfully-committed chunks' work, not mid-chunk, so no partial per-chunk state was possible per CAP-2's existing guarantee.
- Azure Container Apps' HTTP ingress has a fixed ~240s idle/request timeout that isn't configurable at the ingress tier this app uses — inferred from the observed exact duration (240.2s) and platform documentation; not independently confirmed via an Azure support ticket or config lookup, since moving to async makes the exact figure immaterial to the fix.

## Open Questions

- Whether the existing cleanup confirmation dialog's UX (`job-history-list.tsx`) needs a redesign for a polling/progress experience, or can reuse a minimal "processing..." state matching the existing upload flow's polling UI, is left to the implementer/UX judgment during implementation — no dedicated UX pass is presumed necessary given the existing upload flow's pattern already covers this shape of interaction.
- Exact reading-count (row-volume) threshold per chunk (CAP-4, `DeleteReadingVolumeThreshold`) remains an unbenchmarked starting value — unchanged open question from CAP-4, now lower-stakes since CAP-5 removes the timeout pressure that made getting it exactly right urgent.
