---
title: Adversarial Review — Energy Tracker v2 PRD
reviewed: prd.md (prd-energy-tracker-2026-08-08)
context-only: addendum.md
review-date: 2026-08-08
---

# Adversarial Review: Energy Tracker v2 PRD

This document is well-organized and clearly enjoys its own discipline — Glossary-anchored terms, stable FR IDs, an Assumptions Index. That polish makes it easy to skim past what it doesn't actually commit to. Below is what falls apart on closer inspection.

## Findings

- **The primary success metrics are unmeasurable by the product's own design.** SM-1 (spreadsheet retirement), SM-2 (reading cadence retention), SM-3 (early trend catch), and SM-4 (confident tariff decision) all require observing real household behavior over time — but the Constraints section mandates "no telemetry/analytics phone-home by default" for a self-hosted product. There is no mechanism proposed anywhere (opt-in survey, anonymized aggregate reporting, anything) for the PM to ever know whether these metrics were hit. As written, the "Primary" success metrics are aspirational narrative, not something anyone will check off.

- **SM-3 and SM-4 are set to pass on a single anecdote.** "At least one real instance" and "at least once" are trivially satisfiable and trivially unfalsifiable — there's no volume, rate, or time-bound, so the metric can't distinguish a product that works from one that got lucky once. Combined with the measurability problem above, these read more as vibes than success criteria.

- **Vision claims "generic by default" but the feature set is hardcoded to two named vendors.** §1 states v2 is "generic by default, so other self-hosters ... can run it against their own flat without forking the code." FR-4 supports exactly Eve Home and Meross exports. FR-20 (generic column mapping — the actual generic mechanism) is Could-have, explicitly low-confidence, and the PM's own note says it "may be dropped entirely." A self-hoster with a different smart-plug brand gets zero support on day one and no committed path to add one without forking. The vision statement is not backed by MVP scope.

- **Tariff model (FR-10: base fee + price/kWh) is a German billing shape wearing an "en-US launch locale" costume.** Flat base fee plus flat €/kWh is the standard German (Grundpreis/Arbeitspreis) structure. US electricity billing commonly involves tiered rates, time-of-use pricing, and utility-specific fee structures that don't fit this model. Declaring `en-US` a launch Locale implies real usability for US households, but Locale per the NFR is "display formatting" only — the underlying Tariff data model isn't shown to generalize past the German case. Either the locale claim is overstated or the tariff model is under-specified.

- **Single "Main Meter" assumption conflicts with common household setups in the PRD's own primary launch market.** Glossary defines Main Meter as "the single physical utility meter for a Household." Dual-tariff meters (day/night, `Doppeltarifzähler`) and separate heat-pump meters are common in German households — the exact locale the product launches in first. No FR or open question acknowledges multi-meter households; the data model appears to assume they don't exist.

- **No FR anywhere covers Household/account creation.** Every journey (UJ-1–3) starts from "already authenticated." FR-2 references "during onboarding" as if onboarding is already a defined flow, but no FR defines how a Household is created, how the first user is provisioned under a swappable OIDC provider, or how subsequent Household members are invited. For a Must-have, self-deploy-only product aimed partly at non-expert self-hosters, this is a load-bearing gap, not a nice-to-have.

- **FR-17's Wattage Plausibility only handles the "bump" case, but the flagship example event doesn't produce one.** FR-16's canonical example, "away 2 weeks," should produce a *dip* in consumption, not a bump — yet FR-17's Consequences describe correlation only against "the consumption bump Pattern Detective observed." There's no stated behavior for negative-consumption events, which is a real gap given the PRD's own example.

- **The "away 2 weeks" example also implies a duration the data model doesn't obviously support.** FR-16 describes Events as backfillable to "a past date/time," matching Glossary's Event definition, but nothing indicates a start/end range field. A single-timestamp Event can't cleanly represent a two-week absence for correlation purposes — is it logged as a single point, and if so, correlated against what window?

- **FR-14's claim that Tariff Savings Radar and Pattern Detective "share" Bonus-Decay Normalization math is asserted, not explained.** One is a financial bonus-decay curve (currency over contract time), the other is a consumption-pace trending threshold (kWh over calendar time). The PRD states they use "the same underlying normalization logic" as a testable consequence but never describes what mathematical structure is actually common between a money curve and a usage-rate threshold. This reads like an architectural aspiration stated as an already-decided fact.

- **Cold-start behavior for Pattern Detective and Tariff Radar is unspecified.** FR-3's rate computation needs at least two Meter Readings; FR-12 requires "actual household consumption pace from Pattern Detective." Day one, with zero or one reading, there is no defined Status and no defined Radar behavior — not even a stated fallback/empty state, unlike the tariff-reminder "no comparison configured" case which *is* explicitly handled (FR-15).

