---
baseline_commit: b503218
---

# Story 10.2: Event Correlation Forward-Window Recompute

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

<!-- Origin: promoted from deferred-work.md during the 2026-10-02 triage (spec-deferred-work-triage.md). Epic 10 ("Deferred-work hardening") is new; its epic entry is created alongside this story. Until it exists, this file's ACs are the source of truth. -->

## Story

As a Household member who logs Events as they happen,
I want an Event's rough correlation to be (re)evaluated whenever the Meter Readings around it change,
so that an Event logged today still gets its "Roughly matches the bump/dip seen." signal once the readings that reveal the deviation actually exist, instead of being silently judged once against an empty future.

## Why this exists (the defect, precisely)

`CorrelateEvent` (Story 6.3) runs **exactly once**, seconds after `CreateEvent` enqueues it (`src/EnergyTracker.Application/CreateEvent.cs:95-108`). It fetches readings in `[OccurredAt − 7d, OccurredAt + 7d]` (`CorrelateEvent.cs:46-48`). `WindowedDeviationCalculator.ComputeDeviation` returns `null` when fewer than two readings fall in the window (`WindowedDeviationCalculator.cs:44`).

- **Real-time Event** (`OccurredAt ≈ now`): the forward half of the window is empty, so there are almost never two readings → no correlation, forever. This is the *common* case, not an edge case.
- **Backfilled reading**: a Meter Reading entered later with a `ReadingTimestamp` inside an already-evaluated Event's window never triggers anything.
- **Corrected reading** (`EditMeterReading`) and **resolved regression prompt** (`ResolveMeterRegressionPrompt`) change the same deltas and also trigger nothing.

Net effect: the Wattage Plausibility feature (FR-17) can correlate only Events that were backfilled after their readings already existed. The 6.3 code review recorded this as accepted ("derive once", mirrored from AD-10); the 2026-10-02 triage promoted it because it is the usual path, not an edge.

Related deferred entry folded in (decision below): `CorrelateEvent`/`WindowedDeviationCalculator` do not apply AD-12's open-`MeterRegressionPrompt` exclusion.

## Scope Reality Check

- **FR-17's own wording** requires a correlation against "any consumption deviation Pattern Detective observed around that time". Today that is only honored for pre-existing readings. This story closes that gap; it adds **no new FR** and **no UI change**.
- **AD-10 is not violated.** AD-10 (`invariants-rules.md#AD-10`) concerns by-value *tag* snapshots (`TaggedEntityName`). "Derive once, read back forever" for *correlation* was Story 6.3's own extension by analogy (`Event.cs` comment, `docs/data-export-format.md:201`, 6.3 Dev Notes), not an AD. This story relaxes that analogy to "latest evaluation wins". Flagged under **Ask First**.
- **AD-14 stays intact.** Reads only `MeterReading` (Main Meter) data; `SmartPlugReading` is never summed in. `GetCurrentStatus`, `StatusRecomputeService`, `IStatusRecomputeService` and the other guarded files must not gain the word "Event" (see Guard below), so the trigger lives in a **new** Application class, never inside `IStatusRecomputeService`.
- **AD-8 stays intact.** The new trigger does **not** check `Household.AiPlausibilityEnabled`. Only `CorrelateEvent` does, exactly like `CreateEvent`.
- **No schema change, no migration, no export/import change.** `Event.CorrelationDirection`/`CorrelationComputedAtUtc` keep their meaning (both null = no correlation; both set = correlation). A third column (e.g. "last observed direction") was rejected: it would require changing `ExportHouseholdData`, `HouseholdExportResult`, `RestoreHouseholdData`, `docs/data-export-format.md`, the export-stream parity concern in deferred-work.md, and their dual-provider tests, which is a second story's worth of scope.
- **Out of scope** (explicit): recompute on `RestoreHouseholdData` (restore copies the stored correlation opaquely, by design: Story 7.2); sweeping old `CorrelateEvent` `BackgroundJob` rows (the 30-day sweep only covers `ProcessSmartPlugImport` rows today, `SmartPlugImportRepository.cs:755,932`, pre-existing); any frontend change; the real AI backend (Epic 9).

## Design options considered

