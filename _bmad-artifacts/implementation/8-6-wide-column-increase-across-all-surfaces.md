---
baseline_commit: 91158e3217f46d828eeb8efe1b6c26441e718cc7
---

# Story 8.6: Wide-Column Increase Across All Surfaces

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member on a desktop or wide tablet browser,
I want the app's content column to use more of the available width,
so that a surface like Trend History's chart doesn't feel squeezed into a narrow strip while the rest of the window sits empty.

## Acceptance Criteria

1. **Given** any of the four surfaces (Dashboard, Trend History, Tariff Radar, Settings) at an available width ≥660px, **when** the page loads, **then** the content column's max-width is 900px instead of 660px, on all four surfaces alike — still one shared rule, not a per-surface exception (UX-DR19). **And** the 660px breakpoint itself is unchanged as the trigger for the phone→wide layout switch and the nav-chrome swap (Story 8.1) — only the column's own max-width changed.
2. **Given** an available width between 660px and 900px, **when** the page loads, **then** the column simply fills the available width up to the 900px cap (unchanged `w-full`/`max-w` mechanics) rather than sitting at a fixed 660px with unused gutters on either side.
3. **Given** the Dashboard's Status card at ≥900px, **when** rendered, **then** its own proportions are unchanged from Story 8.2 — it simply sits in a column with slightly more margin either side, since nothing about its content changed.

## Tasks / Subtasks

