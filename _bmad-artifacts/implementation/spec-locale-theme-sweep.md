---
title: 'Locale × theme sweep (Epic 8 retro action item #1)'
type: 'chore'
created: '2026-10-02'
status: 'done'
baseline_commit: '9ac6048f87f2e52fa4e2d0f64874a0cd13bb92e0'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-artifacts/implementation/epic-8-retro-2026-10-02.md'
  - '{project-root}/_bmad-artifacts/project-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Locale (de-DE/en-US) and theme (light/dark) toggles are now user-reachable (8.10, 8.11, 8.12), but stories 8.3, 8.4 and 8.10 each verified only English / one theme. Long German labels (Import pill, "Wechselbonus", paired tariff fields) and dark-theme contrast near the 660px breakpoint are unproven.

**Approach:** Add one Playwright matrix (de-DE/en-US × light/dark × 659/660/900px × Dashboard, Trend History, Tariff Radar, Settings) asserting no horizontal overflow, no clipped text and WCAG AA text contrast; fix findings that are small and local, log the rest in `deferred-work.md`; finish with one live pass in real tabs.

## Boundaries & Constraints

**Always:** Reuse the faked-API pattern from `web/e2e/app-shell.spec.ts` (`openSettingsAt`, tariff/readings seeds). Theme set via `localStorage['energy-tracker-theme']` before load; locale via faked `/api/session`. Assertions must be diagnostic (fail on a real defect, no vacuous passes). Live pass uses a new real tab at target width (sandbox `resize_window` does not change `innerWidth`); the real test household Locale is restored to `de-DE` and device theme reset to System afterwards.

