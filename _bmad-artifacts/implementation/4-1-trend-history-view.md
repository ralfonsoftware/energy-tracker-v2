---
baseline_commit: 99c7550b73f12016ba992ed759cdb11628f6dd1c
---

# Story 4.1: Trend History View

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want to view my Status/pace trend over time — and browse and correct my individual Meter Readings from the same surface,
so that I can see how my consumption pace has evolved and drill into or fix the raw entries behind it, without a second parallel page.

## Context you must not re-derive (read this before touching anything)

**This story absorbs Story 2.8** (Epic 2, shipped 2026-08-23, `_bmad-artifacts/implementation/2-8-meter-reading-history-view.md`). Story 2.8 built a standalone "Meter Reading History" page (paginated list, `AuditCorrection`/AD-4 edit-in-place, pending-regression flag) reachable via a Dashboard text-link. Per the **Epic 3 Retro's Significant Discovery** (`_bmad-artifacts/implementation/epic-3-retro-2026-08-23.md`), Ralf's decision was to consolidate rather than duplicate:

1. This story **relocates/integrates** 2.8's already-built backend (`GetMeterReadingHistory`, `EditMeterReading`, `GET`/`PUT /api/meter-readings`) and frontend (`EditMeterReadingDialog`, `meter-reading-history-api.ts`, the list/pagination markup) into the new Trend History surface — **reuse, do not rebuild**.
2. The Dashboard's standalone "History" text-link (`dashboard.historyTrigger`, `onHistoryClick`) is **removed**.
3. Story 4.3 ("Correcting a Meter Reading") is a separate, later story — it adds the **one net-new piece**, `IStatusRecomputeService` recompute-forward on edit. **This story does not touch `EditMeterReading`'s recompute behavior at all** — don't add a recompute call here; that's explicitly Story 4.3's job.

