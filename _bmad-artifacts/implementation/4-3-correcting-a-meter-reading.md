---
baseline_commit: 9cab26f7a5c00cb4e1234ad49062676a1518003f
---

# Story 4.3: Correcting a Meter Reading

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want to correct a Meter Reading I entered incorrectly, with the original value preserved and Status history brought up to date,
so that a mistake doesn't leave my trend permanently wrong or silently hidden.

## ⚠️ Scope Reality Check — Read This First

This story reads as a 4-AC feature but **three of the four ACs are already fully implemented** by Story 2.8 ("Meter Reading History View") and absorbed into Story 4.1's Trend History surface. Do not rebuild them. The **only net-new production code this story adds is AC #3** — wiring a Status recompute call into the existing `EditMeterReading` use case.

| AC | Status | What's actually needed |
|----|--------|-------------------------|
| #1 (audit trail on edit) | ✅ Already built (Story 2.8) | Regression test only |
| #2 (409 on concurrent edit) | ✅ Already built (Story 2.8) | Regression test only |
| #3 (Status recompute on correction) | ❌ Net-new | Production code (Task 1) |
| #4 (edit doesn't touch open regression prompt) | ✅ Already true (Story 2.8, Task 6 deliberately excluded this interaction) | Regression test only |

**Do not create a new use case, new endpoint, new dialog, or new entity.** `EditMeterReading.cs`, `PUT /api/meter-readings/{id}`, and `EditMeterReadingDialog.tsx` all exist today and are correct for AC #1/#2/#4 — extend, don't replace.

## Acceptance Criteria

(Verbatim from `_bmad-artifacts/planning/epics/epic-4-trend-history-per-plug-insight.md`, "Reuses Story 2.8" block, lines ~82-106.)

1. **Given** an existing Meter Reading, browsed via Trend History (Story 4.1), **when** I edit its value, **then** the original value is preserved and shown as a visible correction note alongside the edit, never silently overwritten, via the shared `AuditCorrection` mechanism (NFR8, AD-11).
2. **Given** two Household members editing the same Meter Reading concurrently, **when** both submit, **then** the second writer receives a 409 conflict rather than silently overwriting the first (AD-4, NFR10).
3. **Given** a corrected Meter Reading, **when** the correction is saved, **then** `IStatusRecomputeService` (Story 2.4) is called once, the same way it already is after every new Meter Reading, so that a fresh Status snapshot reflecting the corrected value is computed and persisted going forward — existing `StatusSnapshot` rows are never rewritten, per their documented immutable/insert-only design (AD-7, NFR9).
4. **Given** a Meter Reading that is currently excluded from baseline computation by an unresolved regression prompt (Story 2.3), **when** it is edited, **then** the edit does not bypass or resolve the open regression prompt — the reading remains excluded until the prompt is classified.

## Why AC #3 Reads the Way It Does

An earlier draft of this AC said "the affected `StatusSnapshot` rows are updated... history before the corrected reading is left untouched," which described a multi-row, as-of-a-past-date recompute-and-rewrite that doesn't exist in this codebase and would contradict `StatusSnapshot`'s documented immutable/insert-only design (AD-7/NFR9) — there's no FK from `StatusSnapshot` back to the triggering `MeterReading`, and `IStatusRecomputeService.RecomputeAsync` takes no "as of" parameter; it only computes Status as of now and appends one new snapshot. This tension was flagged unresolved in `deferred-work.md` (lines 159-160) and the Epic 3 Retro. **Ralf resolved it 2026-09-10: reword the AC to match the buildable, single-call behavior** (this file and the epic source have both been updated accordingly) rather than build new as-of-date recompute machinery. Do not build a "walk forward and rewrite each StatusSnapshot row" mechanism, an as-of-date `GetCurrentStatus` variant, or a new FK from `StatusSnapshot` to `MeterReading` — none of that is in scope.

## Tasks / Subtasks

