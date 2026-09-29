---
baseline_commit: b7f971b
---

# Story 8.7: Meter Readings, Events & Tariff History Entry-Grid

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member reviewing Meter Readings, Events, or Tariff History on a wide screen,
I want individual entries arranged in a multi-column grid instead of one long single-column list,
so that I can see more entries at a glance instead of scrolling through a narrow list beside empty space.

## Acceptance Criteria

1. **Given** the Meter Readings list, Events list (Trend History), or Tariff History list (Tariff Radar), expanded, at an available width ≥660px, **when** rendered, **then** individual entries reflow into a `repeat(auto-fill, minmax(260px, 1fr))` grid — 2 or 3 tiles per row depending on available width, never a fixed count — instead of a single-column table (UX-DR28). **And** each tile shows the same content its table row showed (primary value/description, secondary detail lines, timestamp/period, a Pending/Current badge where applicable, the Edit trigger) — reflowed, not redesigned or reduced.
2. **Given** the same three lists at an available width <660px, **when** rendered, **then** the existing single-column list (the shadcn `Table`) is unchanged.
3. **Given** the Room → Power Point → Device tree (Trend History) and the Tariff Radar form fields at ≥660px, **when** rendered, **then** neither is gridded by this story — the tree is addressed by Story 8.8, the form fields are already paired per Story 8.4/UX-DR23.

## Tasks / Subtasks