**Ask First:** Adding any dependency (e.g. `@axe-core/playwright`) — default is a small in-spec computed-style contrast helper. Any fix touching more than one component or any visual redesign. Any change to the 660px breakpoint (that is action item #3).

**Never:** Items #2 (tab order) and #3 (breakpoint single source of truth). Visual-regression/screenshot tooling (decided out of scope). New design tokens, new strings without both catalogs, backend changes, touching real data beyond Locale write/restore.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Cell passes | screen × locale × theme × width | `scrollWidth <= clientWidth` on `documentElement` and content column; no visible text node clipped (`scrollWidth > clientWidth` with `overflow:hidden/ellipsis`); text contrast ≥ 4.5:1 (≥ 3:1 large/UI) | N/A |
| Long German label | de-DE, 659px, Import pill / "Wechselbonus" / paired tariff fields | Label fits or wraps inside its container; adjacent controls not overlapped; hit areas stay ≥ 44px | Finding → fix if local, else log |
| Breakpoint edge | 659 vs 660px | 659 = narrow layout, 660 = wide layout in every cell (matrix cannot silently run in the wrong layout) | Fail with layout name in message |
| Dark + System | `light` stored, OS dark | Stored choice wins; matrix forces theme via storage, not OS | N/A |

</frozen-after-approval>

## Code Map

- `web/e2e/app-shell.spec.ts` -- source of fakes/seeds and boundary-assertion idioms to copy (lines ~140, ~318, ~959)
- `web/e2e/locale-theme-sweep.spec.ts` -- NEW matrix spec
- `web/src/lib/color-scheme.ts` -- `THEME_STORAGE_KEY`, theme resolution
- `web/src/index.css` -- `--breakpoint-wide: 660px`, color tokens
- `web/src/locales/{de-DE,en-US}/translation.json` -- label lengths
- `web/src/color-tokens.contrast.test.ts` -- existing token-pair contrast unit test (what it already covers; avoid duplicating)
- `_bmad-artifacts/implementation/deferred-work.md` -- log unfixed findings (append at end, chronological)

## Tasks & Acceptance

**Execution:**
- [x] `web/e2e/locale-theme-sweep.spec.ts` -- shared helper (session/seed routes, theme via `addInitScript`, navigate to screen) and generated matrix of 4 screens × 2 locales × 2 themes × {659, 660, 900}px -- covers retro action item #1 automation
- [x] same file -- overflow, clipping and contrast checks plus layout-identity assertion per width; dedicated long-label assertions (Import pill, Wechselbonus, paired tariff fields in de-DE)
- [x] Run matrix; for each finding: fix if local (one component, existing tokens) with the failing assertion as red/green, else append `[open]` entry to `deferred-work.md`
- [x] `_bmad-artifacts/implementation/sprint-status.yaml` -- note action item #1 progress and findings in the action's `note` (status stays `open` until merged)
- [x] Live pass (Chrome, new tab per width 659/660/900, de + en, light + dark, all four screens) and record results in this spec's Verification notes

**Acceptance Criteria:**
- Given the matrix spec, when `npm run test:e2e -- locale-theme-sweep` runs, then all 48 cells are green or each failing cell has a linked `deferred-work.md` entry marked `[open]` with `test.fixme` referencing it.
- Given de-DE at 659px in light and dark, when Dashboard, Trend History, Tariff Radar and Settings render, then there is no horizontal page scroll and no clipped label.
- Given any cell, when the viewport is 659px, then the narrow layout is active, and at 660px the wide layout is active.
- Given the live pass, when completed, then results per width/locale/theme are recorded, Locale is restored to `de-DE`, device theme is System.

## Spec Change Log

## Implementation Notes (2026-10-02)

- Matrix: 51 e2e tests (48 cells + Import pill ×2 + tariff labels) green; full e2e 75/75; `npm test` green.
- Findings: no overflow, clipping or German-label defects. 6 colour pairs were below AA (3.9-4.45:1; 188 offender hits). Fixed per Ralf's choice: `--muted-foreground` light/dark (`index.css`), `--nav-chrome-active-foreground` light, `--attractiveness-not-worth-it-text` dark, `unit-input.tsx` light unit alpha.
- Import pill visible box is 40px (<660) / 32px (>=660); 44px hit area is a ::before extension, so only collapse is asserted.
- Environment: local `certs/` made `vite preview` serve HTTPS while `playwright.config.ts` assumed HTTP. At Ralf's request the config now mirrors vite.config's cert check (https + `ignoreHTTPSErrors` locally, http in CI); `npx playwright test` runs 75/75 green with the project config.
- Live pass (2026-10-02, real household, Chrome): same-origin iframes at 659/660/900px (media queries evaluate against frame width) × de-DE/en-US × light/dark × 4 screens, same audit as the matrix. 47/48 cells clean. One finding: Tariff Radar history table scrolls ~10px at 659px de-DE (real data with correction notes) — logged `[open]` in `deferred-work.md`. Locale written to en-US for the English pass and restored to `de-DE` (verified via /api/session); device theme reset to System.
- Review (2026-10-02, Blind + Edge hunters): patched — matrix seed enriched (corrections, closed tariff rows), `reducedMotion` + content-slot wait instead of a 400ms sleep, positive theme/screen assertions, left-edge off-viewport check, dead `data-allow-scroll` hatch removed, long-label tests no longer pass vacuously, unit contrast guards added for muted-foreground and nav-active, stale index.css comment fixed. New real finding: icon-only header buttons are 40px (no 44px hit area) — logged. Known gap: the 659px de-DE table scroll has no automated guard (fakes reproduce only 1px) — logged, so the AC "each failing cell fixme'd" is met by a deferred entry, not a test.
- Substitution: iframes rather than separate real tabs (`resize_window` does not change `innerWidth`) — same accepted evidence as Story 8.12.

## Design Notes

Matrix is generated with nested `for` loops (same idiom as the theme-persistence test at `app-shell.spec.ts:938`) so each cell reports as its own test. Layout identity: Dashboard `nav[data-slot="nav-chrome-top"]` visible iff ≥660. Clipping check: for each visible element with direct text, flag when `scrollWidth > clientWidth + 1` and computed `overflow-x` is not `visible`. Contrast: resolve foreground and effective background (walk ancestors until non-transparent), compute WCAG ratio; skip text over images/gradients and report skipped count.

## Verification

**Commands:**
- `cd web && npm run build && npm run test:e2e -- locale-theme-sweep` -- expected: matrix green or fixme'd with logged findings
- `cd web && npm test && npm run lint` -- expected: green (any fix must not break existing tests)

**Manual checks:**
- Live pass per I/O matrix in real tabs at 659/660/900px; Locale restored to `de-DE`, theme to System.

## Suggested Review Order

**The sweep (entry point)**

- The 48-cell matrix: layout identity, theme/locale, then audit per cell.
  [`locale-theme-sweep.spec.ts:276`](../../web/e2e/locale-theme-sweep.spec.ts#L276)

- In-browser audit: overflow, clipping, off-viewport, WCAG contrast with canvas colour resolution.
  [`locale-theme-sweep.spec.ts:189`](../../web/e2e/locale-theme-sweep.spec.ts#L189)

- Screen setup: theme forced against opposite OS scheme, reduced motion, content-slot guard.
  [`locale-theme-sweep.spec.ts:157`](../../web/e2e/locale-theme-sweep.spec.ts#L157)

- Fake API seed, including corrections and closed tariff rows.
  [`locale-theme-sweep.spec.ts:34`](../../web/e2e/locale-theme-sweep.spec.ts#L34)

**Long German labels**

- Import pill size at 659/660px; documents the 40px (no 44px) finding.
  [`locale-theme-sweep.spec.ts:306`](../../web/e2e/locale-theme-sweep.spec.ts#L306)

- Wechselbonus label fit and paired-field overlap, no vacuous passes.
  [`locale-theme-sweep.spec.ts:327`](../../web/e2e/locale-theme-sweep.spec.ts#L327)

**Contrast fixes (global colour tokens)**

- Light and dark muted text: largest blast radius, most hits.
  [`index.css:144`](../../web/src/index.css#L144)
  [`index.css:236`](../../web/src/index.css#L236)

- Active nav foreground darkened to clear AA on its tint.
  [`index.css:214`](../../web/src/index.css#L214)

- Dark not-worth-it figure text lightened.
  [`index.css:283`](../../web/src/index.css#L283)

- Unit label alpha raised for light theme.
  [`unit-input.tsx:34`](../../web/src/components/ui/unit-input.tsx#L34)

**Guards and config**

- Unit-level contrast guards for the two changed token pairs.
  [`color-tokens.contrast.test.ts:149`](../../web/src/color-tokens.contrast.test.ts#L149)

- Playwright base URL follows the same cert check as vite.config (HTTPS locally).
  [`playwright.config.ts:8`](../../web/playwright.config.ts#L8)

**Bookkeeping**

- Three new `[open]` findings (table scroll, 40px buttons, audit gaps).
  [`deferred-work.md`](./deferred-work.md)
