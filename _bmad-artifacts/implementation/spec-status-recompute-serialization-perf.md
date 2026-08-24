---
title: 'Serialize Status Recompute & Bound History Reads'
type: 'bugfix'
created: '2026-08-23'
status: 'done'
review_loop_iteration: 1
baseline_commit: '66b9fd04f7a84fd9a3808c8eab4678a4501f90a8'
context: ['{project-root}/_bmad-artifacts/implementation/epic-3-retro-2026-08-23.md']
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `IStatusRecomputeService.RecomputeAsync` has no per-household serialization, so its three concurrent trigger call sites (meter reading save, Smart Plug import direct-match completion, import mapping completion) can race and leave the most recent `StatusSnapshot` row not reflecting the newest committed data — deferred since Story 2.4, now due because Epic 4's Trend History will read `StatusSnapshot` rows directly. Compounding it, every recompute (and every live `GET /api/status`) unconditionally re-reads and re-walks a household's *entire* `MeterReading` history even though `PatternDetectiveCalculator.ComputePaceToDate` only ever uses a trailing 365-day window.

**Approach:** Add a per-household in-process async lock (new `IHouseholdRecomputeLock` port/adapter, singleton) that `StatusRecomputeService` acquires around its existing read-then-write body, so recomputes for the same household never overlap. Bound `GetCurrentStatus`'s reading fetch to a window trailing the `MainMeter`'s actual most recent reading (never `DateTimeOffset.UtcNow`), dynamically widened in the same query to also cover an open `MeterRegressionPrompt`'s triggering reading whenever that reading is older than the window — so the fetch is provably never missing data `ExcludeFromOpenPrompt`/`ComputePaceToDate` need, regardless of how long a prompt stays unresolved. Backed by a new composite DB index.

## Boundaries & Constraints

**Always:**
- The app is single-replica (`infra/modules/container-app.bicep`: `maxReplicas = 1`, scale-to-zero) — an in-process lock is correct here; do not build a distributed/DB-level lock.
- `IStatusRecomputeService`'s public interface stays unchanged — the lock lives inside `StatusRecomputeService`, not the port, so existing `Substitute.For<IStatusRecomputeService>()` mocks in `CreateMeterReadingTests`/`ProcessSmartPlugImportTests`/`MapSmartPlugImportToPowerPointTests` are untouched.
- `GetCurrentStatus`'s calculation semantics (`PatternDetectiveCalculator`'s 365-day pace window, `ExcludeFromOpenPrompt`) stay bit-for-bit identical — only how much history is fetched from the DB changes, never how it's calculated.
- The bounded fetch window is computed relative to the `MainMeter`'s actual most recent reading, never `DateTimeOffset.UtcNow` — an idle household with no readings in months must still compute correctly.
- **(Round-2)** The fetch must also dynamically widen to cover an open `MeterRegressionPrompt`'s triggering reading whenever that's older than the base window (see Design Notes for why) — must hold on every call, not just the common case.
- New migration via `scripts/add-migration.sh`, landing in both provider projects in one commit (AD-2).
- Preserve `StatusRecomputeService`'s existing catch-and-log behavior — a recompute failure must never fail the caller's already-committed write.

**Ask First:**
- If one portable query can't express both the base window and the widen logic on both providers, HALT and confirm a fallback before implementing it.

**Never:**
- No distributed lock, provider-specific locking primitive (Postgres advisory locks, `SELECT ... FOR UPDATE`), or external coordination service — out of scope given single-replica topology.
- Don't touch `PatternDetectiveCalculator`'s windowing/calculation logic itself.
- No eviction/cleanup for the per-household lock dictionary — Household count is small and bounded.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Concurrent same-household triggers | `RecomputeAsync` fired for household H from two call sites nearly simultaneously | Both run to completion sequentially, never overlapping; the `StatusSnapshot` row with the latest `ComputedAtUtc` reflects both triggering writes | N/A |
| Concurrent different-household triggers | `RecomputeAsync` fired for H1 and H2 simultaneously | Both proceed concurrently, unblocked by each other | N/A |
| Idle household, bounded fetch | `MainMeter`'s most recent reading is 500 days old | Same Status as an unbounded fetch (window is relative to the last reading, not now) | N/A |
| Long-lived household, bounded fetch | Household has 5+ years of readings | Only the trailing window is fetched; `PaceToDateKwh`/`BaselineToDateKwh`/`Status` match an unbounded fetch exactly | N/A |
| Open prompt, stale | Open prompt's triggering reading is older than the base window (readings kept arriving after it) | Fetch widens to include it; results identical to an unbounded fetch — no exception, no silent data loss | N/A |
| Exception mid-lock | `GetCurrentStatus.ExecuteAsync` or `SaveChangesAsync` throws inside the locked section | Lock is released regardless (no deadlock for the next caller); exception still caught and logged as today | Logged, swallowed |

