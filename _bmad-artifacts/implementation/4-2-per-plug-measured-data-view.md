---
baseline_commit: 9ce5fad021d656d8b25a5baad2d496662af8645c
---

# Story 4.2: Per-Plug Measured Data View

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want to view my measured Smart Plug data organized by Room → Power Point → Device,
so that I can see what's actually been measured without it being confused with my Main Meter total.

## Acceptance Criteria

1. **Given** imported Smart Plug data, **when** I open the Per-Plug view, **then** it's organized by the Room → Power Point → Device structure it's tagged to (FR-9).
2. **Given** the Per-Plug view, **when** rendered, **then** it's explicitly presented as measured context, not a reconciled attribution breakdown of the Main Meter total — nothing here is summed against or claims to explain the Main Meter's number (FR-9, AD-14).
3. **Given** a Device or Power Point retagged after Smart Plug data was already imported (Story 3.2's write-time snapshot), **when** the Per-Plug view is displayed, **then** previously imported data stays attributed to the tag that was active at import time — it does not silently move to follow the retag (FR-9, AD-10).
4. **Given** the Room → Power Point → Device tree, **when** displayed, **then** it's an expandable list (shadcn `details`/accordion pattern), collapsed by default, at Moderate density (UX-DR6).

## Tasks / Subtasks

- [x] Task 1: Backend — read-only aggregation repository (AC: #1, #3)
  - [x] Add `SmartPlugReadingAggregate` record and `ISmartPlugReadingRepository` port in `src/EnergyTracker.Application/Ports/ISmartPlugReadingRepository.cs`, with one method: `Task<IReadOnlyList<SmartPlugReadingAggregate>> GetAggregatedByTagAsync(Guid householdId, CancellationToken cancellationToken)`.
  - [x] Implement `src/EnergyTracker.Infrastructure/Adapters/SmartPlugReadingRepository.cs` using `EnergyTrackerDbContext`. Query: `Where(r => r.HouseholdId == householdId && r.PowerPointId != null)` (excludes `AwaitingPowerPointMapping` readings — they have no tag yet), `GroupBy(r => new { r.RoomName, r.PowerPointName, r.DeviceName })`, `Select` a `SmartPlugReadingAggregate(RoomName, PowerPointName, DeviceName, Sum(KwhValue))`. This is a **new, dedicated read-only port** — do not add this method to `ISmartPlugImportRepository` (that port owns the import pipeline's write path + its own gap-detection reads; this is a display-only concern, same separation `IStatusSnapshotRepository` drew from the write side in Story 4.1 Task 1).
  - [x] Group by the **snapshotted-by-value** `RoomName`/`PowerPointName`/`DeviceName` string columns only — never join to the live `Room`/`PowerPoint`/`Device` tables. This is what makes AC #3 (retag doesn't move history) automatically true: `SmartPlugReading` already carries these as denormalized display fields per AD-10, so grouping on them is the only correct way to build this tree.
- [x] Task 2: Backend — use case shaping the nested tree (AC: #1)
  - [x] Add `src/EnergyTracker.Application/GetPerPlugMeasuredData.cs` with a single `ExecuteAsync(Guid householdId, CancellationToken)`, depending only on `ISmartPlugReadingRepository`. Build three nested records: `DeviceMeasuredData(string DeviceName, decimal TotalKwh)`, `PowerPointMeasuredData(string PowerPointName, decimal TotalKwh, IReadOnlyList<DeviceMeasuredData> Devices)`, `RoomMeasuredData(string RoomName, decimal TotalKwh, IReadOnlyList<PowerPointMeasuredData> PowerPoints)`.
  - [x] Group the flat aggregate list from Task 1 into the tree: sum each Power Point's `TotalKwh` from its Devices, sum each Room's `TotalKwh` from its Power Points. Order Rooms, then Power Points within a Room, then Devices within a Power Point, alphabetically by name (ascending, ordinal) — the epic/mockups don't specify an ordering, so pick the deterministic default; call this out as a non-blocking assumption in Completion Notes, same as Story 4.1's precedent for unspecified UI details.
  - [x] No dependency on `IHouseholdRepository` needed — unlike `GetStatusHistory`, this use case needs no Household-level config (no baseline/locale math happens server-side here).
- [x] Task 3: Backend — endpoint (AC: #1)
  - [x] Add `src/EnergyTracker.Api/Endpoints/SmartPlugReadingEndpoints.cs`, mirroring `StatusEndpoints.cs`'s shape: a private `TryGetHouseholdId` helper (copy it — it's intentionally duplicated per-file in this codebase, not shared), and `api.MapGet("/smart-plug-readings", ...)` calling `GetPerPlugMeasuredData.ExecuteAsync`. Always return `200` with a `List<RoomMeasuredDataResponse>` (possibly empty, never null) — same "empty array, not undefined" shape as `/status/history`, since "no Smart Plug data yet" is a legitimate, common state, not an error.
  - [x] Add response DTOs (`RoomMeasuredDataResponse`, `PowerPointMeasuredDataResponse`, `DeviceMeasuredDataResponse`) and mapping functions in the same file, matching `StatusEndpoints.cs`'s `ToHistoryEntryResponse` pattern.
  - [x] Register in `Program.cs`: `builder.Services.AddScoped<GetPerPlugMeasuredData>();` near the other `Get*` use-case registrations (~line 313), and `api.MapSmartPlugReadingEndpoints();` next to `api.MapStatusEndpoints();`/`api.MapSmartPlugImportEndpoints();` (~line 402-403).
- [x] Task 4: Frontend — API client (AC: #1, #2, #4)
  - [x] Add `web/src/lib/smart-plug-reading-api.ts`, mirroring `status-api.ts`'s `ApiError`/`toApiError` shape and a `fetchPerPlugMeasuredData(): Promise<RoomMeasuredDataDto[]>` calling `GET /api/smart-plug-readings` with `credentials: 'include'`. Response is always a JSON array (unlike `fetchStatusHistory`/`fetchCurrentStatus`'s null-body handling) — `await response.json()` directly, no empty-body special case needed.
- [x] Task 5: Frontend — `PerPlugDataCard` component (AC: #1, #2, #4)
  - [x] Add `web/src/components/trend-history/per-plug-data-card.tsx`. Structure per the mockups (`density-trend-history.html`, unchanged reference for the tree; `key-trend-history.html` for card placement): a `GlassCard` containing a static `<h3>` heading (**not** a collapse toggle itself), then one `<details>` per Room (collapsed by default — no `open` attribute), each containing a nested `<details>` per Power Point (also collapsed by default) containing flat device rows (`<div>`, not `<details>` — Devices are leaves, never further nested). Each Room/Power-Point summary shows its name + its own summed kWh; each device row shows its name + its own kWh.
  - [x] This is a **different disclosure shape** than `MeterReadingsCard`'s single outer `details`/`summary` toggle around the whole card — do not wrap the whole tree in one more collapse level on top of the per-Room ones. AC #4's "collapsed by default" describes the Room/Power-Point tree nodes themselves, exactly as built in `density-trend-history.html`.
  - [x] Render the AD-14 caveat text (AC #2) as a fixed, always-visible line under the tree — mirror the mockup's copy intent ("Measured context, not a reconciled attribution of your Main Meter total"), sourced from a new i18n key (Task 7). This is a hard requirement, not cosmetic: AD-14 binds the whole system including every frontend view.
  - [x] Empty state: when the fetched list is empty (no Smart Plug data imported yet), render an empty-state message instead of the tree — same pattern as `TrendChart`'s `<2`-entries empty state.
  - [x] Load-error state: on a fetch failure, render a distinct error message (same `chartLoadError`-style pattern `TrendHistoryPage` already uses for the chart) — do not silently render the empty state on a genuine fetch error.
- [x] Task 6: Frontend — wire into Trend History (AC: #1)
  - [x] In `trend-history-page.tsx`, add `<PerPlugDataCard />` as the **third** item in the `flex flex-col gap-[var(--spacing-card-gap)]` container, after `<MeterReadingsCard />` — this is the exact slot both `trend-history-page.tsx`'s own comment ("Room -> Power Point -> Device tree (Story 4.2) ... stays last, not added here") and `meter-readings-card.tsx`'s own comment ("matching the Room -> Power Point -> Device tree's identical idiom one card below it (Story 4.2)") already mark. Remove/update those two now-stale comments once the card is actually wired in.
  - [x] Fetch data the same way `TrendHistoryPage` fetches `entries` today (`useEffect` + cancelled-flag guard) — or, simpler, let `PerPlugDataCard` own its own fetch internally (matching `MeterReadingsCard`'s self-contained `useCallback`/`useEffect` fetch, not `TrendChart`'s prop-driven data). Prefer the self-contained pattern (`MeterReadingsCard`'s), since nothing else on the page needs this data.
- [x] Task 7: i18n (AC: #2, #4)
  - [x] Add a `trendHistory.perPlugCard` namespace to both `web/src/locales/en-US/translation.json` and `web/src/locales/de-DE/translation.json`, alongside the existing `trendHistory.readingsCard` block: `heading` ("Room → Power Point → Device"), `caveat` (the AD-14 disclaimer), `emptyState`, `loadError`. Verify key-set parity between both locale files (same discipline Story 4.1's Completion Notes called out explicitly verifying).
- [x] Task 8: Tests (AC: all)
  - [x] Backend unit: `tests/EnergyTracker.Application.Tests/GetPerPlugMeasuredDataTests.cs` — `NSubstitute`-mock `ISmartPlugReadingRepository`, `Shouldly` assertions, `Snake_case_with_underscores` method names. Cover: correct nesting/summing across multiple Rooms/Power Points/Devices; a Room with one Power Point with one Device; empty input → empty output.
  - [x] Backend integration: `tests/EnergyTracker.Api.Tests/SmartPlugReadingEndpointsTests.cs`, Testcontainers-backed, mirroring `StatusEndpointsTests.cs`'s conventions (real Postgres/SqlServer per AD-2). Cover: readings with `PowerPointId != null` appear correctly grouped/summed; readings still `AwaitingPowerPointMapping` (`PowerPointId == null`) are excluded; cross-Household isolation (AD-3); a retagged Power Point's historical readings keep their original `RoomName`/`PowerPointName` (AD-10 regression guard — this is the one AC in this story most worth a dedicated integration test, since it's easy to accidentally "fix" by joining live tables instead).
  - [x] Frontend: `web/src/lib/smart-plug-reading-api.test.ts` (fetch mocking, mirroring `status-api.test.ts`); `web/src/components/trend-history/per-plug-data-card.test.tsx` covering collapsed-by-default rendering, expand interaction, the always-visible caveat text, empty state, and load-error state; update `trend-history-page.test.tsx` to assert the card is present and ordered after `MeterReadingsCard`.
  - [x] Full regression: backend solution suite (`dotnet test`, includes `EnergyTracker.Architecture.Tests`) and frontend suite (`npm test`, `tsc -b`, `oxlint`) must stay green — same bar Story 4.1's Completion Notes documented (425 backend / 227 frontend tests, 0 failed).

### Review Findings

- [x] [Review][Decision] Grouping purely by snapshotted string columns lets two distinct Power Points' history silently merge if a later one reuses a name the first one has since been renamed away from — `RenamePowerPoint`'s uniqueness check only guards against *currently live* name collisions in the same Room (`ListPowerPointsAsync` includes archived rows, but not a live PP that has simply been renamed away from the contested name), so renaming PP-A away from "TV Power Point" then renaming/creating PP-B to "TV Power Point" in the same Room is reachable through normal UI actions with no deletion involved. Both PPs' Smart Plug readings would then collapse into one tree node under `SmartPlugReadingEndpoints`'s response, misattributing PP-B's measured data as if it were PP-A's — the exact class of silent-misattribution bug AD-10 exists to prevent, just via name reuse instead of a live FK join. The unambiguous-looking fix (add `PowerPointId` to the `GroupBy` key in `SmartPlugReadingRepository.GetAggregatedByTagAsync`) only partially works: `GetPerPlugMeasuredData`'s tree-building would still re-collapse same-named aggregates at the Power Point level unless it's also reworked to key off `PowerPointId`, and Dev Notes explicitly says to group "by the snapshotted-by-value ... string columns only" — deviating from that literal instruction to close this gap is a product/scope call, not a code-correctness one. — **Resolved: fix now.** `SmartPlugReadingAggregate` gained a `PowerPointId` field; the repository groups by `(PowerPointId, RoomName, PowerPointName, DeviceName)`, and `GetPerPlugMeasuredData` groups Power Points by `(PowerPointId, PowerPointName)` instead of `PowerPointName` alone — this keeps AD-10's per-rename history split intact (same `PowerPointId`, different snapshotted name still stays separate) while two different Power Points sharing a reused name no longer merge. Verified with a new unit test (`Two_different_Power_Points_sharing_the_same_snapshotted_name_stay_as_separate_tree_nodes`) and a new integration test reproducing the exact reachable rename-then-reuse sequence through real endpoints (`Two_different_Power_Points_that_end_up_sharing_the_same_name_via_reuse_are_not_merged`). The Room-level equivalent (Room names can be reused the same way) remains an accepted, narrower limitation — `SmartPlugReading` has no `RoomId` column to disambiguate by, so closing that would require a schema change/migration, out of scope for this patch.
- [x] [Review][Patch] `PerPlugDataCard` hardcodes the browser's environment locale instead of the household's configured locale [web/src/components/trend-history/per-plug-data-card.tsx:48] — `new Intl.NumberFormat(undefined, ...)` with no `locale` prop on the component at all, while `TrendHistoryPage` passes `locale` to both sibling cards (`TrendChart`, `MeterReadingsCard`) on the same page. This is precisely the anti-pattern Story 4.1's own review pass fixed once; this story's Dev Notes explicitly say "Apply the same care in `PerPlugDataCard`," and it wasn't applied. Every kWh figure in the tree renders in the visitor's browser locale instead of the household's `Locale` (AD-18). — **Fixed:** added a required `locale` prop, threaded from `TrendHistoryPage`, used in `Intl.NumberFormat(locale, ...)`. Covered by a new test asserting de-DE grouping/decimal formatting.
- [x] [Review][Patch] No loading-state UI while the initial fetch is in flight [web/src/components/trend-history/per-plug-data-card.tsx:54-61] — none of the three render branches (error/empty/loaded) match while `loading` is true, so the card shows only the heading and caveat with a blank gap between them. Sibling `MeterReadingsCard` renders an explicit loading message for the identical scenario via a dedicated i18n key. — **Fixed:** added a `loading` render branch and a new `trendHistory.perPlugCard.loading` i18n key (both locales, parity verified). Covered by a new test.
- [x] [Review][Patch] Room/Power Point/Device rows use plain `<div>`s with no list semantics [web/src/components/trend-history/per-plug-data-card.tsx:62-91] — no `<ul>/<li>` or `role="list"`/`role="listitem"` anywhere in the tree, unlike sibling `MeterReadingsCard`'s semantic `<Table>` for the same kind of "N items with a name and a number" data. A screen-reader user gets an undifferentiated text/number stream at every level with no structural landmarks to navigate the list by. — **Fixed:** Room/Power Point/Device rows now render as `<ul>/<li>` at every level instead of plain `<div>`s.
- [x] [Review][Patch] `StringComparer.Ordinal` sorts Rooms/Power Points/Devices by raw codepoint, not natural order for the shipped de-DE locale [src/EnergyTracker.Application/GetPerPlugMeasuredData.cs:22,27,31] — user-entered free-text names containing ä/ö/ü won't sort where a German-speaking household member expects them (ordinal puts accented letters after `Z`, not next to their base letter). No existing backend precedent was found for skipping culture-aware comparison elsewhere in this codebase. — **Fixed:** swapped `StringComparer.Ordinal` → `StringComparer.InvariantCulture` at all three sort sites. Covered by a new unit test with accented Room names.
- [x] [Review][Patch] AD-10 regression coverage only exercises one of three real retag paths [tests/EnergyTracker.Api.Tests/SmartPlugReadingEndpointsTests.cs] — the integration test renames a Power Point (`PUT /power-points/{id}`) and checks history doesn't move, but never tests renaming a Room (`PUT /rooms/{id}`) or moving a Power Point to a different Room (`PUT /power-points/{id}/room`) — both existing endpoints, and the Room-rename/PP-move cases are arguably the more natural "retag" scenario AC #3 is guarding against. The story's own Dev Notes call this "the one AC in this story most worth a dedicated integration test." — **Fixed:** added `A_renamed_Rooms_historical_readings_keep_their_original_snapshotted_RoomName` and `A_Power_Points_historical_readings_keep_their_original_snapshotted_RoomName_after_being_moved_to_a_different_Room`, covering both previously-untested retag paths.

## Dev Notes

- **This is the tree Story 4.1 deliberately deferred.** Both `trend-history-page.tsx` and `meter-readings-card.tsx` already carry code comments naming this exact story and slot ("Story 4.2" appears literally in both files) — this is not a new integration point to design, it's a marked placeholder to fill in.
- **`SmartPlugReading` has no `Device` entity link.** Per its own doc comment (`src/EnergyTracker.Domain/SmartPlugReading.cs`): "this story only matches at Power Point granularity... no Device entity is resolved." `DeviceName` is a raw string tag parsed from the import file, never joined to the `Device` table. Do not attempt to resolve/join to `Device` — group purely on the three string columns (`RoomName`, `PowerPointName`, `DeviceName`), exactly as `SmartPlugReading` already stores them.
- **AD-10 (historical tag integrity) is the crux of AC #3.** `SmartPlugReading.RoomName`/`PowerPointName` are denormalized snapshots taken at import time (Story 3.2), specifically so a later Room/Power-Point retag doesn't rewrite history. Grouping on these string columns (never on a live FK join to `Room`/`PowerPoint`) is what makes AC #3 true *by construction* — this is the one place in this story a reviewer will specifically check for a live-join shortcut.
- **AD-14 (Main Meter sole authoritative total) binds this whole story.** No response DTO, use case, or component may sum `SmartPlugReading` data into a figure compared against or rendered alongside `MeterReading`/Main Meter totals. The per-Room/Power-Point/Device sums here are fine — they're sums *within* the Smart Plug data's own family, never combined with Main Meter data. The caveat text (AC #2, Task 5) is a hard requirement, not decoration.
- **Repository split precedent (Story 4.1, Task 1):** `IStatusSnapshotRepository`/`StatusSnapshotRepository` were created as a brand-new, narrowly-scoped read port rather than folding a new method into an existing repository. Follow the same shape here: `ISmartPlugReadingRepository` is new and read-only, kept separate from the 17-method `ISmartPlugImportRepository` (which owns the import write-path plus its own gap-detection reads — a different capability).
- **Existing `SmartPlugReadingRepository`-adjacent precedent for the household filter:** `StatusSnapshotRepository.GetForHouseholdAsync` explicitly filters `.Where(s => s.HouseholdId == householdId)` even though AD-3's global query filter already scopes by household — this is the established, reviewed pattern in this codebase (redundant-but-explicit, not a bypass); mirror it rather than relying on the global filter alone.
- **Response shape must always be a (possibly empty) array, never null** — same discipline as `/status/history`. "No Smart Plug data imported yet" is FR-9's normal starting state for every new household, not an error.
- **Frontend tree structure, exact per mockups (`density-trend-history.html`, referenced as "unchanged" by `key-trend-history.html`):** a static `<h3>` heading (not a toggle) + one collapsed `<details>` per Room, nested collapsed `<details>` per Power Point inside, flat (non-`<details>`) rows per Device inside that. This is *not* the same shape as `MeterReadingsCard`'s single outer disclosure — don't add an extra outer collapse wrapper around the whole tree.
- **Moderate density only** — no density toggle, matching UX-DR6 and Story 4.1's identical decision for the trend chart. All stats (per-Room, per-Power-Point, per-Device kWh) are shown once expanded; nothing is hidden behind a further "show more."
- **Route naming:** `/api/smart-plug-readings` was free (existing Smart Plug routes are `/smart-plug-imports`, `/smart-plug-imports/{id}/power-point-mapping`, `/smart-plug-import-jobs`) and reads naturally as the plural-noun convention (Consistency Conventions table).
- **No new migration needed.** `SmartPlugReading` and its `(PowerPointId, IntervalStart)` unique index already exist (Story 3.x/AD-20); this story is read-only against existing data.

### Project Structure Notes

- Backend files land exactly where Story 4.1's equivalents did: `Application/Ports/` for the new port, `Infrastructure/Adapters/` for its implementation, a flat `Application/GetPerPlugMeasuredData.cs` for the use case (no feature-folder nesting, per the project's use-case convention), `Api/Endpoints/SmartPlugReadingEndpoints.cs` for the new endpoint file.
- Frontend: `PerPlugDataCard` goes in `web/src/components/trend-history/` alongside `trend-chart.tsx` (page-scoped component, not extracted from a pre-existing feature folder the way `MeterReadingsCard` was from `meter-reading/`). API client goes in `web/src/lib/smart-plug-reading-api.ts`, matching `status-api.ts`'s naming (entity-name-based, not feature-based).
- No conflicts detected with the unified project structure.

### References

- [Source: _bmad-artifacts/planning/epics/epic-4-trend-history-per-plug-insight.md#Story 4.2] — Story 4.2's exact ACs and epic framing.
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md#FR-9-Per-Plug-Measured-Data-View] — FR-9 source requirement.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-9] — Smart-plug parser port, `SmartPlugReading` shape, Eve Home/Meross specifics.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-10] — Historical tag integrity, snapshot-by-value rule.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-14] — Main Meter sole authoritative total.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/consistency-conventions.md] — API route/naming/error-shape conventions.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/density-trend-history.html] — Authoritative Room → Power Point → Device tree markup (unchanged reference per `key-trend-history.html`).
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-trend-history.html] — Card placement/ordering on the composed Trend History page.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md] — IA row for Trend History; "Room → Power Point → Device tree" component-pattern row.
- [Source: src/EnergyTracker.Domain/SmartPlugReading.cs] — entity shape, no `DeviceId`, snapshot-by-value fields.
- [Source: src/EnergyTracker.Application/Ports/IStatusSnapshotRepository.cs, src/EnergyTracker.Infrastructure/Adapters/StatusSnapshotRepository.cs, src/EnergyTracker.Application/GetStatusHistory.cs, src/EnergyTracker.Api/Endpoints/StatusEndpoints.cs] — direct structural precedent from Story 4.1 to mirror.
- [Source: web/src/components/trend-history/trend-history-page.tsx, web/src/components/meter-reading/meter-readings-card.tsx] — literal "Story 4.2" placement comments already in the codebase.
- [Source: _bmad-artifacts/implementation/4-1-trend-history-view.md#Dev Agent Record] — previous story's File List and completion notes (see Previous Story Intelligence below).

## Previous Story Intelligence (Story 4.1 — Trend History View)

- **Direct handoff:** Story 4.1 explicitly left this tree unbuilt and marked exactly where it goes — see the Dev Notes "This is the tree Story 4.1 deliberately deferred" point above. No guessing needed on placement.
- **Established patterns to reuse, not reinvent:**
  - New dedicated read-only repository port pattern (`IStatusSnapshotRepository`) for a display-only query, kept separate from a write-focused repository — do the same for `ISmartPlugReadingRepository` vs. `ISmartPlugImportRepository`.
  - Endpoint file structure (`StatusEndpoints.cs`'s private `TryGetHouseholdId` helper, always-array response for history/drill-down data, DTO mapping functions at the bottom of the file).
  - Frontend self-contained-fetch component pattern (`MeterReadingsCard`: `useCallback` load function + `useEffect`, loading/error/empty states, `GlassCard` + `details`/`summary`).
  - i18n: add a new nested namespace under `trendHistory.*` (mirroring `readingsCard`), verify key parity across both locale files as a named task, not an afterthought.
- **Process learnings from 4.1's own review pass** (all fixed pre-merge, so the current code already reflects them — no action needed here, but useful context for what a reviewer will look for): don't hardcode environment locale, always distinguish a genuine empty state from a fetch error, ensure React list keys are collision-safe, preserve heading-based screen-reader navigation when content moves into a collapsed disclosure. Apply the same care in `PerPlugDataCard`.
- **Manual browser verification was skipped in 4.1** (no OIDC provider configured in this dev environment) — expect the same constraint here; rely on component/integration test coverage instead, as 4.1 did.
- **File List from Story 4.1** (backend new: `IStatusSnapshotRepository.cs`, `StatusSnapshotRepository.cs`, `GetStatusHistory.cs`, `GetStatusHistoryTests.cs`; backend modified: `StatusEndpoints.cs`, `Program.cs`, `StatusEndpointsTests.cs`; frontend new: `trend-chart.tsx`+test, `trend-history-page.tsx`+test, `meter-readings-card.tsx`+test; frontend modified: `status-api.ts`+test, `nav-chrome.tsx`+test, `dashboard-page.tsx`+test, `settings-page.tsx`+test, `App.tsx`+test, both locale files) — this story's own File List (Task 8's completion) should follow the same new/modified split shape.

## Git Intelligence

- Last relevant commits: `a07d403` (feat: story 4.1 - trend history view), `9511f66`/`21e9a95` (story 3.6 — Smart Plug Import Job Status & History, + its own code-review fix pass), `f3b660b` (story 3.7 — Smart-Plug Reading duplicate cleanup, the most recent commit to actually touch `SmartPlugReading` query logic before this story).
- Commit message convention confirmed: `feat: story {epic}.{num} - {short title}` for the main implementation commit; a separate `ref:`/`fix:` follow-up commit for a post-implementation code-review pass, not squashed into the feature commit.
- Branch already exists and matches convention: `feature/4-2-per-plug-measured-data-view` (current branch).

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

### Completion Notes List

- All 8 tasks complete; all 4 ACs satisfied.
- Backend: new `ISmartPlugReadingRepository`/`SmartPlugReadingRepository` read-only port (mirroring Story 4.1's `IStatusSnapshotRepository` split), `GetPerPlugMeasuredData` use case building the nested Room→PowerPoint→Device sum tree, `GET /api/smart-plug-readings` returning an always-array (never null) response. Grouping is strictly on `SmartPlugReading`'s snapshotted-by-value `RoomName`/`PowerPointName`/`DeviceName` string columns — no live join to `Room`/`PowerPoint`/`Device` — which is what makes AC #3 (retag doesn't move history, AD-10) true by construction; verified with a dedicated integration test that renames a Power Point after seeding readings and asserts the response still shows the pre-rename name.
- Non-blocking assumption (flagged per Story 4.1's precedent): Rooms/Power Points/Devices are ordered alphabetically ascending (culture-aware, `InvariantCulture`) at every level of the tree — the epic/mockups didn't specify an ordering.
- Frontend: `PerPlugDataCard` is a self-contained-fetch component (mirrors `MeterReadingsCard`'s pattern), rendered as the third card on `TrendHistoryPage` after `MeterReadingsCard`. The AD-14 caveat text is rendered unconditionally (always visible, independent of loading/empty/error/loaded state) per the story's "hard requirement, not cosmetic" instruction. Stale "Story 4.2" placeholder comments in `trend-history-page.tsx` and `meter-readings-card.tsx` were updated to reference `PerPlugDataCard` directly now that it's wired in.
- Manual browser verification was skipped (no OIDC provider configured in this dev environment), same constraint Story 4.1 documented — relied on component/integration test coverage instead.
- Frontend test note: `<details>` children remain in the jsdom DOM (just `display: none`) when the parent is closed — React Testing Library's `getByText` matches on DOM content regardless of CSS visibility, so the "collapsed by default" test asserts via `toBeVisible()`/the `open` attribute rather than `queryByText(...).not.toBeInTheDocument()`.
- Full regression (initial implementation): backend solution suite 435/435 passed (`EnergyTracker.Architecture.Tests` included); frontend suite 235/235 passed; `tsc -b` clean; `oxlint` clean (pre-existing unrelated warnings only).
- **Code review pass (bmad-code-review, 3-layer adversarial review — Blind Hunter, Edge Case Hunter, Acceptance Auditor):** 1 decision-needed + 5 patch findings, all resolved. See Review Findings above for detail on each. Summary: hardcoded browser locale → `locale` prop threaded through; missing loading state → added with new i18n key; no list semantics → `<ul>/<li>` throughout the tree; `Ordinal` sort → `InvariantCulture`; AD-10 test coverage gap → added Room-rename and Power-Point-move regression tests; and a genuinely reachable data-integrity gap (two different Power Points colliding into one tree node via name reuse after a rename) → fixed by disambiguating on `PowerPointId` in both the repository's `GroupBy` and the use case's tree-building, on top of (not instead of) the snapshotted string columns. 4 additional findings considered and dismissed as noise (reasoning recorded in the review, not written to this file): `<h3>` vs `role="heading"` (the `<h3>` is spec-mandated, Task 5), independent per-level rounding possibly not summing exactly (negligible, no codebase precedent for cross-level reconciliation), empty-state not distinguishing "no data" from "awaiting mapping" (that funnel already exists on the separate Smart Plug Import job page), missing fetch-cancellation-guard test (same untested idiom used elsewhere in the codebase).
- Full regression (post-review-fixes): backend solution suite 440/440 passed; frontend suite 237/237 passed; `tsc -b` clean; `oxlint` clean (pre-existing unrelated warnings only).

### File List

**Backend — new:**
- `src/EnergyTracker.Application/Ports/ISmartPlugReadingRepository.cs`
- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugReadingRepository.cs`
- `src/EnergyTracker.Application/GetPerPlugMeasuredData.cs`
- `src/EnergyTracker.Api/Endpoints/SmartPlugReadingEndpoints.cs`
- `tests/EnergyTracker.Application.Tests/GetPerPlugMeasuredDataTests.cs`
- `tests/EnergyTracker.Api.Tests/SmartPlugReadingEndpointsTests.cs`

**Backend — modified:**
- `src/EnergyTracker.Api/Program.cs`

**Frontend — new:**
- `web/src/lib/smart-plug-reading-api.ts`
- `web/src/lib/smart-plug-reading-api.test.ts`
- `web/src/components/trend-history/per-plug-data-card.tsx`
- `web/src/components/trend-history/per-plug-data-card.test.tsx`

**Frontend — modified:**
- `web/src/components/trend-history/trend-history-page.tsx`
- `web/src/components/trend-history/trend-history-page.test.tsx`
- `web/src/components/meter-reading/meter-readings-card.tsx`
- `web/src/locales/en-US/translation.json`
- `web/src/locales/de-DE/translation.json`
