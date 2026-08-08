# PRD Quality Review — Energy Tracker v2

## Overall verdict

This is a strong, well-earned PRD: it has a real thesis (gap-tolerant baseline as the answer to the no-smart-meter problem), features that follow from it, and testable FRs that would let an engineer start work without guessing. The main risks are mechanical rather than conceptual — a documented `[ASSUMPTION]` tagging mechanism that isn't actually used inline, two FRs (trend/drill-down views) that are thinner than the rest, and small terminology drift around who the "actor" of a Household-scoped FR is.

## Decision-readiness — strong

Trade-offs are named, not smoothed. §6.1 states plainly: "If a build-time cut becomes necessary, the Should tier is what flexes — Pattern Detective and Data Export/Import are the non-negotiable v2 core." FR-15 carries an explicit sequencing decision ("*Ships after FR-10 through FR-14 are stable — sequenced, not day-one.*"). Open Question 1 is a genuinely open, unresolved tension (FR-20's feasibility) rather than a rhetorical question answered in the next breath, and it carries a live `[NOTE FOR PM]` at §4.4 FR-20 flagging the feature "may be dropped entirely."

No findings — this dimension holds up.

## Substance over theater — strong

No persona bloat (one primary protagonist, Sam, plus a lightly-sketched secondary Self-Hoster persona that actually drives SM-5 and the i18n NFR). NFRs are concrete, not boilerplate: "≤2s" / "≤30s" tiers (Cross-Cutting NFRs), a default trending threshold of "~100 kWh over baseline pace-to-date" (FR-6), fixed-decimal currency handling. The Vision (§1) names specific mechanisms (gap-tolerant rolling baseline, Bonus-Decay Normalization, Wattage Plausibility) that couldn't swap into an unrelated PRD unchanged.

No findings — this dimension holds up.

## Strategic coherence — strong

The thesis in §1 ("is your consumption on track, and is your tariff still worth staying on") is directly traceable through MVP prioritization (Pattern Detective = Must, Tariff Radar/Context Capture = Should) and Success Metrics. SM-1 through SM-4 are behavioral, not activity metrics (habit retention, early trend catch, confident decision — not DAU/MAU), and counter-metrics are named (SM-C1 insight volume, SM-C2 drill-down engagement) with an explicit product-discipline constraint backing SM-C2 (§Constraints: "the drill-down views... existing is fine, but... never make checking the drill-down a precondition for trusting the Status").

No findings — this dimension holds up.

### Findings
- **low** MVP scope item without FR anchor (§6.1) — "Room → Power Point → Device tagging scaffold" is listed as a Must alongside FR-numbered items but has no FR ID of its own; it's a structural dependency of FR-4/FR-9 rather than a requirement in its own right. *Fix:* either give it an FR ID or explicitly note it as "supporting structure for FR-4/FR-9, not a standalone FR."

## Done-ness clarity — adequate

Most FRs are unforgiving in the good sense — FR-1, FR-3, FR-4, FR-6, FR-24, FR-25 all carry consequences with real bounds (timestamps, caps, confirmation flows). Two FRs are noticeably thinner than their neighbors:

### Findings
- **medium** FR-8 Trend History View underspecified (§4.1, line ~161) — the sole consequence is "The view shows trend, not just the current point-in-time Status." No time granularity (daily/weekly?), no window (last month? all history?), nothing an engineer could build a done-check against. Contrast with FR-3 or FR-6 in the same section, which have 2+ concrete, checkable consequences. *Fix:* add at least one bound — e.g. minimum time range shown, what "trend" renders as (chart vs. sparkline vs. table).
- **low** FR-9 Per-Plug Measured Data View has one consequence that is a scope disclaimer, not a testable behavior (§4.1, line ~168) — "explicitly presented as measured context, not a reconciled attribution/Residual breakdown" describes what the feature *isn't*, not a verifiable condition for what it *does*. *Fix:* add one consequence describing what the view actually shows (e.g. per-Power-Point interval data over a selectable range).

## Scope honesty — strong

§5 Non-Goals is substantive (9 items, each with a one-line reason, e.g. "Not a market-percentile tariff ranking service — needs a third-party data feed; deliberate long-run deferral, not dismissed"). §9 Assumptions Index has 6 entries, sized appropriately for a green-light PRD of this scope. `[NOTE FOR PM]` appears at the one genuinely unresolved technical-feasibility tension (FR-20). De-scoping is explicit throughout (§6.2 Out of Scope for MVP lists FR-19–21, voice input, push notifications by name with reasons).

See Mechanical notes below for a roundtrip defect that undercuts this dimension slightly: the inline `[ASSUMPTION]` tags §9 depends on are not actually present in the body.

## Downstream usability — strong

Glossary (§3) is thorough and the 22 terms it defines are used consistently in Features, FRs, and UJs — a genuine source of extractable vocabulary for UX/architecture. UJs (§2.3) each have a named protagonist (Sam) carrying context inline rather than floating. FR numbering is contiguous within each feature section except for one gap (see Mechanical notes).

### Findings
- **low** Actor inconsistency between "Household" and "Household member" (§4.1 FR-2, §4.2 FR-10, FR-22/23 vs. §4.1 FR-1, §4.3 FR-16) — the Glossary (§3) defines Household as "the top-level entity everything is scoped to," not an actor, yet FR-2 ("A Household can set..."), FR-10 ("A Household can enter and maintain..."), FR-22/23 ("A Household can export/import...") use it as the grammatical subject performing UI actions, while FR-1 and FR-16 correctly use "A Household member can...". Downstream story-writing will need to resolve who actually clicks the button. *Fix:* standardize on "A Household member" as the actor throughout, reserving "Household" for the scoping/ownership noun.

## Shape fit — strong

This is a household/consumer self-hosted tool with a single-operator-per-household shape — three named UJs with a carried protagonist is the right amount of formalization, not over-built. Brownfield awareness is handled correctly: §5 explicitly separates v1 from v2 ("v2 is a ground-up rebuild, not a migration... a v1 instance is retired, not upgraded in place"), and FR-4's Eve Home/Meross parsing behaviors are the only place existing (v1) behavior is carried forward, correctly flagged as such and pushed to the addendum for schema detail.

No findings — this dimension holds up.

## Mechanical notes

- **`[ASSUMPTION]` mechanism is declared but not used inline (broken roundtrip).** §0 Document Purpose states: "Inline `[ASSUMPTION]` tags mark places where this document infers rather than confirms; all are indexed in §9." A full-text check of the PRD body found zero inline `[ASSUMPTION]` tags — the six entries in §9 (covering §3 Yearly Baseline, §4.1 FR-2, §4.2 FR-10, §4.2 FR-15, §Constraints) have no corresponding inline marker at those locations. The index is populated but the roundtrip back to the body is missing entirely, so a reader scanning the PRD linearly has no way to spot an inference in place — they'd only find it by reading §9 first. *Fix:* either add the inline tags at the six cited locations, or update §0 to describe the mechanism as it's actually used (index-only, not inline).
- **FR-24/FR-25 break numeric contiguity within §4.1.** Pattern Detective's FRs run 1–9, then jump to 24–25 at the end of the same section (§4.1, after FR-9), while FR-10 through FR-23 belong to later sections (Tariff Radar, Context Capture, Extensible Platform, Data Export/Import). This is very likely a "added after initial numbering, appended rather than renumbered" artifact — harmless for uniqueness but a reader scanning by ID range would assume FR-24/25 belong to Data Export/Import (FR-22/23's neighbor), not Pattern Detective. No broken references found downstream. Low risk, cosmetic.
- **Glossary gap: "Residual"** is used in FR-9's consequence ("not a reconciled attribution/Residual breakdown of the Main Meter total") but is not a defined term in §3 Glossary. Minor, single occurrence, but §0 promises "every term used in Features [or] FRs... is defined [in the Glossary] once" — this is the one term that slipped through.
- No other glossary drift found; case/plural usage of defined terms (Meter Reading, Status, Tariff, Contract Period, etc.) is consistent across Features, FRs, and UJs.
- SM and UJ ID sequences are both contiguous and fully cross-referenced (every SM cites the FRs it validates; every UJ is realized by name in at least one FR).
