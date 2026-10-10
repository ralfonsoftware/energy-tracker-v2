---
baseline_commit: bd10ba0
---

# Story 11.3: Azure Queue Consumer Resilience

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- Epic 11 "Codebase audit hardening" (planning/epics/epic-11-codebase-audit-hardening.md). Origin: codebase audit 2026-10-10 (docs/codebase-audit-2026-10-10.md) finding C7 (Medium); also closes the C3 behaviour under AD-24 (missing temp payload file). Architecture: AD-6 (amended 2026-10-10, "Azure consumer (C7)", "Missing payload file", "Failure reasons are stable error codes"), AD-24, AD-3. This file cites the AD and audit IDs, which satisfies the spec-per-fix gate (project-context.md). -->
<!-- Branch already exists: feature/11-3-azure-queue-resilience. -->
<!-- BLOCKED BY STORY 11.2 (epic: "11.3 needs 11.2"). 11.2 was still `backlog` with no story file when this file was written (2026-10-11). Task 0 is a hard gate. -->

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
6. **Implied (not in the epic text; poison-message guarantee):** a queue message that cannot be turned into a `JobMessage` (undecodable Base64, invalid JSON, JSON `null`) can never succeed on retry, so it is logged and **deleted immediately**, without waiting for the dequeue limit. Without this the receive call itself throws on an undecodable body and the message cycles forever, which the story's goal ("poison messages never loop forever") forbids.

## Tasks / Subtasks

Test-first: write Task 1's tests and see them **red** against current code before Tasks 2–7.