</frozen-after-approval>

## Code Map

- `src/EnergyTracker.Infrastructure/Adapters/StatusRecomputeService.cs` -- wrap the existing body in the per-household lock; keep acquisition itself inside the catch-and-log boundary, using the *same* `when (ex is not OperationCanceledException)` filter as the body's existing catch (round-2 finding: the acquisition catch logged cancellation as an error, inconsistent with the body's catch a few lines below)
- `src/EnergyTracker.Application/Ports/IHouseholdRecomputeLock.cs` (new) -- port: `Task<IAsyncDisposable> AcquireAsync(Guid householdId, CancellationToken)`
- `src/EnergyTracker.Infrastructure/Adapters/HouseholdRecomputeLock.cs` (new) -- singleton `ConcurrentDictionary<Guid, SemaphoreSlim>` adapter with a bounded acquisition wait (~10s, not 30s — this is a worst-case ceiling that holds a pooled DB connection open while waiting, so keep it tight); comment noting the `maxReplicas=1` coupling
- `src/EnergyTracker.Api/Program.cs` -- `AddSingleton<IHouseholdRecomputeLock, HouseholdRecomputeLock>()`
- `src/EnergyTracker.Application/Ports/IMeterReadingRepository.cs` -- rename `GetAllByMainMeterAsync` → `GetRecentByMainMeterAsync(Guid mainMeterId, int windowDays, Guid? mustIncludeReadingId, CancellationToken)`; sole caller is `GetCurrentStatus`
- `src/EnergyTracker.Infrastructure/Adapters/MeterReadingRepository.cs` -- **(Round-4, correcting a false round-3 claim) the cutoff formula MUST be exactly `cutoff = Min(latestTimestamp, mustIncludeTimestamp ?? latestTimestamp) - windowDays`** — i.e. `windowDays` is subtracted AFTER taking the min of the two anchors, never applied to only one of them and never dropped for the must-include branch. Round 3's bug: it set `cutoff = mustIncludeTimestamp` (the anchor's bare instant, no margin subtracted at all) whenever that was earlier than the base cutoff — so the widen fetched only the anchor reading itself, not a trailing window behind it, and `ComputePaceToDate` collapsed to `null` every time the widen actually triggered. Round 2's formula (`OR` of two independently-margined thresholds) was mathematically correct in shape; round 3 threw that away while fixing the *id* bug. Fix the doc comment on `IMeterReadingRepository.GetRecentByMainMeterAsync` too — it currently documents the wrong (single-point) behavior as intended. Add a one-line comment noting the must-include lookup is PK-filtered (at most one row) so a reader doesn't mistake `FirstOrDefault()` for "first of many," and filter that lookup on `MainMeterId` too, not just `Id` (defensive, matches `ExcludeFromOpenPrompt`'s own defensiveness a few lines away).
- `src/EnergyTracker.Application/GetCurrentStatus.cs` -- fetch the open prompt *before* the reading fetch; call the bounded method with `windowDays: 400`, passing **`openPrompt?.PreviousMeterReadingId`** as `mustIncludeReadingId` — **not** `openPrompt?.MeterReadingId` (round-2's bug: it passed the trigger reading's own id, but `PatternDetectiveCalculator.ComputePaceToDate` actually windows 365 days behind `includedReadings[^1]`, which after `ExcludeFromOpenPrompt` is the reading *immediately preceding* the trigger — i.e. `PreviousMeterReadingId`, an existing field already on `MeterRegressionPrompt`). Since `PreviousMeterReadingId`'s timestamp is always ≤ the trigger's own timestamp by definition, anchoring on it also automatically keeps the trigger reading itself in the fetched set (needed so `ExcludeFromOpenPrompt` doesn't throw). Extract `windowDays: 400`/its "365+35 margin" reasoning into one shared named constant instead of a literal duplicated across call site and tests (already done in round 3, keep it). Rename the stale `allReadings` result variable (already done in round 3, keep it).
- `src/EnergyTracker.Infrastructure/Configurations/MeterReadingConfiguration.cs` + migration via `scripts/add-migration.sh` -- composite index on `(MainMeterId, ReadingTimestamp)`, both providers
- `tests/EnergyTracker.Infrastructure.Tests/HouseholdRecomputeLockTests.cs` (new) -- serialization/non-blocking unit tests using a deterministic sync primitive (`TaskCompletionSource` or similar) as the actual proof; if any `Task.Delay` remains, it must be provably decorative (a sanity check layered on top of the deterministic assertion, never the sole proof) — say so in a comment
- `tests/EnergyTracker.Infrastructure.Tests/StatusRecomputeServiceTests.cs` (new) -- real lock + real service concurrency test (not a mocked lock); same deterministic-primitive rule as above
- `tests/EnergyTracker.Application.Tests/GetCurrentStatusTests.cs` -- update mocks to the new signature; add idle-household and long-history tests. **Do NOT rely solely on a mocked `GetRecentByMainMeterAsync` for the widen scenario** — round 3's mocked test asserted a result the real repository could never produce (it stubbed a return value inconsistent with the corrected cutoff formula) and passed anyway, masking the actual bug. Mocked tests here may only assert *which arguments* `GetCurrentStatus` passes through; they must never assert the *contents* of what a hypothetical fetch returns.
- `tests/EnergyTracker.Infrastructure.Tests/MeterReadingRepositoryTests.cs` (new) -- Testcontainers coverage (Postgres + SqlServer). **Required cases, against the real DB:** (a) a reading strictly before the must-include anchor's timestamp but within `windowDays` of it is included; (b) a reading more than `windowDays` before the anchor is excluded; (c) a gap of 60+ days between the anchor and the base-window's own latest-reading cutoff still yields a correctly widened result. **Plus one new end-to-end test** (new file or an addition here, whichever fits the existing test project layout) **that chains the real `GetRecentByMainMeterAsync` output through the real `PatternDetectiveCalculator.ExcludeFromOpenPrompt` and `ComputePaceToDate`** for an open prompt whose `PreviousMeterReadingId` reading sits outside the base window with genuine older history behind it — asserting a defined, non-null `PaceToDateKwh` that matches what an unbounded fetch would produce. This is the test that would have caught round 3's bug; a test that only inspects the raw fetched row set, or that mocks the repository, does not satisfy this requirement.

## Tasks & Acceptance

**Execution:**
- [x] `IHouseholdRecomputeLock.cs` -- add port -- follows the `IUnitOfWork` precedent (Story 2.8): thin interface, one adapter
- [x] `HouseholdRecomputeLock.cs` -- add singleton adapter with a ~10s bounded acquisition wait + unit tests
- [x] `Program.cs` -- register the singleton
- [x] `StatusRecomputeService.cs` -- acquire the lock around the existing body; acquisition catch uses the same `not OperationCanceledException` filter as the body's catch
- [x] `IMeterReadingRepository.cs` + `MeterReadingRepository.cs` -- bounded, widen-capable `GetRecentByMainMeterAsync` with the corrected cutoff formula: `Min(latestTimestamp, mustIncludeTimestamp ?? latestTimestamp) - windowDays`
- [x] `MeterReadingConfiguration.cs` + migration -- composite index, both providers
- [x] `GetCurrentStatus.cs` -- reorder open-prompt fetch first, pass `openPrompt?.PreviousMeterReadingId` (not `MeterReadingId`) as `mustIncludeReadingId`, named constant for `windowDays`, rename `allReadings`
- [x] `GetCurrentStatusTests.cs` -- update mocks (argument-only assertions for the widen scenario, never mocked return contents); add idle-household/long-history tests
- [x] `StatusRecomputeServiceTests.cs` -- real lock + real service concurrency test
- [x] `MeterReadingRepositoryTests.cs` -- Testcontainers coverage (Postgres + SqlServer): within-window-of-anchor inclusion, beyond-window-of-anchor exclusion, 60+ day base-to-anchor gap, **plus the real-pipeline end-to-end test** (repository → `ExcludeFromOpenPrompt` → `ComputePaceToDate`) proving a defined result matching an unbounded fetch

**Acceptance Criteria:**
- [x] Given two `RecomputeAsync` calls for the same `HouseholdId` fired concurrently against the real lock (not a mock), when both complete, then the `StatusSnapshot` row with the latest `ComputedAtUtc` reflects both triggering writes. -- `StatusRecomputeServiceTests.Two_concurrent_RecomputeAsync_calls_for_the_same_household_never_overlap_and_both_writes_land` (real `HouseholdRecomputeLock` + real `StatusRecomputeService` against Testcontainers Postgres; deterministic event-order proof, not timing-based, plus asserts 2 distinct `StatusSnapshot` rows land).
- [x] Given `RecomputeAsync` calls for two different `HouseholdId`s fired concurrently, when both run, then neither blocks on the other's lock. -- `StatusRecomputeServiceTests.RecomputeAsync_calls_for_two_different_households_never_block_each_other` and `HouseholdRecomputeLockTests.Acquisitions_for_different_households_never_block_each_other`.
- [x] Given a household whose `MainMeter`'s most recent reading is older than the bounded window, when `GetCurrentStatus.ExecuteAsync` runs, then it returns the same result as before this change. -- `GetCurrentStatusTests.An_idle_household_whose_last_reading_is_500_days_old_still_computes_the_same_result_as_an_unbounded_fetch`.
- [x] Given a household with 5+ years of history, when `GetCurrentStatus.ExecuteAsync` runs, then `PaceToDateKwh`/`BaselineToDateKwh`/`Status` match what an unbounded fetch would have produced. -- `GetCurrentStatusTests.A_household_with_5_plus_years_of_history_matches_what_an_unbounded_fetch_would_produce`.
- [x] Given an open `MeterRegressionPrompt` whose `PreviousMeterReadingId` reading is older than the base window, **and genuine additional reading history exists within `windowDays` before that anchor reading**, when `GetCurrentStatus.ExecuteAsync` runs, then that additional history is fetched and included, and `PaceToDateKwh`/`Status` are defined and match what an unbounded fetch would have produced — no exception, no silently-undefined Status. (Note: this is independent of the gap size between `PreviousMeterReadingId` and the triggering `MeterReadingId` — round 3's bug was gap-independent, unlike round 2's.) -- `PostgresMeterReadingRepositoryTests.Bounded_fetch_through_ExcludeFromOpenPrompt_and_ComputePaceToDate_matches_what_an_unbounded_fetch_would_have_produced`, the real end-to-end pipeline test against Testcontainers Postgres (real `GetRecentByMainMeterAsync` -> real `ExcludeFromOpenPrompt` -> real `ComputePaceToDate`); asserts a defined, non-null `PaceToDateKwh` (1000 kWh over 200 days) exactly matching a hand-computed unbounded-fetch equivalent. See the dev report for the hand-worked arithmetic.
- [x] Given the new migration, when `PostgresMigrationTests`/`SqlServerMigrationTests` run, then both pass. -- both apply the full migration set (including `AddMeterReadingMainMeterReadingTimestampIndex`) via `Database.MigrateAsync`; both green in the full suite run.

## Spec Change Log

- **Round 1 (intent_gap, resolved by Ralf 2026-08-24):** reviewers found the 400-day fetch, anchored to the `MainMeter`'s raw latest reading, ignores that `ComputePaceToDate` actually anchors its 365-day window to the *last included* reading (post `ExcludeFromOpenPrompt`). Since readings keep arriving while a prompt stays open (no auto-resolve/age cap), staleness could exceed the 35-day margin and either silently drop data or crash `ExcludeFromOpenPrompt`. **Resolution:** dynamic widen — the query takes an optional "must include" reading id and widens its cutoff to cover it when needed. **KEEP:** the lock design and single-replica justification, the 400/365-day margin, and "bound from last reading, not `UtcNow`" all held up unchanged.
- **Round 1 patches:** stale `allReadings` name; lock needs a bounded wait with acquisition inside the catch-and-log boundary; concurrency ACs need a real-lock test, not a mock; lock test should prefer a deterministic primitive over `Task.Delay`.
- **Round 2 (bad_spec, root cause in this spec's own Code Map wording, no Ralf renegotiation needed):** round 2 passed `openPrompt.MeterReadingId` (the *trigger*) as the must-include anchor, but `ComputePaceToDate` actually needs everything back from `PreviousMeterReadingId` (the *last included* reading) — the trigger and its predecessor can be arbitrarily far apart (`FindImmediatelyPrecedingAsync` has no time-proximity check), so a >35-day gap between them silently dropped data the calculation needed, collapsing `includedReadings` and returning an undefined Status instead of matching an unbounded fetch. Confirmed empirically against live Postgres in review. **Resolution:** anchor on `PreviousMeterReadingId` instead — its timestamp is always ≤ the trigger's, so this one field swap covers both what `ComputePaceToDate` needs *and* keeps the trigger itself fetched (avoiding round 1's crash too). No change needed to the repository query itself. **KEEP:** everything else from round 2 (lock timeout/catch design, index, query shape) held up.
- **Round 2 patches:** acquisition catch should use the same cancellation-exclusion filter as the body's catch (was logging benign cancellation as an error); lower the lock timeout from 30s to ~10s (holds a pooled DB connection while waiting); extract `windowDays: 400` to a shared named constant; clarify the PK-filtered `FirstOrDefault()` in the widen query; round-2 tests never exercised a >35-day gap — the exact bug's reproduction case — so round 3 must add it explicitly.
- **Round 3 (bad_spec, root cause in this spec's own Code Map wording — again — no Ralf renegotiation needed):** round 3 correctly fixed round 2's wrong-anchor bug, but while rewriting the query, set `cutoff = mustIncludeTimestamp` (the anchor's bare timestamp) instead of `mustIncludeTimestamp - windowDays` — no trailing margin at all in the widen branch. Round 2's own query shape (margin subtracted on *both* anchors, combined via `OR`) was correct and got discarded during the rewrite. Effect: whenever the widen actually triggered, only the single anchor reading was fetched, `includedReadings` collapsed to it alone, and `ComputePaceToDate` returned `null` (undefined Status) instead of a defined result matching an unbounded fetch — for genuinely real history that existed. Confirmed independently by two reviewers reading the code directly (not just testing), since round 3's own new regression test was a false positive: it mocked `GetRecentByMainMeterAsync` to return a result the real (buggy) implementation could never produce, so the test asserted correctness that didn't exist. **Resolution:** the Code Map now states the cutoff formula explicitly (`Min(latest, mustInclude ?? latest) - windowDays`) instead of describing it in prose, and test requirements now forbid mocking the *contents* of a `GetRecentByMainMeterAsync` result for the widen scenario — only a real end-to-end pipeline test (or a real Testcontainers repository test) can prove this class of bug is fixed. **KEEP:** the `PreviousMeterReadingId` anchor choice (round 2's fix) was correct and unaffected by this bug — do not revert to `MeterReadingId`.
- **Round 4 (implementation, no spec change needed):** implemented the round-3 resolution exactly as specified. Independently verified by both reviewers reading the corrected code directly; Blind Hunter additionally *deliberately reintroduced round 3's exact bug shape* (dropped the widen branch's margin) and confirmed 5 tests fail, including the new real end-to-end pipeline test — positive proof the test suite actually guards this class of regression now, not another false positive. Both reviewers found only low-severity, non-blocking findings (test-coverage gaps at input-space edges never previously exercised — `mustIncludeTimestamp` not older than `latestTimestamp`, exact cutoff-boundary equality; a defensive-fallback path confirmed unreachable by construction; query round-trip count; lock-topology assumption undocumented as guarded). All **patched directly** (2 new boundary-condition tests added to `MeterReadingRepositoryTests.cs`, verbose round-1/2/3 narrative trimmed out of shipped production comments — that history now lives here instead — remainder appended to `deferred-work.md`) without another full revert/re-implement cycle, since none required a code change to already-verified-correct logic. Full suite: 372/372 passing (up from 368 — the 2 new tests × 2 providers), `dotnet build` clean in Debug and Release.

## Design Notes

- **Why an in-process lock is safe here:** `infra/modules/container-app.bicep` hardcodes `maxReplicas = 1` — this app never runs more than one instance. If that ever changes, this lock stops being sufficient (would need a Postgres/SqlServer-portable distributed lock instead) — flag that coupling in a comment at `HouseholdRecomputeLock`'s definition.
- **Why bound from the last reading, not from now:** `PatternDetectiveCalculator.ComputePaceToDate` windows internally to 365 days from the *last reading's own timestamp*. Bounding the DB fetch from `DateTimeOffset.UtcNow` instead would silently return zero rows for an idle household whose last reading predates the cutoff.
- **Why the widen anchors on `PreviousMeterReadingId`, not `MeterReadingId`:** `ExcludeFromOpenPrompt` returns everything *before* the trigger, so the last element `ComputePaceToDate` actually windows from is the reading immediately preceding the trigger, not the trigger itself. Anchoring the widen there (rather than on the trigger) is the only anchor point that's provably correct regardless of how large the gap between the two readings is — no fixed margin over the trigger's own timestamp can be proven sufficient, since that gap is unbounded.

## Verification

**Commands:**
- `dotnet test` -- all green, including new `HouseholdRecomputeLockTests` and `MeterReadingRepositoryTests`, and the updated `GetCurrentStatusTests` mock setups
- `dotnet build` (Debug + Release) -- clean
- `scripts/add-migration.sh` output, applied via the Postgres + SqlServer Testcontainers migration tests -- both pass

## Suggested Review Order

**The bug this whole spec exists to fix (start here)**

- Open prompt fetched before the reading fetch, widen anchor chosen — `PreviousMeterReadingId`, not the trigger's own id
  [`GetCurrentStatus.cs:64`](../../src/EnergyTracker.Application/GetCurrentStatus.cs#L64)

**Bounded/widened history query — took 4 review rounds to get the arithmetic right**

- `GetRecentByMainMeterAsync` entry point — bounded fetch replacing the old unbounded `GetAllByMainMeterAsync`
  [`MeterReadingRepository.cs:93`](../../src/EnergyTracker.Infrastructure/Adapters/MeterReadingRepository.cs#L93)
- The corrected cutoff formula itself — `Min(latest, mustInclude) - windowDays`, margin applied after the min, not before
  [`MeterReadingRepository.cs:126`](../../src/EnergyTracker.Infrastructure/Adapters/MeterReadingRepository.cs#L126)
- Port signature + corrected doc comment describing the formula
  [`IMeterReadingRepository.cs:40`](../../src/EnergyTracker.Application/Ports/IMeterReadingRepository.cs#L40)

**Per-household serialization — the concurrent-recompute race fix**

- `RecomputeAsync` wraps its existing read-then-write body in the new lock
  [`StatusRecomputeService.cs:14`](../../src/EnergyTracker.Infrastructure/Adapters/StatusRecomputeService.cs#L14)
- Lock acquisition sits inside the same catch-and-log boundary as the body, so a timeout/cancellation can't fail an already-committed write
  [`StatusRecomputeService.cs:26`](../../src/EnergyTracker.Infrastructure/Adapters/StatusRecomputeService.cs#L26)
- Bounded (~10s) acquisition wait — long enough to ride out normal contention, short enough not to hold a pooled DB connection indefinitely
  [`HouseholdRecomputeLock.cs:24`](../../src/EnergyTracker.Infrastructure/Adapters/HouseholdRecomputeLock.cs#L24)
- Port
  [`IHouseholdRecomputeLock.cs:4`](../../src/EnergyTracker.Application/Ports/IHouseholdRecomputeLock.cs#L4)
- Singleton DI registration — correctness depends on this app's `maxReplicas = 1`
  [`Program.cs:301`](../../src/EnergyTracker.Api/Program.cs#L301)

**Schema**

- Composite `(MainMeterId, ReadingTimestamp)` index supporting the bounded query
  [`MeterReadingConfiguration.cs:56`](../../src/EnergyTracker.Infrastructure/Configurations/MeterReadingConfiguration.cs#L56)

**Tests**

- The real end-to-end pipeline test — repository → `ExcludeFromOpenPrompt` → `ComputePaceToDate` against Testcontainers Postgres; this is the one that actually proves the fix (round 3's equivalent was a mocked false positive)
  [`MeterReadingRepositoryTests.cs:231`](../../tests/EnergyTracker.Infrastructure.Tests/MeterReadingRepositoryTests.cs#L231)
- Real lock + real service concurrency proof, not a mocked lock
  [`StatusRecomputeServiceTests.cs:80`](../../tests/EnergyTracker.Infrastructure.Tests/StatusRecomputeServiceTests.cs#L80)
- Lock behavior in isolation (serialization, non-blocking across households, timeout)
  [`HouseholdRecomputeLockTests.cs:6`](../../tests/EnergyTracker.Infrastructure.Tests/HouseholdRecomputeLockTests.cs#L6)
- `GetCurrentStatus` passes the correct anchor id — argument-only assertion, deliberately never mocks widen-scenario return contents
  [`GetCurrentStatusTests.cs:181`](../../tests/EnergyTracker.Application.Tests/GetCurrentStatusTests.cs#L181)
