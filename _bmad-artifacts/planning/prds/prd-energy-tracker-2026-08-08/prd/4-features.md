# 4. Features

## 4.1 Pattern Detective

**Description:** The core feature. A gap-tolerant rolling consumption baseline computed from manually entered Main Meter Readings, sharpened by Smart Plug import data wherever available, culminating in a single Status shown on the dashboard. Realizes UJ-1 (reading entry), UJ-2 (dashboard status check), UJ-3 (trend/drill-down browsing). Because readings arrive on an irregular 1-2 day cadence rather than a fixed daily one, the baseline is computed as a consumption *rate* between reading pairs rather than assuming daily granularity — this is what makes it gap-tolerant by construction, not as a patched-on exception.

**Functional Requirements:**

### FR-1: Meter Reading Entry

A Household member can enter a Meter Reading with today's date/time pre-selected (editable, to backfill a reading noticed late) and a single kWh number, then save with one confirmation step. Realizes UJ-1.

**Consequences (testable):**
- Save-to-confirmation completes in under a minute for the default (pre-selected, no edits) path.
- Multiple Readings on the same calendar day are accepted as long as their timestamps differ — no duplicate-reject or overwrite prompt.
- Entering a Reading with an earlier timestamp than the most recent one is accepted (backfill case), not rejected.

### FR-2: Yearly Baseline Configuration

A Household member can set and later edit their Household's Yearly Baseline target (kWh/year) during onboarding and from settings.

**Consequences (testable):**
- Household-size presets are offered as starting suggestions, not defaults silently applied: 1 person ≈ 1500 kWh, 2 people ≈ 2500 kWh, 3 people ≈ 3500 kWh, 4 people ≈ 4250 kWh (typical figures from tariff-comparison sites).
- The Yearly Baseline can be changed at any time; a change does not retroactively rewrite past Status history.

### FR-3: Gap-Tolerant Rolling Baseline Computation

The system computes an expected consumption pace from the sequence of Meter Readings, regardless of the interval between them.

