---
title: 'Run job-history cleanup as an async background job'
type: 'bugfix'
created: '2026-09-12'
status: 'done'
review_loop_iteration: 1
baseline_commit: 'dfcc3d9460d5e72e24c46be57f5c57a28eebe863'
context: ['{project-root}/_bmad-artifacts/specs/spec-job-cleanup-bulk-delete-timeout/SPEC.md']
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Two rounds of DB-side chunking correctly bound every individual command, but the same household then hit a *third* failure: total cleanup work now exceeds Azure Container Apps' ~240s synchronous HTTP ingress ceiling, aborting the connection (499) mid-operation even though every chunk succeeds. No chunk-size tuning fixes a wall-clock ceiling.

**Approach:** Move `DELETE /api/smart-plug-import-jobs` onto this codebase's existing async background-job pattern (AD-6, already used for Smart Plug import uploads): new `JobTypes.CleanUpSmartPlugImportJobs`, dispatched by the existing `BackgroundJobProcessor`, frontend polls `GET /api/jobs/{id}` (already generic, no backend change needed there) instead of awaiting one long request.

## Boundaries & Constraints

**Always:**
- Reuse `IBackgroundJobQueue`/`JobEnvelope<T>`, `BackgroundJobProcessor`'s scope/idempotency scaffolding, and `GET /api/jobs/{id}` as-is — only add one `switch` case.
- `CleanUpSmartPlugImportJobs.ExecuteAsync` and `DeleteJobsAsync`/`DeleteEligibleAsync` (CAP-1/CAP-4) unchanged — only how the use case is invoked changes.
- New job type must never appear in the import job history list or its own cleanup eligibility — both already filter on `JobType == JobTypes.ProcessSmartPlugImport`.
- Existing `cleaningUp` dialog state extends to cover polling — no new UI states.

**Ask First:** none.

**Never:** changing chunking algorithm/thresholds; a general async-job UI framework; a new polling hook file (existing `fetchJobStatus`/`JobStatusDto` are already generic enough).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Enqueue | `DELETE ...?deleteAll=true` | `202` + `{jobId}` immediately | N/A |
| Poll while running | `GET /api/jobs/{id}` | `"queued"` then `"processing"` | N/A |
| Poll after success/failure | Job finished | `"completed"` / `"failed"` + `errorMessage` | Frontend shows existing cleanup-error message |
| Large household (incident shape) | Total work > 240s | Completes via polling; no single request held that long | N/A |

</frozen-after-approval>

## Code Map

- `src/EnergyTracker.Application/JobTypes.cs` -- add `CleanUpSmartPlugImportJobs` constant
- `src/EnergyTracker.Application/CleanUpSmartPlugImportJobs.cs` -- add `CleanUpSmartPlugImportJobsPayload(bool DeleteAll)` record, use case unchanged
- `src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs` -- add dispatch `case`
- `src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs` -- `DELETE` enqueues instead of awaiting directly; `SmartPlugImportJobCleanupResponse` reshaped to `(Guid JobId)`
- `web/src/lib/smart-plug-import-api.ts` -- `cleanUpSmartPlugImportJobs` returns a jobId, mirroring `uploadSmartPlugFile`
- `web/src/components/smart-plug-import/job-history-list.tsx` -- `handleConfirmCleanup` polls `fetchJobStatus` until resolved, same 2000ms interval as `use-smart-plug-import-job.ts`
- Tests: dispatcher test (wherever `BackgroundJobProcessor` is already tested), `SmartPlugImportEndpointsTests.cs`, `job-history-list.test.tsx`

## Tasks & Acceptance

