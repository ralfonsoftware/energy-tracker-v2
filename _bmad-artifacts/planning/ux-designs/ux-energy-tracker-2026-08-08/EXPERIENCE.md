---
title: "Experience: Energy Tracker v2"
name: Energy Tracker v2
status: final
created: 2026-08-08
updated: 2026-08-16
sources:
  - _bmad-artifacts/planning/briefs/brief-energy-tracker-2026-08-08/brief.md
  - _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/index.md
---

# Energy Tracker v2 — Experience Spine

> Single-surface responsive web, mobile-first, no native app. shadcn/ui on Tailwind CSS. Paired with `DESIGN.md` (visual identity). Both spines win on conflict with any mock, wireframe, or import.

## Foundation

Responsive web app, **mobile-first**. There is no native app (explicit PRD non-goal) — the product is a single responsive web surface that scales from phone (the primary form factor for meter-side reading entry, UJ-1) up to browser/tablet width (the secondary form factor for calm-evening trend browsing, UJ-3). Built on **shadcn/ui on Tailwind CSS**; standard shadcn components are used unmodified wherever this document or `DESIGN.md` doesn't call out a delta. `DESIGN.md` is the visual-identity reference for everything described here — this document owns *how it works*, `DESIGN.md` owns *how it looks*.

Every route sits behind authentication (OIDC, swappable via config) except the OIDC callback itself. All data is scoped to a single Household; there is no cross-household or admin surface. Dark and Light are equal citizens — neither is a fallback of the other.

**Mobile nav pattern:** a bottom tab bar carries the 3–4 most-used surfaces — Dashboard, Trend History, Tariff Radar, Settings — using `{components.nav-chrome}`'s active-state treatment; less-frequent surfaces (Onboarding/Household Setup, Smart Plug Import, Log Event) are reached through Settings or contextual entry points rather than claiming their own tab, keeping the persistent chrome as quiet as the rest of the product.

## Information Architecture

| Surface | Reached from | Purpose |
|---|---|---|
| Dashboard | App open (authenticated) | The Status (FR-6/FR-7) as the primary, glanceable element; Tariff Check prompt shown only when due; entry point to Log Reading. |
| Log Reading | "+ Log reading" trigger on Dashboard | Sheet/modal **over** the Dashboard — not its own route. Meter kWh entry, today pre-selected/editable (FR-1). |
| Trend History | Dashboard → "Trend history" | Pace-over-time trend (FR-8) plus per-plug measured data by Room → Power Point → Device (FR-9). Realizes UJ-3. |
| Tariff Radar | Dashboard's Tariff Check prompt, or Settings | Tariff configuration (FR-10), candidate comparison entry (FR-11), bonus-normalized savings signal (FR-12/13). |
| Log Event | Dashboard or Trend History (contextual entry point) | Fast text/tap-first Event logging (FR-16), Wattage Plausibility correlation shown once computed (FR-17). |
| Settings | Global nav | Yearly Baseline (FR-2), trending threshold, Tariff Check cadence, AI backend choice for Wattage Plausibility (local vs. cloud), Room/Power Point/Device management (FR-28), data export/import (FR-22/23), member invitation (FR-27). |
| Onboarding / Household Setup | First authenticated visit to a fresh deployment | Household creation (FR-26), Yearly Baseline presets (FR-2), first-run empty state before any Status exists (FR-7). |
| Smart Plug Import | Settings or Dashboard contextual entry | File upload (Eve Home `.xlsx`, Meross `.csv`), async processing with completion notification (FR-4), gap handling (FR-24). |

Log Reading is deliberately **not** a route — it's a sheet layered over whatever the user was looking at, so the "under a minute, standing at the meter" path never pays for a full navigation. Modal/sheet stacking is one level deep: a sheet never opens on top of another sheet (e.g., the FR-25 regression-classification prompt supersedes an open Log Reading sheet rather than stacking on it — see State Patterns).

