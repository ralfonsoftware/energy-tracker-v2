---
baseline_commit: 2a3589b
---

# Story 8.8: Settings Rooms/Power Points Grid-with-Expand & Section Pairing

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member with several Rooms and Power Points,
I want the Rooms/Power Points list to use the available width and short Settings sections to sit side by side,
so that I don't scroll through a long column of one-line collapsed rows while most of the screen sits empty.

## Acceptance Criteria

1. **Given** the "Rooms, Power Points & Devices" tree at an available width ≥660px, **when** Room and Power Point rows are collapsed, **then** they render in a `repeat(auto-fill, minmax(220px, 1fr))` grid instead of a single-column list (UX-DR29).
2. **Given** a Room or Power Point tile in that grid, **when** it is expanded, **then** it spans the full grid width (`grid-column: 1 / -1`) and reveals its children inline below the summary row — the same `details`/`summary` accordion behavior as Story 8.5, just reflowed while collapsed.
3. **Given** Device rows (the leaf level, carrying Story 2.6's drag-to-reorder handle), **when** rendered at any width, **then** they remain a plain single-column list, never gridded — dragging to reorder across a 2D grid is a materially more ambiguous gesture than dragging down a list.
4. **Given** the "AI Plausibility Check" and "Household" sections at ≥660px, **when** rendered, **then** they sit side by side in one row instead of each alone on a full-width row (UX-DR30). **And** "Yearly Baseline" and "Rooms, Power Points & Devices" are not paired with anything and stay full width.
5. *(Implicit, from the same epic — not a new AC)* **Given** any of the above at <660px, **when** rendered, **then** the layout is byte-for-byte what Story 8.5/8.7 shipped: flat single-column sections, bordered-row tree, no grid. Nothing in this story changes phone layout.

## Tasks / Subtasks

- [x] **Task 1: Pair "AI Plausibility Check" + "Household" (AC #4, #5)** — `web/src/components/settings/settings-page.tsx`
  - [x] Wrap the existing AI Plausibility `div` and Household `div` (currently siblings at lines ~91-100, each carrying `wide:order-2` / `wide:order-3`) in ONE new wrapper: `flex flex-col gap-[var(--spacing-card-gap)] wide:flex-row wide:order-2` (flex-col below 660px keeps today's stacked look). Give each of the two children `wide:flex-1 wide:basis-0 wide:min-w-0` (mockup: `.section-pair-row { display:flex; gap:14px } > .card { flex:1 1 0 }`). Use the existing `--spacing-card-gap` (18px) for the gap rather than the mockup's 14px — no new spacing literal; note the delta in Completion Notes.
  - [x] Drop the children's own `wide:order-N` (they're now inside the wrapper; `order` only applies among flex siblings of the same container) and **renumber the outer siblings**: Yearly Baseline `wide:order-1`, pair wrapper `wide:order-2`, Rooms `wide:order-3`, Data `wide:order-4`. Story 8.5's `wide:order-N` mechanism exists so the wide-only visual order (Household above Rooms) differs from DOM order without moving DOM — **keep DOM order exactly as today** (Baseline, AI, Household, Rooms, Data); the wrapper must not reorder the DOM (tab order / screen-reader order unchanged).
  - [x] The logoff `Button` (`wide:hidden self-start`, last child of the outer column) is untouched.
  - [x] Each section keeps its own `SectionLabel` (`hidden wide:block`, `<h2>`). Inside the pair, both labels sit on the same row and therefore align at the same y — that is the intended result.
  - [x] Do **not** touch `InviteMemberRow`, `AiPlausibilityForm`, `YearlyBaselineForm`, or `DataExportPanel/DataImportPanel`. Verify visually that the Household section (a single 1-row `GlassCard`) next to the taller AI card does not look broken — default `align-items: stretch` is what the mockup shows; if the shorter card stretching looks wrong live, `wide:items-start` is an acceptable deviation (note it).

- [x] **Task 2: Room grid (AC #1, #2, #5)** — `web/src/components/tagging-scaffold/tagging-scaffold-manager.tsx` (render block ~lines 475-690; read it fully before editing)
  - [x] Pure CSS via `wide:` variants — **no `useWideBreakpoint` hook, no conditional rendering.** Rationale: unlike Story 8.7 (table ↔ tiles, two different DOM shapes), this is the *same* `<details>` tree restyled, so a hook would only add risk (hydration of open state, remount on resize losing `open`). CSS also keeps every existing tagging-scaffold test running unchanged in jsdom (no media queries evaluated there).
  - [x] The `GlassCard className="gap-0 p-0"` (line ~496) currently holds the rooms as stacked bordered rows. Wrap the mapped rooms in a container: `flex flex-col` below 660px (identical to today) and `wide:grid wide:grid-cols-[repeat(auto-fill,minmax(220px,1fr))] wide:gap-2.5 wide:p-3` at ≥660px (mockup `.room-grid`: gap 10px). Keep `GlassCard` as the outer card (mockup: `.card` contains the grid).
  - [x] Room `<details>` (line ~614, class `group/room ${ROOM_ROW_BORDER_CLASS}`): at `wide:` drop the row border (`wide:border-b-0`) and give it tile styling — `wide:rounded-glass-sm wide:border wide:border-surface-quiet-border wide:bg-surface-quiet` (existing tokens from Story 8.7's `EntryTile`; **do not add new rgba literals or tokens**). The `ROOM_ROW_BORDER_CLASS` constant is shared by the suppressed-row `<div>` branch on purpose ("can't visually drift") — keep them in sync (see the suppressed-wrapper bullet below).
  - [x] **Open state spans the row:** `wide:open:col-span-full` on the Room `<details>` (Tailwind v4 `open:` = `details[open]`; verify the compiled CSS actually emits it — if `wide:open:` does not stack in this Tailwind version, use an arbitrary variant `wide:[&[open]]:col-span-full`). Open tile gets a slightly stronger surface per mockup (`.room-tile.open` bg 0.09 vs 0.06) — optional; if there is no existing token for it, skip it rather than inventing one.
  - [x] **Summary row in a 220px tile:** the summary already shows `name — N Power Points` (the `hidden wide:inline` count from Story 8.5) and, right-aligned, the `ArchivedBadge`. At 220px a long name (German: "Schlafzimmer — 3 Steckdosen") will wrap. Allow it: add `min-w-0` / `wide:flex-wrap` or `break-words` so nothing overflows the tile; do **not** truncate with ellipsis (the count is a fact the user needs, per Story 8.5 AC). Check with the longest realistic German strings live.
  - [x] Rename/Delete icon row and the "Add Power Point" text link stay inside the `<details>` body exactly as today (visible only when open — they already are).

- [x] **Task 3: Power Point grid inside an open Room (AC #1, #2, #5)** — same file
  - [x] `powerPointsList` (line ~530, `flex flex-col pl-4`) becomes, at `wide:`, its own auto-fill grid: `wide:grid wide:grid-cols-[repeat(auto-fill,minmax(220px,1fr))] wide:gap-2.5 wide:pl-0 wide:px-3.5 wide:pb-2` (keep `flex flex-col pl-4` below 660px). Mockup nests `.pp-grid` in a `.pp-nested` block inside the open room tile.
  - [x] Power Point `<details>` (line ~577, `group/pp border-t …`): same tile treatment as Rooms (`wide:border-t-0` + tile surface classes) and `wide:open:col-span-full`. Power Point tile text is 12.5px/600 in the mockup vs 13px/700 for Rooms — the current summary already uses `text-sm font-semibold` for both; leave typography alone.
  - [x] The "Add Power Point" link is a sibling *after* `{powerPointsList}` inside the Room `<details>` — it is not a grid item and needs no change. Same for "Add Device" after `{deviceList}` inside the Power Point.

- [x] **Task 4: Suppressed-row wrappers must not become stray grid cells (AC #1, #2)** — same file (**easy to miss**)
  - [x] AC #5 of Story 1.9 (archived parent with live children): when `suppressRoomRow` is true the Room renders as a plain `<div key className={ROOM_ROW_BORDER_CLASS}>` wrapping `powerPointsList` (line ~604-611); when `suppressPowerPointRow` is true the Power Point renders as a plain `<div className="border-t …">` wrapping `deviceList` (line ~566-573). At ≥660px these wrappers are **direct grid children**, so as-is each would occupy a single 220px cell and squeeze its whole subtree into it. Add `wide:col-span-full` to both, plus `wide:border-b-0` / `wide:border-t-0` so no row border leaks into the grid.
  - [x] Add a unit test that the suppressed wrappers carry `wide:col-span-full` (see Task 6) and check live with `showArchived` toggled off on a household that has an archived Room with live Power Points (or seed one in e2e).

- [x] **Task 5: Devices stay a plain list (AC #3)**
  - [x] `deviceList` (line ~536, `flex flex-col gap-0.5 pb-2 pl-8`) and its rows are **not modified at all**. Add a short code comment above `deviceList` stating why it is deliberately excluded from the grid (Story 2.6 drag handle, UX-DR29) so a future contributor doesn't "finish" the pattern. Each Device row keeps rename/delete/move controls and — if present in the code at the time of implementation — the Story 2.6 drag handle unchanged.
  - [x] Note for the dev: the current code renders Move via a dialog (`move-device`), not a drag handle, in this file. Do not go looking for drag code to guard; AC #3 is satisfied by **not gridding** the leaf level and proving it (Task 6/7).

- [x] **Task 6: Unit tests (AC #1-#5)**
  - [x] Existing `tagging-scaffold-manager.test.tsx` (801 lines) and `settings-page.test.tsx` (237 lines) must pass **unchanged** — every change here is class-only, and neither file asserts on the affected classes (verified by grep: only the dual-render Add-button and `SectionLabel` comments mention `wide:`).
  - [x] `settings-page.test.tsx`: add a test that the AI Plausibility and Household sections share one wrapper element whose class contains `wide:flex-row`, and that Yearly Baseline, Rooms and Data are **not** inside that wrapper (AC #4 negative half). Also assert DOM order stays Baseline → AI → Household → Rooms → Data (guards the "don't reorder DOM" rule). jsdom has no layout — class-string / structure assertions only, same limitation as Stories 8.3–8.7.
  - [x] `tagging-scaffold-manager.test.tsx`: add a `describe('grid-with-expand (>=660px)')` block — with 2+ rooms each with power points and devices: (a) the rooms container's class contains `wide:grid` and `minmax(220px,1fr)`; (b) an opened Room's `<details>` has `wide:open:col-span-full` (or the chosen variant) and its Power Point container carries the same grid class; (c) the **Device list container carries no `grid` class at any level** and Device rows are still in a `flex-col` list; (d) suppressed-Room and suppressed-Power-Point wrappers (archived parent, live child, `showArchived` false) carry `wide:col-span-full`; (e) open/close a Room via its `summary` and confirm `open` toggles and child controls (Rename/Delete/Add Power Point) are reachable — i.e. the accordion behavior is unchanged.

- [x] **Task 7: e2e — extend, don't add a test (AC #1-#5)** — `web/e2e/app-shell.spec.ts`, the Settings test (~line 493)
  - [x] Extend the mocked `**/api/rooms`, `**/api/power-points`, `**/api/devices` routes (currently 1 Room / 1 Power Point / 2 Devices) to **≥4 Rooms** and **≥4 Power Points under one Room**, plus ≥3 Devices under one Power Point, so multi-column is observable. **Careful — existing assertions collide with richer data:** `page.getByText(/1 Power Point\b/)`, `getByText('Living Room')`, `getByText('Wall outlet')`, `getByText(/2 Devices/)` and the `.last()` Add-button locators become strict-mode ambiguous if several Rooms have one Power Point or the same counts. Give the seeded rooms distinct counts/names or scope the locators to the Living Room `details` element (e.g. `page.locator('details', { hasText: 'Living Room' })`).
  - [x] At 1000px (column exactly 900px): AI Plausibility and Household headings have the **same y** (`toBeCloseTo`, 0-1px) and **different x**; the pair's combined width equals the column; Yearly Baseline and Rooms headings/cards span the full 900px column (compare card widths, not just headings). At 659px: AI and Household headings are hidden, and their content cards stack (Household card y > AI card y, same x).
  - [x] At 1000px with all Rooms collapsed: ≥2 Room tiles share a y and differ in x (compare `boundingBox()`); first-row tile count = `floor((innerWidth + gap) / (220 + gap))` bounded by the arithmetic — compute the expected count from the real card padding (`--spacing-card-padding`, plus the new `wide:p-3` on the grid card) instead of trusting the numbers in Dev Notes; assert **exact** values where deterministic (Story 8.6/8.7 review lesson: no loose `>`/`<` bounds).
  - [x] Click one Room's summary: its box width becomes equal to the grid container width and it sits alone on its row (the next tile's y is greater). Then open a Power Point inside it: same full-width assertion within the nested grid. Devices inside: all rows share the same x and have strictly increasing y (single column — AC #3).
  - [x] At 659px the tree is the pre-story single-column bordered-row list (tiles share x, increasing y; no `grid` display — `getComputedStyle(container).display` is `flex`/`block`, not `grid`) — AC #5.
  - [x] Keep the existing 8.5 assertions (labels visible in y-order, Invite dialog, Add-link visibility) — the y-order check is `toEqual(sorted)` and still holds when two headings share a y.

- [x] **Task 8: Full regression + live Chrome verification (mandatory — do not defer)**
  - [x] `npm --prefix web run test`, `tsc -b`, `oxlint` clean. No backend change — no `.NET` run required. No locale change expected: the count strings (`taggingScaffold.powerPointCount`, `deviceCount`) and Add links already exist from Story 8.5.
  - [x] `npx playwright test` (move `certs/vite-dev-cert.pem`/`.key` aside first if present, restore immediately; check for an orphaned `vite preview` on port 4173 per the Story 8.6/8.7 Debug Logs; use a throwaway config on another port if needed and delete it afterwards).
  - [x] **This is browser-dependent responsive-layout behavior — same gate as Stories 8.1–8.7: it cannot move review→done without a live Claude-in-Chrome verification actually performed in this session.** If Chrome isn't connected, raise it immediately and pause for the user to connect it; do not defer or tick the box on unit/e2e proof alone (`project-context.md` Process gates).
  - [ ] Established workaround: `resize_window` on an already-navigated tab is unreliable (Story 8.6/8.7) — open a **new** tab already sized to the target width, then navigate; an in-tab live resize across 660px needs a manual resize by the user (Story 8.7's review found the automation window cannot do it) — ask for it rather than skipping it.
  - [ ] Verify live on the real household (Auth0 login, https://localhost:5173): Settings at ~1000-1400px — AI Plausibility + Household side by side, Baseline and Rooms full width; collapsed Rooms in a multi-column grid; open a Room → spans the row and shows Power Points in a nested grid; open a Power Point → spans and shows a single-column Device list; Rename/Delete/Move/Add still work (open a dialog and cancel — **do not corrupt real data**); Show-archived toggle on/off with an archived Room; German locale long strings don't overflow a 220px tile; Dark **and** Light against `mockups/key-settings.html`; at 659px everything is the flat Story 8.5 layout.

- [x] **Task 9: Verify against every AC**
  - [x] Walk AC #1-#5 individually in Completion Notes and state what proves each (code reference, unit test, e2e assertion, or live check) — same AC-by-AC accounting as Stories 8.1-8.7.

## Dev Notes

- **UI-only story: no backend change, no new domain capability, no new FR/NFR, no new architecture decision.** UX-DR29 and UX-DR30 (added 2026-09-28, Epic 8) are the sole drivers. `web/` files only.

- **Chosen mechanism: CSS-only `wide:` variants, no JS breakpoint hook.** Story 8.7 needed `useWideBreakpoint` because it swaps `<Table>` ↔ tile DOM (two shapes, duplicate content if both rendered). Here the DOM is identical at every width — same `<details>`/`<summary>` tree — only its layout container changes, so plain Tailwind `wide:` classes are the smaller, safer change. This also matches Story 8.5's dual-render/`wide:` precedent in this exact file and keeps jsdom tests untouched (jsdom evaluates no media queries). Do **not** import `useWideBreakpoint` here.

- **Why `col-span-full` on `<details[open]>` works and where it can bite:** a `<details>` is one grid item whose children (summary + body) live inside it, so spanning the whole `<details>` is what reveals children "inline below the summary row" (AC #2) with zero DOM change. Grid auto-placement is *sparse* (default `grid-auto-flow: row`): when an item spans the full row, preceding items stay in their row and following items start the next row, leaving possible blank cells at the end of the preceding row — the mockup accepts this. **Never use `grid-auto-flow: dense`** — it would visually reorder tiles relative to DOM/tab order.

- **Native `<details>` caveats to respect:** `open` is toggled by the browser on `summary` click/Enter/Space — no React state exists for it and none should be added (that would be a behavior change, and would fight the browser). Because the tree is unchanged structurally, expanded/collapsed state survives resizing across 660px automatically; verify that live (open a Room, cross 660px, it stays open).

- **Read-before-modify current state (Step 3 analysis, files marked UPDATE):**
  - `settings-page.tsx` — `SettingsPage` renders `<main>` → `div[data-slot="settings-content"]` (`wide:max-w-[900px]` from Story 8.6, mx-auto) → header row → one `flex flex-col gap-[var(--spacing-card-gap)]` containing five section `div`s (each `flex flex-col gap-[var(--spacing-card-gap)] wide:order-N`, N=1..5: Baseline, AI, Household, Rooms, Data) + the `wide:hidden` logoff button, then the logoff `Dialog` and `NavChrome`. Each section starts with `<SectionLabel>` (an `<h2 hidden wide:block>`, Story 8.5 Task 2) above the form/panel, whose own inner heading is hidden at `wide:`. *This story changes only how sections 2 and 3 are grouped.* **Must preserve:** DOM order, the `SectionLabel` `<h2>`s (screen-reader headings must survive pairing), `wide:mx-auto wide:max-w-[900px]`, the logoff button/dialog wiring, `NavChrome` props.
  - `tagging-scaffold-manager.tsx` (853 lines) — `TaggingScaffoldManager` fetches `/api/rooms`, `/api/power-points`, `/api/devices` in parallel, builds `powerPointsByRoom`/`devicesByPowerPoint` maps, renders a header row (`wide:hidden` `<h2>`, show-archived eye toggle, "Add Room"), then a `GlassCard gap-0 p-0` of Room `<details class="group/room border-b …">` → `<summary>` (chevron, name, `hidden wide:inline` "— N Power Points", `ArchivedBadge`) → icon-action row (`Pencil`/`Trash2`, `wide:hidden` filled "Add Power Point") → `powerPointsList` (`flex flex-col pl-4`) of Power Point `<details class="group/pp border-t …">` (same shape; plus `Move`, "Add Device") → `deviceList` (`flex flex-col gap-0.5 pb-2 pl-8`) → `hidden wide:inline-flex` text-link "Add …". Suppressed-parent branches render plain `<div>` wrappers (AC #5 of Story 1.9). All CRUD dialogs live below the card and are untouched. *This story changes only the containers' and `<details>`' class strings — no handlers, no state, no props, no fetch.* **Must preserve:** the two-branch (suppressed `<div>` vs `<details>`) structure and the `roomHasVisibleChildren`/`powerPointHasVisibleChildren` filtering; the `group/room` / `group/pp` chevron-rotation groups (`group-open/room:rotate-90`, `group-open/pp:rotate-90`); the dual-rendered Add controls; every `aria-label`; the archived toggle's no-persistence rule.

- **Do not mix up `group/room` with the new `open:` variant.** Chevron rotation uses the named-group `group-open/room:` variant already; the new `wide:open:col-span-full` styles the `<details>` element itself. Both coexist on the same element.

- **Tile tokens — reuse, don't invent (Story 8.7 precedent):** `bg-surface-quiet`, `border-surface-quiet-border`, `rounded-glass-sm` (14px = mockup `.room-tile` radius). The mockup's tile is glass-tier (`rgba(220,245,230,0.06)` + `rgba(210,235,220,0.13)` dark; `rgba(255,255,255,0.6)` + `rgba(40,70,50,0.12)` light) — 8.7 found `surface-quiet` dark border is ~2.4x fainter than a comparable mockup border and the user accepted that delta ("keep as is"). Apply the same decision; note any visible delta in Completion Notes; do not add rgba literals. (Existing tree code already contains a few rgba literals on borders — leave those, don't spread them.)

- **Grid arithmetic sanity check (verify against real CSS, don't trust):** 900px column → `GlassCard` (`gap-0 p-0`, no padding today) + new `wide:p-3` (12px) grid padding → ≈ 876px inner → 3 tiles of 220px need 3×220 + 2×10 = 680px, 4 tiles need 4×220 + 3×10 = 910px > 876 ⇒ **3 columns at a 900px column**, and the tiles stretch to ≈ 285px. At a viewport just over 660px the column ≈ 628px → inner ≈ 604px → 2 columns (2×220+10 = 450 ≤ 604 < 3-col 680). Inside an open Room the nested Power Point grid has extra `px-3.5` inset → likely 2-3 columns. Confirm `getComputedStyle` values live and derive e2e expectations from measured tile widths.

- **Scope test (what is *not* in this story):** Tariff Comparison side-by-side is Story 8.9 (UX-DR31). Entry-grid lists are Story 8.7 (done). Device rows and the tree's CRUD dialogs, the show-archived toggle, and the header row above the card are unchanged. "Yearly Baseline" and "Data" are not paired with anything (UX-DR30: "Pairing is reserved for genuinely short, unrelated sections — it is not a rule to apply to every Settings section uniformly", `layout-spacing.md`).

- **Header row / Add Room:** the mockup places `tree-toolbar` (Add Room) *inside* the card above the grid, but the shipped page keeps the header row (eye toggle + "Add Room") *above* the card. Story 8.5 shipped that and it isn't part of UX-DR29's ACs — leave it. (If it visibly clashes live, raise it as a follow-up rather than restructuring here.)

- **Must not regress (implicit requirements):** Story 8.5's section labels, compact preset sizing, "Invite a member" row + dialog (the dialog is a portal — pairing must not clip it: check it still opens correctly from the narrower Household half), Story 8.6's 900px column, Story 1.9's suppressed-parent rendering, Story 2.6's Device move/reorder affordances, Story 8.7's entry-grids elsewhere. Keyboard: Tab order follows DOM order (unchanged); `summary` remains focusable and toggles on Enter/Space.

- **Testing standard:** colocated Vitest + Testing Library (jsdom) — class-string/structure assertions only; real geometry only in Playwright and live Chrome. Same division of proof as Stories 8.2-8.7. The Story 8.5 deferred item "dual-render narrow/wide assertions disambiguated only by DOM-order indexing" is still in force; follow the established `getAllByRole(...)` idiom in these files rather than refactoring it.

- **Git intelligence:** last commits are Story 8.7 (`2a3589b`: `EntryGrid`/`EntryTile`, `useWideBreakpoint`, e2e geometry checks, `stubWideViewport` helper), Story 8.6 (`b7f971b`, column cap → 900px), the doc commit `91158e3` (design docs and `key-settings.html` already updated for 8.6-8.9), and Story 8.5 (`5b30138`: sectioning, `SectionLabel`, tree summary counts, `wide:` dual-render Add controls — this story's direct predecessor in the same two files). Commit style: Conventional-Commits prefix + story/UX-DR reference; branch is already `feature/8-8-settings`. No dependency changes in Epic 8 — none needed (React 19.2, Tailwind v4, Vitest 4.1, Playwright 1.62).

- **Process gates from `project-context.md`:** no fix to shipped code without a linked spec/story (this file is that link); browser-dependent story ⇒ live Chrome verification required before review→done (Task 8). This story does not touch OIDC/claims. AD-1..AD-19: none apply (frontend layout only).

### Project Structure Notes

```text
energy-tracker-v2/
  web/src/components/
    settings/
      settings-page.tsx                     # modified — pair wrapper, renumbered wide:order (Task 1)
      settings-page.test.tsx                # modified — pairing/structure tests (Task 6)
    tagging-scaffold/
      tagging-scaffold-manager.tsx          # modified — class-only: room grid, PP grid, tiles, open span, suppressed wrappers (Tasks 2-5)
      tagging-scaffold-manager.test.tsx     # modified — grid-with-expand describe block (Task 6)
  web/e2e/app-shell.spec.ts                 # modified — Settings test extended (Task 7)
  web/src/index.css                         # UNCHANGED — no new tokens
```

No changes expected to `section-label.tsx`, `invite-member-row.tsx`, any form/panel component, any locale file, `use-wide-breakpoint.ts`, `entry-grid.tsx`, any backend project.

### References

- [Source: _bmad-artifacts/planning/epics/epic-8-desktop-tablet-layout-correction.md#Story 8.8] — story statement and literal ACs (lines 168-192).
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md#UX-DR29, #UX-DR30] — grid-with-expand and section-pairing definitions (lines 132-133); UX-DR28 (line 131) for the "no manual-reordering affordance" test; UX-DR26 for the accordion/summary-row pattern; UX-DR19 for the 900px column.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/layout-spacing.md, lines 15-17] — Grid-with-expand and Section pairing rationale; [DESIGN/components.md, lines 45-47] — Rooms/Power Points/Devices tree component; [EXPERIENCE.md, lines 34, 36, 57].
- [Source: .../mockups/key-settings.html, lines 88-90 (pair CSS), 114-135 (grid-with-expand CSS), 274-330 (Dark markup), 371-425 (Light markup)] — canonical rendered spec; [.../mockups/wide-settings-tariff-2026-09-28.html] — original decision record.
- [Source: web/src/components/settings/settings-page.tsx, lines 76-114] — section wrappers and `wide:order-N`; [web/src/components/settings/section-label.tsx]; [web/src/components/household-invite/invite-member-row.tsx] — Household section content (dual-render, dialog).
- [Source: web/src/components/tagging-scaffold/tagging-scaffold-manager.tsx, lines 475-690] — tree render block; lines 104-105 `ROOM_ROW_BORDER_CLASS`; 192-212 `powerPointHasVisibleChildren`/`roomHasVisibleChildren`.
- [Source: web/src/index.css, lines 10, 77-90] — `--breakpoint-wide: 660px`, quiet-surface tokens, `--radius-glass-sm`; [web/src/components/entry-grid/entry-grid.tsx] — Story 8.7's tile class recipe to mirror.
- [Source: web/e2e/app-shell.spec.ts, Settings test ~488-620] — route mocks, boundary-test pattern, 8.7's AC #3 assertion ("no entry-grid on the tree") that this story must not invalidate (the tree gets a CSS `grid`, not an `entry-grid` `data-slot` — do not reuse `data-slot="entry-grid"` on it, or 8.7's test will fail).
- [Source: _bmad-artifacts/implementation/8-7-meter-readings-events-tariff-history-entry-grid.md] and [8-5-settings-desktop-tablet-layout.md] — previous stories: class-only/`wide:` conventions, geometry-assertion lessons, `resize_window` workaround, Playwright port/cert Debug Log, live-verification gate.
- [Source: _bmad-artifacts/implementation/deferred-work.md — "code review of 8-5-settings-desktop-tablet-layout"] — zero-count "— 0 Power Points" copy and `InviteMemberRow` dialog-across-660px items remain deferred; do not fix here.
- [Source: _bmad-artifacts/project-context.md#Critical Don't-Miss Rules] — process gates; [#TypeScript/Frontend] — `verbatimModuleSyntax`, `noUnusedLocals`, `@/` alias, oxlint.

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5.5

### Debug Log References

- Playwright: certs/vite-dev-cert.* moved aside for the run and restored; no orphaned preview on :4173.
- First e2e run failed only on my own 659px assertion (AI form has its own visible heading at narrow width); fixed by measuring the hidden SectionLabel's parent.

### Completion Notes List

- Task 1: AI Plausibility + Household wrapped in one `flex-col wide:flex-row wide:order-2` wrapper (children `wide:flex-1 wide:basis-0 wide:min-w-0`); outer orders renumbered 1-4; DOM order unchanged. Gap uses `--spacing-card-gap` (18px) vs mockup's 14px. Default stretch kept — live, the short Household card does not stretch and looks fine.
- Tasks 2-4: class-only changes in `tagging-scaffold-manager.tsx` via `WIDE_TILE_CLASS`/`WIDE_TILE_GRID_CLASS`. `wide:open:col-span-full` compiles (`:is([open],:popover-open,:open)`). Deviation: instead of `wide:border-b-0`/`border-t-0` on the `<details>`, tiles use `wide:border` + `wide:last:border-b` + `wide:dark:border-surface-quiet-border` (the row classes only set one side and a dark colour that would otherwise win). Suppressed wrappers carry `wide:col-span-full` + `wide:border-b-0`/`border-t-0`. Summary text gets `min-w-0 wide:flex-wrap break-words`.
- Task 5: device list untouched apart from an explanatory comment.
- Tests: +2 settings-page tests, +6 tagging-scaffold tests (all existing tests unchanged and passing). Unit 451/451, `tsc -b` clean, oxlint no new warnings, Playwright 6/6 (Settings test extended: 4 Rooms/9 PPs/3 Devices, exact tile-width/column-count arithmetic from computed content width, pair same-y/different-x, open-tile full span, single-column devices, 659px flat layout).
- AC accounting: AC1 code + unit (grid class) + e2e (column count/width) + live (3-col computed grid). AC2 unit + e2e + live (`grid-column:1/-1`, nested PP grid). AC3 unit (no grid on device list) + e2e (same x, increasing y) + live (`display:flex`). AC4 unit (wrapper/order) + e2e (same y, spans column) + live screenshot. AC5 e2e at 659px (no grid, tiles share x); live <660px NOT yet checked.
- Live Chrome (~842px viewport, Light, real household): pairing, suppressed-Room wrapper (archived Room with live PP), open Room/PP spans, device list verified. The real household has only one (archived) Room, so multi-tile-per-row is proven by e2e only.
- Dark-mode, German strings and 660px-crossing resize verified live by the user (2026-09-29).

### File List

- web/src/components/settings/settings-page.tsx
- web/src/components/settings/settings-page.test.tsx
- web/src/components/tagging-scaffold/tagging-scaffold-manager.tsx
- web/src/components/tagging-scaffold/tagging-scaffold-manager.test.tsx
- web/e2e/app-shell.spec.ts
- _bmad-artifacts/implementation/sprint-status.yaml
- _bmad-artifacts/implementation/8-8-settings-rooms-power-points-grid-with-expand-section-pairing.md

### Change Log

- 2026-09-29: Story 8.8 implemented — Settings section pairing and Rooms/Power Points grid-with-expand (CSS-only `wide:` variants), unit + e2e coverage.
- 2026-09-29: Live verification completed by user; status → done.