**Execution:**
- [x] `JobTypes.cs` -- add `CleanUpSmartPlugImportJobs` constant
- [x] `CleanUpSmartPlugImportJobs.cs` -- add `CleanUpSmartPlugImportJobsPayload(bool DeleteAll)` record (co-located, matching `ProcessSmartPlugImportPayload`'s precedent)
- [x] `BackgroundJobProcessor.cs` -- add `case JobTypes.CleanUpSmartPlugImportJobs:` deserializing the payload, resolving the use case from DI, calling `ExecuteAsync(message.HouseholdId, payload.DeleteAll, cancellationToken)`; log the returned deleted-count at Information level
- [x] `SmartPlugImportEndpoints.cs` -- `DELETE` builds `jobId`/payload/`JobEnvelope`, enqueues, returns `202` + reshaped `SmartPlugImportJobCleanupResponse(JobId)`
- [x] `smart-plug-import-api.ts` -- `cleanUpSmartPlugImportJobs` returns `Promise<string>` (jobId), same parsing shape as `uploadSmartPlugFile`; drop unused `deletedCount` DTO
- [x] `job-history-list.tsx` -- `handleConfirmCleanup`: enqueue, then loop `fetchJobStatus` every 2000ms (guarded by existing `mountedRef`) until `completed` (close + `load()`) or `failed` (surface via existing `cleanupError`); no new component state
- [x] Backend tests: dispatcher routes the new `JobType` correctly; endpoint returns `202`+enqueues instead of executing synchronously
- [x] Frontend tests: `job-history-list.test.tsx` updated for enqueue-then-poll (mock jobId then queued/processing/completed responses)
- [x] One integration test proving `deleteAll=true` against an incident-shaped household completes via polling with no single request blocking that long

**Acceptance Criteria:**
- Given `DELETE ...?deleteAll=true`, when it completes, then the response is `202`+`jobId`, not a synchronous result.
- Given a queued cleanup job, when dequeued, then `CleanUpSmartPlugImportJobs.ExecuteAsync` runs with the right args and the `BackgroundJob` row reaches `Completed`/`Failed` like an import job does.
- Given the cleanup dialog, when confirmed, then it polls until resolved before closing/refreshing — no new dialog states.
- Given existing use-case/repository tests (CAP-1/2/3/4), when run against this change, then all pass unchanged.

## Spec Change Log

**Review round 1 (Blind Hunter + Edge Case Hunter, 2026-09-12):** both reviewers independently converged on the same three findings, confirmed against the live code and patched (no spec-intent renegotiation needed — all three are mechanical/patch-class fixes within the frozen Boundaries):

- `BackgroundJobProcessor.cs`'s generic failure-message fallback was hardcoded to import-specific text ("...processing this import") regardless of `JobType`, so a failed cleanup job would surface a misleading message. Fixed: branches on `JobTypes.CleanUpSmartPlugImportJobs`.
- No guard against a double-click, a second tab, or a false-timeout retry enqueueing a second concurrent cleanup job for the same household — reintroducing the exact DB contention this three-round incident exists to eliminate. Fixed: `DELETE /api/smart-plug-import-jobs` now checks (via the existing `IBackgroundJobRepository.ListByJobTypeAsync`) for an already-Queued/Processing cleanup job and reuses it instead of enqueueing another.
- The frontend poll loop had no tolerance for a transient fetch failure (network blip/5xx), unlike `use-smart-plug-import-job.ts`'s own `MAX_CONSECUTIVE_POLL_FAILURES` precedent it was meant to mirror — one dropped request would report cleanup failure even if the job was still running or had already succeeded. Fixed: same 3-attempt tolerance, plus the poll loop now checks `mountedRef` before every fetch instead of only after.

Five additional findings (60-minute visibility-timeout redelivery risk, no in-dialog progress indication, always-enqueue-even-when-empty latency, `GET /api/jobs/{id}` not exposing `JobType`, no client-side persistence of an in-flight cleanup job across navigation) were triaged as defer — logged to `deferred-work.md` with rationale. One finding (deleted-row count no longer surfaced) was rejected: confirmed via the round-2 implementation session that the old synchronous count was never actually displayed in the UI, so there is no regression.

All three patches covered by new/updated tests: `BackgroundJobProcessorTests` untouched (dispatch correctness for the new job type is proven via API-integration tests, per this file's established convention); `SmartPlugImportEndpointsTests.cs` gained `DELETE_smart_plug_import_jobs_while_a_cleanup_job_is_still_active_reuses_it_instead_of_enqueueing_another`; `job-history-list.test.tsx` gained `'tolerates one transient poll failure and still completes the cleanup'`. Full suites green: backend 505/505, frontend 263/263, oxlint clean, `dotnet build`/`npm run build` clean.

## Design Notes

**Why not a new polling hook file:** `use-smart-plug-import-job.ts` is import-specific (branches on `importStatus`/gaps) and used nowhere else; a cleanup job needs only `queued`/`processing`/`completed`/`failed`, already exactly what `JobStatusDto` exposes generically. Inlining a small poll loop avoids a new abstraction with one caller.

**Why DB-side chunking stays:** async removes the HTTP-timeout ceiling, not the value of bounding individual command duration.

## Suggested Review Order

1. [`src/EnergyTracker.Application/JobTypes.cs`](../../src/EnergyTracker.Application/JobTypes.cs) — new job type constant.
2. [`src/EnergyTracker.Application/CleanUpSmartPlugImportJobs.cs`](../../src/EnergyTracker.Application/CleanUpSmartPlugImportJobs.cs) — new payload record.
3. [`src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs`](../../src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs) — dispatch case + per-job-type failure message.
4. [`src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs`](../../src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs) — enqueue-instead-of-await, active-job reuse guard.
5. [`web/src/lib/smart-plug-import-api.ts`](../../web/src/lib/smart-plug-import-api.ts) — `cleanUpSmartPlugImportJobs` returns a jobId.
6. [`web/src/components/smart-plug-import/job-history-list.tsx`](../../web/src/components/smart-plug-import/job-history-list.tsx) — poll loop with retry tolerance.
7. [`tests/EnergyTracker.Api.Tests/SmartPlugImportEndpointsTests.cs`](../../tests/EnergyTracker.Api.Tests/SmartPlugImportEndpointsTests.cs)
8. [`web/src/components/smart-plug-import/job-history-list.test.tsx`](../../web/src/components/smart-plug-import/job-history-list.test.tsx)

## Verification

**Commands:**
- `dotnet test` -- full suite green, including new dispatcher/endpoint tests
- Frontend test command -- `job-history-list.test.tsx` green
- `dotnet build` (Debug + Release) -- clean
- Manual/live check post-deploy: trigger "delete everything" against the affected household, confirm it completes via polling
