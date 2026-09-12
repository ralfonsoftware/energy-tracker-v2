---
title: 'Batch a single import''s reading-detach before deleting it'
type: 'bugfix'
created: '2026-09-12'
status: 'done'
review_loop_iteration: 1
baseline_commit: '08e5f36ba930369f497660cac89a189426f12275'
context: ['{project-root}/_bmad-artifacts/specs/spec-job-cleanup-bulk-delete-timeout/SPEC.md']
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Round 3's async move (CAP-5) removed the HTTP-ingress timeout ceiling, but the same household then hit a *fourth* failure: the background cleanup job itself failed. Confirmed via Container App logs (`Failed executing DbCommand (120,205ms) [Parameters=[..., @importIdBatch1=...]`) + Azure Monitor (DTU 94.5%, Log IO 93%) — the same saturation signature as the prior three incidents. Root cause: CAP-4's chunking correctly isolates a single import whose own reading count exceeds `DeleteReadingVolumeThreshold` alone in its own chunk (it can't be split further at the import level), but deleting that import still relies on the FK's `SetNull` cascade to detach however many `SmartPlugReading` rows it has (122,158 in the confirmed case) as one single, unboundable server-side operation. That cascade alone blew Azure SQL's 120s `CommandTimeout`, regardless of whether the surrounding request was synchronous (round 1-2) or async (round 3+). CAP-4's own prior framing — that this residual risk was "less consequential since CAP-5 removes the synchronous-HTTP-timeout pressure" — was wrong: the constraint was never HTTP-timeout pressure, it was SQL `CommandTimeout` pressure a single command's own cascade can hit either way.

**Approach:** Explicitly pre-detach (`SmartPlugImportId = NULL`) each import's own `SmartPlugReading` rows in `DeleteBatchSize`-sized batches *before* that import is deleted, so the subsequent `DELETE FROM SmartPlugImports` always matches zero children and the FK's `SetNull` cascade never has real work to do, regardless of how large the import was.

## Boundaries & Constraints

**Always:**
- Reuse the existing `DeleteBatchSize` (200) constant for the detach loop's batch size — the one value in this codebase's chunking history already proven safe against both the SQL Server ~2100-parameter ceiling and per-command log-volume saturation. No new tunable constant.
- Leave `ChunkImportIdsByReadingVolume`/`DeleteReadingVolumeThreshold` unchanged — CAP-6 makes the reading-volume bound no longer necessary to prevent a timeout, but removing already-tested chunking logic under incident pressure is out of scope.
- Preserve CAP-1/CAP-2/CAP-3/CAP-4/CAP-5 exactly as already shipped — this is an additive change inside the existing `importIdBatch` loop, not a restructuring of it.
- Fix the separately-reported localization bug in the same pass (same root file, same incident-response session): `BackgroundJobProcessor.cs`'s generic-failure `ErrorMessage` must not be a hardcoded English sentence — leave it `null` so each frontend caller's own existing `errorMessage ?? t(...)` fallback renders correctly localized text.

**Ask First:** none.

**Never:** changing `DeleteBatchSize`'s or `DeleteReadingVolumeThreshold`'s values; moving `SweepExpiredAsync` onto the async job pattern (a real, separately-tracked follow-up, out of scope for this hotfix); a SQL Server `LOCK_ESCALATION` schema change (plausible risk, not confirmed, would need its own dedicated investigation).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Single import over threshold | One import, reading count > `DeleteReadingVolumeThreshold` (isolated alone in its own chunk) | Detached in `ceil(count/200)` batches; import then deletes with zero remaining children | No single command approaches `CommandTimeout` |
| Import at exact batch boundary | Reading count == `DeleteBatchSize` | Exactly one detach `UPDATE`, not two | N/A |
| Generic background job failure (any type) | Unhandled, non-validation exception | `BackgroundJob.ErrorMessage` left `null` | Frontend's own `?? t(...)` fallback renders localized text |
| Job history list row for a generically-failed job | `errorMessage: null` | Row still shows a localized fallback suffix, not blank | N/A |

</frozen-after-approval>

## Code Map

- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` -- new `DetachReadingsForImportAsync`, called per-import inside `DeleteEligibleAsync`'s existing chunk loop
- `src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs` -- generic-failure `ErrorMessage` left `null` instead of hardcoded English text
- `web/src/components/smart-plug-import/job-history-list.tsx` -- `metaLine`'s error suffix falls back to `t('smartPlugImport.errorGeneric')` when `errorMessage` is null
- Tests: `SmartPlugImportRepositoryTests.cs`, `SmartPlugImportRepositoryDeleteJobsSqlServerTests.cs`, `BackgroundJobProcessorTests.cs`, `job-history-list.test.tsx`

## Tasks & Acceptance

**Execution:**
- [x] `SmartPlugImportRepository.cs` -- add `DetachReadingsForImportAsync(Guid importId, CancellationToken)`: loop selecting up to `DeleteBatchSize` still-attached reading ids, `ExecuteUpdateAsync` setting them null, until a selection returns zero
- [x] `DeleteEligibleAsync` -- call `DetachReadingsForImportAsync` for every import in `importIdBatch`, before the batch's `SmartPlugImports` delete
- [x] `BackgroundJobProcessor.cs` -- generic (non-`SmartPlugImportValidationException`) catch-all sets `ErrorMessage = null`
- [x] `job-history-list.tsx` -- `metaLine`'s error-state suffix falls back to `t('smartPlugImport.errorGeneric')` when `job.errorMessage` is null
- [x] Backend tests: single-import-over-threshold reproduction (Postgres + SqlServer, exact command-count assertions), exact-`DeleteBatchSize`-boundary test, existing rollback/atomicity tests' command counts updated for the new detach commands, generic-failure-leaves-null-ErrorMessage test
- [x] Frontend tests: job-history-list localized-fallback test (list row), cleanup-dialog localized-fallback test (polling path)

**Acceptance Criteria:**
- Given a single import whose reading count exceeds `DeleteReadingVolumeThreshold`, when `DeleteJobsAsync` runs, then no single database command exceeds `CommandTimeout`, verified against both providers with an exact command-count assertion.
- Given any background job's generic (non-validation) failure, when a client polls `GET /api/jobs/{id}` or fetches the job history list, then the displayed error text is the caller's own correctly-localized string, never raw backend English.
- Given existing CAP-1/2/3/4/5 tests, when run against this change, then all pass (with command-count assertions updated to reflect the new detach commands, not the underlying behavior they verify).

## Spec Change Log

**Review round 1 (Blind Hunter + Edge Case Hunter, 2026-09-12):**

Fixed (patch-class, no loopback):
- `job-history-list.tsx`'s persistent job-history row rendering had no null-safe fallback for the now-nullable `ErrorMessage` — a generically-failed `ProcessSmartPlugImport` job would show no error detail at all in the list (both reviewers found this independently). Fixed with the same `?? t('smartPlugImport.errorGeneric')` pattern already used elsewhere, plus a regression test.
- SqlServer test coverage had the "few imports share the threshold" shape but not the actual incident's "one import alone isolated in its own chunk" shape — added the equivalent of the new Postgres reproduction test.
- `DetachReadingsForImportAsync` had no dedicated boundary test — added (exactly `DeleteBatchSize` rows → exactly one batch, not two).
- Updated comments at both `DetachReadingsForImportAsync` and its call site to explicitly name (not gloss over) the residual TOCTOU risk this fix narrows but doesn't close (a still-`Processing`/`AwaitingPowerPointMapping` import receiving new readings mid-loop), and to note the aggregate wall-clock cost is unbenchmarked, matching this codebase's established caveat style for its other chunking constants.

Deferred (append to `deferred-work.md`, not blocking):
- `SweepExpiredAsync`'s automatic sweep was never moved off the synchronous HTTP request path by CAP-5 (only the manual cleanup endpoint was) — it now also pays this round's detach-loop cost inline, synchronously, for a large aging import. Real risk, not yet confirmed as a live incident.
- SQL Server lock-escalation risk from hundreds of sequential row-level `UPDATE`s within one long transaction, potentially escalating to a table lock and blocking every household sharing `SmartPlugReadings`. Plausible, not confirmed against production.
- The already-tracked "outer transaction stays open longer" (Postgres autovacuum) and "60-minute queue visibility timeout redelivery" risks both increase in magnitude under this round's added round trips — existing `deferred-work.md` entries updated with that context rather than duplicated.
- Concurrent `DeleteJobsAsync`/`SweepExpiredAsync` executions racing via many small per-import `UPDATE`s instead of one coarse cascade — same class of pre-existing risk (two calls to `DeleteEligibleAsync` could already target overlapping rows before this fix), now with a different lock-contention profile.

Rejected:
- Edge Case Hunter's "the detach loop could spin forever if the same import concurrently receives new readings from an active `ProcessSmartPlugImport` job" — verified unreachable given this codebase's actual concurrency model: `infra/modules/container-app.bicep` pins `maxReplicas: 1`, and both `AzureStorageQueueJobQueue.cs` and `InProcessChannelJobQueue.cs` process dequeued messages via a plain sequential `foreach`/`await` loop (no `Task.WhenAll`/parallel dispatch) — so at most one background job of any type is ever executing at a time, globally. A cleanup job's detach loop and an import job's reading inserts can never run concurrently today.
- "Losing the ability to distinguish a failed cleanup job from a failed import job by `ErrorMessage` alone" — weak: `BackgroundJob.JobType` is already a persisted column on the same row (and already included in the `LogError` call), so this distinction was never actually lost, only no longer duplicated into `ErrorMessage` too.

## Design Notes

**Why not disable SQL Server lock escalation via migration:** considered as a more thorough mitigation for the lock-escalation risk, but that's a schema change with its own blast radius (memory overhead of holding many more row locks) that deserves its own dedicated investigation and test cycle, not a same-day addition under incident pressure with no confirmed production occurrence yet.

**Why the detach loop reuses `DeleteBatchSize` rather than a new constant:** this saga's own history shows picking untested batch/threshold values under incident pressure has twice proven wrong (`DeleteReadingVolumeThreshold`'s "well within threshold" assumption, and this very incident). `DeleteBatchSize` is the one value already proven safe on both dimensions (parameter count, per-command volume) that have ever caused a failure in this codebase.

## Suggested Review Order

1. [`src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs) — `DetachReadingsForImportAsync` and its call site in `DeleteEligibleAsync`.
2. [`src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs`](../../src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs) — null `ErrorMessage` for generic failures.
3. [`web/src/components/smart-plug-import/job-history-list.tsx`](../../web/src/components/smart-plug-import/job-history-list.tsx) — `metaLine`'s localized fallback.
4. [`tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs)
5. [`tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryDeleteJobsSqlServerTests.cs`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryDeleteJobsSqlServerTests.cs)
6. [`tests/EnergyTracker.Infrastructure.Tests/BackgroundJobProcessorTests.cs`](../../tests/EnergyTracker.Infrastructure.Tests/BackgroundJobProcessorTests.cs)
7. [`web/src/components/smart-plug-import/job-history-list.test.tsx`](../../web/src/components/smart-plug-import/job-history-list.test.tsx)

## Verification

**Commands:**
- `dotnet test` -- full suite green (509/509), including new/updated detach-loop and command-count tests on both providers
- Frontend test command -- full suite green (265/265), including new localization-fallback tests
- `dotnet build` (Debug) + `npm run build` -- clean
- `oxlint` -- clean, no new warnings
- Manual/live check post-deploy: retrigger "clean up everything" against the affected household, confirm the background job completes
