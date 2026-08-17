---
baseline_commit: 46bb5b9e26a4c0999d1f417f2aa6b2867efd2368
---

# Story 2.4: Gap-Tolerant Rolling Baseline & Status Computation

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want the system to compute a single trustworthy Status from my reading pace vs my Yearly Baseline, tolerant of irregular reading gaps,
so that I know at a glance whether I'm on track.

## Acceptance Criteria

1. **Given** a sequence of Meter Readings with irregular intervals, including multi-day gaps, **when** the pace is computed, **then** each gap is absorbed into the rate calculation between that reading pair rather than breaking or resetting the computation (FR-3).
2. **Given** the computed pace, **when** compared to the Yearly Baseline, **then** the comparison is like-for-like — pace-to-date vs. baseline-to-date (FR-3).
3. **Given** an unusually long gap since the last reading, **when** Status is computed, **then** it's flagged low-confidence rather than presented with the same certainty as a normal 1-2 day interval (FR-3).
4. **Given** the current pace exceeds the Yearly Baseline pace by more than the household's configured threshold (default ~100 kWh), **when** Status is computed, **then** it resolves to *trending* (FR-6).
5. **Given** the pace is exactly equal to the baseline pace plus the threshold, **when** Status is computed, **then** it resolves to *within range*, not *trending* — an exact tie resolves to the calmer state (FR-6).
6. **Given** fewer than two Meter Readings exist, or no Yearly Baseline is set, **when** Status is requested, **then** it is undefined rather than defaulting to any of the three states (FR-6).
7. **Given** a new Meter Reading is saved, **when** the save completes, **then** Status recomputes immediately — never on a fixed schedule alone (FR-6, AD-7).
8. **Given** Status is (re)computed, **when** the computation completes, **then** the result is also written to an immutable `StatusSnapshot` row via the single `IStatusRecomputeService`, so a later Yearly Baseline/threshold edit never rewrites this historical value (AD-7, NFR9).
9. **Given** any Status computation or API response, **when** inspected, **then** no Smart Plug or Event data is summed into or reconciled against the Main Meter-derived pace figure — `MeterReading` is the sole authoritative total (AD-14).
10. **Given** a Reading excluded due to an unresolved regression prompt (Story 2.3), **when** Status is computed, **then** that Reading, and everything chronologically after it, is excluded from the computation until the prompt is resolved (AD-12).

## Tasks / Subtasks

