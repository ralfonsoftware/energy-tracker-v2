# Energy Tracker v2 — Brainstorm Intent

## Framing

v1 was a spreadsheet-replacement built around Decomposition/Residual: attributing consumption to individual rooms/devices against the Main Meter. That crown-jewel feature was deliberately killed in this session — decomposition against a single unmetered main meter never produced trustworthy attribution. v2 reframes the product entirely: it's an aggregate pattern-detection and tariff-strategy app. Instead of explaining *where* energy goes, it tells the user whether their consumption is on track and whether their tariff is still a good deal — with minimal effort to read.

## Confirmed direction: Pattern Detective (core)

- A gap-tolerant rolling baseline computed over Main Meter readings, with imported Smart Plug data (Eve/Meross) folded in as an additional signal — not for room/device attribution, but to sharpen the aggregate pattern.
- Target experience is a single glanceable status, culminating in a zero-tap ambient notification (e.g. "Quiet week, 240kWh under pace, Saturday gaming binge already absorbed") — no chart, no numbers, dismissed in one swipe.
- Emotional core it serves: early warning when consumption trends meaningfully above baseline (~100kWh over baseline is the felt "surprise invoice" threshold), with three states — within range (relief), below baseline (delight), over threshold (actionable worry).

## Tariff Savings Radar

- Shows projected annual savings vs. the user's current contract, explicitly normalized for switching-bonus decay so a bonus-inflated first period doesn't misrepresent the comparison.
- Green/red attractiveness signal shown both with and without the bonus factored in.
- Excludes market-percentile ranking ("your tariff is top 10% / mid-range / expensive") — that requires a 3rd-party tariff data feed and is a deliberate long-run deferral, not a v2 feature.
- Shares its bonus-decay/baseline math with Pattern Detective's pace threshold.

## Context Capture

- Scoped specifically to appliances that can't be metered (induction cooktop, bathroom water heater) — not a general life-logging feature.
- Input is text/tap-first; voice (quick dictation, or an end-of-day/end-of-week recap ritual) is a future enhancement, not a v2 requirement.
- AI assists with plausibility: estimates wattage from named setups/appliances to give annotated events a quasi-proof correlation against the observed pattern.
- Smart Plug data (Eve/Meross) remains a real, separately-imported data source feeding Pattern Detective — Context Capture does not replace it, it covers only what plugs can't reach.

## Extensible Platform

Three scoped extension points only — not a plugin marketplace or code sandbox:
1. Custom event/plausibility rules (voice is just an input modality on this point, not a separate feature)
2. Generic data-source column mapping (for importing new source formats)
3. Tunable threshold/spike settings

## Foundational constraint

The user is a tenant with no smart main meter. Manual readings arrive on an irregular 1-2 day cadence. This is accepted ground truth the entire design must work with — gap-tolerance and baseline math must be built around it, not engineered away.

## MoSCoW for v2 scope

- **Must**: Pattern Detective; the real-world-constraints foundation (irregular manual readings, gap-tolerant baseline)
- **Should**: Context Capture (text/tap-first); Tariff Savings Radar (bonus-decay-aware, no market ranking)
- **Could**: Extensible Platform (3 scoped points); voice-mode input
- **Won't this round**: Market-percentile/tariff-comparison-wizard ranking (needs 3rd-party tariff data feed; confirmed long-run only)
