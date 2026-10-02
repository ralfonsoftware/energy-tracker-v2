---
title: 'Keyboard tab-order check at ≥660px (Epic 8 retro action item #2)'
type: 'chore'
created: '2026-10-02'
status: 'done'
baseline_commit: 'c6b8d5f98dac139709f4d61ba9ff42c84e6c1244'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-artifacts/implementation/epic-8-retro-2026-10-02.md'
  - '{project-root}/_bmad-artifacts/project-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Epic 8 reorders content visually at ≥660px (`wide:order-N` Settings sections, `wide:order-first` NavChrome, paired Tariff fields, grids) while the DOM stays unchanged, and no test anywhere asserts keyboard tab order. That is an untested WCAG 2.4.3 (Focus Order) risk, flagged in 8.4 and 8.5/8.8 and still unowned.

**Approach:** Add one Playwright spec that presses Tab through each reordered surface at ≥660px and asserts focus order matches visual order, with the single known divergence (NavChrome: visually first, last in tab order) pinned as an intentional, documented exception. No production behavior changes.

## Boundaries & Constraints

**Always:** Reuse the faked-API pattern from `web/e2e/locale-theme-sweep.spec.ts` / `app-shell.spec.ts`. Drive real keyboard Tab/Shift+Tab, never `.focus()`. Compare focus order against *visual* order computed from `getBoundingClientRect` (row-major: top, then left) so the check is diagnostic, not a restatement of the DOM. Run at 660px and 900px; also run 659px for the narrow layout (visual order there equals DOM order). The pinned divergence must fail loudly if NavChrome's position changes either way.

**Ask First:** Any production-code change. Any new dependency. Treating a newly found divergence other than NavChrome as intentional.

**Never:** Fixing the NavChrome divergence (decision: document and pin it, log the fix as a separate story in `deferred-work.md`). Action items #1 and #3 (breakpoint single source of truth). Skip links, new strings or tokens, backend changes.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Settings sections | Settings, ≥660px, Tab through | Focus visits Yearly Baseline, AI Plausibility, Household, Rooms/Power Points, Data export/import in visual row-major order | Fail naming the two controls out of order |
| Paired Tariff fields | Tariff Radar compare form + Settings tariff form, ≥660px | Left field before right field, row by row | Same |
| Entry grids | Meter Readings / Events / Tariff History grids, ≥660px | Items focused row-major | Same |
| NavChrome (documented divergence) | Any screen, ≥660px | Top nav + Profile avatar are visually first but last in tab order; at <660px the bottom bar is last both ways | Fail if order changes; message points at the deferred story |
| Profile menu | ≥660px, open via keyboard | Tab: Appearance strip, Language strip, Log off, wraps; Shift+Tab reverses; Escape returns focus to avatar | Fail with observed sequence |

</frozen-after-approval>

## Code Map

- `web/src/components/settings/settings-page.tsx` -- `wide:order-1..4` wrappers (DOM order already equals 1-4) and `wide:hidden` Account group.
- `web/src/components/dashboard/nav-chrome.tsx` -- `wide:order-first` on the last DOM child of `<main>`; the known divergence.
- `web/src/components/dashboard/profile-menu.tsx` -- Radix menu with custom `cycleTabStops`.
- `web/src/components/preferences/preference-strip.tsx` -- radiogroup with roving tabindex (one Tab stop per strip).
- `web/src/components/tariff/tariff-comparison-form.tsx`, `tariff-configuration-form.tsx` -- `wide:flex-row` field pairs.
- `web/src/components/entry-grid/entry-grid.tsx`, `tagging-scaffold/tagging-scaffold-manager.tsx` -- CSS grids, no `dense` flow.
- `web/e2e/locale-theme-sweep.spec.ts` -- fakeApi/openScreen helpers to copy (not import; specs are standalone).

## Tasks & Acceptance