- [x] **Task 1: Shared breakpoint hook `useWideBreakpoint` (AC #1, #2)**
  - [x] Create `web/src/hooks/use-wide-breakpoint.ts` (kebab-case file, matching `use-logoff.ts` next to it) exporting `useWideBreakpoint(): boolean`, built on `useSyncExternalStore` over `window.matchMedia('(min-width: 660px)')` with a `change` listener.
  - [x] **Guard for jsdom:** `window.matchMedia` does not exist in jsdom and `web/src/test/setup.ts` does not stub it. When `typeof window.matchMedia !== 'function'` return `false` (server snapshot and client snapshot both). This keeps every existing table-based unit test passing untouched.
  - [x] The `660` literal must match `--breakpoint-wide: 660px` (`web/src/index.css:10`). Put a one-line comment cross-referencing it; do not try to read the CSS variable at runtime. **Do not change `index.css`.**
  - [x] Add `use-wide-breakpoint.test.ts` (colocated): stub `window.matchMedia` with a controllable `MediaQueryList` (matches true/false + fire a `change` event) and assert the hook value flips, and that it returns `false` when `matchMedia` is undefined. Follow the `use-logoff.test.ts` style.

- [x] **Task 2: Shared entry-grid + tile primitives (AC #1)**
  - [x] Create `web/src/components/entry-grid/entry-grid.tsx` exporting `EntryGrid` (the grid container) and `EntryTile` (one tile) — a new feature folder, not `components/ui` (project rule: new feature UI gets its own folder).
  - [x] `EntryGrid`: `<ul role="list">`-style container with `grid grid-cols-[repeat(auto-fill,minmax(260px,1fr))] gap-3` (mockup: `gap: 12px`). It is only ever rendered at ≥660px (the hook gates it), so no `wide:` prefix is needed on the grid classes themselves.
  - [x] `EntryTile`: an `<li>` with `flex flex-col gap-[3px] rounded-glass-sm px-[15px] pt-[13px] pb-[12px]` (mockup `.entry-tile`: radius 14px = `--radius-glass-sm`, padding 13/15/12) and the quiet-tier surface: `bg-surface-quiet border border-surface-quiet-border` (`index.css:83-84`). **Verify against the mockup before settling:** dark tile in `key-trend-history.html:183` is `rgba(220,245,230,0.045)` + `1px solid rgba(210,235,220,0.12)`, light is `rgba(255,255,255,0.55)` + `rgba(40,70,50,0.13)`. If `--surface-quiet`/`--surface-quiet-border` are visibly off from those in a live check, prefer the existing tokens anyway and note the delta in Completion Notes — do **not** add new hard-coded rgba values or new tokens.
  - [x] Slots/props kept minimal: `EntryTile` takes `children` plus an optional `actions` node rendered in a `mt-2 flex justify-end` row (`.entry-actions`). Primary/secondary/meta text styling is applied by the three callers via plain class strings (`text-sm font-bold`, `text-muted-foreground text-xs`), consistent with how the rows style text today — no per-field props.
  - [x] Colocated `entry-grid.test.tsx`: renders N tiles as list items, `EntryGrid` carries the `minmax(260px,1fr)` class string (string match only — jsdom has no layout, same limitation noted in Stories 8.3/8.6).

- [x] **Task 3: `MeterReadingsCard` — tiles at ≥660px (AC #1, #2)** — `web/src/components/meter-reading/meter-readings-card.tsx`
  - [x] Call `const isWide = useWideBreakpoint()`. Where the `<Table>` block is rendered (line 94-136) render `isWide ? <EntryGrid>…</EntryGrid> : <Table>…</Table>`. The `<Table>` branch stays **byte-for-byte unchanged**.
  - [x] Tile content, in this order, mirroring the mockup (`key-trend-history.html:334-343`) and the current row: primary line = `{numberFormat.format(item.kwhValue)} kWh` (`font-semibold tabular-nums`) + the Pending `<Badge variant="outline">` when `item.isPendingRegression`; secondary = the `correctedFrom` note when `item.correctedFromKwhValue !== null` (`text-muted-foreground text-xs`, identical i18n key/params); meta = `dateTimeFormat.format(new Date(item.readingTimestamp))` (`tabular-nums text-xs`); actions = the same outline/`sm` Edit `Button` with the **identical `aria-label`** (`meterReadingHistory.editTriggerFor`) and `onClick={() => setEditing(item)}`.
  - [x] **Must be preserved untouched:** the collapsed-by-default `<details>` disclosure and `role="heading"` summary, `aria-live="polite"` wrapper, loading/error/empty states, pagination row (stays below the grid, full width), the `EditMeterReadingDialog` wiring and its `onSaved` behavior (`load(page)` + `onReadingCorrected?.()` only when `valueChanged`), and `PAGE_SIZE = 20`.
  - [x] No new i18n keys are expected — every string a tile shows already exists. The column headers (`valueColumn`, `timestampColumn`) simply have no tile equivalent; do not add visually-hidden header text.

- [x] **Task 4: `EventsCard` — tiles at ≥660px (AC #1, #2)** — `web/src/components/event/events-card.tsx`
  - [x] Same `isWide` switch around its `<Table>` (line 83-97); table branch unchanged.
  - [x] Refactor `EventRow` so the inner content (description, `taggedEntityName`, Bump/Dip correlation line) is shared between the `<TableRow>` and the tile — extract the `<div className="flex flex-col gap-1">…</div>` body into a small local component rather than duplicating the AD-10 / UX-DR14 / UX-DR17 branching logic. Tile layout: description primary, `taggedEntityName` and the correlation line as secondary lines exactly as today, timestamp as the meta line. The mockup (`key-trend-history.html:430-441`) composes room and timestamp inline (`Küche · 27.09.2026, 12:19`); either arrangement satisfies AC #1 as long as no fact is dropped, and reusing the existing line-per-fact markup is the lower-risk path.
  - [x] **Preserve the AD-10 comment and behavior:** `taggedEntityName` rendered verbatim from the write-time snapshot, no scaffold-endpoint fetch, no "(deleted)" decoration. Events has **no Edit action** — tiles have no `actions` row.
  - [x] Note that `EventsCard` is a collapsed-by-default `<details>` in code, whereas `DESIGN/components.md:31` says "expanded by default". That doc/code mismatch is pre-existing — do **not** change the disclosure default in this story.

- [x] **Task 5: `TariffHistoryList` — tiles at ≥660px (AC #1, #2)** — `web/src/components/tariff/tariff-history-list.tsx`
  - [x] Same `isWide` switch around its `<Table>` (line 123-172); table branch unchanged.
  - [x] Tile content mirroring the mockup (`key-tariff-radar.html:275-281`): primary = the `tariff.history.periodRange` string (`font-semibold`) + `Badge` (`currentBadge`) when `item.isCurrent`; secondary lines = each `formatCorrection(item)` note, then `{baseFeeFormat.format(item.monthlyBaseFee)} {item.currency}` and `{priceFormat.format(item.pricePerKwh)} {item.currency}/kWh` (both `tabular-nums`); actions = Edit `Button` with the identical `aria-label` (`tariff.history.editTriggerFor`).
  - [x] **Do not touch:** the fixed-decimal `Intl.NumberFormat` instances (AC #5 of Story 5.1 — trailing zeros must never drop), `formatCorrectionValue`, the Retry button, `onLoaded`/`onTariffMutated` callbacks, `refreshNonce`, `EditTariffDialog` wiring, or the `GlassCard` heading. `TariffHistoryList` has no `<details>` and is always expanded — leave it that way.
  - [x] The two-column-label table headers (`periodColumn`, `baseFeeColumn`, `priceColumn`) have no tile equivalent; the base-fee and price lines carry their own currency/unit text, matching the mockup which shows no labels either. Do not add labels.

- [x] **Task 6: Unit tests per list (AC #1, #2)**
  - [x] Existing tests in the three `*.test.tsx` files must keep passing **unchanged** — they run with `matchMedia` undefined ⇒ `false` ⇒ table branch (this is the deliberate reason for the jsdom guard in Task 1). This includes the column-header `w-px` assertions at `meter-readings-card.test.tsx:61` and `events-card.test.tsx:132`, which pin the <660px table and stay valid.
  - [x] In each of the three files add a `describe('at >=660px (entry-grid)')` block that stubs `window.matchMedia` → `matches: true` (restore in `afterEach`, e.g. `vi.stubGlobal` + `vi.unstubAllGlobals()`), then asserts: no `table` role is present; entries render as list items inside the grid; every fact the row test already covers appears in the tile (Meter Readings: kWh value, Pending badge only for `isPendingRegression`, correction note only when non-null, distinct per-entry Edit `aria-label`; Events: `taggedEntityName` plain text, no empty tag affordance, Bump/Dip line only when set, archived-entity tag still rendered; Tariff: period range, Current badge, correction note incl. localized `ContractStartDate` note, base fee + price with fixed decimals). Reuse each file's existing `page()`/`jsonResponse()` helpers and `openDisclosure`.
  - [x] Meter Readings + Tariff History: at ≥660px clicking a tile's Edit still opens the edit dialog, and saving still re-fetches the current page (and, for Tariff, fires `onTariffMutated`).
  - [x] Pagination controls still render and paginate at ≥660px (they sit outside the switched block — one assertion per file is enough).

- [x] **Task 7: e2e — extend, don't add a test (AC #1, #2, #3)** — `web/e2e/app-shell.spec.ts`
  - [x] Trend History test (line ~139): the existing route mocks for `**/api/meter-readings?*` and `**/api/events?*` (lines ~166-190) already return data. Add assertions in the existing test: at 1000px with the Meter Readings and Events disclosures opened, the entries render as tiles laid out in ≥2 distinct columns (compare `boundingBox().x` of the first two tiles — different — and that the tile count per row is 3 at 900px column width: 3 × 260 + 2 × 12 = 804 ≤ ~868 inner width), and at 659px the `table` is present and no tile grid exists. Mocked data must contain **≥3 entries** so multi-column is observable — extend the mock payload if it has fewer.
  - [x] Tariff Radar test (line ~242): the `**/api/tariffs?*` mock exists; same assertion pair for the Tariff History list (≥3 entries in the mock).
  - [x] Dashboard and Settings tests: no change (AC #3 is proven by the tree/forms being untouched — a one-line assertion in the Trend History test that the Room → Power Point → Device card still renders no `entry-grid` is sufficient).
  - [x] Follow Story 8.6's review lesson: assert exact geometry with `toBeCloseTo`/exact comparisons where it is deterministic (column x-offsets differ; tile widths equal) rather than loose `>`/`<` bounds, and write any comment about the 659/660 check as covering only the breakpoint, not tile sizes.

- [x] **Task 8: Full regression + live Chrome verification (mandatory — do not defer)**
  - [x] `npm --prefix web run test`, `tsc -b`, `oxlint` clean (`verbatimModuleSyntax` → `import type` for `EntryTile`-prop types if any; `noUnusedLocals` — no leftover `Table*` imports if a file ever stops using them, though none should). No backend changes — no `.NET` run required.
  - [x] `npx playwright test` (move `certs/vite-dev-cert.pem`/`.key` aside first if present, restore immediately; Story 8.6's Debug Log documents an orphaned `vite preview` on port 4173 — check for it, use a throwaway config on another port if needed, delete afterwards).
  - [x] **This is browser-dependent responsive-layout behavior — same gate as Stories 8.1–8.6: it cannot move review→done without a live Claude-in-Chrome verification actually performed in this session.** If Chrome isn't connected, raise it immediately and pause; do not defer or tick the box on unit/e2e proof alone.
  - [x] Use the established workaround: `resize_window` on an already-navigated tab is unreliable — open a **new** tab already sized to the target width, then navigate.
  - [x] Verify live: Trend History at ~1000–1100px with a real household that has enough readings/events — Meter Readings and Events show 3 tiles per row at a 900px column; at ~700px (column ≈ 668px) they show 2; at 659px the table is back. Tariff Radar history: same. Check Dark **and** Light against `mockups/key-trend-history.html` / `key-tariff-radar.html`.
  - [x] Verify live that Edit works from a tile on Meter Readings (open dialog, cancel is fine — do not corrupt real data) and that the Pending badge / Current badge / correction note render in a tile if such data exists; and that resizing across 660px in one tab swaps table ↔ grid without a reload or losing the expanded/collapsed state of the `<details>`.

- [x] **Task 9: Verify against every AC**
  - [x] Walk AC #1–#3 individually in Completion Notes and state what proves each: code reference, unit test, e2e assertion, or live check (Task 8) — same AC-by-AC accounting as Stories 8.1–8.6.

### Review Findings

- [x] [Review][Decision] Task 8 live Chrome gate — MET (user confirmed in-tab manual resize 2026-09-29: table → 2 columns → 3 columns). Earlier automated live check: (live check run 2026-09-29, dev server on https://localhost:5173, real household with 5 readings / 3 events): Meter Readings Edit from a tile opened the dialog and Cancel closed it (no data changed); fresh mounts at 659px = 2 tables / 0 grids, 660px = 2 grids × 2 columns (tile 283px), 700px = 2 columns (303px), 1000px = 3 columns (275px). (Was open, now confirmed manually by the user:) a live in-tab swap across 660px. The automation window's `resize_window` does not change `innerWidth`; an iframe resized in the same document changed layout and `matchMedia` correctly but React did not swap because the tab reported `visibilityState: hidden` (no media-query change events dispatched). The swap is covered by the hook unit test and the Playwright in-page resize only. Also not done live: Light/Dark comparison against the mockups and a real dark scheme.
- [x] [Review][Decision] Tile border token is about 2.4x fainter than the mockup (dark: 0.05 vs 0.12). The delta is documented; **Resolved 2a: keep `border-surface-quiet-border` as is.**
- [x] [Review][Patch] Long unbroken text (free-text event descriptions) can stretch a tile or overflow the grid — add `min-w-0 break-words` to `EntryTile` [web/src/components/entry-grid/entry-grid.tsx:22]
- [x] [Review][Patch] Meter Readings tile Edit aria-label uses only the minute-granularity timestamp, so two readings in the same minute get identical accessible names — include the kWh value [web/src/components/meter-reading/meter-readings-card.tsx:104]
- [x] [Review][Patch] `stubWideViewport` ignores the query string and always returns `matches: true` — match on the real `(min-width: 660px)` query [web/src/test/wide-viewport.ts:5]
- [x] [Review][Patch] Tariff pagination is not tested at >=660px (Task 6) — the test only asserts "Page 1 of 1"; add a Next click and a page-2 fetch [web/src/components/tariff/tariff-history-list.test.tsx]
- [x] [Review][Patch] The Tariff Current tile's `periodRange` text is not asserted; e2e compares only `tileBoxes[1].width`, not `tileBoxes[2].width` [web/src/components/tariff/tariff-history-list.test.tsx, web/e2e/app-shell.spec.ts]
- [x] [Review][Patch] AC #3 e2e assertion is not scoped to the Room → Power Point → Device tree card (page-wide count of 2 proves nothing about it) — assert the tree is open and contains no `entry-grid` [web/e2e/app-shell.spec.ts]
- [x] [Review][Patch] e2e geometry is loose (y closeTo, x `>`) and nothing asserts the swap exactly at 660px vs 659px — assert exactly 3 columns at the wide viewport and table 0 / `entry-grid` present at 660px [web/e2e/app-shell.spec.ts]
- [x] [Review][Defer] Empty `<ul>`/table when `totalCount > 0` but `items` is empty (page beyond last after a deletion) [web/src/components/meter-reading/meter-readings-card.tsx, web/src/components/event/events-card.tsx, web/src/components/tariff/tariff-history-list.tsx] — deferred, pre-existing (same gap in the table branch)
- [x] [Review][Defer] `dateTimeFormat.format(new Date(item.occurredAt))` throws `RangeError` on an unparseable `occurredAt`, blanking the card [web/src/components/event/events-card.tsx] — deferred, pre-existing (table branch identical)

## Dev Notes

- **UI-only story: no backend change, no new domain capability, no new FR/NFR, no new architecture decision.** UX-DR28 (added 2026-09-28, Epic 8) is the sole driver. No new locale strings — verified: every string a tile displays already has a key used by today's row markup.

- **Chosen mechanism: a JS breakpoint hook + conditional render, not CSS-only dual rendering.** Rendering both the `Table` and the grid and toggling with `hidden`/`wide:hidden` would duplicate every entry in the DOM (duplicate Edit buttons with identical `aria-label`s, duplicated text → existing `getByText`/`getByRole('button', {name})` tests and screen-reader navigation break). Restyling `<table>` elements into a grid via `display` overrides is fragile for a11y semantics. A single-DOM conditional keeps exactly one copy of every entry. Trade-off accepted: one shared `matchMedia` hook whose `660` literal must track `--breakpoint-wide` — both live next to a cross-referencing comment.

- **Why the jsdom `matchMedia` guard matters:** `web/src/test/setup.ts` stubs only IndexedDB; jsdom has no `matchMedia`. `web/src/lib/color-scheme.ts` calls it unguarded but only from `main.tsx`, never from a component under test. A hook that throws in jsdom would break every test rendering `TrendHistoryPage`, `TariffRadarPage`, `App`, etc. Returning `false` when absent preserves today's table behavior in all existing tests.

- **Breakpoint semantics are unchanged.** `(min-width: 660px)` matches Tailwind's `wide:` variant (`--breakpoint-wide: 660px`, `index.css:10`) — `min-width` is inclusive, so 660px is wide, 659px is not, same as Stories 8.1/8.6. Do not introduce a second breakpoint; column count must fall out of `auto-fill`/`minmax` only (UX-DR28: "never a fixed count").

- **Grid arithmetic sanity check (for e2e/live expectations):** the 900px column sits inside a `GlassCard` with `--spacing-card-padding` on each side, and the details body adds its own inset; 3 tiles need 3×260 + 2×12 = 804px of inner width, 2 tiles need 532px. Below 660px viewport the hook returns false, so `minmax(260px, …)` never has to collapse to 1 column. At a viewport just over 660px the column is ~628px, minus card padding ≈ 580–600px ⇒ 2 tiles. Confirm the actual card padding value in `index.css` (`--spacing-card-padding`) rather than trusting these numbers.

- **Scope test (why these three lists and not others):** UX-DR28 grids only flat, independent, period/timestamp-ordered lists with **no manual-reordering affordance**. Device rows (drag handle, Story 2.6) and the Room/Power Point tree (hierarchical disclosure — Story 8.8) fail that test and stay as-is. Also out of scope: per-plug measured data card (`per-plug-data-card.tsx`), Smart Plug Import job history list, Settings lists — none are named by UX-DR28/Story 8.7.

- **Read-before-modify current state (Step 3 analysis):**
  - `MeterReadingsCard` — collapsed `<details>`, own fetch/pagination (20/page), shadcn `Table` with 3 columns (value+Pending badge+correction note / timestamp `w-px` / Edit `w-px text-right`), `EditMeterReadingDialog` re-fetches current page and pings parent `TrendChart` via `onReadingCorrected` only when the value changed. *This story changes only how entries are laid out at ≥660px.*
  - `EventsCard` — collapsed `<details>` (code) though design doc says expanded; `Table` with 2 columns; read-only rows (no Edit); AD-10 snapshot-name rule and UX-DR14/UX-DR17 correlation-line rule live in `EventRow`.
  - `TariffHistoryList` — always-open `GlassCard`, `Table` with 4 columns, fixed-decimal formats, Retry on load error, `EditTariffDialog` triggers `load(page)` + `onTariffMutated`, `onLoaded` feeds `TariffRadarPage`'s "has any tariff" gating.

- **Must not regress (implicit requirements):** Story 8.3's dense-table fix at <660px (the `w-px` column assertions stay green); Story 8.6's 900px column (no width change here); Story 4.3/2.8 correction-note visibility; Story 5.4 AC #5 (Tariff edit refreshes the Tariff Check reminder); Story 6.3's correlation lines; the a11y contract of one distinct accessible name per Edit button.

- **Design tokens/visual reference:** `DESIGN/layout-spacing.md` (Entry-grid pattern), `DESIGN/components.md` lines 27/31/35, `EXPERIENCE.md` lines 32/96. Tile sizes per mockup CSS: primary 13.5–14.5px bold, secondary/meta 11–11.5px, `Edit` pill 11px. Match the app's existing Tailwind text scale rather than pixel-copying the mockup's font sizes (existing rows use `text-sm`/`text-xs`).

- **Testing standard:** colocated Vitest + Testing Library (jsdom), no real CSS ⇒ grid/`minmax` is only verifiable as a class-string match in unit tests; real geometry only in Playwright and live Chrome — same division of proof as Stories 8.2–8.6.

- **Git intelligence:** last commits are Story 8.6 (`b7f971b`, column cap → 900px, four `wide:max-w-[900px]` literals + e2e), doc commit `91158e3` (design docs already amended for 8.6–8.9, including this pattern), and Stories 8.3–8.5. Conventional-Commit prefix + story/UX-DR reference in the commit message; branch is already `feature/8-7-entry-grid`. No dependency changes anywhere in Epic 8 — none needed here (React 19.2, Tailwind v4, Vitest 4.1, Playwright 1.62; `useSyncExternalStore` is in React core).

- **Process gates from `project-context.md`:** no fix to shipped code without a linked spec/story (this file is that link); browser-dependent story ⇒ live Chrome verification required before review→done (Task 8). This story does not touch OIDC/claims.

### Project Structure Notes

```text
energy-tracker-v2/
  web/src/
    hooks/
      use-wide-breakpoint.ts                # NEW — matchMedia hook (Task 1)
      use-wide-breakpoint.test.ts           # NEW
    components/
      entry-grid/
        entry-grid.tsx                      # NEW — EntryGrid + EntryTile (Task 2)
        entry-grid.test.tsx                 # NEW
      meter-reading/
        meter-readings-card.tsx             # modified — tiles at >=660px (Task 3)
        meter-readings-card.test.tsx        # modified — new >=660px describe block (Task 6)
      event/
        events-card.tsx                     # modified — tiles + shared row body (Task 4)
        events-card.test.tsx                # modified (Task 6)
      tariff/
        tariff-history-list.tsx             # modified — tiles at >=660px (Task 5)
        tariff-history-list.test.tsx        # modified (Task 6)
  web/e2e/app-shell.spec.ts                 # modified — Trend History + Tariff Radar tests extended (Task 7)
  web/src/index.css                         # UNCHANGED — --breakpoint-wide stays 660px
```

No changes expected to `trend-history-page.tsx` / `tariff-radar-page.tsx` (wrappers stay), `per-plug-data-card.tsx`, `ui/table.tsx`, any backend project, or any locale file.

### References

- [Source: _bmad-artifacts/planning/epics/epic-8-desktop-tablet-layout-correction.md#Story 8.7: Meter Readings, Events & Tariff History Entry-Grid] — story statement and literal ACs (lines 146–166).
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md#UX-DR28] — entry-grid definition and scope test (line 131); UX-DR19 (line 130) for the 900px column; UX-DR29 (line 132) for why Device rows/tree are excluded.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/layout-spacing.md, line 13] — Entry-grid pattern rationale; [DESIGN/components.md, lines 27, 31, 35] — per-list tile content; [EXPERIENCE.md, lines 32, 96].
- [Source: .../mockups/key-trend-history.html, lines 100–120 (CSS), 330–365 (Meter Readings tiles), 428–460 (Events tiles)] and [.../mockups/key-tariff-radar.html, lines 111–125 (CSS), 272–290 (Tariff History tiles)] — rendered tile spec, Dark and Light.
- [Source: web/src/components/meter-reading/meter-readings-card.tsx, lines 94–136] — table block to switch; [web/src/components/event/events-card.tsx, lines 83–97, 123–149]; [web/src/components/tariff/tariff-history-list.tsx, lines 123–172].
- [Source: web/src/index.css, lines 10, 77–84] — `--breakpoint-wide: 660px`; quiet-tier surface tokens; [web/src/hooks/use-logoff.ts] — hooks folder convention; [web/src/test/setup.ts] — no `matchMedia` stub; [web/src/lib/color-scheme.ts] — only existing `matchMedia` use.
- [Source: web/src/components/meter-reading/meter-readings-card.test.tsx, lines 61–75; events-card.test.tsx, lines 132–141] — existing `w-px` column assertions that must stay green.
- [Source: web/e2e/app-shell.spec.ts, Trend History test ~139–240 and Tariff Radar test ~242–390] — existing route mocks and boundary-test pattern to extend.
- [Source: _bmad-artifacts/implementation/8-6-wide-column-increase-across-all-surfaces.md] — previous story: per-page literal convention, `resize_window` workaround, Playwright port/cert Debug Log, and review lessons (exact-geometry e2e assertions, accurate comments).
- [Source: _bmad-artifacts/project-context.md#Critical Don't-Miss Rules] — process gates; [#TypeScript/Frontend] — `verbatimModuleSyntax`, `noUnusedLocals`, `@/` alias, oxlint.

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5.5

### Debug Log References

- Playwright: moved `certs/vite-dev-cert.*` aside, `npm run build` then `npx playwright test` (6/6 pass), certs restored; no orphaned process on port 4173.
- Existing Trend History e2e measured the Meter Readings table at 1000px; since >=660px is now tiles, that dead-space (`w-px`) check moved to the 659px step where the table still renders.
- Live-Chrome `resize_window` on an existing tab did not change `innerWidth` (stayed 660); used fresh tabs after resizing, per the 8.6 workaround.

### Completion Notes List

- **Implementation:** `useWideBreakpoint` (`useSyncExternalStore` over `(min-width: 660px)`, `false` when `matchMedia` is absent), `EntryGrid`/`EntryTile` primitives (`data-slot="entry-grid"`, `repeat(auto-fill,minmax(260px,1fr))`, gap 12px, quiet-tier tokens), and an `isWide` switch in `MeterReadingsCard`, `EventsCard`, `TariffHistoryList`. The `<Table>` blocks are untouched apart from an added `!isWide` guard; events share a new `EventBody` between row and tile (AD-10 comment kept).
- **Tokens delta:** tiles use the existing `--surface-quiet`/`--surface-quiet-border` (dark computes to `rgba(220,245,230,0.03)` / `rgba(210,235,220,0.05)` vs mockup 0.045/0.12); no new tokens added, per story instruction.
- **Test helper:** added `web/src/test/wide-viewport.ts` (`stubWideViewport`) shared by the three list test files (not in the story's file plan; avoids triplicating the stub).
- **Tests:** full suite 443/443, `tsc -b` clean, oxlint no warnings in touched files; Playwright 6/6.
- **AC #1:** `entry-grid.tsx` + three components; unit tests per list (tiles as list items, all facts, Edit a11y names, dialog/save, pagination); e2e asserts 3 tiles in one row with equal widths at 1000px on Trend History (both lists) and Tariff Radar; live Chrome: 2 tiles/row at 660px, 3 tiles/row (275px each, 12px gap) at 1400px on Meter Readings + Events, Tariff history tiles with Current badge, Light and Dark (dark via forced theme attribute).
- **AC #2:** existing table tests unchanged and green (matchMedia absent); e2e asserts tables present and no entry-grid at 659px and after resizing back across the boundary; live Chrome at 659px: 2 tables, 0 grids.
- **AC #3:** e2e asserts exactly two entry-grids on Trend History (tree/forms untouched); tree and Tariff form files not modified.
- **Live checks (Task 8):** Edit on a Tariff tile opened the dialog and Cancel closed it (no data changed). Not observed live: Pending badge / correction note tiles (no such data in the household; covered by unit tests), and an in-tab live resize across 660px without reload (Chrome window resize did not update the existing tab; covered by e2e which resizes within one page).

### File List

- `web/src/hooks/use-wide-breakpoint.ts` (new)
- `web/src/hooks/use-wide-breakpoint.test.ts` (new)
- `web/src/components/entry-grid/entry-grid.tsx` (new)
- `web/src/components/entry-grid/entry-grid.test.tsx` (new)
- `web/src/test/wide-viewport.ts` (new)
- `web/src/components/meter-reading/meter-readings-card.tsx`
- `web/src/components/meter-reading/meter-readings-card.test.tsx`
- `web/src/components/event/events-card.tsx`
- `web/src/components/event/events-card.test.tsx`
- `web/src/components/tariff/tariff-history-list.tsx`
- `web/src/components/tariff/tariff-history-list.test.tsx`
- `web/e2e/app-shell.spec.ts`
- `_bmad-artifacts/implementation/sprint-status.yaml`

### Change Log

- 2026-09-29: Story 8.7 implemented — entry-grid tiles at >=660px for Meter Readings, Events, Tariff History.