→ Composition reference: [mockups/direction-green-eco.html](mockups/direction-green-eco.html) covers the Status card in isolation across all three states; [mockups/motion-demo.html](mockups/motion-demo.html) covers motion; [mockups/density-trend-history.html](mockups/density-trend-history.html) covers Trend History; [mockups/key-tariff-radar.html](mockups/key-tariff-radar.html) covers Tariff Radar (FR-10–FR-15, both entry paths); [mockups/key-smart-plug-import.html](mockups/key-smart-plug-import.html) covers Smart Plug Import (FR-4, FR-24) — upload, a mid-range gap, the Story 3.2 create/map prompt, a first-import gap left unfilled, and an entirely-gaps file flagged for review (all 5 of FR-24's cases now rendered). [mockups/key-dashboard.html](mockups/key-dashboard.html) covers the Dashboard as a full composed screen — topbar, Status card, Tariff Check card, primary action button, bottom nav chrome, and the FR-7 first-run empty state, in both Dark and Light. [mockups/key-log-reading-flow.html](mockups/key-log-reading-flow.html) covers the Log Reading sheet and the Meter Regression prompt superseding it. [mockups/key-settings.html](mockups/key-settings.html) covers Settings (Yearly Baseline + Room/Power Point/Device management at rest), in both Dark and Light. [mockups/key-room-management.html](mockups/key-room-management.html) covers the four actions that tree implies — Add, Move to… (Story 2.6), Rename, Delete (AD-10 soft-delete, deliberately not styled as destructive). [mockups/key-onboarding.html](mockups/key-onboarding.html) covers Onboarding/Household Setup (household creation + first-run Yearly Baseline). [mockups/key-household-invite.html](mockups/key-household-invite.html) covers Household Member Invitation. Still spine-only, no rendered mock: Log Event (Epic 6, out of this pass's scope).

## Voice and Tone

Microcopy is plain-language, specific, and human — it names the actual number and the actual thing that happened, never a generic congratulation or a vague nudge. Brand voice and aesthetic posture live in `DESIGN.md`.

| Do | Don't |
|---|---|
| "Quiet week, 240kWh under pace, Saturday's gaming session already absorbed." | "Great job! You're crushing your energy goals! 🎉" |
| "Worth a look. Approaching the pace that led to last year's surprise invoice." | "Warning: high consumption detected." |
| "Tariff check — nothing due right now." | Fabricating a recommendation when no comparison is actually due |
| "Roughly matches the bump seen." / "Roughly matches the dip seen." (FR-17 Wattage Plausibility — a rough correlation, never false precision) | "Confirmed: your gaming session caused this spike." |
| "No reading came in between Apr 3–17, so that stretch is shown as a gap rather than guessed at." | Silently interpolating a line through a gap |
| "Log your first reading to get started." (FR-7 empty state) | Showing a blank dashboard or a default Status value |
| Numbers and named events, stated plainly. | Exclamation marks, streak language, gamified congratulation copy — this is a household utility, not a habit app. |

## Component Patterns

Behavioral. Visual specs live in `DESIGN.md.Components`.

