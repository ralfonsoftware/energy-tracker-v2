---
baseline_commit: a37f87cbf77fbd084af963ce0b386a64a1d8af5d
---

# Story 8.4: Tariff Radar Desktop/Tablet Layout

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want the Tariff forms at ≥660px to use compact, paired input fields,
so that I don't see a single number field stretched across the whole screen with its unit far from the value.

## Acceptance Criteria

1. **Given** the "Add Tariff" form is displayed at ≥660px, **when** the page loads, **then** Monthly Base Fee and Price per kWh render as composed value+unit fields (unit-inside-field), side by side in one row instead of each alone on full width (UX-DR23).
2. **Given** the "Compare Tariff" form is displayed at ≥660px, **when** the page loads, **then** Candidate Base Fee, Candidate Price/kWh, and Switching Bonus use the same paired unit-inside-field pattern.
3. **Given** the "Your current Tariff" and "Candidate Tariff" result cards (pure reference values, no input), **when** rendered at ≥660px, **then** they use the "quiet" card tier; the "Is it worth switching?" verdict card with the signal rows remains in the "glass" tier (UX-DR24).
4. **And** all cards are constrained to the 660px column (UX-DR19).

## Tasks / Subtasks

- [x] **Task 1: Constrain Tariff Radar's page content to a centered 660px column at ≥660px** (AC #4)
  - [x] In `web/src/components/tariff/tariff-radar-page.tsx`, wrap the page's own content — the `<h1>` (line 62), `<TariffCheckCard>` (line 64), and the existing card stack `<div className="flex flex-col gap-[var(--spacing-card-gap)]">` (`TariffConfigurationForm`/`TariffHistoryList`/`TariffComparisonForm`, lines 66-83) — in a single new wrapper `<div data-slot="tariff-radar-content" className="flex flex-col gap-4 wide:mx-auto wide:w-full wide:max-w-[660px]">`, exactly mirroring `dashboard-page.tsx:134`'s `data-slot="dashboard-content"` wrapper (Story 8.2) and `trend-history-page.tsx`'s `data-slot="trend-history-content"` wrapper (Story 8.3) — same literal `max-w-[660px]`, same `wide:` prefixes, same `{page}-content` naming convention.
  - [x] Do **not** wrap `<NavChrome>` (lines 85-94) in this constraint — its top-nav variant is deliberately full-width per Story 8.1/8.2/8.3 precedent.
  - [x] `<main>` keeps `flex min-h-svh flex-col gap-4 p-4` unchanged.
  - [x] Unlike Dashboard/Trend History, Tariff Radar has no header icon buttons needing a `wide:`-visible label — there is no Task-2-style deferred obligation from a prior story for this page (checked Story 8.3's Review Findings: none reference Tariff Radar).

- [x] **Task 2: Pair Monthly Base Fee + Price per kWh in the "Add Tariff" form at ≥660px** (AC #1)
  - [x] **Scope Reality Check — read before starting:** the epic's AC #1 text ("render as composed value+unit fields... instead of each alone on full width") describes the *pre-Epic-8* screenshot state the critique mockup was built from. The actual shipped code already uses the unit-inside-field pattern via the shared `UnitInput` component (`web/src/components/ui/unit-input.tsx`, built in Story 5.1) for both fields — the value and unit already render in one bordered box. **The only real gap is the row-pairing at ≥660px.** Do not rebuild or modify `UnitInput`.
  - [x] In `tariff-configuration-form.tsx`, wrap the two existing field `<div className="flex flex-col gap-2">` blocks (lines 82-96: Monthly Base Fee; lines 98-112: Price per kWh) in a new row wrapper: `<div className="flex flex-col gap-4 wide:flex-row wide:gap-4">`, and add `wide:flex-1` to each inner field div's className (`flex flex-col gap-2 wide:flex-1`). Reuses the form's own existing `gap-4` vertical rhythm for the new horizontal gap too — no new spacing value invented.
  - [x] No `min-w-0` addition needed: `Input`'s own base classes (`web/src/components/ui/input.tsx`) already include `w-full min-w-0`, which `UnitInput`'s className override doesn't remove — the paired `wide:flex-1` fields won't overflow at exactly 660px.
  - [x] Currency, Contract Start Date, and Contract Period Months (lines 114-153) stay **unpaired**, each its own full-width row, unchanged. AC #1 names only Monthly Base Fee + Price per kWh; the confirmed mockup's Tarifradar-Formular section (`critique-desktop-breakpoint-2026-09-23.html`, Section 2) doesn't show Currency/Start Date/Period at all — there is no confirmed design to pair them against. Do not invent a pairing for them.
  - [x] Below 660px, both fields keep stacking full-width exactly as today — `flex-col` is the base state; `wide:flex-row`/`wide:flex-1` apply only at the breakpoint.

- [x] **Task 3: Pair the Candidate fields in the "Compare Tariff" form at ≥660px** (AC #2)
  - [x] Same Scope Reality Check as Task 2 applies: `tariff-comparison-form.tsx`'s three candidate fields already use `UnitInput` (Story 5.1/5.2) — only the row-pairing is new.
  - [x] In `tariff-comparison-form.tsx`, wrap Candidate Monthly Base Fee (lines 136-150) + Candidate Price per kWh (lines 152-166) in the identical row wrapper pattern from Task 2 (`flex flex-col gap-4 wide:flex-row wide:gap-4`, each field div gaining `wide:flex-1`).
  - [x] Wrap Candidate Switching Bonus (lines 168-181) in a second row of the same wrapper, with an empty `<div className="wide:flex-1" aria-hidden="true" />` as its sibling — this matches the confirmed mockup's own two-column grid for the odd third field (`critique-desktop-breakpoint-2026-09-23.html` lines 689-692: `<div class="after-field">...Wechselbonus...</div><div class="after-field"></div>`) instead of letting the field stretch to full row width alone at ≥660px. This is a plain empty `<div>` — no form control, no test/AC impact beyond visual width.
  - [x] Below 660px, all three fields keep stacking full-width exactly as today.

- [x] **Task 4: Apply the "quiet" tier to the current/candidate result summary cards; keep the verdict card "glass"** (AC #3)
  - [x] In `tariff-comparison-form.tsx`, import `QuietCard` from `@/components/ui/quiet-card` (already built in Story 8.3 — its own Dev Notes and `DESIGN/components.md` both explicitly name Tariff Radar's two summary panels as the next consumers: *"this is the token's second real consumer beyond `TariffCheckCard`... Tariff Radar's two summary panels, Story 8.4, will be the third/fourth."*). Do not build a new component.
  - [x] Swap the two `<GlassCard className="flex flex-col gap-2">` wrappers around the "Your current Tariff" summary (lines 204-211, heading key `tariff.compare.summary.currentHeading`) and the "Candidate Tariff" summary (lines 213-231, heading key `tariff.compare.summary.candidateHeading`) to `<QuietCard className="flex flex-col gap-2">` — import swap + tag rename only, no other structural change (mirrors Story 8.3 Task 4's `per-plug-data-card.tsx` `GlassCard` → `QuietCard` swap exactly).
  - [x] Leave the "Is it worth switching?" verdict `<GlassCard className="flex flex-col gap-3">` (lines 236-283, containing the two `SignalRow`s) **unchanged** — `DESIGN/components.md`'s Tariff comparison card section is explicit: the signal card "keeps the glass tier... it is the screen's actual payoff, not a passive reference."
  - [x] Leave unchanged: the `result === null` empty-state `<GlassCard size="lg">` (lines 193-196 — not one of the two named "reference values" cards) and the outer form-wrapping `<GlassCard>` (line 131 — contains the input fields, so UX-DR24 requires it stay glass).
  - [x] **Resolved ambiguity — apply this tier change unconditionally, not `wide:`-gated**, following Story 8.3's identical resolved ambiguity for `PerPlugDataCard`/`TariffCheckCard`. `DESIGN/components.md`'s text carries no breakpoint qualifier ("these two summary panels are pure read-only reference display — they now use `{colors.surface-quiet}`"), and `TariffCheckCard` (this same page) has used the quiet tier at every breakpoint since Story 5.4. `QuietCard` has no `wide:` variant machinery at all — this is a plain, always-on component substitution, correct at every width including <660px.

- [x] **Task 5: Automated test coverage**
  - [x] Extend `web/src/components/tariff/tariff-configuration-form.test.tsx`: assert the new row wrapper around Monthly Base Fee/Price per kWh carries `wide:flex-row`, and each field's wrapper div carries `wide:flex-1` (match the file's existing query style — locate via the field `Label`'s `htmlFor`/parent traversal, or add a `data-slot` if that's cleaner given jsdom limitations; mirror whatever query approach `trend-history-page.test.tsx` used for its own wrapper-class assertion in Story 8.3).
  - [x] Extend `web/src/components/tariff/tariff-comparison-form.test.tsx`: same class assertions for the three candidate fields' row wrappers, plus assert the empty spacer div exists in the switching-bonus row. Also assert — after a comparison result renders — the current/candidate summary panels carry `data-slot="quiet-card"` (not `data-slot="glass-card"`; `QuietCard`'s own `data-slot`, see `web/src/components/ui/quiet-card.tsx:14`), while the verdict panel still carries `data-slot="glass-card"` (`web/src/components/ui/glass-card.tsx:41`).
  - [x] **jsdom does not evaluate real CSS media queries** (same limitation Stories 8.1/8.2/8.3 documented) — unit tests can only prove markup/class presence, not actual rendered breakpoint behavior. Extend `web/e2e/app-shell.spec.ts` with a new Tariff Radar case mirroring the existing Dashboard/Trend History ones:
    - Route-mock `/api/session`, `/api/status`, `/api/tariff-check`, `/api/meter-regression-prompts/open` as the existing tests do; additionally mock `/api/tariffs?*` (seed one current Tariff so `TariffComparisonForm` actually renders — `tariff-radar-page.tsx`'s `currentTariffCurrency` gating, lines 44-58) and `/api/tariffs/compare` (a comparison result, reusing the `mockupWorkedExample` shape from `tariff-comparison-form.test.tsx`).
    - Navigate to Tariff Radar (`page.getByRole('button', { name: 'Tariff Radar' }).first().click()`, matching the Trend History case's nav-click pattern).
    - At 500px/659px: assert the Monthly Base Fee and Price per kWh field boxes stack (same `x`, different `y` via `getBoundingClientRect()`). At 660px/1000px: assert they sit side by side (same `y`, different `x`, roughly equal `width`) — a precise geometric measurement, not a visual-only class-presence check, per Story 8.2's code-review-driven precedent.
    - At ≥660px, submit the compare form and assert `getComputedStyle(...).backdropFilter` reads `none` on the current/candidate summary panels (`[data-slot="quiet-card"]`) and the glass blur value on the verdict panel (`[data-slot="glass-card"]`), mirroring Story 8.3's exact `backdropFilter` assertion technique.
    - Assert `[data-slot="tariff-radar-content"]` measures ~660px wide and centered at 1000px, tracking the viewport at 500px — same technique as the Dashboard/Trend History cases.
  - [x] Run `npm --prefix web run test`, `tsc -b`, `oxlint`, and the extended/new Playwright spec clean before marking any task complete. No backend changes in this story — no `.NET` test run required.

- [x] **Task 6: Live Chrome verification (mandatory — do not defer to a later story)**
  - [x] This story is pure browser-dependent responsive-layout and visual-surface behavior (the same trigger condition Stories 8.1/8.2/8.3 hit) — it cannot move review→done without a live Claude-in-Chrome verification actually performed in this session. If Chrome isn't connected, raise it immediately and pause rather than deferring.
  - [x] Reuse the established workaround: `resize_window` on an already-navigated tab does not reliably change `window.innerWidth` in this sandbox — open a *new* tab already sized to the target width, then navigate it (Story 8.3's Debug Log documents the exact retry pattern that worked).
  - [x] Ensure the dev household has at least one Tariff configured and run one comparison live (seed via the UI if none exist) so both forms render their populated, not empty/pre-fill, state.
  - [x] Verify live, specifically, at a tab opened ≥660px (e.g. 1000px): (a) `[data-slot="tariff-radar-content"]` measures ~660px and is centered (equal left/right gaps via `getBoundingClientRect()`), matching Dashboard/Trend History's already-verified pattern; (b) the Add Tariff form's Monthly Base Fee and Price per kWh fields sit side by side, roughly equal width, via `getBoundingClientRect()` — not eyeballing; (c) after running a comparison, the Compare form's three candidate fields show the same paired/spacer layout; (d) `getComputedStyle(...).backdropFilter` reads `none` on the current/candidate summary panels and the glass blur value (`blur(20px) saturate(1.4)`, per Story 8.3's Debug Log) on the verdict panel.
  - [x] Verify live at a tab opened <660px (e.g. 500px): all fields stack single-column full-width exactly as before this story, and the content column tracks the viewport width — no regression. Include the 659px/660px exact-boundary pair, matching Stories 8.1/8.2/8.3's established precedent.

- [x] **Task 7: Verify against every AC**
  - [x] Walk AC #1-#4 individually and state in Completion Notes exactly what proves each one — code reference, automated test, or live verification (Task 6) — following the same AC-by-AC accounting Stories 1.12/1.7/8.1/8.2/8.3 used.

## Dev Notes

- **This is a UI-only story with no new domain capability, no backend change, and no new architecture decision** — Epic 8's own framing, same as Stories 8.1/8.2/8.3. UX-DR19 (660px column), UX-DR23 (paired unit-inside-field rows), and UX-DR24 (glass/quiet card-hierarchy tiers) are the drivers.
- **Scope Reality Check (read before starting either form task):** the epic AC text for #1/#2 describes the *pre-Epic-8* state — a bare field with a separate stepper/unit label at the far edge — because that's what the critique mockup's "before" screenshots capture. The live codebase already moved past that: `UnitInput` (`web/src/components/ui/unit-input.tsx`, Story 5.1) already composes value+unit in one bordered box for every Tariff form field. **This story's real, narrower scope is: (1) the 660px page-column wrap, (2) row-pairing two fields at ≥660px in each form, (3) the quiet-tier swap on two result cards.** Do not rebuild `UnitInput` or treat the unit-inside-field pattern itself as missing.
- **`--breakpoint-wide: 660px` already exists** (`web/src/index.css:10`, Story 8.1's `@theme inline` block) and generates the `wide:` responsive variant reused here — do not add a second breakpoint token.
- **`QuietCard` already exists** (`web/src/components/ui/quiet-card.tsx`, Story 8.3) — a drop-in `GlassCard` replacement (same `rounded-glass-md`/padding/gap shape, no blur/shadow/ring). This story is explicitly its planned third/fourth consumer per `DESIGN/components.md` and Story 8.3's own Dev Notes — do not build a second quiet-tier component.
- **Design tokens/patterns to reuse verbatim (do not invent new ones):** `max-w-[660px]` literal for the column wrapper (Story 8.2/8.3 precedent); `data-slot="{page}-content"` naming (`dashboard-content`, `trend-history-content` → `tariff-radar-content` here); the form's own existing `gap-4` for the new row-pairing gap (no new spacing scale — the critique mockup's own intro states "no new spacing scale" except explicitly `[PROPOSAL]`-marked items, and field pairing's *gap value* isn't one of those proposals, only the row-pairing concept itself is `[PROPOSAL 2]`).
- **Card-hierarchy tiers (UX-DR24), full picture for this page:** `TariffCheckCard` is already quiet-tier (Story 5.4, renamed onto the shared token in Story 8.3). `TariffHistoryList` and the outer wrapping `<GlassCard>` of both forms **keep** glass tier — they contain an Edit action / input fields respectively. Only the comparison result's two summary panels (current/candidate) move from glass to quiet; the verdict panel with the signal rows stays glass. This is the exact scope `DESIGN/components.md`'s "Tariff comparison card" section describes.
- **No new i18n keys needed** — this story only changes layout classes (`flex`/`wide:` utilities) and swaps a card component; no new UI copy is introduced.
- **`onLoaded`/`currentTariffCurrency` gating (`tariff-radar-page.tsx` lines 44-58) is unaffected** — `TariffComparisonForm` still only renders once a current Tariff exists; this story doesn't touch that logic, only the JSX inside the already-rendered forms.
- **Testing standard reminder:** frontend unit tests are colocated next to source, Vitest + Testing Library, `jsdom` — real breakpoint/viewport behavior can only be proven by the Playwright e2e spec (`web/e2e/app-shell.spec.ts`) and live Chrome verification (Task 6), per `project-context.md` and Stories 8.1/8.2/8.3's own hard-won lesson. Precise pixel/geometry measurements need `getBoundingClientRect()`/`getComputedStyle()`, not visual estimation.
- **No backend change, no new FR/NFR, no new architecture decision** — this story does not trigger the OIDC/claims-verification condition of the project's live-verification gate, only the browser-dependent-responsive-layout condition (same as Stories 8.2/8.3).

### Project Structure Notes

```text
energy-tracker-v2/
  web/src/
    components/
      tariff/
        tariff-radar-page.tsx                # modified — new wide:max-w-[660px] content
                                               # wrapper (Task 1)
        tariff-configuration-form.tsx         # modified — Monthly Base Fee + Price per kWh
                                               # paired into a wide:flex-row (Task 2)
        tariff-configuration-form.test.tsx    # extended (Task 5)
        tariff-comparison-form.tsx            # modified — candidate fields paired (Task 3);
                                               # current/candidate summary panels
                                               # GlassCard -> QuietCard (Task 4)
        tariff-comparison-form.test.tsx       # extended (Task 5)

  web/e2e/
    app-shell.spec.ts                         # extended — new Tariff Radar breakpoint case
                                               # (Task 5)
```

No changes expected to `web/src/components/ui/unit-input.tsx`, `web/src/components/ui/quiet-card.tsx`, `web/src/components/ui/glass-card.tsx`, `web/src/components/tariff/tariff-check-card.tsx`, `web/src/components/tariff/tariff-history-list.tsx`, `web/src/components/tariff/edit-tariff-dialog.tsx`, `web/src/components/dashboard/nav-chrome.tsx`, `web/src/index.css`, any backend project, or any i18n resource file.

### References

- [Source: _bmad-artifacts/planning/epics/epic-8-desktop-tablet-layout-correction.md#Story 8.4: Tariff Radar Desktop/Tablet Layout] — story statement and acceptance criteria (lines 78-98); epic-level framing (lines 3-10: no new FR, UX-DR19/23/24 drivers, story sequencing places this after Trend History).
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md#UX-DR19] — full amended 660px-column rule text (line 130).
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md#UX-DR23] — full paired unit-inside-field rule text (line 131): extends the pattern to Tariff Radar's Grundgebühr/Preis-pro-kWh/Wechselbonus fields, pairs two related fields per row at ≥660px.
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md#UX-DR24] — card-hierarchy glass/quiet tier rule (line 132), the basis for Task 4.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/components.md#Tariff comparison card, lines 27-31] — confirms the current/candidate summary panels move to `{colors.surface-quiet}` (Task 4) while the verdict/signal card keeps glass; confirms the unit-inside-field extension to both Tariff forms and the ≥660px row-pairing rule (Task 2/3), citing the exact same critique mockup as the source.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/critique-desktop-breakpoint-2026-09-23.html] — Section 2 ("Tarifradar — Formular", lines 447-534) for the Add Tariff form's before/after field-pairing markup; Section 4 ("Tarifradar — Vergleich", lines 634-708) for the Compare form's paired candidate fields (including the switching-bonus spacer div, lines 689-692) and the `.kv-card.quiet` result-panel treatment (lines 619-624, 695-696) versus the unchanged `.kv-card` verdict panel (line 697).
- [Source: _bmad-artifacts/implementation/8-3-trend-history-desktop-tablet-layout.md] — read in full before starting. Establishes the `data-slot="{page}-content"` wrapper pattern (Task 1 here, reused verbatim), the `QuietCard` component this story consumes (Task 4), the "apply quiet-tier unconditionally, not `wide:`-gated" resolved ambiguity this story follows identically, and the `resize_window`-before-navigation live-verification workaround.
- [Source: _bmad-artifacts/implementation/8-2-dashboard-desktop-tablet-layout.md] — origin of the `data-slot="dashboard-content"` wrapper pattern and the precision-measurement (not visual-only) code-review precedent this story's Task 5/6 follow.
- [Source: web/src/components/tariff/tariff-radar-page.tsx] — target file for Task 1; current header/`TariffCheckCard`/card-stack structure (lines 60-96), `currentTariffCurrency` gating (lines 44-58).
- [Source: web/src/components/tariff/tariff-configuration-form.tsx] — target file for Task 2; Monthly Base Fee field (lines 82-96), Price per kWh field (lines 98-112), unpaired Currency/Start Date/Period fields (lines 114-153) — confirms `UnitInput` already in use (lines 84, 100).
- [Source: web/src/components/tariff/tariff-comparison-form.tsx] — target file for Tasks 3-4; candidate fields (lines 136-181), current-tariff `GlassCard` summary (lines 204-211), candidate-tariff `GlassCard` summary (lines 213-231), verdict `GlassCard` (lines 236-283), empty-state `GlassCard` (lines 193-196), outer wrapping `GlassCard` (line 131).
- [Source: web/src/components/ui/unit-input.tsx] — confirms the unit-inside-field pattern is already fully built (Story 5.1); do not modify.
- [Source: web/src/components/ui/quiet-card.tsx] — the exact component Task 4 swaps in; `data-slot="quiet-card"` (line 14) for test assertions.
- [Source: web/src/components/ui/glass-card.tsx] — `data-slot="glass-card"` (line 41) for the verdict-card-unchanged test assertion.
- [Source: web/src/components/ui/input.tsx] — confirms `Input`'s base classes already include `w-full min-w-0` (line 11), preempting a flex-overflow concern at the paired-field breakpoint boundary.
- [Source: web/src/components/dashboard/dashboard-page.tsx:128-134] — the exact `data-slot="dashboard-content"` wrapper markup Task 1 mirrors.
- [Source: web/src/index.css:10] — `--breakpoint-wide: 660px` token (already exists, Story 8.1).
- [Source: web/e2e/app-shell.spec.ts] — existing Playwright viewport-resize/route-mock conventions from Stories 8.1/8.2/8.3 to extend (Task 5); Trend History case (lines 139-236) is the closest structural precedent (page-under-test has its own data fetches beyond `/api/status`/`/api/tariff-check`).
- [Source: web/src/components/tariff/tariff-comparison-form.test.tsx] — existing test conventions and the `mockupWorkedExample` fixture (lines 15-31) reusable for Task 5/e2e result-card assertions.
- [Source: _bmad-artifacts/project-context.md#Critical Don't-Miss Rules, Process gates] — the mandatory live-verification gate this story triggers on the browser-dependent-behavior condition (Task 6).

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5

### Debug Log References

- `npm --prefix web run test -- --run tariff-configuration-form tariff-comparison-form`: 21/21 passing.
- `npm --prefix web run test -- --run`: full suite 49 files / 413 tests passing, no regressions.
- `npx tsc -b`: clean.
- `npx oxlint`: clean (same 4 pre-existing warnings in untouched files as prior stories).
- `playwright test app-shell.spec.ts`: 5/5 passing, including the new Tariff Radar breakpoint case — run against a temporary local-HTTPS `playwright.config.ts` override (`baseURL`/`webServer.url` switched to `https://localhost:4173` + `ignoreHTTPSErrors: true`) to work around this sandbox's pre-existing `certs/vite-dev-cert.*` making `vite preview` serve HTTPS while the tracked `playwright.config.ts` assumes HTTP — the identical local-environment quirk Stories 8.2/8.3 already documented and worked around the same way. Reverted via `git checkout` immediately after the run; not a tracked-file change.
- Live-verification session (Task 6, 2026-09-27): reused the already-running local dev stack (Postgres via Docker, API via `dotnet run` on :5133, Vite dev server on :5173), driven via Claude-in-Chrome against `https://localhost:5173`, already-authenticated real Auth0 session. `resize_window` on an already-navigated tab confirmed unreliable again in this sandbox (stuck reporting the previous tab's width); the working pattern was always: `tabs_create_mcp` → `resize_window` on that brand-new, not-yet-navigated tab → `navigate` — `window.innerWidth` then read back correctly every time. One extra wrinkle this session: even a fresh tab's resize could silently not "stick" once (a `659`-target tab read back `500`); closing it and retrying with a brand-new tab id fixed it — treat one no-op resize as reason to retry with a fresh tab, not as a hard blocker.
  - At 1000px (`window.innerWidth` 1001): `[data-slot="tariff-radar-content"]` measured exactly 660px wide with equal 170.5px left/right gaps (170.5 + 660 + 170.5 = 1001) — true centering, not just a max-width cap. Add Tariff's Monthly Base Fee/Price per kWh field boxes: same `y` (350), equal `width` (297), side by side.
  - Created a live Tariff (12.50 USD / 0.32 USD/kWh) via the UI (a prior current Tariff, 8.50 USD, already existed from an earlier session) so the Compare form rendered, then ran one live comparison (candidate 14.90/0.315/350 bonus) — both signal rows rendered ("Worth switching" / "Not worth it"), proving the already-shipped FR-13 behavior survived this story's changes untouched.
  - Compare form's candidate fields, same tab: Candidate Monthly Base Fee + Candidate Price per kWh at identical `y` (-44 relative to scroll position, i.e. same row), equal width (297); Candidate Switching Bonus alone in the next row (`y` 36), same `x`/`width` as the row above — confirming the spacer `<div>` correctly reserves its column instead of letting the field stretch full-width.
  - After the live comparison rendered: `getComputedStyle(...).backdropFilter` read exactly `none` on both `[data-slot="quiet-card"]` panels ("Your current tariff", "Candidate tariff") and `blur(20px) saturate(1.4)` on the verdict `[data-slot="glass-card"]` ("Is it worth switching?") — confirmed via `document.querySelectorAll` + a `.find()` guarding against the outer form-wrapping `GlassCard` (which also contains the verdict heading text as a descendant, so a naive text-match would pick up the wrong ancestor — same containment gotcha as the e2e spec's `.last()` fix, Task 5).
  - At 500px (`window.innerWidth` 500): content column measured 468px (tracks the viewport, not capped at 660px); Monthly Base Fee/Price per kWh field boxes shared the same `x` (41), different `y` (317 vs. 401) — stacked, matching pre-story behavior.
  - Exact-boundary pair: at `window.innerWidth` 659 (a 659px-sized fresh tab), fields still stacked (same `x` 41, different `y`). At `window.innerWidth` 661 (nearest achievable to 660 in this sandbox — a 660px-sized fresh tab read back 659, so the tab was resized to 661 instead to guarantee landing on the ≥660px side of the breakpoint), fields already sat side by side (same `y` 362, equal `width` 281.5) — the breakpoint transition is confirmed to land at 660px, not drifted by an off-by-one in `--breakpoint-wide` or the `max-w-[660px]` literal.
  - All tabs closed after verification, per cleanup convention.

### Completion Notes List

- Task 1: `tariff-radar-page.tsx` wraps the `<h1>`/`TariffCheckCard`/card-stack in `data-slot="tariff-radar-content"` with `wide:mx-auto wide:w-full wide:max-w-[660px]`, mirroring `dashboard-content`/`trend-history-content` verbatim. `NavChrome` and `<main>` left untouched.
- Task 2: `tariff-configuration-form.tsx`'s Monthly Base Fee + Price per kWh field divs now sit inside a `flex flex-col gap-4 wide:flex-row wide:gap-4` row wrapper, each with `wide:flex-1`. Currency/Contract Start Date/Contract Period Months left unpaired, unchanged.
- Task 3: `tariff-comparison-form.tsx`'s Candidate Monthly Base Fee + Candidate Price per kWh paired identically; Candidate Switching Bonus placed in its own row alongside an empty `aria-hidden` spacer `<div className="wide:flex-1" />`.
- Task 4: `tariff-comparison-form.tsx`'s current/candidate summary panels swapped `GlassCard` → `QuietCard` (import + tag rename only). Verdict panel, empty-state panel, and outer form-wrapping `GlassCard` left unchanged (still glass).
- Task 5: extended both component test files with row-wrapper/spacer/`data-slot` assertions (21 tests total, all passing); added a new Playwright case to `app-shell.spec.ts` covering the 500/659/660/1000px breakpoints (field stacking → pairing, content-column width) and `backdropFilter` on quiet vs. glass cards after a live comparison submission. Full frontend suite (`vitest`, `tsc -b`, `oxlint`, `playwright`) all clean.
- Task 6: live Claude-in-Chrome verification performed this session — see Debug Log for the full breakpoint/geometry/backdropFilter readings. No regressions found; the 660px breakpoint, centering, field pairing, spacer column, and glass/quiet tier split all matched the automated test expectations exactly when measured against the real running app.
- Task 7 — AC-by-AC accounting:
  - **AC #1** (Add Tariff form: Monthly Base Fee + Price per kWh paired, unit-inside-field, at ≥660px): `tariff-configuration-form.tsx`'s row wrapper (`wide:flex-row`/`wide:flex-1`); `UnitInput` (pre-existing, Story 5.1) already composes value+unit. Proven by `tariff-configuration-form.test.tsx`'s new row-wrapper-class assertion, the `app-shell.spec.ts` Tariff Radar case's field-geometry assertions at 660/1000px, and live verification (Task 6: same-`y`, equal-`width` field boxes at 1000px and at the 661px boundary).
  - **AC #2** (Compare Tariff form: Candidate Base Fee/Price/Switching Bonus paired at ≥660px): `tariff-comparison-form.tsx`'s two row wrappers (fields + spacer). Proven by `tariff-comparison-form.test.tsx`'s row-wrapper/spacer assertions, the e2e case's candidate-field geometry check, and live verification (Task 6: candidate fields paired, spacer reserves the switching-bonus row's second column).
  - **AC #3** (current/candidate summary cards use "quiet" tier; verdict card stays "glass"): `tariff-comparison-form.tsx`'s `GlassCard` → `QuietCard` swap on the two summary panels only. Proven by `tariff-comparison-form.test.tsx`'s `data-slot="quiet-card"`/`"glass-card"` assertions, the e2e case's `backdropFilter` check (`none` vs. `blur(20px) saturate(1.4)`), and live verification (Task 6: identical `backdropFilter` readings against the real running app).
  - **AC #4** (all cards constrained to the 660px column): `tariff-radar-page.tsx`'s `data-slot="tariff-radar-content"` wrapper. Proven by the e2e case's content-column width assertions at 500/1000px and live verification (Task 6: exactly 660px wide, centered with equal 170.5px gaps, at 1000px; 468px — tracking the viewport — at 500px).

### File List

- `web/src/components/tariff/tariff-radar-page.tsx` (modified)
- `web/src/components/tariff/tariff-configuration-form.tsx` (modified)
- `web/src/components/tariff/tariff-configuration-form.test.tsx` (modified)
- `web/src/components/tariff/tariff-comparison-form.tsx` (modified)
- `web/src/components/tariff/tariff-comparison-form.test.tsx` (modified)
- `web/e2e/app-shell.spec.ts` (modified)