**Consequences (testable):**
- A gap of several days between two Readings does not break or reset the computation — it's absorbed into the rate calculation between that reading pair.
- The computed pace is comparable against the Yearly Baseline on a like-for-like (pace-to-date vs. baseline-to-date) basis.
- An unusually long gap (the household hasn't logged in months) is flagged as low-confidence rather than presented with the same certainty as a normal 1-2 day interval.

### FR-4: Smart Plug File Import

A Household member can upload a Smart Plug export file (Eve Home `.xlsx`, Meross `.csv`); the system parses it and associates the data with the Power Point it's tagged to.

**Consequences (testable):**
- Import processing is asynchronous with a completion notification — file parsing does not block the UI.
- An import tagged to a Power Point that doesn't yet exist prompts creation/mapping rather than silently failing.
- Eve Home timestamps are interpreted as local time, not converted to UTC on import — avoids corrupting interval data at midnight boundaries.
- Meross exports are matched to a Device/Power Point via the export filename pattern, not by trusting in-file metadata alone.

*(Exact file schema — sheet names, cell references, column layout per source — is implementation detail; see `addendum.md` for the reference mapping carried over from v1.)*

### FR-5: Baseline Sharpening from Smart Plug Signal

Imported Smart Plug data refines Pattern Detective's baseline/Status as an additional signal.

**Consequences (testable):**
- A Household with zero Smart Plug coverage still gets a fully functional Status from Meter Readings alone.
- Smart Plug data is never required to reconcile to the Main Meter total — it sharpens the read, it doesn't get summed against it.

### FR-6: Status Computation

The system computes a single Status — *within range*, *below baseline*, or *trending* — from the current pace vs. the Yearly Baseline and the household's threshold setting.

**Consequences (testable):**
- *Trending* fires when pace exceeds Yearly Baseline pace by more than the household's configured threshold (default ~100 kWh over baseline pace-to-date, editable in settings).
- Status recomputes on every new Meter Reading or completed Smart Plug import — never on a fixed schedule alone.
- With fewer than two Meter Readings, or no Yearly Baseline set, Status is undefined rather than defaulting to any of the three states — FR-7 shows an onboarding empty state instead.
- Pace exactly equal to Yearly Baseline pace plus the threshold resolves to *within range*, not *trending* — ties resolve to the calmer state throughout the product.

### FR-7: Dashboard Status Display

The main dashboard shows the current Status as the primary, glanceable element. Realizes UJ-2.

**Consequences (testable):**
- Status is visible without scrolling or drilling into a sub-view on first dashboard load.
- No chart is required to read the Status — it's legible as a single state.
- On first-ever load with no computable Status (per FR-6), the dashboard shows an onboarding prompt (e.g. "log your first reading to get started") rather than blank space or a default Status value.

**Out of Scope:** Ambient/push notification delivery of the Status — deferred to a later version; dashboard display is the v2 mechanism (see Open Question 2).

### FR-8: Trend History View

A Household member can view historical Status/pace trend over time. Realizes UJ-3.

**Consequences (testable):**
- The view shows trend, not just the current point-in-time Status.
- Gaps in the underlying Meter Reading history are rendered as a visible break in the trend, not an interpolated line — interpolation-and-flag treatment (FR-24) is specific to Smart Plug import data, not the core Reading-based trend.

### FR-9: Per-Plug Measured Data View

A Household member can view measured Smart Plug data organized by the Room → Power Point → Device structure it's tagged to. Realizes UJ-3.

**Consequences (testable):**
- This view is explicitly presented as measured context, not a reconciled attribution breakdown of the Main Meter total — nothing here is summed against or claims to explain the Main Meter's number.
- If a Device or Power Point is retagged after Smart Plug data was already imported, previously imported data stays attributed to the tag that was active at import time — it does not silently move to follow the retag.

### FR-24: Smart-Plug Import Gap Handling

The system detects gaps within an imported Smart Plug file's covered date range (missing interval data) and handles them without silently treating the gap as zero consumption.

**Consequences (testable):**
- A missing date within an import's covered range is treated as a Gap; a 0 kWh reading is a valid data point, not a Gap.
- Gap values used to sharpen the baseline (FR-5) are bounded — e.g. capped at the average of the preceding week — and visibly flagged as interpolated, never presented as measured data.
- A Gap at the very start of a household's first-ever import (no preceding week to average) is left unfilled and flagged as missing, not interpolated from nothing.
- An import file whose data is entirely Gaps is flagged for review rather than wholesale-interpolated.

### FR-25: Meter Reading Regression Detection

When a new Meter Reading is lower than the Reading immediately preceding it by timestamp for the same Main Meter, the system flags it instead of silently computing a negative consumption rate, and distinguishes two causes: a meter replacement/reset, or a digit rollover (e.g. a mechanical meter wrapping from 99999 to 00000).

**Consequences (testable):**
- A lower-than-previous Reading prompts the household to classify it as *reset* or *rollover* rather than feeding a negative rate into FR-3's baseline computation.
- Confirming *reset* starts a new baseline-computation sequence going forward without discarding prior Reading history.
- Confirming *rollover* computes the interval's consumption as (meter's known digit capacity − previous Reading) + new Reading, rather than treating the sequence as reset to zero.
- "Immediately preceding" is determined by timestamp order, not entry order — a backfilled Reading is compared against its chronological neighbor, not the most recently entered Reading.
- An unconfirmed regression prompt excludes that Reading from FR-3's computation until resolved; it does not silently expire or default to either classification.
- A second lower-than-previous Reading arriving while an earlier regression prompt is still unconfirmed queues behind it rather than opening a second, conflicting prompt.

**Feature-specific NFRs:**
- Reading entry (FR-1) and dashboard load (FR-7) target sub-2-second response (Tier 1, see §Cross-Cutting NFRs).
- Smart Plug import (FR-4) runs fully async with a completion signal (Tier 3).

## 4.2 Tariff Savings Radar

**Description:** Answers whether the household's current Tariff is still worth staying on. Compares the current Tariff against a candidate rate the user enters, normalizing out any switching bonus so an inflated first period doesn't misrepresent the comparison. Shares its bonus-decay math with Pattern Detective's pace threshold so the two never diverge. Realizes UJ-2 (dashboard tariff insight).

**Functional Requirements:**

### FR-10: Tariff Configuration

A Household member can enter and maintain their Household's current Tariff: base fee, price/kWh, currency, contract start date, and Contract Period.

**Consequences (testable):**
- Tariff history is retained — each entry covers until the next one's start date, not overwritten.
- Price fields lock once their contract start date has passed; editing a locked entry requires an explicit override step, not a silent overwrite.
- Tariff Configuration models a flat base-fee-plus-price/kWh structure only; tiered or time-of-use billing structures are explicitly out of scope for v2 (see Non-Goals) — a Household on such a tariff approximates it as a flat rate.
- Currency is a required field per Tariff entry, not hardcoded to EUR.

### FR-11: Candidate Tariff Comparison Entry

A Household member can enter a candidate tariff (price/kWh, base fee, optional switching bonus terms) to compare against their current Tariff.

**Consequences (testable):**
- Comparison entries are scratch/exploratory — entering one doesn't alter the Household's actual current Tariff (FR-10) until they explicitly switch.