| Component | Use | Behavioral rules |
|---|---|---|
| Status card | Dashboard | Exactly **3** visual states — within range / below baseline / trending (FR-6) — never a 4th "unknown" visual treatment. Recomputes on every new Meter Reading or completed Smart Plug import, never on a fixed schedule alone. Legible as a single glanceable state with no scrolling and no chart required (FR-7). The only exception to "3 states" is the explicit onboarding empty state (FR-7 — see State Patterns), which is a distinct empty-state layout, not a 4th Status color. |
| Log Reading sheet | Dashboard trigger | Sheet over Dashboard, never its own route. Today's date/time pre-selected, editable (backfill case). Single kWh field, one confirmation tap. Save-to-confirmation targets under a minute for the default path. Accepts a second same-day reading as a distinct entry (different timestamp) — never rejects as duplicate, never silently overwrites. Queues locally and syncs when connectivity returns (offline capture NFR) — meter locations are frequently signal-weak. |
| Tariff Check prompt card | Dashboard | Rendered only when a check is actually due (gated 3 months before contract exit, then recurring per FR-15). When not due: a neutral/empty quiet-weight line ("nothing due right now"), never a fabricated recommendation. Tapping opens Tariff Radar. Deliberately lower visual weight than the Status card at all times. |
| Trend chart | Trend History | Moderate density is the only shipped default (confirmed this session — no user-facing Minimal/Dense toggle). Gaps in Meter Reading history render as a visible line break, never interpolated (FR-8) — interpolation-with-flagging is specific to Smart Plug import gaps (FR-24), a different data path. → [mockups/density-trend-history.html](mockups/density-trend-history.html) |
| Room → Power Point → Device tree | Trend History | Expandable list (collapsed by default), Moderate density. Explicitly labeled as measured context, not a reconciled attribution of the Main Meter total — figures here never claim to sum to the household total (FR-9). Retagging a Power Point/Device after import leaves previously-imported data attributed to the tag active at import time. |
| Primary action button (Log Reading trigger) | Dashboard | One instance per Dashboard. Press state is a compression + shadow pull-in, never a color flash (see Interaction Primitives). |
| Event entry | Log Event | Comparable effort to Log Reading — fast text/tap-first, not a form. Optional Room/Power Point/Device tag. Backfillable to a past date/time. A deleted tagged item leaves the Event's historical tag as inert text, not a broken reference (FR-16). |
| Tariff comparison card | Tariff Radar | Current-vs-candidate tariff summary plus the FR-13 two-way attractiveness signal (bonus-included / bonus-normalized rows), always shown together, never toggled. An exact breakeven resolves to "not worth switching" per FR-13's tie-resolution rule. Comparison entries are scratch/exploratory (FR-11) — they never alter the real Tariff until an explicit switch action. → [mockups/key-tariff-radar.html](mockups/key-tariff-radar.html) |
| Smart Plug import trigger | Settings / Smart Plug Import | File picker (Eve Home `.xlsx`, Meross `.csv`) → async upload with a completion notification; UI never blocks on parsing (FR-4). An import tagged to a not-yet-existing Power Point prompts creation/mapping rather than failing silently. → [mockups/key-smart-plug-import.html](mockups/key-smart-plug-import.html) |
| Meter regression prompt | Triggered inline, wherever a Reading is entered | Modal, one level deep — supersedes rather than stacks behind an open Log Reading sheet. Forces a *reset* vs. *rollover* classification (FR-25); the flagged Reading is excluded from baseline computation until resolved. See the dedicated micro-flow below. |
| Nav chrome | Global (bottom tab bar) | Active tab uses `{components.nav-chrome.active-bg}` / `-dark` and `{components.nav-chrome.active-foreground}` / `-dark` — brand-accent-tinted, never a status color. Reflects the current top-level surface only (Dashboard, Trend History, Tariff Radar, Settings); no badge/counter treatment on a tab. |
| Wattage Plausibility correlation display | Log Event (after correlation computes) | Rendered as a rough/approximate match, never false precision (FR-17) — plain-language phrasing only ("Roughly matches the bump seen"), no confidence percentage, no claimed causation. Computed once and shown inline with the Event, not as a separate step the household has to trigger. |
| Household-size preset strip | Onboarding / Settings' Yearly Baseline | A compact single row of icon buttons (one per household-size preset), never full-text pills — tapping one loads its kWh value into the field below, the field remains freely editable after, and no preset is force-selected on load unless the stored value exactly matches one (Story 2.1: presets are suggestions, never silently applied). Added this session, replacing an earlier 4-pill layout that wrapped to two rows at phone width. → [mockups/key-settings.html](mockups/key-settings.html), [mockups/key-onboarding.html](mockups/key-onboarding.html) |
| Unit-inside-field (kWh entry) | Yearly Baseline, Log Reading sheet | Any bare-kWh numeric entry renders the unit suffix *inside* the same bordered field as the number, never as a separate label beside it — one composed field, so the unit can't wrap onto its own line independently of the value. The Log Reading sheet's kWh field originated this pattern; Yearly Baseline's field was aligned to match it this session. |

