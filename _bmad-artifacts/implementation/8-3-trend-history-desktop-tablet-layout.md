---
baseline_commit: af754285dd6bcff54d0767e74ea925f4b2c8defb
---

# Story 8.3: Trend History Desktop/Tablet Layout

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want Meter Readings and Events in Trend History at ≥660px shown as a dense list without wasted space,
so that I can see more entries at a glance instead of a table with a wide dead column.

## Acceptance Criteria

1. **Given** the Trend History page is displayed at an available width ≥660px, **when** the page loads, **then** the trend chart, the Meter Readings list, the Events list, and the Room → Power Point → Device card are constrained to the 660px column (UX-DR19).
2. **Given** the Meter Readings or Events list is expanded, **when** rendered at ≥660px, **then** table rows show their content without a wide unused gap between the timestamp and any trailing content (prior finding, Meter Readings specifically: ~40% of row width was dead space between date and the Edit action).
3. **Given** the Room → Power Point → Device card (pure reference display, no interaction), **when** rendered, **then** it uses the "quiet" card tier instead of the "glass" tier still used by, e.g., the Meter Readings list (which has an Edit action) (UX-DR24).

## Tasks / Subtasks

- [x] **Task 1: Constrain Trend History's page content to a centered 660px column at ≥660px** (AC #1)
  - [x] In `web/src/components/trend-history/trend-history-page.tsx`, wrap the page's own content — the header row (`<h1>`/Smart Plug Import button, currently lines 77-88) and the card stack `<div className="flex flex-col gap-[var(--spacing-card-gap)]">` (chart `GlassCard`, `MeterReadingsCard`, `EventsCard`, `PerPlugDataCard`, lines 90-104) — in a single new wrapper `<div data-slot="trend-history-content" className="flex flex-col gap-4 wide:mx-auto wide:w-full wide:max-w-[660px]">`, exactly mirroring `dashboard-page.tsx`'s `data-slot="dashboard-content"` wrapper from Story 8.2 (same literal `max-w-[660px]`, same `wide:` prefixes). Do **not** wrap `<NavChrome>` (lines 106-115) in this constraint — its top-nav variant is deliberately full-width per Story 8.1/8.2 precedent.
  - [x] `max-w-[660px]` as a literal Tailwind arbitrary value matches this codebase's established convention (`dashboard-page.tsx`'s own wrapper, `status-card.tsx:88`, `tariff-comparison-form.tsx:195`) — reuse it verbatim, do not invent a new token. `--breakpoint-wide: 660px` (index.css, Story 8.1) already exists for the media-query side.
  - [x] `<main>` keeps `flex min-h-svh flex-col gap-4 p-4` unchanged.

