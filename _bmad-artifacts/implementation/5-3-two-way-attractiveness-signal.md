---
baseline_commit: 7da0e10
---

# Story 5.3: Two-Way Attractiveness Signal

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want to see the switch-worthiness signal shown both with and without the switching bonus,
so that I'm not misled by an inflated first-period offer.

## Scope Reality Check — Read This First

This is the **third story in Epic 5**, building directly on Story 5.2's `CompareTariff` use case / `POST /api/tariffs/compare` (done). 5.2's own header comment names this story explicitly and pre-verified the numbers this story needs against `mockups/key-tariff-radar.html` — see Dev Notes for the exact derivation.

- **This story owns:** FR-13's two-way signal (bonus-included row + bonus-normalized row, shown together, never toggled), the breakeven tie-break rule, the dedicated 4th `attractiveness-*` color pair (UX-DR7, UX-DR10), and the "current-vs-candidate tariff summary" stacked glass panels (UX-DR7) that don't exist in the codebase yet — Story 5.1/5.2 never built them (5.2's own Task 3 note: "there is no dedicated 'Your current tariff' summary card built yet").
- **Do NOT touch `Domain.Calculations.BonusDecayNormalizer` itself.** The bonus-*included* figure this story adds is the plain, undecayed calculation (full bonus subtracted once, in year one) — it deliberately does **not** call `BonusDecayNormalizer` (that call already exists, unchanged, for the bonus-*normalized* figure only). Don't be tempted to route the bonus-included math through the normalizer "for consistency" — the whole point of showing both rows is that they use two genuinely different formulas.
- **Do NOT build Story 5.4's Tariff Check Reminder here.** The `tariff-check-card` banner visible in the mockup's frames is out of scope.
- **No new persisted entity, no new candidate input fields.** This story is a **pure display/computation extension** of Story 5.2's existing `CompareTariff` request/response shape — same three candidate inputs (base fee, price/kWh, optional switching bonus), same no-write, scratch/exploratory semantics (FR-11, unchanged).

## Acceptance Criteria

(Sourced from `_bmad-artifacts/planning/epics/epic-5-tariff-savings-radar.md`, Story 5.3 block, lines 74-97.)

1. **Given** a computed comparison, **when** displayed, **then** a green/red signal is shown twice — once with the Switching Bonus included, once normalized out — both shown together, never toggled (FR-13).
2. **Given** an exact breakeven (zero projected savings) on the normalized signal, **when** evaluated, **then** it resolves to red/not-worth-switching — ties favor staying put (FR-13).
3. **Given** the Tariff comparison card, **when** rendered, **then** both signal rows use the dedicated 4th attractiveness-signal color pair (never reused from the Status triad, brand-accent, or destructive/error red), each with its own AA-verified supporting-text/figure-text tokens and a plain-language verdict word ("Worth switching" / "Not worth it") — never color alone (UX-DR7, UX-DR10).
4. **Given** the current-vs-candidate tariff summary, **when** rendered, **then** each is a stacked glass panel with label/value rows using tabular-nums figures (UX-DR7).

**Not explicitly written above but required for the system to work end-to-end (project-context.md's "the dev agent owns this" rule) — see Dev Notes' "Edge cases beyond the stated ACs":**
- AC #2's literal text only names "the normalized signal," but `EXPERIENCE.md`'s own Component Patterns table states the tie-resolution rule generically ("An exact breakeven resolves to 'not worth switching' per FR-13's tie-resolution rule," not scoped to one row) — resolved below to apply identically to **both** rows, not just the normalized one.
- A `0` (blank/omitted) switching bonus — the bonus-included and bonus-normalized figures become numerically identical in this case (nothing to normalize away). Both rows will legitimately show the same amount and the same verdict; that's correct behavior, not a bug, but the bonus-included row's explanatory copy must not falsely claim a bonus was applied.
- "Colors always follow the verdict, not fixed row order" (`DESIGN/components.md`'s own explicit instruction) — the bonus-included row is not hardcoded green and the bonus-normalized row is not hardcoded red. Each row's color/badge is driven independently by its own savings figure's sign.

## Tasks / Subtasks

- [ ] **Task 1: Extend `CompareTariff` with the bonus-included figure and both verdict flags** (AC: #1, #2)
  - [ ] `src/EnergyTracker.Application/CompareTariff.cs`: after the existing `bonusNormalizedAnnualSavings` computation, add:
    ```csharp
    // Naive, undecayed comparison (Story 5.3, FR-13) — deliberately does NOT call
    // BonusDecayNormalizer. The whole point of the two-way signal is that this figure and the
    // bonus-normalized one above use two genuinely different formulas: this one applies the full
    // switching bonus once, in year one, exactly as a household would naively read the offer.
    var candidateAnnualCostBonusIncluded = candidateAnnualCostNoBonus - candidateSwitchingBonus;
    var bonusIncludedAnnualSavings = currentAnnualCost - candidateAnnualCostBonusIncluded;

    // FR-13 tie-break: a breakeven (== 0, not just < 0) resolves to "not worth it" — ties favor
    // staying put. Strictly-greater-than, applied identically to both rows (see Dev Notes on why
    // this isn't scoped to the normalized row only, despite the epic AC's literal wording).
    var isBonusIncludedWorthSwitching = bonusIncludedAnnualSavings > 0m;
    var isBonusNormalizedWorthSwitching = bonusNormalizedAnnualSavings > 0m;
    ```
  - [ ] Add `CandidateAnnualCostBonusIncluded`, `BonusIncludedAnnualSavings`, `IsBonusIncludedWorthSwitching`, `IsBonusNormalizedWorthSwitching` to the `TariffComparisonResult` record (append after the existing `BonusNormalizedAnnualSavings` field — all call sites use named arguments, so field order is safe to extend) and pass them through the final `return new TariffComparisonResult(...)`.
  - [ ] Verdict computation stays in the Application layer, inline, next to the rest of `CompareTariff`'s arithmetic — **do not** add a new `Domain.Calculations` helper for this. Unlike `BonusDecayNormalizer` (genuinely shared with Pattern Detective, AD-5), nothing else in the product computes an "is this a positive number" verdict, so there's no reuse case for a shared module. This does mirror the *design instinct* behind `GetCurrentStatus` computing its `Status` enum server-side rather than leaving threshold logic to the frontend (see Dev Notes) — but doesn't rise to an AD-level shared-module rule.

- [ ] **Task 2: Extend the API response** (AC: #1, #2)
  - [ ] `src/EnergyTracker.Api/Endpoints/TariffEndpoints.cs`: add the same four fields (`CandidateAnnualCostBonusIncluded`, `BonusIncludedAnnualSavings`, `IsBonusIncludedWorthSwitching`, `IsBonusNormalizedWorthSwitching`) to `TariffComparisonResponse`, and pass them through in `ToComparisonResponse`. No request-shape change — `CompareTariffRequest` is untouched, this is a response-only extension.
  - [ ] `web/src/lib/tariff-api.ts`: add the same four fields (camelCase: `candidateAnnualCostBonusIncluded`, `bonusIncludedAnnualSavings`, `isBonusIncludedWorthSwitching`, `isBonusNormalizedWorthSwitching`) to the `TariffComparisonDto` interface — this is the type Task 5's frontend rendering reads from; forgetting this update makes the new fields `any`/untyped at the call site with no compile-time safety.

- [ ] **Task 3: Add the dedicated attractiveness-signal color tokens** (AC: #3)
  - [ ] `web/src/index.css`: add to the `@theme inline` block (mirroring the existing `--color-status-*` mappings at lines 51-59):
    ```css
    --color-attractiveness-worth-it: var(--attractiveness-worth-it);
    --color-attractiveness-worth-it-bg: var(--attractiveness-worth-it-bg);
    --color-attractiveness-worth-it-badge-text: var(--attractiveness-worth-it-badge-text);
    --color-attractiveness-not-worth-it: var(--attractiveness-not-worth-it);
    --color-attractiveness-not-worth-it-bg: var(--attractiveness-not-worth-it-bg);
    --color-attractiveness-not-worth-it-badge-text: var(--attractiveness-not-worth-it-badge-text);
    --color-attractiveness-not-worth-it-text: var(--attractiveness-not-worth-it-text);
    --color-attractiveness-signal-supporting-text: var(--attractiveness-signal-supporting-text);
    ```
  - [ ] Add the raw variables to **both** the `:root` block and the `.dark` block. `mockups/key-tariff-radar.html` (lines 100-118) is **dark-mode only** — its hex values are verbatim-correct for `.dark`:
    ```css
    /* .dark block */
    --attractiveness-worth-it: #6FDB93;
    --attractiveness-worth-it-bg: rgba(111, 219, 147, 0.16);
    --attractiveness-worth-it-badge-text: #06120D;
    --attractiveness-not-worth-it: #E2685A;
    --attractiveness-not-worth-it-bg: rgba(226, 104, 90, 0.16);
    --attractiveness-not-worth-it-badge-text: #2B0E09;
    --attractiveness-not-worth-it-text: #EB958C;
    --attractiveness-signal-supporting-text: rgba(234, 245, 238, 0.7);
    ```
    **The `:root` (light-mode) values are not in the mockup — it's dark-only, same gap `colors.md` already flags for `--destructive`'s light value ("[ASSUMPTION] ... not yet confirmed in a rendered light frame").** Derive them using the same approach the Status triad already used for its own light/dark split (`--status-below-baseline: #2F9E52` light vs `#4FCA72` dark — light mode uses a more saturated/darker hue since it sits on a lighter surface): a darker, more-saturated mint (`worth-it`) and a darker, more-saturated clay-red (`not-worth-it`) than the dark-mode pair, then **actually verify** (a contrast-ratio tool, not a guess) that `--attractiveness-signal-supporting-text` and `--attractiveness-not-worth-it-text` clear 4.5:1 against the composited `-bg` tint over the light-mode glass card surface (`--surface-glass: rgba(255,255,255,0.72)` over `--background: #F3F8ED`) before finalizing. The badge fill/text pair (`--attractiveness-*-badge-text`, near-black on an opaque saturated pill) is self-contained AA-wise and can plausibly reuse the same values in both themes — verify, don't assume, but don't spend the same derivation effort there that the translucent-background tokens need.
  - [ ] Do **not** reuse `--status-below-baseline`/`--status-trending`/`--destructive` for any of this — `colors.md`'s own reasoning (lines 56-77 in the mockup's header comment) explicitly rejects both, and this rejection is a hard constraint, not a stylistic preference.

- [ ] **Task 4: Frontend — current-vs-candidate tariff summary panels** (AC: #4)
  - [ ] In `web/src/components/tariff/tariff-comparison-form.tsx`, once a non-null result exists, render two stacked `GlassCard` panels above the signal card — mirrors `mockups/key-tariff-radar.html`'s `.card` structure (lines 276-293) but using this codebase's own established label/value row pattern (`status-detail-dialog.tsx` lines 113-135: `flex items-baseline justify-between gap-4` wrapper, `text-muted-foreground text-sm` label, `text-sm font-semibold tabular-nums` value) — **do not** invent a new row-layout convention when one is already established and does the same job.
  - [ ] Current tariff panel: base fee (`result.currentMonthlyBaseFee`), price/kWh (`result.currentPricePerKwh`), both formatted in `result.currency`. Candidate panel: candidate base fee, candidate price/kWh, candidate switching bonus (omit the switching-bonus row entirely when it's `0` — nothing was entered, showing "€0.00" as a row implies a deliberate zero-bonus candidate rather than "not entered").
  - [ ] No tariff *names* anywhere — unlike the mockup's "Stadtwerke Flex 12" / "GrünStrom Wechselbonus 12" headings, `Tariff` (`src/EnergyTracker.Domain/Tariff.cs`) has no name/label field and this story does not add one. Use generic headings (e.g. "Your current tariff" / "Candidate tariff") instead of a per-entry name.
  - [ ] Reuse the same `moneyFormat`/`kwhFormat` `Intl.NumberFormat` instances the component already constructs (lines 66-70 of the current file) — don't create parallel formatter instances for the summary panels.

- [ ] **Task 5: Frontend — the two-way signal card** (AC: #1, #2, #3)
  - [ ] Replace the current single-line result block (`web/src/components/tariff/tariff-comparison-form.tsx` lines 141-155 — the `result.bonusNormalizedAnnualSavings >= 0 ? savingsPositive : savingsNegative` block) with a signal card rendering **both** rows together, always, never toggled. This is a hard replacement, not an addition — the old single-figure sentence is exactly what Story 5.3 was scoped to supersede (5.2's own Scope Reality Check: "Story 5.3's Two-Way Attractiveness Signal... [is] explicitly excluded" from 5.2).
  - [ ] Per row (bonus-included, bonus-normalized), drive color/badge from that row's **own** verdict flag (`result.isBonusIncludedWorthSwitching` / `result.isBonusNormalizedWorthSwitching`) independently — never assume a fixed worth/not-worth row order (`DESIGN/components.md`'s explicit instruction: "colors always follow the verdict, not fixed row order"). Structurally mirror `status-card.tsx`'s `DOT_CLASS`/`BADGE_CLASS` lookup-table pattern (lines 48-60): a `Record<boolean, string>`-shaped (or simple ternary) class map keyed on the verdict boolean, not inline conditional class strings scattered through JSX.
  - [ ] Badge: a `Badge` component (`@/components/ui/badge`), `variant="outline"` override exactly like `status-card.tsx`'s own badge usage, filled with the raw `bg-attractiveness-worth-it`/`bg-attractiveness-not-worth-it` token (not the `-bg` tint — the badge itself is an opaque solid pill per the mockup, `text-attractiveness-*-badge-text` for its label color) and the plain-language verdict word (`t('tariff.compare.signal.worthBadge')` / `notWorthBadge`) — **never color alone**, exactly as `colors.md`/`components.md` require.
  - [ ] Row background uses the translucent `-bg` token (`bg-attractiveness-worth-it-bg` / `bg-attractiveness-not-worth-it-bg`); the amount figure uses the raw token for the worth-it case (`text-attractiveness-worth-it`, already clears AA per the mockup's own comment) and the dedicated `text-attractiveness-not-worth-it-text` token for the not-worth-it case (the raw `--attractiveness-not-worth-it` measured only 3.50:1 at that text weight per the mockup's own verification comment — do not use the raw token there); the supporting detail sentence under each badge uses `text-attractiveness-signal-supporting-text` in both rows.
  - [ ] Detail-sentence copy: templated, not the mockup's literal vendor-name sentences (no tariff names exist in this data model — see Task 4). Bonus-included row: state the switching-bonus amount and that it's credited once (skip this sentence entirely when the switching bonus is `0` — there's nothing to explain). Bonus-normalized row: state that once the bonus is gone, the candidate's ongoing rate costs more/less than the current tariff at the household's actual pace — reuse the existing `paceFootnote` figure/copy discipline (`result.annualPaceKwh`) already established in this file rather than restating pace differently per row.
  - [ ] `IsLowConfidence` footnote (currently rendered once, below the single savings line) stays — render it once, below both signal rows, not duplicated per row.
  - [ ] Fixed-decimal formatting (NFR6): every new amount figure goes through the existing `moneyFormat` (`minimumFractionDigits: 2, maximumFractionDigits: 2`) — guard against Story 5.1's own review-found bug (a whole-euro value silently dropping trailing zeros) recurring on any of these *new* figures, exactly as the existing `paceFootnote`/old savings line already do correctly.

- [ ] **Task 6: i18n — both locales** (AC: #1, #3, #4)
  - [ ] Add `tariff.compare.summary.*` (panel headings, base-fee/price/switching-bonus field labels — reuse `tariff.form.monthlyBaseFeeLabel`/`pricePerKwhLabel`/`tariff.compare.candidateSwitchingBonusLabel` where the exact same concept already has a key rather than adding near-duplicates) to `web/src/locales/en-US/translation.json` and `de-DE/translation.json`.
  - [ ] Add `tariff.compare.signal.*`: `heading` ("Is it worth switching?"), `worthBadge` ("Worth switching"), `notWorthBadge` ("Not worth it"), bonus-included row label/detail/positive/negative sentence keys, bonus-normalized row label/detail/positive/negative sentence keys.
  - [ ] Remove (don't leave orphaned) the now-superseded `tariff.compare.savingsPositive`/`savingsNegative` keys once the old single-line block (Task 5) is gone, in both locale files — an unused key left behind silently drifts out of sync over time (same discipline this project already applies elsewhere; check for other callers first with a repo-wide grep before deleting, there should be none outside the file this task replaces).

- [ ] **Task 7: Tests** (AC: all)
  - [ ] `tests/EnergyTracker.Application.Tests/CompareTariffTests.cs`: extend the existing `A_valid_comparison_at_exactly_one_year_elapsed_returns_the_mockups_own_worked_numbers` test with the new fields — `CandidateAnnualCostBonusIncluded.ShouldBe(836.80m)`, `BonusIncludedAnnualSavings.ShouldBe(337.20m)`, `IsBonusIncludedWorthSwitching.ShouldBeTrue()`, `IsBonusNormalizedWorthSwitching.ShouldBeFalse()` (see Dev Notes for the derivation — these are the mockup's own "Save about €337 this year" / "About €13/yr more, not less" numbers, already cross-checked once in Story 5.2's Dev Notes). Add new cases: an exact-breakeven scenario for each row independently (construct current/candidate figures that make `BonusIncludedAnnualSavings == 0` with `BonusNormalizedAnnualSavings != 0`, and vice versa) asserting the zero-savings row's verdict flag is `false` (AC #2, and its extension to both rows per this story's resolved-ambiguity note above); a `0` switching-bonus case asserting `CandidateAnnualCostBonusIncluded == CandidateAnnualCostBonusNormalized` (nothing to normalize away when there's no bonus).
  - [ ] `tests/EnergyTracker.Api.Tests/TariffEndpointsTests.cs`: extend `POST_tariffs_compare_returns_200_with_a_computed_result_once_a_current_Tariff_and_pace_exist` (lines 322-337) with the same new-field assertions against `TariffComparisonResponse`.
  - [ ] Frontend (`web/src/components/tariff/tariff-comparison-form.test.tsx`): this file's existing tests assert the now-removed single-line sentences (e.g. `'This candidate would cost about 12.80 EUR/yr more, bonus-normalized.'` at lines 40, 103) — these must be **rewritten**, not left in place, since that exact text no longer renders after Task 5. New/updated cases: both signal rows render together for the mockup's worked numbers (worth-it badge + amount for bonus-included, not-worth-it badge + amount for bonus-normalized — assert both are present simultaneously, proving AC #1's "never toggled"); a breakeven case renders "Not worth it" for the zero-savings row; the current-vs-candidate summary panels render the right label/value pairs with 2-decimal money formatting; the switching-bonus summary row is omitted when the bonus is `0`; the low-confidence footnote still renders once (not duplicated).
  - [ ] Full backend suite green, full frontend suite green, `dotnet build`/`tsc -b`/`oxlint`/`vite build` all clean before moving to review — this project's established verification bar.

## Dev Notes

- **The math was already verified once, in Story 5.2's own Dev Notes (line 89), against `mockups/key-tariff-radar.html`'s worked example (lines 276-314) — this story's job is to actually build the figure 5.2 deliberately left uncomputed:**
  - Current: `€12.50/mo` base fee, `€0.3200`/kWh → `currentAnnualCost = 1174.00` (unchanged from 5.2).
  - Candidate: `€14.90/mo` base fee, `€0.3150`/kWh, `€350` one-time switching bonus, pace `3,200 kWh/yr` → `candidateAnnualCostNoBonus = 1186.80` (unchanged from 5.2).
  - **Bonus-normalized** (5.2's existing output, unchanged by this story): `1186.80` → `bonusNormalizedAnnualSavings = 1174.00 - 1186.80 = -12.80` → mockup's "About €13/yr more, not less" → **not worth it** (negative).
  - **Bonus-included** (this story's new output): `candidateAnnualCostBonusIncluded = 1186.80 - 350 = 836.80` → `bonusIncludedAnnualSavings = 1174.00 - 836.80 = 337.20` → mockup's "Save about €337 this year" → **worth it** (positive).
  - This is exactly the mockup's dramatization: the same candidate reads as a win with the bonus and a loss without it — the two verdict flags for this worked example must come out `true`/`false` respectively, not the same value twice. If a test run produces the same verdict for both rows on this exact input, the bonus-included formula is wrong (most likely: it accidentally called `BonusDecayNormalizer` instead of the plain subtraction, decaying away the very bonus it's supposed to show in full).

- **Why the verdict boolean is computed server-side, not derived from the raw savings number in the frontend:** this codebase already has a strong, consistent precedent for this — `GetCurrentStatus` computes a discrete `Status` enum (`WithinRange`/`BelowBaseline`/`Trending`) server-side in `PatternDetectiveCalculator`, and `status-card.tsx` never re-derives that enum from raw pace/baseline numbers; it only maps the already-decided enum to a badge/color via static lookup tables (`DOT_CLASS`/`BADGE_CLASS`/`HEADLINE_KEY`, lines 35-60). Putting `IsBonusIncludedWorthSwitching`/`IsBonusNormalizedWorthSwitching` on `TariffComparisonResult` (rather than having the frontend check `bonusIncludedAnnualSavings > 0`) follows that same pattern: the tie-break rule (AC #2, `== 0` favors "not worth it") is a business rule, testable once at the Application layer, not duplicated/re-derived in JSX.

- **Edge cases beyond the stated ACs (project-context.md's "the dev agent owns this" rule):**
  - **AC #2's scope, resolved:** the epic's literal AC text says "on the normalized signal," but `EXPERIENCE.md`'s Component Patterns table (line 69) states the rule generically for "the Tariff comparison card" as a whole, not scoped to one row — and there is no principled reason the bonus-included row's tie-break should behave differently (a `€0.00` bonus-included saving is exactly as much "not a win" as a `€0.00` bonus-normalized one). Resolved: apply `> 0m` (strict) identically to both rows. This is a design resolution made during story creation, not an open question left for the dev agent to guess at.
  - **Zero switching bonus:** when `candidateSwitchingBonus == 0` (the field is optional, Story 5.2's own default), `candidateAnnualCostBonusIncluded == candidateAnnualCostNoBonus == candidateAnnualCostBonusNormalized` — both rows show numerically identical figures and verdicts. This is correct, not a bug: with no bonus, there's nothing to "normalize away." The bonus-included row's detail sentence must not claim a bonus was applied in this case (Task 5) — check the switching-bonus amount before rendering that sentence, don't render it unconditionally.
  - **Row order vs. verdict color:** `DESIGN/components.md` explicitly warns the two rows are not fixed-green-then-red — a candidate that's a loss even *with* the bonus included (a genuinely bad offer) would show **both** rows red. A candidate whose bonus makes an otherwise-losing rate look temporarily attractive (this story's worked example) shows one green, one red. A candidate that's a win even after the bonus fully decays away (the good-faith honest case) would show **both** rows green. All three combinations are valid and must render correctly — don't build to only the one worked example.
  - **Currency, still no FX:** identical to 5.2 — the candidate is implicitly in the current Tariff's own `Currency` (not `Household.Currency`), and both new figures are echoed/formatted in that same currency, no conversion.

- **Closest existing analogs to build from:**
  - `status-card.tsx` (lines 35-60, 106-146) — the enum-driven color/badge lookup-table pattern this story's two independent verdict flags should mirror, and the `Badge variant="outline"` override shape for a solid-fill, plain-language-labeled pill.
  - `status-detail-dialog.tsx` (lines 111-135) — the exact label/value row shape (`flex items-baseline justify-between gap-4`, `text-muted-foreground text-sm` label, `text-sm font-semibold tabular-nums` value) for AC #4's summary panels — this is the established convention, not `mockups/key-tariff-radar.html`'s raw `.field-row` CSS, which was written for a static mockup and doesn't map onto this codebase's existing Tailwind utility conventions.
  - `mockups/key-tariff-radar.html` (lines 8-118 header comment) — the full color-token reasoning (why not reuse the Status triad, why not reuse destructive-red) already worked out and citable verbatim; lines 198-222 for the exact CSS shape (`.signal-row`, `.signal-badge`, `.signal-amount`, `.signal-detail`) to translate into Tailwind utility classes plus the new `--color-attractiveness-*` tokens from Task 3.
  - `DESIGN/colors.md` (line 21) and `DESIGN/components.md` (line 29) — the authoritative, already-written component spec for exactly this card (token names, which figure uses which token, why the "colors follow the verdict not row order" rule exists).

### Previous Story Intelligence (Story 5.2)

- 5.2 shipped `CompareTariff.cs` (`GetCurrentStatus` + `ITariffRepository` deps only, per AD-7's single computation seam — do not add a third dependency here either), `POST /api/tariffs/compare` (no-write, null-body-when-undefined), and `tariff-comparison-form.tsx` (the file this story edits, not replaces). 5.2's own header comment (lines 21-24 of its story file) explicitly named this story and its scope boundary — nothing to renegotiate there.
- 5.2's own Dev Notes (lines 88-90) already computed both this story's target numbers by hand as a design cross-check before 5.2 was even built — treat those numbers as verified ground truth, not something to re-derive from first principles.
- 5.2's code review found and fixed a real bug worth remembering: the candidate-field currency label must come from the *current Tariff's own* `Currency`, not `Household.Currency` — already fixed in the shipped `tariff-comparison-form.tsx`/`tariff-radar-page.tsx` this story builds on top of; no action needed here, just don't regress it while editing the same file.
- 5.2's `IsLowConfidence` handling (added during its own code review) is the precedent for how this story's new fields should be surfaced: propagate on the backend result/response records, render as a footnote on the frontend. Same shape, reused.

### Git Intelligence

- Recent commits (`ede256e` "feat: story 5.2 candidate tariff comparison & bonus-decay normalized savings (#53)", `90fd066` "doc: story 5.2 planning", `1b1960c` "fix: cap Infrastructure.Tests parallelism to reduce Testcontainers CI flakiness (#52)") confirm the project's shipped cadence: one planning-doc commit, one squash-merged implementation PR. Commit messages use `feat:`/`fix:`/`doc:` Conventional-Commits prefixes.
- The current branch (`bugfix/smart-plug-import-flaky-completion-note-test`) is an unrelated in-flight fix, not part of this story's lineage — this story should branch fresh from `main` (or wherever `dev-story` is run) rather than building on top of that branch.

### Project Structure Notes

- Backend: all changes are additive extensions to existing files (`CompareTariff.cs`, `TariffEndpoints.cs`) — no new files, no new port methods, no migration (no persisted state changes).
- Frontend: all changes are within the existing `web/src/components/tariff/tariff-comparison-form.tsx` (plus its colocated test file) and `web/src/index.css` — no new component files are required, though splitting the signal-row rendering into a small local sub-component within the same file is a reasonable readability choice if the file grows unwieldy (not mandated either way).
- No new npm packages — `GlassCard`/`Badge`/`UnitInput` already exist and cover this story's UI needs.

### References

- [Source: _bmad-artifacts/planning/epics/epic-5-tariff-savings-radar.md#Story-5.3] — Story 5.3 definition and ACs (lines 74-97); Epic 5 header (FR-10–FR-15, NFR6/8/9/10, AD-4/5/7/11, UX-DR5/7/10/12) — only FR-13, UX-DR7, UX-DR10 are new to this story (FR-11/12/14/AD-5/NFR6 already satisfied by 5.1/5.2).
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md] — FR-13 (line 27, full testable text incl. the tie-break rule); UX-DR7 (line 117, "Build the Tariff comparison card component" — explicitly binds this story's exact scope).
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/colors.md] — line 21, the authoritative "Attractiveness signal — Mint/Clay" token definitions and rejection reasoning (why not the Status triad, why not destructive-red).
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/components.md] — line 29, "Tariff comparison card" component spec: two summary panels + signal card, token-to-element mapping, the "colors follow the verdict not row order" rule.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md] — line 69, "Tariff comparison card" Component Pattern row (generic breakeven-tie-break phrasing, resolved above); line 85, the pre-pace empty state (already built by 5.2, unaffected by this story).
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-tariff-radar.html] — full header comment (lines 8-118, color-token resolution reasoning) and rendered markup/CSS (lines 260-317, Frame 1) this story's UI is built from.
- [Source: src/EnergyTracker.Application/CompareTariff.cs] — the exact use case this story extends; existing `bonusNormalizedAnnualSavings` sign convention (positive = switching saves money) this story's new `bonusIncludedAnnualSavings` must match.
- [Source: src/EnergyTracker.Domain/Tariff.cs] — confirms no name/label field exists on `Tariff`, informing the "no tariff names in copy" decision (Task 4).
- [Source: src/EnergyTracker.Domain/Calculations/BonusDecayNormalizer.cs], [src/EnergyTracker.Domain/Calculations/PatternDetectiveCalculator.cs] — AD-5's shared module, explicitly NOT touched by this story (only the bonus-normalized figure uses it, unchanged).
- [Source: src/EnergyTracker.Api/Endpoints/TariffEndpoints.cs] — `TariffComparisonResponse`/`ToComparisonResponse`, the response shape this story extends.
- [Source: web/src/components/dashboard/status-card.tsx] — enum-driven badge/color lookup-table pattern (lines 35-60) this story's two-verdict rendering should mirror.
- [Source: web/src/components/dashboard/status-detail-dialog.tsx] — the established label/value row convention (lines 113-135) for AC #4's summary panels.
- [Source: web/src/components/tariff/tariff-comparison-form.tsx], [web/src/components/tariff/tariff-comparison-form.test.tsx] — the file(s) this story edits; current single-line result block (lines 141-155) being replaced, current test assertions (lines 40, 103) that must be rewritten.
- [Source: web/src/index.css] — existing `--color-status-*` token pattern (lines 51-59, 149-157, 208-216) this story's new `--color-attractiveness-*` tokens must follow structurally.
- [Source: web/src/lib/tariff-api.ts] — `TariffComparisonDto`, the frontend type this story extends with the four new fields.
- [Source: _bmad-artifacts/implementation/5-2-candidate-tariff-comparison-bonus-decay-normalized-savings.md] — previous story's full context; Dev Notes lines 88-90 (the pre-computed bonus-included numbers this story implements); Review Findings (the currency-label bug already fixed, the `IsLowConfidence` precedent).
- [Source: _bmad-artifacts/project-context.md] — project-wide conventions (naming, testing, i18n discipline, "read files being modified" rule this story leaned on heavily given how much of `tariff-comparison-form.tsx` it rewrites).

## Dev Agent Record

### Agent Model Used

### Debug Log References

### Completion Notes List

### File List

## Change Log

- 2026-09-15: Story created (create-story). Scoped strictly to FR-13 (two-way signal, breakeven tie-break) + UX-DR7/UX-DR10 (dedicated attractiveness color pair, current-vs-candidate summary panels) — builds directly on Story 5.2's `CompareTariff`/`tariff-comparison-form.tsx`, which deliberately left this story's bonus-included figure and signal-card UI unbuilt. Resolved one real scope ambiguity during drafting: the epic AC's breakeven tie-break text names only "the normalized signal," but `EXPERIENCE.md`'s own generic phrasing and the absence of any principled reason for asymmetric behavior resolve it to apply identically to both rows. Verdict computation (`IsBonusIncludedWorthSwitching`/`IsBonusNormalizedWorthSwitching`) is placed server-side on `TariffComparisonResult`, following the same design instinct already established by `GetCurrentStatus`'s server-computed `Status` enum, rather than left for the frontend to derive from raw sign-of-savings. Flagged for dev-story attention (not left as an open question — this is an implementation detail requiring actual contrast verification, not product judgment): the light-mode `--attractiveness-*` token values have no mockup precedent (the mockup is dark-only, same gap already flagged for `--destructive` in `colors.md`) and must be independently derived and contrast-checked against the light-mode glass card surface, not assumed identical to the dark-mode hex values.