- [x] **Task 1 — Household-scoped config additions (AC #4, #6; AD-15)**
  - [x] Add `TrendingThresholdKwh` (`decimal`, `HasPrecision(18,2)`) to `Household` — defaults to `100m` at creation (FR-6's default is applied automatically, unlike the Yearly Baseline preset which must never be silently applied — these are different rules, don't conflate them).
  - [x] Add a low-confidence gap threshold to `Household` (e.g. `LowConfidenceGapDays`, `int`) — **no numeric default is specified anywhere in the PRD/epics for "unusually long gap" (AC #3)**, but FR-3's own consequence text (`4-features.md:33`) describes it qualitatively as "the household hasn't logged in **months**" — pick a placeholder consistent with that order of magnitude (e.g. 45-60 days, not something week-scale like 14) and flag it explicitly in Completion Notes as an assumption, not a discovered requirement.
  - [x] Both new columns are covered by `Household.Version` (AD-4) — no new concurrency token needed.
  - [x] No new endpoint/UI for editing these is required by this story's ACs (FR-21's broader tunable-threshold settings UI is explicitly deferred) — a DB-level default is sufficient.

- [x] **Task 2 — Domain calculation layer (AC #1, #2, #4, #5; AD-5, AD-14)**
  - [x] Create `EnergyTracker.Domain/Calculations/BonusDecayNormalizer.cs` — a **new, shared, pure** static/pure-function class. Per AD-5, this is "a pure function of (rate, bonus terms, elapsed time)" and must be the **one and only** place that does elapsed-time-proportional target normalization. Story 5.2 (Tariff Savings Radar, later epic) already commits in its own AC text to calling this exact same module — build it now shaped generically enough that Story 5.2 can pass real bonus terms later, while this story calls it with no/zero bonus terms for the pace-vs-baseline-to-date comparison.
  - [x] Create a second calculation type (e.g. `EnergyTracker.Domain/Calculations/PatternDetectiveCalculator.cs` or similar) that computes gap-tolerant pace-to-date from an ordered `MeterReading` sequence — this is distinct from `BonusDecayNormalizer` (which only normalizes the *comparison target*, not the pace itself). Do not conflate the two into one class.
  - [x] Pace-to-date computation: walk the (filtered — see Task 3) reading sequence pairwise by `ReadingTimestamp`; each gap's consumption/time is absorbed into that pair's rate, never breaking the sequence (AC #1).
  - [x] Baseline-to-date: prorate `Household.YearlyBaselineKwh` by elapsed time via `BonusDecayNormalizer` (AC #2).
  - [x] Status resolution: `trending` only when pace exceeds baseline-to-date by **more than** `TrendingThresholdKwh` — an exact-equal case is `withinRange` (AC #4, #5). Implement this as `>`, not `>=`.
  - [x] Nowhere in this calculation path may `SmartPlugReading` or `Event` data be read or summed (AC #9, AD-14) — this story only ever touches `MeterReading`.

- [x] **Task 3 — Status enum + undefined semantics (AC #6)**
  - [x] Add `EnergyTracker.Domain/Status.cs` enum with exactly 3 members: `WithinRange`, `BelowBaseline`, `Trending` — **no 4th "Undefined" member** (mirrors the UX rule that there is no 4th visual status state). "Undefined" is represented by returning `null`/no computable Status from the service and API layer, not an enum case.
  - [x] Fewer than two Meter Readings (after regression-exclusion, Task 4) or no `YearlyBaselineKwh` set ⇒ undefined (`null`) result, not a default enum value (AC #6).

- [x] **Task 4 — Regression-prompt exclusion (AC #10; AD-12)**
  - [x] Reuse `IMeterRegressionPromptRepository.GetOpenForHouseholdAsync(householdId, ct)` (already exists, `src/EnergyTracker.Application/Ports/IMeterRegressionPromptRepository.cs:11`) to find the open prompt, if any.
  - [x] When an open prompt exists, exclude its triggering `MeterReading` (via `MeterRegressionPrompt.MeterReadingId`) **and every reading with a `ReadingTimestamp` at or after it** from the pace computation — not just the one flagged reading.
  - [x] Add a new read method to `IMeterReadingRepository` (e.g. `GetAllByMainMeterAsync(Guid mainMeterId, CancellationToken ct)`, ordered by `ReadingTimestamp`) — no such full-sequence read exists today (only `FindImmediatelyPrecedingAsync` and `FindByIdAsync` exist, `src/EnergyTracker.Application/Ports/IMeterReadingRepository.cs`).
  - [x] Define an explicit, deterministic tiebreak for readings sharing an identical `ReadingTimestamp` (e.g. order by `Id` as a secondary key) — Story 2.3's code review flagged nondeterministic same-timestamp ordering twice (`FindImmediatelyPrecedingAsync`, `GetOpenForHouseholdAsync`); this story's sequence walk is exactly the kind of code that bug class recurs in if not addressed upfront.

- [x] **Task 5 — `IStatusRecomputeService` + `StatusSnapshot` (AC #7, #8; AD-7, AD-4)**
  - [x] Add `EnergyTracker.Domain/StatusSnapshot.cs` — immutable, insert-only (**no `Version` column** — unlike `MeterReading`/`Household`, nothing ever updates a snapshot row). Include at minimum: `Id`, `HouseholdId`, `Status` (nullable — `null` when undefined, but in practice only recompute a definite Status; consider whether an undefined result should write a snapshot at all — see Open Questions), `PaceToDateKwh`, `BaselineToDateKwh`, `IsLowConfidence` (bool), `ComputedAtUtc`.
  - [x] Add `EnergyTracker.Application/Ports/IStatusRecomputeService.cs` — one method, e.g. `Task RecomputeAsync(Guid householdId, CancellationToken ct)`. Per AD-7's exact wording, this is "exactly one application service" — but since writing a `StatusSnapshot` row requires EF Core (an Infrastructure concern under AD-1), the interface lives in `Application/Ports` and its concrete implementation lives in `Infrastructure/Adapters` (e.g. `StatusRecomputeService.cs`) — same Ports & Adapters split as every other port in this codebase, despite AD-7's prose calling it an "application service."
  - [x] Wire the call into `CreateMeterReading.ExecuteAsync` (`src/EnergyTracker.Application/CreateMeterReading.cs`) — call `IStatusRecomputeService.RecomputeAsync` **immediately before `return persistedReading;` at line 78**, i.e. *after* the existing regression-prompt block (lines 58-76), not at line 56 where `persistedReading` is merely assigned. Call it unconditionally, regardless of whether a regression prompt was also opened in that same call (AC #7 says every save recomputes; AD-7 names the Meter-Reading-create handler as one of exactly two call sites — the other, Smart-Plug-import-completion, doesn't exist yet and is Epic 3's job to wire, not this story's).
  - [x] Add `IStatusRecomputeService`/its adapter to `Program.cs`'s DI block near the existing MeterReading/MeterRegressionPrompt registrations (`Program.cs:287-291`), following the flat `AddScoped<IPort, Adapter>()` pattern used there — no generic registration helper.

- [x] **Task 6 — Live-read query + API endpoint (AC #6, #7; AD-7, Consistency Conventions)**
  - [x] Add a query use case (e.g. `GetCurrentStatus.cs` in `EnergyTracker.Application`) that computes Status **live, synchronously, at request time** per AD-7 ("current Status... pure, synchronous computation evaluated on every relevant read") — this must **not** just read the latest `StatusSnapshot` row; it re-runs Task 2's calculation against current data, using the same exclusion/threshold logic as the recompute path.
  - [x] Add `EnergyTracker.Api/Endpoints/StatusEndpoints.cs` exposing `GET /api/status` (kebab-case convention, singular concept — no plural form applies; matches the already-established precedent at `src/EnergyTracker.Api/Endpoints/SessionEndpoints.cs:11`, "Singleton resource, not a collection — `/api/session`, not plural (consistent with `/health`)"). Per Consistency Conventions, "the Dashboard Status endpoint returns only the current Status value and its one headline/supporting sentence — drill-down data is always a separate endpoint, never merged into the Status response." Return `null`/`204`-shaped response (not an error) when Status is undefined (AC #6) — Story 2.5 needs this to render its onboarding empty state.
  - [x] **This endpoint is not explicitly named in this story's own AC list** (only in the epic-wide Consistency Conventions doc) — it's included because Story 2.5 (Dashboard Status Display) has no backend ACs of its own and must consume something. Confirmed with Ralf during dev-story activation: build it now in 2.4 rather than deferring to 2.5.
  - [x] Include the `IsLowConfidence` flag (AC #3) in the response DTO — Story 2.5's UX spec (`EXPERIENCE.md`) references this exact flag for its "unusually long gap" treatment.

- [x] **Task 7 — Migrations (AD-2)**
  - [x] Run `scripts/add-migration.sh <Name>` (never `dotnet ef` directly) to add: `Households.TrendingThresholdKwh`, `Households.LowConfidenceGapDays`, and the new `StatusSnapshots` table — in one migration, added to both `Infrastructure.Migrations.Postgres` and `Infrastructure.Migrations.SqlServer` in the same commit.
  - [x] `StatusSnapshotConfiguration.cs` in `Infrastructure/Configurations` — mirror `MeterReadingConfiguration.cs`'s exact pattern: `ToTable`, explicit `HasPrecision(18,2)` on every decimal, FK to `Household` with `OnDelete(DeleteBehavior.Restrict)` (never Cascade, AD-10 precedent), `HasIndex(s => s.HouseholdId)` for AD-3's query filter. No unique index needed (multiple snapshots per household over time are expected).

- [x] **Task 8 — Close a named deferred gap (not in this story's own AC list, but explicitly flagged as this story's to own)**
  - [x] Add bounds validation on `MeterReading.ReadingTimestamp` in `CreateMeterReading.ExecuteAsync` (reject e.g. far-future timestamps beyond a small clock-skew allowance, and unreasonable past dates) — `_bmad-artifacts/implementation/deferred-work.md` (from Story 2.2's code review) explicitly states an unbounded timestamp "could distort Story 2.3/2.4's baseline and regression logic that will rely on timestamp ordering" and names this story as the likely owner. Story 2.3 didn't need it (regression detection only compares adjacent pairs); this story's gap/pace/elapsed-time math is exactly the code an out-of-range timestamp would corrupt. Throw the existing `MeterReadingValidationException` (400), matching the bound-checking discipline already used for `KwhValue` in the same method.

- [x] **Task 9 — Tests**
  - [x] `BonusDecayNormalizerTests` and pace-calculator tests — no `EnergyTracker.Domain.Tests` project exists (`tests/` contains only `EnergyTracker.Infrastructure.Tests`, `EnergyTracker.Application.Tests`, `EnergyTracker.Api.Tests`, `EnergyTracker.Architecture.Tests`); put these under `EnergyTracker.Application.Tests`. Cover: multi-day gap absorption (AC #1), tie-at-threshold resolves to `WithinRange` not `Trending` (AC #5), undefined with <2 readings or no baseline (AC #6).
  - [x] `GetCurrentStatusTests` / recompute-service tests (`EnergyTracker.Application.Tests`, `Snake_case_with_underscores` method names, Shouldly assertions, NSubstitute mocks for the repository ports) — cover regression-exclusion (AC #10, mock `GetOpenForHouseholdAsync` returning an open prompt and assert the flagged reading + later readings are excluded) and low-confidence gap flagging (AC #3).
  - [x] `CreateMeterReadingTests` — extend to assert `IStatusRecomputeService.RecomputeAsync` is called (`Received(1)`) after every successful save, including the regression-prompt-opened path (AC #7).
  - [x] API integration tests (`EnergyTracker.Api.Tests`, `EnergyTrackerApiFactory` + real Postgres via Testcontainers, following `MeterRegressionPromptEndpointsTests.cs`'s precedent) — `GET /api/status` has no `{id}` path parameter (it's a caller-scoped singleton like `/api/session`), so "cross-Household isolation" is verified by asserting each Household's own Status is never affected by another Household's readings/baseline, plus the standard "no Household → 403" precedent; also assert the undefined-Status response shape (AC #6).
  - [x] No AD-14 violation: add/keep a guard test (or extend `EnergyTracker.Architecture.Tests` if that's where AD-14 is enforced) confirming no `SmartPlugReading`/`Event` type is referenced anywhere in the new calculation/service/DTO code.

### Review Findings

- [x] [Review][Decision→Confirmed] `StatusResponse` returns raw `PaceToDateKwh`/`BaselineToDateKwh` figures — Ralf confirmed this is an approved, deliberate exception to the "headline/supporting sentence only" convention: Story 2.5's frontend needs the raw figures to render its own sentence. No code change.
- [x] [Review][Decision→Confirmed] `BelowBaseline` boundary at an exact pace==baseline tie — Ralf confirmed the implemented behavior (exact tie → `WithinRange`) matches product intent, consistent with AC #5's "ties resolve to the calmer state" principle. No code change.
- [x] [Review][Decision→Patch] Resolved Reset/Rollover regressions are never corrected for in the pace walk — Fixed. `PatternDetectiveCalculator.ComputePaceToDate` now takes a `resolvedPromptsByTriggeringReadingId` lookup (sourced from new `IMeterRegressionPromptRepository.GetResolvedForMainMeterAsync`) and applies FR-2's algorithm per pair: Rollover offsets via `(prompt.DigitCapacityKwh − previous.KwhValue) + current.KwhValue` (using the prompt's own captured capacity, not `MainMeter`'s current mutable value); Reset voids the spanning pair entirely (contributes to neither total). [`src/EnergyTracker.Domain/Calculations/PatternDetectiveCalculator.cs`, `src/EnergyTracker.Application/GetCurrentStatus.cs`, `src/EnergyTracker.Application/Ports/IMeterRegressionPromptRepository.cs`, `src/EnergyTracker.Infrastructure/Adapters/MeterRegressionPromptRepository.cs`] — covered by new `PatternDetectiveCalculatorTests`/`GetCurrentStatusTests` cases (rollover correction, reset voiding).
- [x] [Review][Decision→Patch] Pace/baseline-to-date are anchored to the household's entire lifetime reading history, not a bounded year — Fixed. `ComputePaceToDate` now windows to the trailing 365 days from the most recent reading before walking pairs; returns undefined if fewer than 2 readings survive the window. [`src/EnergyTracker.Domain/Calculations/PatternDetectiveCalculator.cs`] — covered by new test cases in both `PatternDetectiveCalculatorTests` and `GetCurrentStatusTests`.
- [x] [Review][Patch] `StatusRecomputeService.RecomputeAsync` failure after the Meter Reading is already persisted causes a spurious 500 on an otherwise-successful save — Fixed. The try/catch (and `ILogger`) live in `StatusRecomputeService` (Infrastructure), not `CreateMeterReading` (Application) — AD-1 forbids Application from referencing framework/vendor packages like `Microsoft.Extensions.Logging`, and Infrastructure already has the ASP.NET Core `FrameworkReference` needed for `ILogger<T>`. A failure is logged and swallowed; the triggering write is unaffected. [`src/EnergyTracker.Infrastructure/Adapters/StatusRecomputeService.cs`] — not covered by a dedicated fault-injection test (no seam exists to force `SaveChangesAsync` to throw without a new test-only abstraction); verified by code inspection.
- [x] [Review][Patch] `ExcludeFromOpenPrompt` silently falls back to the unfiltered reading sequence instead of throwing when the open prompt's triggering reading ID isn't found — Fixed: now throws `InvalidOperationException`. [`src/EnergyTracker.Domain/Calculations/PatternDetectiveCalculator.cs`] — covered by new `PatternDetectiveCalculatorTests` case.
- [x] [Review][Patch] Idempotency-key check runs after timestamp-bounds validation, so a retry of an already-persisted reading that violates a bounds rule added later would 400 instead of returning the existing reading — Fixed: the `FindByIdempotencyKeyAsync` short-circuit now runs first. [`src/EnergyTracker.Application/CreateMeterReading.cs`] — existing `CreateMeterReadingTests` idempotency/validation cases still pass unchanged (validated the reordering didn't affect either behavior).
- [x] [Review][Patch] Identical-timestamp readings (a state the code explicitly anticipates via its `(ReadingTimestamp, Id)` tiebreak) produce zero total elapsed time, so `BaselineToDateKwh` normalizes to 0 and any positive consumption spuriously resolves to `Trending` — Fixed: `ComputePaceToDate` returns undefined (`null`) when total elapsed time is zero. [`src/EnergyTracker.Domain/Calculations/PatternDetectiveCalculator.cs`] — covered by new `PatternDetectiveCalculatorTests` case.
- [x] [Review][Patch] AC #8 (`StatusSnapshot` persistence) has zero test coverage — Fixed: new `StatusEndpointsTests` integration test (Testcontainers) asserts no snapshot is written while Status is undefined, then asserts a snapshot row with correct `Status`/`PaceToDateKwh`/`BaselineToDateKwh` exists after a save that makes Status definite. [`tests/EnergyTracker.Api.Tests/StatusEndpointsTests.cs`]
- [x] [Review][Defer] Full-history read/walk on every meter-reading save is a latent NFR1 Tier-1 (≤2s) performance risk that grows unboundedly for long-lived households, compounding the lifetime-anchoring decision item above [`src/EnergyTracker.Application/GetCurrentStatus.cs:36`, `src/EnergyTracker.Application/CreateMeterReading.cs:107`] — deferred, pre-existing pattern extended, not yet a measured problem at current data volumes.
- [x] [Review][Defer] `TrendingThresholdKwh`/`LowConfidenceGapDays` have no range/bound validation, unlike the bound-checking discipline Story 2.3's own review called for [`src/EnergyTracker.Domain/Household.cs:23,29`] — deferred, currently unreachable since no endpoint in this diff writes to these columns; revisit when FR-21's settings-editing UI ships.
- [x] [Review][Defer] Resolving a `MeterRegressionPrompt` doesn't trigger a Status recompute, leaving a `StatusSnapshot` audit-trail gap at classification-resolution events [`src/EnergyTracker.Application/CreateMeterReading.cs` — contrast with Story 2.3's resolve-prompt use case] — deferred, in-spec per AD-7's two-call-site rule; revisit once FR-8 Trend History is built.
- [x] [Review][Defer] Concurrent `RecomputeAsync` calls for the same household race with no per-household serialization; a later insert from a stale read can leave the most recent `StatusSnapshot` row not reflecting the newest data [`src/EnergyTracker.Infrastructure/Adapters/StatusRecomputeService.cs:9-35`] — deferred, no consumer of `StatusSnapshot` ordering exists yet (Trend History is Epic 4); revisit before that ships.

## Dev Notes

### Architecture compliance (binding, not optional)

- **AD-5 gap in the epic header — read this carefully.** Epic 2's own header (`epic-2-meter-reading-pattern-detective-status-core.md:7`) lists only AD-4, AD-7, AD-12, AD-14, AD-16 — it does **not** mention AD-5. But `capability-architecture-map.md:5` explicitly binds AD-5 to Pattern Detective (this story's feature) for "pace threshold" math, and Story 5.2's own AC text (`epic-5-tariff-savings-radar.md:68`) commits Tariff Savings Radar to calling "the single shared `Domain.Calculations.BonusDecayNormalizer` also used by Pattern Detective's pace threshold." Since Story 5.2 comes later and depends on this module already existing and already being Pattern-Detective-proven, **this story must build `Domain.Calculations.BonusDecayNormalizer`**, not a locally-scoped prorating calculation. This is the single easiest thing to miss in this story — it's absent from the epic's own AD list.
- **AD-7**: current Status is a pure, synchronous, request-time computation — never precomputed on a schedule (no `IHostedService`/`Timer`). Separately, every (re)computation writes one immutable `StatusSnapshot` row via the single `IStatusRecomputeService`. Two call sites exist in the full design (Meter-Reading-create, Smart-Plug-import-completion) — this story wires only the first; the second doesn't exist yet (Epic 3).
- **AD-12**: at most one open `MeterRegressionPrompt` per Main Meter; it excludes its triggering reading and everything chronologically after it from baseline computation until resolved. "Open" is computed (via `GetOpenForHouseholdAsync`), never a stored boolean flag — Story 2.3 established this pattern deliberately; don't add an `IsOpen` column.
- **AD-14**: `MeterReading` is the sole authoritative total. No `SmartPlugReading`/`Event` data may be read, summed, or referenced anywhere in this story's Domain/Application/Api code — there is no Smart Plug data to sharpen with yet (Epic 3), so this should be a non-issue in practice, but the architecture test guard exists precisely so a future story can't quietly violate it either.
- **AD-15**: the trending threshold and low-confidence gap threshold are `Household`-scoped config columns, never literals in code.
- **AD-4**: both new `Household` columns ride the existing `Version` token — no new concurrency machinery.
- **NFR9** (recomputation policy, `cross-cutting-nfrs.md:11`): a later Yearly Baseline/threshold edit must never rewrite a past `StatusSnapshot` — this is exactly why the snapshot is immutable/insert-only with no Version column and no update path.
- **NFR1**: Status recompute runs synchronously inline with `CreateMeterReading`, which is a Tier 1 (≤2s) path — keep the recompute calculation cheap (single Main Meter's full reading sequence, not a cross-household scan).

### Existing code this story builds on (read before writing anything)

- `src/EnergyTracker.Domain/MeterReading.cs` — `Id, HouseholdId, MainMeterId, KwhValue, ReadingTimestamp, IdempotencyKey, CreatedAtUtc`. No `Version` (deliberate).
- `src/EnergyTracker.Domain/Household.cs` — already has `YearlyBaselineKwh` (nullable decimal) and `Version` (AD-4 token); add the two new columns here.
- `src/EnergyTracker.Domain/MainMeter.cs` — `Id, HouseholdId, CreatedAtUtc, DigitCapacityKwh?`; one per household.
- `src/EnergyTracker.Domain/MeterRegressionPrompt.cs` / `MeterRegressionClassification.cs` — prompt has `ResolvedAtUtc?`, `Classification?`, `MeterReadingId`.
- `src/EnergyTracker.Application/Ports/IMeterRegressionPromptRepository.cs:11` — `GetOpenForHouseholdAsync(Guid householdId, CancellationToken)` already exists; reuse it, don't reinvent.
- `src/EnergyTracker.Application/Ports/IMeterReadingRepository.cs` — has `FindByIdempotencyKeyAsync`, `GetOrCreateMainMeterAsync`, `AddAsync`, `FindImmediatelyPrecedingAsync`, `FindByIdAsync`. **No full-sequence read exists** — you must add one.
- `src/EnergyTracker.Application/CreateMeterReading.cs` — primary-constructor DI (`IMeterReadingRepository`, `IMeterRegressionPromptRepository`); `ExecuteAsync` returns `persistedReading` at line 78. Add the `IStatusRecomputeService` call right before that return, after the existing regression-prompt block.
- Confirmed (via full-codebase search): **no `StatusSnapshot`, `IStatusRecomputeService`, `BonusDecayNormalizer`, or `Domain/Calculations` namespace exists anywhere in `src/` yet** — this story is greenfield for all of them.

### File structure / conventions to follow exactly

- One use-case class per file, flat under `EnergyTracker.Application/` (no feature-folder nesting) — e.g. `GetCurrentStatus.cs`, imperative-verb named.
- Ports: `I{Capability}` in `Application/Ports`. Adapters: `{Capability}` (this codebase's existing repositories aren't vendor-prefixed, e.g. `MeterReadingRepository`, not `PostgresMeterReadingRepository` — the vendor split happens at the DbContext/migrations level, not the adapter class name) in `Infrastructure/Adapters`.
- EF configs: `{Entity}Configuration.cs` in `Infrastructure/Configurations`, `IEntityTypeConfiguration<T>`, explicit `HasPrecision(18,2)` on every decimal, `OnDelete(DeleteBehavior.Restrict)` on every FK (never Cascade — AD-10 precedent, applies here too even though `StatusSnapshot` isn't a soft-deletable entity), `HasIndex` on `HouseholdId` for AD-3's query filter.
- Migrations: `scripts/add-migration.sh <Name>` only, both provider projects in one commit.
- DI: flat `builder.Services.AddScoped<IPort, Adapter>()` blocks in `Program.cs` (~line 269-291), grouped by feature, no generic registration helper.
- Test naming: `{SubjectClass}Tests.cs`, one file per class, `Snake_case_with_underscores` method names, Shouldly assertions (`.ShouldBe`, `Should.ThrowAsync<T>`), NSubstitute mocks (`Substitute.For<IPort>()`), `TestContext.Current.CancellationToken` for async tests, mirrored 1:1 between `src/{Layer}` and `tests/{Layer}.Tests`.

### Previous story intelligence (Story 2.3)

- Story 2.3's own Dev Notes state explicitly: *"No live baseline/pace computation exists yet... there is no Status endpoint, no pace calculation, and nothing to display a computed kWh figure to yet."* — confirms this story starts from zero, nothing partial to reconcile.
- Two code-review findings from 2.3 are directly relevant here and must not recur: (a) nondeterministic tiebreak on identical `ReadingTimestamp` values, flagged twice in review — this story's full-sequence walk needs an explicit, deterministic tiebreak from day one (Task 4); (b) a decimal input (`DigitCapacityKwh`) shipped without upper-bound validation and caused an unhandled 500 instead of a 400 — apply the same bound-checking discipline to the two new `Household` decimal/int columns this story adds.
- `deferred-work.md` (Story 2.2 review) explicitly names this story as the likely owner of `ReadingTimestamp` bounds validation, since unbounded timestamps would distort exactly the gap/pace/elapsed-time math this story implements (Task 8).
- Story 2.4 has **zero UX-DR references** in the epic file, unlike 2.3/2.5 — confirmed this is a pure computation/API story with no new UI. All UI consumption is Story 2.5's job.

### Testing standards summary

.NET: xunit.v3.mtp-v2, Shouldly, NSubstitute, Testcontainers (Postgres/SqlServer) for integration tests — no in-memory EF provider for anything touching AD-2 portability. Frontend: not applicable, this story has no frontend changes.

### Project Structure Notes

- Alignment: follows the exact layered structure (`Domain` → `Application` → `Infrastructure`/`Api`) and dual-migration convention already established by every prior Epic 1/2 story — no deviation needed.
- Detected conflict: the epic-2 header's AD list omits AD-5, which this story's own scope requires per the capability map and Story 5.2's forward dependency — resolved above under Architecture compliance, not treated as a blocker.

### References

- [Source: `_bmad-artifacts/planning/epics/epic-2-meter-reading-pattern-detective-status-core.md#Story 2.4`] — story statement + AC source (verbatim).
- [Source: `_bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md#FR-3, #FR-6`] — FR consequences, exact threshold default wording.
- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-4, #AD-5, #AD-7, #AD-12, #AD-14, #AD-15`] — exact AD rule text.
- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/capability-architecture-map.md`] — AD-5 binding to Pattern Detective.
- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/consistency-conventions.md`] — "Dashboard Status endpoint" API-surface-shape rule.
- [Source: `_bmad-artifacts/planning/epics/epic-5-tariff-savings-radar.md#Story 5.2`] — forward dependency on `BonusDecayNormalizer` already existing.
- [Source: `_bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/cross-cutting-nfrs.md`] — NFR9 recomputation policy, exact wording.
- [Source: `_bmad-artifacts/implementation/2-3-meter-reading-regression-detection-classification.md`] — previous story intelligence.
- [Source: `_bmad-artifacts/implementation/deferred-work.md`] — `ReadingTimestamp` bounds-validation gap naming this story.
- [Source: `src/EnergyTracker.Application/CreateMeterReading.cs`, `src/EnergyTracker.Application/Ports/IMeterReadingRepository.cs`, `src/EnergyTracker.Application/Ports/IMeterRegressionPromptRepository.cs`, `src/EnergyTracker.Domain/Household.cs`, `src/EnergyTracker.Infrastructure/Configurations/MeterReadingConfiguration.cs`] — existing code this story extends.

## Open Questions for Ralf — resolved during dev-story activation (2026-08-17)

1. **"Unusually long gap" numeric default.** Confirmed: `LowConfidenceGapDays` defaults to **45 days**.
2. **`GET /api/status` endpoint scope.** Confirmed: build it now in Story 2.4 (Task 6), not deferred to Story 2.5.
3. **Should an undefined Status write a `StatusSnapshot` row?** Confirmed: **no** — `IStatusRecomputeService.RecomputeAsync` is a no-op when the live computation is undefined; only a definite Status is ever snapshotted. `StatusSnapshot.Status` is therefore non-nullable.

**Note (not a question — resolved during authoring):** epic-2's own header omits AD-5 from its AD list, but `capability-architecture-map.md:5` explicitly binds AD-5 to Pattern Detective, and Story 5.2's AC text already commits to calling `Domain.Calculations.BonusDecayNormalizer` "also used by Pattern Detective's pace threshold." Treating the epic header as stale on this one point and building the real shared module now (Task 2) is the reading the evidence supports — not treated as open.

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5), via the dev-story workflow.

### Debug Log References

None — no blocking failures required a separate debug log. One implementation issue surfaced and was fixed during Task 9's test run: several *pre-existing* API integration tests (`MeterRegressionPromptEndpointsTests`, `MeterReadingEndpointsTests`) anchored their `readingTimestamp` fixtures at `DateTimeOffset.UtcNow` and then applied *positive* hour offsets (`.AddHours(1)`, `.AddHours(2)`, `.AddHours(3)`) to establish reading order — which is legitimate test-data construction, but collided with this story's new Task 8 future-timestamp bounds check (5-minute clock-skew allowance). Fixed by anchoring those fixtures a day in the past (`DateTimeOffset.UtcNow.AddDays(-1)`) instead, preserving every test's relative ordering/assertions unchanged.

### Completion Notes List

- **Assumption (Task 1, AC #3):** `LowConfidenceGapDays` defaults to **45 days**. No numeric value for "unusually long gap" exists anywhere in the PRD/epics (FR-3 only says qualitatively "hasn't logged in months"); this was proposed by the story and confirmed with Ralf during dev-story activation before implementation.
- **Inference confirmed (Task 6):** `GET /api/status` is not named in this story's own AC list, only in the epic-wide Consistency Conventions doc (Story 2.5 has no backend ACs of its own and needs something to call). Confirmed with Ralf during dev-story activation: build it now in 2.4, not deferred to 2.5.
- **Decision confirmed (Task 5/AC #8, Open Question #3):** An undefined Status computation never writes a `StatusSnapshot` row — `StatusRecomputeService.RecomputeAsync` is a no-op in that case. `StatusSnapshot.Status` is therefore non-nullable (only ever populated with a definite Status). Confirmed with Ralf during dev-story activation.
- **Necessary plumbing beyond the story's own task list:** added `IHouseholdRepository.FindByIdAsync` (no existing method read a `Household` by id alone) and `IMeterReadingRepository.FindMainMeterByHouseholdAsync` (a genuinely read-only MainMeter lookup — reusing the existing `GetOrCreateMainMeterAsync` in a read path would have the side effect of inserting a stray `MainMeter` row the first time a household with zero readings loads its dashboard). Both are minimal, single-method additions following the existing repository patterns.
- **BonusDecayNormalizer signature:** implemented as `NormalizeToDate(decimal annualRateKwh, decimal bonusTermsKwh, TimeSpan elapsed)`, matching AD-5's literal "(rate, bonus terms, elapsed time)" parameter order. A hardcoded 365-day year is used internally (AD-5's wording doesn't mention a period parameter); this story always calls it with `bonusTermsKwh: 0`, which degenerates the bonus-decay term to zero and leaves only a straight day-count proration of `YearlyBaselineKwh`. Story 5.2 is the first real caller of the bonus-decay behavior itself and may need to revisit whether a period parameter should be added — flagging for that story's own dev-story activation rather than guessing its shape now.
- **Status resolution semantics (not fully pinned by the story's ACs):** only AC #4 (`Trending` boundary) and AC #5 (tie → `WithinRange`) are specified. `BelowBaseline` is implemented as `pace - baselineToDate < 0`; an exact tie at `pace == baselineToDate` also resolves to `WithinRange` (not `BelowBaseline`), by the same "ties resolve to the calmer state" principle FR-6 states for the Trending boundary. This is an implementation inference, not a directly-stated AC — confirmed with Ralf during code review (see Review Findings above).
- All 219 backend tests pass (Domain calculations exercised via `EnergyTracker.Application.Tests` per the story's own guidance — no `EnergyTracker.Domain.Tests` project exists); `dotnet build` clean in both Debug and Release; both Postgres and SqlServer migrations verified against real Testcontainers instances. No frontend files touched (story has zero UI surface, confirmed in Dev Notes).
- **Code review round (2026-08-17):** 4 decision-needed (2 confirmed as-is, 2 became patches — rollover/reset correction and trailing-365-day windowing), 5 patch, 4 defer, 3 dismissed. All patches applied; see Review Findings above. Test count grew from 210 to 219 (8 new Domain/Application unit tests + 1 new API integration test).

### File List

**New files:**
- `src/EnergyTracker.Domain/Status.cs`
- `src/EnergyTracker.Domain/StatusSnapshot.cs`
- `src/EnergyTracker.Domain/Calculations/BonusDecayNormalizer.cs`
- `src/EnergyTracker.Domain/Calculations/PatternDetectiveCalculator.cs`
- `src/EnergyTracker.Application/GetCurrentStatus.cs`
- `src/EnergyTracker.Application/Ports/IStatusRecomputeService.cs`
- `src/EnergyTracker.Infrastructure/Adapters/StatusRecomputeService.cs`
- `src/EnergyTracker.Infrastructure/Configurations/StatusSnapshotConfiguration.cs`
- `src/EnergyTracker.Api/Endpoints/StatusEndpoints.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260817051304_AddStatusSnapshotAndHouseholdThresholds.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260817051304_AddStatusSnapshotAndHouseholdThresholds.Designer.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260817051307_AddStatusSnapshotAndHouseholdThresholds.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260817051307_AddStatusSnapshotAndHouseholdThresholds.Designer.cs`
- `tests/EnergyTracker.Application.Tests/BonusDecayNormalizerTests.cs`
- `tests/EnergyTracker.Application.Tests/PatternDetectiveCalculatorTests.cs`
- `tests/EnergyTracker.Application.Tests/GetCurrentStatusTests.cs`
- `tests/EnergyTracker.Api.Tests/StatusEndpointsTests.cs`
- `tests/EnergyTracker.Architecture.Tests/PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests.cs`

**Modified files:**
- `src/EnergyTracker.Domain/Household.cs` — `TrendingThresholdKwh`, `LowConfidenceGapDays` columns.
- `src/EnergyTracker.Application/CreateMeterReading.cs` — `ReadingTimestamp` bounds validation (Task 8), `IStatusRecomputeService` wiring (Task 5/AC #7).
- `src/EnergyTracker.Application/Ports/IHouseholdRepository.cs` — added `FindByIdAsync`.
- `src/EnergyTracker.Application/Ports/IMeterReadingRepository.cs` — added `FindMainMeterByHouseholdAsync`, `GetAllByMainMeterAsync`.
- `src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs` — `FindByIdAsync` implementation.
- `src/EnergyTracker.Infrastructure/Adapters/MeterReadingRepository.cs` — `FindMainMeterByHouseholdAsync`, `GetAllByMainMeterAsync` implementations.
- `src/EnergyTracker.Infrastructure/Configurations/HouseholdConfiguration.cs` — new column configuration.
- `src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs` — `StatusSnapshots` DbSet + AD-3 query filter.
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/EnergyTrackerDbContextModelSnapshot.cs` — regenerated by `add-migration.sh`.
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/EnergyTrackerDbContextModelSnapshot.cs` — regenerated by `add-migration.sh`.
- `src/EnergyTracker.Api/Program.cs` — DI registrations (`IStatusRecomputeService`, `GetCurrentStatus`), `MapStatusEndpoints()`.
- `tests/EnergyTracker.Application.Tests/CreateMeterReadingTests.cs` — `IStatusRecomputeService` mock wiring, AC #7 recompute assertions, Task 8 bounds-validation tests.
- `tests/EnergyTracker.Api.Tests/MeterReadingEndpointsTests.cs` — timestamp fixture fix (see Debug Log References).
- `tests/EnergyTracker.Api.Tests/MeterRegressionPromptEndpointsTests.cs` — timestamp fixture fix (see Debug Log References).
