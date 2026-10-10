---
baseline_commit: b48cb3c
---

# Story 11.3: Azure Queue Consumer Resilience

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- Epic 11 "Codebase audit hardening" (planning/epics/epic-11-codebase-audit-hardening.md). Origin: codebase audit 2026-10-10 (docs/codebase-audit-2026-10-10.md) finding C7 (Medium); also closes the C3 behaviour under AD-24 (missing temp payload file). Architecture: AD-6 (amended 2026-10-10, "Azure consumer (C7)", "Missing payload file", "Failure reasons are stable error codes"), AD-24, AD-3. This file cites the AD and audit IDs, which satisfies the spec-per-fix gate (project-context.md). -->
<!-- Branch already exists: feature/11-3-azure-queue-resilience. -->
<!-- 2026-10-11: AC #6 (12-hour backstop for message-less Queued rows) added at Ralf's request; it amends AD-6 and 11.2's "Queued rows are never failed for age". -->
<!-- Depends on Story 11.2 (merged: b48cb3c, PR #101). Updated against the merged code on 2026-10-11: IBackgroundJobLifecycle, JobFailureCodes, the rewritten BackgroundJobProcessor and the one-writer architecture guard exist. -->

## Story

As the operator of the Azure deployment,
I want transient queue errors and poison messages to never take down the app or loop forever,
so that one bad message or storage hiccup doesn't stop every request (audit C7).

## Acceptance Criteria

1. **Given** `CreateIfNotExistsAsync` or `ReceiveMessagesAsync` throws, **when** the consumer loop runs, **then** the exception is caught and logged, the loop backs off (exponential, capped) and retries, and the host keeps running. `ExecuteAsync` never faults on a storage/network error; the default `BackgroundServiceExceptionBehavior.StopHost` is never triggered. Proven with a faulting `QueueClient` substitute.
2. **Given** the consumer, **when** it polls, **then** it receives exactly one message per call (`maxMessages: 1`).
3. **Given** a message whose `DequeueCount` exceeds 5 (i.e. the 6th delivery), **when** it is received, **then** its job is conditionally failed with code `job-retries-exhausted` (only from `Queued`/`Processing`; a terminal status is never overwritten, and a missing row is not an error) and the message is deleted. The job is **not** processed.
4. **Given** a job whose temp payload file no longer exists (replica replaced, AD-24), **when** it is processed (`ProcessSmartPlugImport` and `RestoreHouseholdData`), **then** it ends terminally `Failed` with code `upload-missing`, the message is deleted, and nothing retries.
5. **Given** `ProcessSmartPlugImport` fails and `PersistFailedImportAsync` also throws, **when** the error propagates, **then** the original exception is logged first and is the one that propagates (not masked by the persist failure).
6. **Given** a `Queued` job older than 12 hours (typically a row whose queue message no longer exists), **when** `GET /api/jobs/{id}`, the job list, restore-confirm or the cleanup check runs, **then** that Household's such jobs are first marked failed with code `job-interrupted` (same conditional update as stale `Processing` jobs, never overwriting a terminal status). `Queued` jobs younger than 12 hours are never failed for age. This is an AD-6 amendment (2026-10-11, Ralf) that revises 11.2's "never failed for age" rule, so no manual database edit is needed to unblock restore or cleanup.
7. **Implied (not in the epic text; poison-message guarantee):** a queue message that cannot be turned into a `JobMessage` (undecodable Base64, invalid JSON, JSON `null`) can never succeed on retry, so it is logged and **deleted immediately**, without waiting for the dequeue limit. Without this the receive call itself throws on an undecodable body and the message cycles forever, which the story's goal ("poison messages never loop forever") forbids.

## Tasks / Subtasks

Test-first: write Task 1's tests and see them **red** against current code before Tasks 2–7.

- [ ] Task 0: Preconditions (verify, 11.2 is merged)
  - [ ] 0.1 `git log` on the branch contains `b48cb3c` (Story 11.2); `sprint-status.yaml` shows 11-2 as `done`.
  - [ ] 0.2 Read, do not re-create: `src/EnergyTracker.Application/Ports/IBackgroundJobLifecycle.cs`, `Adapters/BackgroundJobLifecycle.cs`, `JobFailureCodes.cs` (`Interrupted`, `RetriesExhausted` = `"job-retries-exhausted"`, `UploadMissing` = `"upload-missing"`; already defined, with comments saying this story emits the last two), `JobLifecycleTimings.cs`, the rewritten `Adapters/BackgroundJobProcessor.cs`, `web/src/lib/job-error.ts` and the `jobError.*` catalog keys (en-US and de-DE already contain `retriesExhausted` and `uploadMissing`: **no frontend change is needed**), and `tests/EnergyTracker.Architecture.Tests/BackgroundJobStatusHasOneWriterTests.cs`.
  - [ ] 0.3 The lifecycle port has **no** "fail from `Queued`/`Processing` without a token" method (`TryFailAsync` requires the `StartedAtUtc` token). Add exactly one method to the same port and `BackgroundJobLifecycle`: `Task<bool> TryFailNonTerminalAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken)` — `ExecuteUpdateAsync … WHERE Id = @id AND Status IN (Queued, Processing)` setting `Status=Failed`, `ErrorMessage`, `CompletedAtUtc`, true when a row changed. It must be written in `BackgroundJobLifecycle.cs` (the architecture guard rejects any other file that assigns `Status`). Never write job status from the consumer or processor.
- [ ] Task 1: Tests, red first (AC: #1–#6)
  - [ ] 1.1 `AzureStorageQueueJobProcessingServiceTests` (Infrastructure.Tests, existing file; keep the visibility-timeout test). Use `Substitute.For<QueueClient>()`; build messages with `QueuesModelFactory.QueueMessage(messageId, popReceipt, messageText, dequeueCount, …)` (public factory in `Azure.Storage.Queues.Models`, verified in 12.22.0; `DequeueCount` is `long`). Cases, each with `TestContext.Current.CancellationToken` and **no real multi-second waits** (see Task 2.4 for the delay seam):
    - (a) `CreateIfNotExistsAsync` throws once, then succeeds → service keeps running, error logged, `ReceiveMessagesAsync` is eventually called, `ExecuteTask` is not faulted.
    - (b) `ReceiveMessagesAsync` throws repeatedly → `ExecuteTask` stays running (not completed, not faulted), the recorded backoff delays grow and then cap, a success resets them, `StopAsync` returns promptly. A fresh start after a faulting `CreateIfNotExistsAsync` still creates the queue once it succeeds.
    - (c) `ReceiveMessagesAsync` is called with `maxMessages: 1` and a visibility timeout ≥ 30 min (extend the existing assertion; do **not** lower the 60 min value).
    - (d) `DequeueCount == 6` → processor's retries-exhausted path called with that message, `ProcessAsync` **not** called, message deleted. `DequeueCount == 5` → processed normally.
    - (e) Retries-exhausted call throws (e.g. DB down) → message **not** deleted, error logged, loop continues.
    - (f) Undecodable/invalid-JSON/`null` body → processor never called, message deleted, error logged (AC #6).
    - (g) `ProcessAsync` throws an unexpected exception → message not deleted, logged, loop continues to the next message (existing behaviour; pin it).
    - (h) Happy path: `ProcessAsync` returns → message deleted, **in that order** (delete only after processing).
    - (i) Cancellation of `stoppingToken` during receive or during a backoff delay → `StopAsync` completes cleanly, no error logged for the cancellation.
  - [ ] 1.2 Lifecycle + processor tests. (z) `BackgroundJobLifecycleTests` (both providers), **replacing** the `queuedForHours` assertion in `FailStale_fails_only_Processing_rows_whose_heartbeat_is_older_than_five_minutes` (line ~309/324; rename the test to say Processing *and old Queued*): a `Queued` row created 5 h ago stays `Queued`; one created 13 h ago becomes `Failed` with `job-interrupted` and `CompletedAtUtc`; a `Queued` row exactly younger than the limit is untouched; another Household's old `Queued` row is untouched; a `Completed` row older than 12 h is untouched; the returned count includes the Queued row; a `Queued` row failed by age whose message is later delivered is skipped by `ProcessAsync` (existing "terminal row" test covers the skip; add one explicit case). (a) `BackgroundJobLifecycleTests` (the abstract base runs on **Postgres and SQL Server**, add the new cases there): `TryFailNonTerminal` fails a `Queued` row and a `Processing` row (any token/heartbeat state) → `Failed`, given `ErrorMessage`, `CompletedAtUtc` set, returns true; leaves `Completed` and already-`Failed` rows unchanged and returns false; returns false for a missing id; never touches another Household's row (AD-3 filter via `JobHouseholdContext`/`FixedHouseholdAccessor`, no `IgnoreQueryFilters`). (b) `BackgroundJobProcessorTests` (Postgres): `FailRetriesExhaustedAsync` on a `Queued` and a `Processing` row → `Failed` with `ErrorMessage == JobFailureCodes.RetriesExhausted`; on a terminal row and on a missing row → no change, no throw; retries a transient database error like the other transitions. (c) A `RestoreHouseholdData` job whose `TempFilePath` does not exist, run through `ProcessAsync` with a real `RestoreHouseholdData` over a substituted `IHouseholdRestoreWriter` (cheapest real use case to register), ends `Failed` with `ErrorMessage == JobFailureCodes.UploadMissing`, `ProcessAsync` returns normally (so the consumer deletes the message), and a `HouseholdImportValidationException`'s message is still forwarded verbatim (existing test stays green). The `ProcessSmartPlugImport` side is covered at Application level (1.3).
  - [ ] 1.3 `ProcessSmartPlugImportTests` (Application.Tests): (a) missing temp file → throws the new `UploadMissingException` and still persists a `Failed` `SmartPlugImport` row (keeps the job-history entry); (b) `parser.ReadDeviceTag` throws `X` **and** `smartPlugImportRepository.AddAsync` for the failed row throws `Y` → `Should.ThrowAsync<X>`; and `X` was logged (`Substitute.For<ILogger<ProcessSmartPlugImport>>()` with `Log(LogLevel.Error, Arg.Any<EventId>(), Arg.Any<Arg.AnyType>(), x, …)` — NSubstitute 6 `Arg.AnyType`) **before** the `AddAsync` call (`Received.InOrder`). No hand-rolled logger class, no new test package.
  - [ ] 1.4 `RestoreHouseholdDataTests` (Application.Tests): rewrite `A_missing_temp_file_throws_a_HouseholdImportValidationException_with_a_user_facing_message` (line ~203) to expect `UploadMissingException`.
  - [ ] 1.5 `QueueClient` options wiring (AC #6): if `QueueMessageDecodingFailedEventArgs` can be constructed in a test (it has a public constructor), unit-test the decode-failure handler (calls `DeleteMessageAsync(messageId, popReceipt)` for a received message, logs, never throws). If it cannot be constructed, cover the handler by reading the code carefully and record that in Completion Notes; do not skip the handler.
- [ ] Task 2: Consumer loop (AC: #1, #2, #6) — `AzureStorageQueueJobQueue.cs`, class `AzureStorageQueueJobProcessingService`
  - [ ] 2.1 Restructure `ExecuteAsync`: one outer `while (!stoppingToken.IsCancellationRequested)`; the **whole** body (queue ensure, receive, per-message handling) inside `try`. Move `CreateIfNotExistsAsync` inside the loop behind a `queueEnsured` flag so a failing create is retried (with backoff) until it succeeds. `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)` → leave the loop quietly. `catch (Exception ex)` → log once at Error (include the consecutive failure count and next delay), then `await` the backoff delay with `stoppingToken`.
  - [ ] 2.2 Backoff: exponential, `InitialBackoff = 5 s`, doubling, `MaxBackoff = 5 min`; reset to initial after any receive that completes without throwing (empty or not). Only ensure/receive faults drive the backoff; a failure while handling one message does **not** (see 2.5). Keep the 5 s `PollInterval` for an empty queue; after a handled message loop again immediately.
  - [ ] 2.3 Receive `maxMessages: 1`; keep `MessageVisibilityTimeout = 60 min` and its comment (the 2026-09-01 redelivery storm is why; fix the comment's "up to 8" wording only if it mentions it).
  - [ ] 2.4 Make the delays testable without real waiting, following the pattern 11.2 just established: a small `sealed record QueueConsumerTimings(TimeSpan PollInterval, TimeSpan InitialBackoff, TimeSpan MaxBackoff)` with a `Default` (5 s, 5 s, 5 min), registered `AddSingleton(QueueConsumerTimings.Default)` in the `"azurestoragequeue"` case, injected into the service; tests use `with`/short values like `JobLifecycleTimings`. (`TimeProvider` is Story 11.17's job; do not start it here.)
  - [ ] 2.5 Per message: (1) deserialize `JobMessage` (`queueMessage.MessageText`); `JsonException`/`null` → log Error with message id and **delete** (AC #6). (2) `queueMessage.DequeueCount > MaxDequeueCount` (`private const long MaxDequeueCount = 5`) → `await processor.FailRetriesExhaustedAsync(message, ct)` then delete. (3) otherwise `await processor.ProcessAsync(message, ct)` then delete. Any other exception → log Error with message id and `DequeueCount`, leave the message undeleted so it reappears after the visibility timeout (existing behaviour), continue with the next poll.
  - [ ] 2.6 Do not add a poison queue, a visibility-renewal loop, or parallel processing (AD-6: the `BackgroundJob` row is the record; one at a time).
- [ ] Task 3: Dequeue limit in the processor (AC: #3) — `BackgroundJobProcessor.cs`
  - [ ] 3.1 Add `internal async Task FailRetriesExhaustedAsync(JobMessage message, CancellationToken cancellationToken)` to `BackgroundJobProcessor`: create a scope, set `JobHouseholdContext.HouseholdId = message.HouseholdId` **first** (same opening lines as `ProcessAsync`; AD-3, never `IgnoreQueryFilters`), resolve `IBackgroundJobLifecycle`, call `TryFailNonTerminalAsync(message.JobId, JobFailureCodes.RetriesExhausted, …)` through the existing `RetryTransientAsync` helper. `false` → log Warning ("already terminal or missing") and return normally; `true` → log Error ("retries exhausted, job failed"). The consumer deletes the message in both cases; an exception propagates (message stays, see Task 2.5).
  - [ ] 3.2 Test seam so consumer tests need no database: add `internal interface IJobMessageProcessor { Task ProcessAsync(JobMessage, CancellationToken); Task FailRetriesExhaustedAsync(JobMessage, CancellationToken); }` implemented by `BackgroundJobProcessor` (keep its public class and constructor; `InProcessChannelJobProcessingService` keeps taking the concrete class), and add `<InternalsVisibleTo Include="DynamicProxyGenAssembly2" />` next to the existing `EnergyTracker.Infrastructure.Tests` entry in `EnergyTracker.Infrastructure.csproj` so NSubstitute can proxy the internal interface. Register `IJobMessageProcessor` to the existing `BackgroundJobProcessor` singleton (`sp => sp.GetRequiredService<BackgroundJobProcessor>()`). The consumer takes `IJobMessageProcessor`. If you find a simpler seam, take it and say so in Completion Notes.
- [ ] Task 3b: Time-based backstop for message-less `Queued` rows (AC: #6)
  - [ ] 3b.1 `JobLifecycleTimings`: add `public TimeSpan QueuedExpiresAfter { get; init; } = TimeSpan.FromHours(12);` (init property, like `TransitionRetryDelay`, so the positional constructor and `Default` callers stay unchanged; tests shorten it with `with`). Comment: why 12 h (longer than a serial batch plus five 60-minute redelivery cycles; AD-6 amended 2026-10-11).
  - [ ] 3b.2 `BackgroundJobLifecycle.FailStaleAsync`: extend the single `ExecuteUpdateAsync` predicate to `HouseholdId = @h AND ((Status = Processing AND heartbeat/start/created < now - StaleAfter) OR (Status = Queued AND CreatedAtUtc < now - QueuedExpiresAfter))`, same `job-interrupted` code and `CompletedAtUtc`. Still one statement, still the lifecycle's own file (architecture guard). Update the class header and `IBackgroundJobLifecycle.FailStaleAsync` comments: they currently say "Queued rows are never failed for age".
  - [ ] 3b.3 No new call sites: `GetBackgroundJobStatus`, `ListSmartPlugImportJobs`, restore-confirm and the cleanup check already call `FailStaleAsync` first. No new error code and no frontend change (`job-interrupted` copy is deliberately neutral). Race with a late dequeue is safe by construction: `TryStartAsync` and this update are both conditional on `Queued`, so exactly one wins; if the fail wins, the message is later skipped and deleted.
  - [ ] 3b.4 Known and accepted: a row failed this way whose message is still in the queue leaves its temp upload file behind until the instance's disk is replaced (the skip path never deleted it either).
- [ ] Task 4: `upload-missing` (AC: #4)
  - [ ] 4.1 New `UploadMissingException` in `EnergyTracker.Application` (one type per file, `*Exception` naming, like the other typed domain exceptions). Not a `*ValidationException`, so the processor does not forward its message text to the client.
  - [ ] 4.2 `ProcessSmartPlugImport.ExecuteAsync`: first statement inside the existing `try`: `if (!File.Exists(payload.TempFilePath)) throw new UploadMissingException(...)`. It then flows through the existing generic `catch` (persist a `Failed` `SmartPlugImport` row, rethrow), so the job-history list still shows the file. Keep the `finally` temp-file cleanup.
  - [ ] 4.3 `RestoreHouseholdData.ExecuteAsync`: replace the `HouseholdImportValidationException` thrown for a missing file with `UploadMissingException` (update the comment: the English message is replaced by a stable code, AD-6). Leave the "deserialized to nothing" validation exception as is.
  - [ ] 4.4 `BackgroundJobProcessor.ProcessAsync`'s generic `catch (Exception ex)` computes `failureMessage` (today: validation exceptions forward `ex.Message`, everything else `null`). Add `UploadMissingException` → `JobFailureCodes.UploadMissing` (map by exception **type**, never by message text), and update the comment there ("stable codes are written by the lifecycle, never from an exception message" becomes: the code comes from the exception type). The job then ends `Failed` through the existing `TryFailAsync`, and `ProcessAsync` returns normally. Do not special-case it in the consumer.
  - [ ] 4.5 Do **not** touch `HouseholdImportEndpoints`' own `File.Exists` checks at confirm time (lines ~136/161): they cover the earlier HTTP-level validate→confirm window; this task covers the later enqueue→dequeue window.
- [ ] Task 5: Don't mask the original exception (AC: #5) — `ProcessSmartPlugImport.cs`, the generic `catch (Exception)` block
  - [ ] 5.1 `catch (Exception ex)`: `logger.LogError(ex, …)` first (include job id and import id), then call `PersistFailedImportAsync` inside its own `try/catch (Exception persistEx)` that logs `persistEx` at Error and swallows it, then `throw;` (bare rethrow of the original, so the processor still records the real failure). Keep `catch (OperationCanceledException) { throw; }` above it untouched.
- [ ] Task 6: Composition root (AC: #6) — `Program.cs`, the `"azurestoragequeue"` case
  - [ ] 6.1 Subscribe `QueueClientOptions.MessageDecodingFailed` (async handler, follow the SDK sample: branch on `args.IsRunningSynchronously`, `args.ReceivedMessage` vs `args.PeekedMessage`). For a received message: log Error (message id, **not** the body: it may be arbitrary foreign data) and `DeleteMessageAsync(messageId, popReceipt)`; a failure to delete is logged and swallowed (the handler must never throw into the receive call). Resolve the logger via `sp.GetRequiredService<ILoggerFactory>()`. Put the handler in a small named method in Infrastructure (testable per 1.5) and keep `Program.cs` to the wiring line. Keep `MessageEncoding = QueueMessageEncoding.Base64`.
  - [ ] 6.2 Register `QueueConsumerTimings.Default` (2.4) and `IJobMessageProcessor` (3.2) in the `"azurestoragequeue"` case / next to `BackgroundJobProcessor`.
  - [ ] 6.3 No new config key, no Bicep/`infra/` change, no migration.
- [ ] Task 7: Comments and docs
  - [ ] 7.1 Rewrite the stale class comment on `AzureStorageQueueJobProcessingService` ("no bespoke retry/backoff logic is built here" is no longer true): one to three lines of *why* (loop never faults; dequeue limit 5; undecodable → delete). Add the AD-24-style one-liner only where it applies (it doesn't to this class: the Azure queue is durable).
  - [ ] 7.2 `docs/local-vs-azure-deltas.md`: add one short row/paragraph if (and only if) it fits the file's existing structure: the Azure adapter now survives storage errors and fails a job after 5 dequeues with `job-retries-exhausted`; the in-process adapter has no dequeue limit. Skip if it doesn't fit; do not restructure the doc.
  - [ ] 7.3 `docs/data-import-restore.md` "If a restore is interrupted" (added by 11.2): replace the last sentence of the "Recovery is automatic" bullet ("A job that only waits in a lost queue message is not covered by the 5-minute check (the Azure queue's own recovery is a separate piece of work)") with: a job that is still waiting after 12 hours, for example because its queue message was lost, is marked failed the same way, so it can no longer block a restore or cleanup for longer than that. Also fix any sentence in that section or in `docs/self-hosting.md` that says waiting jobs are never failed by age.
- [ ] Task 8: Regression and finish
  - [ ] 8.1 `dotnet test` (whole solution) green; `dotnet build` has no new warnings (note: the 6 existing `xUnit1051` warnings are Story 11.18's, but your new tests must pass `TestContext.Current.CancellationToken` and add none). `npm` suites are untouched (no frontend change in this story); run `npm test` only if you changed `web/`.
  - [ ] 8.2 No live Auth0/Chrome gate applies (no auth or browser behaviour). Optional manual smoke, not a blocker: run the Azure adapter against Azurite (`docker run -p 10001:10001 mcr.microsoft.com/azure-storage/azurite azurite-queue --queueHost 0.0.0.0`, connection string `UseDevelopmentStorage=true`, `JobQueue:Provider=azurestoragequeue`) and stop/start Azurite while the API runs to see the backoff log lines and recovery. Azurite is not wired into the repo; do not add it to CI or compose here.
  - [ ] 8.3 Append any leftovers to `deferred-work.md` per project-context convention (section at the end, `[open]` entries).

## Dev Notes

### What this fixes and why it is safe to change

- **C7:** `CreateIfNotExistsAsync` and `ReceiveMessagesAsync` sit outside the `try` in `ExecuteAsync`. Any storage/network exception escapes; since .NET 6 the default `BackgroundServiceExceptionBehavior` is `StopHost` and `Program.cs` does not override it, so the whole web app shuts down. Separately, a message whose processing keeps throwing is never deleted and reappears every 60 minutes forever. Also 8 messages were received under one 60-minute timeout (processed serially, so later ones could become visible again).
- No wire-format, schema, config or API change. Messages already in the queue keep working (`JobMessage` is unchanged). No migration, so the Story 10.1 restore-point / expand-contract rules don't apply.

### Current state of each file you will touch (read before editing)

- `src/EnergyTracker.Infrastructure/Adapters/AzureStorageQueueJobQueue.cs` — two classes. `AzureStorageQueueJobQueue.EnqueueAsync`: records the `Queued` row (`BackgroundJobEnqueueRecorder.RecordAsync`) **before** `SendMessageAsync`, and on a send failure best-effort deletes the orphan `Queued` row (review-round-2 patch). **Do not touch the enqueue side.** `AzureStorageQueueJobProcessingService`: `PollInterval` 5 s, `MessageVisibilityTimeout` 60 min, `CreateIfNotExistsAsync` once before the loop (unprotected), `ReceiveMessagesAsync(maxMessages: 8, …)` unprotected, per-message `try { deserialize; processor.ProcessAsync; DeleteMessageAsync } catch (Exception) when (!stoppingToken.IsCancellationRequested) { LogError }`.
- `src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs` (post-11.2; ctor `(IServiceScopeFactory, JobLifecycleTimings, ILogger)`): `ProcessAsync` creates a scope, sets `JobHouseholdContext` first, `lifecycle.TryStartAsync` (via `RetryTransientAsync`, 3 attempts) returns the `StartedAtUtc` token or `null` (missing / terminal / live owner → logs and **returns normally**, which is the "delete the message" signal), runs a 1-minute heartbeat from its own scopes, executes the use case by `JobType`, then `TryCompleteAsync`/`TryFailAsync` with `CancellationToken.None`. Shutdown cancellation (`OperationCanceledException` with the stopping token cancelled) is rethrown and leaves the job `Processing`; any other exception is `Failed` with `ErrorMessage` = the message for `SmartPlugImportValidationException`/`HouseholdImportValidationException`, else `null`. `ProcessAsync` is `internal`, non-virtual.
- `src/EnergyTracker.Application/Ports/IBackgroundJobLifecycle.cs` + `Adapters/BackgroundJobLifecycle.cs`: the only writer of `BackgroundJob.Status` (`BackgroundJobStatusHasOneWriterTests` scans `src/` and fails on any other file assigning `Status`, except the enqueue recorder's initial `Queued`). Methods today: `TryStartAsync`, `TryHeartbeatAsync`, `TryCompleteAsync`, `TryFailAsync` (token required), `FailStaleAsync`, `FailInterruptedBeforeAsync`. `TryStartAsync` also takes over a `Processing` row whose heartbeat is older than 5 min, so an Azure redelivery of a crashed attempt restarts under a fresh token.
- `src/EnergyTracker.Application/JobFailureCodes.cs`: the three codes already exist; frontend translation (`web/src/lib/job-error.ts`, `jobError.*` in both catalogs) already exists. This story only **emits** `job-retries-exhausted` and `upload-missing`.
- `src/EnergyTracker.Application/ProcessSmartPlugImport.cs` — `ExecuteAsync` `try` → generic `catch (Exception)` calls `PersistFailedImportAsync` then `throw;` → `finally` deletes the temp file when not cancelled. `PersistFailedImportAsync` writes a `Failed` `SmartPlugImport` via `smartPlugImportRepository.AddAsync(failedImport, [], ct)`; if it throws, it replaces the original exception (AC #5). Today a missing file surfaces as `FileNotFoundException` from `File.OpenRead` (null error message, no stable code).
- `src/EnergyTracker.Application/RestoreHouseholdData.cs` — already checks `File.Exists` first and throws `HouseholdImportValidationException` with an English sentence; that text would be forwarded verbatim to the client (violates the AD-6 "stable error codes, not English text" rule).
- `src/EnergyTracker.Api/Program.cs` ~line 416–453 — `JobQueue:Provider` switch (read once, AD-6). The `QueueClient` is a singleton built with `QueueClientOptions { MessageEncoding = Base64 }`.
- Tests: `tests/EnergyTracker.Infrastructure.Tests/AzureStorageQueueJobProcessingServiceTests.cs` (one test; `Substitute.For<QueueClient>()`, passes `null!` as processor when no message is delivered), `BackgroundJobProcessorTests.cs` (Postgres Testcontainer, `BuildServices`, `FixedHouseholdAccessor`), `tests/EnergyTracker.Application.Tests/ProcessSmartPlugImportTests.cs` (NSubstitute ports, `NullLogger`), `RestoreHouseholdDataTests.cs`.

### What must be preserved (regression guard)

- `MessageVisibilityTimeout` stays 60 min and the test that pins it stays green. Lowering it recreates the 2026-09-01 incident (~2.7M log lines in 16 minutes, Log Analytics daily cap).
- A message is deleted only after the processor returned normally, or after a deliberate terminal decision (retries exhausted, unparseable, already terminal). Never delete on an unexpected exception.
- Shutdown cancellation: the job stays `Processing`, the message stays undeleted (redelivered to the next instance). Only `OperationCanceledException` with `stoppingToken.IsCancellationRequested` is "shutdown".
- Only `BackgroundJobLifecycle` writes job status (architecture guard). The consumer never touches `BackgroundJobs`; the processor reaches it only through `IBackgroundJobLifecycle`.
- `ProcessAsync` returning normally = delete the message; throwing = leave it. 11.2's "missing / terminal / live owner → return normally" depends on exactly that; do not change it.
- The enqueue path, `JobMessage` shape, and `InProcessChannelJobQueue`/its service are untouched. No startup sweep on the Azure adapter (AD-6: the queue is durable).
- `ProcessSmartPlugImport` must keep writing the `Failed` `SmartPlugImport` row for ordinary failures (job-history list depends on it) and must keep deleting the temp file in `finally`.

### Design decisions already made (do not re-litigate)

- **Backoff** 5 s → ×2 → 5 min cap, reset on a clean receive. Cap keeps a long storage outage to ≤ 12 error lines/hour, which matters because stdout logging shares the Log Analytics daily cap that was hit on 2026-09-01.
- **Dequeue limit** = fail on the 6th delivery (`DequeueCount > 5`), per the epic's "exceeds 5". AD-6's prose says "after 5 dequeues"; the AC governs. First delivery has `DequeueCount == 1`.
- **Undecodable/unparseable message** → delete immediately (AC #6). Our own adapter always writes valid Base64 JSON, so this only fires for foreign producers or corruption.
- **Retries-exhausted fail is token-less** (`TryFailNonTerminalAsync`, Task 0.3: no `StartedAtUtc`, limited to `Queued`/`Processing`, error text = `JobFailureCodes.RetriesExhausted`). It also fails a `Processing` row that still heartbeats; accepted: reaching a 6th delivery takes at least five 60-minute visibility cycles.
- **Failed `SmartPlugImport` row is still written for `upload-missing`**, so the member sees the file in job history.
- **Epic 10 retro note (for 11.2's create-story check, confirmed here):** the member-visible job list (`ListSmartPlugImportJobs`) is filtered to `JobType == ProcessSmartPlugImport`, so `CorrelateEvent`, restore and cleanup rows never appear in it; they are only reachable by id through `GET /api/jobs/{id}`. `CorrelateEvent` messages go through the same dequeue limit and `RequeueEventCorrelations` can enqueue several per Meter Reading write; one-at-a-time receive serialises them. No extra handling here.

### Considered and left out (do not add to this story)

- **Double run after a stale takeover** (deferred item from 11.2's review: "ownership lost only stops the heartbeat; a stale takeover can run a job twice while the original is alive … fold into Story 11.3"). Not changed here. On the Azure adapter a second run needs the message to be redelivered (visibility timeout 60 min) **and** the original to have shown no heartbeat for more than 5 min, i.e. an attempt that is still alive after an hour but silent. AD-24 keeps the app at one instance, so there is no second replica to race. Disposition decided by Ralf 2026-10-11: accepted (single instance per AD-24; revisit if `maxReplicas` > 1). The entry has already been moved to `deferred-work-accepted.md`; nothing to do in this story.
- **A `Queued` row with no queue message** is now handled by the 12-hour read-time backstop (AC #6, Task 3b). Ways it arises: crash between the enqueue-time row insert and `SendMessageAsync`; a failed send whose best-effort cleanup also failed; a database rolled back to a restore point (Story 10.1 runbook) while the messages were already consumed; a purged queue or a message past Azure's 7-day TTL. Not covered and not needed: asking the queue whether a message exists (the API cannot look a message up by job id).
- Moving the temp payload to blob storage (C3), `TimeProvider` (11.17), single-instance observation (11.13).

### Architecture compliance

- **AD-6 (amended 2026-10-10):** "Azure consumer (C7)", "Conditional transitions only", "Missing payload file is a terminal failure, not a retry", "stable error codes" are this story's rules; `invariants-rules.md` lines ~36–52.
- **AD-3:** the retries-exhausted path runs in its own scope with `JobHouseholdContext` set first; never `IgnoreQueryFilters`, never `DbSet.Find`.
- **AD-24:** a missing temp file is accepted as a terminal failure, never a retry (the disk is not stable across replicas).
- **AD-1:** `UploadMissingException` lives in Application; Azure SDK types stay in Infrastructure.
- **Project conventions:** file-scoped namespaces, primary constructors, `Snake_case` test names, Shouldly, NSubstitute against ports (a `QueueClient` substitute is the established pattern for the SDK type), `TestContext.Current.CancellationToken`, one type per file.

### Library / framework notes

- `Azure.Storage.Queues` is pinned at **12.22.0** in `Directory.Packages.props`; do not bump it here (Story 11.5 owns dependency bumps).
- Verified in the 12.22.0 package docs: `QueuesModelFactory.QueueMessage(string messageId, string popReceipt, string messageText, long dequeueCount, DateTimeOffset? nextVisibleOn, DateTimeOffset? insertedOn, DateTimeOffset? expiresOn)` exists for building test messages; `QueueMessage.DequeueCount` is `long`; `QueueClientOptions.MessageDecodingFailed` is a `SyncAsyncEventHandler<QueueMessageDecodingFailedEventArgs>`, the client **does not delete** the message itself, and the handler must do so. Without a handler, a body that fails Base64 decoding makes the whole `ReceiveMessagesAsync` call throw.
- `BackgroundService`: since .NET 6 an exception escaping `ExecuteAsync` stops the host (`StopHost`); this is why the whole loop body is wrapped rather than changing `HostOptions`.
- NSubstitute 6.1.0 supports `Arg.AnyType` for `ILogger.Log<TState>` assertions.

### Testing standards (from project-context.md)

- Test class per subject `{Subject}Tests`, `Snake_case_with_underscores` method names, Shouldly, NSubstitute (no hand-rolled fakes of ports), `TestContext.Current.CancellationToken`.
- Real-DB behaviour (the conditional fail, AD-3 scoping) is tested against Testcontainers (`postgres:18-alpine` in `BackgroundJobProcessorTests`), not an in-memory provider. SQL Server coverage for the lifecycle service itself is 11.2's job.
- No test may wait for a real backoff delay (Task 2.4).

### Project Structure Notes

- No new projects. New production file: `src/EnergyTracker.Application/UploadMissingException.cs`; possibly an Infrastructure file for the decode-failure handler and `IJobMessageProcessor`. Everything else is edits in place.
- Branch `feature/11-3-azure-queue-resilience` exists; commit prefix `fix:`/`feat:`/`doc:`; reference "Story 11.3, AD-6, audit C7" in commits and the PR. Do not merge without this story file linked.

### Previous work intelligence

- **Story 3.6 (job status/history) and its review-round-2 patches** are in the code you are editing: the `Queued` row at enqueue time, the conditional `Queued→Processing` transition, the orphan-row cleanup on a failed send. 11.2 reshapes the processor part; leave the enqueue-side patches alone.
- **Incident `6c1ba91` (2026-09-01):** the 60-minute visibility timeout. Its regression test is the one existing test in `AzureStorageQueueJobProcessingServiceTests`; keep it and extend it.
- **Story 11.1 (done, `bd10ba0`) lessons:** write tests red first and record the red run in Debug Log References; keep scope to the AC list; record implied requirements explicitly (AC #6 here); use the existing test helpers rather than inventing parallel ones.
- **Story 11.2 (done, `b48cb3c`, PR #101) lessons:** one conditional `ExecuteUpdateAsync` per transition and a shared private helper; dual-provider lifecycle tests (abstract base + `Postgres…`/`SqlServer…` classes); `JobLifecycleTimings` as the injectable-timings pattern; the review found real bugs in the cancellation/heartbeat paths, so test shutdown (`stoppingToken` cancelled) explicitly. 11.2 left a note that its `Queued`-row recovery on the Azure adapter is this story's dequeue limit.
- **Epic 10 retro:** 10.2's uncapped `CorrelateEvent` rows interact with 11.2/11.3 (see Design decisions). 11.2's startup sweep and stale recovery don't apply to the Azure adapter's `Queued` rows: for those, this story's dequeue limit (message present) and 12-hour backstop (message gone) are the only terminal paths besides a successful run, which is why AC #3 must fail from `Queued` too.
- **Git (last 5):** `2710d1d` this story's definition, `b48cb3c` Story 11.2 (rewrote the processor, added the lifecycle service, left the Azure adapter untouched), `bd10ba0` Story 11.1, `794ffe1` Epic 10 retro doc, `f3089ee` Epic 11 planning. The queue adapter file's last real change is still `6c1ba91`.

### References

- [Source: _bmad-artifacts/planning/epics/epic-11-codebase-audit-hardening.md#Story 11.3]
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-6, #AD-24, #AD-3]
- [Source: docs/codebase-audit-2026-10-10.md#C7, #C8, #C3]
- [Source: _bmad-artifacts/planning/sprint-change-proposal-2026-10-10.md]
- [Source: _bmad-artifacts/implementation/epic-10-retro-2026-10-10.md]
- [Source: _bmad-artifacts/implementation/11-2-job-lifecycle-conditional-transitions-stale-recovery.md] (lifecycle service, deferred items, Dev Notes line 157)
- [Source: _bmad-artifacts/implementation/11-1-restore-export-never-carry-access-grants.md] (story format, red-first practice)
- [Source: _bmad-artifacts/implementation/3-6-smart-plug-import-job-status-history.md]
- [Source: _bmad-artifacts/project-context.md] (Testing Rules, Framework rules AD-3/AD-6, Process gates)
- [Source: Azure.Storage.Queues 12.22.0 package XML docs: `QueueClientOptions.MessageDecodingFailed`, `QueuesModelFactory.QueueMessage`]

## Dev Agent Record

### Agent Model Used

{{agent_model_name_version}}

### Debug Log References

### Completion Notes List

### File List