| | A. Event-driven re-evaluation (recommended) | B. Delayed first computation at window close | C. Lazy, read-triggered (`GET /api/events` enqueues) |
|---|---|---|---|
| Idea | On Meter Reading create / real edit / regression-prompt resolve, enqueue `CorrelateEvent` for every Event whose ±7d window contains that reading; `CorrelateEvent` re-evaluates idempotently | Enqueue the one job with a delay until `OccurredAt + 7d` | `GET /api/events` enqueues jobs for Events whose correlation is missing or stale |
| Real-time Event | Evaluated as soon as the second in-window reading arrives | Nothing for 7 days, then once | Nothing until someone opens the card after day 7 |
| Backfill / edit / prompt resolve | Handled (that is the trigger) | Not handled; window already closed for backfilled Events | Only if staleness can be detected |
| Fits the infra | Uses the existing `IBackgroundJobQueue` and `CorrelateEvent`; no new infra | `IBackgroundJobQueue` has no delayed enqueue and `InProcessChannelJobQueue` can't delay; an in-process timer is what AD-7 forbids (scale-to-zero) | GET with side effects. With only two columns, "evaluated, no match" is indistinguishable from "never evaluated" (both null), so every GET would re-enqueue forever |
| AI cost | At most one call per *change of observed direction* (guard below); typically 1 per Event | 1 per Event | 1 per Event if detectable, else unbounded |
| AD-10 "derive once" analogy | Relaxed to "latest evaluation wins" | Kept | Relaxed |
| Single worker (AD-6) | A few cheap DB-only jobs per reading save; AI calls only on direction change | 1 job per Event | Bursts of jobs on page load |
| UI | None. Correlation may appear or later disappear as readings arrive | None | None |

**Recommendation: A.** It is the only option that fixes all four triggers (real-time, backfill, edit, prompt resolve) with existing infrastructure and no schema change. B cannot be built without breaking AD-7 or extending the queue port. C cannot be made correct without a schema change.

## Acceptance Criteria

