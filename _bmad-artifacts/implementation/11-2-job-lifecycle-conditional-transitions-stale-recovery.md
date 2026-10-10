---
baseline_commit: bd10ba0
---

# Story 11.2: Job Lifecycle — Conditional Transitions, Heartbeat, Stale Recovery

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- Epic 11 "Codebase audit hardening" (planning/epics/epic-11-codebase-audit-hardening.md). Origin: codebase audit 2026-10-10 (docs/codebase-audit-2026-10-10.md) finding C8 (Medium), plus the C7 half that lives in the processor (non-shutdown OperationCanceledException); architecture AD-6 (amended 2026-10-10), AD-3, AD-24. This file cites the AD and audit IDs, which satisfies the spec-per-fix gate (project-context.md). -->
<!-- Branch already exists and is checked out: feature/11-2-job-lifecycle-stale-recovery. -->
<!-- This is the FIRST migration-bearing Epic 11 story: Epic 10 retro action #3 (Ralf) applies to its deploy, see Dev Notes "Deploy". -->

## Story

As a household member,
I want a background job that was interrupted by a restart to end as failed with a clear message,
so that I can retry a restore or cleanup instead of being blocked forever.

## Acceptance Criteria

1. **Given** any `BackgroundJob` status change, **when** it happens, **then** it goes through one Application service as a single conditional update (`WHERE Id AND Status IN expected`, plus the `StartedAtUtc` token when leaving `Processing`). A terminal status is never overwritten, and zero rows affected makes the caller stop (AD-6, 2026-10-10). The service is the `IBackgroundJobLifecycle` port in `Application/Ports` with one Infrastructure adapter; no other code writes `BackgroundJob.Status`.
2. **Given** the expand-only migration adding nullable `StartedAtUtc` and `HeartbeatAtUtc`, created for both providers via `scripts/add-migration.sh`, **when** a job runs, **then** the processor refreshes `HeartbeatAtUtc` every minute from its own DbContext scope (independent of the job's own long transaction).
3. **Given** a `Processing` job whose heartbeat is older than 5 minutes, **when** `GET /api/jobs/{id}`, the job list, restore-confirm or the cleanup check runs, **then** that Household's stale jobs are first marked failed with code `job-interrupted`. A job that is still heartbeating is never failed, and restore-confirm's 409 guard holds for its whole run. `Queued` rows are never failed for age.
4. **Given** the in-process queue and an app restart, **when** the app starts, **then** each Household's `Queued`/`Processing` rows created before the process started are marked failed (`job-interrupted`), iterating Households and setting `JobHouseholdContext` (never `IgnoreQueryFilters`). A restore-confirm that was blocked before the restart now succeeds (integration test). The Azure adapter never runs this sweep.
5. **Given** a dequeued message whose job row is terminal or missing, **when** it is processed, **then** nothing runs and the message is deleted. The "row missing, insert a `Processing` row" fallback is removed.
6. **Given** an `OperationCanceledException` that is not caused by shutdown, **when** it escapes a job, **then** the job ends `Failed`. Failure codes (`job-interrupted`, `job-retries-exhausted`, `upload-missing`) are translated in both locales (de-DE and en-US).
7. **Given** terminal `CorrelateEvent` `BackgroundJob` rows (added per Meter Reading write by Story 10.2, never swept today), **when** the existing 30-day job-history sweep runs, **then** it also removes them, with the same retention and the same `JobHouseholdContext` handling as for `ProcessSmartPlugImport` rows. *(Added by Epic 10 retro, 2026-10-10; Ralf's decision.)*
8. **Given** the Epic 10 retro note, **when** this story is created, **then** it is confirmed whether `CorrelateEvent` rows surface in the member-visible job list. **Confirmed at create-story (2026-10-10): they do not**, so nothing is hidden or labelled (evidence in Dev Notes "CorrelateEvent rows").
9. **Given** the failure codes now arrive in `errorMessage` of `GET /api/jobs/{id}` and the job list (implied requirement, not in the epic text), **when** the frontend shows a failed job (restore panel, Smart Plug upload, Smart Plug job history, history cleanup), **then** a known code renders its translated sentence and any other value renders as before. A raw code such as `job-interrupted` is never shown to a member.
10. **Given** the docs and the deferred list (implied requirement), **when** this story is done, **then** `docs/data-import-restore.md` explains what an interrupted restore looks like and that retry is safe, and the two `deferred-work.md` entries this story closes are removed or trimmed (Task 10).

## Tasks / Subtasks

Test-first: write the tests in Tasks 1–3 and see them **red** against current code (or not compiling because the port does not exist yet) before Tasks 4–9.

- [x] Task 1: Lifecycle adapter tests, dual-provider, red first (AC: #1, #3)
  - [x] 1.1 New `BackgroundJobLifecycleTestsBase` + `PostgresBackgroundJobLifecycleTests` / `SqlServerBackgroundJobLifecycleTests` in `tests/EnergyTracker.Infrastructure.Tests` (pattern: `HouseholdRestoreWriterTestsBase`; Testcontainers, real migrations). Seed rows directly (the tests set `Status`, `StartedAtUtc`, `HeartbeatAtUtc`, `CreatedAtUtc` by hand, so thresholds are tested by seeding old timestamps, not by sleeping).
  - [x] 1.2 `TryStart`: `Queued` → `Processing`, sets `StartedAtUtc == HeartbeatAtUtc == token`, returns the token; `Completed`/`Failed`/missing id → `null` and the row unchanged; `Processing` with a **fresh** heartbeat → `null` (live owner); `Processing` with a **stale** heartbeat → restarted with a **new** token. Two concurrent `TryStart` calls on one `Queued` row: exactly one gets a token (replaces `ProcessAsync_two_concurrent_deliveries_...`).
  - [x] 1.3 **Token round trip (the provider-sensitive one):** the token returned by `TryStart` must be accepted by `TryHeartbeat`, `TryComplete` and `TryFail` on **both** providers (Postgres `timestamptz` keeps microseconds, SQL Server `datetimeoffset(7)` keeps ticks; see Dev Notes "Token precision"). A wrong token returns `false` and changes nothing.
  - [x] 1.4 `TryComplete` / `TryFail`: `Processing`+token → terminal with `CompletedAtUtc`, `ErrorMessage` as given; a row that is already `Completed` or `Failed` is **not** overwritten (call `TryFail` on a `Completed` row and `TryComplete` on a `Failed` row; both `false`).
  - [x] 1.5 `FailStale(householdId)`: fails `Processing` rows whose `COALESCE(HeartbeatAtUtc, StartedAtUtc, CreatedAtUtc)` is older than 5 min with `ErrorMessage == "job-interrupted"` and `CompletedAtUtc` set; does **not** touch a `Processing` row with a fresh heartbeat even if `CreatedAtUtc` is hours old (headline "long but live job" case), a `Queued` row of any age, terminal rows, or another Household's rows (create two Households). Returns the count.
  - [x] 1.6 `FailInterruptedBefore(householdId, processStartedAtUtc)`: fails `Queued` and `Processing` rows with `CreatedAtUtc < processStartedAtUtc` (any heartbeat), leaves rows created at or after it, terminal rows and other Households' rows alone.
  - [x] 1.7 The adapter is used under `JobHouseholdContext` for the sweep and under the HTTP accessor for read paths. Run the household-scoping cases (1.5, 1.6) with a `FixedHouseholdAccessor` for Household A and assert Household B's stale row is untouched, to prove the AD-3 query filter plus the explicit `HouseholdId` predicate hold for `ExecuteUpdateAsync`.
- [x] Task 2: Processor, queue-service and sweep tests, red first (AC: #2, #4, #5, #6)
  - [x] 2.1 Rework `BackgroundJobProcessorTests.cs` (298 lines, Postgres): keep the two enqueue-recorder tests (`EnqueueAsync_persists_...`, `DeleteAsync_removes_...`) unchanged. **Delete** `ProcessAsync_inserts_a_fresh_row_when_no_Queued_row_exists_for_the_message_defensive_fallback` and replace it with `ProcessAsync_does_nothing_when_the_row_is_missing` (no row created, use case never resolved). Rework the Queued-transition, concurrent-delivery, generic-failure (`ErrorMessage` stays null) and redelivered-terminal tests onto the new flow; they must still pass in spirit.
  - [x] 2.2 New processor cases: (a) a successful run ends `Completed` with `StartedAtUtc` and `HeartbeatAtUtc` set; (b) heartbeat: with a short test interval (see Dev Notes "Timing settings") the row's `HeartbeatAtUtc` advances while the use case is still running (use case blocked on a `TaskCompletionSource`); (c) heartbeat stops after completion; (d) an `OperationCanceledException` thrown by the use case while the stopping token is **not** cancelled ends `Failed` (AC #6); (e) the same exception with the stopping token cancelled is rethrown and the row stays `Processing` (shutdown is not a failure); (f) a job whose row was deleted mid-run (cleanup deleted it): the final transition affects zero rows, the processor logs and returns, no row is resurrected, no exception escapes; (g) an `Exception` from `TryHeartbeat` (DB blip) does not stop the job or the loop.
  - [x] 2.3 Startup sweep tests (`InProcessJobStartupSweepTests`, Infrastructure, Postgres is enough since the SQL is covered by 1.6): two Households, each with a `Queued` and a `Processing` row created before the cut-off and one created after; after the sweep only the early ones are `Failed`/`job-interrupted`, per Household, with no `IgnoreQueryFilters` involved (see the architecture guard in Task 3). A Household whose sweep throws does not stop the others.
  - [x] 2.4 `InProcessChannelJobProcessingService`: the sweep runs once before the channel loop reads; if the sweep throws (database unreachable or not yet migrated), the service logs and **still starts reading the channel** (no unhandled exception out of `ExecuteAsync`).
  - [x] 2.5 **Integration test (AC #4), `tests/EnergyTracker.Api.Tests`** (e.g. in `HouseholdImportEndpointsTests`, real DB): seed a `Queued` `RestoreHouseholdData` row for the test Household with `CreatedAtUtc` in the past; upload+validate a file; `POST …/confirm` returns **409**; invoke the sweep through DI (`factory.Services` → `InProcessJobStartupSweep.SweepAsync(DateTimeOffset.UtcNow, ct)`, simulating "the process restarted"); `POST` a fresh validate+confirm now returns **202**. (Why the sweep is invoked explicitly: `EnergyTrackerApiFactory` builds the host before `MigrateAsync`, so the automatic startup sweep has nothing to sweep. See Dev Notes "Test host start order".)
  - [x] 2.6 Same file, AC #3: a seeded `Processing` restore row with a fresh heartbeat and a `CreatedAtUtc` 10 minutes ago keeps `confirm` at **409**; with `HeartbeatAtUtc` 6 minutes ago it is failed and `confirm` returns **202**. `GET /api/jobs/{id}` on a stale seeded row returns `status: "failed"`, `errorMessage: "job-interrupted"` (add to `JobEndpointsTests`).
  - [x] 2.7 Smart Plug cleanup check (`DELETE /api/smart-plug-import-jobs`) in `SmartPlugImportEndpointsTests`: a stale `Processing` cleanup row no longer makes the endpoint return the old job id; a new job is enqueued.
- [x] Task 3: Guard tests and CorrelateEvent sweep tests, red first (AC: #1, #7)
  - [x] 3.1 `tests/EnergyTracker.Architecture.Tests/BackgroundJobStatusHasOneWriterTests.cs` (AD-6 as an executable invariant, same source-scan style as `HouseholdExportReaderDoesNotBypassTenantIsolationTests`: repo root by walking up to `EnergyTracker.sln`, strip `//` comments): under `src/`, the only file that may assign `BackgroundJob.Status` (`.Status =` on a `BackgroundJob`, `SetProperty(…Status…)` against `BackgroundJobs`) is `BackgroundJobLifecycle.cs`; `BackgroundJobEnqueueRecorder.cs` may only set the initial `Status = BackgroundJobStatus.Queued` in its object initializer. Also assert `IBackgroundJobLifecycle` lives in `EnergyTracker.Application.Ports`, and that the startup sweep file contains neither `IgnoreQueryFilters` nor `FromSql`.
  - [x] 3.2 `IBackgroundJobRepository` sweep of terminal `CorrelateEvent` rows (see Task 8): tests in `tests/EnergyTracker.Infrastructure.Tests` (new `BackgroundJobRepositoryTests` or the existing repository test style, Postgres): deletes `Completed`/`Failed` `CorrelateEvent` rows with `CompletedAtUtc` older than the cutoff; keeps newer ones, any `Queued`/`Processing` `CorrelateEvent` row, other Households' rows and **all** `ProcessSmartPlugImport` rows (that path is the existing sweep's); bounded to one batch per call (insert 205 eligible rows, one call deletes at most 200, a second call the rest).
  - [x] 3.3 `ListSmartPlugImportJobsTests` (Application, NSubstitute): the use case calls `FailStaleAsync(householdId)` **before** `ListByJobTypeAsync`, and the CorrelateEvent sweep with the same cutoff as `RetentionWindow`. `CleanUpSmartPlugImportJobsTests` (add if absent): older-than-30-days mode passes the same cutoff, `deleteAll` passes `null`, and both also call the CorrelateEvent cleanup.
  - [x] 3.4 `GetBackgroundJobStatus` test: calls `FailStaleAsync` before `FindByIdAsync`.
- [x] Task 4: Domain and migration (AC: #2)
  - [x] 4.1 `BackgroundJob`: add `public DateTimeOffset? StartedAtUtc { get; set; }` and `public DateTimeOffset? HeartbeatAtUtc { get; set; }` with a short comment (ownership token / liveness, AD-6). Document on `ErrorMessage` that it can hold a stable failure code (`job-interrupted`, `job-retries-exhausted`, `upload-missing`) as well as a user-facing validation message.
  - [x] 4.2 `BackgroundJobConfiguration`: no new index (queries are `HouseholdId`-scoped and the existing `HouseholdId` and `(HouseholdId, JobType, CreatedAtUtc)` indexes cover them), no length constraint. Leave the `QueuedByHouseholdMemberId` `SetNull` comment alone.
  - [x] 4.3 Run `scripts/add-migration.sh AddBackgroundJobStartedAndHeartbeat` (never `dotnet ef` directly). Open **both** generated files and confirm each contains exactly two nullable `AddColumn<DateTimeOffset>` calls (Postgres `timestamp with time zone`, SQL Server `datetimeoffset`), and nothing else. Check the model snapshots changed in both projects. Expand-only: the previous image can run against the migrated schema (it ignores both columns), which is the rule in project-context "Migrations".
  - [x] 4.4 Run `PostgresMigrationTests` and `SqlServerMigrationTests` (they apply the full chain on real engines).
- [x] Task 5: Port and adapter (AC: #1, #3, #4)
  - [x] 5.1 `src/EnergyTracker.Application/Ports/IBackgroundJobLifecycle.cs` and the timing constants (see Dev Notes "Design" for the exact surface and semantics). `src/EnergyTracker.Infrastructure/Adapters/BackgroundJobLifecycle.cs` implements it with `ExecuteUpdateAsync` only (no tracked entities, AD-2 portable LINQ, no raw SQL).
  - [x] 5.2 Register it in `Program.cs` next to `IBackgroundJobRepository` (scoped). The processor, which is a singleton, resolves it from its per-message scope.
  - [x] 5.3 Terminal transitions (`TryComplete`, `TryFail`) are called with `CancellationToken.None` by the processor (see Dev Notes). Every `Failed` transition also sets `CompletedAtUtc`.
- [x] Task 6: Processor rewrite (AC: #2, #5, #6)
  - [x] 6.1 Rewrite `BackgroundJobProcessor.ProcessAsync` onto the lifecycle port: set `JobHouseholdContext` first (unchanged), `TryStart`; `null` → log at information and return normally (the Azure adapter then deletes the message, the in-process adapter moves on). Remove the insert fallback, the `DbUpdateException` reconcile branch, the three duplicated skip branches and the tracked-entity `SaveChangesAsync`.
  - [x] 6.2 Heartbeat loop: `PeriodicTimer(HeartbeatInterval)` in a task linked to the stopping token, each tick in **its own** `IServiceScopeFactory` scope with `JobHouseholdContext` set, calling `TryHeartbeat(jobId, token)`. A `false` result means ownership is lost: log a warning and stop the loop (do **not** cancel the running use case). An exception is logged as a warning and the loop continues. Always stop and await the loop in a `finally` before the terminal transition.
  - [x] 6.3 Catch order: `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }` (shutdown: leave `Processing`), then `catch (Exception ex)` → `TryFail`. The `ErrorMessage` rule is unchanged: `SmartPlugImportValidationException` / `HouseholdImportValidationException` → `ex.Message`, anything else → `null` (frontend supplies its own localized fallback). The long comment block explaining that rule may be trimmed to the why, not deleted.
  - [x] 6.4 A `TryComplete`/`TryFail` that returns `false` is logged ("no longer owns job") and ends processing quietly. It is not an error and must not throw.
- [x] Task 7: Stale checks and startup sweep (AC: #3, #4)
  - [x] 7.1 Call `lifecycle.FailStaleAsync(householdId, ct)` **first** in: `GetBackgroundJobStatus.ExecuteAsync`, `ListSmartPlugImportJobs.ExecuteAsync`, the restore-confirm endpoint (before `ListByJobTypeAsync`, after the token is consumed, as today), and the cleanup endpoint (before `ListByJobTypeAsync`). Endpoints inject the port into the lambda like they do `IBackgroundJobRepository`; do not add a fifth copy of any helper (11.19 owns `TryGetHouseholdId`).
  - [x] 7.2 `src/EnergyTracker.Infrastructure/Adapters/InProcessJobStartupSweep.cs`: `SweepAsync(DateTimeOffset processStartedAtUtc, CancellationToken)`. One scope to list `Households` ids (`AsNoTracking`, ids only, no query filter exists on `Household`), then **one fresh DI scope per Household** with `JobHouseholdContext.HouseholdId` set **before** resolving anything that touches `EnergyTrackerDbContext`, calling `FailInterruptedBeforeAsync`. A failure for one Household is logged and the loop continues. Log the total at information.
  - [x] 7.3 `InProcessChannelJobProcessingService.ExecuteAsync`: capture `processStartedAtUtc` when the service is constructed, run the sweep inside `try/catch` (log error, never rethrow, never skip the channel loop), then `ReadAllAsync`. Register the sweep in the `default:` (in-process) branch of the `JobQueue:Provider` switch in `Program.cs` only. `AzureStorageQueueJobProcessingService` is **not** touched (Story 11.3 owns it).
- [x] Task 8: CorrelateEvent retention (AC: #7)
  - [x] 8.1 `IBackgroundJobRepository` + `BackgroundJobRepository`: add one method that deletes, for a Household, terminal (`Completed` or `Failed`) rows of a given `JobType` whose `CompletedAtUtc` is older than an optional cutoff (null = no age limit), at most `DeleteBatchSize` (200, same value as `SmartPlugImportRepository.DeleteBatchSize`) per call, oldest first, and returns the number deleted. Select the ids with `Take` first, then `ExecuteDeleteAsync … Where(Contains)` (the pattern `SweepExpiredAsync` uses; do not rely on `ExecuteDelete` over a `Take`d query). Explicit `HouseholdId` predicate plus the standard query filter; no `IgnoreQueryFilters`.
  - [x] 8.2 `ListSmartPlugImportJobs.ExecuteAsync`: after the existing `SweepExpiredAsync`, call it once for `JobTypes.CorrelateEvent` with cutoff `now - RetentionWindow` (one batch per list read; any remainder goes on the next read, exactly as the existing bounded sweep does).
  - [x] 8.3 `CleanUpSmartPlugImportJobs.ExecuteAsync` (runs under `JobHouseholdContext` inside the cleanup job): after `DeleteJobsAsync`, loop the new method for `JobTypes.CorrelateEvent` until it returns 0, with the same cutoff rule as the import deletion (`deleteAll` → null, otherwise `now - RetentionWindow`). Add the CorrelateEvent count to the existing "Cleanup job … deleted … row(s)" log line only if it stays one line; do not change the use case's return type or the cleanup response.
- [x] Task 9: Frontend (AC: #6, #9)
  - [x] 9.1 Tests first: `web/src/lib/job-error.test.ts` and updates to `use-household-import-job` (covered through `data-import-panel.test.tsx`), `job-history-list.test.tsx`, `smart-plug-import-page.test.tsx`: a failed job with `errorMessage: 'job-interrupted'` renders the translated sentence (en-US and de-DE), an unknown message renders as before, `null` still falls back to the existing `errorGeneric`.
  - [x] 9.2 `web/src/lib/job-error.ts`: `translateJobError(t, errorMessage, fallback)` mapping the three codes to keys; exact string equality only. Add `jobError.interrupted`, `jobError.retriesExhausted`, `jobError.uploadMissing` to `web/src/locales/en-US/translation.json` and `de-DE/translation.json` (parity is enforced by `translation-consistency.test.ts`). Wording in Dev Notes "Frontend".
  - [x] 9.3 Use it at the four sites: `use-household-import-job.ts` (`job.errorMessage ?? t('settings.dataImport.errorGeneric')`), `use-smart-plug-import-job.ts` (`job.errorMessage ?? t('smartPlugImport.errorGeneric')`), `job-history-list.tsx` twice (cleanup `status.errorMessage ?? t('…cleanup.error')` and the per-row meta line `job.errorMessage ?? t('smartPlugImport.errorGeneric')`). Behaviour for every non-code value must be byte-identical to today.
- [x] Task 10: Docs, project-context and deferred list (AC: #10)
  - [x] 10.1 `docs/data-import-restore.md`: add a short section "If a restore is interrupted": the app restarting mid-restore ends the job as failed with the "interrupted" message; the restore is one database transaction, so the Household's data is unchanged; the member can upload the file again; recovery is automatic (on the next status read after 5 minutes without a heartbeat, or at once at startup with the in-process queue). `docs/self-hosting.md`: one sentence at the end of section "5. Stopping and restarting" (~line 99–107): stopping or restarting the stack while an import, restore or cleanup is running ends that job as failed on the next start, and it has to be started again; stored data is not affected.
  - [x] 10.2 `project-context.md`, "Async jobs (AD-6)" bullet: add that every `BackgroundJob` status change goes through `IBackgroundJobLifecycle` (conditional update, ownership token) and that a guard test enforces it. Keep it to the bullet; do not restructure the file.
  - [x] 10.3 `deferred-work.md`: delete the `[open]` entry "`CorrelateEvent` `BackgroundJob` rows are never swept …" (section "Deferred from: story 10-2 …"). In the "Deferred from: code review of story-10-2" section trim the entry "Requeue is not deduplicated or capped …" to drop only the "and the job rows are never swept (overlaps …)" clause; the dedup/cap part stays `[open]`. Update the themed index at the top if either entry is listed there.
  - [x] 10.4 Add `[open]` entries to `deferred-work.md` only for real leftovers found, appended at the end in the documented format (candidates from this story: the SQL Server lock-escalation interaction with heartbeats noted in Dev Notes, if you observe it).
- [x] Task 11: Full verification
  - [x] 11.1 `dotnet test` (all projects; Testcontainers need Docker). `dotnet format --verify-no-changes` is not a gate yet (11.18), but add no warnings. Frontend: `npm test`, `npm run lint` (oxlint), `npx tsc -b`.
  - [x] 11.2 **Live check (required before review → done).** Epic 11's live gate lists 11.6–11.9 only, but this story changes what a member sees when a job fails and its headline claim is "a blocked restore works again after a restart", which tests cannot show end to end (same reasoning as Story 11.1). With the local stack (`scripts/dev-up.sh`, in-process queue, Postgres) and Claude-in-Chrome: (a) start a restore (or a Smart Plug import) and stop the API while the job is `Queued`/`Processing` (`scripts/dev-down.sh`, or kill the API process; if the job finishes too quickly to catch, insert a `Processing` row by hand with `psql` and say so in the Completion Notes), bring the stack back up; (b) in the UI the job shows the translated "interrupted" message (switch Settings → language to check en-US and de-DE), not the raw code; (c) restore-confirm for the same Household is accepted again and the restore completes; (d) for the 5-minute path, leave a hand-inserted `Processing` row with a stale heartbeat in place, reload the Settings/Import page and confirm it is failed on first read. If Chrome is not connected, raise it immediately and pause; do not check this off on test evidence alone.

### Review Findings

Code review 2026-10-10 (Blind Hunter, Edge Case Hunter, Acceptance Auditor; diff vs `bd10ba0`, 45 files, generated Designer files excluded). 2 decision-needed, 5 patch, 4 defer, 24 dismissed. All decisions resolved and patches applied the same day; 2 of the 4 deferred items were then fixed directly (see the [x] notes).

- [x] [Review][Decision] Live check (Task 11.2) evidence substitutes need Ralf's explicit acceptance — the "interrupted" rows were hand-inserted with `psql` and the restore steps (a)/(c) were driven with same-origin `fetch`, not by killing a real running job and clicking through the UI. Project-context says a substitute stays blocked in review until Ralf accepts it and the acceptance is recorded in the story; the Completion Notes already say "Ralf to confirm acceptance". Options: accept (record it here, story can go to done) or re-run (a)/(c) through the UI. **Resolved 2026-10-10: Ralf accepted the hand-inserted rows and `fetch`-driven restore steps as live-check evidence.**
- [x] [Review][Decision] `jobError.interrupted` copy says "The app restarted before this finished" but the same code is also written by the 5-minute stale-heartbeat path (e.g. a DB outage), where no restart happened. The wording follows the story spec, so changing it is Ralf's call. Options: keep as is, or make it neutral ("This was interrupted before it finished…") in en-US and de-DE, plus the matching sentence in `docs/data-import-restore.md`. **Resolved 2026-10-10: neutral wording chosen and applied (en-US "This was interrupted before it finished…", de-DE "Das wurde unterbrochen, bevor es abgeschlossen war…"), tests and docs updated.** [web/src/locales/en-US/translation.json, web/src/locales/de-DE/translation.json]
- [x] [Review][Patch] AD-6 guard test only inspects lines that name a `BackgroundJobStatus.X` literal, so `job.Status = newStatus;` or `SetProperty(j => j.Status, status)` with a variable passes silently; Task 3.1 asks to catch any `.Status =` on a `BackgroundJob` and any `SetProperty(…Status…)` against `BackgroundJobs`. Also `CodeLines` cuts a line at the first `//` (a string containing a URL hides code after it). [tests/EnergyTracker.Architecture.Tests/BackgroundJobStatusHasOneWriterTests.cs:48-59,101-106]
- [x] [Review][Patch] `BackgroundJobProcessor` stops the heartbeat in two places instead of one `finally` (Task 6.2); if the generic `catch` body throws (e.g. `LogError`), the loop is never cancelled and keeps heartbeating a dead job until host shutdown. [src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs:82-110]
- [x] [Review][Patch] `BackgroundJobLifecycle` header comment claims every call carries an explicit `HouseholdId` predicate; only `FailStaleAsync` and `FailInterruptedBeforeAsync` do, the other four filter by `Id` + status/token and rely on the AD-3 query filter via `JobHouseholdContext`. Fix the comment (and the matching Dev Notes line). [src/EnergyTracker.Infrastructure/Adapters/BackgroundJobLifecycle.cs:8-12]
- [x] [Review][Patch] Stale comment: `HouseholdRestoreWriter` says "BackgroundJobProcessor tracks its own BackgroundJob row across this entire call", but the rewritten processor tracks no entity. [src/EnergyTracker.Infrastructure/Adapters/HouseholdRestoreWriter.cs:174]
- [x] [Review][Patch] `docs/data-import-restore.md` "If a restore is interrupted" overclaims: "an interrupted restore changes nothing" and "a long restore that is still running is never failed" ignore the accepted Known risk (DB outage over 5 min makes a live job look stale and it is reported `job-interrupted` even if its transaction committed); "marked failed as soon as the app starts" ignores that the sweep is a single attempt, and "with any queue" ignores that a lost `Queued` message on the Azure adapter is not recovered until Story 11.3. Add the caveats. [docs/data-import-restore.md:291-310]
- [x] [Review][Defer] **Fixed 2026-10-10:** `BackgroundJobProcessor` now retries `TryStart`, `TryComplete` and `TryFail` up to 3 attempts (`JobLifecycleTimings.TransitionRetryDelay`, 500 ms base, never retried once the stopping token is cancelled), three new tests. Originally: a transient DB error in `TryCompleteAsync`/`TryFailAsync` (or in `TryStartAsync` for a dequeued in-process message) was not retried: the exception leaves the row `Processing` (later reported `job-interrupted` although the work finished) or `Queued` with its message dropped (blocks restore until the next restart). Same exposure as the old `SaveChangesAsync`. [src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs:32,112-114] — fixed directly
- [x] [Review][Defer] Heartbeat "ownership lost" only stops the heartbeat and a stale takeover can start a second run while the original owner is still alive (GC pause, DB outage); the ownership token protects the status row, not the side effects. Matters for multi-replica Azure redelivery, which Story 11.3 owns. [src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs:140-146, BackgroundJobLifecycle.cs:22-25] — deferred, belongs to 11.3 / AD-24
- [x] [Review][Defer] Stale comments in `SmartPlugImportRepository` (lines 38 and 62) still say `BackgroundJobProcessor` tracks its own `BackgroundJob`; the file is off-limits to this story (11.19/11.4). [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:38,62] — deferred, out of story scope
- [x] [Review][Defer] **Fixed 2026-10-10:** the single-instance assumption is now stated in the `InProcessJobStartupSweep` header and in `docs/self-hosting.md` (one app container per database); Story 11.13 still owns the broader invariant doc. Originally: the startup sweep fails every pre-start `Queued`/`Processing` row for all Households, so it would kill another live replica's jobs if two in-process instances shared a database; correct only under the AD-24 single-instance reading. [src/EnergyTracker.Infrastructure/Adapters/InProcessJobStartupSweep.cs:28-40] — fixed directly

## Dev Notes

### What this fixes and why (read first)

- **C8 (Medium):** with the default in-process `Channel` queue a restart loses the message, and the `BackgroundJobs` row stays `Queued`/`Processing` forever. Restore-confirm and the cleanup endpoint both refuse to start while such a row exists, so the Household is stuck until someone edits the database. The Azure adapter has the same hole when a message is lost or a replica dies mid-job, and its recovery (dequeue limit, poison handling, receive-loop resilience) is Story 11.3, not this one.
- **C7, processor half:** `BackgroundJobProcessor` rethrows *every* `OperationCanceledException` past the status update, so an `HttpClient` timeout or a command timeout that surfaces as a cancellation leaves the job `Processing` forever. Only a cancellation caused by the stopping token is a shutdown.
- **AD-6 (amended 2026-10-10)** is the rule set; the AC text above is its summary. Re-read `invariants-rules.md` AD-6 "Job liveness" and AD-24 before coding. Decisions already made there, do not reopen: thresholds are **1 minute** heartbeat and **5 minutes** stale, held as operational constants in one place (not Household config, AD-15 does not apply); staleness is evaluated **lazily at read time, never by a timer** (AD-7 reasoning); `Queued` is never time-swept; the startup sweep is in-process adapter only; no poison queue.
- **Retro action #1 (AMENDED marking):** this create-story changed no epic AC. Items 9 and 10 above are implied requirements and are labelled as such. If an Ask First or review decision changes an AC while you work, mark it `AMENDED <date, decision>` with the old text struck through.

### Current state of each file you will touch (read before editing)

| File | Today | Change |
|---|---|---|
| `src/EnergyTracker.Domain/BackgroundJob.cs` | `Id, HouseholdId, JobType, Status (settable), ErrorMessage, OriginalFileName, QueuedByHouseholdMemberId, CreatedAtUtc, CompletedAtUtc`. `BackgroundJobStatus { Processing, Completed, Failed, Queued }`, stored as a plain int, **`Queued` is last on purpose**: never insert or reorder values (comment on the enum explains why). | Add `StartedAtUtc`, `HeartbeatAtUtc` (nullable). **Do not touch the enum.** |
| `src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs` (187 lines) | Singleton. Per message: scope, set `JobHouseholdContext`, load the row as a **tracked** entity, three near-identical branches (missing → insert `Processing`; `Queued` → conditional `ExecuteUpdate`; already `Processing` → reuse), run the use case by `JobType` (4 types), `job.Status = Completed`, or `Failed` + `ErrorMessage` rule, then `SaveChangesAsync(cancellationToken)`. `catch (OperationCanceledException) { throw; }` for **any** OCE. | Rewrite per Tasks 5–6. Keep the `JobType` switch and the payload deserialization exactly as they are. |
| `src/EnergyTracker.Infrastructure/Adapters/BackgroundJobEnqueueRecorder.cs` | Singleton, inserts the `Queued` row at enqueue (`CreatedAtUtc = DateTimeOffset.UtcNow`), and `DeleteAsync` removes it when the queue send fails. | **Unchanged.** The initial insert is not a status *change*, and the compensating delete is not a transition. The guard test (3.1) allows exactly this. |
| `src/EnergyTracker.Infrastructure/Adapters/InProcessChannelJobQueue.cs` | `EnqueueAsync` records then writes the channel. `InProcessChannelJobProcessingService.ExecuteAsync` is `await foreach (… ReadAllAsync …)` with a per-message `try/catch` that logs and continues. | Add the startup sweep before the loop (Task 7.3). Nothing else. |
| `src/EnergyTracker.Infrastructure/Adapters/AzureStorageQueueJobQueue.cs` | Receive loop, 8 messages per poll, 60 min visibility timeout, no `try/catch` around receive, no dequeue limit; message deleted after `ProcessAsync` returns without throwing. | **Do not touch** (Story 11.3). Its observable contract with the processor must stay: `ProcessAsync` returning normally means "delete the message", throwing means "leave it". The new "terminal or missing → return normally" relies on exactly that. |
| `src/EnergyTracker.Application/GetBackgroundJobStatus.cs` | `FindByIdAsync` then optional Smart Plug import enrichment. | Inject `IBackgroundJobLifecycle`; call `FailStaleAsync` first. |
| `src/EnergyTracker.Application/ListSmartPlugImportJobs.cs` | Lazy `SweepExpiredAsync` (imports only), `ListByJobTypeAsync(ProcessSmartPlugImport)`, derives the six states; `DeriveState` throws on an unknown status. | `FailStaleAsync` first; CorrelateEvent sweep after the import sweep. `RetentionWindow` is `internal static`; reuse it. |
| `src/EnergyTracker.Application/CleanUpSmartPlugImportJobs.cs` | Computes the cutoff, calls `smartPlugImportRepository.DeleteJobsAsync`. | Add the CorrelateEvent cleanup (Task 8.3). |
| `src/EnergyTracker.Api/Endpoints/HouseholdImportEndpoints.cs` | `confirm`: consume token, then `ListByJobTypeAsync(RestoreHouseholdData)`, 409 if any `Queued`/`Processing`. | `FailStaleAsync` before the list. Nothing else (upload streaming is 11.10, the DB-enforced single-active-restore guard is 11.14). |
| `src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs` (~line 170–200) | Cleanup `DELETE`: `ListByJobTypeAsync(CleanUpSmartPlugImportJobs)`, returns the active job's id if one is `Queued`/`Processing`. | `FailStaleAsync` before the list. |
| `src/EnergyTracker.Api/Endpoints/JobEndpoints.cs` | Generic, `GET /jobs/{id}` → `GetBackgroundJobStatus`. `JobStatusResponse.ErrorMessage` is a plain string. | **No change.** The code travels in the existing `errorMessage` field (`status` is `failed`). |
| `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` | `SweepExpiredAsync` (bounded, import jobs only), `DeleteJobsAsync` + `DeleteEligibleAsync` (cleanup, import jobs only). ~1,190 lines. | **Do not edit** (Story 11.19 splits it; Story 11.4 edits it). The CorrelateEvent cleanup goes on `IBackgroundJobRepository`, which is why. |
| `src/EnergyTracker.Infrastructure/Adapters/CurrentHouseholdAccessor.cs` | `EnsureResolved()` **caches** the resolved `HouseholdId` for the life of the scope. | Do not change. It is why the startup sweep needs a fresh scope per Household (below). |
| `src/EnergyTracker.Api/Program.cs` (~line 416–453) | `JobHouseholdContext` scoped, `BackgroundJobProcessor` + `BackgroundJobEnqueueRecorder` singletons, `IBackgroundJobRepository` scoped, `switch (JobQueue:Provider)`. | Register `IBackgroundJobLifecycle` (scoped), the timing settings (singleton), and the sweep in the in-process branch. No new config value, no environment branching. |

### Design (follow it, don't redesign it)

**Port**, `src/EnergyTracker.Application/Ports/IBackgroundJobLifecycle.cs` (names are a recommendation; keep the semantics):

```csharp
public interface IBackgroundJobLifecycle
{
    // Queued -> Processing, or a Processing row with a stale heartbeat -> Processing (redelivery of an orphan).
    // Returns the new ownership token (StartedAtUtc), or null when the row is missing, terminal, or still owned.
    Task<DateTimeOffset?> TryStartAsync(Guid jobId, CancellationToken cancellationToken);
    Task<bool> TryHeartbeatAsync(Guid jobId, DateTimeOffset token, CancellationToken cancellationToken);
    Task<bool> TryCompleteAsync(Guid jobId, DateTimeOffset token, CancellationToken cancellationToken);
    Task<bool> TryFailAsync(Guid jobId, DateTimeOffset token, string? errorMessage, CancellationToken cancellationToken);
    // Read-path check: fails this Household's stale Processing rows with "job-interrupted". Returns the count.
    Task<int> FailStaleAsync(Guid householdId, CancellationToken cancellationToken);
    // Startup sweep: fails this Household's Queued/Processing rows created before the process started.
    Task<int> FailInterruptedBeforeAsync(Guid householdId, DateTimeOffset processStartedAtUtc, CancellationToken cancellationToken);
}
```

Story 11.3 will add the dequeue-limit transition (`job-retries-exhausted`, expected `Queued`/`Processing`, no token) to this same port; keep the adapter's shared private transition helper general enough that it is one more method, not a second writer. Do not add it now.

**Transition table** (every row is one `ExecuteUpdateAsync … Where(HouseholdId-filtered, Id, …)`; "affected rows == 0" means the caller no longer owns the job):

| Transition | Expected status | Extra predicate | Sets |
|---|---|---|---|
| Start | `Queued`, **or** `Processing` | for `Processing` only: `COALESCE(HeartbeatAtUtc, StartedAtUtc, CreatedAtUtc) < now − 5 min` | `Processing`, `StartedAtUtc = HeartbeatAtUtc = token` |
| Heartbeat | `Processing` | `StartedAtUtc = token` | `HeartbeatAtUtc = now` |
| Complete | `Processing` | `StartedAtUtc = token` | `Completed`, `CompletedAtUtc = now` |
| Fail (owner) | `Processing` | `StartedAtUtc = token` | `Failed`, `ErrorMessage`, `CompletedAtUtc = now` |
| Fail stale | `Processing` | `HouseholdId` and the same stale predicate as Start | `Failed`, `ErrorMessage = "job-interrupted"`, `CompletedAtUtc = now` |
| Fail interrupted | `Queued`, `Processing` | `HouseholdId` and `CreatedAtUtc < processStartedAtUtc` | `Failed`, `ErrorMessage = "job-interrupted"`, `CompletedAtUtc = now` |

- `COALESCE(HeartbeatAtUtc, StartedAtUtc, CreatedAtUtc)` (LINQ `j.HeartbeatAtUtc ?? j.StartedAtUtc ?? j.CreatedAtUtc`) is deliberate. A `Processing` row written by the **previous** image (rollback, or the deploy that applies this migration) has no heartbeat or start time. Falling back to `CreatedAtUtc` makes such rows fail after 5 minutes instead of never. Accepted side effect: if the previous image is redeployed against the migrated schema, a long-running job it started can be failed after 5 minutes because it never heartbeats. Rollback-only, document nothing more than a code comment.
- The two redelivery cases both end correctly: live owner (`Processing`, fresh heartbeat) → `TryStart` returns `null` → processor returns → the Azure adapter deletes the message; if that owner later dies, the row goes stale and fails as `job-interrupted`. Stale orphan whose message is redelivered first → restarted with a fresh token (AD-6: "a redelivered message that legitimately restarts the job gets a fresh token"). A stale-fail that wins the race against `TryStart` simply makes `TryStart` return `null`.
- Always call `FailStaleAsync` **before** reading the rows in the same scope. `ExecuteUpdateAsync` bypasses the change tracker, so a row already tracked in that scope would show stale values. None of the four call sites tracks the rows before the call; keep it that way.
- AD-1: `ExecuteUpdateAsync` is EF, so the adapter is Infrastructure; the Application layer only sees the port. AD-2: portable LINQ only (no `FromSqlRaw`, no provider branches). AD-3: the standard query filter applies to every call (Household from `JobHouseholdContext` on the job path); the two bulk methods that take a `householdId` also state it as an explicit predicate; no `IgnoreQueryFilters()` anywhere in this story.

**Token precision (the trap).** The token is `StartedAtUtc` and must compare equal after a database round trip. .NET ticks are 100 ns; Postgres `timestamptz` stores microseconds and would silently truncate, so `StartedAtUtc = @token` would never match and every job would fail its own final transition. Generate the token truncated to whole milliseconds (e.g. `new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero)`), in one helper, and keep the value in memory for the later calls. Test 1.3 is the proof on both providers. (`UtcDateTimeOffsetConverter` normalises offset to UTC on write, which is fine; it does not alter precision.)

**Timing settings.** Put `HeartbeatInterval = 1 min` and `StaleAfter = 5 min` in one Application-level type, e.g. `public sealed record JobLifecycleTimings(TimeSpan HeartbeatInterval, TimeSpan StaleAfter)` with a `Default`, registered as a singleton in `Program.cs`. The processor and the adapter take it by constructor. That keeps the thresholds in one place and lets the heartbeat test use a 50 ms interval without sleeping for a minute. Time source: `DateTimeOffset.UtcNow`, like the surrounding code. Story 11.17 converts the whole codebase to `TimeProvider`; don't start that here.

**Heartbeat isolation.** The heartbeat must not share the job's `DbContext` or connection: restore runs one long transaction on its scope's context (120 s command timeout, chunked). Use `IServiceScopeFactory` per tick, set `JobHouseholdContext`, resolve `IBackgroundJobLifecycle`, call, dispose. A heartbeat failure is logged and ignored; it must never fail or cancel the job. Use the job's stopping token for the loop, `CancellationToken.None` is wrong there (the loop must end on shutdown).

**Terminal transitions use `CancellationToken.None`.** They are single short statements. Passing a stopping token that has just been cancelled would leave a job that finished (e.g. a restore that already committed) `Processing`, to be failed later as `job-interrupted` while its data was in fact replaced. This is the same discipline the codebase uses for compensating cleanup (`AzureStorageQueueJobQueue` catch block, `DeletePartiallyPersistedImportAsync`). For a *shutdown* cancellation, `ProcessAsync` rethrows **without** a transition, as today.

**Startup sweep (AC #4), gotchas:**
- One fresh DI scope **per Household**, `JobHouseholdContext.HouseholdId` set first. `CurrentHouseholdAccessor` caches the first resolution for the scope's life, so reusing one scope across Households would apply Household 1's query filter to everyone.
- `Household` has no query filter (AD-3-exempt, see `EnergyTrackerDbContext.OnModelCreating`), so listing ids needs no household context and no `IgnoreQueryFilters`.
- `processStartedAtUtc` is captured when `InProcessChannelJobProcessingService` is constructed (before the host serves a request), so a row enqueued by this process has `CreatedAtUtc >=` it and is never swept. Compare with `<`.
- The sweep **must never** stop the host: `BackgroundService.ExecuteAsync` faulting stops the host by default (`StopHost`), and the database can be unreachable or unmigrated at that moment. Catch, log at error, continue into the channel loop. The lazy stale check cannot rescue `Queued` rows, so a failed sweep on the in-process adapter leaves them until the next restart; that residual is accepted (single attempt, no retry loop).
- Container Apps runs old and new revisions side by side, but the in-process adapter is the self-host/local default only; Azure uses `azurestoragequeue` and never runs this sweep (AD-6, AD-24).

**Test host start order.** `EnergyTrackerApiFactory.InitializeAsync` starts the container, then touches `Services` (which builds and **starts the host**, including hosted services), and only then calls `MigrateAsync`. So in the Api tests the automatic startup sweep runs against an unmigrated database and fails; with the try/catch above that is just a logged error. That is why test 2.5 invokes the sweep explicitly through DI. Do not "fix" the factory's order in this story.

### CorrelateEvent rows (AC #7, #8)

- Created by `CreateEvent.cs` (~line 97) and `RequeueEventCorrelations.cs` (~line 77) with `Guid.NewGuid()` job ids that are **never returned** to any caller. They are only reachable through `BackgroundJobProcessor`.
- The member-visible list is `ListSmartPlugImportJobs` → `ListByJobTypeAsync(householdId, JobTypes.ProcessSmartPlugImport)`, which filters on job type. `GET /api/jobs/{id}` needs a known id and the frontend never has one for these rows. **Confirmed: they do not surface; no hiding or labelling is needed.** The stale check and the startup sweep fail them like any other type (`job-interrupted`, no retry); the correlation re-evaluates the next time a Meter Reading in the Event's window changes (Story 10.2), so that is acceptable (Retro 10 note).
- Retention: terminal means `Completed` or `Failed` with `CompletedAtUtc` set (every `Failed` transition in the new lifecycle sets it, including stale and startup failures). Cutoff `now − ListSmartPlugImportJobs.RetentionWindow` (30 days). Never touch `Queued`/`Processing` rows here; a stuck one is the stale check's job.
- Both existing sweep entry points get it: the lazy read-triggered sweep (bounded, one batch per read) and the manual cleanup job (`deleteAll` removes all terminal CorrelateEvent rows, "older than 30 days" uses the cutoff). The cleanup job runs under `JobHouseholdContext`, so it needs nothing beyond the explicit `HouseholdId` predicate.
- No FK references `BackgroundJobs` from CorrelateEvent data (only `SmartPlugImport.BackgroundJobId`, which is irrelevant to this job type), so a plain delete is safe.

### What must be preserved (regression guard)

- A successful job still ends `Completed` with `CompletedAtUtc`; a failing use case still ends `Failed`; `ErrorMessage` is still `ex.Message` **only** for `SmartPlugImportValidationException` / `HouseholdImportValidationException` and `null` otherwise (Round-4 incident fix: the backend never sends English text for generic failures; the client supplies its own localized fallback). Codes are the new, third kind of value.
- Shutdown (stopping token cancelled) still leaves the job `Processing` and rethrows; `ProcessSmartPlugImport` and `RestoreHouseholdData` skip their temp-file cleanup when the token is cancelled (unchanged, so a redelivered message can still find the file).
- A message for a job row that **cleanup deleted** is dropped, and a job whose row cleanup deletes **mid-run** ends with a zero-row final transition that is logged and swallowed. Before this story the same situation threw `DbUpdateConcurrencyException`; now it is quiet by design (AD-6: no resurrecting deleted rows).
- Smart Plug cleanup `deleteAll` deletes `Queued`/`Processing` import jobs by design (Story 3.10); that does not change.
- The `BackgroundJobStatus` enum order and the plain-int storage, `QueuedByHouseholdMemberId` `SetNull`, the enqueue-time `Queued` row, the compensating `DeleteAsync`, the polling contract (`GET /api/jobs/{id}`, no push) and the 202 + `jobId` shape of both enqueueing endpoints.
- The restore-confirm token is consumed before the active-job check (so a 409 still burns the token and deletes the temp file). Unchanged.
- AD-3/AD-24 prohibitions: no `IgnoreQueryFilters()`, no `Find()`, no `FromSqlRaw` against Household-scoped entities; process-local mechanisms keep a one-line comment pointing at AD-24 (put one on the startup sweep).

### Frontend

- The codes ride in the existing `errorMessage` string, with `status: "failed"`. A code is recognised only by **exact equality** with one of the three strings; anything else is displayed as before (a validation sentence is shown verbatim, `null` falls back).
- Sites (verified 2026-10-10): `web/src/components/data-import/use-household-import-job.ts:49`, `web/src/components/smart-plug-import/use-smart-plug-import-job.ts:113`, `web/src/components/smart-plug-import/job-history-list.tsx:152` (cleanup) and `:215` (per-row meta line).
- Catalog keys, top level `jobError` in both catalogs (parity enforced by `translation-consistency.test.ts`). Use the existing "du" register and the term "Haushalt" where relevant:
  - `interrupted`: en "The app restarted before this finished, so it was stopped. Please try again." / de "Die App wurde neu gestartet, bevor dies abgeschlossen war, und wurde daher abgebrochen. Bitte versuche es erneut."
  - `retriesExhausted`: en "This could not be completed after several attempts. Please try again." / de "Das konnte auch nach mehreren Versuchen nicht abgeschlossen werden. Bitte versuche es erneut."
  - `uploadMissing`: en "The uploaded file is no longer available on the server. Please upload it again." / de "Die hochgeladene Datei ist auf dem Server nicht mehr verfügbar. Bitte lade sie erneut hoch."
- This story **adds the translations for all three codes** (epic AC) but only **emits** `job-interrupted`. `job-retries-exhausted` and `upload-missing` are produced by Story 11.3; don't change `RestoreHouseholdData`'s existing missing-file `HouseholdImportValidationException` text or `ProcessSmartPlugImport` here.
- No new UI states, no mock needed (text in existing error slots). The history list's `error` badge already covers a failed job; only the suffix text changes.

### Docs to change (AC #10)

- `docs/data-import-restore.md`: new short section placed near the existing job/"Write target" material (the doc already describes polling and the single transaction, lines ~48–53 and ~247). Facts to state: one transaction, so an interrupted restore changes nothing; message the member sees; safe to upload again; 5-minute stale rule and the immediate startup recovery for the default in-process queue.
- `docs/self-hosting.md`, section "5. Stopping and restarting": one sentence (see Task 10.1). The single-process statement itself lives in `docs/data-import-restore.md` (~lines 68–70), not in self-hosting.md; the full AD-24 self-hosting section is Story 11.13.
- Don't rewrite `docs/codebase-audit-2026-10-10.md`; it is a dated record.

### Architecture compliance

- **AD-6 (amended)** is the story. **AD-3** (job path resolves Household from the envelope via `JobHouseholdContext`, never `IgnoreQueryFilters`), **AD-7** (no timer deciding anything; the heartbeat is liveness evidence written by the worker, staleness is judged on read), **AD-2** (portable LINQ, migration for both providers in one commit via the script), **AD-1** (port in Application, adapter in Infrastructure; the new architecture test also pins the port's location), **AD-18** (failure reasons are stable codes translated by the client catalogs), **AD-24** (the startup sweep and the single-instance reading of "stale" are process-local-friendly; add the one-line AD-24 comment), **AD-11** unaffected.
- API errors stay RFC 7807; no new endpoint, route or config value.
- Hard process gate: this change to already-shipped code is covered by this story file, which is the linked spec.

### Library / framework notes

No new packages and no version bumps. `ExecuteUpdateAsync`/`ExecuteDeleteAsync` (EF Core 10.0.10) are already used in `BackgroundJobProcessor` and `SmartPlugImportRepository`; `PeriodicTimer` is BCL. EF applies the `UtcDateTimeOffsetConverter` to `ExecuteUpdate` parameters. Web research was not needed: nothing here depends on an external API or a library behaviour newer than the repo already uses.

### Testing standards (from project-context.md)

- One test class per subject, `{Subject}Tests`, snake_case behaviour names, Shouldly only, `TestContext.Current.CancellationToken` on async tests, NSubstitute against Application ports only, real Testcontainers databases for anything touching SQL (an NSubstitute-mocked lifecycle cannot prove the conditional update; the proofs are Task 1 and Task 2.5/2.6).
- Dual-provider shape: abstract base + `Postgres…`/`SqlServer…` subclasses (see `HouseholdRestoreWriterTests`). Postgres-only is enough for the processor and sweep orchestration tests, because the SQL is already proven on both providers in Task 1.
- Don't sleep to cross thresholds: seed `HeartbeatAtUtc`/`CreatedAtUtc` in the past. The heartbeat-advances test is the one place that needs a short real interval (inject `JobLifecycleTimings`).
- Frontend: colocated `*.test.tsx`, locale catalogs in parity.

### Project structure notes

- New files: `Application/Ports/IBackgroundJobLifecycle.cs` (+ the timings type), `Infrastructure/Adapters/BackgroundJobLifecycle.cs`, `Infrastructure/Adapters/InProcessJobStartupSweep.cs`, the two migration pairs (generated), `tests/EnergyTracker.Architecture.Tests/BackgroundJobStatusHasOneWriterTests.cs`, new Infrastructure tests, `web/src/lib/job-error.ts` (+ test). Flat `Application/` (no feature folders), one type per file.
- Branch `feature/11-2-job-lifecycle-stale-recovery` is checked out. Commit prefix `feat:`/`fix:`/`doc:`; reference "Story 11.2, AD-6, audit C8". Don't merge without this story file linked.

### Deploy (Ralf)

- This is the first migration-bearing Epic 11 deploy. **Epic 10 retro action #3 (Ralf):** inspect the Summary tab of this deploy's Actions run (pending-migrations list and the patched "Pre-migration restore point" block) and note the result. The migration is two nullable columns (expand-only), so the restore point and rollback runbook (`infra/README.md` → "Rolling back a bad migration") are the safety net but should not be needed.
- A job that is `Processing` in production when the deploy lands has no heartbeat; the COALESCE fallback fails it after 5 minutes. Expected, harmless.

### Known risk to watch (not a blocker)

- SQL Server lock escalation: the manual cleanup job deletes many `BackgroundJobs` rows in one long transaction (`DeleteEligibleAsync`); `deferred-work.md` already records the escalation risk for related tables. If that escalates to a table lock on `BackgroundJobs`, heartbeat writes from other connections wait, and a long cleanup could be judged stale by a concurrent status read. Heartbeat failures are non-fatal by design (logged, retried next tick), the final transition ignores token cancellation, and Azure SQL defaults to RCSI so reads do not block. If you see it in the live check or in tests, record it as an `[open]` deferred entry (Task 10.4) rather than widening this story.
- A database outage longer than 5 minutes while a job runs makes a live job look stale; its final transition then affects zero rows and is logged. Accepted by AD-6's design (the job is reported `job-interrupted` even if its transaction did commit). Retry is safe: a restore is wholesale replace.

### Previous work intelligence

- **Story 11.1 (done, `bd10ba0`):** restore no longer writes `HouseholdMember`; `BackgroundJob.QueuedByHouseholdMemberId` stays `SetNull` for Story 11.9. Lessons that apply: red-first including **dual-provider** proof for anything DB-semantic; a real live Chrome pass found things tests could not; evidence substitutes (iframe, hand-seeded rows) need to be disclosed and, where the project-context gate applies, explicitly accepted by Ralf. 11.1 shipped no migration; this one does.
- **Story 3.6 / AD-6 extension:** introduced the `Queued` row, `BackgroundJobEnqueueRecorder`, and the conditional `Queued → Processing` update the processor already uses; the redelivery tests in `BackgroundJobProcessorTests` encode that history. Keep their intent.
- **Story 3.10 / `spec-3-10-cleanup-async-job` and follow-ups:** the cleanup job and the "reuse the still-active cleanup job" check in the DELETE endpoint are exactly the "cleanup check" in AC #3. The production incident history (120 s command timeout, chunked deletes in one transaction) is why the heartbeat has its own scope.
- **Story 7.2 (restore):** the `409` guard and the temp-file lifecycle; `RestoreHouseholdData` deletes the file in `finally` when not cancelled.
- **Story 10.2:** per-Meter-Reading-write `CorrelateEvent` jobs; `deferred-work.md` entries on unswept rows are closed here.
- **Epic 10 retro:** Story 10.1's expand/contract rule, restore point and rollback runbook underpin this migration; the retro folded the `CorrelateEvent` AC into this story.
- **Git (last 5):** `bd10ba0` Story 11.1 (#96), `794ffe1` Epic 10 retro doc, `f3089ee` Epic 11 planning (AD-24–AD-27), `6212bdc` dev-up/dev-down scripts (#95), `5e1d763` undici bump. Nothing in recent history touches the job pipeline; baseline is `bd10ba0`.

### References

- [Source: _bmad-artifacts/planning/epics/epic-11-codebase-audit-hardening.md#Story 11.2]
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-6, #AD-24, #AD-3, #AD-7]
- [Source: docs/codebase-audit-2026-10-10.md#C7, #C8, #C3]
- [Source: _bmad-artifacts/planning/sprint-change-proposal-2026-10-10.md (Story 11.2)]
- [Source: _bmad-artifacts/implementation/epic-10-retro-2026-10-10.md (Decisions, Action Items #1, #3)]
- [Source: _bmad-artifacts/implementation/11-1-restore-export-never-carry-access-grants.md], [3-6-smart-plug-import-job-status-history.md], [3-10-manual-job-history-cleanup.md], [7-2-full-data-import-restore-migration.md], [10-2-event-correlation-forward-window-recompute.md]
- [Source: _bmad-artifacts/project-context.md] (Async jobs AD-6, Migrations expand/contract, Testing Rules, Process gates, Security)
- [Source: _bmad-artifacts/implementation/spec-datetimeoffset-utc-normalization.md] (converter; offset only, precision untouched)
- [Source: docs/data-import-restore.md], [docs/self-hosting.md]

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5.5 (claude-sonnet-5-5)

### Debug Log References

- Red phase: after Tasks 1-3 the three test projects (Infrastructure, Application, Api) did not compile (no `IBackgroundJobLifecycle`, `JobLifecycleTimings`, `InProcessJobStartupSweep`, `DeleteTerminalByJobTypeAsync`); the frontend `job-error.test.ts` failed on a missing module.
- Guard sanity check: a temporary `job.Status = BackgroundJobStatus.Failed` in a throwaway `src/` file made `BackgroundJobStatusHasOneWriterTests` fail with the file named; file removed.
- Final runs: `dotnet test EnergyTracker.sln` 1013 passed / 0 failed (Architecture 14, Application 452+, Infrastructure 303+, Api 244, includes both providers' migration chains and the SQL Server lifecycle tests); web `npm test` 568 passed, `npx tsc -b` clean, `oxlint` 4 warnings (same 4 as before the change, none in new files). No new compiler warnings from files this story touched.

### Implementation Plan

- Domain: `BackgroundJob.StartedAtUtc` (ownership token) and `HeartbeatAtUtc`, both nullable; `ErrorMessage` documented as validation sentence, stable code, or null.
- Application: `IBackgroundJobLifecycle` port (TryStart/Heartbeat/Complete/Fail, FailStale, FailInterruptedBefore), `JobLifecycleTimings` (1 min heartbeat, 5 min stale; singleton), `JobFailureCodes` constants. Read paths (`GetBackgroundJobStatus`, `ListSmartPlugImportJobs`) call `FailStaleAsync` first; `IBackgroundJobRepository.DeleteTerminalByJobTypeAsync` (one bounded batch of 200) backs the CorrelateEvent retention in the list read and the cleanup job.
- Infrastructure: `BackgroundJobLifecycle` is the only `Status` writer, `ExecuteUpdateAsync` only, explicit `HouseholdId` predicate where one exists, `COALESCE(Heartbeat, Started, Created)` staleness; token truncated to whole milliseconds (provider round trip). `BackgroundJobProcessor` rewritten: `TryStart`, heartbeat loop (own scope per tick, `PeriodicTimer`, failures logged), `catch (OCE) when stopping token cancelled` rethrow else fail, terminal transitions with `CancellationToken.None`, false result logged and swallowed. `InProcessJobStartupSweep` (one scope per Household, `JobHouseholdContext` first) registered for the in-process adapter only and called, guarded by try/catch, before the channel loop.
- Api: restore-confirm and cleanup `DELETE` call `FailStaleAsync` before listing. No route, DTO or config change.
- Migration `AddBackgroundJobStartedAndHeartbeat` for both providers via `scripts/add-migration.sh`: two nullable `AddColumn<DateTimeOffset>` each, nothing else; both snapshots updated. Expand-only.
- Frontend: `translateJobError(t, errorMessage, fallback)` (exact-match of the three codes), `jobError.*` keys in both catalogs, used at the four sites; every non-code value renders as before.

### Completion Notes List

- All 10 ACs satisfied. AC #8 was confirmed at create-story (CorrelateEvent rows do not surface in the member-visible list); nothing hidden or labelled.
- Tests are red-first as the story required (see Debug Log). Dual-provider proof for the token round trip, conditional transitions, `FailStale` and `FailInterruptedBefore` (Postgres and SQL Server). The `ProcessAsync_inserts_a_fresh_row...` fallback test was deleted and replaced by `ProcessAsync_does_nothing_when_the_row_is_missing`; the processor tests now use the `CleanUpSmartPlugImportJobs` use case with NSubstitute ports as the controllable "job that runs".
- Test-design note: the heartbeat-exception case (2.2 g) uses an NSubstitute `IBackgroundJobLifecycle` (no DB), everything else uses the real adapter on Postgres.
- Added `EnergyTrackerApiFactory.SeedBackgroundJobAsync` for the Api tests.
- **Live check (Task 11.2), done with Claude-in-Chrome on the local stack (in-process queue, Postgres, de-DE then en-US).** Evidence substitutes, disclosed per the project's live-check rule: the "interrupted" rows were **hand-inserted with psql** (a `Queued` Smart Plug import row, a `Queued` `RestoreHouseholdData` row and two `Processing` rows with heartbeats 6 min and 20 s old), not produced by killing a real running job (jobs finish in milliseconds locally); the restart itself was real (`scripts/dev-down.sh` then `scripts/dev-up.sh`). Results: (a) before the restart restore-confirm returned **409** with the orphaned `Queued` restore row; after the restart the startup sweep logged `failed 2 job(s) ... (job-interrupted)` and both rows were `Failed`/`job-interrupted`; (b) the Smart Plug import history showed the translated sentence in de-DE ("Die App wurde neu gestartet, bevor dies abgeschlossen war ...") and in en-US ("The app restarted before this finished, so it was st..."), no raw code anywhere; (c) validate + confirm of the Household's own export then returned **202** and the restore job **completed**; (d) a `Processing` row with a 6-minute-old heartbeat was shown as failed/`job-interrupted` on the first list read after a page reload, while a `Processing` row (30 min old, 20 s heartbeat) stayed `Processing`. The restore calls in (a)/(c) were driven with same-origin `fetch` from the logged-in tab (export, validate, confirm, poll); the UI display checks were real clicks. Side effects reverted: the UI language was toggled to EN and back to DE; the seeded rows were deleted; the restore replaced the dev Household's data with its own export; the stack was stopped. Ralf accepted the hand-inserted rows as evidence in code review (2026-10-10).
- The local dev database had to be migrated with `scripts/migrate.sh` before the live check (the app does not auto-migrate). The first API start on the old schema logged a harmless sweep failure per Household and kept serving, which is the "sweep never stops the host" behaviour.
- SQL Server lock escalation with heartbeats (Dev Notes "Known risk") was not observed in tests or the live run, so no `[open]` entry was added (Task 10.4: no real leftovers found).
- Not done on purpose (per story scope): `AzureStorageQueueJobProcessingService` untouched (Story 11.3 emits `job-retries-exhausted`/`upload-missing`), `SmartPlugImportRepository` untouched (11.19/11.4), `TimeProvider` conversion left to 11.17.
- Deploy reminder for Ralf (Epic 10 retro action #3): this is the first migration-bearing Epic 11 deploy, inspect the Summary tab of the Actions run (pending-migrations list and the "Pre-migration restore point" block). A job `Processing` in production at deploy time has no heartbeat and fails as `job-interrupted` after 5 minutes (expected, harmless).

### File List

Source
- src/EnergyTracker.Domain/BackgroundJob.cs (modified)
- src/EnergyTracker.Application/Ports/IBackgroundJobLifecycle.cs (new)
- src/EnergyTracker.Application/JobLifecycleTimings.cs (new)
- src/EnergyTracker.Application/JobFailureCodes.cs (new)
- src/EnergyTracker.Application/Ports/IBackgroundJobRepository.cs (modified)
- src/EnergyTracker.Application/GetBackgroundJobStatus.cs (modified)
- src/EnergyTracker.Application/ListSmartPlugImportJobs.cs (modified)
- src/EnergyTracker.Application/CleanUpSmartPlugImportJobs.cs (modified)
- src/EnergyTracker.Infrastructure/Adapters/BackgroundJobLifecycle.cs (new)
- src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs (rewritten)
- src/EnergyTracker.Infrastructure/Adapters/BackgroundJobRepository.cs (modified)
- src/EnergyTracker.Infrastructure/Adapters/InProcessJobStartupSweep.cs (new)
- src/EnergyTracker.Infrastructure/Adapters/InProcessChannelJobQueue.cs (modified)
- src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20261010203512_AddBackgroundJobStartedAndHeartbeat.cs, .Designer.cs (new), EnergyTrackerDbContextModelSnapshot.cs (modified)
- src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20261010203515_AddBackgroundJobStartedAndHeartbeat.cs, .Designer.cs (new), EnergyTrackerDbContextModelSnapshot.cs (modified)
- src/EnergyTracker.Api/Program.cs (modified)
- src/EnergyTracker.Api/Endpoints/HouseholdImportEndpoints.cs (modified)
- src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs (modified)

Tests
- tests/EnergyTracker.Infrastructure.Tests/BackgroundJobLifecycleTests.cs (new, Postgres + SQL Server)
- tests/EnergyTracker.Infrastructure.Tests/BackgroundJobProcessorTests.cs (reworked)
- tests/EnergyTracker.Infrastructure.Tests/InProcessJobStartupSweepTests.cs (new)
- tests/EnergyTracker.Infrastructure.Tests/InProcessChannelJobProcessingServiceTests.cs (new)
- tests/EnergyTracker.Infrastructure.Tests/BackgroundJobRepositoryTests.cs (new)
- tests/EnergyTracker.Application.Tests/GetBackgroundJobStatusTests.cs (new)
- tests/EnergyTracker.Application.Tests/ListSmartPlugImportJobsTests.cs, CleanUpSmartPlugImportJobsTests.cs (modified)
- tests/EnergyTracker.Architecture.Tests/BackgroundJobStatusHasOneWriterTests.cs (new)
- tests/EnergyTracker.Api.Tests/EnergyTrackerApiFactory.cs, HouseholdImportEndpointsTests.cs, JobEndpointsTests.cs, SmartPlugImportEndpointsTests.cs (modified)

Frontend
- web/src/lib/job-error.ts, job-error.test.ts (new)
- web/src/locales/en-US/translation.json, de-DE/translation.json (modified)
- web/src/components/data-import/use-household-import-job.ts, data-import-panel.test.tsx (modified)
- web/src/components/smart-plug-import/use-smart-plug-import-job.ts, job-history-list.tsx, job-history-list.test.tsx, smart-plug-import-page.test.tsx (modified)

Docs and tracking
- docs/data-import-restore.md, docs/self-hosting.md (modified)
- _bmad-artifacts/project-context.md (modified, Async jobs bullet)
- _bmad-artifacts/implementation/deferred-work.md (modified: closed the CorrelateEvent sweep entries)
- _bmad-artifacts/implementation/sprint-status.yaml (modified)
- _bmad-artifacts/implementation/11-2-job-lifecycle-conditional-transitions-stale-recovery.md (this file)

### Change Log

- 2026-10-10: Implemented Story 11.2 (AD-6 amended, audit C8 and the processor half of C7): `IBackgroundJobLifecycle` as the single conditional-update writer of `BackgroundJob.Status`, 1-minute heartbeat and 5-minute lazy stale recovery, in-process startup sweep, non-shutdown cancellation now ends the job `Failed`, terminal `CorrelateEvent` rows swept with the 30-day retention, failure codes translated in de-DE and en-US, docs and deferred list updated. Migration `AddBackgroundJobStartedAndHeartbeat` (expand-only) for both providers. Live check performed with Claude-in-Chrome (hand-seeded rows disclosed above).
- 2026-10-10: Code review (BMad): 5 patches applied (AD-6 guard scanner now catches variable writes and has scanner tests, heartbeat stopped in a `finally`, lifecycle and `HouseholdRestoreWriter` comments corrected, restore docs caveats), `interrupted` copy made neutral in en-US/de-DE, bounded retry of the lifecycle statements on transient database errors, single-instance assumption documented for the startup sweep. Live-check evidence accepted by Ralf. Tests: Architecture 24, Application 452, Infrastructure 306, Api 244, web job tests 81 all passing. Status review -> done.