### FR-12: Bonus-Decay Normalized Savings Projection

The system projects annual savings of the candidate tariff vs. the current Tariff, applying Bonus-Decay Normalization.

**Consequences (testable):**
- The projection uses actual household consumption pace (from Pattern Detective), not a generic/assumed usage figure.
- A comparison requested before Pattern Detective has a usable pace (fewer than two Meter Readings) shows the same onboarding empty state as FR-6/FR-7, not a zero or undefined projection.

### FR-13: Two-Way Attractiveness Signal

The comparison shows a green/red signal twice: once with the Switching Bonus included, once normalized out.

**Consequences (testable):**
- Both signals are shown together, not toggled — the bonus-inflated view stays visibly distinct from the honest one.
- Exact breakeven (projected savings of zero) resolves to red/not-worth-switching on the normalized signal — ties favor staying put, consistent with the product never overstating a case to switch.

### FR-14: Shared Bonus-Decay Math with Pattern Detective

Tariff Savings Radar and Pattern Detective's pace threshold use the same underlying Bonus-Decay Normalization logic.

**Consequences (testable):**
- A change to the normalization formula affects both features identically — no separate, divergent implementations.

### FR-15: Tariff Check Reminder

*(Ships after FR-10 through FR-14 are stable — sequenced, not day-one.)* The system surfaces a proactive prompt to revisit the Tariff Savings Radar, gated to no earlier than 3 months before the current Contract Period ends, then recurring at a cadence the household can customize.

**Consequences (testable):**
- No reminder fires while more than 3 months remain in the current Contract Period.
- Default recurring cadence after the gate opens is every 3 months, editable per household.
- If the household has no Tariff configured yet (FR-10 empty), no reminder fires — nothing to compare against.
- Contract Period represents a minimum term, not necessarily a hard end date: once the minimum term elapses, the Reminder gate opens and stays open on the recurring cadence whether the Tariff then ends outright or auto-continues on a rolling basis — the household is not required to know or enter an explicit contract end date to get reminders.
- Editing the Tariff's contract start date or Contract Period (FR-10) after the Reminder schedule was computed recomputes the gate and cadence against the new dates going forward.

## 4.3 Context Capture

**Description:** Covers what no Smart Plug can see — the induction cooktop, the bathroom water heater — with fast, text/tap-first logging of Events. AI-assisted Wattage Plausibility gives a logged Event a rough correlation against the observed Pattern Detective deviation, without claiming precise attribution. Deliberately scoped to unmeasurable appliances — not a general life-logging feature, not a replacement for Smart Plug data where that already exists.

**Functional Requirements:**

### FR-16: Event Logging

A Household member can log an Event with a short text/tap-first entry (e.g. "cooked 2h," "gaming session 3h," "away 2 weeks") and an optional tag to a Room, Power Point, or Device.

**Consequences (testable):**
- Logging an Event takes comparable effort to a Meter Reading entry (FR-1) — deliberately low-friction, not a form.
- An Event can be logged for a past date/time (backfill), same as a Reading.
- A Room/Power Point/Device tag on an Event is a label at the time of logging — deleting the tagged item later leaves the Event's historical tag as inert text rather than a broken reference.

**Out of Scope:** Recurring/pattern events (e.g. "WFH 4x/week") — each Event is a single dated occurrence in v2; a household with a recurring pattern logs it once per occurrence.

### FR-17: Wattage Plausibility Correlation

The system gives a logged Event a rough plausibility correlation against the consumption deviation — a bump or a dip — Pattern Detective observed around that time.

**Consequences (testable):**
- The correlation is shown as a rough/approximate signal (e.g. "roughly matches the bump seen" or "roughly matches the dip seen"), never presented with false precision or as a verified attribution.
- An Event expected to raise consumption (e.g. "gaming session 3h") correlates against a bump; an Event expected to lower it (e.g. "away 2 weeks") correlates against a dip — the direction is inferred from the Event, not assumed to always be a bump.
- An Event with no corresponding observable deviation is not flagged as wrong — it's shown without a correlation, since absence of a deviation doesn't disprove the event.
- Two Events logged in the same window as one observed deviation both receive the correlation — the mapping is many-to-one, not first-match-wins.
- This feature is optional and gracefully degradable: the rest of the product functions fully with it disabled (see §Constraints and Guardrails).
- The AI backend is a Household-level configuration choice: a locally hosted model (e.g. via LMStudio) or a cloud/external API. Which mode is active — and therefore whether Event data ever leaves the deployment — is always visible and under the household's control, consistent with the Constraints privacy stance.

### FR-18: Proactive Weekly Recap

