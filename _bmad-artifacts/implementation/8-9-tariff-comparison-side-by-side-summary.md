---
baseline_commit: db0c640
---

# Story 8.9: Tariff Comparison Side-by-Side Summary

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member comparing tariffs,
I want the current and candidate tariff summaries shown side by side,
so that I can compare them at a glance instead of scrolling between two stacked cards.

## Acceptance Criteria

1. **Given** a computed Compare-Tariff result at an available width ≥660px, **when** rendered, **then** the "Your current Tariff" and "Candidate Tariff" summary panels render side by side in two columns instead of stacked (UX-DR31).
2. **Given** the "Is it worth switching?" verdict/signal card, **when** rendered at ≥660px, **then** it stays full width below both summary panels — it does not become a third column alongside them.
3. **Given** the same screen at <660px, **when** rendered, **then** the existing stacked layout (Story 8.4) is unchanged.

## Tasks / Subtasks

- [x] **Task 1: Pair the two summary panels (AC #1, #2, #3)** — `web/src/components/tariff/tariff-comparison-form.tsx` (the only production file that changes)
  - [x] In the `{result && (...)}` block (~lines 205-290) the two `QuietCard`s are direct children of `div.flex.flex-col.gap-4`, followed by the verdict `GlassCard`. Wrap **only the two `QuietCard`s** in ONE new `div` with `flex flex-col gap-4 wide:flex-row` (below 660px this is byte-for-byte the same stacked column: same `gap-4`, same `flex-col`). Give each `QuietCard` `wide:flex-1 wide:basis-0 wide:min-w-0` (mockup: `.compare-summary-row { display:flex; gap:14px } > .quiet-card { flex:1 1 0 }`). Keep the outer `gap-4` (16px) for the row gap — no new spacing literal; note the mockup's 14px delta in Completion Notes.
  - [x] The verdict `GlassCard data-testid="tariff-compare-verdict-card"` stays a **sibling of the new wrapper**, not a child — it keeps full width of the outer `flex-col`, which is AC #2. Do not add any `wide:` class to it.
  - [x] **Class-only, CSS-only change.** No `useWideBreakpoint`, no conditional rendering, no new state/props/i18n keys. The DOM is identical at every width (same two panels, same order: current first, candidate second — DOM/tab/screen-reader order must not change). This mirrors Story 8.4's own `wide:flex-row` / `wide:flex-1` field-pairing already in the same file (lines 137-187) and Story 8.8's section pairing.
  - [x] Height mismatch is expected and fine: the candidate panel has a third row (Switching Bonus) only when `candidateSwitchingBonus > 0`; the current panel always has two rows. Default `align-items: stretch` makes both cards equal height in the row (mockup shows the same). Do **not** add `wide:items-start` unless it looks wrong live — if you do, note it.
  - [x] Check `Card`'s base classes (`web/src/components/ui/card.tsx`) for anything that would fight `flex-1`/`basis-0`/`min-w-0` (e.g. an explicit `w-full`); `QuietCard` merges `className` via `cn`/tailwind-merge so the `wide:` utilities apply. Do not modify `QuietCard` or `Card`.
  - [x] Update the stale JSX comment at ~line 207 ("stacked glass panels") — the panels are quiet-tier and now stacked below 660px / paired at ≥660px. Comment density: match the surrounding style (a short, why-oriented block comment).
  - [x] The `result === null` empty-state `GlassCard` branch and all form/validation/error code are untouched.

- [x] **Task 2: Unit tests (AC #1-#3)** — `web/src/components/tariff/tariff-comparison-form.test.tsx` (327 lines; every existing test must pass **unchanged**)
  - [x] Reuse the file's existing `mockupWorkedExample` result fixture and the submit helper used by the tests at ~lines 135, 156 and 279 (read them first — copy their idiom, do not invent a new render helper).
  - [x] Add a test (near the existing "quiet card tier" test at ~line 279): after a computed result, both `[data-slot="quiet-card"]` elements share **one** parent element whose class contains `wide:flex-row` and `flex-col`; each carries `wide:flex-1`; the two are the first and second children in DOM order (current heading `Your current Tariff` first, `Candidate Tariff` second).
  - [x] Add the negative half (AC #2): the verdict card (`data-testid="tariff-compare-verdict-card"`) is **not** inside that wrapper, is a following sibling of it, and its class does not contain `wide:flex-1`/`wide:flex-row`.
  - [x] jsdom evaluates no media queries and has no layout — class-string / structure assertions only (same limitation as Stories 8.3–8.8; the existing e2e comment at `app-shell.spec.ts:304-307` says the same). AC #3's "unchanged below 660px" is proven by e2e (Task 3), not jsdom.

- [x] **Task 3: e2e — extend the existing Tariff Radar test, don't add a new one (AC #1-#3)** — `web/e2e/app-shell.spec.ts`, test "the Tariff Radar content column, paired form fields, and card tiers behave correctly at the 660px breakpoint" (~lines 308-495)
  - [x] The test already mocks `**/api/tariffs/compare` (~line 368) and submits the form **once, at 1000px**, near the end (~lines 461-495), asserting `quietCards` count 2 and backdrop-filter tiers. Add geometry assertions there, in that same post-submit block, using `page.locator('[data-slot="quiet-card"]').evaluateAll(... getBoundingClientRect())` and `getByTestId('tariff-compare-verdict-card').boundingBox()`:
    - **At 1000px (column exactly 900px):** the two quiet cards have the same `y` (`toBeCloseTo(…, 0)`), the second's `x` is greater than the first's, and their widths are equal within 1px (equal `flex-1 basis-0`). Assert **exact** derived values, not loose bounds (Story 8.6/8.7/8.8 review lesson): both cards sit inside the outer `GlassCard` (900px column − 2×`--spacing-card-padding` 24px = 852px content) with a 16px gap ⇒ each ≈ 418px — **derive from `getComputedStyle`/measured container width in the test rather than trusting these numbers** (padding token could differ), then `toBeCloseTo`. Their combined width + gap equals the wrapper's width.
    - **Verdict card spans full width below (AC #2):** its `y` ≥ the quiet cards' `y + height`, its `width` equals the wrapper/outer content width (≈ 852px, i.e. ≥ the two cards' combined width + gap), and its `x` equals the first quiet card's `x`.
  - [x] **Below 660px (AC #3):** the submit happens only once, at 1000px, so to prove the stacked layout without a second submission, **resize the same page after the result is rendered**: `setViewportSize({ width: 659 })` → both quiet cards share the same `x`, second `y` > first `y + height`; then `setViewportSize({ width: 660 })` → they flip to side by side (same `y`, different `x`) — exact-boundary check, same 659/660 precedent as the Dashboard/Trend History/Tariff Radar assertions in this test. Resizing an already-loaded page is fine for Playwright here (pure CSS, no state loss); the `resize_window` unreliability from Stories 8.6/8.7 applies to the *Chrome-extension* live check, not Playwright.
  - [x] Keep every existing assertion in the test (fields pairing, 900px column, entry-grid tiles, backdrop-filter tiers) untouched; the `quietCards` `toHaveCount(2)` still holds.
  - [x] Optional if it stays cheap: also assert at ~800px (column ≈ 768px) that the panels are still side by side and neither overflows the column (each `x + width` ≤ column right edge) — the mid-width case is where a long translated `SummaryRow` label + value could squeeze (see Dev Notes).

- [x] **Task 4: Full regression + live Chrome verification (mandatory — do not defer)**
  - [x] `npm --prefix web run test`, `tsc -b`, `oxlint` clean. No backend change — no `.NET` run required. No locale change expected (no new UI string; `tariff.compare.summary.currentHeading` / `candidateHeading` already exist).
  - [x] `npx playwright test` (move `certs/vite-dev-cert.pem`/`.key` aside first if present and restore immediately; check for an orphaned `vite preview` on port 4173 — see the Story 8.6/8.7/8.8 Debug Logs; use a throwaway config on another port if needed and delete it afterwards).
  - [x] **This is browser-dependent responsive-layout behavior — same gate as Stories 8.1–8.8: it cannot move review→done without a live Claude-in-Chrome verification actually performed in this session.** If Chrome isn't connected, raise it immediately and pause for the user to connect it; do not defer or tick the box on unit/e2e proof alone (`project-context.md` Process gates).
  - [x] Established workaround: `resize_window` on an already-navigated tab is unreliable (Stories 8.6/8.7) — open a **new** tab already sized to the target width, then navigate; an in-tab live resize across 660px needs a manual resize by the user — ask for it rather than skipping it.
  - [x] Verify live on the real household (Auth0 login, https://localhost:5173): Tariff Radar at ~1000–1400px with a current Tariff present, run Compare with a candidate **with** a switching bonus and once **without** (blank field — candidate panel then has 2 rows like the current panel; heights should match): current/candidate side by side, verdict card full width below both, the two signal rows still readable; Dark **and** Light against `mockups/key-tariff-radar.html`; German locale (long labels "Grundgebühr"/"Wechselbonus" + values) at ~700px does not overflow or clip inside a ~340px panel; at 659px the panels are stacked exactly as Story 8.4 shipped. **Do not corrupt real data** — Compare is scratch/never persisted (FR-11), so it is safe; do not add or edit real Tariff entries.

- [x] **Task 5: Verify against every AC**
  - [x] Walk AC #1-#3 individually in Completion Notes and state what proves each (code reference, unit test, e2e assertion, or live check) — same AC-by-AC accounting as Stories 8.1–8.8.

## Dev Notes

- **UI-only story: no backend change, no new domain capability, no new FR/NFR, no new architecture decision.** UX-DR31 (added 2026-09-28, Epic 8) is the sole driver. One production file changes: `tariff-comparison-form.tsx`. `web/` only; no locale file, no `index.css` token, no shared `ui/` component.

- **Chosen mechanism: CSS-only `wide:` variants, no JS breakpoint hook** — same decision and rationale as Story 8.8 (its Dev Notes, "Chosen mechanism"). The DOM is identical at every width; only the container's flex direction changes. `useWideBreakpoint` (Story 8.7, for table↔tile DOM swaps) must **not** be imported here. It also keeps all existing tariff-comparison tests running unchanged in jsdom.

- **Column width reality (epic text is stale):** the epic ACs and older comments say "660px column"; Story 8.6 raised the column cap to **900px** (`wide:max-w-[900px]` on `div[data-slot="tariff-radar-content"]`, `tariff-radar-page.tsx:67`). The 660px value is only the *breakpoint* that triggers the wide layout. Do not reintroduce 660px anywhere; the two panels split the outer `GlassCard`'s content width (≈ 852px at a 900px column ⇒ ≈ 418px each; at a viewport just over 660px the column ≈ 628px ⇒ card content ≈ 580px ⇒ ≈ 282px each).

- **Read-before-modify current state (Step 3 analysis, file marked UPDATE):**
  - `tariff-comparison-form.tsx` (294 lines) — `TariffComparisonForm({ currency, locale })` renders one outer `GlassCard flex flex-col gap-4`: `h2` + description → `<form>` (Story 8.4's paired `wide:flex-row` field rows with a `hidden wide:block wide:flex-1` spacer beside Switching Bonus, lines 137-187; error line; `glass-primary` submit `Button`) → either the `result === null` onboarding empty-state `GlassCard`, or (`result` truthy) `div.flex.flex-col.gap-4` containing **[QuietCard current] [QuietCard candidate] [GlassCard verdict, `data-testid="tariff-compare-verdict-card"`]**. `SummaryRow` (`flex items-baseline justify-between gap-4`) renders label/value rows; the candidate card shows the Switching Bonus row only when `candidateSwitchingBonus > 0`. `SignalRow` renders the verdict rows. **This story changes only how the two `QuietCard`s are grouped/laid out** — no handler, state, prop, fetch, text, or `SignalRow`/`SummaryRow` change. **Must preserve:** DOM order (current → candidate → verdict), both card headings (`h3`), the `data-slot="quiet-card"` / `data-testid="tariff-compare-verdict-card"` hooks the existing unit + e2e tests query, the quiet-vs-glass tier split (UX-DR24, Story 8.4 AC #3 — quiet cards `backdrop-filter: none`, verdict card blurred), the `result === null` empty state, the stale-result clearing on a failed resubmit, and the Switching-Bonus-omitted-when-0 rule.
  - `tariff-radar-page.tsx` (104 lines) — **unchanged**. It gates `TariffComparisonForm` on `currentTariffCurrency` (a *current* Tariff must exist), so the comparison form — and therefore the result panels — only ever render for a household that already has a current Tariff.
  - `QuietCard` (`components/ui/quiet-card.tsx`) — **unchanged**. `Card` + `rounded-glass-md border bg-surface-quiet border-surface-quiet-border p-[var(--spacing-card-padding)] gap-[var(--spacing-card-gap)] ring-0`; it merges a passed `className` through `cn`, so `wide:flex-1 wide:basis-0 wide:min-w-0` land cleanly. Reuse it as-is (Story 8.4 established this exact tier for these panels).

- **Why the wrapper, not `grid`:** the mockup uses `display:flex; gap:14px` with `flex: 1 1 0` children (`key-tariff-radar.html` `.compare-summary-row` / `.quiet-card`), and Story 8.4/8.8 use the identical `wide:flex-row` + `wide:flex-1` recipe. Follow it. `wide:basis-0` + `wide:flex-1` guarantees equal widths regardless of content length (a plain `flex-1` gives `basis-0%` in Tailwind v4 already, but `basis-0` is what Story 8.8 used — match it); `wide:min-w-0` prevents a long value string from forcing one panel wider and overflowing the column.

- **Long-string risk (i18n):** `SummaryRow` is `justify-between gap-4` with no truncation. At ~660–700px each panel content is ≈ 240–260px wide (282px card − card padding). "Preis pro kWh" + "0,3150 EUR/kWh" / "Candidate monthly base fee" (en) + "14.90 USD" should fit; if a translation wraps, `items-baseline justify-between` degrades to wrapping text, not overflow — acceptable, and `min-w-0` keeps it inside the card. Do **not** truncate with ellipsis (values are facts, per Story 8.8's summary-row precedent). Story 8.3/8.4 already deferred "no coverage for longer localized strings" repo-wide (`deferred-work.md`); verifying German live (Task 4) is this story's part.

- **Must not regress (implicit requirements):** Story 8.4 stacked layout <660px (AC #3 — the new wrapper's `flex-col gap-4` must reproduce today's spacing exactly: today's outer `gap-4` between the three siblings is now split between the wrapper's internal `gap-4` and the outer `gap-4` between wrapper and verdict — both 16px, so pixel-identical), Story 8.4's quiet/glass tier proof (`quietCards` count 2 and backdrop-filter checks in the existing e2e), Story 8.6's 900px column, Story 8.7's Tariff History entry-grid on the same page (untouched), Story 5.2's verdict content (two `SignalRow`s, footnotes, low-confidence note).

- **Scope test (what is *not* in this story):** the Tariff Check card, Add-Tariff form, Tariff History list (Story 8.7), the compare form's field pairing (Story 8.4), signal-row layout inside the verdict card, and any wording/locale change. The verdict card must **not** be paired, placed in a third column, or restyled (UX-DR31: it is "the shared conclusion both feed into").

- **Testing standard:** colocated Vitest + Testing Library (jsdom) — class-string/structure assertions only; real geometry only in Playwright and live Chrome. Same division of proof as Stories 8.2–8.8. The Story 8.5 deferred item about dual-render narrow/wide assertions disambiguated only by DOM-order indexing does not apply here (no dual render — one DOM tree).

- **Git intelligence:** last commits are Story 8.8 (`26891d8`, PR #80: pair-wrapper `flex-col wide:flex-row` + `wide:flex-1 wide:basis-0 wide:min-w-0` recipe and the class-string unit-test/exact-geometry e2e pattern this story mirrors), Story 8.7 (`2a3589b`: `EntryGrid`, `useWideBreakpoint`, `stubWideViewport`, the Tariff History tiles on this same page), Story 8.6 (`b7f971b`, 900px column), Story 8.4 (the current-vs-candidate panels and their quiet tier, in the same file). The doc commit `91158e3` already updated `key-tariff-radar.html`, `layout-spacing.md` ("Comparison-panel pairing", line 19) and EXPERIENCE/DESIGN for 8.6–8.9 — no design-doc work left for the dev. Commit style: Conventional-Commits prefix + story/UX-DR reference (e.g. `Story 8.9: Tariff Comparison Side-by-Side Summary (UX-DR31)`); branch is already `feature/8-9-tariff-comparison`. No dependency changes in Epic 8 — none needed (React 19.2, Tailwind v4, Vitest 4.1, Playwright 1.62).

- **Process gates from `project-context.md`:** no fix to shipped code without a linked spec/story (this file is that link); browser-dependent story ⇒ live Chrome verification required before review→done (Task 4). This story does not touch OIDC/claims/auth flows (the live check still needs the normal Auth0 login to reach the page). AD-1..AD-19: none apply (frontend layout only; the verdict values are server-computed and untouched — AD-5 Bonus-Decay Normalization is not reimplemented or altered).

### Project Structure Notes

```text
energy-tracker-v2/
  web/src/components/tariff/
    tariff-comparison-form.tsx           # modified — pair wrapper around the two QuietCards (Task 1)
    tariff-comparison-form.test.tsx      # modified — pairing/structure tests (Task 2)
  web/e2e/app-shell.spec.ts              # modified — Tariff Radar test extended (Task 3)
  web/src/index.css                      # UNCHANGED — no new tokens
```

No changes expected to `tariff-radar-page.tsx`, `quiet-card.tsx`, `glass-card.tsx`, `unit-input.tsx`, any other tariff component, any locale file, `use-wide-breakpoint.ts`, or any backend project.

### References

- [Source: _bmad-artifacts/planning/epics/epic-8-desktop-tablet-layout-correction.md#Story 8.9] — story statement and literal ACs (lines 194-212).
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md#UX-DR31] — side-by-side definition (line 134); UX-DR24 (line 136) for the quiet/glass tier that must be preserved; UX-DR23 (line 135) for the already-paired form fields; UX-DR19 for the 900px column.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/layout-spacing.md, line 19] — "Comparison-panel pairing" rationale; [mockups/key-tariff-radar.html, lines 124-129 (`.compare-summary-row`/`.quiet-card`/`.kv-row` CSS), 312-315 (Dark markup), 404-407 (Light markup)] — canonical rendered spec; [mockups/wide-settings-tariff-2026-09-28.html] — original decision record.
- [Source: web/src/components/tariff/tariff-comparison-form.tsx, lines 131-292] — render block; lines 137-187 Story 8.4's `wide:flex-row`/`wide:flex-1` field pairing (the recipe to mirror); lines 205-290 the result block.
- [Source: web/src/components/tariff/tariff-comparison-form.test.tsx, lines 135-178, 256-296] — summary-panel, field-pairing and quiet-tier tests whose idioms/fixtures to reuse.
- [Source: web/src/components/tariff/tariff-radar-page.tsx, lines 60-104] — 900px content column and `currentTariffCurrency` gating.
- [Source: web/e2e/app-shell.spec.ts, lines 304-495] — the Tariff Radar test to extend (compare mock ~368, post-submit block ~461-495, 659/660 boundary precedent ~421-436).
- [Source: _bmad-artifacts/implementation/8-8-settings-rooms-power-points-grid-with-expand-section-pairing.md] — previous story: pair-wrapper recipe, exact-geometry e2e lessons, Playwright port/cert Debug Log, live-verification gate, `resize_window` workaround.
- [Source: _bmad-artifacts/implementation/deferred-work.md — code review of story-8.4] — no long-localized-string coverage for the paired compare fields; the existing e2e is a single long test (known, inherited structure — extend it, don't refactor it here).
- [Source: _bmad-artifacts/project-context.md#Critical Don't-Miss Rules] — process gates; [#TypeScript/Frontend] — `verbatimModuleSyntax`, `noUnusedLocals`, `@/` alias, oxlint.

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5.5 (claude-sonnet-5-5)

### Debug Log References

- Playwright `webServer` (`npm run preview`) timed out until `web/` was built and `certs/vite-dev-cert.{pem,key}` were moved aside (restored immediately after). No orphaned preview process on :4173.
- First unit-test draft failed: `GlassCard` wraps its card in `data-slot="glass-card-stack"`, so the verdict card's *sibling* of the pair wrapper is that stack, not the `data-testid` element. Test now asserts against the stack.

### Completion Notes List

- `tariff-comparison-form.tsx`: wrapped only the two `QuietCard`s in `div.flex.flex-col.gap-4.wide:flex-row`; each card got `wide:min-w-0 wide:flex-1 wide:basis-0`. Verdict card left as a full-width sibling with no `wide:` classes. Class-only change: same DOM/order at every width, no new state/props/i18n keys. Stale "stacked glass panels" comment updated. `Card`/`QuietCard` untouched (no `w-full` in `Card` to fight flex sizing).
- Row gap uses the outer `gap-4` (16px); the mockup's 14px is a 2px delta, deliberately not introduced as a new literal.
- No `wide:items-start` needed: default stretch gives equal heights (verified live, with and without bonus).
- Unit test added (`tariff-comparison-form.test.tsx`): shared wrapper with `flex-col`/`wide:flex-row`, DOM order current→candidate, `wide:flex-1 basis-0 min-w-0` on each, verdict stack is a following sibling outside the wrapper without `wide:flex-1/flex-row`. Existing tests unchanged. Note: existing headings are "Your current tariff"/"Candidate tariff" (lowercase t).
- e2e (`app-shell.spec.ts`): extended the existing Tariff Radar test post-submit at 1000px (same y, equal widths, x gap = 16px, equal heights, verdict card x/width equal to the pair span and below it), 800px (side by side, inside column), then 659px (stacked, 16px vertical gap) and 660px (paired) on the already-rendered result.
- Validation: vitest 452/452 pass; `tsc -b` clean; `oxlint` shows only pre-existing warnings; Playwright 6/6 pass.
- Live Chrome verification (Task 4): dark theme at 891px on the real household — cards 396.5px each, 16px gap, same y/height; verdict card 809px full width below; candidate without bonus → both panels 126px tall. Light theme, German locale (~700px overflow check) and the 659px stacked layout were checked manually by Ralf and approved.
- AC accounting: **AC #1** — code (wrapper + `wide:` card classes), unit test, e2e 1000/800/660px geometry, live check. **AC #2** — verdict card is a sibling of the wrapper: unit test, e2e (below pair, full pair width), live check. **AC #3** — `flex-col gap-4` below 660px identical to prior stacked spacing: e2e 659px exact boundary, live check by Ralf.

### File List

- web/src/components/tariff/tariff-comparison-form.tsx (modified)
- web/src/components/tariff/tariff-comparison-form.test.tsx (modified)
- web/e2e/app-shell.spec.ts (modified)
- _bmad-artifacts/implementation/sprint-status.yaml (modified)
- _bmad-artifacts/implementation/8-9-tariff-comparison-side-by-side-summary.md (new/modified)

### Change Log

- 2026-09-29: Story 8.9 created — ready-for-dev.
- 2026-09-29: Story 8.9 implemented — summary panels paired side by side at ≥660px, verdict card full width; unit + e2e + live verification. Status → review.