- [x] **Task 1: Change the column max-width literal from `660px` to `900px` on all four page wrappers (AC #1, #2)**
  - [x] In `web/src/components/dashboard/dashboard-page.tsx:134`, change `<div data-slot="dashboard-content" className="flex flex-col gap-4 wide:mx-auto wide:w-full wide:max-w-[660px]">` to `wide:max-w-[900px]`. Leave `wide:mx-auto wide:w-full` and the `gap-4` untouched — only the max-width literal changes.
  - [x] In `web/src/components/trend-history/trend-history-page.tsx:81`, make the identical change: `wide:max-w-[660px]` → `wide:max-w-[900px]` on the `data-slot="trend-history-content"` wrapper.
  - [x] In `web/src/components/tariff/tariff-radar-page.tsx:67`, make the identical change on the `data-slot="tariff-radar-content"` wrapper.
  - [x] In `web/src/components/settings/settings-page.tsx:58`, make the identical change on the `data-slot="settings-content"` wrapper (this one uses `gap-6`, not `gap-4` — leave that as-is, same as every prior Epic 8 story that touched this file).
  - [x] **Do not touch `--breakpoint-wide: 660px` in `web/src/index.css:10`.** That token drives the `wide:` variant (the phone→wide trigger and the nav-chrome swap) and is explicitly unchanged by this story (AC #1's second sentence). The only thing that changes is the max-width literal inside each page's `wide:max-w-[...]` class — two visually similar but semantically distinct 660-px values in this codebase; only one of them moves.
  - [x] Update the stale `660px`/`>=660px` mentions inside the JSDoc-style comments directly above each of the four wrapper `<div>`s (`dashboard-page.tsx:130`, `trend-history-page.tsx:78`, `tariff-radar-page.tsx:63`, `settings-page.tsx` — check for an equivalent comment) to say `900px` where they describe the column's own width, while leaving any `>=660px` reference that describes the *breakpoint trigger* itself unchanged. Read each comment before editing — some sentences mix both meanings in one breath (e.g. trend-history-page.tsx:78 reads "a centered 660px column at >=660px" — only the first `660px` becomes `900px`).
  - [x] No other file in `web/src` contains the `max-w-[660px]` literal outside these four page components and their own test files (confirmed via `grep -rn "660" web/src --include="*.tsx" --include="*.ts" --include="*.css"` during story creation) — do not go looking for a fifth surface or a shared constant to extract; the established Epic 8 pattern deliberately repeats this literal per page (see Dev Notes).

- [x] **Task 2: Update unit test assertions that pin the literal class string (AC #1)**
  - [x] `web/src/components/dashboard/dashboard-page.test.tsx:338` — change `expect(wrapper).toHaveClass('wide:mx-auto', 'wide:w-full', 'wide:max-w-[660px]')` to assert `'wide:max-w-[900px]'`.
  - [x] `web/src/components/trend-history/trend-history-page.test.tsx:83` — identical change: assert `'wide:max-w-[900px]'` instead of `'wide:max-w-[660px]'`.
  - [x] `web/src/components/settings/settings-page.test.tsx` and `web/src/components/tariff/`: confirmed during story creation that neither `settings-page.test.tsx` (only asserts the wrapper's *presence* via `data-slot`, not its class list — line 78) nor any tariff test file asserts the literal `max-w-[660px]` class string, since **no `tariff-radar-page.test.tsx` file exists in this codebase** (Tariff Radar's page-level wrapper is only proven by the e2e spec, Task 3 below). No action needed for either file beyond re-running the suite to confirm nothing else broke.
  - [x] Grep `web/src` for any remaining `max-w-\[660px\]` after Task 1+2's edits to confirm zero occurrences (excluding e2e assertions on pixel *measurements*, which are numeric bounds, not the literal Tailwind class string, and are handled in Task 3).

- [x] **Task 3: Update `web/e2e/app-shell.spec.ts` pixel-measurement assertions and add the 660–900px fill-behavior check (AC #1, #2, #3)**
  - [x] **Dashboard case** (`app-shell.spec.ts:74-133`): at line 130-132, the 1000px-viewport assertion currently reads `expect(wideColumnBox?.width).toBeGreaterThan(600)` / `.toBeLessThanOrEqual(660)`. Change the upper bound to `900` (keep `toBeGreaterThan(600)` as a loose lower sanity bound, or tighten to `800` if you prefer a stricter signal — either is defensible since the real value should land at exactly 900 when the viewport comfortably exceeds it). Update the trailing comment from "must actually be a ~660px column, not collapsed" to "~900px column".
  - [x] **Trend History case** (`app-shell.spec.ts:139-…`): identical change at lines 212-214.
  - [x] **Tariff Radar case** (`app-shell.spec.ts:242-…`): identical change at lines 355-357.
  - [x] **Settings case** (`app-shell.spec.ts:392-…`): identical change at lines 477-479. The centered-column check right below it (`leftGap`/`rightGap` computed against a 1000px viewport) still works arithmetically once the cap is 900 — a 1000px viewport with a 900px column leaves 50px on each side instead of 170px; the `Math.abs(leftGap - rightGap)).toBeLessThan(4)` assertion doesn't need to change, just re-verify it still passes.
  - [x] **New fill-behavior assertion (AC #2), one per surface, added alongside the existing 1000px checks:** at a viewport between 660 and 900 (e.g. 800px), assert the content column's `boundingBox().width` is greater than roughly 750 (tracks the viewport, not capped) — proving the column isn't still pinned to 660 or jumping straight to 900. Add this as a new `page.setViewportSize({ width: 800, height: 800 })` step + `contentColumn.boundingBox()` assertion in each of the four existing tests, right after each test's existing `660px` boundary check and before its `1000px` check, following the same `contentColumn` locator each test already defines.
  - [x] Do not add a fifth Playwright test — extend the four existing per-surface tests in place, consistent with how Stories 8.2-8.5 each added their own surface's assertions to a single existing test rather than proliferating new top-level `test()` blocks.

- [x] **Task 4: Full regression + live Chrome verification (mandatory — do not defer)**
  - [x] Run `npm --prefix web run test`, `tsc -b`, `oxlint` clean. No backend changes in this story — no `.NET` test run required.
  - [x] Run `npx playwright test` (temporarily move `certs/vite-dev-cert.pem`/`.key` aside first if present locally — see Story 8.5's Debug Log for why; restore immediately after).
  - [x] **This story is pure browser-dependent responsive-layout behavior — the same trigger condition Stories 8.1-8.5 hit.** It cannot move review→done without a live Claude-in-Chrome verification actually performed in this session. If Chrome isn't connected, raise it immediately and pause rather than deferring.
  - [x] Reuse the established workaround from Stories 8.3-8.5's Debug Logs: `resize_window` on an already-navigated tab is unreliable in this sandbox — open a *new* tab already sized to the target width, then navigate it; retry with a fresh tab if a resize doesn't "stick".
  - [x] Verify live at ~1000-1100px on each of the four surfaces: content column measures ~900px (not ~660px) and stays centered; on Trend History specifically, confirm the chart visibly uses the extra width (it's a `width="100%"` SVG inside `trend-chart.tsx:239`, already responsive — this should require no chart code change, just visual confirmation the fix reaches it).
  - [x] Verify live at ~800px (between 660 and 900) on at least Dashboard and Trend History: column tracks the viewport width, not pinned at 660 or jumping to 900 — no unused gutters.
  - [x] Verify live at 659px and 660px on at least one surface: confirms the breakpoint trigger itself (nav-chrome swap, icon labels appearing) is unaffected by this story — only the column's cap changed, not the trigger point.
  - [x] Verify the Dashboard's Status card at ~1000px: its own internal proportions (padding, font sizes, icon sizing) look identical to before this story — it should simply sit in a wider column with more side margin, no internal resize.

- [x] **Task 5: Verify against every AC**
  - [x] Walk AC #1-#3 individually and state in Completion Notes exactly what proves each one — code reference, automated test, or live verification (Task 4) — following the same AC-by-AC accounting Stories 1.12/1.7/8.1-8.5 used.

### Review Findings

- [x] [Review][Decision] Resolved 2026-09-29 (re-ran live check, option 1): at exactly 659px on the dev server, `matchMedia('(min-width: 660px)')` is false, bottom tab bar is `display:flex`, top nav is `display:none`, header buttons are icon-only, column is 627px (659 − 2×16). The 660px live side was verified in the original dev session (a re-resize to 660 stuck at 659 again). — Task 4 "verify live at 659px" is checked off although it was not performed — Completion Notes admit the live 659px check produced 660px (resize quirk), so that side of the boundary is proven by Playwright only. Story rules say a missing live check blocks review→done rather than deferring. Options: (1) re-run the 659px live check in a fresh pre-sized tab, (2) explicitly accept the e2e proof for the 659px side and note the sign-off.
- [x] [Review][Patch] Inaccurate e2e comment: the 659/660 exact-boundary check cannot detect drift in the `max-w-[900px]` literal — reword to cover only `--breakpoint-wide`, and point to the 800px/1000px checks for the cap [web/e2e/app-shell.spec.ts:116-118] (also Trend History comment at ~209)
- [x] [Review][Patch] Loose column-width bounds in e2e: at 1000px viewport the column must be exactly 900px (`toBeCloseTo(900, 0)`, not `>800 && <=900`), and at 800px exactly 768px (not `>750 && <800`); otherwise a 850/880 cap or padding drift still passes [web/e2e/app-shell.spec.ts:137,225,374,502 and the four 800px blocks]
- [x] [Review][Defer] Settings and Tariff Radar have no unit-level assertion of the `wide:max-w-[900px]` class [web/src/components/settings/settings-page.test.tsx] — deferred, pre-existing (spec Dev Notes: coverage gap predates this story; tariff-radar-page.test.tsx does not exist)

## Dev Notes

- **This is a UI-only story with no new domain capability, no backend change, and no new architecture decision** — Epic 8's own framing, same as Stories 8.1-8.5. UX-DR19 (amended a second time, 2026-09-28) is the sole driver. There is no new FR/NFR and no new AD.

- **The entire functional change is a single literal-value swap, repeated in 4 places: `wide:max-w-[660px]` → `wide:max-w-[900px]`.** No new component, no new prop, no new i18n key, no new class. Resist the temptation to add anything beyond this — Stories 8.7-8.9 (backlog, same epic) own the entry-grid, grid-with-expand, section-pairing, and side-by-side-panel work that the *rest* of the extra width will eventually be used for; none of that is in scope here. This story's only job is growing the column itself.

- **Why the literal is repeated 4× instead of extracted into a shared constant/token:** confirmed via `grep -rn "660" web/src` during story creation that Stories 8.2-8.5 already established this as per-page inline Tailwind arbitrary values (`wide:max-w-[660px]`), not a shared CSS variable or layout-wrapper component — only the *breakpoint* (`--breakpoint-wide`) is centralized, not the column's own max-width. Introducing a shared constant now would be an uninvited refactor of a pattern four prior stories deliberately established and none of them consolidated; follow the existing convention rather than improving it unasked.

- **Two different `660px` values exist in this codebase and only one of them moves:**
  1. `--breakpoint-wide: 660px` (`web/src/index.css:10`) — the phone→wide trigger point and nav-chrome swap (Story 8.1). **Unchanged by this story** (AC #1's second sentence is explicit).
  2. `wide:max-w-[660px]` — the column's own cap, repeated in the 4 page wrappers. **This is the one that becomes `900px`.**
  Every one of the 4 files' own comments above the wrapper mixes both meanings in the same sentence (e.g. "a centered 660px column at >=660px") — read carefully before editing so the trigger-point mention survives and only the column-width mention changes.

- **The Trend History chart needs no code change to benefit from the wider column** — confirmed via `grep -n "width=" web/src/components/trend-history/trend-chart.tsx:239`, it already renders at `width="100%"` inside its container, so it will simply fill however wide `trend-history-content` now is. The epic's own motivating complaint ("Trend History's chart doesn't feel squeezed") is fixed entirely by the column growing; Task 4's live verification should *observe* this, not implement anything for it.

- **`tariff-radar-page.tsx` has no dedicated unit test file** (`tariff-radar-page.test.tsx` does not exist in this codebase — confirmed via `find web/src -iname "*tariff-radar*"` during story creation; only `tariff-configuration-form.test.tsx`, `tariff-comparison-form.test.tsx`, `tariff-history-list.test.tsx`, `tariff-check-card.test.tsx`, `edit-tariff-dialog.test.tsx` exist, none of which touch the page-level wrapper). This means the Tariff Radar column-width change is proven *only* by the e2e spec (Task 3) and live verification (Task 4) — don't assume a unit-test gap here is an oversight to fix; it's pre-existing and out of this story's scope to add.

- **`settings-page.test.tsx` only asserts the wrapper's presence (`data-slot="settings-content"`), not its class list** (line 78) — unlike `dashboard-page.test.tsx`/`trend-history-page.test.tsx`, which do pin the literal `max-w-[...]` string. This is a pre-existing asymmetry across the 4 pages' test coverage, not something this story needs to fix; just don't expect to find a class assertion to update there.

- **Design-doc source is already amended** — `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/layout-spacing.md` line 11 (commit `91158e3`) already documents `{spacing.content-max-wide}` = 900px superseding the 660px cap, with `{spacing.breakpoint-wide}` (660px) explicitly called out as unchanged. This story is pure code catch-up to already-finalized design documentation — no design decision is being made here, only implemented.

- **Testing standard reminder:** frontend unit tests are colocated next to source, Vitest + Testing Library, `jsdom` — jsdom doesn't apply real CSS, so a `wide:max-w-[900px]` class change is only verifiable in unit tests as a *string* match (Task 2), never as an actual measured width. Real pixel measurement only happens in the Playwright e2e spec (Task 3) and live Chrome verification (Task 4), per every prior Epic 8 story's established division of proof.

- **No backend change, no new FR/NFR, no new architecture decision** — this story does not trigger the OIDC/claims-verification condition of the project's live-verification gate, only the browser-dependent-responsive-layout condition (same as Stories 8.2-8.5).

### Project Structure Notes

```text
energy-tracker-v2/
  web/src/
    components/
      dashboard/
        dashboard-page.tsx                # modified — max-w-[660px] -> max-w-[900px] (Task 1)
        dashboard-page.test.tsx           # modified — class assertion updated (Task 2)
      trend-history/
        trend-history-page.tsx            # modified — max-w-[660px] -> max-w-[900px] (Task 1)
        trend-history-page.test.tsx       # modified — class assertion updated (Task 2)
      tariff/
        tariff-radar-page.tsx             # modified — max-w-[660px] -> max-w-[900px] (Task 1)
                                           # (no dedicated unit test file — pre-existing gap)
      settings/
        settings-page.tsx                 # modified — max-w-[660px] -> max-w-[900px] (Task 1)
                                           # (settings-page.test.tsx needs no change — only
                                           #  asserts wrapper presence, not class list)

  web/e2e/
    app-shell.spec.ts                     # modified — pixel-bound updates on all 4 existing
                                           # per-surface tests + new 800px fill-behavior
                                           # assertion per surface (Task 3)

  web/src/index.css                       # UNCHANGED — --breakpoint-wide: 660px stays as-is
```

No changes expected to `web/src/components/trend-history/trend-chart.tsx`, `web/src/components/dashboard/nav-chrome.tsx`, `web/src/components/dashboard/profile-menu.tsx`, any backend project, any locale file (no new/changed UI strings), or the Status card's internal markup/styling in `dashboard-page.tsx` (only its containing wrapper's max-width changes).

### References

- [Source: _bmad-artifacts/planning/epics/epic-8-desktop-tablet-layout-correction.md#Story 8.6: Wide-Column Increase Across All Surfaces] — story statement and literal acceptance criteria (lines 124-144); epic-level framing naming Stories 8.6-8.9 as a 2026-09-28 follow-up round sourced from live desktop review of shipped 8.1-8.5 (lines 5, 10).
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md#UX-DR19] — full text of both the 2026-09-23 (660px column) and 2026-09-28 (660px→900px cap growth) amendments, explicit that the breakpoint itself is unchanged, only the cap (line 130).
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/layout-spacing.md, lines 9 and 11] — `{spacing.breakpoint-wide}` (660px, unchanged) vs. `{spacing.content-max-wide}` (900px, new) — the authoritative token-level statement of exactly what this story changes and what it doesn't.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-trend-history.html, lines 13-22, 63-64] — motivating rationale (squeezed chart, empty desktop viewport) and the confirmed 900px column value in a rendered mockup.
- [Source: web/src/index.css, line 10] — `--breakpoint-wide: 660px` in the `@theme inline` block, generating the `wide:` Tailwind variant; confirmed unchanged by this story.
- [Source: web/src/components/dashboard/dashboard-page.tsx, lines 129-134] — Story 8.2's original 660px wrapper comment + `data-slot="dashboard-content"` div, the Task 1 edit target.
- [Source: web/src/components/trend-history/trend-history-page.tsx, lines 78-81] — Story 8.3's equivalent wrapper, Task 1 edit target.
- [Source: web/src/components/tariff/tariff-radar-page.tsx, lines 63-67] — Story 8.4's equivalent wrapper, Task 1 edit target; confirmed no dedicated unit test file exists for this page component.
- [Source: web/src/components/settings/settings-page.tsx, line 58] — Story 8.5's equivalent wrapper (note: `gap-6`, not `gap-4`, unlike the other three), Task 1 edit target.
- [Source: web/src/components/trend-history/trend-chart.tsx, line 239] — `width="100%"` on the chart's rendering container, confirming it needs no code change to benefit from the wider column.
- [Source: web/src/components/dashboard/dashboard-page.test.tsx, lines 307-338] — existing test asserting the literal `wide:max-w-[660px]` class string, Task 2 edit target.
- [Source: web/src/components/trend-history/trend-history-page.test.tsx, lines 73-83] — equivalent existing test, Task 2 edit target.
- [Source: web/src/components/settings/settings-page.test.tsx, lines 71-78] — confirmed this test only checks wrapper presence via `data-slot`, not the class list — no edit needed here.
- [Source: web/e2e/app-shell.spec.ts] — the four existing per-surface Playwright tests and their `contentColumn` locators/pixel-bound assertions: Dashboard (lines 74-133, bound at 130-132), Trend History (lines 139-…, bound at 212-214), Tariff Radar (lines 242-…, bound at 355-357), Settings (lines 392-…, bound at 477-483) — all four Task 3 edit targets, plus the pattern for adding the new 800px fill-behavior check to each.
- [Source: _bmad-artifacts/implementation/8-5-settings-desktop-tablet-layout.md] — read in full before starting. Establishes the `data-slot="{page}-content"` wrapper pattern this story edits (not creates), the `resize_window`-before-navigation live-verification workaround (Task 4), and the project's live-verification gate reasoning for browser-dependent layout stories.
- [Source: _bmad-artifacts/implementation/8-4-tariff-radar-desktop-tablet-layout.md, 8-3-trend-history-desktop-tablet-layout.md, 8-2-dashboard-desktop-tablet-layout.md] — origin of the `data-slot="{page}-content"` wrapper convention and each page's own e2e boundary-test pattern this story extends rather than replaces.
- [Full-codebase grep performed during story creation] — `grep -rn "660" web/src --include="*.tsx" --include="*.ts" --include="*.css"` confirmed the 4 wrapper literals plus `index.css`'s breakpoint token are the only implementation-level occurrences (all other matches are comments or test assertions, enumerated above); `find web/src -iname "*tariff-radar*"` confirmed no `tariff-radar-page.test.tsx` exists.

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5.5

### Debug Log References

- Red/green: updated the two class-string unit assertions first (2 failed as expected), then swapped the four `wide:max-w-[660px]` literals (21/21 pass).
- Playwright: an orphaned `vite preview` from a prior session (PID 91180, started 2026-09-28) was still holding port 4173 over HTTPS, so the stock config timed out on `webServer`. Left that process alone and ran the suite through a throwaway config on port 4174 (deleted afterwards), with `certs/vite-dev-cert.*` moved aside and restored immediately. `web/dist` was rebuilt in the process.
- Live Chrome: `resize_window` on an already-navigated tab did not stick (as in 8.3-8.5); a fresh tab per width worked. Window widths land on innerWidth = requested (+1 at 659/660: 659 gave 660).

### Completion Notes List

- Changed `wide:max-w-[660px]` -> `wide:max-w-[900px]` on the four page wrappers and updated the column-width mentions in their comments; `>=660px` breakpoint mentions and `--breakpoint-wide: 660px` untouched.
- Unit: `dashboard-page.test.tsx` and `trend-history-page.test.tsx` now assert `wide:max-w-[900px]`. Full suite 423/423, `tsc -b` clean, `oxlint` 0 errors (4 pre-existing warnings).
- e2e (`app-shell.spec.ts`): 1000px bounds now >800 / <=900 on all four surfaces; new 800px fill assertion (750 < width < 800) added to each existing test. 6/6 pass.
- AC #1: code (4 wrappers), unit class assertions, e2e 900px bound, live: all four surfaces measured 900px, x=100 (centered) at 1100px. Breakpoint unchanged: `index.css` untouched; live at 660px the top nav is active; e2e covers the 659/660 swap exactly.
- AC #2: e2e 800px assertion and live at 800px (Dashboard, Trend History): column 768px = viewport minus 2x16px padding, no cap; Trend History chart spans the full width.
- AC #3: Status card markup untouched; live screenshot at 1100px shows identical card internals in the wider column.
- Deviation to flag: the live check at exactly 659px could not be produced (browser window resize quirk gave 660); that side of the boundary is proven by the Playwright test only. **Update (code review, 2026-09-29):** the 659px live check was subsequently performed and passed — see Review Findings.

### File List

- web/src/components/dashboard/dashboard-page.tsx
- web/src/components/dashboard/dashboard-page.test.tsx
- web/src/components/trend-history/trend-history-page.tsx
- web/src/components/trend-history/trend-history-page.test.tsx
- web/src/components/tariff/tariff-radar-page.tsx
- web/src/components/settings/settings-page.tsx
- web/e2e/app-shell.spec.ts
- _bmad-artifacts/implementation/sprint-status.yaml

### Change Log

- 2026-09-29: Story 8.6 implemented — content column cap raised from 660px to 900px on all four surfaces; tests and e2e updated; live-verified in Chrome.