**Execution:**
- [x] `web/e2e/tab-order.spec.ts` -- new spec: shared helper that Tabs N times collecting `{accessible name, rect}` per stop and asserts visual row-major order; cases per I/O matrix at 660 and 900px (plus 659 narrow) -- closes the repo-wide gap
- [x] `web/e2e/tab-order.spec.ts` -- NavChrome pin: assert first Tab stop on each of the four screens is page content, top-nav links and avatar come after it, and bottom bar likewise at 659px -- documents the intentional divergence
- [x] `web/e2e/tab-order.spec.ts` -- Profile menu keyboard test per matrix, incl. Escape focus return -- covers `cycleTabStops`
- [x] `_bmad-artifacts/implementation/deferred-work.md` -- append at END a `[open]` entry: NavChrome tab order (render nav first in DOM at ≥660px via `useWideBreakpoint`, or add skip link) with the WCAG 2.4.3 rationale -- keeps the fix owned
- [x] `_bmad-artifacts/project-context.md` -- add one line under Frontend rules: visual reorder via `wide:order-*` must keep tab order = visual order, except documented NavChrome -- prevents recurrence
- [x] Resolve the stale 2026-09 deferred entry (tariff form wrappers, "no tab order tests") by appending a note, not editing in place -- per append-only convention

**Acceptance Criteria:**
- Given any surface in the matrix at ≥660px, when Tab is pressed through it, then focus order equals visual row-major order, except the pinned NavChrome case.
- Given a future change that moves NavChrome in the DOM or adds an un-pinned reorder, when the spec runs, then it fails with the offending controls named.
- Given `cd web && npm run test:e2e -- tab-order` and `npm run lint` and `tsc -b`, then all pass with no production file changed.

## Design Notes

Stop identity uses accessible name plus `data-slot`/role where names collide (several "Settings" buttons exist in both navs). Hidden navs (`display:none`) are never tab stops, so only the layout-visible nav is checked. Visual order is computed, not hardcoded, so a CSS reorder without a DOM change is exactly what trips it.

## Verification

**Commands:**
- `cd web && npx playwright test e2e/tab-order.spec.ts` -- expected: all pass
- `cd web && npx tsc -b && npx oxlint` -- expected: clean

**Manual checks (if no CLI):**
- Temporarily swap two `wide:order-N` values in `settings-page.tsx`; the spec must fail naming the sections. Revert.

**Evidence (run 2026-10-02):** `npx playwright test e2e/tab-order.spec.ts` 22 passed, twice in a row; `tsc -b` clean; `oxlint` no new warnings. Mutation check run (preview serves the *built* bundle, so rebuild with `npx vite build` first): changing `wide:order-3` to `wide:order-9` in `settings-page.tsx` failed both Settings cases naming "Data → Rooms, Power Points & Devices", then reverted.

## Suggested Review Order

**The decision: NavChrome divergence pinned, not fixed**

- The one real focus-order divergence: nav is visually first at ≥660px but last in the DOM.
  [`nav-chrome.tsx:48`](../../web/src/components/dashboard/nav-chrome.tsx#L48)

- Pin on all four screens and three widths; fails if the nav moves either way.
  [`tab-order.spec.ts:385`](../../web/e2e/tab-order.spec.ts#L385)

- Fix kept as its own story, with candidate approaches and WCAG rationale.
  [`deferred-work.md:535`](deferred-work.md#L535)

**How the check works (group tab order vs visual order)**

- Real Tab presses, with focus-trap and cap guards so a truncated walk cannot pass.
  [`tab-order.spec.ts:171`](../../web/e2e/tab-order.spec.ts#L171)

- Groups, not single controls: column blocks legitimately tab column by column.
  [`tab-order.spec.ts:230`](../../web/e2e/tab-order.spec.ts#L230)

- Deterministic row clustering for visual reading order, computed from layout.
  [`tab-order.spec.ts:248`](../../web/e2e/tab-order.spec.ts#L248)

**Surfaces covered**

- Settings `wide:order-N` sections plus the side-by-side AI/Household pair.
  [`tab-order.spec.ts:282`](../../web/e2e/tab-order.spec.ts#L282)

- Paired Tariff fields (add and compare forms) at 659, 660 and 900px.
  [`tab-order.spec.ts:312`](../../web/e2e/tab-order.spec.ts#L312)

- Entry-grid tiles: every seeded tile reachable, multi-column grid proven.
  [`tab-order.spec.ts:342`](../../web/e2e/tab-order.spec.ts#L342)

- Profile menu Tab cycle, reverse wrap, Escape returns focus to the avatar.
  [`tab-order.spec.ts:420`](../../web/e2e/tab-order.spec.ts#L420)

**Guard rails and bookkeeping**

- Prose rule so new reordered surfaces join the spec; exception named.
  [`project-context.md:74`](../project-context.md#L74)