*(Optional, ships after FR-16/17 are stable.)* When Pattern Detective flags a spike or a *trending* Status, the system can proactively prompt the household — "anything unusual this week?" — and thread the reply directly onto the flagged spike as an Event.

**Consequences (testable):**
- Opt-in: off by default, enabled per household in settings — not a blanket nag turned on for everyone.
- Only fires against an actual detected spike/trending Status, not on a fixed weekly schedule regardless of data.
- A reply becomes a normal Event (FR-16), shown and correlated (FR-17) exactly like a self-initiated log entry — no separate data path.
- A given spike/trending episode prompts once, not on every recomputation cycle while it persists.
- If the household already logged an Event (FR-16) covering the same window before the prompt would fire, the prompt is suppressed — it doesn't ask about something already explained.
- A discontinuity from a confirmed FR-25 meter reset is not itself treated as a spike for prompting purposes — the recap targets real consumption deviations, not reset artifacts.

**Out of Scope:** General life-logging unrelated to energy use; replacing Smart Plug data for appliances that already have plug coverage.

## 4.4 Extensible Platform

**Description:** Three scoped extension points, not a plugin marketplace — the mechanism that lets Energy Tracker "reach" further than a spreadsheet without becoming a platform project. All three are Could-have: genuine value, not blocking.

**Functional Requirements:**

### FR-19: Custom Event/Plausibility Rules

A Household member can define custom rules for how logged Events correlate to Wattage Plausibility, beyond the built-in correlation logic.

**Consequences (testable):**
- This is the seam that voice input plugs into later — dictation becomes just another input modality producing an Event, not a separate feature requiring its own correlation logic.

### FR-20: Generic Data-Source Column Mapping

*(Could-have, low confidence — see Open Question 1.)* A Household member can map a new Smart Plug export format's columns to the system's expected schema without a code change.

**Consequences (testable):**
- Scoped narrowly to formats that are actually tabular/mappable; formats requiring bespoke parsing logic remain a code-level addition, not a config-level one.

**Notes:** `[NOTE FOR PM]` Real-world export formats vary enough (structure, encoding, units, timestamp handling — see Eve Home vs. Meross differences) that generic column mapping may not be achievable with useful effort. Evaluate feasibility before committing engineering time; may be dropped entirely.

### FR-21: Tunable Threshold/Spike Settings

A Household member can adjust threshold and spike-detection settings — beyond the single trending-threshold number already exposed in Pattern Detective (FR-6) — governing things like trend history view sensitivity and per-plug measured-data view highlighting, through settings rather than low-level config file editing.

**Consequences (testable):**
- Adjustments are made through the product's UI/settings surface, not by editing deployment config files.

## 4.5 Data Export/Import

**Description:** Disaster-recovery backup for a household's own data, underneath all four capabilities above. Must-have.

**Functional Requirements:**

### FR-22: Full Data Export

A Household member can export all of their Household's data (Meter Readings, Tariff history, Events, Smart Plug data, settings) in a documented format for disaster-recovery backup.

### FR-23: Full Data Import

A Household member can import a previously exported dataset (in the v2 export format from FR-22) to restore a v2 instance or move it to new hosting.

**Consequences (testable):**
- Import validates against the documented v2 export format and rejects/reports malformed data rather than partially applying it.
- This is a v2-to-v2 restore/migration mechanism only — it does not read or convert v1 data (see §5 Non-Goals).
- Importing into a Household that already has data is blocked by default, requiring an explicit "replace all data" confirmation step — v2 has no partial-merge import mode.

## 4.6 Household & Access

**Description:** Every other feature assumes an authenticated member of a Household. This feature covers how that Household and its members come to exist in the first place — the load-bearing prerequisite for a self-deploy-only product.

**Functional Requirements:**

### FR-26: Household Provisioning

The first person to access a fresh Energy Tracker deployment can create the Household and becomes its first member, authenticated via the configured OIDC provider.

**Consequences (testable):**
- A fresh deployment with no Household yet routes any authenticated visitor into a Household-creation step rather than a broken or empty dashboard.
- Household creation does not require a second party, an invite code, or any manual database step.

### FR-27: Household Member Invitation

An existing Household member can invite additional members to the same Household.

**Consequences (testable):**
- All Household members have equal, full access — there is no separate admin/owner role, consistent with the product not being designed around managing users (see Non-Goals).

### FR-28: Room / Power Point / Device Management

A Household member can create, edit, and delete Rooms, Power Points, and Devices in the tagging scaffold used across Pattern Detective (FR-4, FR-9) and Context Capture (FR-16).

**Consequences (testable):**
- Deleting a Power Point or Device that already has tagged historical data (imports, Events) orphans/leaves that data reassignable rather than cascade-deleting it.