**Two more pieces of already-completed groundwork this story depends on and must not redo:**
- The Epic 3 Retro's Action Items #1/#2 (concurrent-recompute race + full-history-walk perf risk) shipped 2026-08-25 in PR #21 (`IHouseholdRecomputeLock`, bounded-window `GetCurrentStatus` reads) — this was the explicit gate before Trend History could safely read `StatusSnapshot` rows. It's done; no action needed here, just know `StatusSnapshot` writes are now race-free.
- Story 3.5's deferred-work entry (`_bmad-artifacts/implementation/deferred-work.md`, "Deferred from: story-3-5-dual-entry-points-multi-file-import-queuing") explicitly assigns this story the job of adding a **second Smart Plug Import entry-point icon button** on the Trend History page, wired to the same `setView('smartPlugImport')` destination the Dashboard's icon button already uses (UX-DR20, epic-3 Story 3.5 AC #2). No backend/new-screen work — just a second button. Include this even though it isn't in epic-4's own AC list for Story 4.1: it's a real, previously-deferred requirement explicitly targeted at "whichever Epic 4 story first builds the Trend History screen," which is this one.

**Out of scope for this story:** the Room → Power Point → Device tree card visible in the mockup belongs to **Story 4.2** ("Per-Plug Measured Data View") — it has its own ACs (FR-9, AD-14, AD-10) not yet built. Build only the Trend chart and the Meter Readings card in this story; leave the tree for 4.2 to add as a third card.

## Acceptance Criteria

1. **Given** the Trend History surface, **when** displayed, **then** alongside the aggregate Status/pace trend, it also surfaces a browsable list of individual Meter Readings (value + timestamp, Main Meter only), ordered by timestamp descending — the functionality Story 2.8 originally shipped as a standalone page, now consolidated here (FR-31).
2. **Given** a Meter Reading in the browsable list that is currently under an open, unconfirmed regression classification (Story 2.3), **when** it appears in the list, **then** it's visibly flagged as pending rather than shown as a normal confirmed entry — unchanged from Story 2.8's original behavior (FR-31, FR-25).
3. **Given** the Dashboard Status card's standalone "History" text-link (built in Story 2.8), **when** this story's merged surface ships, **then** that link is removed — Trend History becomes the single place to browse (and, per Story 4.3, correct) Meter Readings, not two parallel entry points.
4. **Given** historical `StatusSnapshot` rows (Story 2.4), **when** I open Trend History, **then** I see trend over time, not just the current point-in-time Status (FR-8).
5. **Given** the Trend History view, **when** rendered, **then** it reads only persisted `StatusSnapshot` rows, never a live recomputation against current settings — a later Yearly Baseline/threshold edit cannot rewrite what's shown (FR-8, AD-7, NFR9).
6. **Given** gaps in the underlying Meter Reading history, **when** rendered, **then** they show as a visible break in the trend line, never an interpolated line — distinct from FR-24's Smart-Plug-import gap interpolation (FR-8).
7. **Given** the trend chart, **when** displayed, **then** Moderate density is the only shipped default (no user-facing density toggle), with status-triad line coloring for in-range vs. trending segments only — never a 4th chart-specific color (UX-DR6).
8. **Given** the Trend History surface, **when** accessed on a tablet/browser-width screen, **then** it widens to that frame but stays single-column-of-cards internally — no dense multi-column grid (UX-DR19).
9. **Given** the product's "says less, on purpose" discipline, **when** Trend History is compared to the Dashboard Status card, **then** checking Trend History is never presented as a precondition for trusting the Status (NFR15).

## Tasks / Subtasks

### Backend

- [x] **Task 1 — `IStatusSnapshotRepository` port + adapter (AC #4, #5)**
  - [x] New file `src/EnergyTracker.Application/Ports/IStatusSnapshotRepository.cs`:
    ```csharp
    public interface IStatusSnapshotRepository
    {
        // Ascending by ComputedAtUtc (chart reads chronologically), then Id as the deterministic
        // tiebreak on an identical timestamp — same tiebreak discipline as
        // FindImmediatelyPrecedingAsync/GetPageForMainMeterAsync elsewhere in this codebase.
        Task<IReadOnlyList<StatusSnapshot>> GetForHouseholdAsync(Guid householdId, CancellationToken cancellationToken);
    }
    ```
    This is the **first read consumer** of `StatusSnapshot` — until now only `StatusRecomputeService` (Infrastructure/Adapters) has ever written to it (`src/EnergyTracker.Infrastructure/Adapters/StatusRecomputeService.cs:57-68`). Read it first for the exact `DbContext.StatusSnapshots` access pattern and the AD-3 query-filter behavior (already wired in `EnergyTrackerDbContext.OnModelCreating` — do **not** add a second, redundant `HouseholdId` filter in the query itself).
  - [x] New file `src/EnergyTracker.Infrastructure/Adapters/StatusSnapshotRepository.cs` implementing the above: `dbContext.StatusSnapshots.Where(s => s.HouseholdId == householdId).OrderBy(s => s.ComputedAtUtc).ThenBy(s => s.Id).ToListAsync(...)`. No pagination — AC #4's "trend over time" reads the full lifetime series; see Dev Notes' perf caveat below (deliberately not addressed by this story).
  - [x] `src/EnergyTracker.Api/Program.cs`: `builder.Services.AddScoped<IStatusSnapshotRepository, StatusSnapshotRepository>();` near the existing `IMeterReadingRepository`/`IMeterRegressionPromptRepository` registrations.

- [x] **Task 2 — `GetStatusHistory` use case (AC #4, #5, #6)**
  - [x] New file `src/EnergyTracker.Application/GetStatusHistory.cs`. Result record:
    ```csharp
    public record StatusHistoryEntry(Status Status, decimal PaceToDateKwh, decimal BaselineToDateKwh, bool IsLowConfidence, DateTimeOffset ComputedAtUtc, bool GapBeforeThisEntry);
    ```
    `ExecuteAsync(Guid householdId, CancellationToken cancellationToken)`:
    1. `var household = await householdRepository.FindByIdAsync(householdId, cancellationToken);` — needed for `LowConfidenceGapDays` (step 3). If `null`, return `[]` (same "undefined state is not an error" precedent `GetMeterReadingHistory` and `GetCurrentStatus` already follow — a Household without even a Household row can't happen in practice via the authenticated path, but mirror the defensive shape anyway).
    2. `var snapshots = await statusSnapshotRepository.GetForHouseholdAsync(householdId, cancellationToken);` (Task 1).
    3. Map to `StatusHistoryEntry`, computing `GapBeforeThisEntry` per entry by comparing consecutive `ComputedAtUtc` values: `(snapshots[i].ComputedAtUtc - snapshots[i - 1].ComputedAtUtc).TotalDays > household.LowConfidenceGapDays`. The first entry's `GapBeforeThisEntry` is always `false` (nothing precedes it). **Reuses `Household.LowConfidenceGapDays`** (`src/EnergyTracker.Domain/Household.cs:29`, Story 2.4's existing "unusually long gap since last reading" household-scoped config, default 45 days) rather than inventing a second gap-threshold concept — AD-15 forbids a hardcoded literal here, and this field already means exactly the right thing. **This specific reuse was not explicitly confirmed with Ralf** — flagged as an open question at the end of this story; implement it as specified unless redirected.
  - [x] This use case is a pure read — no write, no recompute call (AD-7's two-call-site rule for `IStatusRecomputeService` is `CreateMeterReading` and Smart-Plug-import completion; a chart *read* is never a third site).

- [x] **Task 3 — `GET /api/status/history` endpoint (AC #4, #5)**
  - [x] `src/EnergyTracker.Api/Endpoints/StatusEndpoints.cs` (existing file, additive): add a third route, `api.MapGet("/status/history", ...)`, following the exact same `TryGetHouseholdId` guard + shape as `/status` and `/status/detail` immediately above it in this file. Per this file's own existing comment on `/status/detail` ("drill-down data (Trend History, FR-8) is always a separate endpoint, never merged in here") — this route is that drill-down's implementation. Returns `Results.Ok(entries.Select(ToHistoryEntryResponse).ToList())` — always a (possibly empty) array, never null (unlike `/status`'s nullable-body precedent, which models "no Status yet"; here "no history yet" is legitimately an empty list, not an undefined single value).
  - [x] Add to the same file:
    ```csharp
    public record StatusHistoryEntryResponse(string Status, decimal PaceToDateKwh, decimal BaselineToDateKwh, bool IsLowConfidence, DateTimeOffset ComputedAtUtc, bool GapBeforeThisEntry);
    ```
    Map via the existing private `ToStatusString` helper (reuse, don't duplicate the switch expression) — use named-argument construction (6 fields, several adjacent same-typed) per this codebase's own established discipline (Story 2.8's Dev Notes cite Story 2.7's positional-construction review finding as the reason every multi-field response record in this codebase now uses named arguments).
  - [x] `src/EnergyTracker.Api/Program.cs`: `builder.Services.AddScoped<GetStatusHistory>();` near the existing `GetCurrentStatus` registration.

### Frontend

- [x] **Task 4 — API client (AC #4)**
  - [x] `web/src/lib/status-api.ts` (existing file, additive — same file as `fetchCurrentStatus`/`fetchStatusDetail`, not a new file; this is one more drill-down of the same `/api/status/*` resource family):
    ```typescript
    export interface StatusHistoryEntryDto {
      status: StatusValue
      paceToDateKwh: number
      baselineToDateKwh: number
      isLowConfidence: boolean
      computedAtUtc: string
      gapBeforeThisEntry: boolean
    }
    export async function fetchStatusHistory(): Promise<StatusHistoryEntryDto[]>
    ```
    `GET /api/status/history`, `credentials: 'include'`, throws `ApiError` on non-2xx via the existing `toApiError` — mirror `fetchStatusDetail`'s shape exactly, except the body is always a real JSON array (no empty-body-means-null special case needed here, per Task 3's "always an array" contract).
  - [x] Extend `web/src/lib/status-api.test.ts` if it exists (check first — `fetchCurrentStatus`/`fetchStatusDetail` may or may not currently have dedicated tests; follow whatever precedent exists) with success-parsing and `ApiError`-on-non-2xx cases for `fetchStatusHistory`.

- [x] **Task 5 — `TrendChart` component (AC #4, #5, #6, #7)**
  - [x] New file `web/src/components/trend-history/trend-chart.tsx` (new `trend-history` feature folder, per the `web/src/components/{feature}` convention — this is a new feature domain, not a sub-folder of `dashboard` or `meter-reading`). **No charting library** — this codebase has zero chart-library dependency today (`grep chart web/package.json` returns nothing); hand-roll inline SVG mirroring `mockups/key-trend-history.html`'s structure (viewBox, grid lines, a dashed "0" baseline reference line, axis labels, `<path>` segments, gap-band `<rect>`s) rather than introducing a new dependency for one chart.
  - [x] Props: `{ entries: StatusHistoryEntryDto[] }`. **Reuse `computeStatusDifference(paceToDateKwh, baselineToDateKwh)`** from `web/src/lib/status-difference.ts` (already shared between `status-card.tsx` and `status-detail-dialog.tsx` for exactly this sign/rounding logic — Story 2.7 extracted it specifically so a third copy never drifts) for both the per-point plotted value (`-rawDifference`, i.e. `baselineToDateKwh - paceToDateKwh`, so a positive plotted value means under baseline, matching the mockup's "Currently 240 kWh under baseline" caption) and the `trendHistory.chartCaption`'s `under`/`over`/`on` wording for the latest point — don't reimplement the branch-on-unrounded-sign logic a third time.
  - [x] **Segment coloring (AC #7):** exactly 2 line colors, never a 3rd/4th chart-specific color. `Status.Trending` → `var(--color-status-trending)`; **both** `Status.WithinRange` and `Status.BelowBaseline` → `var(--color-status-within-range)` (these two CSS custom properties already exist in `web/src/index.css:57-58` and `:150-153` — reuse them directly; do **not** invent separate `trend-chart.line-*` CSS variables even though `DESIGN/components.md` names them abstractly as `{components.trend-chart.line-within-range}`/`{components.trend-chart.line-trending}` — no such distinct CSS vars were ever added to `index.css`, and the mockup's own hardcoded hex values approximate the existing status tokens, not new ones). Color each line segment (the path between two consecutive non-gapped points) by the **later** point's Status.
  - [x] **Gap rendering (AC #6):** where `entry.gapBeforeThisEntry` is `true`, do **not** draw a connecting path segment from the previous point — render a visible break (mirror the mockup's `.gap-band` `<rect>` + label, e.g. "No reading {{dateRange}}"). This is a new visual/data pattern — no existing chart or gap-rendering code to copy in this codebase (Story 3.3's `SmartPlugImportGap`/gap-band vocabulary is a *different*, already-styled concept reused only for its color token per `DESIGN/components.md`, not its data model).
  - [x] Empty/insufficient-data state: fewer than 2 entries can't draw a line — render the same plain-text "no data yet" treatment style as `MeterReadingHistoryPage`'s empty state (translated key below), not a broken/empty SVG.
  - [x] New test file `web/src/components/trend-history/trend-chart.test.tsx`: renders a path for a contiguous run of entries; renders a visible gap (no connecting path) where `gapBeforeThisEntry` is true; uses the trending color for a `Trending`-status segment and the within-range color for `WithinRange`/`BelowBaseline` segments; empty state with 0 or 1 entries.

- [x] **Task 6 — Extract `MeterReadingsCard` from `MeterReadingHistoryPage` (AC #1, #2)**
  - [x] New file `web/src/components/meter-reading/meter-readings-card.tsx`. Extract the table/pagination/edit-dialog body (currently `web/src/components/meter-reading/meter-reading-history-page.tsx:20-160`, minus the outer `<main>`/heading/back-button shell) into this new component, wrapped in a `details`/`summary` disclosure per the mockup (`<details class="readings-list">`, collapsed by default — "says less, on purpose," NFR15/AC #9, matching the Room→PowerPoint→Device tree's identical collapsed-by-default idiom one card below it). Props: `{ locale: string }` (same as before, `onBack` no longer needed — this is now a card, not a page). **All internal state/logic (page, data, loading, error, editing; `fetchMeterReadingHistory`/`updateMeterReading` calls; the Pending badge; the correction note; the Edit dialog wiring) is copied verbatim from the existing page** — this is a relocation, not a rewrite; don't change the pending-flag logic, the pagination math, or the re-fetch-current-page-after-save behavior.
  - [x] Summary label shows a live count, e.g. `t('trendHistory.readingsCard.summary', { count: data?.totalCount ?? 0 })` (mockup: "Meter Readings — 214 logged").
  - [x] Delete `web/src/components/meter-reading/meter-reading-history-page.tsx` and its test file — fully superseded by this extraction plus Task 7's `TrendHistoryPage`, per AC #3's "single place" requirement. Keep `edit-meter-reading-dialog.tsx` and `meter-reading-history-api.ts` (and their tests) **unchanged** — reused as-is, imported by this new card exactly as the old page imported them.
  - [x] New test file `web/src/components/meter-reading/meter-readings-card.test.tsx`, migrating the relevant assertions from the deleted page's test file (renders fetched readings; Pending badge only when `isPendingRegression`; correction note only when `correctedFromKwhValue` is non-null; Previous/Next pagination and disabled states; loading/error states; edit flow calls `onSaved`→re-fetch-current-page).

- [x] **Task 7 — `TrendHistoryPage` (AC #1, #3, #4, #7, #8, #9)**
  - [x] New file `web/src/components/trend-history/trend-history-page.tsx`. Shell mirrors `SettingsPage` (`web/src/components/settings/settings-page.tsx`) — **not** the old `MeterReadingHistoryPage`'s no-`NavChrome` shape: Trend History **is** one of the 4 fixed nav-chrome tabs (`nav-chrome.tsx`'s `NavTab` already includes `'trendHistory'`), unlike Story 2.8's standalone page, which deliberately had no tab slot. Render `<NavChrome active="trendHistory" onDashboardClick={onBack} onTrendHistoryClick={() => {}} onSettingsClick={onSettingsClick} />` at the bottom, same placement as `SettingsPage:38`.
  - [x] Props: `{ locale: string; onBack: () => void; onSettingsClick: () => void; onSmartPlugImportClick: () => void }`.
  - [x] Fetch `fetchStatusHistory()` (Task 4) on mount; render `<TrendChart entries={...} />` (Task 5) inside a card, then `<MeterReadingsCard locale={locale} />` (Task 6) directly below it — **in that order**, per the mockup's placement rationale (chart + readings list are two views of the same Main Meter data and read as a pair; the future Story 4.2 tree is a structurally different Smart Plug signal and stays last). Do **not** add a placeholder/empty card for the Room → Power Point → Device tree — that's Story 4.2's own addition.
  - [x] Page header: title + the Smart Plug Import icon entry-point button (Task 8) in the same row, matching `DashboardPage`'s `page-title-row` layout in the mockup (title left, icon button right) — copy the icon button's exact markup/classnames from `dashboard-page.tsx:124-129` (`smartPlugImport.entryPointLabel` aria-label/title, same SVG icon, same `onClick` shape but wired to this page's `onSmartPlugImportClick` prop).
  - [x] Responsive width (AC #8): no new CSS needed beyond what `SettingsPage`/`MeterReadingHistoryPage` already use (`max-width` isn't set anywhere in this codebase's page shells — they're already fluid within the app frame) — confirm visually rather than adding a new breakpoint utility; this AC is about staying single-column, not about adding a distinct tablet layout.
  - [x] New test file `web/src/components/trend-history/trend-history-page.test.tsx`: renders the chart and the Meter Readings card; renders the Smart Plug Import icon button and calls `onSmartPlugImportClick`; renders `NavChrome` with `active="trendHistory"`.

- [x] **Task 8 — Wire `NavChrome`'s Trend History tab + Dashboard's Smart Plug Import icon reuse (AC #1)**
  - [x] `web/src/components/dashboard/nav-chrome.tsx`: add `onTrendHistoryClick: () => void` to `NavChromeProps`. Change the current inert placeholder (`<div role="button" aria-disabled="true">`, lines 36-39) to a real `<button type="button" onClick={onTrendHistoryClick} aria-current={active === 'trendHistory' ? 'page' : undefined} className={cn(ITEM_CLASSNAME, active === 'trendHistory' && ACTIVE_CLASSNAME)}>` — mirror the Dashboard/Settings buttons' exact shape immediately above/below it. **Do not touch the Tariff Radar placeholder** (still Epic 5, still correctly inert) — update the file's own top comment (lines 17-20) to say Trend History now has a real surface, only Tariff Radar remains a placeholder.
  - [x] Update every existing `NavChrome` call site to pass the new required prop: `dashboard-page.tsx:146` gets `onTrendHistoryClick={onTrendHistoryClick}` (a new prop threaded from `App.tsx`, Task 9); `settings-page.tsx:38` gets `onTrendHistoryClick={onTrendHistoryClick}` (same threading); `trend-history-page.tsx` (Task 7) passes `() => {}` (already active there, matching `onDashboardClick={() => {}}`'s existing no-op-when-already-there pattern on `dashboard-page.tsx:146`).
  - [x] Extend `web/src/components/dashboard/nav-chrome.test.tsx` (or create if none exists — check first): the Trend History tab is now a real button, calls `onTrendHistoryClick`, and reflects `active="trendHistory"` the same way the Dashboard/Settings tabs already are tested.

- [x] **Task 9 — Remove the Dashboard History trigger; wire the new `'trendHistory'` view (AC #1, #3)**
  - [x] `web/src/components/dashboard/dashboard-page.tsx`: **remove** the `onHistoryClick` prop, the second `<button onClick={onHistoryClick}>` inside `detailTrigger` (lines 110-116), and the now-unused `dashboard.historyTrigger` translation key. The `detailTrigger` fragment goes back to wrapping only the Status Detail trigger — do **not** leave an empty fragment; simplify back to what it was before Story 2.8's Task 12 addition (a single `<StatusDetailDialog trigger={...} />`, no surrounding `<>...</>` needed once there's only one child). Add the new `onTrendHistoryClick: () => void` prop (Task 8) threaded to `NavChrome`.
  - [x] `web/src/App.tsx`: replace the `'history'` view value with `'trendHistory'` in the `view` union (`App.tsx:40`). Replace the `view === 'history'` branch (`App.tsx:248-250`, currently rendering the now-deleted `MeterReadingHistoryPage`) with `view === 'trendHistory'` rendering `<TrendHistoryPage locale={state.household.locale} onBack={() => setView('dashboard')} onSettingsClick={() => setView('settings')} onSmartPlugImportClick={() => setView('smartPlugImport')} />` (Task 7). Remove the `MeterReadingHistoryPage` import. Change `onHistoryClick={() => setView('history')}` (`App.tsx:274`) to `onTrendHistoryClick={() => setView('trendHistory')}` passed into `DashboardPage`. Thread the same `onTrendHistoryClick={() => setView('trendHistory')}` into `SettingsPage` too (Task 8's new required prop there).
  - [x] Extend `web/src/App.test.tsx` if a `'history'`-view test case exists (per Story 2.8's own note: check whether direct `App.tsx` test coverage exists before assuming) — update/replace it for `'trendHistory'` rendering `TrendHistoryPage`, `onBack` returning to `'dashboard'`.

- [x] **Task 10 — i18n copy (AD-18)**
  - [x] Add a `trendHistory` top-level namespace (sibling to `dashboard`, `meterReadingHistory`, `meterReading`) to both `web/src/locales/en-US/translation.json` and `de-DE/translation.json`: `heading` (en: "Trend History", de: "Zählerverlauf" — check for an existing closer match before inventing new German wording), `chartCaption` (e.g. en: "Currently {{kwh}} kWh {{direction}} baseline." with `direction` as `"under"`/`"over"`, mirroring `dashboard.status.body.underPace`/`overPace`'s existing sign convention rather than a new phrasing), `gapLabel` (e.g. en: "No reading {{range}}"), `emptyState` (e.g. en: "Not enough history yet to show a trend."), `readingsCard.summary` (e.g. en: "Meter Readings — {{count}} logged"), `readingsCard.expand`/`readingsCard.collapse` (e.g. en: "Browse & correct" / "Hide", matching the mockup's disclosure toggle text).
  - [x] Remove `dashboard.historyTrigger` (Task 9) from both locale files.
  - [x] `meterReadingHistory.heading`/`meterReadingHistory.backToApp` become unused once `meter-reading-history-page.tsx` is deleted (Task 6) — remove them; **keep every other `meterReadingHistory.*` key** (`valueColumn`, `timestampColumn`, `pendingBadge`, `correctedFrom`, `editTrigger`, `editTriggerFor`, `editDialogTitle`, `save`, `saving`, `cancel`, `conflictError`, `errorGeneric`, `loadError`, `loading`, `emptyState`, `previousPage`, `nextPage`, `pageIndicator`) — `MeterReadingsCard` (Task 6) and `EditMeterReadingDialog` (unchanged) still use them verbatim.
  - [x] `dashboard.nav.trendHistory` already exists (`"Trend History"`/de equivalent, added when `nav-chrome.tsx` was first built) — reuse it for the page heading too if the copy matches, don't duplicate the string under a new key if it's identical.
  - [x] `nav-chrome.tsx`'s top comment (Task 8) documents Trend History losing its placeholder status — no i18n key changes there beyond what Task 8 already covers.
  - [x] Both locale files updated together, verified for byte-for-byte key-set parity — same script-based check Story 2.5/2.6/2.7/2.8 already established.

### Tests

- [x] **Task 11 — Backend tests**
  - [x] `tests/EnergyTracker.Application.Tests/GetStatusHistoryTests.cs` (new): empty history (no `StatusSnapshot` rows) returns `[]`; ordering is `ComputedAtUtc` ascending; `GapBeforeThisEntry` is `false` for the first entry and for any pair within `LowConfidenceGapDays`, `true` for a pair exceeding it; a Household with a non-default `LowConfidenceGapDays` value is respected (don't hardcode 45 in the use case). Mock `IStatusSnapshotRepository`/`IHouseholdRepository` with NSubstitute, following `GetMeterReadingHistoryTests.cs`'s existing mocking style.
  - [x] `tests/EnergyTracker.Api.Tests/StatusEndpointsTests.cs` (extend if it exists, else create following `MeterReadingEndpointsTests.cs`'s naming style): `GET_status_history_returns_entries_ordered_by_ComputedAtUtc_ascending`; `GET_status_history_returns_an_empty_array_when_no_snapshots_exist` (not null — assert the JSON body is literally `[]`); cross-Household isolation (`A_households_status_history_is_never_affected_by_another_households_snapshots`, both directions, per Story 2.7's symmetric-assertion discipline); `A_principal_without_a_Household_is_forbidden_from_...` mirroring the existing pattern for `/status`/`/status/detail`.

- [x] **Task 12 — Frontend tests**
  - [x] Task 4/5/6/7's tests listed above.
  - [x] `web/src/components/dashboard/dashboard-page.test.tsx` (extend): the History trigger/`onHistoryClick` assertions are removed (superseded); add/confirm the Smart Plug Import icon button assertion still passes unchanged; the `NavChrome` render now also passes `onTrendHistoryClick`.
  - [x] `web/src/components/settings/settings-page.test.tsx` (extend, if it renders/tests `NavChrome`): assert the new `onTrendHistoryClick` prop is threaded through.
  - [x] `web/src/App.test.tsx` (Task 9).

- [x] **Task 13 — Documentation**
  - [x] `_bmad-artifacts/implementation/deferred-work.md`: mark the Story 3.5 "Trend History's Smart Plug Import icon entry point... deferred to whichever Epic 4 story first builds the Trend History screen" entry as resolved by this story (Task 8), or remove it — follow whatever this file's existing convention is for closing out a deferred item (check how prior stories closed theirs, e.g. Epic 3 Retro's Action Items table `✅ Done` style vs. deferred-work.md's own entries, which appear to stay as historical log rather than being deleted — default to leaving the entry in place with a note that it shipped in this story, not deleting it, matching the append-only-log character of the rest of this file).
  - [x] Add a new entry: unbounded full-lifetime `StatusSnapshot` read in `GetStatusHistory`/`StatusSnapshotRepository.GetForHouseholdAsync` has no pagination or trailing-window bound, unlike `GetCurrentStatus`'s bounded-window read (Epic 3 Retro Action Item #2) — a latent NFR1 perf risk for a long-lived household with years of recompute history, deliberately not addressed here since no AC requires bounding and Trend History's whole point is showing the full trend, not a windowed one. Mirrors the "pre-existing pattern extended, not yet a measured problem at current data volumes" framing already used for the identical class of issue in `deferred-work.md`'s Story 2.4 entry.

## Dev Notes

### Architecture compliance (binding, not optional)

- **AD-7 — read-only, no new recompute call site.** This story adds `GetStatusHistory` as a pure read of already-persisted `StatusSnapshot` rows. It must **never** call `IStatusRecomputeService` or trigger a live `GetCurrentStatus` computation for chart data (AC #5's whole point) — the two existing call sites (`CreateMeterReading`, Smart-Plug-import completion) are unchanged by this story.
- **AD-3 — tenant isolation via the existing query filter, not a manual check.** `StatusSnapshot` already has `HasQueryFilter(e => e.HouseholdId == CurrentHouseholdId)` wired in `EnergyTrackerDbContext.OnModelCreating` (`src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs:67`) — `StatusSnapshotRepository.GetForHouseholdAsync`'s own `Where(s => s.HouseholdId == householdId)` clause is redundant-but-harmless defense-in-depth (matches every other repository method in this codebase that also takes an explicit `householdId` parameter alongside the ambient filter), not a substitute for it. Do not add `.IgnoreQueryFilters()` anywhere.
- **AD-14 — no new exposure risk.** Every field this story returns/renders (`Status`, `PaceToDateKwh`, `BaselineToDateKwh`, `IsLowConfidence`, `ComputedAtUtc`, plus reused Meter Reading fields) comes from `StatusSnapshot`/`MeterReading`/`MeterRegressionPrompt`/`AuditCorrection` — never `SmartPlugReading`/`Event`. `PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests` needs no changes.
- **AD-15 — no hardcoded gap threshold.** Task 2's gap detection reuses `Household.LowConfidenceGapDays`, a household-scoped config value, never a literal day count in the use case.
- **AD-18 — storage/display split.** `ComputedAtUtc` is UTC in the API response (per the wire-format rule: `DateTimeOffset`, ISO 8601 with explicit offset); the frontend's `Intl.DateTimeFormat(locale, ...)` handles locale display, same discipline as every other timestamp in this codebase.
- **UX-DR9 (nav-chrome's 4 fixed tabs)** — this story is the first to make a second tab (Trend History) real; only Tariff Radar remains a placeholder after this story. Don't repurpose or remove the Tariff Radar slot.

### Existing code this story builds on (read before writing anything)

- `src/EnergyTracker.Domain/StatusSnapshot.cs` — the entity this story's first read query targets; note its own doc comment: immutable, insert-only, only ever written for a *definite* (non-null) Status.
- `src/EnergyTracker.Infrastructure/Adapters/StatusRecomputeService.cs:57-68` — the only existing `StatusSnapshot` write, for field-name/shape reference.
- `src/EnergyTracker.Application/GetMeterReadingHistory.cs` and `src/EnergyTracker.Application/Ports/IMeterReadingRepository.cs` — the closest structural precedent for a new read-only, paginated-or-not history use case + port pair.
- `src/EnergyTracker.Api/Endpoints/StatusEndpoints.cs` — read completely before adding the third route; its own inline comments already state the "drill-down is a separate endpoint" rule this story's endpoint fulfills, and its `TryGetHouseholdId`/`ToStatusString` helpers are reused, not duplicated.
- `src/EnergyTracker.Domain/Household.cs:29` (`LowConfidenceGapDays`) — Task 2's gap-threshold source; read Story 2.4's original comment on this field ("no numeric default anywhere in the PRD... confirmed with Ralf") before assuming its meaning extends cleanly to a chart-gap use — it might not (see Open Questions below).
- `web/src/components/meter-reading/meter-reading-history-page.tsx` — read completely before extracting (Task 6); every piece of its current behavior (loading/error/empty states, pagination math, re-fetch-after-save) must survive the move unchanged.
- `web/src/components/settings/settings-page.tsx` — the exact page-shell + `NavChrome` composition Task 7's `TrendHistoryPage` mirrors.
- `web/src/components/dashboard/nav-chrome.tsx` — read completely; its own top comment currently states Trend History has no surface yet — Task 8 makes that comment stale and must update it.
- `web/src/components/dashboard/dashboard-page.tsx:95-146` — `detailTrigger`, the Smart Plug Import icon button (`124-129`), and the `NavChrome` render (`146`) — all three are touched by this story (icon button copied into `TrendHistoryPage`, `detailTrigger` simplified, `NavChrome` gets a new prop).
- `web/src/lib/status-api.ts` — the `ApiError`/`fetchStatusDetail` pattern Task 4 extends in place.
- `web/src/lib/status-difference.ts` (`computeStatusDifference`) — the shared pace-vs-baseline sign/rounding logic Task 5's chart reuses rather than reimplementing a third time.
- `web/src/index.css:51-59, 149-157` (dark) and the light-theme equivalent block — the exact `--color-status-*` CSS custom properties Task 5's chart reuses; do not add new ones.
- `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-trend-history.html` — the only rendered mock for this story's surface (Dark + Light); its own header comment explains the card-ordering rationale in full. `mockups/density-trend-history.html` is the earlier Minimal/Moderate/Dense exploration that settled Moderate as the sole default — reference only, not a second layout to reconcile.

### Previous story intelligence (Stories 2.4, 2.7, 2.8, 3.5)

- Story 2.8 is the direct structural precedent for the Meter Readings card portion: its "Confirmed with Ralf" surface-shape discussion no longer applies as originally written (this story gives Trend History a real nav-chrome tab, reversing 2.8's own "no nav slot" finding) — don't copy that specific conclusion forward, only the reusable row/pagination/edit mechanics.
- Story 2.4 is the origin of `LowConfidenceGapDays` and the "confirmed with Ralf, no PRD-given default" pattern for exactly this kind of judgment call — Task 2's reuse of the same field for chart-gap detection follows that story's spirit (a real, non-arbitrary default) but is this story's own extension, not something 2.4 itself specified.
- Story 2.7's positional-argument-construction review finding (now standard practice: named arguments at every multi-field record construction site) applies to `StatusHistoryEntryResponse`'s 6 fields up front — don't wait for a review finding to catch it, per Story 2.8's own Dev Notes making the same point.
- Story 3.5's deferred-work entry is this story's authority for Task 8's Smart Plug Import icon button — that entry already states the shared destination screen needs no changes, only a second entry-point wired the same way.
- Epic 3 Retro Action Items #1/#2 (`_bmad-artifacts/implementation/epic-3-retro-2026-08-23.md`) are why this story is safe to read `StatusSnapshot` now — read the retro's own framing ("Trend History will read StatusSnapshot rows directly, turning a silent audit-trail inconsistency into a visibly wrong/missing trend line for the first time") to understand why that fix was a hard prerequisite, not incidental context.

### File structure / conventions to follow exactly

- New feature folder `web/src/components/trend-history/` for `trend-chart.tsx` and `trend-history-page.tsx` — a new feature domain, per the established `web/src/components/{feature}` convention (not nested under `dashboard` or `meter-reading`).
- `web/src/components/meter-reading/meter-readings-card.tsx` stays in the existing `meter-reading` folder — same feature domain as `edit-meter-reading-dialog.tsx`/`log-reading-sheet.tsx`, which it sits alongside.
- Backend: one use-case class per file (`GetStatusHistory.cs`), one port per file (`IStatusSnapshotRepository.cs`), one adapter per file (`StatusSnapshotRepository.cs`) — no folding into existing files.
- `verbatimModuleSyntax: true` — `import type { StatusHistoryEntryDto }` etc. wherever type-only.
- Both `en-US`/`de-DE` `translation.json` updated together, byte-for-byte key-set parity verified.
- Test naming: .NET — `Snake_case_with_underscores`, Shouldly, NSubstitute, `TestContext.Current.CancellationToken`. Frontend — colocated, Vitest + Testing Library, `vi.stubGlobal('fetch', ...)`.
- **No new EF Core migration required** — `StatusSnapshot`'s table/columns/query-filter/index already exist (added by Story 2.4, `20260817051304_AddStatusSnapshotAndHouseholdThresholds`). This story adds a read-only query against the existing schema only.

### Testing standards summary

.NET: xunit.v3.mtp-v2, Shouldly, NSubstitute for Application-layer mocks, real Postgres+SqlServer via Testcontainers for `EnergyTracker.Api.Tests`. Frontend: Vitest + `@testing-library/react`, `jsdom`, globals on, real i18next catalogs (no test-provider wrapper).

### Project Structure Notes

- Alignment: follows the exact `web/src/components/{feature}` + colocated-test convention, the flat `web/src/lib/` convention (Task 4 extends `status-api.ts` rather than creating a new file, since this is one more endpoint on the same `/api/status/*` resource, unlike Story 2.8's `/api/meter-readings` which got its own client file), and the one-use-case-class-per-file backend convention every prior story establishes.
- This story is a **relocation-plus-one-new-read-path** story, not a from-scratch feature: most of its size comes from correctly moving Story 2.8's already-built, already-tested Meter Reading list/edit surface without regressing any of its behavior, plus one genuinely new piece (the `StatusSnapshot`-backed trend chart, including the gap-rendering and 2-color-segment logic that has no existing precedent anywhere in this codebase). Resist rebuilding the reused parts "cleaner" — the review bar for this story is "did Story 2.8's behavior survive the move unchanged," not "is this a nicer list component."

### References

- [Source: `_bmad-artifacts/planning/epics/epic-4-trend-history-per-plug-insight.md#Story 4.1`] — story statement + AC source (verbatim), including the "Absorbs Story 2.8" framing.
- [Source: `_bmad-artifacts/implementation/epic-3-retro-2026-08-23.md`] — the Significant Discovery + Action Items #1/#2/#4 that gate this story; read in full, not just the excerpt above.
- [Source: `_bmad-artifacts/implementation/2-8-meter-reading-history-view.md`] — the story being absorbed; its Tasks 8-13 (frontend) and Task 4-7 (backend reuse) are the direct source of Tasks 4-6/9-10 above.
- [Source: `_bmad-artifacts/implementation/deferred-work.md`, "Deferred from: story-3-5-dual-entry-points-multi-file-import-queuing"] — the Smart Plug Import icon entry-point obligation (Task 8).
- [Source: `_bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md#FR-8, #FR-31`] — FR wording this story's ACs mirror.
- [Source: `_bmad-artifacts/planning/epics/requirements-inventory.md#UX-DR6, #UX-DR12, #UX-DR19`] — exact UX-DR wording (chart density/coloring, IA reach point, responsive layout).
- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-3, #AD-7, #AD-14, #AD-15, #AD-18`] — exact AD rule text this story must not violate.
- [Source: `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-trend-history.html`] — the composed Dark+Light mock, card ordering rationale, edit-dialog reuse confirmation.
- [Source: `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/components.md#Trend chart, #Meter Readings list`] — component-level design tokens and reuse rules.
- [Source: `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md`] — updated IA row (Trend History now covers FR-8+FR-31+FR-9) and the Meter Readings list / Trend chart Component Patterns rows.
- [Source: `src/EnergyTracker.Domain/StatusSnapshot.cs`, `src/EnergyTracker.Application/Ports/IStatusRecomputeService.cs`, `src/EnergyTracker.Infrastructure/Adapters/StatusRecomputeService.cs`, `src/EnergyTracker.Application/GetCurrentStatus.cs`, `src/EnergyTracker.Api/Endpoints/StatusEndpoints.cs`, `src/EnergyTracker.Domain/Household.cs`] — existing backend code this story extends read-first.
- [Source: `web/src/components/meter-reading/meter-reading-history-page.tsx`, `web/src/components/meter-reading/edit-meter-reading-dialog.tsx`, `web/src/components/settings/settings-page.tsx`, `web/src/components/dashboard/dashboard-page.tsx`, `web/src/components/dashboard/nav-chrome.tsx`, `web/src/App.tsx`, `web/src/lib/status-api.ts`, `web/src/index.css`] — existing frontend code this story extends read-first.

## Open Questions for Ralf (not blocking — implement as specified above unless redirected)

1. **Chart gap-threshold reuse:** Task 2 reuses `Household.LowConfidenceGapDays` (Story 2.4's "unusually long gap since last reading," default 45 days) as the trend chart's visible-break threshold (AC #6). No UX-DR or FR ties these two concepts together explicitly — it's this story's own inference that they mean the same thing. Confirm, or specify a different threshold/config.
2. **No pagination/bound on `GetStatusHistory`'s read:** the chart reads a household's entire `StatusSnapshot` lifetime with no window, unlike every other Story-2.4-era read (`GetCurrentStatus`'s Epic-3-Retro-fixed bounded window). Acceptable for now (logged in `deferred-work.md`, Task 13), but flagging since it's the same class of issue the Epic 3 Retro just fixed elsewhere.
3. **Chart empty/low-data state wording and exact axis/label styling:** the mockup shows only a populated example; this story's own copy for the "not enough history yet" state (Task 10) and the precise SVG label positions are this story's own invention, not lifted from a second mockup state.

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5), via bmad-dev-story.

### Debug Log References

None — no test/build failures required debugging beyond two self-caught issues during implementation: an initial `StatusEndpointsTests` cross-Household test used a `0m` first reading (rejected by `MeterReadingValidation.ValidateKwhValue`'s `> 0` rule) and was fixed to `1m`; the first `MeterReadingsCard` re-fetch-after-save test needed a call-order-aware fetch mock (a static "after" response made the pre-edit assertion fail too).

### Completion Notes List

- Backend: `IStatusSnapshotRepository`/`StatusSnapshotRepository` (Task 1), `GetStatusHistory` (Task 2), and `GET /api/status/history` (Task 3) implemented per the story's exact spec — ascending `ComputedAtUtc`/`Id` ordering, `GapBeforeThisEntry` computed against `Household.LowConfidenceGapDays` (reused, not hardcoded), always-an-array response (never null). All backend tests pass (7 new `GetStatusHistoryTests`, 4 new `StatusEndpointsTests` cases), full solution suite green: 425 tests, 0 failed (includes `EnergyTracker.Architecture.Tests`).
- Frontend: `fetchStatusHistory` added to `status-api.ts` (Task 4); `TrendChart` hand-rolled inline SVG (Task 5) — 2-color status-triad segment coloring, gap bands with no connecting path, empty state for <2 entries; `MeterReadingsCard` extracted verbatim from the deleted `MeterReadingHistoryPage` behind a collapsed-by-default disclosure (Task 6); `TrendHistoryPage` composes chart + readings card behind `NavChrome` (Task 7); `NavChrome`'s Trend History tab is now a real button (Task 8); Dashboard's standalone History trigger removed, `'trendHistory'` view wired end-to-end through `App.tsx`/`DashboardPage`/`SettingsPage` (Task 9); i18n `trendHistory` namespace added to both locales with verified key-set parity, `dashboard.historyTrigger`/`meterReadingHistory.heading`/`backToApp` removed (Task 10). Full frontend suite green (227 tests after the code review pass below, 0 failed); `tsc -b` clean; `oxlint` clean (only 3 pre-existing unrelated warnings). Frontend production build (`npm run build`) succeeds.
- `chartCaption` was implemented as three full-sentence i18n keys (`on`/`under`/`over`), not a single template with a `{{direction}}` slot — the story's own Dev Notes point (mirror `dashboard.status.body.onPace/underPace/overPace`'s convention) argued against interpolating a bare direction word that can't carry correct grammar/case across locales (`de-DE`'s "unter"/"über" need agreement `en-US` doesn't).
- Manual browser verification was not performed — sign-in requires a registered OIDC provider not configured in this environment (`docs/local-development.md`). Coverage instead comes from the dedicated `TrendChart`/`MeterReadingsCard`/`TrendHistoryPage`/`nav-chrome`/`App` component and integration tests (segment coloring, gap rendering, disclosure expand/collapse, pagination, edit-then-refetch, nav wiring), plus the `StatusEndpointsTests` Testcontainers-backed integration coverage for the new endpoint.
- All 3 of the story's own "Open Questions for Ralf" were implemented exactly as specified (not blocking, per the story's own instruction) — `LowConfidenceGapDays` reuse for the chart gap threshold, no pagination/bound on `GetStatusHistory`'s read (now logged in `deferred-work.md`), and the empty/low-data state copy + axis/label styling are this story's own invention as flagged.

**Post-implementation code review pass (`/code-review`, 2026-08-29):** 10 findings reported; 6 verified and fixed in-place (no new files, File List unchanged), 4 verified-but-not-auto-fixed and left as documented tradeoffs:
- **Fixed:** `App.tsx` — `SmartPlugImportPage`'s `onBack` was hardcoded to `'dashboard'`, dropping the user's Trend History context when launched from its new second entry point; now tracked via `smartPlugImportReturnView` state. Added `App.test.tsx` regression test.
- **Fixed:** `TrendHistoryPage` — a `fetchStatusHistory()` failure was silently rendered as the same empty state as a genuinely new household; now a distinct `chartLoadError` state renders `trendHistory.chartLoadError` (new i18n key, both locales), mirroring `MeterReadingsCard`'s existing error-state pattern.
- **Fixed:** `TrendChart` — reversed my own earlier "no `locale` prop" call from the initial pass (see prior note, now superseded): the reviewer correctly flagged this as an AD-18 inconsistency (axis/gap-label formatting used the environment locale while `MeterReadingsCard` on the same page correctly used the household's). `TrendChart` now takes `locale: string` and formats with it; `TrendHistoryPage` threads it through. Added a de-DE-vs-en-US formatting regression test.
- **Fixed:** `TrendChart` — segment/gap-band React keys were built from `computedAtUtc` alone, which `StatusSnapshotRepository`'s own ordering comment documents is not guaranteed unique (Id is the tiebreak, never sent to the client); keys now also include the array index. Added a same-timestamp regression test.
- **Fixed:** `MeterReadingsCard` — the extracted readings section lost the standalone `MeterReadingHistoryPage`'s `<h1>`, degrading heading-based screen-reader navigation; the disclosure's summary text now carries `role="heading" aria-level={2}`.
- **Fixed:** `meter-readings-card.test.tsx` — the deleted page's "gives each row a distinct accessible name for its Edit button" regression test wasn't carried over during the Task 6 migration; restored.
- **Not fixed, flagged as a documented tradeoff:** `GetStatusHistory`'s `GapBeforeThisEntry` is computed from `StatusSnapshot.ComputedAtUtc` (recompute time), not the underlying `MeterReading.ReadingTimestamp` — correctly identified by the reviewer as diverging from a backfilled/backdated reading's real gap, but this is the exact mechanism the story's own Task 2 specified verbatim (no `MeterReading` timestamp is available on `StatusSnapshot` — closing this would need a schema change, out of scope here). Left as-is; worth a product conversation, not a silent code change.
- **Not fixed, flagged as a documented tradeoff:** `MeterReadingsCard` fetches its page eagerly on mount even though the disclosure is collapsed by default. The mockup's own collapsed-header copy ("Meter Readings — 214 logged") requires a live count be available before the household member ever expands it, so deferring the fetch until expansion would silently break that feature, not just optimize it — a real product tradeoff, not an oversight.
- **Not fixed, flagged as minor:** the Smart Plug Import icon button's markup is duplicated verbatim between `dashboard-page.tsx` and `trend-history-page.tsx` — the story's own Task 7 explicitly instructed copying it byte-for-byte from `dashboard-page.tsx`, so this was spec-directed, not an oversight. Extracting a shared component would be a small, low-risk follow-up but is a refactor beyond this story's scope.
- **Not fixed, flagged as minor:** `NavChrome`'s three call sites (`DashboardPage`/`SettingsPage`/`TrendHistoryPage`) each pass an explicit `() => {}` no-op handler for their own active tab — the reviewer suggested `NavChrome` could suppress the click internally when `active === tab` instead. Reasonable, but touches every page's call site for a cosmetic simplification; deferred as a follow-up, not applied here.

### File List

**Backend — new:**
- `src/EnergyTracker.Application/Ports/IStatusSnapshotRepository.cs`
- `src/EnergyTracker.Infrastructure/Adapters/StatusSnapshotRepository.cs`
- `src/EnergyTracker.Application/GetStatusHistory.cs`
- `tests/EnergyTracker.Application.Tests/GetStatusHistoryTests.cs`

**Backend — modified:**
- `src/EnergyTracker.Api/Endpoints/StatusEndpoints.cs`
- `src/EnergyTracker.Api/Program.cs`
- `tests/EnergyTracker.Api.Tests/StatusEndpointsTests.cs`

**Frontend — new:**
- `web/src/components/trend-history/trend-chart.tsx`
- `web/src/components/trend-history/trend-chart.test.tsx`
- `web/src/components/trend-history/trend-history-page.tsx`
- `web/src/components/trend-history/trend-history-page.test.tsx`
- `web/src/components/meter-reading/meter-readings-card.tsx`
- `web/src/components/meter-reading/meter-readings-card.test.tsx`

**Frontend — modified:**
- `web/src/lib/status-api.ts`
- `web/src/lib/status-api.test.ts`
- `web/src/components/dashboard/nav-chrome.tsx`
- `web/src/components/dashboard/nav-chrome.test.tsx`
- `web/src/components/dashboard/dashboard-page.tsx`
- `web/src/components/dashboard/dashboard-page.test.tsx`
- `web/src/components/settings/settings-page.tsx`
- `web/src/components/settings/settings-page.test.tsx`
- `web/src/App.tsx`
- `web/src/App.test.tsx`
- `web/src/locales/en-US/translation.json`
- `web/src/locales/de-DE/translation.json`

**Frontend — deleted:**
- `web/src/components/meter-reading/meter-reading-history-page.tsx`
- `web/src/components/meter-reading/meter-reading-history-page.test.tsx`

**Documentation — modified:**
- `_bmad-artifacts/implementation/deferred-work.md`
- `_bmad-artifacts/implementation/sprint-status.yaml`
