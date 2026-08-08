---
title: Edge Case Hunter — Energy Tracker v2 PRD
reviewed: prd.md (25 FRs) + addendum.md
created: 2026-08-08
---

# Edge Case Hunter Findings — PRD: Energy Tracker v2

Method: exhaustive path walk of each FR's stated Consequences, boundary values on the domain objects each FR introduces, concurrent/conflicting operations, empty/missing states, and cross-FR interactions. Only unhandled paths are listed — anything the PRD's Consequences already resolve is omitted.

---

## 4.1 Pattern Detective

### FR-1: Meter Reading Entry (prd.md:98–106)
- **Same timestamp, not just same day.** Consequences guarantee two readings on the *same day* with *differing* timestamps are both accepted. Two readings submitted with the **identical** timestamp (e.g. two household members racing to log at the same minute, or a client retry sending the same request twice) is not addressed — could silently create a duplicate entry or a zero-duration interval that breaks FR-3's rate calculation (division by zero).
- **First-ever reading for a household.** FR-1 doesn't say what happens when there is no preceding reading to pair with — FR-3's rate computation has nothing to compute against yet. No stated behavior (e.g. "first reading establishes the baseline start point, no rate/Status until a second reading exists").
- **Meter rollover / wrap-around.** Physical meters can roll over (e.g. 99999 → 00000). A new reading numerically lower than the previous one due to rollover is indistinguishable from FR-25's regression case at the input layer, but rollover isn't the same event as "meter replaced/reset" — no distinct handling path.
- **Concurrent submission race.** Two household members (multi-user is technically possible per Glossary) submitting readings for the same meter within the same request window — no stated conflict resolution or last-write-wins behavior.

### FR-2: Yearly Baseline Configuration (prd.md:107–114)
- **No Baseline ever set.** Onboarding presets are offered, not silently applied — meaning a household can decline all presets and skip entry. FR-6/FR-7 depend on a Baseline to compute/display Status; the PRD doesn't state what Status (if any) shows when no Baseline exists yet.
- **Mid-year Baseline edit and pace-to-date comparison.** FR-2 states an edit "does not retroactively rewrite past Status history," but FR-3 requires "pace-to-date vs. baseline-to-date" comparison. When the Baseline changes mid-year, it's unspecified whether the *going-forward* pace-to-date math prorates against the old Baseline for the elapsed portion of the year or fully switches to the new figure — ambiguous, and it directly changes what "within range" means the day after the edit.
- **Baseline set to 0.** No lower-bound validation stated; a 0 kWh/year Baseline would make FR-6's threshold-over-baseline-pace comparison degenerate (any nonzero consumption is infinitely over pace).

### FR-3: Gap-Tolerant Rolling Baseline Computation (prd.md:115–122)
- **Single reading (no pair yet).** See FR-1 above — FR-3 assumes "the sequence of Meter Readings" produces a rate; with exactly one reading there is no interval to rate against, and no stated fallback Status.
- **Unbounded gap / long-stale data.** FR-3 states a gap "does not break or reset the computation," but there's no upper bound — a household that stops logging for months still produces *a* rate, silently treated with the same confidence as a 1-2 day interval. No stated staleness signal distinguishing "confidently within range" from "computed from an ancient, low-confidence interval."

### FR-4: Smart Plug File Import (prd.md:123–133)
- **Re-import / overlapping date range.** Uploading a file whose covered dates overlap a previously imported file (re-export, corrected export, accidental duplicate upload) — no stated dedup/overwrite/merge behavior, unlike FR-23 which explicitly rejects malformed data rather than partially applying it.
- **Malformed/partially-corrupt file within a supported format.** FR-4 states unmapped Power Points prompt creation rather than silent failure, but doesn't address a file that parses partially then hits a corrupt row/sheet mid-file — no stated all-or-nothing vs. partial-apply behavior (contrast with FR-23's explicit reject-rather-than-partially-apply rule for data import).
- **Concurrent imports targeting the same Power Point.** Two async imports (FR-4 is async per the Tier-3 NFR) for overlapping date ranges processed in parallel — no stated ordering/locking guarantee.

