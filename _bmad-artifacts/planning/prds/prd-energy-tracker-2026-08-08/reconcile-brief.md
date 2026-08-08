---
title: Brief-to-PRD Reconciliation — Energy Tracker v2
created: 2026-08-08
---

# Brief-to-PRD Reconciliation

Comparing `brief-energy-tracker-2026-08-08/brief.md` (source) against `prd-energy-tracker-2026-08-08/prd.md` and `addendum.md`.

## Method

Read all three documents in full. Checked every section of the brief (Executive Summary, The Problem, From v1 to v2, The Solution, What Makes This Different, Who This Serves, Success Criteria, Scope table, Vision) against the PRD's corresponding sections and the addendum, looking specifically for: requirements, nuances, constraints, "why," and scope boundaries that didn't make it across. Explicitly excluded from "gaps": deliberate re-scoping the PRD does openly (e.g. ambient notification pushed to Open Question 2 / Out of Scope, the entire brief "Should" tier folded into MVP scope) — these are visible, intentional decisions, not silent drops.

## Overall Assessment

The PRD is a faithful, unusually thorough operationalization of the brief. Every FR traces to brief content; the Glossary anchors terminology cleanly; MVP Scope, Non-Goals, and Success Metrics map almost one-to-one onto the brief's Scope table and Success Criteria (SM-1–SM-6 and SM-C1/C2 mirror the brief's Success Criteria section nearly verbatim). The "Should" tier (Tariff Radar, Context Capture, Room→Power Point→Device, Export/Import) was deliberately promoted into MVP rather than left as "ships close behind" — a defensible, visible re-scoping, not a gap. Design rationale that the brief states as narrative (e.g. "not bolted on later") is frequently carried into FR consequence language nearly word-for-word (e.g. FR-3's "not as a patched-on exception"), which is a good sign of careful authoring rather than mechanical FR extraction.

The gaps below are genuine but narrow — mostly qualitative/tone content from "What Makes This Different" and "Who This Serves" that the FR structure doesn't have a natural home for, plus a couple of scope-boundary statements the brief makes explicitly that the PRD never restates.

## Gaps Found

### 1. "It says less, on purpose" — the discipline itself isn't stated as a principle anywhere in the PRD

The brief calls this out as one of four core differentiators and flags it explicitly as fragile: *"That's a harder discipline to hold onto than it sounds, especially once a drill-down view exists and it's tempting to make it the headline."* This is exactly the kind of self-aware design constraint that should survive into a PRD as guidance for downstream UX/architecture work — something like an explicit guardrail: "the drill-down view (FR-8, FR-9) must never become something a user needs to check to trust the headline Status."

In the PRD, this shows up only indirectly, as a *metric* (SM-C2: "Drill-down engagement/time-in-app — more drill-down usage is not a win"). A counter-metric measures the failure after the fact; it doesn't instruct UX/architecture to design against the failure in the first place. Nothing in §4.1 (Pattern Detective), Constraints and Guardrails, or Non-Goals states the "say less" principle as a design constraint the dashboard/drill-down UI must honor. This is a real risk: a downstream UX pass could reasonably read FR-7/FR-8/FR-9 in isolation and build a richer drill-down than the brief intends, and nothing in the PRD would flag it as off-spec until the counter-metric shows up in hindsight.

**Recommendation:** Add an explicit line to Constraints and Guardrails (or a new "Design Principles" subsection) capturing this discipline directly, not just its failure-mode metric.

### 2. Self-hoster onboarding via documentation — dropped entirely

The brief's "Who This Serves" section states: *"documentation that treats 'I found this on GitHub' as a real onboarding path, not an afterthought to a single-user tool."* This is a concrete expectation about a deliverable (self-hoster-facing setup documentation) tied directly to the Secondary persona and to SM-5 (external adoption).

The PRD's SM-5 ("at least one other self-hoster runs it against their own household without forking/hardcoding") measures the *outcome* this documentation is meant to produce, but nothing in Features, Cross-Cutting NFRs, or Constraints requires or even mentions documentation as a deliverable. It isn't in MVP Scope, isn't in Non-Goals (so it's not deliberately excluded either) — it simply isn't addressed. Given SM-5 depends on it, this looks like an accidental drop rather than a deliberate cut.

**Recommendation:** Either add a lightweight NFR/requirement for self-hoster-facing setup documentation, or explicitly note it as an assumption/dependency of SM-5 in §9.

### 3. "No carried-over data" / v1-is-not-migrated — not restated as a PRD scope boundary

The brief is explicit: *"v2 is a ground-up rebuild, not a migration: new architecture, new deployment model, no carried-over data — v1 will eventually be retired."* This is a clear scope boundary (no v1→v2 data migration path) with real implications for FR-22/FR-23 (Data Export/Import) — a reader could otherwise wonder whether "Full Data Import" (FR-23) is meant to cover importing legacy v1 exports.

The PRD never states this. It isn't in Non-Goals, isn't in the Assumptions Index, and FR-23's scope ("import a previously exported dataset to restore or migrate an instance") is ambiguous enough that "migrate an instance" could be misread as covering v1→v2 migration, which the brief explicitly rules out.

**Recommendation:** Add a one-line Non-Goal ("Not a v1-to-v2 data migration tool — v2 starts from zero data by design") to remove the ambiguity in FR-23.

### 4. Persona's mental model ("kWh and euros, not raw voltage curves") — no corresponding UI/language guidance

The brief's Primary persona description specifies how the user thinks about their own data: *"They think in kWh and euros, not raw voltage curves."* This is a minor but real piece of UX guidance (avoid exposing raw electrical units/telemetry-style detail; keep everything framed in consumption and cost terms) that has no explicit landing spot in the PRD — not in JTBD (2.1), not in Cross-Cutting NFRs, not in Constraints. It's consistent with everything else in the PRD (nothing contradicts it) but isn't stated anywhere for the UX phase to pick up directly.

**Recommendation:** Low priority — likely fine to leave implicit given how consistently kWh/currency framing is used throughout the PRD's own FRs, but worth a one-line mention in §2.1 if the UX spec wants an explicit anchor.

## Non-Gaps (checked, confirmed present)

- Zero-tap ambient notification: correctly and visibly deferred (FR-7 Out of Scope, Open Question 2) — intentional re-scoping, not a drop.
- "Honest about what it can't measure precisely": well covered (FR-9, FR-17 consequences, Non-Goals room-audit bullet).
- "Your data stays yours" / occupancy-proxy privacy framing: carried near-verbatim into Constraints and Guardrails.
- "Built for the no-smart-meter case, not around it": carried into PRD §1 Vision and FR-3's gap-tolerance-by-construction framing.
- Brief's full Must/Should/Could/Won't scope table: every item accounted for in MVP Scope §6 or Non-Goals §5, including the deliberate promotion of "Should" items into MVP.
- Success Criteria (primary, secondary, counter-metrics): map essentially 1:1 to PRD §7 Success Metrics SM-1 through SM-C2.
- Extensible Platform scoping ("not a plugin marketplace," voice input as future seam via FR-19): faithfully carried.
- Addendum content (threshold profiles idea, candidate Azure deployment shape): correctly kept out of the PRD body as implementation-level/deferred detail, not brief content that needed to be in the PRD.