## State Patterns

| State | Surface | Treatment |
|---|---|---|
| First-ever load, no computable Status | Dashboard | Onboarding prompt ("log your first reading to get started") — not blank space, not a default Status value. Fires whenever there are fewer than two Meter Readings or no Yearly Baseline set (FR-6/FR-7). |
| Status undefined mid-life (Baseline cleared, etc.) | Dashboard | Same onboarding-empty treatment as first-run — Status is never allowed to silently default to one of the three real states. |
| Tariff comparison requested pre-pace | Tariff Radar | Same onboarding empty state as FR-6/FR-7 (fewer than two Readings) rather than a zero or undefined projection (FR-12). |
| No Tariff Check currently due | Dashboard / Tariff Radar | Neutral/empty quiet line in the Tariff Check card — never a fabricated recommendation. Applies identically whether reached via a fired reminder or opened proactively (e.g. from Settings) with nothing due (UJ-2 edge case). |
| Smart Plug import in progress | Smart Plug Import / Settings | Fully async (Tier 3 NFR) — upload confirms immediately, a completion notification lands separately. UI never blocks on parsing. |
| Smart Plug import — gap detected within covered range | Smart Plug Import | Missing dates flagged as a Gap (0 kWh is a valid reading, not a Gap). Gap values used to sharpen the baseline are capped (e.g., at the preceding week's average) and visibly flagged as interpolated, never shown as measured (FR-24). |
| Smart Plug import — gap at the very start of first-ever import | Smart Plug Import | No preceding week to average from — left unfilled and flagged as missing, not interpolated from nothing. |
| Smart Plug import — entirely gaps | Smart Plug Import | Flagged for review rather than wholesale-interpolated. |
| Meter Reading regression detected | Wherever entered | Modal prompt forcing *reset* vs. *rollover* classification; excluded from baseline computation until resolved; does not silently expire or default. A second regression arriving while one is unresolved queues behind it — never a second, conflicting prompt (FR-25). See micro-flow below. |
| Unusually long gap since last reading | Dashboard / Trend History | Flagged low-confidence rather than presented with the same certainty as a normal 1-2 day interval (FR-3). |
| Offline at point of entry | Log Reading sheet | Entry queues locally, syncs on reconnect — the reading habit must not depend on live connectivity (offline capture NFR; meter locations are frequently signal-weak). |
| Cold app load | Dashboard | shadcn `Skeleton` in place of the Status card, resolving on data — matches the eventual card's footprint so nothing reflows. |
| Event with no corresponding observable deviation | Log Event | Shown without a correlation line at all — never flagged as wrong, never a forced "no match found" error state. The absence of a deviation is not itself information worth surfacing loudly (FR-17). |
| Editing a past Meter Reading or Tariff entry | Trend History / Tariff Radar | The original value is preserved and shown as a visible correction note alongside the edit — never silently overwritten (Cross-Cutting NFR: audit trail on corrections). |
| Editing a locked Tariff price field | Tariff Radar | Price fields lock once the contract start date has passed; changing one requires an explicit override step, not a plain inline edit (FR-10). |
| Import data fails validation | Smart Plug Import / Settings (data import) | Malformed data against the documented v2 export format is rejected and reported with what failed — never partially applied (FR-23). |

## Interaction Primitives

The motion language (from [mockups/motion-demo.html](mockups/motion-demo.html) — the Interaction Primitives motion reference) is calm and settling, never snappy or bouncy — this is a household utility reporting a number, not a game giving feedback.

- **Status card entrance/settle** — opacity + scale (0.96→1) + an 8px translateY, eased out (`cubic-bezier(0.22,0.61,0.36,1)`) with **no overshoot/bounce**. Plays once when the card's data resolves (cold load, or after a Status recompute). Reads as "the number has arrived and is safe to trust," not "look at me."
- **Specular sweep** — a soft diagonal highlight drifts once across the settled glass panel over roughly **2.2 seconds**, opacity-ramped at both ends so it never hard-cuts in or out (`{colors.specular-sweep}` / `{colors.specular-sweep-dark}` per `DESIGN.md`). Plays once per card entrance, immediately after settle — not a looping ambient effect in the live product (the `mockups/motion-demo.html` 8s loop is a demo-file convenience for continuous viewing, not the real cadence).
- **Primary-action press** — the Log Reading trigger compresses to ~0.965 scale with its shadow pulling in, like glass being pushed down into the stack. **No color flash** on press — the material *moves*, it doesn't recolor.
- **Sheet open/close** — Log Reading slides up from the bottom edge while the Dashboard backdrop blurs in behind it (`backdrop-filter` ramp), and reverses symmetrically on close.

**`prefers-reduced-motion` is a behavioral contract, not a decorative toggle.** Every animation above is declared *only* inside `@media (prefers-reduced-motion: no-preference)`; the unconditioned base state for every animated element already renders its final, settled appearance. Concretely, when reduced motion is requested:

- The Status card's entrance never plays — it is simply present, fully opaque and settled, on first paint. No fade, no scale-in.
- The specular sweep never runs, ever — no moving highlight under any condition.
- The primary action's press becomes a plain, instant `:active` state swap (scale/shadow change with zero transition) — no eased compression.
- The Log Reading sheet does not slide and the backdrop does not blur-ramp; it appears via an instant open/close state swap (optionally a quick opacity crossfade, never a slide/translate).

This is real, load-bearing behavior (WCAG 2.3.3-relevant), implemented as an actual CSS media-query gate — not a "reduce the speed" compromise. Nothing in the product depends on motion to convey information; motion is always a calm reinforcement of a state that's also expressed instantly and legibly without it.

## Accessibility Floor

Behavioral. Visual contrast lives in `DESIGN.md` (dark/light token pairs were chosen specifically to hold AA contrast on their respective grounds — see `DESIGN.md` Elevation & Depth on the Light-mode degradation path).

- **WCAG 2.2 AA** across the responsive web surface — confirmed this session for consumer-grade stakes.
- **`prefers-reduced-motion` support is accessibility-load-bearing, not optional.** See Interaction Primitives above — every animated element has a real, complete, instantly-legible fallback state; motion is never the sole carrier of information (state, confirmation, or otherwise).
- **Color-blind-safe status triad — decided, not a bare color signal.** The Status/trend semantic system (sage / emerald / amber) pairs every state with an explicit text badge/label ("WITHIN RANGE", "TRENDING") and a plain-language headline sentence — never a bare colored dot. Confirmed sufficient for WCAG 1.4.1: no additional shape/icon backup on the dot/indicator itself.
- Tap targets meet standard mobile touch-target minimums (44×44pt-equivalent) given the primary meter-side entry context is a phone.
- Focus order follows reading order on every surface; the Log Reading sheet and the Meter Regression prompt trap focus while open and return it to the triggering control on close.
- Sheets and modals are announced on open (role + label) for screen readers; the Status card's state change on recompute is announced via `aria-live="polite"` rather than requiring a manual refresh check.

## Micro-Flow: Meter Reading Regression Classification

FR-25 is a real, unusual, load-bearing branch that doesn't fit neatly into either Component or State Patterns above, so it gets its own walkthrough.

1. A Household member (or Log Reading sheet) submits a new Meter Reading whose value is lower than the Reading immediately preceding it **by timestamp**, not by entry order — so a backfilled Reading is compared against its chronological neighbor, never the most recently *entered* Reading.
2. The system does not compute a negative consumption rate. Instead, a classification prompt opens: *reset* or *rollover*.
3. This prompt is modal and takes priority — if a Log Reading sheet happens to still be open, the regression prompt supersedes it rather than stacking on top of it (the product's one-level-deep modal rule).
4. **Confirming *reset*** starts a new baseline-computation sequence going forward, without discarding prior Reading history — the old sequence stays visible in Trend History, just no longer chained into the live baseline math.
5. **Confirming *rollover*** computes that interval's consumption as `(meter's known digit capacity − previous Reading) + new Reading` — the mechanical meter wrapped, it didn't reset.
6. Until classified, the flagged Reading is excluded from FR-3's rolling-baseline computation — it does not silently expire, and it does not default to either classification.
7. If a second lower-than-previous Reading arrives while an earlier regression prompt is still unresolved, it queues behind the first rather than opening a second, conflicting prompt — the household resolves them one at a time, in order.

## Key Flows

### UJ-1 — Sam logs a reading on the way out the door

Sam tracks their own flat's electricity with no smart main meter, folding the reading into a daily routine — taking out the trash, leaving for work, back from the gym.

1. Sam is already authenticated (stays logged in on their phone) and opens the app standing right at the meter.
2. Sam taps the Log Reading trigger; the sheet opens over the Dashboard with today's date/time pre-selected.
3. Sam types the meter's current kWh number into the single field.
4. Sam taps Save — one confirmation step.
5. **Climax:** Confirmation lands in under a minute. The reading is captured, the habit is reinforced, and nothing broke the streak — no dashboard detour, no chart to read first.
6. Sam continues on their way.

Edge case: Sam enters a second reading later the same day with a different timestamp — accepted as a distinct entry, never rejected as a duplicate or silently overwritten. If the meter cupboard has no signal, the entry queues locally and syncs when connectivity returns.

### UJ-2 — Sam checks the dashboard after new data lands

A fresh reading just went in, or a Smart Plug file import finished — Sam opens the app deliberately, not the daily habit-tap, to see what changed.

1. Sam is authenticated and navigates to the Dashboard.
2. Sam sees the primary pace-vs-baseline Status — within range / below baseline / trending — as the first thing on the screen, no scrolling required.
3. Sam sees the Tariff Check prompt card — but *only* if a check is actually due (gated to no earlier than 3 months before the earliest contract exit, then recurring). If not due, the card shows a neutral "nothing due right now" line at quiet visual weight.
4. Sam taps into either the Status card or the Tariff Check card for detail.
5. **Climax:** Both real questions — "am I on track" and "is my tariff still worth it" — are answered together, on the same screen, without hunting for either one.
6. Sam either dismisses (all fine) or taps through into Trend History / Tariff Radar for more detail.

Edge case: no tariff check is currently due — the insight area shows the neutral/empty state described in State Patterns, not a fabricated recommendation.

### UJ-3 — Sam browses trends on a calm evening

No urgent task — self-initiated curiosity, distinct from the daily habit-tap and the "new data arrived" check.

1. Sam is authenticated and opens the app with no specific trigger, likely on a browser/tablet-width screen rather than mid-errand on a phone.
2. Sam navigates from the Dashboard into Trend History.
3. Sam reviews the general consumption trend over time — the Moderate-density chart, with any gaps in reading history rendered as a visible break rather than a guessed-at line.
4. Sam drills into the Room → Power Point → Device tree for anything notable, expanding a room or device row for measured-context detail — explicitly not a reconciled breakdown of the Main Meter total.
5. **Climax:** Nothing needs fixing, but Sam either learns something about their pattern or spots something minor worth remembering — low-stakes, satisfied browsing, not a task to complete.
6. Sam closes the app.
