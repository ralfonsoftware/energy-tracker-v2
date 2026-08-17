---
baseline_commit: 89e313fa1eca35fb38e3a36ad8de9923c2a30750
---

# Story 2.5: Dashboard Status Display

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want to see my current Status as the first thing on the Dashboard,
so that I know if I'm on track without hunting for the answer.

## Acceptance Criteria

1. **Given** the Dashboard, **when** it loads, **then** the Status is visible without scrolling or drilling into a sub-view (FR-7).
2. **Given** the Status card, **when** rendered, **then** no chart is required to read it — it's legible as a single glanceable state: status dot, uppercase badge, headline sentence, supporting sentence (FR-7, UX-DR2).
3. **Given** first-ever load with no computable Status (fewer than two Readings or no Yearly Baseline, per Story 2.4), **when** the Dashboard renders, **then** it shows an onboarding prompt ("log your first reading to get started") rather than blank space or a default Status value (FR-7, UX-DR14).
4. **Given** the three real Status states (within range / below baseline / trending), **when** rendered, **then** each uses its dedicated AA-verified badge-text token (never the raw status-triad hex) and its own status color — never the brand-accent teal, and never a 4th "unknown" visual treatment (UX-DR1, UX-DR2).
5. **Given** the Status card, **when** rendered in Dark and Light mode, **then** both render the rear/front glass panel stack with backdrop blur+saturate as equal citizens — Dark shows the glow/specular treatment, Light substitutes frosted-white translucency with soft green-tinted drop shadows rather than attempting to replicate the Dark-mode glow (UX-DR11).
6. **Given** the Status card's data resolves, on cold load or after a recompute, **when** it appears, **then** it plays the settle + specular-sweep entrance animation once, gated behind `prefers-reduced-motion: no-preference`, with a fully settled/instant fallback when reduced motion is requested (UX-DR15).
7. **Given** a Status recompute happens while the Dashboard is open, **when** the value changes, **then** it's announced via `aria-live="polite"` rather than requiring a manual refresh check (UX-DR16).
8. **Given** the Dashboard's cold load, **when** data hasn't resolved yet, **then** a shadcn `Skeleton` matching the Status card's footprint is shown so nothing reflows on resolution (UX-DR14).
9. **Given** the Status headline/body copy, **when** rendered, **then** it follows the plain-language voice/tone discipline — named number, named thing that happened, never generic congratulation or gamified language (UX-DR17).
10. **Given** the Status card and any other Dashboard element, **when** compared, **then** the Status card remains the single highest-visual-weight surface on the Dashboard — nothing else visually competes with it (NFR15).
11. **Given** the Dashboard, **when** rendered, **then** it includes the primary "Log Reading" action button (pill shape, gradient fill) that opens the Log Reading sheet from Story 2.2; its press state compresses to ~0.965 scale with shadow pull-in, never a color flash (UX-DR8).
12. **Given** the bottom tab bar (mobile), **when** the Dashboard is the active surface, **then** its nav item uses the brand-accent-tinted active state, never a status color; the tab bar shell carries all four top-level entries (Dashboard, Trend History, Tariff Radar, Settings) per UX-DR9, with the latter three surfaces' content filled in by later epics.

## Tasks / Subtasks

- [x] **Task 1 — Status API client (AC #1, #3; Consistency Conventions)**
  - [x] Create `web/src/lib/status-api.ts`, `fetchCurrentStatus(): Promise<StatusDto | null>` calling `GET /api/status` (`credentials: 'include'`). Mirror `meter-regression-api.ts`'s exact empty-body-as-null handling: `GET /api/status` returns HTTP 200 with an **empty body** when Status is undefined (ASP.NET Core's `Results.Ok(null)` writes nothing, not the JSON literal `"null"`) — read as text first, `JSON.parse` only if non-empty, otherwise return `null`. Reuse the same `ApiError`/`toApiError` shape as `meter-regression-api.ts` for non-2xx responses.
  - [x] `StatusDto` fields (camelCase JSON from `StatusResponse` in `src/EnergyTracker.Api/Endpoints/StatusEndpoints.cs:69`): `status: 'withinRange' | 'belowBaseline' | 'trending'`, `paceToDateKwh: number`, `baselineToDateKwh: number`, `isLowConfidence: boolean`.