### FR-5: Baseline Sharpening from Smart Plug Signal (prd.md:135–142)
- **Smart Plug signal exceeds Main Meter total.** Glossary states Main Meter is "the sole source of truth" and Smart Plug data "sharpens the read, it doesn't get summed against it" — but if summed/derived Smart Plug consumption for a period *exceeds* what the Main Meter recorded for the same window (plausible with measurement error or tagging mistakes), there's no stated resolution (which one governs the sharpened baseline).

### FR-6: Status Computation (prd.md:143–150)
- **Exact threshold boundary.** "Trending fires when pace exceeds ... by more than the threshold" — the case where pace equals baseline-pace-plus-threshold exactly is not classified (falls to *within range* by the "more than" wording, but this is never stated as a deliberate inclusive/exclusive boundary decision, just implied by phrasing).
- **Threshold changed retroactively.** Same ambiguity as FR-2: when a household edits the trending-threshold setting, it's unstated whether already-computed historical Status entries are left alone (as FR-2 explicitly promises for Baseline edits) or whether only forward computation changes — FR-6 has no equivalent explicit non-retroactivity statement.
- **Status computable before a Baseline exists.** No stated behavior for FR-7's dashboard display when FR-6 has nothing to compute against (see FR-2 above) — contrast with FR-15's explicit "no reminder fires" rule for the analogous no-Tariff-configured case; FR-6/FR-7 lack an equivalent explicit empty-state rule.