- [ ] Task 0: Preconditions (hard gate)
  - [ ] 0.1 Confirm Story 11.2 is `done` in `sprint-status.yaml` and merged into the branch you are on (`git log` shows it). **If not, HALT** and tell Ralf: 11.3 must not build its own job-status writer (AD-6: "all transitions go through one Application service — no second writer of job status").
  - [ ] 0.2 Read 11.2's story file and code: the lifecycle service (AD-6 suggests the name `IBackgroundJobLifecycle`; use whatever 11.2 actually named it), the failure-code constants (`job-interrupted`, `job-retries-exhausted`, `upload-missing` are all introduced by 11.2's i18n AC), the reshaped `BackgroundJobProcessor`, and the frontend catalog keys. Reuse them; do not add a second constants holder or string literals.
  - [ ] 0.3 Confirm the lifecycle service can do **"fail from `Queued` or `Processing` without an ownership token"** (needed for the dequeue limit: the message's attempts may have crashed mid-run, so there is no live `StartedAtUtc` to present). If 11.2 only offers the token variant, add the token-less variant **to that same service** (conditional `ExecuteUpdateAsync … WHERE Id AND Status IN (Queued, Processing)`; sets `Status=Failed`, `ErrorMessage=<code>`, `CompletedAtUtc`). Never write job status from the consumer or processor directly.
  - [ ] 0.4 Confirm 11.2 left the `catch (OperationCanceledException)` shutdown semantics for the **Azure** consumer intact (shutdown cancellation must leave the job `Processing` and the message undeleted).
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
  - [ ] 1.2 `BackgroundJobProcessorTests` (Infrastructure.Tests, Postgres Testcontainer like the existing tests): (a) retries-exhausted on a `Queued` row and on a `Processing` row → `Failed`, `ErrorMessage == "job-retries-exhausted"`, `CompletedAtUtc` set; on a `Completed` and on an already-`Failed` row → unchanged (terminal never overwritten); on a missing row → no throw. The row belongs to the message's Household and the call works with the AD-3 `JobHouseholdContext` set on its own scope (no `IgnoreQueryFilters`). (b) A `ProcessSmartPlugImport` job and a `RestoreHouseholdData` job whose `TempFilePath` does not exist → `ProcessAsync` returns normally (so the consumer deletes the message) and the row is `Failed` with `ErrorMessage == "upload-missing"`. Add the same two cases on SQL Server only if 11.2's lifecycle tests already run dual-provider; otherwise one provider is enough for this wiring.
  - [ ] 1.3 `ProcessSmartPlugImportTests` (Application.Tests): (a) missing temp file → throws the new `UploadMissingException` and still persists a `Failed` `SmartPlugImport` row (keeps the job-history entry); (b) `parser.ReadDeviceTag` throws `X` **and** `smartPlugImportRepository.AddAsync` for the failed row throws `Y` → `Should.ThrowAsync<X>`; and `X` was logged (`Substitute.For<ILogger<ProcessSmartPlugImport>>()` with `Log(LogLevel.Error, Arg.Any<EventId>(), Arg.Any<Arg.AnyType>(), x, …)` — NSubstitute 6 `Arg.AnyType`) **before** the `AddAsync` call (`Received.InOrder`). No hand-rolled logger class, no new test package.
  - [ ] 1.4 `RestoreHouseholdDataTests` (Application.Tests): rewrite `A_missing_temp_file_throws_a_HouseholdImportValidationException_with_a_user_facing_message` (line ~203) to expect `UploadMissingException`.
  - [ ] 1.5 `QueueClient` options wiring (AC #6): if `QueueMessageDecodingFailedEventArgs` can be constructed in a test (it has a public constructor), unit-test the decode-failure handler (calls `DeleteMessageAsync(messageId, popReceipt)` for a received message, logs, never throws). If it cannot be constructed, cover the handler by reading the code carefully and record that in Completion Notes; do not skip the handler.
- [ ] Task 2: Consumer loop (AC: #1, #2, #6) — `AzureStorageQueueJobQueue.cs`, class `AzureStorageQueueJobProcessingService`
  - [ ] 2.1 Restructure `ExecuteAsync`: one outer `while (!stoppingToken.IsCancellationRequested)`; the **whole** body (queue ensure, receive, per-message handling) inside `try`. Move `CreateIfNotExistsAsync` inside the loop behind a `queueEnsured` flag so a failing create is retried (with backoff) until it succeeds. `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)` → leave the loop quietly. `catch (Exception ex)` → log once at Error (include the consecutive failure count and next delay), then `await` the backoff delay with `stoppingToken`.
  - [ ] 2.2 Backoff: exponential, `InitialBackoff = 5 s`, doubling, `MaxBackoff = 5 min`; reset to initial after any receive that completes without throwing (empty or not). Only ensure/receive faults drive the backoff; a failure while handling one message does **not** (see 2.5). Keep the 5 s `PollInterval` for an empty queue; after a handled message loop again immediately.
  - [ ] 2.3 Receive `maxMessages: 1`; keep `MessageVisibilityTimeout = 60 min` and its comment (the 2026-09-01 redelivery storm is why; fix the comment's "up to 8" wording only if it mentions it).
  - [ ] 2.4 Make the delays testable without real waiting. Preferred (no new package, least invasive): `internal` constructor parameters or a small `internal sealed record` for `PollInterval`/`InitialBackoff`/`MaxBackoff` with the production values as defaults (MS DI honours default parameter values, so `AddHostedService<…>()` still resolves it). `TimeProvider` + `Microsoft.Extensions.TimeProvider.Testing` is also acceptable but is a new central package and belongs to Story 11.17's scope; do not start that migration here.
  - [ ] 2.5 Per message: (1) deserialize `JobMessage` (`queueMessage.MessageText`); `JsonException`/`null` → log Error with message id and **delete** (AC #6). (2) `queueMessage.DequeueCount > MaxDequeueCount` (`private const long MaxDequeueCount = 5`) → `await processor.FailRetriesExhaustedAsync(message, ct)` then delete. (3) otherwise `await processor.ProcessAsync(message, ct)` then delete. Any other exception → log Error with message id and `DequeueCount`, leave the message undeleted so it reappears after the visibility timeout (existing behaviour), continue with the next poll.
  - [ ] 2.6 Do not add a poison queue, a visibility-renewal loop, or parallel processing (AD-6: the `BackgroundJob` row is the record; one at a time).
- [ ] Task 3: Dequeue limit in the processor (AC: #3) — `BackgroundJobProcessor.cs`
  - [ ] 3.1 Add `internal async Task FailRetriesExhaustedAsync(JobMessage message, CancellationToken ct)`: create a scope, set `JobHouseholdContext.HouseholdId = message.HouseholdId` **before** resolving anything else (same first lines as `ProcessAsync`; AD-3, never `IgnoreQueryFilters`), call 11.2's lifecycle service (token-less fail from `Queued`/`Processing`, code `job-retries-exhausted`). Zero rows affected → log at Warning ("job already terminal or missing") and return normally; the consumer then deletes the message. Rows affected → log at Error ("retries exhausted, job failed").
  - [ ] 3.2 Test seam for the consumer: the consumer must be unit-testable without a database. Recommended: an `internal interface IJobMessageProcessor { ProcessAsync; FailRetriesExhaustedAsync }` implemented by `BackgroundJobProcessor`, registered in `Program.cs`, plus `<InternalsVisibleTo Include="DynamicProxyGenAssembly2" />` in `EnergyTracker.Infrastructure.csproj` (same style as the existing `InternalsVisibleTo` for `EnergyTracker.Infrastructure.Tests`) so NSubstitute can proxy it. `InProcessChannelJobProcessingService` may keep taking the concrete class; do not widen scope there. Alternative: drive the consumer tests with the real processor against the Postgres Testcontainer. Pick one and say which in Completion Notes.
- [ ] Task 4: `upload-missing` (AC: #4)
  - [ ] 4.1 New `UploadMissingException` in `EnergyTracker.Application` (one type per file, `*Exception` naming, like the other typed domain exceptions). Not a `*ValidationException`, so the processor does not forward its message text to the client.
  - [ ] 4.2 `ProcessSmartPlugImport.ExecuteAsync`: first statement inside the existing `try`: `if (!File.Exists(payload.TempFilePath)) throw new UploadMissingException(...)`. It then flows through the existing generic `catch` (persist a `Failed` `SmartPlugImport` row, rethrow), so the job-history list still shows the file. Keep the `finally` temp-file cleanup.
  - [ ] 4.3 `RestoreHouseholdData.ExecuteAsync`: replace the `HouseholdImportValidationException` thrown for a missing file with `UploadMissingException` (update the comment: the English message is replaced by a stable code, AD-6). Leave the "deserialized to nothing" validation exception as is.
  - [ ] 4.4 `BackgroundJobProcessor`'s catch: map `UploadMissingException` to the `upload-missing` code through the lifecycle service / error-code mapping 11.2 established (it must end `Failed`, not retry, and `ProcessAsync` must return normally so the message is deleted). Do not special-case it in the consumer.
  - [ ] 4.5 Do **not** touch `HouseholdImportEndpoints`' own `File.Exists` checks at confirm time (lines ~136/161): they cover the earlier HTTP-level validate→confirm window; this task covers the later enqueue→dequeue window.
- [ ] Task 5: Don't mask the original exception (AC: #5) — `ProcessSmartPlugImport.cs`, the generic `catch (Exception)` block
  - [ ] 5.1 `catch (Exception ex)`: `logger.LogError(ex, …)` first (include job id and import id), then call `PersistFailedImportAsync` inside its own `try/catch (Exception persistEx)` that logs `persistEx` at Error and swallows it, then `throw;` (bare rethrow of the original, so the processor still records the real failure). Keep `catch (OperationCanceledException) { throw; }` above it untouched.
- [ ] Task 6: Composition root (AC: #6) — `Program.cs`, the `"azurestoragequeue"` case
  - [ ] 6.1 Subscribe `QueueClientOptions.MessageDecodingFailed` (async handler, follow the SDK sample: branch on `args.IsRunningSynchronously`, `args.ReceivedMessage` vs `args.PeekedMessage`). For a received message: log Error (message id, **not** the body: it may be arbitrary foreign data) and `DeleteMessageAsync(messageId, popReceipt)`; a failure to delete is logged and swallowed (the handler must never throw into the receive call). Resolve the logger via `sp.GetRequiredService<ILoggerFactory>()`. Put the handler in a small named method in Infrastructure (testable per 1.5) and keep `Program.cs` to the wiring line. Keep `MessageEncoding = QueueMessageEncoding.Base64`.
  - [ ] 6.2 If you introduced `IJobMessageProcessor` (3.2), register `BackgroundJobProcessor` for it as the same singleton instance.
  - [ ] 6.3 No new config key, no Bicep/`infra/` change, no migration.
- [ ] Task 7: Comments and docs
  - [ ] 7.1 Rewrite the stale class comment on `AzureStorageQueueJobProcessingService` ("no bespoke retry/backoff logic is built here" is no longer true): one to three lines of *why* (loop never faults; dequeue limit 5; undecodable → delete). Add the AD-24-style one-liner only where it applies (it doesn't to this class: the Azure queue is durable).
  - [ ] 7.2 `docs/local-vs-azure-deltas.md`: add one short row/paragraph if (and only if) it fits the file's existing structure: the Azure adapter now survives storage errors and fails a job after 5 dequeues with `job-retries-exhausted`; the in-process adapter has no dequeue limit. Skip if it doesn't fit; do not restructure the doc.
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
- `src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs` — **11.2 rewrites most of this file** (conditional transitions through the lifecycle service, ownership token, removal of the "row missing → insert Processing" fallback, non-shutdown OCE → Failed). Read the post-11.2 shape and fit 3.1/4.4 into it. Today: `ProcessAsync` creates a scope, sets `JobHouseholdContext` first, runs the use case by `JobType`, catches `OperationCanceledException` (rethrown, job stays `Processing`) and `Exception` (→ `Failed`, `ErrorMessage` forwarded only for `SmartPlugImportValidationException`/`HouseholdImportValidationException`, otherwise `null`).
- `src/EnergyTracker.Application/ProcessSmartPlugImport.cs` — `ExecuteAsync` `try` → generic `catch (Exception)` calls `PersistFailedImportAsync` then `throw;` → `finally` deletes the temp file when not cancelled. `PersistFailedImportAsync` writes a `Failed` `SmartPlugImport` via `smartPlugImportRepository.AddAsync(failedImport, [], ct)`; if it throws, it replaces the original exception (AC #5). Today a missing file surfaces as `FileNotFoundException` from `File.OpenRead` (null error message, no stable code).
- `src/EnergyTracker.Application/RestoreHouseholdData.cs` — already checks `File.Exists` first and throws `HouseholdImportValidationException` with an English sentence; that text would be forwarded verbatim to the client (violates the AD-6 "stable error codes, not English text" rule).
- `src/EnergyTracker.Api/Program.cs` ~line 416–453 — `JobQueue:Provider` switch (read once, AD-6). The `QueueClient` is a singleton built with `QueueClientOptions { MessageEncoding = Base64 }`.
- Tests: `tests/EnergyTracker.Infrastructure.Tests/AzureStorageQueueJobProcessingServiceTests.cs` (one test; `Substitute.For<QueueClient>()`, passes `null!` as processor when no message is delivered), `BackgroundJobProcessorTests.cs` (Postgres Testcontainer, `BuildServices`, `FixedHouseholdAccessor`), `tests/EnergyTracker.Application.Tests/ProcessSmartPlugImportTests.cs` (NSubstitute ports, `NullLogger`), `RestoreHouseholdDataTests.cs`.

### What must be preserved (regression guard)

- `MessageVisibilityTimeout` stays 60 min and the test that pins it stays green. Lowering it recreates the 2026-09-01 incident (~2.7M log lines in 16 minutes, Log Analytics daily cap).
- A message is deleted only after the processor returned normally, or after a deliberate terminal decision (retries exhausted, unparseable, already terminal). Never delete on an unexpected exception.
- Shutdown cancellation: the job stays `Processing`, the message stays undeleted (redelivered to the next instance). Only `OperationCanceledException` with `stoppingToken.IsCancellationRequested` is "shutdown".
- The processor, not the consumer, owns job status. The consumer never touches `BackgroundJobs`.
- The enqueue path, `JobMessage` shape, and `InProcessChannelJobQueue`/its service are untouched. No startup sweep on the Azure adapter (AD-6: the queue is durable).
- `ProcessSmartPlugImport` must keep writing the `Failed` `SmartPlugImport` row for ordinary failures (job-history list depends on it) and must keep deleting the temp file in `finally`.

### Design decisions already made (do not re-litigate)

- **Backoff** 5 s → ×2 → 5 min cap, reset on a clean receive. Cap keeps a long storage outage to ≤ 12 error lines/hour, which matters because stdout logging shares the Log Analytics daily cap that was hit on 2026-09-01.
- **Dequeue limit** = fail on the 6th delivery (`DequeueCount > 5`), per the epic's "exceeds 5". AD-6's prose says "after 5 dequeues"; the AC governs. First delivery has `DequeueCount == 1`.
- **Undecodable/unparseable message** → delete immediately (AC #6). Our own adapter always writes valid Base64 JSON, so this only fires for foreign producers or corruption.
- **Retries-exhausted fail is token-less** (no `StartedAtUtc`) and limited to `Queued`/`Processing`.
- **Failed `SmartPlugImport` row is still written for `upload-missing`**, so the member sees the file in job history.
- **Epic 10 retro note (for 11.2's create-story check, confirmed here):** the member-visible job list (`ListSmartPlugImportJobs`) is filtered to `JobType == ProcessSmartPlugImport`, so `CorrelateEvent`, restore and cleanup rows never appear in it; they are only reachable by id through `GET /api/jobs/{id}`. `CorrelateEvent` messages go through the same dequeue limit and `RequeueEventCorrelations` can enqueue several per Meter Reading write; one-at-a-time receive serialises them. No extra handling here.

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
- **Epic 10 retro:** 10.2's uncapped `CorrelateEvent` rows interact with 11.2/11.3 (see Design decisions). 11.2's startup sweep and stale recovery don't apply to the Azure adapter's `Queued` rows: for those, **this story's dequeue limit is the only terminal path** besides a successful run, which is why AC #3 must fail from `Queued` too.
- **Git (last 5):** `bd10ba0` Story 11.1, `794ffe1` Epic 10 retro doc, `f3089ee` Epic 11 planning (AD-24–27), `6212bdc` dev-up/dev-down scripts, then Dependabot bumps. Nothing in recent history touches the queue adapter; the file's last real change is `6c1ba91`.

### References

- [Source: _bmad-artifacts/planning/epics/epic-11-codebase-audit-hardening.md#Story 11.3]
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-6, #AD-24, #AD-3]
- [Source: docs/codebase-audit-2026-10-10.md#C7, #C8, #C3]
- [Source: _bmad-artifacts/planning/sprint-change-proposal-2026-10-10.md]
- [Source: _bmad-artifacts/implementation/epic-10-retro-2026-10-10.md]
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
