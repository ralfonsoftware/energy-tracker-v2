# Story 11.17: TimeProvider and Recompute-Lock Eviction

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- Epic 11 "Codebase audit hardening" (planning/epics/epic-11-codebase-audit-hardening.md), tier Later. Origin: codebase audit 2026-10-10 (docs/codebase-audit-2026-10-10.md) findings C6 (direct `DateTimeOffset.UtcNow`, no `TimeProvider`) and C5 (`HouseholdRecomputeLock` never evicts). -->
<!-- Branch already exists: feature/11-17-timeprovider-recompute-lock-eviction (current checkout, clean, at 2e19e9c). ONE PR: backend only. No migration, no new package (FakeTimeProvider is excluded by project rule), no frontend change, no workflow change, no new AD. -->
<!-- No Ask First in this story. The Auth0/Chrome live gate does not apply (no auth-flow change), but invite accept/revoke and session paths read the clock: run the existing Playwright suite once at the end as a smoke check. -->

## Story

As a maintainer,
I want time-dependent logic testable without the real clock, and process-local locks that don't grow forever,
So that expiry and window logic is covered by deterministic tests (audit C6, C5).

## Acceptance Criteria

1. **Given** the ~47 direct `DateTimeOffset.UtcNow` calls (the real count at `2e19e9c` is **55 code sites in 38 files** under `src`, listed in "Current state"), **when** this story is done, **then** Application and Infrastructure code use an injected `TimeProvider` (`GetUtcNow()`), and the Api endpoint that still reads the clock (`HouseholdInviteEndpoints.cs:95`) takes it from DI as well. **Domain calculations receive "now" as a parameter** (Domain has no clock read today; keep it that way, and do not add a `TimeProvider` dependency to Domain).
2. **Given** the conversion, **when** it is reviewed, **then** it is **behaviour-neutral in production**: `TimeProvider.System` is registered once (`Program.cs:196`, already there), no `now` is captured earlier or later than before (see "Do not change the timing semantics" in Dev Notes), and no public HTTP contract, DB shape or JSON changes.
3. **Given** the composition root, **when** the architecture test runs, **then** a new guard `tests/EnergyTracker.Architecture.Tests/NoDirectClockReadsTests.cs` **fails on any direct clock read in `src`** (`DateTimeOffset.UtcNow`, `DateTimeOffset.Now`, `DateTime.UtcNow`, `DateTime.Now`, `DateTime.Today`; comments stripped) **outside `src/EnergyTracker.Api/Program.cs`** (the composition root; `Program.cs` has none today, the exemption is the epic's wording and costs nothing), excluding `Migrations/` and `bin/obj`. It also bans `TimeProvider.System` outside `Program.cs` (a use case that news up the system clock is the same hole). The guard proves itself non-vacuous the way `HouseholdImportIsStreamedTests` does (it detects known-bad text and ignores comments) and asserts it scanned more than 100 files.
4. **Given** `HouseholdRecomputeLock`, **when** a Household's lock is idle (not held and nobody waiting), **then** its dictionary entry is **evicted**; while it is held **or** while another caller is waiting for it, the entry is **never** removed, so mutual exclusion per Household is exactly what it was (AD-7). The file keeps a one-line `AD-24` pointer comment, and the old "No eviction/cleanup ... accepted tradeoff" paragraph is replaced by a short description of the eviction rule (also pointing to AD-24).
5. **Implied (the eviction must be race-free, not just "remove after release"):** the naive form (`TryRemove` after `Release()`) lets a caller who already fetched the old `SemaphoreSlim` and a later caller who creates a fresh one both hold "the lock" for the same Household. Eviction therefore uses a **reference count on the entry** (holders plus waiters), changed only under one `lock`, so an entry is removed only when the count reaches 0 and no one can still be holding a reference to its semaphore. The acquisition **timeout** path and the **caller-cancellation** path must also release their reference (a leak there would keep the entry forever, the exact thing C5 is about).
6. **Implied (tests must prove the conversion, not just compile it):** the production code that now takes the clock gets **at least one deterministic boundary test per cluster** against a controllable clock (see Task 6), and every existing test keeps passing. Where a test constructor changes only because a `TimeProvider` parameter was added, the edit is the minimal one (`TimeProvider.System`, or the shared `ManualTimeProvider` where the test already reasons about time).
7. **Implied (docs and project memory, the Story 11.1-11.16 practice):** `_bmad-artifacts/project-context.md` gets one rule ("time comes from an injected `TimeProvider`; direct clock reads are banned in `src` outside `Program.cs` by `NoDirectClockReadsTests`; Domain takes `now` as a parameter; tests use `ManualTimeProvider`, not a `FakeTimeProvider` package") and its "Last Updated" bump; `docs/codebase-audit-2026-10-10.md` is **not** edited (it is a dated record); the C5/C6 lines in `deferred-work.md` (if any open ones exist, grep `UtcNow` and `recompute lock`) are closed with a pointer to this story. `docs/self-hosting.md` needs **no** change (the lock stays process-local; only its memory behaviour changed).

## Tasks / Subtasks

Write each test red first and record the red run in Debug Log References (Story 11.1-11.16 practice). One PR, branch `feature/11-17-timeprovider-recompute-lock-eviction`. Baseline at Story 11.16's close: .NET about 1560+ across the five test projects (Domain/Application/Infrastructure/Api/Architecture), Vitest about 660+, Playwright about 99; **re-run `dotnet test` for each project and record the actual numbers in Task 0** rather than trusting these.

- [ ] Task 0: Baseline, before any production change
  - [ ] 0.1 Record baselines: `dotnet build EnergyTracker.sln` (warnings count), `dotnet test` per test project (Docker must be running: the Infrastructure and Api suites use Testcontainers).
  - [ ] 0.2 Re-read "Current state" below and confirm line numbers against HEAD. `git grep -nE "DateTimeOffset\.(UtcNow|Now)|DateTime\.(UtcNow|Now|Today)" -- src ':!src/**/Migrations/**'` must list exactly the 55 sites below (the 2 `SessionLifetime.cs`/`Program.cs` hits for `clock.GetUtcNow()` are already converted and are not in that list; the `HouseholdExportEndpoints.cs:57` hit is a comment).
  - [ ] 0.3 Reproduce C5 first as a failing test (Task 1.1 (a)): acquire and release for 1000 distinct Household ids, assert the tracked entry count is 0. It cannot compile until the test seam in 1.2 exists; write it against `internal int TrackedHouseholdCount` and let it fail to compile, then fail red.
- [ ] Task 1: `HouseholdRecomputeLock` eviction (AC: #4, #5)
  - [ ] 1.1 Red first, `tests/EnergyTracker.Infrastructure.Tests/HouseholdRecomputeLockTests.cs` (new tests; the five existing ones stay **unedited** and green): (a) 1000 distinct Households acquire and release, `TrackedHouseholdCount` is 0 afterwards; (b) while held, the count is 1, after dispose 0; (c) **waiter keeps the entry**: A holds, B waits, A releases, B acquires (count still 1, B really ran after A), B releases, count 0; (d) **timeout path**: holder keeps the lock, a second acquire times out with `TimeoutException`, count is still 1 (holder), and 0 after the holder releases (the failed waiter's reference was returned); (e) **cancellation path**: same with a pre-cancelled token and with a token cancelled mid-wait; (f) **double dispose** of a handle neither releases twice nor decrements twice (count never negative, a third acquirer still blocks behind the real holder); (g) **churn race**: 64 tasks x 200 iterations on the same 3 Household ids acquire, bump a per-Household "concurrent holders" counter, assert it is never above 1, `await Task.Yield()`, decrement, dispose; finally `TrackedHouseholdCount == 0`. This is the test that fails with the naive `TryRemove` eviction; make sure it really fails against a deliberately naive version before you trust it (record that in Debug Log).
  - [ ] 1.2 `src/EnergyTracker.Infrastructure/Adapters/HouseholdRecomputeLock.cs`: replace the `ConcurrentDictionary<Guid, SemaphoreSlim>` with a `Dictionary<Guid, Entry>` guarded by one private `lock` object, where `Entry` is a private sealed class `{ SemaphoreSlim Semaphore; int References; }`. `AcquireAsync`: under the lock `GetOrAdd` the entry and `References++`; **outside** the lock `await semaphore.WaitAsync(timeoutCts.Token)`; on `OperationCanceledException`/any exception from the wait, `Return(householdId, entry)` (under the lock `References--`, remove from the dictionary at 0) before rethrowing/translating to `TimeoutException`. `Releaser.DisposeAsync` (keep the `Interlocked.Exchange` double-dispose guard): `semaphore.Release()` first, then `Return(...)`. Never hold the `lock` across an `await`. Add `internal int TrackedHouseholdCount` (read under the lock) for the tests; `InternalsVisibleTo("EnergyTracker.Infrastructure.Tests")` already exists. Keep the `acquisitionTimeout` constructor parameter and the 10 s default untouched.
  - [ ] 1.3 Comments: keep one `AD-24` line at the top (the guard needs it and the `SemaphoreSlim` pattern must remain in this file), replace the "No eviction/cleanup" paragraph with two lines of *why* (entries are reference-counted and removed when idle; bounded by active Households, never grows with history). Remove nothing else.
  - [ ] 1.4 Run `SingleInstanceInvariantIsDocumentedTests` (Architecture): it must stay green unedited. If it fails because `ConcurrentDictionary<` left the file, that is fine as long as `SemaphoreSlim` keeps the allow-listed pattern hit; if it fails for any other reason, fix the code, not the guard.
- [ ] Task 2: Test seam for the clock (AC: #6)
  - [ ] 2.1 `tests/Shared/ManualTimeProvider.cs` (new): `sealed class ManualTimeProvider : TimeProvider` with a settable/advanceable `UtcNow` (`GetUtcNow()` override, `Advance(TimeSpan)`, `Set(DateTimeOffset)`), nothing else. **No `Microsoft.Extensions.TimeProvider.Testing` package** (project rule; see `deferred-work.md` line ~561 and the Story 11.13 review). Link it like `SharedDatabaseServers.cs` into `EnergyTracker.Application.Tests`, `EnergyTracker.Infrastructure.Tests` and `EnergyTracker.Api.Tests` csproj files (`<Compile Include="..\Shared\ManualTimeProvider.cs" Link="ManualTimeProvider.cs" />`; Application.Tests does not link Shared today, add the item group there). Existing tests that use `Substitute.For<TimeProvider>()` (`RecordInstanceHeartbeatTests`, `SessionLifetimeTests`) stay as they are.
- [ ] Task 3: Convert the Application layer (AC: #1, #2)
  - [ ] 3.1 Add `TimeProvider timeProvider` to the primary constructor of each Application class in the list below and replace each `DateTimeOffset.UtcNow` with `timeProvider.GetUtcNow()`. Follow `RecordInstanceHeartbeat` (primary constructor, `timeProvider.GetUtcNow()`), the one existing example.
  - [ ] 3.2 Where a method reads the clock more than once in one operation today (`CreateMeterReading` 46/69/94, `ProcessSmartPlugImport` 130-131, 265-266, `CompleteSmartPlugImportProcessing`), do **not** merge or split reads. Keep the exact number and order of reads, so behaviour is unchanged (a single local `var now` is only allowed where the code already used a single local `now`).
  - [ ] 3.3 `static`/non-injectable spots: `ListSmartPlugImportJobs.cs:46` and `CleanUpSmartPlugImportJobs.cs:19` share `ListSmartPlugImportJobs.RetentionWindow` (a static); keep the static `TimeSpan`, change only the clock read. `GetTariffHistory.cs:51`, `GetTariffCheckReminder.cs:33`, `GetCurrentStatus.cs:91`: same, clock read only. `GetCurrentStatus.ExecuteAsync` stays `virtual` (other use cases' tests mock it).
  - [ ] 3.4 Fix the compile fallout in tests (the compiler lists every `new UseCase(...)`; about 60-100 sites): add `TimeProvider.System`, or `ManualTimeProvider` where Task 6 needs it. Do not rewrite test bodies.
- [ ] Task 4: Convert the Infrastructure layer and the Api endpoint (AC: #1, #2)
  - [ ] 4.1 Adapters with a clock read (list below): `BackgroundJobLifecycle` (7 methods; it is `AddScoped`, takes `EnergyTrackerDbContext` + `JobLifecycleTimings`, add `TimeProvider` as a third parameter), `BackgroundJobEnqueueRecorder` (singleton; add the parameter next to `IServiceScopeFactory`), `HouseholdRepository.cs:154`, `MeterReadingRepository.cs:25`, `AuditCorrectionRecorder.cs:21`, `TariffRepository.cs:42`, `StatusRecomputeService.cs:66`, `MemoryHouseholdImportUploadRegistry` (two reads, 47 and 120; it has a public parameterless ctor and an `internal` `(TimeSpan ttl)` ctor that tests use: AC #3 bans `TimeProvider.System` outside `Program.cs`, so the parameterless ctor cannot default to it: give both constructors a `TimeProvider` parameter, let DI supply it, and update the tests' `new MemoryHouseholdImportUploadRegistry(...)` calls with `TimeProvider.System` or `ManualTimeProvider`; tests are not scanned), `InProcessChannelJobProcessingService` (`_processStartedAtUtc` field initializer at line 39 becomes a constructor-body assignment from the injected provider; same moment in time, still captured at construction, before the host serves a request).
  - [ ] 4.2 `src/EnergyTracker.Api/Endpoints/HouseholdInviteEndpoints.cs:95`: add `TimeProvider timeProvider` as an endpoint-delegate parameter (DI-resolved, `TimeProvider` is registered as a singleton) and use `timeProvider.GetUtcNow()`. The accept endpoint must behave identically at the `ExpiresAtUtc <= now` boundary (strictly `<=`).
  - [ ] 4.3 `Program.cs`: no new registrations are needed (every converted class is already container-built and `TimeProvider.System` is registered at line 196 *before* them). Confirm `ILogger`/DI resolution by running the Api test suite; if any converted class is constructed by hand in `Program.cs` (e.g. `new X(...)` in a `AddSingleton(sp => ...)` factory), pass `sp.GetRequiredService<TimeProvider>()` there.
  - [ ] 4.4 `HouseholdExportEndpoints.cs:57` is a comment about "a fresh DateTimeOffset.UtcNow call"; reword it to "a fresh clock read" so a plain grep of the repo for `UtcNow` stops matching it (cosmetic; the guard strips comments anyway).
- [ ] Task 5: The architecture guard (AC: #3)
  - [ ] 5.1 Red first: write `NoDirectClockReadsTests.cs` **before** Tasks 3-4 land (or on a stash of them) and record that it lists the 55 sites by file and line. Pattern: copy the helpers (`FindRepoRoot`, `StripComments`, text scan of `src/**/*.cs`) from `HouseholdImportIsStreamedTests`; Architecture.Tests references only Application and Domain, the scan is by text on purpose. Tests: (a) no forbidden token in any `src` file outside the allow list (failure message names file and token and says "inject TimeProvider; Domain takes now as a parameter"); (b) the allow list is exactly `src/EnergyTracker.Api/Program.cs` for `TimeProvider.System` and for the five clock tokens; (c) non-vacuous: the scan flags `var x = DateTimeOffset.UtcNow;`, `DateTime.Now`, `TimeProvider.System` and ignores them inside `//`, `/* */` and (not needed) leaves `timeProvider.GetUtcNow()` alone; (d) it scanned more than 100 files; (e) `Migrations`, `bin` and `obj` paths are excluded the same way `IsBuildOrMigrationPath` does in `SingleInstanceInvariantIsDocumentedTests` (copy the helper, do not reference the other test class).
  - [ ] 5.2 Confirm the guard is green only after Tasks 3 and 4, and that reintroducing one `DateTimeOffset.UtcNow` in any Application file turns it red (do this once by hand and record it).
- [ ] Task 6: Deterministic tests for the converted logic (AC: #6). Write red first where the old code could not be tested without the real clock.
  - [ ] 6.1 **Invites** (`AcceptHouseholdInvite`, `CreateHouseholdInvite`, the endpoint): an invite expires at exactly `ExpiresAtUtc` (`<=`): accepted one tick before, `409` at the instant and after; created invite's `ExpiresAtUtc == now + InviteLifetime` for the manual clock. Use the existing test files for these classes.
  - [ ] 6.2 **Upload registry TTL** (`MemoryHouseholdImportUploadRegistryTests`): replace the real-time wait in the existing 50 ms TTL test with a `ManualTimeProvider` advance **as an added test** (leave the old one if it is not flaky; if you remove a real-time wait, say so in the Debug Log). Cover: entry valid at `ttl - 1 tick`, gone at `ttl + 1 tick`, `Register` sweeps expired entries and returns their temp files for deletion.
  - [ ] 6.3 **Job lifecycle** (`BackgroundJobLifecycleTests` or the nearest existing file): `TryStartAsync` treats a `Processing` row as stale exactly when its heartbeat is older than `StaleAfter`: restartable at `StaleAfter + 1 tick`, not at `StaleAfter - 1 tick`, with the manual clock. This is the "window logic" the epic names.
  - [ ] 6.4 **Future-clock-skew windows**: `CreateMeterReading` and `CreateEvent` reject `timestamp > now + MaxFutureClockSkew` and accept `== now + MaxFutureClockSkew`; `RequeueEventCorrelations.ExecuteFromAsync` passes `now + MaxFutureClockSkew` as the range end (assert the argument the mocked `ExecuteAsync`/event repository receives).
  - [ ] 6.5 **Status**: `GetCurrentStatus` uses the injected "now" for pace/elapsed (a manual clock moved by a day changes `ElapsedDays` by 1, nothing else); `StatusRecomputeService` stamps `ComputedAtUtc` with the injected clock.
  - [ ] 6.6 Keep every added test free of `Task.Delay` and real timers (the whole point of C6). If a test needs to "wait", advance `ManualTimeProvider`.
- [ ] Task 7: Docs and memory (AC: #7)
  - [ ] 7.1 `_bmad-artifacts/project-context.md`: add the clock rule under the existing backend rules, bump "Last Updated", update `rule_count` if the file tracks it.
  - [ ] 7.2 `_bmad-artifacts/implementation/deferred-work.md`: close any open item about `UtcNow`/`TimeProvider` or recompute-lock growth (grep first; do not invent entries). The open `InstanceHeartbeatServiceTests` real-timer item (line ~561) stays open: that is `Task.Delay`-based service timing, not clock reads, and still has no package-free fix.
  - [ ] 7.3 `sprint-status.yaml`: `11-17-...` moves ready-for-dev -> in-progress -> review as the BMAD flow says (dev-story does this).
- [ ] Task 8: Verification (all ACs)
  - [ ] 8.1 `dotnet build EnergyTracker.sln` with **no new warnings**; `dotnet test` for all five test projects; compare counts with Task 0 (they only go up).
  - [ ] 8.2 `git grep -nE "DateTimeOffset\.(UtcNow|Now)|DateTime\.(UtcNow|Now|Today)" -- src ':!src/**/Migrations/**'` prints nothing (comment at `HouseholdExportEndpoints.cs` reworded).
  - [ ] 8.3 Playwright once (`npm --prefix web run build`, then the suite) as a smoke check of invite accept/revoke, readings and status, since they all read the clock now through DI.
  - [ ] 8.4 Both providers: the Infrastructure and Api suites already run Postgres and SQL Server via Testcontainers; both must be green (the lifecycle and repository clock reads feed `ExecuteUpdateAsync`/inserts on each).

## Dev Notes

### Why it exists

Audit C6: ~47 direct `DateTimeOffset.UtcNow` calls and no `TimeProvider`, so invite expiry, upload TTLs, job staleness and the clock-skew windows can only be tested against the real clock (hence the 50 ms-TTL style tests and sleeps). Audit C5: `HouseholdRecomputeLock`'s per-Household `SemaphoreSlim` dictionary never evicts. Both Low, both verified. The first piece of the fix already exists and is the pattern to copy: Story 11.8 registered `TimeProvider.System` (`Program.cs:196`, `TryAddSingleton`), and Stories 11.8/11.13 use it in `SessionLifetime`, `RecordInstanceHeartbeat`, `InstanceHeartbeatService` and `AppInstanceIdentity`. Stories 11.2, 11.3, 11.9, 11.10, 11.13, 11.14 and 11.15 each wrote "do not pre-empt, Story 11.17 converts all of it together" and left `UtcNow` in place on purpose; this is that conversion.

### Current state of each file you will touch (HEAD `2e19e9c`)

Clock reads to convert (`file:line`, all `DateTimeOffset.UtcNow`):

- **Application (30 files):** `RequeueEventCorrelations.cs:27`; `CreateDevice:33`, `CreatePowerPoint:33`, `CreateRoom:24`, `CreateTariff:43`, `EditTariff:109`, `CreateEvent:48,79`, `CreateMeterReading:46,69,94`, `CreateHousehold:43`, `CreateHouseholdInvite:19`, `AcceptHouseholdInvite:29,49`, `ArchiveDevice:21`, `ArchivePowerPoint:24`, `ArchiveRoom:29`, `RevokeHouseholdInvite:9`, `RemoveHouseholdMember:14`, `ListOpenHouseholdInvites:11`, `ResolveMeterRegressionPrompt:55`, `ExportHouseholdData:17`, `GetCurrentStatus:91`, `GetTariffHistory:51`, `GetTariffCheckReminder:33`, `CorrelateEvent:121`, `MapSmartPlugImportToPowerPoint:51`, `CompleteSmartPlugImportProcessing:73`, `ProcessSmartPlugImport:130,131,265,266,277,302`, `CleanUpSmartPlugImportJobs:19`, `ListSmartPlugImportJobs:46`.
- **Infrastructure/Adapters (10 files):** `MemoryHouseholdImportUploadRegistry:47,120`, `HouseholdRepository:154`, `MeterReadingRepository:25`, `AuditCorrectionRecorder:21`, `InProcessChannelJobQueue:39` (inside `InProcessChannelJobProcessingService`), `StatusRecomputeService:66`, `BackgroundJobLifecycle:22,40,49,60,72,84,102,119` (the last, `NewToken()` at 119, is a **static** helper: make it an instance method that reads `timeProvider`), `BackgroundJobEnqueueRecorder:32`, `TariffRepository:42`.
- **Api (1 file):** `Endpoints/HouseholdInviteEndpoints.cs:95`.
- **Domain:** none. `SmartPlugGapDetector.DetectGaps(..., DateTimeOffset now)` already takes "now" as a parameter (`CompleteSmartPlugImportProcessing:73` is its caller); that is the model for the epic's "Domain calculations receive now as a parameter".
- **Already converted (do not touch):** `RecordInstanceHeartbeat`, `InstanceHeartbeatService`, `SessionLifetime`, `Program.cs:471` (`AppInstanceIdentity`).

Preserve exactly:

- **The semantics of `now`.** `BackgroundJobLifecycle.TryStartAsync` computes `staleBefore = now - StaleAfter` and uses `token` (a `DateTimeOffset` returned by `NewToken()`) as the ownership token for later heartbeat/complete/fail. `NewToken()` (static, line 117) is the clock read for that token: it truncates "now" to whole milliseconds (portable across both providers' datetime precision). Keep the truncation exactly; the token is an ownership identity, so a test with a manual clock must advance it between two starts, or two jobs get an equal token. Cover the millisecond truncation with one test.
- **`AcceptHouseholdInvite`'s boundary** (`ExpiresAtUtc <= now` means expired), `CreateMeterReading`/`CreateEvent`'s skew windows (`> now + MaxFutureClockSkew` rejected), `RetentionWindow` arithmetic. These are the behaviours Task 6 pins.
- **`HouseholdExportEndpoints`** derives the file name's date from `export.ExportedAtUtc`, deliberately not from a fresh read. The export use case reads the clock once; keep it once.
- **`GetCurrentStatus`** stays `virtual`; the bounded fetch, the must-include anchor, and the Smart Plug exclusion (AD-14) are untouched (Story 11.15's note: "`DateTimeOffset.UtcNow` ... Story 11.17 converts it; do not pre-empt" - this is where it converts).

### `HouseholdRecomputeLock` design (AC #4, #5)

Today: `ConcurrentDictionary<Guid, SemaphoreSlim>`, `GetOrAdd`, `WaitAsync` with a linked 10 s timeout, a `Releaser` that `Release()`s once. Required end state:

```csharp
private readonly object _gate = new();
private readonly Dictionary<Guid, Entry> _entries = [];
private sealed class Entry { public readonly SemaphoreSlim Semaphore = new(1, 1); public int References; }

// Acquire:  lock(_gate){ get-or-add entry; entry.References++; }   (never await inside the lock)
//           try { await entry.Semaphore.WaitAsync(timeoutCts.Token); }
//           catch (...) { Return(householdId, entry); throw / translate to TimeoutException; }
// Release:  entry.Semaphore.Release(); Return(householdId, entry);
// Return:   lock(_gate){ if (--entry.References == 0) _entries.Remove(householdId); }
```

Why a reference count and not "remove when `CurrentCount == 1`": `CurrentCount` is racy against a caller who has fetched the entry but not yet called `WaitAsync`. The count is incremented under the same lock that removes, so "count 0" means no one can reach the semaphore any more. `SemaphoreSlim` without `AvailableWaitHandle` needs no `Dispose`; do not dispose it (a late `Release` on a disposed one throws). A waiter that is cancelled **after** it was granted the semaphore but before `Acquire` returns would leak both the semaphore and the reference: `WaitAsync` either completes (granted) or throws (not granted), so the `catch` only runs when not granted; keep the `try` narrow around the `WaitAsync` and put nothing between it and `return new Releaser(...)` that can throw.

`HouseholdRecomputeLock` stays a singleton registered in `Program.cs:357` (`IHouseholdRecomputeLock`); `StatusRecomputeService` and the `IHouseholdRecomputeLock` port are untouched. AD-24 is unchanged: the lock is still process-local, and the guard `SingleInstanceInvariantIsDocumentedTests` still lists this file.

### Do not change the timing semantics

- **Singletons capture "now" once on purpose**: `InProcessChannelJobProcessingService._processStartedAtUtc` must be assigned at construction, not read lazily when the sweep runs (a row enqueued by this process must have `CreatedAtUtc >= this`).
- **Scoped vs singleton**: `TimeProvider` is a singleton, so any lifetime can take it. Do not add `IServiceProvider` lookups for it.
- **`DateTimeOffset.UtcNow` vs `GetUtcNow()`**: same value and type (`UtcNow` is `DateTime`-backed under the hood; `GetUtcNow()` on `TimeProvider.System` is `DateTimeOffset.UtcNow`), so no offset or precision change. Do not use `GetLocalNow()` or `TimeProvider.GetTimestamp()`.
- **`Stopwatch`, `Task.Delay`, `PeriodicTimer`, `CancelAfter`** are not clock reads in the audit's sense. Leave them (the lock's acquisition timeout stays `CancelAfter`). Converting timers to `TimeProvider` (`TimeProvider.CreateTimer`, `Task.Delay(..., timeProvider)`) is out of scope.

### Testing standards (this repo)

- xunit.v3 (MTP), Shouldly, NSubstitute. Names are sentences (`Acquire_throws_TimeoutException_when_the_configured_wait_is_exceeded`); `TestContext.Current.CancellationToken` for tokens (the 11.18 story will otherwise count `xUnit1051` warnings: do not add new ones).
- Infrastructure and Api tests hit real Postgres and SQL Server containers (`SharedDatabaseServers`); do not mock `DbContext`.
- `ManualTimeProvider` replaces both `FakeTimeProvider` (banned: no extra package) and per-test `Substitute.For<TimeProvider>()` for new tests; the two existing substitute-based tests stay.
- Architecture guards read source as text, strip comments, and prove themselves non-vacuous; follow `HouseholdImportIsStreamedTests` and `SingleInstanceInvariantIsDocumentedTests` for shape and failure-message style.

### Project Structure Notes

- New files: `tests/Shared/ManualTimeProvider.cs` (linked into three test projects), `tests/EnergyTracker.Architecture.Tests/NoDirectClockReadsTests.cs`. All other work edits existing files. One top-level type per file.
- Architecture.Tests does not reference Infrastructure or Api: the guard scans `src` as text, by design.
- Namespace/layout: unchanged (Application use cases in `EnergyTracker.Application`, adapters in `EnergyTracker.Infrastructure.Adapters`).
- Conflicts/variances: the epic says "~47 calls"; the real count is 55 code sites (the audit's count included different grep scope). The AC's intent (no direct reads outside the composition root) is what the guard enforces, so the number itself is not asserted anywhere.

### References

- `_bmad-artifacts/planning/epics/epic-11-codebase-audit-hardening.md#story-1117-timeprovider-and-recompute-lock-eviction`
- `_bmad-artifacts/planning/sprint-change-proposal-2026-10-10.md` (Story 11.17, line ~578)
- `docs/codebase-audit-2026-10-10.md` (C5 line ~249, C6 line ~253)
- `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#ad-24-single-instance-invariant-explicit-documented-observed` and AD-7 (compute at read time; recompute serialization)
- `_bmad-artifacts/implementation/11-13-single-instance-invariant-documentation.md` (guard design; "Story 11.17 will add the eviction comment later")
- `_bmad-artifacts/implementation/11-8-auth-hardening-csrf-logout-session-cap.md` (task 4.2: `TimeProvider.System` registration)
- `_bmad-artifacts/implementation/11-15-trending-threshold-scaled-to-elapsed.md` (the `GetCurrentStatus` hand-off note)
- `_bmad-artifacts/project-context.md` (backend and testing rules)
- `src/EnergyTracker.Application/RecordInstanceHeartbeat.cs` and `tests/EnergyTracker.Application.Tests/RecordInstanceHeartbeatTests.cs` (the existing `TimeProvider` example)

### Previous story intelligence (11.16 and the 11.x run)

- 11.16 was frontend-only; the last backend story is 11.15 (`GetCurrentStatus`). Practice carried since 11.1: red first with the red run recorded in Debug Log, existing tests unedited, one PR per story, project-context bump, a `*Tests` architecture guard per invariant that is meant to stay true.
- Each earlier 11.x story explicitly postponed its `UtcNow` to this one, so a reviewer will check for stragglers: the guard in Task 5 is what makes that check mechanical.
- Review lessons in this epic: a "defensive" comment that overstates behaviour gets flagged (keep the lock comments factual); guard tests must be non-vacuous; do not widen scope (no `Task.Delay` -> `TimeProvider` timer conversion, no analyzer work: that is 11.18, no `Program.cs` split: that is 11.19, which will move the `TimeProvider` registration into an extension method).

### Git intelligence

Recent commits (`2e19e9c` deps bump, `4892ab1` NuGet group, `3d03add` over-quota alert every 15 min, `765631b` 11.16 result) touch dependencies, CI and frontend; none overlaps the files above. No package change is needed here, so Dependabot churn does not conflict.

### Latest technical information

`TimeProvider` is in `System` (BCL, .NET 8+), so no package. `GetUtcNow()` is `virtual` (subclassable and NSubstitute-able; that is how the existing tests do it). `Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`) exists but is deliberately **not** adopted (project rule), hence the tiny `ManualTimeProvider`. EF Core 10 `ExecuteUpdateAsync` takes the captured `now` as a closure parameter, as the existing code already does; keep capturing `var now = timeProvider.GetUtcNow();` into a local *before* the lambda (a provider call inside an expression tree would not translate).

## Dev Agent Record

### Agent Model Used

### Debug Log References

### Completion Notes List

### File List