- **AI-assisted Wattage Plausibility's data-egress model is left ambiguous against the PRD's own privacy stance.** Constraints explicitly frame consumption data as sensitive ("a fairly direct proxy for occupancy patterns") and commit to no phone-home by default. FR-17 is "AI-assisted" with no statement of whether this AI runs locally or calls a third-party API. If it's a hosted LLM API, sending occupancy-correlated data externally when the feature is enabled directly contradicts the privacy framing; the PRD never resolves this either way.

- **No FR governs CRUD for the Room → Power Point → Device scaffold, despite it being a named Must-have.** §6.1 lists "Room → Power Point → Device tagging scaffold" as MVP-required, but no functional requirement anywhere defines creating, editing, deleting, or validating Rooms/Power Points/Devices — they only appear as tagging targets inside FR-4, FR-9, and FR-16. Downstream epic breakdown has nothing to reference for this Must-have.

- **Meter-reading regression handling (FR-25) conflates two different real-world causes into one confirmation dialog.** A lower reading can mean a meter replacement/reset, or it can mean digit rollover on an analog meter (e.g., wraps from 99999 to 00000) — the two require different handling for correct downstream consumption math (reset means new sequence from zero; rollover means the delta is still computable, just not via naive subtraction). FR-25 treats both as "meter replaced or reset?" without distinguishing them.

- **Import merge semantics for FR-23 (Full Data Import) are unstated.** The consequence describes rejecting malformed data wholesale, but not what happens when importing into an instance that already has data — silent overwrite, hard reject on non-empty target, or merge? "Restore a v2 instance or move it to new hosting" implies overwrite, but that's inferred, not stated, and destructive-by-default import behavior deserves an explicit call-out given the disaster-recovery framing.

- **No data-volume/retention NFR despite explicitly targeting low-power hardware.** Cross-Cutting NFRs commit to ≤2s dashboard loads on "a low-power NAS/single-board-computer class device," but Smart Plug data arrives at ~10-minute intervals (FR-4/addendum) with no stated retention policy or aggregation strategy. Multi-year data on constrained hardware is a real performance risk the NFR section doesn't acknowledge, let alone bound.

- **No FR or NFR addresses offline/PWA behavior despite the core journey needing it.** UJ-1 is explicitly a phone-in-hand, standing-at-the-physical-meter workflow — meter cupboards and basements are exactly where household WiFi/cellular coverage is worst. The "under a minute, don't break the streak" promise (FR-1) is highest-risk precisely where connectivity is weakest, yet there's no mention of offline capture, local queueing, or installability anywhere in the document.

- **GDPR/data-subject-rights posture is asserted by omission, not addressed.** The Constraints section explicitly flags energy data as sensitive occupancy-proxy data and commits to no telemetry — but a Household concept that "can technically hold more than one" user, self-hosted potentially by someone other than the data subjects, has real GDPR obligations (erasure, portability beyond bulk export, access control between members) that the PRD doesn't touch. Export (FR-22) covers portability; nothing covers erasure or member-level access rights.

- **"Confirmed" assumptions in §9 are self-confirmed.** Every entry in the Assumptions Index is marked "Confirmed," but §0 states these were "confirmed during PRD authoring" — i.e., by the same author who wrote the assumption. There's no visible external stakeholder or user validation behind any of the six confirmations; the label implies more rigor than the process described supports.

- **Household-size baseline presets (FR-2) are unsourced.** "Typical figures from tariff-comparison sites" backs four specific kWh numbers (1500/2500/3500/4250) that directly seed the Yearly Baseline default — the number the entire Pattern Detective Status hinges on for a new household. No citation, no locale-scoping (are these German household norms being applied to en-US households too?), just an appeal to unnamed authority.

- **Tariff Check Reminder gating assumes contracts have a hard end date; many don't.** FR-15 gates on "3 months before the current Contract Period ends." Glossary defines Contract Period as a minimum *duration*, not an end date — many real-world energy contracts auto-continue on a rolling/cancellable basis after the minimum period rather than terminating. The reminder model implicitly assumes a fixed-term contract that ends, which may not match the tariffs households actually hold.

## Also worth a skeptical eyebrow

- FR-13's "two-way attractiveness signal" is framed as a meaningful double-check, but stripping a bonus should never make a comparison *more* favorable — the two signals can only diverge in one direction (bonus-green / honest-red). The "shown twice" framing oversells how much new information the second signal actually carries versus just showing the honest one with a bonus-inflated figure as a footnote.
- The FR numbering sequence (1–9, then a jump to 24–25, then back to 10–18, then 19–21, then 22–23) signals requirements were added after initial drafting without renumbering. Harmless under the "stable ID" promise, but worth flagging as a smell for anyone assuming the document was written in the order it reads.