### FR-7: Dashboard Status Display (prd.md:151–159)
- **No computable Status.** Per FR-6 above, the PRD defines an explicit neutral/empty state for the *tariff* insight area (UJ-2's "no tariff check due" case) but not for the Status element itself when Pattern Detective can't yet produce one (onboarding day one, single reading, no Baseline) — since FR-7 says Status is "the primary, glanceable element ... visible ... on first dashboard load," a first-ever session has no defined content for that slot.

### FR-8: Trend History View (prd.md:161–167)
- **Gaps rendered in the trend.** FR-24 defines gap handling for Smart Plug imports specifically (flagged as interpolated), but the trend view over Meter-Reading-only history has no stated treatment for how a multi-day reading gap is rendered (e.g. interpolated line vs. visible break) — inconsistent with FR-24's explicit "never presented as measured data" rule for the smart-plug case.

### FR-9: Per-Plug Measured Data View (prd.md:168–174)
- **Device/Power Point retagged after data was imported.** If a Device is moved to a different Power Point (or a Power Point renamed/reassigned to a different Room) after Smart Plug data was already imported and associated, it's unstated whether historical imported data follows the new tag or stays attributed to the tag active at import time.

### FR-24: Smart-Plug Import Gap Handling (prd.md:175–182)
- **Gap at the start of an import range with no preceding week.** The stated bound ("capped at the average of the preceding week") has no defined behavior when the gap is at the very beginning of a household's first-ever import — there is no preceding week of data to average.
- **Import file that is entirely gaps.** No stated minimum-data threshold below which the whole import is rejected/flagged rather than interpolated wholesale.

### FR-25: Meter Reading Regression Detection (prd.md:183–190)
- **Unconfirmed regression left pending.** FR-25 states a lower reading "prompts confirmation" but doesn't state what happens if the household never responds (dismisses, closes the app, ignores) — is the reading held in limbo, silently excluded from FR-3, or does it default to one of the two paths (accept-as-reset vs. reject) after some condition?
- **Ordering by timestamp vs. entry order for backfilled readings.** FR-1 explicitly allows a reading with an earlier timestamp than the most recent one (backfill). FR-25 triggers "when a new Meter Reading is lower than the immediately preceding Reading" — for a backfilled entry it's unstated whether "immediately preceding" means immediately preceding *by timestamp* (the reading actually adjacent in sequence once inserted) or *by entry order* (the most recently entered reading, which may be chronologically unrelated). These produce different regression triggers for the same backfill.
- **Second regression before the first is resolved.** No stated handling for a new out-of-order/lower reading arriving while an earlier regression prompt is still unconfirmed — stacked/ambiguous pending state.

---

## 4.2 Tariff Savings Radar

### FR-10: Tariff Configuration (prd.md:201–208)
- **Overlapping or out-of-order Tariff entries.** History is retained with "each entry covers until the next one's start date," but nothing stops a new entry's start date from being *before* the currently latest entry's start date (data-entry error or intentional backfill) — no stated conflict rule, so "current Tariff" becomes ambiguous.
- **Two entries sharing the same start date.** Not addressed — which one is authoritative for "current Tariff" is undefined.
- **Exact-boundary lock.** Price fields "lock once their contract start date has passed" — the instant the start date *equals* today is a boundary not explicitly resolved (locked yet or still editable on the start date itself).

### FR-11: Candidate Tariff Comparison Entry (prd.md:209–215)
- **Multiple simultaneous scratch comparisons.** No stated limit or replace-vs-accumulate behavior when a household enters more than one candidate comparison — does a new entry replace the prior scratch comparison or do multiple persist side by side?

### FR-12: Bonus-Decay Normalized Savings Projection (prd.md:216–222)
- **Projection requested before Pattern Detective has a usable pace.** FR-12 states the projection "uses actual household consumption pace (from Pattern Detective)," but per the FR-3 gap above (no pace until ≥2 readings exist), there's no stated fallback when that pace isn't yet available — can a household run a Tariff comparison on day one, and if so, against what figure?

### FR-13: Two-Way Attractiveness Signal (prd.md:223–229)
- **Exact breakeven (savings = 0).** No stated classification for savings that land exactly at zero — undefined whether the signal reads green or red at the boundary.

### FR-14: Shared Bonus-Decay Math with Pattern Detective (prd.md:230–236)
- **Formula change vs. already-computed comparisons.** FR-14 guarantees the same logic feeds both features "identically," but doesn't state whether a change to the normalization formula (e.g. via future tuning) retroactively recomputes standing scratch comparisons (FR-11) and previously-set Pattern Detective thresholds, or only applies forward — same class of retroactivity gap as FR-2/FR-6.

### FR-15: Tariff Check Reminder (prd.md:237–245)
- **Tariff edited mid-cycle changes the gate the reminder is tracking.** FR-10 allows editing a Tariff entry (via the explicit override step for locked entries) — if the contract start date or Contract Period length changes after the reminder schedule was already computed against the old dates, FR-15 doesn't state whether the 3-months-before-exit gate and recurring cadence recompute against the new dates or keep running on the stale schedule. This is the direct analog of an FR-25-style reset invalidating a previously-fired-from assumption, but nothing in FR-15 or FR-10 addresses it.
- **Household switches Tariff before the reminder ever fires.** If a candidate (FR-11) is accepted and becomes the new current Tariff (FR-10) before the old Contract Period's reminder gate opened, it's unstated whether the reminder timer resets against the new Tariff's Contract Period or carries over stale state from the replaced one.
- **Which Contract Period gates the reminder when Tariff history exists.** FR-10 retains history; FR-15 refers to "the current Contract Period" — with multiple retained entries (especially given the FR-10 overlapping-entry gap above), which entry's Contract Period is authoritative for the gate is not explicit.
- **Cadence changed after a reminder is already scheduled.** No stated behavior for whether a mid-cycle cadence edit reschedules the next-due date immediately or only affects cycles after the one already pending.
- **Baseline (FR-2) changes mid-Contract-Period, altering what the dashboard shows alongside the reminder.** UJ-2 presents "am I on track" (FR-6/FR-7, driven by FR-2's Baseline) and "is my tariff still worth it" (FR-15) together on the same dashboard view. A Baseline edit mid-Contract-Period changes the pace-to-date figure FR-12's projection consumes (see FR-12 above) at the same time FR-15 is independently gating on contract dates — no stated coordination between the two changing inputs landing on the same screen, so a household could see a stale-projection tariff prompt fire using pace data that just changed underneath it.

---

## 4.3 Context Capture

### FR-16: Event Logging (prd.md:252–261)
- **Overlapping Event time spans.** Two Events logged with overlapping windows (e.g. "away 2 weeks" logged twice with overlapping ranges, or a correction re-log without editing the original) — no stated conflict or merge handling.
- **Event tagged to a Room/Power Point/Device later deleted.** No stated behavior for the orphaned tag reference on an already-logged Event.

### FR-17: Wattage Plausibility Correlation (prd.md:262–270)
- **Multiple Events competing for the same observed bump.** If two Events fall in the same window as a single Pattern Detective bump, FR-17 doesn't state whether both get the correlation, only the closer one, or how the many-to-one mapping resolves.
- **AI service transient failure vs. static disable.** The graceful-degradation guarantee (§Constraints) covers the feature being off; it doesn't address a live request that times out or errors mid-session while the feature is enabled — no stated fallback message/retry behavior distinct from the "disabled" state.

### FR-18: Proactive Weekly Recap (prd.md:271–281)
- **Same spike triggers repeat prompts.** If a *trending* Status persists across multiple detection cycles (FR-6 recomputes "on every new Meter Reading or completed import"), FR-18 doesn't state whether the same underlying spike re-prompts each time it's re-evaluated or prompts exactly once per spike.
- **Recap fires after the spike is already explained.** If a household already self-logged an Event (FR-16) covering the same window before the proactive recap would fire, FR-18 doesn't state whether the system checks for an existing covering Event and suppresses the redundant prompt.
- **Recap prompt triggered by a meter-reset artifact, not real consumption.** A confirmed FR-25 reset starts "a new baseline-computation sequence" — if that discontinuity is itself misread as a spike/trending Status by FR-6 before enough post-reset data accumulates, FR-18 could proactively ask "anything unusual this week?" about an artifact of the meter reset rather than real usage. Neither FR-25 nor FR-18 states a suppression rule for this interaction.

---

## 4.4 Extensible Platform

### FR-19: Custom Event/Plausibility Rules (prd.md:288–294)
- **Conflicting custom rules.** No stated precedence/conflict-resolution when two custom rules would both match the same logged Event with different correlation outcomes.

### FR-20: Generic Data-Source Column Mapping (prd.md:295–303)
- **Required field left unmapped, or mapped twice.** No stated validation behavior for an incomplete or duplicate column mapping (flagged low-confidence/may-be-dropped in the PRD itself, but as written the FR has no stated guard).

### FR-21: Tunable Threshold/Spike Settings (prd.md:304–310)
- **Cross-view threshold inconsistency.** FR-21's per-view sensitivity/highlighting settings and FR-6's single trending-threshold number both define "spike"-like behavior; no stated rule for what happens when they disagree (e.g. trend view flags a spike FR-6's Status doesn't consider *trending*) — risks contradicting the product-discipline guardrail that the drill-down must never be needed to trust the headline Status.

---

## 4.5 Data Export/Import

### FR-22: Full Data Export (prd.md:317–319)
- **Export requested while an import or Smart Plug processing is in flight.** No stated consistency guarantee (snapshot isolation) for an export triggered concurrently with an in-progress FR-4 async import or an FR-23 import — could export a partially-applied state.

### FR-23: Full Data Import (prd.md:321–328)
- **Import into a Household that already has data.** FR-23 is framed as "restore/migrate," but doesn't state whether importing into an already-populated instance replaces, merges, or is blocked — ambiguous whether existing data is destroyed.
- **Cross-Household import.** Given the cross-cutting tenant-isolation NFR, no stated check that an imported export file's Household identity matches the importing instance/Household — an export from one Household imported into another isn't addressed.
- **Concurrent writes during import.** No stated behavior for Meter Readings/Events being actively logged by another session while a full import is in progress (race between live writes and the restore operation).

---

## Summary

25 FRs reviewed for unhandled edge cases in their stated Consequences, plus cross-FR interactions. Recurring unhandled classes across the document:
1. **Retroactivity ambiguity** on mid-cycle changes to Baseline (FR-2), threshold (FR-6), and normalization formula (FR-14) — each states or implies a "changes don't rewrite history" rule inconsistently, or not at all.
2. **No-data / first-use empty states** — FR-3, FR-6, FR-7, FR-12 all assume at least one prior data point or configured value exists; none state the true first-run behavior.
3. **Unconfirmed/pending user-decision states** — FR-25's regression prompt and FR-4's Power-Point-creation prompt have no stated timeout/abandonment behavior.
4. **Schedule invalidation across FRs** — FR-15's reminder gate doesn't account for FR-10 Tariff edits or FR-11 switch-acceptance changing the dates it's tracking; FR-18's recap doesn't account for FR-25 reset artifacts.
5. **Concurrent/overlapping operations** — simultaneous readings, overlapping imports, overlapping Tariff entries, overlapping Events, and import-vs-live-write races are consistently unaddressed.
</content>
