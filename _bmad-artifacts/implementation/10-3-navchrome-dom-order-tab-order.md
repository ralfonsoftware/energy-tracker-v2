---
baseline_commit: b503218
---

# Story 10.3: NavChrome Document Order Matches Visual Order (Focus Order, WCAG 2.4.3)

Status: in-progress

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- Epic 10 "Deferred-work hardening" is new (created 2026-10-02 from the Epic 8 retro action #6 triage, `spec-deferred-work-triage.md`). Origin: deferred-work.md entry "NavChrome last in DOM at wide" (spec-tab-order-check, 2026-10-02), promoted by Ralf. -->

## Story

As a keyboard or screen-reader user on a tablet or desktop browser (≥660px),
I want the top navigation and Account menu to be the first thing Tab reaches, as they are the first thing I see,
so that focus order follows reading order instead of making me Tab through the whole page before I can reach the nav.

## Acceptance Criteria

1. **Given** any of the four screens (Dashboard, Trend History, Tariff Radar, Settings) at an available width ≥660px (660 and 900 checked), **when** the member presses Tab from the start of the page, **then** the first five stops are the top nav's four links (Dashboard, Trend History, Tariff Radar, Settings, left to right) and then the Account menu avatar, followed by the page content. The nav stops come first in the real Tab sequence and are visually above all content.
2. **Given** the same screens at an available width <660px (659 checked), **when** the member tabs through the page, **then** the first stop is page content and the four bottom-bar buttons are the last four stops. The bottom bar is still visually below all content, pinned to the bottom of the viewport on short pages exactly as today (Story 8.1 AC #2: no mobile regression).
3. **Given** any width, **when** the member tabs through the page, **then** only the nav variant for that width contributes Tab stops (top at ≥660, bottom below). The hidden variant stays `display:none` and is never focusable. The Profile/Account menu is still mounted exactly once (top variant only).
4. **Given** the change is complete, **when** the page renders at any width, **then** the layout is visually unchanged: the top nav is still a full-width bar above the 900px content column, the bottom bar is still bottom-pinned, and no extra gap or empty flex item appears (`gap-4` on `<main>` must not add space for a hidden variant). The locale × theme sweep (`web/e2e/locale-theme-sweep.spec.ts`, 48 cells: de-DE/en-US × light/dark × 659/660/900 across the four screens) and `web/e2e/app-shell.spec.ts` stay green with no assertion loosened.
5. **Given** a member at ≥660px who opens the Account menu with the keyboard, **when** they Tab through the menu and press Escape, **then** behaviour is identical to today (cycle Appearance → Language → Log off, Escape returns focus to the avatar), unchanged by the new DOM position.
6. **Given** either nav variant on any screen, **when** a member activates Dashboard / Trend History / Tariff Radar / Settings, **then** the same `onXClick` callback fires as today, and the active entry keeps `aria-current="page"` and the `bg-nav-chrome-active-bg` / `text-nav-chrome-active-foreground` tokens. NavChrome remains one component driven by one set of props (UX-DR9), not two diverging implementations.
7. **Given** a future change that moves the nav back behind the page content (or reorders it with CSS only), **when** `web/e2e/tab-order.spec.ts` runs, **then** it fails and names the offending stops. The former "pinned divergence" test is inverted into a real "nav first at ≥660px, last at <660px, equals visual order" assertion. The `deferred-work.md` NavChrome entry is removed (resolved) and the `project-context.md` WCAG 2.4.3 rule no longer lists NavChrome as an exception.
8. **Given** the change touches no copy, **when** reviewed, **then** no new i18n keys, tokens, dependencies or backend changes are introduced, and `web/src/locales/en-US` and `de-DE` are untouched. (If the dev chooses the skip-link variant instead, see Ask First: new strings are then required in **both** catalogs.)
9. **Given** this is browser-dependent layout and focus behaviour, **when** the story is reviewed, **then** it has been verified live in a real browser tab at 659px and 660px (plus one wide width) per the `project-context.md` live-resize procedure, with real Tab presses. A Playwright run, an iframe or a width that silently landed elsewhere is **not** a substitute (see Task 8).

## Tasks / Subtasks

Test-first: Tasks 1 and 2 must be run and seen **red** against the current code before Task 3.

- [x] Task 1: Invert the NavChrome pin in `web/e2e/tab-order.spec.ts` (AC: #1, #2, #3, #7)
  - [x] 1.1 Replace the `NavChrome tab position … (pinned: …)` loop (lines ~379–416) with assertions per screen × width [659, 660, 900]: at ≥660 the first 5 `tabThrough` stops all have `nav === 'top'` and content stops follow; at 659 every stop after the first `nav` stop is `nav === 'bottom'` and the first stop is content. Keep the existing checks that survive: only one nav tabbable (`new Set(navStops.map(s => s.nav))`), nav link count 5 / 4, links left-to-right, `Account menu` last in the top nav, top nav visually above content at ≥660 and bottom bar below at <660.
  - [x] 1.2 Add the diagnostic that stops this from restating the DOM: compare Tab order against *visual* order computed from `getBoundingClientRect` (nav stops' `rect.top` ≤ every content stop's `rect.top` at ≥660). A pure CSS reorder must fail it.
  - [x] 1.3 Rewrite the header comment (lines 3–13) and the failure message (line ~400) so they describe the guard, not a divergence. Keep every other test in the file untouched (their groups ignore ungrouped nav stops via `tabOrderOfGroups`, so they should stay green with nav now first; confirm).
  - [x] 1.4 Run the spec against the unchanged code and confirm the new NavChrome tests fail at 660/900 and pass at 659.
- [x] Task 2: Unit tests (AC: #1, #3, #4, #6, #7)
  - [x] 2.1 `nav-chrome.test.tsx`: render `placement="top"` and `placement="bottom"` separately; assert each renders exactly one `nav[data-slot=…]` with the right classes (`hidden wide:flex` / `wide:hidden`), four entries, active state, handlers, Account menu only in top.
  - [x] 2.2 One page-level test per screen (dashboard, trend-history, tariff-radar, settings — extend the existing `*-page.test.tsx`): the top nav is the first element child of `<main>` and the bottom nav the last (document order), so a page that forgets one mount fails.
  - [x] 2.3 Fix tests that index navs by DOM order, which flips once the top nav comes first: `dashboard-page.test.tsx` ~line 202–204 (`settingsButtons[1]` was "the top nav") and the comment at ~174–175. Scope by `data-slot` (`within(document.querySelector('nav[data-slot="nav-chrome-top"]')!)`) instead of an index. The other `getAllByRole(...)[0]` sites (`App.test.tsx`, `settings-page.test.tsx:185`, `trend-history-page.test.tsx:99,110`, `dashboard-page.test.tsx:168,238`) click whichever variant comes first and call the same handler, so they stay valid; leave them unless one asserts which variant it hit.
- [x] Task 3: Split placement in `web/src/components/dashboard/nav-chrome.tsx` (AC: #1–#4, #6, #8)
  - [x] 3.1 Add a required prop `placement: 'top' | 'bottom'`. `top` renders only the existing `<nav data-slot="nav-chrome-top" className="hidden wide:flex …">` (brand, four links, ProfileMenu). `bottom` renders only `<nav data-slot="nav-chrome-bottom" className="mt-auto wide:hidden …">`. Remove the shared wrapper `<div className="mt-auto wide:order-first wide:mt-0">` entirely: `mt-auto` moves onto the bottom nav itself and `wide:order-first`/`wide:mt-0` disappear. With no wrapper a hidden variant is `display:none`, so it is not a flex item and `gap-4` adds no space (AC #4). Do **not** keep an empty wrapper div; it would become a zero-height flex item that still receives the gap.
  - [x] 3.2 Keep the markup, classes, `aria-current` handling, `data-slot` values and the four buttons per variant byte-for-byte; only the wrapper and `placement` branching change. Share the four-button data (label key, icon, handler, active key) instead of copy-pasting if it can be done without changing the DOM; otherwise leave the duplication as it is today. Do not widen scope into a refactor.
  - [x] 3.3 Update the file's header comment (lines ~25–30, ~45–47), which still describes `wide:order-first` and a single mount point.
- [x] Task 4: Mount both placements in the four pages (AC: #1–#4, #6)
  - [x] 4.1 In `dashboard-page.tsx`, `trend-history-page.tsx`, `tariff-radar-page.tsx`, `settings-page.tsx`: render `<NavChrome placement="top" … />` as the **first child of `<main>`**, before the `data-slot="*-content"` wrapper, and `<NavChrome placement="bottom" … />` where NavChrome is mounted today (last, before `MeterRegressionPromptDialog` on Dashboard). Pass the identical props to both (`active`, the four `onXClick`, `householdId`, `supportsFederatedLogout`, `email`); build the props once per page (a local `navProps` object) so the two mounts cannot drift.
  - [x] 4.2 Update the comments in the four pages that say NavChrome is "deliberately outside this wrapper" only if they now misdescribe the structure (the nav is still outside the content wrapper, so most stay true).
- [x] Task 5: Green the unit and e2e suites (AC: #1–#7)
  - [x] 5.1 `npm test` in `web/` (Vitest), `npm run lint`, type-check/build as the repo does in `pr-review.yml`. `layout-constants.drift.test.ts` must stay green (breakpoint and column copies are untouched).
  - [x] 5.2 Playwright: `tab-order.spec.ts`, `app-shell.spec.ts` and `locale-theme-sweep.spec.ts` all green. The sweep's layout-identity check (`nav[data-slot=…]` visible per width) still holds because the `data-slot` attributes are unchanged.
- [x] Task 6: Close the loop in docs (AC: #7)
  - [x] 6.1 `_bmad-artifacts/implementation/deferred-work.md`: delete the `[promoted: needs story]` NavChrome entry (and its line in the Index). The related open entry about tab-order coverage gaps stays (its Profile-menu-below-900px item is unrelated).
  - [x] 6.2 `_bmad-artifacts/project-context.md` (line ~74, "Visual reordering must not reorder focus"): remove the NavChrome exception ("the one documented exception is NavChrome … tracked in `deferred-work.md`"); the rule now has no exceptions. Note that NavChrome's two placements are mounted separately precisely so no CSS `order` is needed.
  - [x] 6.3 No new `deferred-work.md` entries unless review finds something real; if so, use the `[open]` marker format from `project-context.md`.
- [x] Task 7: Self-review checklist before handoff (AC: #3, #4, #8)
  - [x] 7.1 Confirm `git grep "wide:order-first"` returns nothing in `web/`.
  - [x] 7.2 Confirm there is no JS breakpoint branch added (`useWideBreakpoint` is not used here); the swap stays CSS-only.
  - [x] 7.3 Confirm `en-US`/`de-DE` catalogs are unchanged (`git diff --stat web/src/locales`).
- [ ] Task 8: **Live verification gate** (AC: #9). The story cannot move review → done without this.
  - [x] 8.1 Follow the `project-context.md` "Live-resize verification procedure" exactly: close prior tabs, `tabs_create_mcp` a fresh tab, `resize_window` **before** the first `navigate`, then read back `window.innerWidth` via `javascript_tool` and require it to equal the target. One tab per width: **659**, **660**, **900** (add 500 if cheap). A 659 request that reads back 660 is the clamp floor, not a 659px check; retry in a fresh tab group or ask Ralf.
  - [x] 8.2 The app requires Auth0 sign-in. Use the environment the previous Epic 8 live checks used (local dev via `./scripts/run-api.sh` or the deployed site) and ask Ralf to sign in if needed. If the Claude-in-Chrome extension is "not connected", raise it immediately and pause for Ralf, don't defer.
  - [x] 8.3 In each tab, on each of the four screens, press the **real Tab key** from a fresh load (blur first) and record `document.activeElement`'s accessible name and `getBoundingClientRect().top` for the first and last few stops. Expect: ≥660 → Dashboard, Trend History, Tariff Radar, Settings, Account menu, then content; 659 → content first, bottom bar last.
  - [x] 8.4 Measure, don't eyeball: `getBoundingClientRect()` of the top nav (above content, full width at ≥660), of the bottom bar at 659 (bottom of viewport on a short page, below content), and the `<main>` children's `display`/order.
  - [ ] 8.5 In-tab live swap: an in-tab resize across 660 without reload can't be driven by automation, so **ask Ralf to resize the real window** across 660 and record what he observes (nav swaps, no flash, nothing remounts, page state such as a typed field survives). Don't skip it.
  - [ ] 8.6 Record the evidence (widths read back, stops observed, Ralf's observation) in Completion Notes. Substitutes need Ralf's explicit acceptance recorded in the story (8.12 precedent).

## Dev Notes

### Scope Reality Check

- **Deferred entry, not an epic AC:** this story exists because Story 8.1 kept NavChrome last in the DOM and repositioned it with `wide:order-first` so phones kept the bottom bar last (`nav-chrome.tsx:48`). Epic 8's tab-order check (`spec-tab-order-check`, Epic 8 retro action #2) found it was the only real WCAG 2.4.3 divergence and deliberately pinned instead of fixing it (spec "Never: Fixing the NavChrome divergence").
- **Both navs are always in the DOM today** (`hidden`/`wide:hidden` = `display:none`, not tabbable). So the problem is purely *where* the visible one sits in the DOM, not duplicate focus stops. Splitting placement keeps that property.
- **This is not the skip-link story.** A skip link addresses WCAG 2.4.1 (Bypass Blocks); it does not make sequential focus order match visual order. Out of scope here (see Options).
- **No router, no focus management exists:** `App.tsx` swaps pages via a single `view` state, so activating a nav item unmounts the page and focus falls back to `<body>`. With the nav now first in tab order the next Tab lands on the nav, which is the right place. Do not add programmatic focus management in this story.

### Options considered (decision recorded under Ask First)

| Option | How | Verdict |
|---|---|---|
| **A. Split placement, CSS-only (recommended)** | `NavChrome` gets `placement: 'top'\|'bottom'`; each page mounts the top one first in `<main>` and the bottom one last. `wide:`/`hidden` classes keep doing the swap. | No JS breakpoint, no remount when crossing 660, no hydration/flash (Vite SPA, no SSR), no new strings. 4 pages × 1 extra mount. Same component and props (UX-DR9). |
| B. Hook: mount NavChrome first or last via `useWideBreakpoint()` | Each page decides the mount point from `matchMedia('(min-width: 660px)')`. | Works but: the nav remounts when crossing 660 (an open Account menu closes, state resets); a second copy of the breakpoint decision lives in JS (drift risk; the drift test only scans px literals); jsdom has no `matchMedia` so `useWideBreakpoint` returns false and the wide branch is untestable in Vitest; 4 pages need conditional placement. More moving parts for the same result. |
| C. Skip link + `<nav aria-label>` landmarks | New "Skip to content" link and labelled landmarks. | Does **not** fix focus order (nav still last at ≥660), needs new UI and strings in en-US **and** de-DE, new focus styles. Addresses a different criterion (2.4.1). Worth its own story if wanted. |
| D. A + C | Both. | Larger than one PR; C is separable. |

### Current state of files being modified (UPDATE)

- `web/src/components/dashboard/nav-chrome.tsx`: one component, one wrapper `<div className="mt-auto wide:order-first wide:mt-0">` containing `nav[data-slot=nav-chrome-bottom]` (`wide:hidden flex items-stretch justify-around border-t border-border px-2 pt-2.5 pb-4`) then `nav[data-slot=nav-chrome-top]` (`hidden wide:flex items-center justify-between border-b border-border px-4 py-2.5`, brand wordmark + four links + `ProfileMenu`). Each variant has its own four buttons with `aria-current`. **Preserve:** every class, `data-slot`, `aria-current`, icon, label key (`dashboard.nav.*`, `app.title`), the single `ProfileMenu` mount in the top variant. **Change:** remove the wrapper, add `placement`.
- `web/src/components/dashboard/dashboard-page.tsx` (~189): `<NavChrome …>` is the last child of `<main className="flex min-h-svh flex-col gap-4 p-4">`, after `data-slot="dashboard-content"`, before `MeterRegressionPromptDialog`. **Change:** add the top mount as first child; keep the bottom mount in place.
- `web/src/components/trend-history/trend-history-page.tsx` (~113), `web/src/components/tariff/tariff-radar-page.tsx` (~92), `web/src/components/settings/settings-page.tsx` (~170; `<main className="… gap-6 p-4">`, the logoff `Dialog` sits before NavChrome and is portalled, so unaffected): same shape, same change. Note the differing `onXClick` wiring per page (`onDashboardClick={onBack}` on three pages, `() => {}` for the active tab). Keep each page's wiring exactly; just factor it into one props object.
- `web/e2e/tab-order.spec.ts`: lines 3–13 header and 379–416 pin (see Task 1). `tabThrough()` already tags `nav: 'top'|'bottom'|null` via `closest('nav[data-slot]')` and `openScreen()` already clicks nav items with `getByRole(...).first()` (hidden variant excluded by role queries), so both helpers work unchanged.
- Tests: `nav-chrome.test.tsx` (renders both variants together today and expects two matches everywhere; jsdom never evaluates media queries), `dashboard-page.test.tsx` (index-based `[1]` at ~204).
- Docs: `deferred-work.md` entry and index line; `project-context.md` ~74.

### Preserve (must not break)

- Phones: bottom bar last in DOM, last in Tab order, visually bottom-pinned via `mt-auto` on a `min-h-svh` flex column; identical padding/border.
- Top nav: full-width (outside the 900px `*-content` column), `border-b`, brand + links + avatar; Account menu behaviour (`profile-menu.tsx`, Radix dropdown, returns focus to the avatar on Escape), Story 1.12 logoff flow, `supportsFederatedLogout`/`email` props.
- `data-slot` hooks used by `app-shell.spec.ts` (swap test), `locale-theme-sweep.spec.ts` (layout identity) and `tab-order.spec.ts`.
- `aria-current="page"` and the active tokens on both variants; the same `onXClick` handlers.
- All other tab-order tests (Settings sections, Tariff paired fields, entry grids, Profile menu cycle).
- 660px breakpoint and 900px column: untouched (`layout-constants.drift.test.ts` guards them).

### Architecture and project rules that apply

- **WCAG 2.4.3 rule (`project-context.md` Frontend rules):** visual reordering must not reorder focus; this story removes the last exception. Add any new reordered surface to `tab-order.spec.ts`.
- **UX-DR9:** one NavChrome component with the same active/`onXClick` props for both layouts; do not fork into two components.
- **i18n (AD-18):** no hardcoded strings; this story adds none. Existing keys only.
- **Dual-render test convention:** both variants are present in jsdom, so tests disambiguate by DOM-order indexing (deferred entry, accepted). Prefer `data-slot`-scoped queries in new/changed tests instead of adding more indices.
- **Process gates:** this story doc satisfies the no-undocumented-change gate; Task 8 satisfies the live-verification gate. Unmounted-guard rule: nothing async is added.
- **Testing:** colocated Vitest tests next to source; Playwright specs in `web/e2e/` with the faked-API pattern (`fakeApi`/`openScreen`). Real `Tab` key presses, never `.focus()` (spec-tab-order-check convention).

### Project Structure Notes

```text
web/
  src/components/dashboard/
    nav-chrome.tsx            # modified — placement prop, wrapper + wide:order-first removed
    nav-chrome.test.tsx       # modified — per-placement tests
    dashboard-page.tsx        # modified — top mount first in <main>, bottom mount unchanged
    dashboard-page.test.tsx   # modified — DOM-position test; fix [1] index + comment
  src/components/trend-history/trend-history-page.tsx (+ .test.tsx)   # modified
  src/components/tariff/tariff-radar-page.tsx                          # modified (no page test file today: add a minimal one only if needed for the DOM-position check, or cover via App.test.tsx)
  src/components/settings/settings-page.tsx (+ .test.tsx)              # modified
  e2e/tab-order.spec.ts       # modified — pin inverted into a guard
_bmad-artifacts/
  project-context.md          # modified — drop NavChrome exception (~line 74)
  implementation/deferred-work.md   # modified — remove resolved entry + index line
```

No new files, dependencies, tokens, strings, migrations or backend changes. One PR.

### Ask First (decision for Ralf)

1. **Which fix?** Recommendation: **Option A** (split placement, CSS-only). Choose B (hook) or A+C (add a skip link) only if you want a skip link for WCAG 2.4.1; that adds en-US and de-DE strings, a focus style and a token check, and probably deserves its own story.
2. Tariff Radar has no page-level test file today. Is adding a small `tariff-radar-page.test.tsx` for the DOM-position check acceptable, or should that page be covered by `App.test.tsx`/e2e only? (Default: e2e + `nav-chrome.test.tsx`, no new test file.)

### Previous work intelligence

- **8.1:** introduced the single component, `--breakpoint-wide`, and kept the DOM position on purpose; its Dev Notes explain mount-point and prop-drilling constraints (no router, `active` is a literal).
- **8.2–8.5:** the content wrapper (`data-slot="*-content"`) is deliberately separate from NavChrome so the top nav stays full-width.
- **spec-tab-order-check / Epic 8 retro:** established `tabThrough`, the `nav` stop tagging, the visual-order computation, and the rule that Playwright role queries ignore `display:none` elements.
- **8.12 / Epic 8 retro:** live verification was substituted in 6 of 12 stories; the procedure in `project-context.md` exists for exactly this. The 659px request can clamp to 660px.
- **Recent commits:** `71992ba` drift test (breakpoint/column guard), `cee2c59` tab-order spec (#86), `ba8cf6c` locale × theme sweep.

### References

- [Source: _bmad-artifacts/implementation/deferred-work.md — "NavChrome last in DOM at wide" (spec-tab-order-check, 2026-10-02)]
- [Source: _bmad-artifacts/implementation/spec-tab-order-check.md — NavChrome divergence decision; "Never: Fixing the NavChrome divergence"]
- [Source: _bmad-artifacts/implementation/8-1-responsive-nav-chrome-profile-menu.md — Dev Notes, AC #1–#2]
- [Source: _bmad-artifacts/implementation/epic-8-retro-2026-10-02.md — pattern 3, action #2, live-resize action #4]
- [Source: _bmad-artifacts/project-context.md — Frontend rules (WCAG 2.4.3, breakpoint guard), Process gates (live-resize procedure)]
- [Source: _bmad-artifacts/implementation/spec-deferred-work-triage.md — promotion decision]

## Dev Agent Record

### Agent Model Used

claude-sonnet-5-5

### Debug Log References

- Red phase: new e2e NavChrome tests failed 8/12 (all ≥660px) against unchanged code, passed at 659; new unit tests failed 25.

### Completion Notes List

- Option A (split placement, CSS-only) implemented; Ask First defaults taken (no new `tariff-radar-page.test.tsx`; Tariff Radar covered by e2e).
- `NavChrome` takes `placement: 'top'|'bottom'`, wrapper and `wide:order-first` removed, `mt-auto` moved to the bottom nav. Four pages mount top first in `<main>` and bottom last via one `navProps` object.
- Tests: Vitest 542/542 pass; `tsc -b` and lint clean (only pre-existing warnings); Playwright tab-order, app-shell, locale-theme-sweep 97/97 pass. `en-US`/`de-DE` untouched, no JS breakpoint added.
- `git grep wide:order-first web/` only matches the negative assertion in `nav-chrome.test.tsx`.
- **Task 8 live evidence (2026-10-04, local dev app `https://localhost:5173`, Ralf signed in, de-DE UI, real Tab key presses via Claude-in-Chrome, one fresh tab per width, `window.innerWidth` read back):**
  - 659 (read back 659; an earlier 660 request read back 659 while a stale tab shared the window and was redone after closing it): all four screens: first stop is content, last four stops are the bottom bar (Dashboard, Verlauf, Tarifradar, Einstellungen). `nav-chrome-top` `display:none`; `<main>` children: top(none), content, bottom(flex). Bottom bar rect y 564–641 of a 657px viewport on the short Dashboard (bottom-pinned).
  - 660 (read back 660): all four screens: stops 1–5 are Dashboard, Verlauf, Tarifradar, Einstellungen, Konto-Menü (rect top 28), then content (min rect top 89+). Top nav rect y16–73, full width 628, above content; bottom nav `display:none`.
  - 900 (read back 900; a first 900 request read back 660 and was discarded): identical result; top nav width 868 above the content column.
  - Not done (optional): 500px.
  - Note: key presses only reached the page after a screenshot call focused it; plain Tab immediately after navigate registered no stops.
  - **8.5 pending**: Ralf to resize the real window across 660 and report. Story stays in-progress until recorded.

### File List

- web/src/components/dashboard/nav-chrome.tsx
- web/src/components/dashboard/nav-chrome.test.tsx
- web/src/components/dashboard/dashboard-page.tsx
- web/src/components/dashboard/dashboard-page.test.tsx
- web/src/components/trend-history/trend-history-page.tsx
- web/src/components/trend-history/trend-history-page.test.tsx
- web/src/components/tariff/tariff-radar-page.tsx
- web/src/components/settings/settings-page.tsx
- web/src/components/settings/settings-page.test.tsx
- web/e2e/tab-order.spec.ts
- _bmad-artifacts/project-context.md
- _bmad-artifacts/implementation/deferred-work.md
- _bmad-artifacts/implementation/sprint-status.yaml
- _bmad-artifacts/implementation/10-3-navchrome-dom-order-tab-order.md

### Change Log

- 2026-10-04: Split NavChrome placement so DOM/Tab order matches visual order; inverted e2e pin into guard; docs closed out (Tasks 1–7).