- [x] **Task 2 — GlassCard hero size variant (AC #2, #5, #10)**
  - [x] Extend `web/src/components/ui/glass-card.tsx` with a `size?: 'md' | 'lg'` prop (default `'md'`, preserving all existing behavior/tests unchanged). `'lg'` renders at `rounded-glass-lg` (28px, already defined in `index.css`'s `@theme`) with panel-back inset `9px_-6px_-13px_9px` and front-card padding `27px_23px_25px` — the exact values from `direction-green-eco.html`'s primary (non-`.inset`) frame, distinct from the existing `'md'`/18px "drill-down card" size (`key-settings.html`'s `.card`) the component already implements. The Status card is the **only** consumer of `'lg'` in this story — it is the product's hero surface (AC #10), which is why it gets the larger radius/padding the smaller `'md'` size doesn't.
  - [x] Extend `glass-card.test.tsx` with a case asserting `size="lg"` applies `rounded-glass-lg` and the default (`size` omitted) still applies `rounded-glass-md` (regression check).

- [x] **Task 3 — Status card entrance/specular-sweep motion (AC #6; EXPERIENCE.md Interaction Primitives)**
  - [x] Add two keyframe animations to `web/src/index.css`, values verbatim from `mockups/motion-demo.html`'s real (non-looping) segment: `card-entrance` (`opacity: 0 → 1`, `transform: scale(0.96) translateY(8px) → scale(1) translateY(0)`, `cubic-bezier(0.22,0.61,0.36,1)`, ~1.28s / 0.4s is also acceptable — motion-demo.html's 8s-loop timeline maps 0–16% to this phase, i.e. ~1.28s real duration, no bounce/overshoot) and `specular-sweep` (diagonal highlight, `linear-gradient(115deg, transparent 30%, rgba(220,245,230,0.14) 46%, transparent 62%)` dark / `linear-gradient(115deg, transparent 35%, rgba(255,255,255,0.6) 48%, transparent 60%)` light, opacity-ramped at both ends, ~2.2s, starting immediately after settle — not a loop). Implemented as Tailwind v4 `--animate-*` theme tokens (`--animate-status-card-entrance`, `--animate-status-card-specular-sweep`) referencing top-level `@keyframes`, applied via `motion-safe:animate-status-card-*` utility classes (Tailwind's built-in `prefers-reduced-motion: no-preference` variant, same variant family already used in `button.tsx`'s `motion-reduce:transition-none`).
  - [x] Gate **both** animations inside `@media (prefers-reduced-motion: no-preference)` only, via `motion-safe:` — the un-gated base CSS for the Status card renders `opacity: 1; transform: none` (final settled state, set directly in `status-card.tsx`, Task 4) and the specular overlay at `opacity: 0` with no animation-name, so reduced-motion is the *default*, not a stripped-down variant (matches `motion-demo.html`'s exact base-vs-media-query structure).
  - [x] Entrance plays **once** per data resolution: `status-card.tsx` (Task 4) keys the animated wrapper on a fingerprint of the resolved Status value so React remounts it and the CSS animation restarts exactly when the underlying Status actually changes, not on unrelated re-renders.

- [x] **Task 4 — StatusCard component: 3 real states + copy (AC #2, #4, #9)**
  - [x] Create `web/src/components/dashboard/status-card.tsx`. Uses `GlassCard size="lg"`. Layout top-to-bottom: status-row (dot + uppercase badge), headline sentence, supporting sentence — matches `DESIGN.md` Components → "Status card" content order exactly.
  - [x] Dot: solid fill from the raw triad token (`bg-status-within-range` / `bg-status-below-baseline` / `bg-status-trending` — already wired in `index.css`'s `@theme`, Story 2.2b). Badge: background from `-badge-bg`, text from `-badge-text` (the dedicated AA-verified pair, **not** the raw triad — raw triad fails 2.85–3.98:1 as small badge-label text, per `DESIGN/components.md` "Status card"). All three status-color families are already defined as CSS custom properties in both `:root` and `.dark` — no new color values need inventing this story, only correct application.
  - [x] Headline per state (verbatim from `mockups/key-dashboard.html`/`direction-green-eco.html`, the confirmed key-screen reference): `withinRange` → "Quiet week.", `belowBaseline` → "Well under baseline.", `trending` → "Worth a look."
  - [x] Supporting sentence: compose from `paceToDateKwh − baselineToDateKwh` (`difference`), rounded to the nearest whole kWh via `Intl.NumberFormat(locale, { maximumFractionDigits: 0 })` (AD-18). `difference < 0` → "{abs(difference)} kWh under pace."; `difference === 0` → "Right on pace."; `difference > 0` → "{difference} kWh over pace." **Confirmed with Ralf during dev-story activation:** numeric-only copy (no fabricated triggering event) — Event/Wattage-Plausibility correlation (FR-16/17, Epic 6) isn't built yet.
  - [x] When `isLowConfidence`, renders one additional quiet-weight line below the supporting sentence ("It's been a while since your last reading — treat this as a rough estimate."), at the `text-muted-foreground` quiet tier.
  - [x] Headline+supporting-sentence+low-confidence block wrapped in `aria-live="polite"` (AC #7).
  - [x] kWh figures use `tabular-nums` so digits don't jitter in width on update.

- [x] **Task 5 — Onboarding empty state + skeleton (AC #3, #8)**
  - [x] Empty state (Status `null`, non-error): renders inside the **same** `GlassCard size="lg"` footprint (not a different card shape) — "No Status yet" title, "Log your first reading to get started — Pattern Detective needs at least two to find your pace." body (verbatim, `mockups/key-dashboard.html`), and an `emptyStateAction` slot for the primary Log Reading action button (Task 6/8 renders the actual button into this slot), matching the mockup's empty frame.
  - [x] Cold-load skeleton (AC #8): scaffolded shadcn's `Skeleton` primitive (`npx shadcn add skeleton`). Skeleton bars render inside the same `GlassCard size="lg"` shape/dimensions as the real card (dot+badge row, headline-width bar, body-width bar) so resolution never reflows the page.
  - [x] A `GET /api/status` fetch failure degrading to the onboarding-empty treatment is `DashboardPage`'s own concern (Task 8's data-fetching logic), not `StatusCard`'s — `StatusCard` itself is a pure function of whatever `status`/`loading` it's given.

- [x] **Task 6 — Primary "Log Reading" action button (AC #11)**
  - [x] Reused the **existing** `Button variant="glass-primary"` (`web/src/components/ui/button.tsx:22-26`) unmodified — pill shape, dark/light gradient fills, `active:scale-[0.965]` press-compression-not-color-flash. Same variant Story 2.2's Log Reading sheet already uses for its Save action; this is a second, independent usage.
  - [x] Rendered via `DashboardPage` (Task 8) as the `LogReadingSheet` trigger, with a leading lucide `Plus` icon and "Log reading" label, replacing `App.tsx`'s former plain `<Button variant="outline">` placeholder.

- [x] **Task 7 — Bottom tab bar nav chrome (AC #12)**
  - [x] Create `web/src/components/dashboard/nav-chrome.tsx` — 4 items: Dashboard, Trend History, Tariff Radar, Settings. Active-state tokens (dark `active-bg: rgba(47,179,151,0.16)`, `active-fg: #6FD1B1`; light `active-bg: rgba(30,122,97,0.14)`, `active-fg: #1E7A61`) added as `--color-nav-chrome-active-bg`/`-active-foreground` in `index.css`'s `@theme inline` + `:root`/`.dark`, same pattern as the status-triad tokens. Inactive tabs use `text-muted-foreground`, never a status color.
  - [x] Only **Dashboard** and **Settings** are interactive — Dashboard is the current surface (`aria-current="page"`, no navigation), Settings calls `onSettingsClick` (wired to `App.tsx`'s existing `setView('settings')` in Task 8). **Confirmed with Ralf during dev-story activation:** Trend History and Tariff Radar tabs render but are inert (`role="button"`, `aria-disabled="true"`, no click handler) — their surfaces don't exist yet.
  - [x] lucide-react icons (`Home`, `LineChart`, `Clock`, `Settings`) — already a project dependency.

- [x] **Task 8 — Compose the real Dashboard, wire into App.tsx (AC #1, #10, #12)**
  - [x] Create `web/src/components/dashboard/dashboard-page.tsx` — presentational; receives `status`/`statusLoading` as props (App.tsx owns the fetch, mirroring how it already owns `openRegressionPrompt`). Renders `StatusCard` (populated/empty/skeleton per Task 4/5) as the **first, highest-visual-weight** element with no other Dashboard element at competing visual weight (AC #10 — this story does **not** build the Tariff Check prompt card. **Confirmed with Ralf during dev-story activation:** omitted, its FR-15 gating logic is Epic 5), one shared `LogReadingSheet` instance placed either inline in the empty state or below the populated card (never both — the two render branches are mutually exclusive), and the bottom nav chrome (Task 7).
  - [x] `App.tsx` owns `status`/`statusLoading` state + a `refreshStatus` callback (mirrors `refreshOpenRegressionPrompt` exactly) and re-fetches after the same three triggers `refreshOpenRegressionPrompt` already uses: the mount effect, the `registerOfflineSync` callback, and the combined `onReadingSaved`/`onRegressionResolved` handlers passed into `DashboardPage`. A single `registerOfflineSync` call stays (a second concurrent call would race `flushQueue()` against the first, defeating its own anti-overlap guard) — its callback now triggers both refreshes.
  - [x] Replaced `App.tsx`'s dashboard placeholder block (previously `<main>` with a bare `<h1>`/`<Button>`) with `<DashboardPage household={state.household} status={status} statusLoading={statusLoading} ... />`. Removed the now-unused `Button`/`InviteGeneratePanel`/`LogReadingSheet`/`MeterRegressionPromptDialog` imports from `App.tsx` (moved into `DashboardPage`) and the orphaned `shell.placeholder` locale key (both locales).
  - [x] `view === 'settings'` continues to render `SettingsPage` exactly as today; the nav chrome's Settings tap calls the same existing `setView('settings')`.
  - [x] Verified visually in a live browser (temporary preview harness, removed before completion) across all 3 real states + empty + skeleton, both Dark and Light — glass panel stack, status colors, primary button gradient/press, and the Log Reading sheet's open flow all render correctly; no regressions to any pre-existing `App.test.tsx` coverage (Settings navigation, invite panel, regression-prompt dialog superseding the sheet).

- [x] **Task 9 — i18n copy (AD-18)**
  - [x] Added a `dashboard` namespace to `web/src/locales/en-US/translation.json` and `de-DE/translation.json` (mirrored keys, added incrementally alongside each task above) covering: empty-state title/body, low-confidence note, the 3 headline strings, badge labels, the supporting-sentence templates (`onPace`/`underPace`/`overPace`, i18next `{ kwh }` interpolation, same pattern as `meterReading.savedConfirmation`), and nav-chrome tab labels. Verified 1:1 key parity between both locale files (99 keys each, zero drift) via a full key-set diff.

- [x] **Task 10 — Tests**
  - [x] `status-api.test.ts` — 3 cases (null-on-empty-body, parsed DTO on 2xx-with-body, `ApiError` on non-2xx).
  - [x] `status-card.test.tsx` — 12 cases: one per real state, tie-at-zero copy, locale-aware number formatting, low-confidence note present/absent, `aria-live="polite"` region, badge-text-not-raw-triad regression guard, skeleton, empty state, `emptyStateAction` slot.
  - [x] `nav-chrome.test.tsx` — 4 cases: all tabs render, active-state classes, Settings click wiring, Trend History/Tariff Radar inertness.
  - [x] `dashboard-page.test.tsx` — 5 cases: Status card first/highest-weight, primary button placement (populated vs. empty-state-inline vs. absent-during-skeleton, never duplicated), nav chrome wiring.
  - [x] `App.test.tsx` extended — 3 new integration cases: skeleton → populated flow through the real fetch, onboarding-empty on a null-body response, and a full Log-Reading-sheet save triggering a second `/api/status` fetch. All 17 pre-existing `App.test.tsx` cases still pass unmodified (Settings navigation, invite panel, regression-prompt superseding the sheet).
  - [x] `glass-card.test.tsx` — extended per Task 2 (2 new size-variant cases).
  - [x] Full validation: 94/94 frontend tests pass, `tsc -b` clean, `vite build` clean, `oxlint` clean (only 3 pre-existing, unrelated warnings). Zero backend files touched (confirmed via `git status`/`git diff --stat` on `src/`/`tests/`). Manually verified in a live browser (temporary preview harness, removed) across all 3 states + empty + skeleton, Dark and Light.

## Dev Notes

### Architecture compliance (binding, not optional)

- **AD-7**: the frontend must never invent its own "is Status stale" polling/timer logic — the backend's `GET /api/status` is already a live, synchronous, request-time computation (Story 2.4). This story's job is purely: fetch on mount, re-fetch after the specific events listed in Task 8 that could actually change Status, and render whatever comes back. No `setInterval`/polling loop.
- **AD-14**: no Smart Plug/Event data is referenced anywhere in this story's frontend code — `StatusDto` (Task 1) only ever carries `MeterReading`-derived figures (`paceToDateKwh`, `baselineToDateKwh`), matching the backend's own AD-14 guard.
- **AD-18**: number formatting (Task 4) and all new copy (Task 9) key off `Household.Locale`, never a hardcoded format/language.
- This story has **zero backend changes** — `GET /api/status` (`StatusResponse`) already exists and is stable from Story 2.4; confirmed via full search of `src/` (`grep -rn "api/status" web/src` returns nothing pre-this-story). Do not modify anything under `src/`.

### Existing code this story builds on (read before writing anything)

- `src/EnergyTracker.Api/Endpoints/StatusEndpoints.cs` — `GET /api/status` → `StatusResponse(string Status, decimal PaceToDateKwh, decimal BaselineToDateKwh, bool IsLowConfidence)`, camelCase on the wire; `Results.Ok(null)` (empty body) when undefined. `Status` string values: `"withinRange"`, `"belowBaseline"`, `"trending"` (exact casing, see `ToStatusString`).
- `web/src/lib/meter-regression-api.ts` — the exact `ApiError`/`toApiError`/empty-body-as-null pattern `status-api.ts` (Task 1) must replicate. Read this file's own comments before writing the new one; do not diverge from this precedent without reason.
- `web/src/components/ui/glass-card.tsx` — the two-layer glass panel primitive (Story 2.2b). Currently hardcodes the `'md'`/18px size; Task 2 adds a `'lg'` variant, it does not replace or fork the component.
- `web/src/components/ui/button.tsx` — `variant="glass-primary"` (lines 22-26) already implements the exact primary-action-button spec (pill, gradient, `active:scale-[0.965]`, `motion-reduce:transition-none`). Already used once, for `LogReadingSheet`'s own Save action (`log-reading-sheet.tsx:133`). Task 6 is this variant's *second* usage, on the Dashboard trigger — reuse, don't reinvent.
- `web/src/index.css` — the entire status-triad (`--status-within-range`/`-badge-bg`/`-badge-text` × 3 states, `:root` + `.dark`) and glass-surface tokens (`--surface-glass`, `--surface-panel-back`, `--radius-glass-{sm,md,lg}`) are **already wired** (Story 2.2b) verbatim from `direction-green-eco.html`. This story applies them; it does not define new status colors.
- `web/src/App.tsx` — `App.tsx:211-239` is this story's own placeholder, explicitly commented as such ("Real Dashboard is Epic 2... this placeholder is Story 1.1's existing skeleton content" / "the polished pill/gradient Dashboard button (UX-DR8) is Story 2.5's own deliverable"). `App.tsx` already owns `logSheetOpen`, `openRegressionPrompt`, `refreshOpenRegressionPrompt`, `handleLogSheetOpenChange`, and the `registerOfflineSync` wiring — Task 8 threads these into the new `DashboardPage` as props, it does not re-implement them.
- `web/src/components/meter-reading/log-reading-sheet.tsx` — reference for: i18next interpolation pattern (`t('key', { kwh, time })`), locale-aware `toLocaleTimeString(i18n.language, ...)` (Task 4's `Intl.NumberFormat` should follow the same "use the Household's actual locale field" discipline, though `household.locale` — the AD-18 field — is more correct here than `i18n.language`, which is the UI-language choice and can diverge), and the `role="status"` confirmation-message pattern (Task 4 uses explicit `aria-live="polite"` instead, per this story's own AC #7 wording, but the two are ARIA-equivalent).

### File structure / conventions to follow exactly

- New dashboard-specific components live under `web/src/components/dashboard/` (new folder) — matches the existing per-feature grouping (`household-creation`, `meter-reading`, `settings`, `tagging-scaffold`, `yearly-baseline`), per `project-context.md`'s "new feature UI gets its own folder" rule.
- `web/src/lib/status-api.ts` — flat under `lib/`, alongside `meter-regression-api.ts`/`meter-reading-sync.ts`.
- Colocated tests (`status-card.test.tsx` next to `status-card.tsx`, etc.) — not a parallel `__tests__/` tree.
- `verbatimModuleSyntax: true` — `import type { ... }` for the `StatusDto` type import wherever it's type-only.
- i18n: both `en-US` and `de-DE` `translation.json` updated in the same change (Task 9) — never one locale alone.
- oxlint, not ESLint — `react/rules-of-hooks`/`no-unused-vars` are build-breaking.

### Testing standards summary

Vitest + `@testing-library/react`, `jsdom` environment, globals on. `vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(...))))` for API mocking (`jsonResponse` helper already exists as a local pattern in `log-reading-sheet.test.tsx`, replicate it in the new test files rather than importing across test files). i18next is globally initialized in `web/src/test/setup.ts` (`import '@/i18n'`) — components under test can call real translated strings directly, no i18n test-provider wrapper needed. No backend tests required (zero backend changes).

### Previous story intelligence (Story 2.4)

- Story 2.4 built `GET /api/status` specifically so this story would have something to consume — confirmed in 2.4's own Dev Agent Record: *"This endpoint is not explicitly named in this story's own AC list... it's included because Story 2.5 (Dashboard Status Display) has no backend ACs of its own and must consume something. Confirmed with Ralf during dev-story activation: build it now in 2.4 rather than deferring to 2.5."* This is exactly why this story's own AC list (above) has zero backend/API acceptance criteria — they were all satisfied one story early.
- 2.4's own review confirmed (as a deliberate, approved exception) that `StatusResponse` returns raw `PaceToDateKwh`/`BaselineToDateKwh` rather than a pre-composed headline/supporting sentence, specifically *because* "Story 2.5's frontend needs the raw figures to render its own sentence" — Task 4's copy-composition logic is not a workaround, it's the intended design.
- 2.4 has zero frontend changes (confirmed in its own Dev Notes: "this story has zero UI surface"). This story is the first to touch `web/src/` since Story 2.2b (design-system foundation) and Story 2.3 (regression prompt dialog) — the glass-panel/status-color token layer it needs is already fully wired, not something to build from scratch.

### Project Structure Notes

- Alignment: follows the exact `web/src/components/{feature}` + colocated-test convention every prior frontend story (2.2, 2.2b, 2.3) already established. No deviation.
- Detected scope questions (not blockers, flagged for confirmation at dev-story activation — see Tasks 4, 5, 7, 8 above for full reasoning on each): (1) exact headline/supporting-sentence copy algorithm (Task 4) — the mockup's specific event-naming isn't achievable pre-Epic-6; (2) whether the Tariff Check quiet card belongs in this story's own Dashboard composition or is fully Epic 5's job (Task 8) — this story assumes the latter; (3) whether Trend History/Tariff Radar nav tabs should be inert placeholders vs. some other treatment (Task 7) — this story assumes inert/non-interactive.

### References

- [Source: `_bmad-artifacts/planning/epics/epic-2-meter-reading-pattern-detective-status-core.md#Story 2.5`] — story statement + AC source (verbatim).
- [Source: `_bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md#FR-6, #FR-7`] — FR consequences, exact "Status" resolution/onboarding wording.
- [Source: `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md`] — Information Architecture ("Dashboard"), Component Patterns ("Status card", "Primary action button", "Nav chrome"), State Patterns, Interaction Primitives (motion contract, exact timings), Accessibility Floor, Voice and Tone.
- [Source: `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/components.md#Status card, #Primary action button, #Nav chrome`] — token names and exact composition rules.
- [Source: `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/typography.md`] — `{typography.status-headline}`/`{typography.status-figure}` (tabular-nums) roles.
- [Source: `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-dashboard.html`] — the composed real Dashboard screen (topbar, Status card, primary button, nav chrome, both themes, both populated/empty states) — the authoritative key-screen reference for this story, supersedes the isolated `direction-green-eco.html` component study where they'd otherwise conflict (they don't, this file's own header comment confirms it reuses `direction-green-eco.html`'s tokens verbatim).
- [Source: `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/direction-green-eco.html`] — exact hex/rgba values for all 3 status states, both themes, both the `'lg'` (primary) and `'md'`-equivalent (`.inset`) card sizes.
- [Source: `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/motion-demo.html`] — exact entrance/specular-sweep keyframe values and the reduced-motion-as-default CSS structure (Task 3).
- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-7, #AD-14, #AD-18`] — exact AD rule text.
- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/consistency-conventions.md`] — "Dashboard Status endpoint" API-surface-shape convention (explains why `StatusResponse` carries raw figures, not pre-composed copy).
- [Source: `_bmad-artifacts/implementation/2-4-gap-tolerant-rolling-baseline-status-computation.md`] — previous story intelligence; `GetCurrentStatus`/`StatusEndpoints` implementation this story consumes read-only.
- [Source: `web/src/App.tsx`, `web/src/components/ui/glass-card.tsx`, `web/src/components/ui/button.tsx`, `web/src/lib/meter-regression-api.ts`, `web/src/components/meter-reading/log-reading-sheet.tsx`, `web/src/index.css`] — existing code this story extends.

## Review Findings

- [x] [Review][Patch] Relocate InviteGeneratePanel from the Dashboard to Settings — resolves the AC #10 visual-weight conflict (a second glass card + primary-styled button competing with the Status card). Move the component render from `DashboardPage` to `SettingsPage`, and update `SettingsPage`'s own "not yet built" comment plus any story docs (1-8's story file, this story's Dev Notes) that describe its current placement so they stay consistent with the implementation. Decision: relocate to Settings now (user-selected).
- [x] [Review][Patch] Add `NavChrome` to `SettingsPage`, with `active="settings"`, so the persistent tab bar (UX-DR9/AC #12) survives navigation into Settings instead of disappearing. Decision: add now (user-selected).
- [x] [Review][Patch] Build an empty-state-shaped skeleton variant for `StatusCard`'s cold-load state, alongside the existing populated-shaped one, so first-time households (the most common skeleton case) don't reflow into a structurally different layout on resolution (AC #8). Decision: build empty-state skeleton (user-selected).
- [x] [Review][Patch] Entrance/specular-sweep animation replays on every Settings→Dashboard round trip, not just cold load/recompute [web/src/components/dashboard/status-card.tsx:97-104]
- [x] [Review][Patch] aria-live region is torn down and recreated alongside the entrance-animation remount, risking status-change announcements not reaching screen readers [web/src/components/dashboard/status-card.tsx:100-129]
- [x] [Review][Patch] Rounded pace/baseline difference can contradict the status badge at the (-0.5, 0) kWh boundary — sentence says "Right on pace" while badge/dot say "Below baseline" [web/src/components/dashboard/status-card.tsx:84-93]
- [x] [Review][Patch] `DashboardHousehold.currency` is required and passed down but never consumed [web/src/components/dashboard/dashboard-page.tsx:12-16]
- [x] [Review][Defer] Concurrent `refreshStatus()` calls (e.g. offline-sync flush racing the mount-effect fetch) aren't sequenced — a slower, earlier-triggered response can resolve after a newer one and silently overwrite Status with stale data [web/src/App.tsx:49-58,148-160] — deferred, pre-existing (identical unsequenced-fetch pattern already exists in `refreshOpenRegressionPrompt`, not introduced by this diff)

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5), via the dev-story workflow.

### Debug Log References

None — no blocking failures required a separate debug log. `npx shadcn add skeleton` scaffolded the new `Skeleton` primitive into the wrong path (a stray `web/@/components/ui/skeleton.tsx` instead of `web/src/components/ui/skeleton.tsx`, an alias-resolution quirk in this environment) — worked around by writing the file directly to the correct path and removing the stray `web/@` directory.

### Completion Notes List

- **Confirmed with Ralf during dev-story activation (three scope decisions, all "Recommended" option accepted):**
  1. Status card supporting-sentence copy names only the pace/baseline delta number ("42 kWh under pace."), never a fabricated triggering event — Event/Wattage-Plausibility correlation (FR-16/17) is Epic 6, not built yet.
  2. The Tariff Check "nothing due right now" quiet card seen in `mockups/key-dashboard.html`'s full composed screen is **not** built in this story — its FR-15 due-date gating logic is Epic 5.
  3. Nav chrome's Trend History and Tariff Radar tabs render but are inert (`role="button"`, `aria-disabled="true"`, no click handler) — no surface exists yet for either.
- **Design decision (Task 8):** `App.tsx` owns `status`/`statusLoading` state and a `refreshStatus` callback, mirroring its pre-existing `openRegressionPrompt`/`refreshOpenRegressionPrompt` pattern exactly, rather than having `DashboardPage` fetch internally. This keeps a single `registerOfflineSync()` registration (a second concurrent registration would race `flushQueue()` against the first and defeat its own anti-overlap guard) while still re-fetching Status after every event that could change it.
- **Design decision (Task 4):** Supporting-sentence sign/number are derived from the *rounded* pace-baseline difference, not the raw decimal, so the displayed direction word ("under"/"over") never disagrees with the displayed number at a near-zero boundary.
- **GlassCard extended, not forked (Task 2):** added a `size: 'md' | 'lg'` prop (default `'md'`, fully backward compatible — all 4 existing consumers are unaffected) rather than creating a second glass-panel component for the Status card's larger hero radius.
- Removed the now-orphaned `shell.placeholder` i18n key (both locales) and its unused `Button`/`InviteGeneratePanel`/`LogReadingSheet`/`MeterRegressionPromptDialog` imports from `App.tsx` — all now live in/under `DashboardPage`.
- Manually verified in a live browser via a temporary preview harness (`preview.html` + `preview-main.tsx`, created and fully removed before completion — never committed): all 3 real Status states, the onboarding-empty state, and the loading skeleton, in both Dark and Light, plus the Log Reading sheet's open flow (slide-up + backdrop blur). Confirms the glass panel stack, status-triad colors, and primary-button gradient/press-state render as specified.
- 94/94 frontend tests pass (15 test files — 6 new, 4 modified), `tsc -b` clean, `vite build` clean, `oxlint` clean (only 3 pre-existing warnings, unrelated to this story). Zero backend files touched — confirmed via `git status`/`git diff --stat` against `src/`/`tests/`.

### File List

**New files:**
- `web/src/lib/status-api.ts`
- `web/src/lib/status-api.test.ts`
- `web/src/components/ui/skeleton.tsx`
- `web/src/components/dashboard/status-card.tsx`
- `web/src/components/dashboard/status-card.test.tsx`
- `web/src/components/dashboard/nav-chrome.tsx`
- `web/src/components/dashboard/nav-chrome.test.tsx`
- `web/src/components/dashboard/dashboard-page.tsx`
- `web/src/components/dashboard/dashboard-page.test.tsx`

**Modified files:**
- `web/src/App.tsx` — owns `status`/`statusLoading` state + `refreshStatus`; replaced the dashboard placeholder block with `<DashboardPage>`; removed now-unused imports.
- `web/src/App.test.tsx` — added 3 Status-card wiring integration cases.
- `web/src/components/ui/glass-card.tsx` — added `size: 'md' | 'lg'` prop.
- `web/src/components/ui/glass-card.test.tsx` — added 2 size-variant regression cases.
- `web/src/index.css` — nav-chrome active-tab tokens, Status card entrance/specular-sweep `--animate-*` keyframes, specular-overlay gradient tokens (Dark + Light).
- `web/src/locales/en-US/translation.json` — `dashboard` namespace; removed orphaned `shell.placeholder`.
- `web/src/locales/de-DE/translation.json` — `dashboard` namespace (mirrored); removed orphaned `shell.placeholder`.
- `_bmad-artifacts/implementation/sprint-status.yaml` — status tracking.
