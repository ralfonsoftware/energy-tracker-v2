---
title: Brainstorm → PRD Reconciliation
created: 2026-08-08
---

# Reconciliation: Brainstorm Session vs. PRD

**Sources read:**
- `_bmad-artifacts/brainstorming/brainstorm-energy-tracker-v2-2026-08-08/brainstorm-intent.md`
- `_bmad-artifacts/brainstorming/brainstorm-energy-tracker-v2-2026-08-08/brainstorm.html` (rendered session ledger; substance extracted, styling ignored)
- No `.memlog.md` file exists in that folder (only `brainstorm-intent.md` and `brainstorm.html` are present — the HTML footer claims to be "Transcribed from .memlog.md" but no such raw file is on disk to cross-check separately)
- `_bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd.md`
- `_bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/addendum.md`

## Part 1 — Verifying the four previously-identified deltas

| # | Brainstorm delta | Claimed landing spot | Verified? |
|---|---|---|---|
| 1 | ~100kWh surprise-invoice threshold anchor | FR-6 default | **Confirmed.** FR-6 consequence: "*Trending* fires when pace exceeds Yearly Baseline pace by more than the household's configured threshold (default ~100 kWh over baseline pace-to-date, editable in settings)." Exact number and "editable" nuance both preserved. |
| 2 | Proactive weekly recap mechanic | FR-18, optional/opt-in, ships after core Context Capture | **Confirmed.** FR-18 header: "*(Optional, ships after FR-16/17 are stable.)*"; consequence: "Opt-in: off by default, enabled per household in settings." Sequencing and opt-in nature both correct. The coach's specific mechanic — "anything unusual this week?" threaded onto the flagged spike — is reproduced almost verbatim. |
| 3 | "Two separate jobs to be done" framing | §2.1 | **Confirmed.** §2.1 lists both: "Emotional/early-warning" ("Tell me early if I'm heading for a surprise invoice…") and "Decision" ("Help me decide whether switching tariffs would actually save money…") as distinct JTBD entries, matching Work Order 1 and Work Order 2 in the brainstorm's Job-to-Be-Done section. |
| 4 | Minor AI-plausibility example detail | — | **Confirmed dropped, as expected.** The brainstorm's specific illustrative examples ("guesstimate wattage from named setups — gaming PC components, electric cooking appliances") do not appear in FR-17 or the §3 Wattage Plausibility glossary entry, which stay generic. This matches the framing of this item as a known, accepted, minor loss — not re-flagged as a new gap. |

All four claims check out as described.

## Part 2 — New gaps found (beyond the known list)

### Gap A: Recurring/pattern-style event example ("WFH 4x/week") has no home in the Event model

The brainstorm's life-event annotation idea (Section 1, "re-lit" bulb 4) lists four examples: *"cooked 2h, gaming session 3h, WFH 4x/week, on vacation 2 weeks."* FR-16 (Event Logging) carries forward three of the four almost verbatim ("cooked 2h," "gaming session 3h," "away 2 weeks" ≈ "on vacation 2 weeks") but silently drops "WFH 4x/week."

This isn't just a dropped example string — "WFH 4x/week" is qualitatively different from the other three: it describes a *recurring weekly pattern*, not a single dated occurrence. FR-16's Event model is single-occurrence only ("can be logged for a past date/time (backfill), same as a Reading" — no mention of recurrence). Nothing in Context Capture (§4.3), the Glossary's `Event` definition, or the Extensible Platform's custom rules (FR-19) addresses recurring/standing annotations. If a household wants to explain a sustained pattern shift (e.g., "working from home 4 days/week now" as an ongoing state rather than a one-off event), the current model has no way to express that — it wasn't deliberately scoped out (no Non-Goal or Out-of-Scope line mentions recurring events), it was just quietly not carried through.

**Severity:** Minor/moderate. Not addressed in the addendum either (which only discusses threshold profiles and deployment shape). Worth either an explicit Non-Goal line ("Events are single-occurrence only; recurring pattern annotations are out of scope") or a follow-up Open Question, so the omission reads as a decision rather than an oversight.

### Gap B: Brainstorm's Must/Should priority split isn't preserved in the PRD's MVP scope section

The brainstorm's MoSCoW table (Section 5) explicitly separates:
- **Must:** Pattern Detective, Real-World Constraints (foundation)
- **Should:** Context Capture (text/tap-first), Tariff Savings Radar (bonus-decay-aware, no market ranking)

The PRD's §6.1 "In Scope" for MVP flattens this: Pattern Detective, Tariff Savings Radar, Context Capture, Data Export/Import, and the tagging scaffold are all listed as equally in-scope, with no Must/Should distinction carried over. (The PRD *does* use Could-have/Must-have labels elsewhere — e.g. §4.4's "All three are Could-have," §4.5's "Must-have" for Data Export/Import — so the labeling convention exists, it's just not applied to distinguish Pattern Detective from Tariff Radar/Context Capture in the scope section.)

**Severity:** Minor. This matters mainly as a fallback signal — if MVP scope ever needs to shrink under time pressure, the PRD as written gives no textual cue that Tariff Savings Radar and Context Capture were originally "Should," one notch below Pattern Detective's "Must." Worth a one-line addition to §6.1 if that prioritization signal should survive for downstream planning (sprint planning / epic sequencing).

### Observation (not counted as a gap): the ambient zero-tap notification "north star" was descoped, but traceably

The brainstorm's Sci-Fi Artifact technique produced an explicit **Decision** — adopting the ambient, zero-tap push notification as "the concrete north-star artifact for v2's future interaction model." The PRD does *not* silently drop this: FR-7 explicitly calls it out as "Out of Scope: Ambient/push notification delivery of the Status — deferred to a later version; dashboard display is the v2 mechanism (see Open Question 2)," and Open Question 2 asks about delivery channel and prioritization. So this is a legitimate, traceable PM scoping call (dashboard-first for v2, ambient notification deferred) rather than a lost idea — flagged here for completeness since it was the brainstorm's climactic decision, but it does not count as a gap.

## Part 3 — Everything else cross-checked clean

Systematically walked every Idea/Decision/Insight in the brainstorm HTML (all 4 techniques + convergence + synthesis) against the PRD; besides the two items above, everything else lands correctly:
- Crown jewel kill (Decomposition/Residual) → correctly reflected as a Non-Goal and in the Room→Power Point→Device "explicitly not an attribution system" glossary note.
- Tariff optimization as first-class, bonus-decay normalization, green/red both-with/without-bonus signal → FR-10–FR-14, matches precisely.
- Smart Plug correction (feeds Pattern Detective, not decomposition) → FR-4/FR-5 and Glossary.
- Context Capture scoped to induction cooktop / bathroom water heater → §4.3 description, literal match.
- Market-percentile tariff ranking as deliberate long-run deferral → §5 Non-Goals, matches exactly.
- Extensible Platform's three scoped points, voice as an input modality on point one rather than a separate feature → FR-19–21, FR-19 explicitly states "This is the seam voice input plugs into later," a near-verbatim callback.
- Tenant/no-smart-meter/irregular 1-2 day cadence foundation → Vision §1 and FR-3's gap-tolerant-by-construction rationale.
- Developer/self-hoster "reach spheres the spreadsheet can't" insight → §2.1 Social JTBD and §4.4 Extensible Platform description ("reach further than a spreadsheet"), matches almost verbatim.
- Voice-mode input as Could-have → §6.2 Out of Scope for MVP, consistent.