- [x] **Task 1: Wire Status recompute into `EditMeterReading`** (AC: #3)
  - [x] In `src/EnergyTracker.Application/EditMeterReading.cs`, add `IStatusRecomputeService statusRecomputeService` as a 4th primary-constructor parameter (matches `CreateMeterReading`'s constructor-injection style).
  - [x] `ExecuteAsync` currently ends with `return await unitOfWork.ExecuteInTransactionAsync(...)` as its final statement — this needs restructuring, not just appending a line. Capture the transaction's result in a local variable (e.g. `var updated = await unitOfWork.ExecuteInTransactionAsync(...)`), then call `await statusRecomputeService.RecomputeAsync(householdId, cancellationToken)`, then `return updated;` — same placement style, unconditional-after-a-real-change, as `CreateMeterReading.cs:100`.
  - [x] Do **not** call it when the existing no-op guard (`if (oldValue == kwhValue) return reading;`) short-circuits — a no-op save must not trigger a recompute, matching the existing no-op-skip-write discipline already in the method.
  - [x] Do **not** put the recompute call inside the `ExecuteInTransactionAsync` callback — mirror `CreateMeterReading`, where recompute runs as a separate step after the core write, not wrapped in the same DB transaction.
  - [x] No new DI registration needed — `IStatusRecomputeService` is already registered in `Program.cs` (consumed by `CreateMeterReading` today); constructor injection resolves it automatically.
  - [x] Update the class's `/// <summary>` doc comment to mention the recompute (one line, matching the project's single-line XML-doc convention) — see `CreateMeterReading.cs`'s summary for the style to match.

- [x] **Task 2: Regression-prove AC #1, #2, #4 on the already-shipped code path** (AC: #1, #2, #4) — no production code expected; this task is test-only.
  - [x] In `tests/EnergyTracker.Application.Tests/EditMeterReadingTests.cs`, confirm (add a test if missing) that a real value change calls `IAuditCorrectionRecorder.RecordAsync` with the original value preserved as `OldValue` (AC #1).
  - [x] Confirm (add a test if missing) that editing with a stale `expectedVersion` surfaces the existing `MeterReadingConcurrencyConflictException` → 409 mapping, and that the second writer's request never overwrites the first writer's committed value (AC #2). `tests/EnergyTracker.Api.Tests/MeterReadingEndpointsTests.cs` already has `PUT_meter_readings_id_...` conflict cases — verify coverage, extend if a gap exists.
  - [x] Add an explicit regression test proving that editing a `MeterReading` which has an open `MeterRegressionPrompt` (per AD-12) does **not** call `IMeterRegressionPromptRepository` at all and does not resolve/close the prompt (AC #4). This is currently true by omission (Story 2.8's `EditMeterReading` never references `IMeterRegressionPromptRepository`) — the test exists to make that omission a proven, locked-in behavior rather than an accident that a future change could silently break.

- [x] **Task 3: Add recompute-call assertions to existing tests** (AC: #3)
  - [x] In `tests/EnergyTracker.Application.Tests/EditMeterReadingTests.cs`, add an NSubstitute assertion that a real value change results in `statusRecomputeService.Received(1).RecomputeAsync(householdId, Arg.Any<CancellationToken>())` — mirror the assertion style already used for `IAuditCorrectionRecorder` in this file, and the style `CreateMeterReadingTests.cs` uses for its own `RecomputeAsync` assertion.
  - [x] Add a companion test that a no-op save (`kwhValue == oldValue`) results in `statusRecomputeService.DidNotReceive().RecomputeAsync(...)`.
  - [x] In `tests/EnergyTracker.Api.Tests/MeterReadingEndpointsTests.cs`, extend the existing successful-edit round-trip test to assert a new `StatusSnapshot` row appears (via `GetStatusHistory`/the Trend History read path, or a direct DB check consistent with how this test file already verifies persisted side effects) after a real correction — this is the end-to-end proof that AC #3's "recompute forward" reaches the database, not just the mocked unit boundary.

- [x] **Task 4: Verify no frontend change is needed** (AC: #3)
  - [x] Confirm `web/src/components/trend-history/trend-chart.tsx` (or whatever component reads Status/pace history) already re-fetches on next load/navigation rather than caching stale data indefinitely — if so, no frontend code change is needed for AC #3; the existing `edit-meter-reading-dialog.tsx` save-then-refresh flow already surfaces the new recompute's effect on next fetch.
  - [x] If a genuine staleness gap is found (e.g. the trend chart holds cached Status data in memory that isn't invalidated after a correction save), file it as a small, explicitly-scoped fix — do not build new client-side cache-invalidation infrastructure beyond what's needed to refresh the one view.

- [x] **Task 5: Housekeeping**
  - [x] Update or retire the `deferred-work.md` entry (around line 159) noting "Editing a Meter Reading's value... doesn't trigger a Status recompute" — this story resolves it.
  - [x] Run the full regression bar: backend `dotnet test` (baseline: 440 tests green as of Story 4.2) and frontend `npm test` + `tsc -b` + `oxlint` (baseline: 237 tests green) — both must stay green with no reduction in count other than intentional additions.

## Dev Notes

- **This is a small, surgical story.** One constructor parameter, one method call, and a batch of regression tests. Resist the urge to build additional infrastructure (new DTOs, new endpoints, new frontend components) — everything except the one `RecomputeAsync` call already exists and is correct.
- **AD-7 call-site count changes from 2 to 3.** AD-7's own prose in `invariants-rules.md` currently says "exactly one application service... the Meter-Reading-create handler and the Smart-Plug-import-completion handler both call into it" — naming only two call sites. After this story, `EditMeterReading` becomes a third. This is a **within-scope extension** of an existing pattern (calling an existing port from a new place), not a new architecture decision — no AD amendment is needed for this part, only for the (out-of-scope) multi-row-rewrite interpretation discussed above.
- **AD-11's `AuditCorrection` mechanism is entity-agnostic already** — `EntityType`/`FieldName` are plain strings, `EntityId` has no FK (deliberately polymorphic for future entity types like Tariff). Nothing to change here.
- **AD-12's regression-prompt exclusion is computed, not a stored flag** — "open" means `IMeterRegressionPromptRepository.GetOpenForHouseholdAsync` returns an unresolved prompt ordered by the triggering reading's `ReadingTimestamp`. `EditMeterReading` must continue to never query or touch this repository — Task 2's regression test locks this in.
- **Transaction discipline:** `EditMeterReading` already wraps its value-update + `AuditCorrection` write in `IUnitOfWork.ExecuteInTransactionAsync` (established in Story 2.8, reaffirmed as the pattern to reuse in the most recent commit `fa77aef` — "the same port `EditMeterReading` already uses"). The new `RecomputeAsync` call goes **after** that transaction commits, matching `CreateMeterReading`'s placement — do not fold it into the same transaction.
- **Locale/i18n:** no new user-facing strings are introduced by this story (the dialog, list, and correction note all already exist and are already localized). If Task 4 does surface a genuine frontend fix, any new string must go through the existing i18next catalogs with byte-for-byte `en-US`/`de-DE` key parity, per this repo's established (and repeatedly review-caught) convention.
- **No new NuGet/npm packages, no new libraries.** This story is pure composition of existing ports/services — skip any web research for library versions.

### Project Structure Notes

- All backend changes are confined to `src/EnergyTracker.Application/EditMeterReading.cs` (UPDATE, not NEW) — flat use-case-per-file convention, no feature folders, already respected by the existing file.
- Test changes are confined to `tests/EnergyTracker.Application.Tests/EditMeterReadingTests.cs` and `tests/EnergyTracker.Api.Tests/MeterReadingEndpointsTests.cs` (both UPDATE) — mirrored 1:1 test-file-per-class convention already followed.
- No new files are anticipated for AC #1/#2/#3/#4. If Task 4's frontend check surfaces a real gap, any new/changed file belongs under `web/src/components/trend-history/` or `web/src/components/meter-reading/`, matching the existing per-feature grouping — do not introduce a new top-level component folder for this.
- No new API route, no new DTO, no new EF Core migration — `MeterReadingResponse`/`MeterReadingHistoryItemResponse`/`EditMeterReadingRequest` already carry everything needed.

### References

- [Source: _bmad-artifacts/planning/epics/epic-4-trend-history-per-plug-insight.md] — Story 4.3 definition, ACs, "Reuses Story 2.8" scope note (lines ~82-106); Epic 4 objective/business value, FR-8/FR-9/FR-31, NFR8/NFR9/NFR10/NFR15, AD-4/AD-7/AD-10/AD-11/AD-14, UX-DR6/UX-DR12/UX-DR19.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md] — AD-3 (tenant isolation), AD-4 (optimistic concurrency), AD-7 (compute-at-request-time / StatusSnapshot, exactly-one-service), AD-11 (shared audit-correction mechanism), AD-12 (regression queue, at-most-one-open-prompt), AD-14 (Main Meter sole authority), AD-18 (locale-neutral storage).
- [Source: _bmad-artifacts/implementation/2-8-meter-reading-history-view.md] — origin of `MeterReading.Version`, `AuditCorrection`/`IAuditCorrectionRecorder`, `EditMeterReading`, `PUT /api/meter-readings/{id}`, `EditMeterReadingDialog`; Task 6's explicit "deliberately out of scope: recompute, regression-prompt interaction" note that this story now partially revisits (AC #3) and partially reaffirms (AC #4).
- [Source: _bmad-artifacts/implementation/4-1-trend-history-view.md] — the absorbing story (status: review) that merged Story 2.8's UI into Trend History; explicitly states it does not touch `EditMeterReading`'s recompute behavior and that this is "explicitly Story 4.3's job."
- [Source: _bmad-artifacts/implementation/epic-3-retro-2026-08-23.md] — "Significant Discovery: Epic 4 Definition Update Required" section; the decision record for why 4.1 absorbed 2.8 and why 4.3's scope is narrowed to the recompute piece.
- [Source: _bmad-artifacts/implementation/deferred-work.md, lines 159-160] — the original flag of the AC #3 immutability tension, resolved by Ralf during this story's creation (see "Why AC #3 Reads the Way It Does" above).
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-trend-history.html] — visual reference for the Meter Readings card, Pending badge, correction note, and the Edit Meter Reading dialog (explicitly labeled in the mockup as reused verbatim from Story 2.8) — no new UX design needed for this story.
- [Source: src/EnergyTracker.Application/EditMeterReading.cs] — the use case this story extends (read in full during story creation).
- [Source: src/EnergyTracker.Application/CreateMeterReading.cs, line 100] — the existing `IStatusRecomputeService.RecomputeAsync` call-site precedent this story mirrors.
- [Source: src/EnergyTracker.Application/Ports/IStatusRecomputeService.cs] — the port's exact signature (`RecomputeAsync(Guid householdId, CancellationToken)`), confirming it has no "as of" parameter.
- [Source: src/EnergyTracker.Domain/StatusSnapshot.cs] — the immutable/insert-only doc comment underpinning the AC #3 scoping decision above.
- [Source: tests/EnergyTracker.Application.Tests/EditMeterReadingTests.cs, tests/EnergyTracker.Application.Tests/CreateMeterReadingTests.cs, tests/EnergyTracker.Api.Tests/MeterReadingEndpointsTests.cs] — test files to extend; existing NSubstitute/Shouldly/Testcontainers conventions to mirror.

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

None — no failures requiring debug-log capture; the story's own scope-narrowing (see "Scope Reality Check" and "Why AC #3 Reads the Way It Does" above) meant implementation was small and surgical as anticipated.

### Completion Notes List

- **Task 1:** Added `IStatusRecomputeService statusRecomputeService` as `EditMeterReading`'s 4th primary-constructor parameter. Restructured `ExecuteAsync` to capture the transaction's result in `updatedReading`, call `statusRecomputeService.RecomputeAsync(householdId, cancellationToken)` after the transaction commits, then return `updatedReading` — mirrors `CreateMeterReading.cs`'s placement exactly (recompute outside the write transaction, unconditional on a real change). The existing no-op guard (`if (oldValue == kwhValue) return reading;`) already short-circuits before this new code, so a no-op save correctly never triggers a recompute. Updated the class's XML-doc summary to mention the recompute. No DI registration change needed. Builds clean, 0 warnings.
- **Task 2:** AC #1 (audit trail) and AC #2 (409 on stale version) were already covered by existing tests (`RecordAsync_is_called_exactly_once_...`, `A_stale_Version_throws_MeterReadingConcurrencyConflictException`). Strengthened the AC #2 API-level test (`PUT_meter_readings_id_with_a_stale_Version_returns_409_and_never_overwrites_the_first_writers_committed_value` in `MeterReadingEndpointsTests.cs`) to simulate a genuine two-writer race (first writer commits, second writer — still holding the pre-edit Version — is rejected) and assert via GET that the first writer's committed value survives untouched, not just that the response is a 409. Added a new API-level regression test for AC #4 (`PUT_meter_readings_id_for_a_reading_under_an_open_regression_prompt_does_not_resolve_the_prompt`) proving that editing the reading an open `MeterRegressionPrompt` tracks leaves that prompt open (via `GET /api/meter-regression-prompts/open`) with the corrected value reflected — `EditMeterReading` has no dependency on `IMeterRegressionPromptRepository` at all, so this is proven at the integration level rather than via an NSubstitute "not called" assertion on a port the class doesn't take.
- **Task 3:** Added `RecomputeAsync_is_called_exactly_once_on_a_real_change` and `A_no_op_save_of_the_same_value_never_calls_RecomputeAsync` to `EditMeterReadingTests.cs`, mirroring `CreateMeterReadingTests.cs`'s own `RecomputeAsync` assertion style. Added `PUT_meter_readings_id_correcting_the_value_persists_a_new_StatusSnapshot_row` to `MeterReadingEndpointsTests.cs` (sets a Yearly Baseline, posts two readings to make Status definite, records the StatusSnapshot count, then edits one reading and asserts the count increased by exactly one) — the end-to-end proof AC #3's recompute reaches the database, not just the mocked unit boundary. Uses the same `IgnoreQueryFilters`/direct-`DbContext` counting pattern as `StatusEndpointsTests.CountStatusSnapshotRowsAsync`.
- **Task 4:** Found a genuine (small) frontend staleness gap: `TrendHistoryPage` fetches `StatusHistoryEntryDto[]` for `TrendChart` once on mount (`useEffect` with `[]` deps) and never re-fetches it; `MeterReadingsCard`'s edit-and-save flow only re-fetches its own paginated list (`load(page)`), with no way to signal its sibling chart to refresh. Within a single page visit, correcting a reading updated the Meter Readings list but left the Trend Chart above it showing stale Status history until the household member navigated away and back. Fixed narrowly: extracted the chart's fetch into a `loadStatusHistory` callback, passed it down to `MeterReadingsCard` as a new optional `onReadingCorrected` prop, and called it (alongside the card's own `load(page)`) in the edit dialog's `onSaved` handler. No new client-side cache-invalidation infrastructure — just one callback wired through one existing prop-passing path. Added a unit test in `meter-readings-card.test.tsx` proving `onReadingCorrected` fires on save, and an integration test in `trend-history-page.test.tsx` proving `/api/status/history` is actually re-fetched after a correction.
- **Task 5:** Marked both `deferred-work.md` entries from the story-2.8 review (the recompute gap and the Story 4.3 scope-tension entry) as `RESOLVED by story-4-3-correcting-a-meter-reading (2026-09-10)`, with a short note on how each was actually resolved, rather than deleting the historical record. Ran the full regression bar: backend `dotnet test` across the whole solution — 475/475 green (was 440 baseline as of Story 4.2; extra pre-existing growth from other in-progress work on this branch, plus this story's own additions) — and frontend `npm test` (254/254 green), `tsc -b` (clean), `oxlint` (clean, only pre-existing unrelated warnings in unrelated files). No regressions, no reduction in test count.

### File List

- `src/EnergyTracker.Application/EditMeterReading.cs` (UPDATE) — AC #3 production code.
- `tests/EnergyTracker.Application.Tests/EditMeterReadingTests.cs` (UPDATE) — Task 2/3 unit tests.
- `tests/EnergyTracker.Api.Tests/MeterReadingEndpointsTests.cs` (UPDATE) — Task 2/3 integration tests.
- `web/src/components/trend-history/trend-history-page.tsx` (UPDATE) — Task 4 staleness fix (extracted `loadStatusHistory`, wired to `MeterReadingsCard`).
- `web/src/components/trend-history/trend-history-page.test.tsx` (UPDATE) — Task 4 regression test.
- `web/src/components/meter-reading/meter-readings-card.tsx` (UPDATE) — Task 4 staleness fix (new `onReadingCorrected` prop, called on save).
- `web/src/components/meter-reading/meter-readings-card.test.tsx` (UPDATE) — Task 4 regression test.
- `_bmad-artifacts/implementation/deferred-work.md` (UPDATE) — Task 5 housekeeping.

### Review Findings

- [x] [Review][Patch] Status History chart can race between overlapping `loadStatusHistory` calls and show stale data after a correction [web/src/components/trend-history/trend-history-page.tsx:31-51,77] — a slower mount-time fetch can resolve after and overwrite a fresher correction-triggered fetch; the correction-triggered call's cleanup closure is also discarded, so an in-flight fetch is not cancelled on unmount. Fixed: replaced the per-call `cancelled` closure with a shared `latestRequestId` ref so only the latest call's response is ever applied, across both call sites; the mount effect's cleanup bumps the id to invalidate any pending response on unmount.
- [x] [Review][Patch] AC #3's DB-level test only proves a new `StatusSnapshot` row was inserted, not that it reflects the corrected value [tests/EnergyTracker.Api.Tests/MeterReadingEndpointsTests.cs:213-237] — only a row-count assertion; a recompute using stale/cached data would still pass. Fixed: added `factory.GetLatestStatusSnapshotAsync(...)` and asserted `PaceToDateKwh` reflects the corrected 2100m total (1100 kWh), not the pre-correction 2050m total.
- [x] [Review][Patch] `CountStatusSnapshotRowsAsync` duplicated verbatim from `StatusEndpointsTests` instead of shared [tests/EnergyTracker.Api.Tests/MeterReadingEndpointsTests.cs:36-42]. Fixed: moved `CountStatusSnapshotRowsAsync` (and a new `GetLatestStatusSnapshotAsync`) onto `EnergyTrackerApiFactory`, removed both private copies, and updated call sites in `StatusEndpointsTests.cs`/`MeterReadingEndpointsTests.cs` to use `factory.*`.
- [x] [Review][Patch] Every dialog save re-fetches Status history even on a true no-op value [web/src/components/meter-reading/meter-readings-card.tsx:164-171] — `EditMeterReadingDialog` has no client-side guard against submitting an unchanged value, so `onReadingCorrected()` fires an unneeded `/api/status/history` call. Fixed: `EditMeterReadingDialog.onSaved` now reports whether the server's returned value actually changed; `MeterReadingsCard` only calls `onReadingCorrected()` when it did (its own `load(page)` re-fetch still always runs, unchanged).
- [x] [Review][Patch] AD-7's architecture-spine prose is now stale — this diff adds `EditMeterReading` as a third `IStatusRecomputeService` call site but `invariants-rules.md`'s AD-7 section still names only two handlers [_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md]. Fixed: updated the AD-7 prose to name all three call sites, noting the edit handler only recomputes on a real value change.
- [x] [Review][Defer] Optimistic-concurrency check is bypassed entirely on the no-op path [src/EnergyTracker.Application/EditMeterReading.cs:32-35] — deferred, pre-existing (Story 2.8, untouched by this diff; a stale `expectedVersion` submitted alongside a value equal to the current value returns silent 200, not 409).
- [x] [Review][Defer] No unit test distinguishes "recompute after commit" vs. "inside the transaction" [tests/EnergyTracker.Application.Tests/EditMeterReadingTests.cs] — deferred, pre-existing (identical gap in `CreateMeterReadingTests.cs`; the `ExecuteInTransactionAsync` NSubstitute stub is a synchronous passthrough either way).
- [x] [Review][Defer] Unhandled `RecomputeAsync` exception after the write transaction already committed [src/EnergyTracker.Application/EditMeterReading.cs:57-61] — deferred, pre-existing (identical, deliberately-mirrored pattern in `CreateMeterReading.cs:100`).

## Change Log

- 2026-09-10: Implemented Story 4.3. Wired `IStatusRecomputeService.RecomputeAsync` into `EditMeterReading` as a third AD-7 call site (AC #3). Regression-proved AC #1/#2/#4 on the already-shipped Story 2.8 code path with strengthened/new tests. Found and fixed a small frontend staleness gap (Trend Chart not refreshing after a Meter Reading correction within the same page visit). Resolved both related `deferred-work.md` entries. Full regression bar green (backend 475/475, frontend 254/254, `tsc -b` clean, `oxlint` clean).