1. **Given** an AI-enabled Household with a Yearly Baseline and an Event logged in real time (fewer than two Main-Meter readings in its ±7-day window, so no correlation yet), **when** a Meter Reading is created whose `ReadingTimestamp` falls inside that window and the window now holds two or more readings with a deviation beyond the prorated `TrendingThresholdKwh`, **then** a `CorrelateEvent` job is enqueued for that Event, and after it runs the Events card shows the Bump/Dip correlation per the AI classification (FR-17, AC #1/#2 of Story 6.3).
2. **Given** an Event whose window already closed, **when** a Meter Reading is backfilled with a `ReadingTimestamp` inside that window (regardless of when it is entered), **then** the Event is re-evaluated; selection is by `ReadingTimestamp`, never by `CreatedAtUtc`/entry order (AD-12 ordering principle).
3. **Given** a Meter Reading edit that changes the kWh value, **when** the edit commits, **then** Events whose window contains that reading's `ReadingTimestamp` are re-evaluated; **and given** a no-op edit (same value, `EditMeterReading.cs:32`), **then** nothing is enqueued.
4. **Given** a Meter Reading whose `ReadingTimestamp` is more than 7 days from every Event (or the Household has no Events), **when** it is created, **then** no job is enqueued. Window membership is inclusive at both ends (`|Event.OccurredAt − T| ≤ 7 days`).
5. **Given** the trigger path, **when** it runs for a Household with `AiPlausibilityEnabled = false` or no configured backend, **then** it still enqueues (no AI check outside `CorrelateEvent`, AD-8), and `CorrelateEvent` no-ops exactly as it does today; the persisted correlation of such an Event is left untouched.
6. **Given** `CorrelateEvent` re-runs for an Event, **when** the newly observed deviation direction equals the persisted `CorrelationDirection`, **then** it makes **zero** `IAiPlausibilityClient` calls and writes nothing; **when** the observed direction is non-null and differs from the persisted one (including persisted null), **then** it makes exactly one call and persists the result; **when** no deviation is observed now, **then** it makes zero calls and clears both columns to null (latest evaluation wins, Story 6.3 AC #3: shown without a correlation, never flagged as wrong).
7. **Given** an open `MeterRegressionPrompt` (AD-12), **when** `CorrelateEvent` evaluates an Event, **then** the prompt's triggering reading and every reading chronologically at or after it (by `ReadingTimestamp`, then `Id`) are excluded from the window before the deviation is computed, including when the triggering reading lies outside the window (then every in-window reading at/after it is excluded and the result is "no deviation"). This closes the deferred AD-12 entry.
8. **Given** an open prompt is resolved as Reset or Rollover, **when** `ResolveMeterRegressionPrompt` succeeds, **then** Events with `OccurredAt ≥ triggeringReading.ReadingTimestamp − 7 days` are re-evaluated, so the now-corrected (or voided) pair is reflected.
9. **Given** the requeue step throws (queue unavailable, DB blip), **when** it runs after a Meter Reading create/edit or a prompt resolve, **then** the already-committed write still succeeds, the failure is logged as a warning, and nothing propagates (same discipline as `CreateEvent.cs:95-108`). **Given** the Event no longer exists when a job runs, **then** `CorrelateEvent` returns without throwing.
10. **Given** this story ships, **when** reviewed, **then**: no migration exists; `ExportHouseholdData`/`RestoreHouseholdData`/`HouseholdExportResult` are unchanged; the Events card renders only the two fixed translated strings (UX-DR14/UX-DR17, no new states, no flicker markup); `docs/data-export-format.md` and the `Event.cs`/`CorrelateEvent`/`WindowedDeviationCalculator` comments describe "latest evaluation wins"; `PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests` passes unmodified.
11. **Given** the live-verification gate (`project-context.md`, Process gates), **when** the story moves review→done, **then** the Chrome scenario in Task 7 has actually been performed against the real Auth0 session, with a local stub AI backend so the rendered correlation text and its later disappearance are seen in a real render.

## Tasks / Subtasks

Order is test-first (red, green, refactor); each task's tests are written before its implementation. Run each test project separately (MTP rejects a combined multi-project run).

- [ ] **Task 1: Windowed open-prompt exclusion in the Domain calculator (AC: #7)**
  - [ ] 1.1 Tests first in `tests/EnergyTracker.Application.Tests/WindowedDeviationCalculatorTests.cs`: a new `WindowedDeviationCalculator.ExcludeAtOrAfter(orderedReadings, boundary)` returns only readings strictly before `(boundary.ReadingTimestamp, boundary.Id)`; boundary null → input unchanged; boundary before window start → empty; boundary inside window → prefix only; same-timestamp tiebreak by `Id` matches the repo ordering (`ReadingTimestamp` then `Id`).
  - [ ] 1.2 Implement it in `src/EnergyTracker.Domain/Calculations/WindowedDeviationCalculator.cs`. **Do not** call `PatternDetectiveCalculator.ExcludeFromOpenPrompt`: it throws `InvalidOperationException` when the triggering reading is not in the supplied sequence (`PatternDetectiveCalculator.cs:30-37`), which is the normal case for a ±7-day slice. Do not edit `PatternDetectiveCalculator.cs` at all (guarded file).
  - [ ] 1.3 Update the stale comment at `WindowedDeviationCalculator.cs:33-37` ("deliberately still deferred").

- [ ] **Task 2: Event repository support (AC: #4, #6, #9)**
  - [ ] 2.1 Tests first in `EventRepositoryTestsBase` (`tests/EnergyTracker.Infrastructure.Tests/EventRepositoryTests.cs`, runs on both `PostgresEventRepositoryTests` and `SqlServerEventRepositoryTests`): `GetByOccurredAtRangeAsync(from, to)` is inclusive at both ends, ordered by `OccurredAt` then `Id`, excludes other Households (AD-3); `FindByIdAsync` returns the Event or null; `SetCorrelationAsync(id, null, null)` clears both columns and `(direction, ts)` still sets both (existing tests at lines 312/338 stay green).
  - [ ] 2.2 Extend `src/EnergyTracker.Application/Ports/IEventRepository.cs`: add `FindByIdAsync(Guid, CancellationToken)` and `GetByOccurredAtRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken)`; change `SetCorrelationAsync`'s `computedAtUtc` to `DateTimeOffset?` so "clear" is expressible (update the port comment: both null or both set).
  - [ ] 2.3 Implement in `src/EnergyTracker.Infrastructure/Adapters/EventRepository.cs` (`AsNoTracking`, AD-3 filter applies; the existing composite index `(HouseholdId, OccurredAt, CreatedAtUtc)` already serves the range query, so no new index and no migration). Keep `ExecuteUpdateAsync` for the correlation write.

- [ ] **Task 3: Make `CorrelateEvent` an idempotent re-evaluation (AC: #5, #6, #7, #9)**
  - [ ] 3.1 Tests first in `CorrelateEventTests.cs` (existing 7 tests stay valid; its helper `SetUpWindowedReadings` stubs `GetInWindowByMainMeterAsync`): Event not found → no-op; disabled / no baseline / no Main Meter → no writes, existing correlation untouched; unchanged observed direction equal to persisted → zero AI calls, zero writes; observed Bump with persisted null → one AI call, persisted; observed Dip with persisted Bump → one call, overwritten; AI returns null with persisted Bump and observed Dip → both columns cleared; no deviation with persisted Bump → cleared, zero AI calls; open prompt whose trigger lies inside the window → trailing readings excluded; trigger before the window → no deviation → cleared/unchanged-null.
  - [ ] 3.2 Implement in `src/EnergyTracker.Application/CorrelateEvent.cs`: after the household/main-meter guards, `eventRepository.FindByIdAsync(payload.EventId)`; fetch the open prompt via `regressionPromptRepository.GetOpenForHouseholdAsync(householdId)` and its trigger via `readingRepository.FindByIdAsync(prompt.MeterReadingId)`; apply `ExcludeAtOrAfter`; compute the deviation; decision table per AC #6; write through `SetCorrelationAsync`. Preserve: early return when AI disabled/unconfigured baseline (AD-8's single check), the "never throws on AI failure" contract (the client resolves failures to null), and the plain-record payload (AD-6; payload shape does **not** change, so in-flight queued messages stay valid).
  - [ ] 3.3 Update the class XML doc (no longer "computes once").

- [ ] **Task 4: The requeue trigger and its three call sites (AC: #1-#5, #8, #9)**
  - [ ] 4.1 Tests first: `tests/EnergyTracker.Application.Tests/RequeueEventCorrelationsTests.cs` (NSubstitute for `IEventRepository`, `IBackgroundJobQueue`; `ILogger` substitute): one `JobEnvelope<CorrelateEventPayload>` (`JobTypes.CorrelateEvent`, fields from the Event) per Event in range; none → no enqueue; does **not** read `IHouseholdRepository`; an enqueue exception for one Event is logged and the others are still attempted; a repository exception is logged and swallowed. Then extend `CreateMeterReadingTests`, `EditMeterReadingTests` (real change enqueues, no-op does not), `ResolveMeterRegressionPromptTests` (success enqueues with `from = trigger.ReadingTimestamp − 7d`; a lost-race `MeterRegressionPromptNotOpenException` does not).
  - [ ] 4.2 New `src/EnergyTracker.Application/RequeueEventCorrelations.cs`: `ExecuteAsync(householdId, DateTimeOffset occurredFrom, DateTimeOffset occurredTo, ct)`; callers pass `[T − WindowRadius, T + WindowRadius]` for a reading and `[trigger − WindowRadius, UtcNow + skew]` for a resolve. Reuse `WindowedDeviationCalculator.WindowRadius`; never hardcode 7. Whole body in the try/catch pattern of `CreateEvent` (cancellation excluded).
  - [ ] 4.3 Call it after `statusRecomputeService.RecomputeAsync` in `CreateMeterReading.ExecuteAsync` (after the prompt insert, so an open prompt already excludes the new reading) and `EditMeterReading.ExecuteAsync` (real-change path only), and after a successful `ResolveAsync` in `ResolveMeterRegressionPrompt` (this class additionally needs `IMeterReadingRepository` to read the trigger's timestamp). Never inside a transaction (AD-7 placement precedent).
  - [ ] 4.4 Register `RequeueEventCorrelations` as scoped in `src/EnergyTracker.Api/Program.cs` next to `CorrelateEvent` (~line 349). Update every test that constructs `CreateMeterReading`, `EditMeterReading` or `ResolveMeterRegressionPrompt` directly for the new constructor parameter.

- [ ] **Task 5: API-level proof (AC: #1, #4)**
  - [ ] 5.1 In `tests/EnergyTracker.Api.Tests/MeterReadingEndpointsTests.cs`: POST a reading inside a seeded Event's window and assert a `CorrelateEvent` `BackgroundJob` row exists for the Household; a reading outside any window creates none. Follow how `EventEndpointsTests.cs` (~line 318) observes the job; if `EnergyTrackerApiFactory` cannot observe the queue, assert on `BackgroundJobs` rows.

- [ ] **Task 6: Docs, comments, deferred-work bookkeeping (AC: #10)**
  - [ ] 6.1 Update `src/EnergyTracker.Domain/Event.cs` comments ("Set once ... never recomputed" → "written by CorrelateEvent; re-evaluated when readings in the window change; both null or both set") and `docs/data-export-format.md:201` ("Set once by the AI correlation job, never recomputed" → latest-evaluation wording; the restore still copies it opaquely).
  - [ ] 6.2 Per the project-context deferred-work convention, delete the resolved entries: "Real-time Event has no forward-window readings" and the `CorrelateEvent`/`WindowedDeviationCalculator` AD-12 entry from `deferred-work.md`; the "Correlation computed exactly once" entry from `deferred-work-accepted.md`; drop the promoted item from the file's index. New leftovers from this story (see Known limitations) go to the end of `deferred-work.md` with `[open]`.

- [ ] **Task 7: Verification gate (AC: #10, #11)**
  - [ ] 7.1 Per-project backend runs green: Domain/Application/Infrastructure (dual-provider Testcontainers)/Api/Architecture; `PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests` explicitly re-run and unmodified. Frontend untouched, but run `npx vitest run`, `npx tsc -b`, `npx oxlint` as regression proof.
  - [ ] 7.2 **Live Auth0/Chrome verification (hard gate, does not defer).** If the Claude-in-Chrome extension is not connected, say so immediately and pause for Ralf; never tick this off from tests. Scenario: start the local stack (`scripts/run-api.sh`, Vite) with a throwaway stub OpenAI-compatible server kept in the scratchpad and **not committed** (answers `POST /v1/chat/completions` with `{"choices":[{"message":{"content":"Bump"}}]}`), `AiPlausibility__BaseUrl` pointing at it and `AiPlausibility__Model` set; sign in; set a Yearly Baseline; enable the Settings toggle; log an Event "now"; log two readings around it whose delta exceeds the prorated threshold. Observe: the Event gains "Roughly matches the bump seen." after the second in-window reading (reload Trend History); a third in-window reading that removes the deviation clears it; an AI-off Household still shows nothing and the job completes cleanly; console and network clean (`read_console_messages` with `onlyErrors`). Restore the toggle and note any inert test data left (Events are append-only).

## Dev Notes

### Ask First (decision for Ralf before dev-story starts)

1. **Confirm Option A** (event-driven re-evaluation) and with it relaxing Story 6.3's "derive once, never recompute" analogy to "latest evaluation wins". Consequence the household can see: a correlation shown today can disappear after a later reading changes the picture.
2. **Fold in** the AD-12 open-prompt exclusion and the prompt-resolve trigger (recommended: without them, a freshly entered reading that triggers a regression prompt is the very reading that fires re-evaluation, and its raw negative delta would yield a bogus "Dip"). The alternative is to exclude both and keep the deferred AD-12 entry open.
3. The trigger enqueues for AI-off Households too (AD-8 forbids a second enablement check). Cost is a handful of cheap rows per reading save, only for Events inside the window. If Ralf prefers a documented exception (the trigger reads `AiPlausibilityEnabled`), say so; the default here is no exception.

### Epic 9 interaction

Epic 9 (stories 9.1-9.4) picks and stands up the real backend; only `NoOpAiPlausibilityClient` has ever run. This story is independent of it and can ship first. The AI-cost guard (AC #6: zero calls when the observed direction already matches the persisted correlation; one call per change of direction) is what keeps the real backend's cost and the single-worker queue (AD-6, 10 s client timeout, `OpenAiCompatibleClient.cs`) from scaling with reading count. Residual: when the AI answers "None" for a persisting deviation, each later in-window reading costs one call (the persisted columns cannot distinguish "evaluated, no match" from "never evaluated"); bounded by readings per ±7-day window. Stories 9.3/9.4's live verification should include one forward-window recompute run against the real backend; note that when they are written.

### Current state of every file being touched (read before editing)

**UPDATE**
- `src/EnergyTracker.Application/CorrelateEvent.cs`: today takes `(IHouseholdRepository, IMeterReadingRepository, IMeterRegressionPromptRepository, IEventRepository, IAiPlausibilityClient)`; returns early (never writes) on disabled/no-baseline/no-meter, on no deviation, and on a null AI answer; writes only on a match via `SetCorrelationAsync(eventId, direction.ToString(), UtcNow)`; already loads resolved prompts and passes them to the calculator. Changes: load the Event, apply open-prompt exclusion, decision table, clearing. Preserve: AD-8 single enablement check, resolved-prompt correction, the `CorrelateEventPayload(EventId, HouseholdId, OccurredAt, Description)` record shape.
- `src/EnergyTracker.Domain/Calculations/WindowedDeviationCalculator.cs`: `WindowRadius = 7d`; `ComputeDeviation` needs ≥2 readings, pairwise walk with resolved Rollover/Reset correction, prorates baseline and `TrendingThresholdKwh` through `BonusDecayNormalizer.NormalizeToDate` (AD-5), strict `>`/`<` comparison. Add only `ExcludeAtOrAfter` and fix the comment; do not change the math.
- `src/EnergyTracker.Application/Ports/IEventRepository.cs` / `src/EnergyTracker.Infrastructure/Adapters/EventRepository.cs`: today `AddAsync` (with `ReloadAsync` for UTC normalization), `GetPageForHouseholdAsync`, `SetCorrelationAsync` (`ExecuteUpdateAsync`, 0-row = no-op). Add the two read methods; widen `computedAtUtc` to nullable.
- `src/EnergyTracker.Application/CreateMeterReading.cs`: the final steps are prompt insert (`:82`) then `RecomputeAsync` (`:100`); add the requeue after that, before `return`. Preserve the idempotency-key fast path, validation order and the "persisted instance, not local" comment.
- `src/EnergyTracker.Application/EditMeterReading.cs`: no-op early return at `:32`, transaction at `:41`, `RecomputeAsync` at `:59`; add the requeue after it (real-change path only).
- `src/EnergyTracker.Application/ResolveMeterRegressionPrompt.cs`: add `IMeterReadingRepository` and the requeue after the successful `ResolveAsync`; keep the one-open-at-a-time and lost-race exceptions exactly as they are.
- `src/EnergyTracker.Api/Program.cs` (~`:349`): register the new scoped class.
- `src/EnergyTracker.Domain/Event.cs`, `docs/data-export-format.md:201`: comment/doc wording only.
- Tests: `CorrelateEventTests.cs`, `WindowedDeviationCalculatorTests.cs`, `CreateMeterReadingTests.cs`, `EditMeterReadingTests.cs`, `ResolveMeterRegressionPromptTests.cs`, `EventRepositoryTests.cs` (base class), `MeterReadingEndpointsTests.cs`.
- Bookkeeping: `deferred-work.md`, `deferred-work-accepted.md`.

**NEW**
- `src/EnergyTracker.Application/RequeueEventCorrelations.cs`, `tests/EnergyTracker.Application.Tests/RequeueEventCorrelationsTests.cs`.

**Must NOT be touched**
- The 9 AD-14-guarded files (any occurrence of the word "Event" fails `PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests`): `BonusDecayNormalizer.cs`, `PatternDetectiveCalculator.cs`, `Status.cs`, `StatusSnapshot.cs`, `GetCurrentStatus.cs`, `IStatusRecomputeService.cs`, `StatusRecomputeService.cs`, `StatusSnapshotConfiguration.cs`, `StatusEndpoints.cs`.
- `web/` (events-card.tsx already renders `correlationDirection` as exactly two fixed translated strings), migrations, `ExportHouseholdData.cs`, `RestoreHouseholdData.cs`, `BackgroundJobProcessor.cs` (the `CorrelateEvent` dispatch case already exists and stays as is).

### Architecture compliance

- **AD-1**: one flat use case per file in `Application`; the new class depends on ports only (`IEventRepository`, `IBackgroundJobQueue`, `ILogger`).
- **AD-3**: `FindByIdAsync`/`GetByOccurredAtRangeAsync` rely on the DbContext query filter; no manual `HouseholdId` predicate. The job path sets `JobHouseholdContext` from the envelope (`BackgroundJobProcessor.cs:24`), so reads inside `CorrelateEvent` are already scoped.
- **AD-6**: payload stays a plain record; the queue adapters and `BackgroundJobEnqueueRecorder` need no change. Duplicate jobs for one Event (several readings entered quickly) are harmless: the first evaluation persists the direction, so the rest hit the zero-AI-call branch.
- **AD-7**: requeue runs after the core write commits, never inside a transaction, and never from `IStatusRecomputeService`.
- **AD-8**: no enablement/backend check anywhere except `CorrelateEvent`.
- **AD-12**: prompts ordered by reading timestamp; exclusion is "trigger and everything at/after".
- **AD-14**: Main-Meter readings only.

### Library / framework requirements

No new packages. xunit.v3 (MTP) + Shouldly + NSubstitute; Testcontainers for the repository tests (per `project-context.md`). Only the portable EF subset (AD-2): the new range query is a plain `Where` on `DateTimeOffset` plus `OrderBy`. `OccurredAt` is stored UTC-normalized (`UtcDateTimeOffsetConverter`), so boundary comparisons are instant-based.

### Testing requirements

- Test names `Snake_case_with_underscores`, `{SubjectClass}Tests`, `TestContext.Current.CancellationToken` in async tests; repository tests go in the abstract `EventRepositoryTestsBase` so both providers run them.
- Boundary cases that must be pinned: exactly ±7 days (inclusive), identical timestamps (tiebreak by `Id`), trigger before/inside/after the window, persisted-direction equal/different/null, AI returns null, Event deleted-or-missing.
- Mutation-style sanity check for the AI-cost guard: temporarily make `CorrelateEvent` always call the client and confirm the zero-call test fails.
- Run per project: Domain, Application, Infrastructure, Api, Architecture.

### Known limitations (file as `[open]` entries if still true at review)

- `CorrelateEvent` `BackgroundJob` rows are never swept (pre-existing; this story adds a few more).
- After a prompt resolve, the re-evaluated set is "Events since the trigger", uncapped; typically a handful.
- A correlation can flip or disappear as readings arrive; by design, but not announced in the UI.

### References

- [Source: _bmad-artifacts/implementation/deferred-work.md and deferred-work-accepted.md, entries from "code review of story-6.3 (2026-09-21)"]
- [Source: _bmad-artifacts/implementation/spec-deferred-work-triage.md, PROMOTE decision 2]
- [Source: _bmad-artifacts/implementation/6-3-wattage-plausibility-correlation.md, Dev Notes decisions 3-5 and the AD-14 guard file list]
- [Source: _bmad-artifacts/implementation/epic-6-retro-2026-09-21.md]
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-6, #AD-7, #AD-8, #AD-10, #AD-12, #AD-14]
- [Source: _bmad-artifacts/planning/epics/epic-9-wattage-plausibility-ai-backend-decision.md]
- [Source: _bmad-artifacts/project-context.md, Process gates and deferred-work.md convention]
- [Source: src/EnergyTracker.Application/CorrelateEvent.cs, CreateEvent.cs, CreateMeterReading.cs, EditMeterReading.cs, ResolveMeterRegressionPrompt.cs]
- [Source: src/EnergyTracker.Domain/Calculations/WindowedDeviationCalculator.cs, PatternDetectiveCalculator.cs (read-only reference)]
- [Source: src/EnergyTracker.Infrastructure/Adapters/EventRepository.cs, BackgroundJobProcessor.cs, BackgroundJobEnqueueRecorder.cs]
- [Source: docs/data-export-format.md#events]

## Dev Agent Record

### Agent Model Used

### Debug Log References

### Completion Notes List

### File List