- [x] **Task 2: Add a visible "Import" label to Trend History's Smart Plug Import button at ≥660px** (not in Epic 8's own AC text for this story — a committed obligation from Story 8.2, see Dev Notes' "Resolved ambiguity #1")
  - [x] In `trend-history-page.tsx`'s header button (currently lines 79-87), add the same `wide:` pill-shape classes and label span Story 8.2 already added to Dashboard's identical Import button (`dashboard-page.tsx:154-163`) — reuse the **existing** `smartPlugImport.shortLabel` i18n key verbatim (already added to both `en-US`/`de-DE` in Story 8.2 specifically for this reuse; do **not** create a new key):
    ```tsx
    <button
      type="button"
      onClick={onSmartPlugImportClick}
      aria-label={t('smartPlugImport.entryPointLabel')}
      title={t('smartPlugImport.entryPointLabel')}
      className="bg-nav-chrome-active-bg text-nav-chrome-active-foreground flex size-10 shrink-0 items-center justify-center rounded-xl wide:size-auto wide:justify-start wide:gap-1.5 wide:px-3 wide:py-2"
    >
      <Upload className="size-4" aria-hidden="true" />
      <span className="hidden wide:inline wide:text-xs wide:font-semibold">{t('smartPlugImport.shortLabel')}</span>
    </button>
    ```
  - [x] Keep the existing `aria-label`/`title` unchanged — already verified in Story 8.2 to contain "Import" as a case-insensitive substring (WCAG 2.5.3), same string reused here.
  - [x] **Do not move this button into `NavChrome`.** The critique mockup's "after" frame visually shows it relocated into the top-nav's right side, but Story 8.2 explicitly decided to keep Dashboard's matching Import button in the page's own header instead (see Dev Notes #1) — match that shipped precedent for consistency between the two screens' identical buttons, not the mockup's literal arrangement.

- [x] **Task 3: Eliminate the wide dead-space gap in table rows** (AC #2)
  - [x] In `web/src/components/meter-reading/meter-readings-card.tsx`'s `<Table>` (lines 95-101): add `className="w-px"` to the Timestamp `<TableHead>` and to the empty action `<TableHead />` — `whitespace-nowrap` is already applied by `TableHead`'s own base classes (`ui/table.tsx:71`), and `width: 1%` (Tailwind `w-px`) combined with `whitespace-nowrap` is the standard technique that makes a `table-layout: auto` browser shrink a column to its content's minimum width instead of distributing leftover row width into it — leave the Value `<TableHead>` unset so it naturally absorbs the remaining row width. Add `className="text-right"` to the action `<TableCell>` (line 122) to right-align the Edit button against its now-tight column, matching the confirmed mockup (`critique-desktop-breakpoint-2026-09-23.html`, `.readings-table` rows, `text-align:right` on the edit cell).
  - [x] Apply the identical `w-px` fix to the Timestamp `<TableHead>` in `web/src/components/event/events-card.tsx` (lines 85-89) for consistency between the two tables using the same `Table`/`TableCell` primitives. **Note:** `EventsCard` has only two columns (description, timestamp) and no Edit action — AC #2's epic-level wording names "Meter Readings or Events list" together, but the ~40% dead-space finding and the literal "value/date/Edit action" row shape apply only to `MeterReadingsCard`'s 3-column table (confirmed by `critique-desktop-breakpoint-2026-09-23.html`, which only shows the Meter Readings table's before/after, and by `DESIGN/components.md`'s Events card description, which lists no Edit control). Do not invent a "value" column or an Edit action for Events — this is a defensive consistency pass on the same underlying table-layout mechanism, not a second confirmed bug.
  - [x] This is a table-layout correctness fix, not breakpoint-gated behavior — apply the classes unconditionally (no `wide:` prefix). It is a strict improvement at any width, including <660px; there is no "revert below 660px" requirement in this story's AC (contrast with Task 1/2, which are `wide:`-scoped by design).

- [x] **Task 4: Formalize the shared `{colors.surface-quiet}` token and apply the "quiet" tier to the Room → Power Point → Device card** (AC #3)
  - [x] In `web/src/index.css`: add `--surface-quiet` / `--surface-quiet-border` to the `:root` block (light: `rgba(255, 255, 255, 0.4)` / `rgba(40, 70, 50, 0.08)`, exactly the current `--tariff-check-card-bg`/`-border` values at lines 214-215) and to the `.dark` block (dark: `rgba(220, 245, 230, 0.03)` / `rgba(210, 235, 220, 0.05)`, exactly the current values at lines 290-291). **Reuse these numbers verbatim — do not invent new ones.** `DESIGN/colors.md`'s "Quiet surface" section states the token is "unchanged from the Tariff Check card, not a new hue," even though its own prose quotes the dark border as `rgba(210,235,220,0.06)` — that figure doesn't match what's actually shipped in `index.css` (`0.05`); carry forward the shipped `0.05`, following Story 8.2's "reuse verbatim" discipline over the doc's prose. This ~0.01-alpha documentation drift predates this story; noting it here, not silently reconciling it, is sufficient.
  - [x] In the `@theme inline` block (lines 77-82), replace the `--color-tariff-check-card-bg`/`-border` entries with `--color-surface-quiet: var(--surface-quiet); --color-surface-quiet-border: var(--surface-quiet-border);` — update the preceding comment to describe the generalized, Epic 8-formalized token rather than the Story 5.4-specific one.
  - [x] Remove the old `--tariff-check-card-bg`/`-border` custom properties from `:root` and `.dark` entirely (fully superseded — no duplicate/back-compat alias).
  - [x] In `web/src/components/tariff/tariff-check-card.tsx:26`, change the className from `bg-tariff-check-card-bg border-tariff-check-card-border` to `bg-surface-quiet border-surface-quiet-border`. No other change to this file — it stays its own plain `<button>`/`<div>` implementation (see Dev Notes #4 for why it is *not* refactored onto the new shared component below).
  - [x] **Update `web/src/color-tokens.contrast.test.ts`** — a real, currently-passing consumer of the old token name that is easy to miss because it reads `index.css` by parsing, not by import: rename the `'tariff-check-card-bg'` string literal (line ~143) and the enclosing `describe(...)` title (line 140, "Tariff check card...") to reference `surface-quiet` instead. No new assertion is needed for `PerPlugDataCard` specifically — its own text (`muted-foreground`, default `foreground`) renders against the exact same token pair this test already verifies, since `surface-quiet`'s near-transparent alpha composites to essentially the same tint as the value it replaces.
  - [x] Create `web/src/components/ui/quiet-card.tsx` (`QuietCard`), modeled directly on `glass-card.tsx`'s inner `<Card>` usage — same `rounded-glass-md` radius, `p-[var(--spacing-card-padding)]` padding, `gap-[var(--spacing-card-gap)]` gap (so swapping tiers changes only the surface material, never the card's shape/spacing) — but with **no** panel-back depth layer, **no** `backdrop-blur`/`backdrop-saturate`, **no** box-shadow, **no** `ring`, and `bg-surface-quiet border-surface-quiet-border` instead of the glass tokens (matching `DESIGN/colors.md`: "near-transparent, no backdrop blur, no border emphasis"). This is the token's second real consumer beyond `TariffCheckCard` (Tariff Radar's two summary panels, Story 8.4, will be the third/fourth per `DESIGN/components.md`) — build it as a reusable component now, not an inline className on `PerPlugDataCard` alone.
  - [x] In `web/src/components/trend-history/per-plug-data-card.tsx`, replace the `<GlassCard>`/`</GlassCard>` wrapper (lines 55, 111) with `<QuietCard>`/`</QuietCard>` — import swap only, no other structural change; internal spacing (`mt-3`, etc.) is unaffected since `QuietCard` reuses the identical padding/gap values.
  - [x] **Resolved ambiguity — this tier change applies at every breakpoint, not only ≥660px.** Epic 8's own AC text for this bullet says "When rendered at ≥660px," but `DESIGN/colors.md`, `DESIGN/components.md`, and `DESIGN/dos-and-donts.md` all describe `{colors.surface-quiet}` as a permanent card-hierarchy rule ("applied consistently across all screens"), not a wide-viewport-only correction — and the already-shipped `TariffCheckCard` has used this exact tier at every breakpoint since Story 5.4, with no `wide:` conditioning. Making `PerPlugDataCard`'s tier swap universal (not `wide:`-gated) is consistent with that precedent and with `DESIGN/components.md`'s explicit statement that the Meter Readings card "keeps the glass tier (it has an Edit action) — contrast with the Room → Power Point → Device card below it... which is pure reference display and uses `{colors.surface-quiet}` instead." Implementing a breakpoint-conditional variant here would require a second, more complex component (or duplicate mounts) for zero documented benefit — see Dev Notes #4.

- [x] **Task 5: Automated test coverage**
  - [x] Extend `web/src/components/meter-reading/meter-readings-card.test.tsx` and `web/src/components/event/events-card.test.tsx`: assert the new `w-px`/`text-right` classes exist on the relevant header/cell (match each file's existing query style).
  - [x] Extend `web/src/components/trend-history/trend-history-page.test.tsx`: assert the new `data-slot="trend-history-content"` wrapper carries `wide:max-w-[660px]`, and that the Import button renders a `hidden wide:inline` span with `smartPlugImport.shortLabel` text alongside the unchanged `aria-label` (mirror `dashboard-page.test.tsx`'s Story 8.2 assertions).
  - [x] `web/src/components/trend-history/per-plug-data-card.test.tsx`: no behavior change is expected from the `GlassCard` → `QuietCard` swap; confirm existing tests still pass unmodified.
  - [x] **jsdom does not evaluate real CSS media queries** (same limitation Stories 8.1/8.2 documented) — extend `web/e2e/app-shell.spec.ts` with a new Trend History case mirroring the existing Dashboard one (`'the Dashboard content column and header-icon-button labels swap at the 660px breakpoint'`): at ≥660px assert the content column's bounding box is ~660px (not the full window), the Import button shows visible "Import" text, and — via `getBoundingClientRect()` on the Meter Readings table's timestamp cell and Edit button (with at least one reading seeded so the table actually renders, not the empty state) — there is no large gap between them (a precise measurement, not a visual-only class-presence check, per Story 8.2's code-review-driven precedent). At <660px, assert the Import button stays icon-only and the content column tracks the viewport width. Include the 659px/660px exact-boundary pair Story 8.2's spec already established.
  - [x] Run `npm --prefix web run test`, `tsc -b`, `oxlint`, and the extended/new Playwright spec clean before marking any task complete. No backend changes in this story — no `.NET` test run required.

- [x] **Task 6: Live Chrome verification (mandatory — do not defer to a later story)**
  - [x] This story is pure browser-dependent responsive-layout and visual-surface behavior (the same trigger condition Stories 8.1/8.2 hit) — it cannot move review→done without a live Claude-in-Chrome verification actually performed in this session. If Chrome isn't connected, raise it immediately and pause rather than deferring.
  - [x] Reuse the established workaround from Stories 8.1/8.2's Debug Logs: `resize_window` on an already-navigated tab does not change `window.innerWidth` in this sandbox — open a *new* tab already sized to the target width, then navigate it.
  - [x] Verify live, specifically: (a) at a tab opened ≥660px (e.g. 1000px), Trend History's content column measures ~660px and is centered, matching Dashboard's already-verified pattern; (b) the Import button shows its icon plus visible "Import" text; (c) with the Meter Readings list expanded and at least one reading present (seed one if the dev household has none), the Edit button sits directly adjacent to the timestamp column with no large blank gap between them — use `getBoundingClientRect()` for a precise measurement, not a visual-only judgment (matching Story 8.2's code-review-driven precision-measurement precedent); (d) the Room → Power Point → Device card visually reads as flatter/less blurred than the Meter Readings card beside it — confirm via `getComputedStyle(...).backdropFilter` (should be `none` on the quiet card, present on the glass one) rather than eyeballing; (e) at a tab opened <660px (e.g. 500px), the Import button remains icon-only and the content column tracks the viewport width — but note the table dead-space fix (Task 3) and the quiet-tier change (Task 4) are *not* breakpoint-gated by design, so don't expect those two specific visuals to differ from ≥660px. **(a)-(d) verified live; (e) blocked by a genuine session-specific sandbox limitation, substituted with real-Chromium Playwright coverage — see Debug Log.**

- [x] **Task 7: Verify against every AC**
  - [x] Walk AC #1-#3 individually, plus the two obligations beyond the epic's literal AC text (the Import label reuse, Task 2; the universal quiet-tier/table-fix scope, Task 3/4's resolved ambiguities), and state in Completion Notes exactly what proves each one — code reference, automated test, or live verification (Task 6) — following the same AC-by-AC accounting Stories 1.12/1.7/8.1/8.2 used.

## Dev Notes

- **This is a UI-only story with no new domain capability, no backend change, and no new architecture decision** — Epic 8's own framing, same as Stories 8.1/8.2. UX-DR19 (660px column) and UX-DR24 (card-hierarchy quiet/glass tiers) are the drivers; UX-DR9/UX-DR27 (nav chrome/Profile menu) are Story 8.1's concern, already shipped, not touched here.
- **Resolved ambiguity #1 — the Import button label is this story's job, not optional.** Epic 8's own AC text for Story 8.3 does not mention labeling Trend History's Smart Plug Import button, but Story 8.2's own Review Findings explicitly flagged this: *"Cross-surface inconsistency: `trend-history-page.tsx`'s identical Smart Plug Import button stays icon-only/unlabeled until Story 8.3 reuses `smartPlugImport.shortLabel` — deliberate per this story's Dev Notes... deferred, pre-existing scope boundary (**Story 8.3's job**)"* (`8-2-dashboard-desktop-tablet-layout.md`, Review Findings). The `smartPlugImport.shortLabel` i18n key was added in Story 8.2 *specifically* for this reuse — implement Task 2 as a real obligation, not a nice-to-have.
- **Resolved ambiguity #2 — the quiet-tier swap and the table dead-space fix apply at every breakpoint, not only ≥660px**, despite Epic 8's AC phrasing ("When rendered at ≥660px"). See Task 3/4's own bullets for the full reasoning (design-doc citations + shipped `TariffCheckCard` precedent). Do not build `wide:`-conditional variants for either.
- **`--breakpoint-wide: 660px` already exists** (`web/src/index.css`, Story 8.1's `@theme inline` block) and generates the `wide:` responsive variant reused here (`wide:hidden`, `wide:max-w-[660px]`, etc.) — do not add a second breakpoint token.
- **Design tokens to reuse verbatim (do not invent new ones):** `bg-nav-chrome-active-bg`/`text-nav-chrome-active-foreground` for the Import button's fill (already used this way on Dashboard); `max-w-[660px]` literal for the 660px column wrapper (Story 8.2 precedent); the exact `--tariff-check-card-bg`/`-border` numeric values, carried into the new `--surface-quiet`/`-border` tokens (Task 4).
- **Card-hierarchy tiers (UX-DR24), full picture for this page:** `MeterReadingsCard` and `EventsCard` **keep** the glass tier (`GlassCard`, both have interactive controls — Edit / pagination) — no change to either's surface. Only `PerPlugDataCard` (Room → Power Point → Device, pure reference display, no interaction) moves from glass to quiet. This is confirmed explicitly in `DESIGN/components.md`'s Meter Readings card description: *"This card keeps the `{colors.surface-glass}` tier (it has an Edit action) — contrast with the Room → Power Point → Device card below it on the same page... which uses `{colors.surface-quiet}` instead (Epic 8, UX-DR24)."*
- **Why `TariffCheckCard` is *not* refactored onto the new `QuietCard` component:** `TariffCheckCard` is a polymorphic `<button>`/`<div>` (renders as a button only when `onClick` is passed) with its own `rounded-2xl p-4` shape — different from `QuietCard`'s fixed `rounded-glass-md p-[var(--spacing-card-padding)]` (18px/24px) shape used by `GlassCard`-tier cards on this page. Forcing it onto a shared `<div>`-only component would either lose its button polymorphism or require `QuietCard` to grow conditional-element complexity it doesn't otherwise need. `TariffCheckCard` only needs the *token rename* (Task 4); `QuietCard` exists for new consumers that don't have this constraint (`PerPlugDataCard` now; Tariff Radar's summary panels in Story 8.4).
- **`onSmartPlugImportClick` is a plain navigation callback, not a controlled overlay** (`web/src/App.tsx` sets `view` state to switch to `SmartPlugImportPage` — confirmed by reading `web/src/App.tsx:310-311,364-365`), unlike Dashboard's `LogEventSheet` (a real controlled `<Sheet>` with internal state). This means, unlike the Event button, there is no overlay-duplication risk in moving the Import button into `NavChrome` — but Task 2 still keeps it in the page's own header, for consistency with the already-shipped Dashboard implementation, not because of a technical constraint. Do not use this as license to move it into `NavChrome`; that would make Dashboard's and Trend History's identical buttons visually inconsistent with each other.
- **Testing standard reminder:** frontend unit tests are colocated next to source, Vitest + Testing Library, `jsdom` — real breakpoint/viewport behavior can only be proven by the Playwright e2e spec (`web/e2e/app-shell.spec.ts`), per `project-context.md` and Stories 8.1/8.2's own hard-won lesson. Precise pixel/gap measurements (Task 5/6) need `getBoundingClientRect()`/`getComputedStyle()`, not visual estimation — Story 8.2's code-review cycle specifically caught an inferred-but-unverified live claim; don't repeat that.
- **No backend change, no new FR/NFR, no new architecture decision** — this story does not trigger the OIDC/claims-verification condition of the project's live-verification gate, only the browser-dependent-responsive-layout condition (same as Story 8.2).

### Project Structure Notes

```text
energy-tracker-v2/
  web/src/
    index.css                                    # modified — --surface-quiet/-border tokens
                                                   # replace --tariff-check-card-bg/-border (Task 4)
    color-tokens.contrast.test.ts                 # modified — token rename (Task 4)
    components/
      ui/
        quiet-card.tsx                            # NEW — QuietCard, quiet-tier counterpart to
                                                   # GlassCard (Task 4)
      trend-history/
        trend-history-page.tsx                    # modified — new wide:max-w-[660px] content
                                                   # wrapper (Task 1); Import button gains wide:
                                                   # label span and pill-shape classes (Task 2)
        trend-history-page.test.tsx                # extended (Task 5)
        per-plug-data-card.tsx                     # modified — GlassCard -> QuietCard (Task 4)
      meter-reading/
        meter-readings-card.tsx                    # modified — w-px/text-right table classes
                                                    # (Task 3)
        meter-readings-card.test.tsx               # extended (Task 5)
      event/
        events-card.tsx                            # modified — w-px table class (Task 3)
        events-card.test.tsx                       # extended (Task 5)
      tariff/
        tariff-check-card.tsx                      # modified — className token rename only
                                                    # (Task 4)

  web/e2e/
    app-shell.spec.ts                              # extended — Trend History breakpoint case
                                                    # (Task 5)
```

No changes expected to `web/src/components/dashboard/nav-chrome.tsx`, `profile-menu.tsx`, `dashboard-page.tsx`, any backend project, or `web/src/index.css`'s `--breakpoint-wide` token (already exists from Story 8.1).

### References

- [Source: _bmad-artifacts/planning/epics/epic-8-desktop-tablet-layout-correction.md#Story 8.3: Trend History Desktop/Tablet Layout] — story statement and acceptance criteria (lines 58-77); epic-level framing (lines 3-10: no new FR, UX-DR19/UX-DR24 drivers, story sequencing).
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md#UX-DR19] — full amended 660px-column rule text (line 130).
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md#UX-DR24] — card-hierarchy glass/quiet tier rule (line 132), the basis for Task 4.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/colors.md#Quiet surface] — `{colors.surface-quiet}`/`-dark` formalization text, its origin as `TariffCheckCard`'s private token, and its exact concrete values (light/dark bg+border) — the basis for Task 4's token values.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/components.md] — Meter Readings card description confirming it keeps the glass tier (has an Edit action) in explicit contrast with the Room → Power Point → Device card's quiet tier; Tariff Radar summary-panel note confirming this token's next (Story 8.4) consumers.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/dos-and-donts.md] — line 10, the card-hierarchy Do/Don't pair, framed as a system-wide rule (not breakpoint-scoped).
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/layout-spacing.md] — 660px breakpoint rule, "on every surface" (line 9).
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/critique-desktop-breakpoint-2026-09-23.html] — Section 3 ("Verlauf", lines 536-633) for the before/after column layout, the `.readings-table`/`.edit-btn-sm` dense-row CSS (lines 222-227), and the `.kv-card.quiet` treatment for the Room → Power Point → Device card (lines 293-299, 619-624).
- [Source: _bmad-artifacts/implementation/8-2-dashboard-desktop-tablet-layout.md] — read in full before starting. Establishes the `data-slot="dashboard-content"` wrapper pattern (Task 1 there, reused verbatim here), the Import button's `wide:` pill-shape classes and `smartPlugImport.shortLabel` key (Task 2 there, reused verbatim here), the `resize_window`-before-navigation live-verification workaround, and — critically — its own Review Findings' explicit "Story 8.3's job" deferral for Trend History's Import label, and its Dev Notes' explicit deferral of formalizing `{colors.surface-quiet}` to this story.
- [Source: web/src/components/trend-history/trend-history-page.tsx] — target file for Tasks 1-2; current header row (lines 76-88), card stack (lines 90-104), `NavChrome` mount (lines 106-115).
- [Source: web/src/components/dashboard/dashboard-page.tsx:128-134,154-163] — the exact `data-slot="dashboard-content"` wrapper and Import-button markup this story's Tasks 1-2 mirror verbatim.
- [Source: web/src/components/meter-reading/meter-readings-card.tsx] — target file for Task 3; the 3-column `<Table>` (lines 95-135) with the dead-space bug.
- [Source: web/src/components/event/events-card.tsx] — target file for Task 3's consistency pass; the 2-column `<Table>` (lines 84-97), no Edit action.
- [Source: web/src/components/ui/table.tsx] — `TableHead`/`TableCell` base classes (`whitespace-nowrap` already present, lines 71,84) — Task 3 only needs to add `w-px`, not `whitespace-nowrap`.
- [Source: web/src/components/ui/glass-card.tsx] — the component `QuietCard` (Task 4) is modeled on: `rounded-glass-md`/`p-[var(--spacing-card-padding)]`/`gap-[var(--spacing-card-gap)]` shape, panel-back depth layer to *omit*.
- [Source: web/src/components/tariff/tariff-check-card.tsx] — target file for Task 4's token rename; confirms the quiet tier's origin (private tokens, polymorphic button/div, `rounded-2xl p-4` shape not shared with `QuietCard`).
- [Source: web/src/components/trend-history/per-plug-data-card.tsx] — target file for Task 4's `GlassCard` → `QuietCard` swap (lines 55, 111); internal spacing (`mt-3` etc.) unaffected.
- [Source: web/src/index.css:77-96,164-216,254-292] — `@theme inline` block, `:root` (light) and `.dark` blocks containing the current `--tariff-check-card-bg`/`-border` definitions Task 4 renames.
- [Source: web/src/color-tokens.contrast.test.ts:140-146] — the automated AA-contrast test that reads `index.css` by parsing (not import) and references the old token name literally; must be updated in the same change (Task 4), easy to miss otherwise.
- [Source: web/src/App.tsx:310-311,364-365] — confirms `onSmartPlugImportClick` is a plain view-switch callback, not a controlled sheet (Dev Notes).
- [Source: web/src/locales/en-US/translation.json:322-325, de-DE/translation.json:322-325] — existing `smartPlugImport.entryPointLabel`/`shortLabel` keys Task 2 reuses verbatim (both already added in Story 8.2).
- [Source: web/e2e/app-shell.spec.ts] — existing Playwright viewport-resize convention from Stories 8.1/8.2 to extend (Task 5).
- [Source: _bmad-artifacts/project-context.md#Critical Don't-Miss Rules, Process gates] — the mandatory live-verification gate this story triggers on the browser-dependent-behavior condition (Task 6).

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

- Full frontend suite passes clean: `vitest run` 411/411 (48 files), `tsc -b` clean, `oxlint` clean (only 4 pre-existing warnings in untouched files: `household-size-preset-row.tsx`, `badge.tsx`, `button.tsx`, `use-smart-plug-import-job.ts`). No backend changes in this story, so no `.NET` test run was needed.
- `playwright test app-shell.spec.ts` 4/4 passing, including the new Trend History breakpoint case — run against a temporary local-HTTPS `playwright.config.ts` override (`baseURL`/`webServer.url` switched to `https://localhost:4173` + `ignoreHTTPSErrors: true`) to work around this sandbox's pre-existing `certs/vite-dev-cert.*` making `vite preview` serve HTTPS while the tracked `playwright.config.ts` assumes HTTP — the identical local-environment quirk Story 8.2 already documented and worked around the same way. Reverted via `git checkout` immediately after the run; not a tracked-file change.
- A stale `vite preview --port 4173` process from an earlier session (PID 7702, running >1h, serving empty replies) was blocking the e2e webServer on port 4173; killed it before running Playwright. Unrelated to this story's code.
- Live-verification session (Task 6, 2026-09-26): reused the already-running local dev stack (Postgres via Docker, API via `dotnet run` on :5133, Vite dev server on :5173), driven via Claude-in-Chrome against `https://localhost:5173`, real Auth0 session (already authenticated).
- **(a)-(d) verified live at a tab opened at 1000px `window.innerWidth`:** Trend History's `[data-slot="trend-history-content"]` wrapper measured exactly 660px wide via `getBoundingClientRect()`, with equal 170px gaps on both sides (170 + 660 + 170 = 1000) confirming true centering, not just a max-width cap; the Import button showed its icon plus the visible "Import" label text; one Meter Reading was seeded live (the dev household had 0 at first fetch, then showed readings from a prior session once refetched — logged one more via the Dashboard's "Log reading" flow to be certain, resolving a "lower than last reading" reset/rollover prompt along the way, unrelated to this story) and the expanded Meter Readings table's Edit-button cell measured a `0`px gap from the Timestamp cell's right edge via `getBoundingClientRect()` — no dead space; `getComputedStyle(...).backdropFilter` read `blur(20px) saturate(1.4)` on `[data-slot="glass-card"]` (Meter Readings) and exactly `none` on `[data-slot="quiet-card"]` (Room → Power Point → Device), confirming the quiet tier is genuinely flatter, not just visually similar.
- **(e) (<660px icon-only/tracks-viewport check) could not be completed live this session** — a genuine, reproducible sandbox limitation, not a code defect. Unlike Stories 8.1/8.2's documented failure mode (`resize_window` silently not taking effect at all), this session's `resize_window` reported success but the browser window's actual `window.innerWidth` clamped to a **hard floor of exactly 660px** no matter what width was requested (tried 500, 400, and 660, both on already-navigated tabs and on brand-new tabs resized before navigation, across three separate fresh tab groups) — confirmed via direct `window.innerWidth` reads, not just screenshot inspection. This looks like a window-manager-level minimum-window-width constraint on this particular sandbox session, coincidentally landing exactly on this app's own `wide:` breakpoint value. Substituted with `web/e2e/app-shell.spec.ts`'s new Trend History Playwright case, which drives a real (non-jsdom) Chromium instance via `page.setViewportSize()` and passed at 500px, the exact 659px/660px boundary pair, and 1000px — this is genuine real-browser regression coverage for the narrow-side behavior (a), even though it wasn't additionally eyeballed live in this specific session.

### Completion Notes List

- **AC #1** (trend chart, Meter Readings list, Events list, and the Room → Power Point → Device card constrained to the 660px column at ≥660px): `trend-history-page.tsx`'s new `data-slot="trend-history-content"` wrapper (`wide:mx-auto wide:w-full wide:max-w-[660px]`) wraps the header row and the entire card stack (chart, `MeterReadingsCard`, `EventsCard`, `PerPlugDataCard`), mirroring `dashboard-page.tsx`'s Story 8.2 wrapper verbatim; `NavChrome` stays outside it, matching the established precedent. Proven by `trend-history-page.test.tsx`'s new wrapper-class assertion and, live, by a `getBoundingClientRect()` measurement showing an exact 660px column centered with equal 170px side gaps at a 1000px-wide tab (Debug Log).
- **Task 2 obligation** (visible "Import" label on Trend History's Import button at ≥660px, committed in Story 8.2's Review Findings): implemented with the identical `wide:` pill-shape classes and `smartPlugImport.shortLabel` reuse as Dashboard's button. Proven by `trend-history-page.test.tsx`'s new label-visibility-class assertion and, live, by the visible "Import" text at 1000px width (Debug Log).
- **AC #2** (no wide dead-space gap between the timestamp and trailing content in the Meter Readings/Events tables): `w-px` added to the Timestamp `TableHead` in both `meter-readings-card.tsx` and `events-card.tsx`, plus `w-px` on the empty action `TableHead` and `text-right` on the action `TableCell` in `meter-readings-card.tsx` only (Events has no Edit action, per Dev Notes). Applied unconditionally (no `wide:` prefix), per the story's resolved ambiguity. Proven by the two component tests' new class assertions and, live, by a `getBoundingClientRect()` measurement showing an exact `0`px gap between the Timestamp cell's right edge and the Edit button's cell (Debug Log).
- **AC #3** (Room → Power Point → Device card uses the "quiet" tier, not "glass"): `{colors.surface-quiet}`/`-border` formalized in `index.css` (values carried forward verbatim from the old `--tariff-check-card-*` tokens, which are now fully removed), consumed by the new `QuietCard` component (modeled on `GlassCard`'s shape, no panel-back/blur/shadow/ring) and swapped into `per-plug-data-card.tsx` in place of `GlassCard`. `tariff-check-card.tsx` renamed onto the same tokens without further refactor (Dev Notes explain why). Applied unconditionally at every breakpoint, per the story's resolved ambiguity. Proven by `color-tokens.contrast.test.ts`'s renamed AA-contrast assertion (still passing against the same numeric values), `per-plug-data-card.test.tsx`'s unmodified passing suite, and, live, by `getComputedStyle(...).backdropFilter` reading `none` on the quiet card versus `blur(20px) saturate(1.4)` on the adjacent glass-tier Meter Readings card (Debug Log).
- **Task 3/4 resolved-ambiguity scope** (table fix and quiet-tier swap apply at every breakpoint, not just ≥660px): both changes were implemented with no `wide:` prefix/conditioning at all, so there is nothing that could regress "below 660px" — the live verification at 1000px above is representative of the only rendering path that exists for these two changes.
- **Full frontend suite, `tsc -b`, `oxlint`, and the extended/new Playwright spec all pass clean** (Debug Log). No `.NET` test run needed — no backend changes in this story.

### File List

- `web/src/components/trend-history/trend-history-page.tsx` — modified (Task 1, Task 2)
- `web/src/components/trend-history/trend-history-page.test.tsx` — extended (Task 5)
- `web/src/components/trend-history/per-plug-data-card.tsx` — modified, `GlassCard` → `QuietCard` (Task 4)
- `web/src/components/meter-reading/meter-readings-card.tsx` — modified, `w-px`/`text-right` table classes (Task 3)
- `web/src/components/meter-reading/meter-readings-card.test.tsx` — extended (Task 5)
- `web/src/components/event/events-card.tsx` — modified, `w-px` table class (Task 3)
- `web/src/components/event/events-card.test.tsx` — extended (Task 5)
- `web/src/components/tariff/tariff-check-card.tsx` — modified, className token rename only (Task 4)
- `web/src/components/ui/quiet-card.tsx` — NEW, `QuietCard` component (Task 4)
- `web/src/index.css` — modified, `--surface-quiet`/`-border` tokens replace `--tariff-check-card-bg`/`-border` (Task 4)
- `web/src/color-tokens.contrast.test.ts` — modified, token rename (Task 4)
- `web/e2e/app-shell.spec.ts` — extended, new Trend History breakpoint case (Task 5)
