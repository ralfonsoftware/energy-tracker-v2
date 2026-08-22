---
stepsCompleted: [1, 2, 3, 4, 5, 6]
filesIncluded:
  prd: '_bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/ (sharded, index.md + 12 section files)'
  architecture: '_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/ (sharded, index.md + 7 section files)'
  epics: '_bmad-artifacts/planning/epics/ (index.md + epic-list.md + epic-1..7 + requirements-inventory.md)'
  ux: '_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/ (EXPERIENCE.md + DESIGN/ + mockups/ + review docs)'
---

# Implementation Readiness Assessment Report

**Date:** 2026-08-22
**Project:** energy-tracker

## Step 1: Document Discovery

### PRD Files Found

**Sharded:** `prds/prd-energy-tracker-2026-08-08/prd/`
- `index.md`, `0-document-purpose.md`, `1-vision.md`, `2-target-user.md`, `3-glossary.md`, `4-features.md`, `5-non-goals-explicit.md`, `6-mvp-scope.md`, `7-success-metrics.md`, `8-open-questions.md`, `9-assumptions-index.md`, `constraints-and-guardrails.md`, `cross-cutting-nfrs.md`
- No whole-document duplicate found. `cross-cutting-nfrs.md` last modified 2026-08-22 (today's correct-course amendment).
- Sibling files in the parent `prd-energy-tracker-2026-08-08/` folder (`addendum.md`, `reconcile-*.md`, `review-*.md`, `.memlog.md`) are process/reconciliation artifacts, not part of the PRD itself — excluded from this assessment.

### Architecture Files Found

**Sharded:** `architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/`
- `index.md`, `capability-architecture-map.md`, `consistency-conventions.md`, `deferred.md`, `design-paradigm.md`, `invariants-rules.md`, `stack.md`, `structural-seed.md`
- No whole-document duplicate found. `invariants-rules.md` last modified 2026-08-22 (today's AD-20 addition).

### Epics & Stories Files Found

**Sharded:** `epics/`
- `index.md`, `epic-list.md`, `epic-1-foundation-deployment-household-access.md`, `epic-2-meter-reading-pattern-detective-status-core.md`, `epic-3-smart-plug-import-baseline-sharpening.md`, `epic-4-trend-history-per-plug-insight.md`, `epic-5-tariff-savings-radar.md`, `epic-6-context-capture-wattage-plausibility.md`, `epic-7-data-export-import-disaster-recovery.md`, `requirements-inventory.md`
- No whole-document duplicate found. `epic-3-...md` and `requirements-inventory.md` last modified 2026-08-22 (today's Story 3.4 / AD-20 additions).
- Implementation-level story files (per-story detail, distinct from these epic-level definitions) live separately under `_bmad-artifacts/implementation/*.md`, tracked via `_bmad-artifacts/implementation/sprint-status.yaml`.

### UX Design Files Found

**Sharded:** `ux-designs/ux-energy-tracker-2026-08-08/`
- `EXPERIENCE.md`, `DESIGN/` (index.md, brand-style, colors, components, dos-and-donts, elevation-depth, layout-spacing, shapes, typography), `mockups/*.html`, plus review/validation artifacts.
- No whole-document duplicate found.

### Issues Found

- **Duplicates:** None — every document type has exactly one sharded source, no competing whole-document version.
- **Missing documents:** None — PRD, Architecture, Epics, and UX are all present.

No critical issues to resolve before proceeding.

## PRD Analysis

### Functional Requirements

FR-1: Meter Reading Entry — enter a Meter Reading (kWh + timestamp, today pre-selected/editable), save with one confirmation tap. Sub-1-minute default path; multiple same-day readings accepted with differing timestamps; backfilled/earlier timestamps accepted.

FR-2: Yearly Baseline Configuration — set/edit the Household's Yearly Baseline (kWh/year) at onboarding and from settings. Household-size presets (1p≈1500…4p≈4250 kWh) offered as suggestions, never silently applied; edits never retroactively rewrite past Status history.

FR-3: Gap-Tolerant Rolling Baseline Computation — compute expected pace from the Reading sequence regardless of interval length. Multi-day gaps absorbed into the rate, not a break/reset; pace compares like-for-like to baseline; unusually long gaps flagged low-confidence.

FR-4: Smart Plug File Import — upload Eve Home `.xlsx`/Meross `.csv`; parse and associate with the tagged Power Point. Async with completion notification; unmatched Power Point prompts creation/mapping; Eve Home timestamps stay local time, never UTC-converted; Meross identity from filename pattern, not in-file metadata.

FR-5: Baseline Sharpening from Smart Plug Signal — imported data refines Status as an additional signal. Zero-coverage households still get a fully functional Status from Readings alone; Smart Plug data never summed against/reconciled to the Main Meter total.

FR-6: Status Computation — single Status (within range / below baseline / trending) from pace vs Yearly Baseline + threshold. "Trending" fires past threshold (default ~100 kWh); recomputes on every new Reading/completed import, never a fixed schedule alone; undefined (not defaulted) below two Readings or no Baseline; exact tie resolves to "within range."

FR-7: Dashboard Status Display — main dashboard shows current Status as the primary glanceable element. Visible without scrolling/drilling on first load; no chart required; first-ever load with no computable Status shows an onboarding prompt. Out of scope: ambient/push notification delivery.

FR-8: Trend History View — view historical Status/pace trend. Shows trend, not just current value; Reading-history gaps render as a visible break, never interpolated (distinct from FR-24's Smart-Plug interpolation).

FR-9: Per-Plug Measured Data View — view measured Smart Plug data by Room → Power Point → Device. Explicitly measured context, never a reconciled attribution breakdown; retagging after import leaves previously-imported data attributed to the tag active at import time.

FR-10: Tariff Configuration — maintain current Tariff (base fee, price/kWh, currency, contract start date, Contract Period). History retained, never overwritten; locked fields need explicit override to edit; flat rate only; currency required per entry, never hardcoded.

FR-11: Candidate Tariff Comparison Entry — enter a candidate tariff to compare. Scratch/exploratory, never alters the actual current Tariff until an explicit switch.

FR-12: Bonus-Decay Normalized Savings Projection — project annual savings vs current Tariff, Bonus-Decay-normalized. Uses actual household pace, never a generic figure; pre-usable-pace comparison shows the same onboarding empty state as FR-6/FR-7.

FR-13: Two-Way Attractiveness Signal — green/red signal shown twice, with and without Switching Bonus. Both shown together, never toggled; exact breakeven resolves to red/not-worth-switching.

FR-14: Shared Bonus-Decay Math with Pattern Detective — one shared normalization logic; a formula change affects both features identically.

FR-15: Tariff Check Reminder — *(sequenced after FR-10–14)* proactive prompt gated to ≤3 months before Contract Period end, then a customizable recurring cadence. No reminder while >3 months remain; default 3-month cadence, editable; none if no Tariff configured; gate/cadence recompute on contract-date edits.

FR-16: Event Logging — log an Event via short text/tap-first entry, optional Room/Power Point/Device tag. Comparable effort to FR-1, deliberately low-friction; backfillable; deleted tagged item leaves the Event's tag as inert text. Out of scope: recurring/pattern events.

FR-17: Wattage Plausibility Correlation — rough plausibility correlation between a logged Event and an observed consumption deviation. Rough/approximate, never false precision; direction inferred from the Event; no-deviation Events shown without correlation, never flagged wrong; many-to-one mapping; AI backend is a Household-level config choice, always visible; optional/gracefully degradable.

FR-18: Proactive Weekly Recap — *(optional, after FR-16/17 stable)* proactive "anything unusual this week?" prompt threaded onto a flagged spike as an Event. Opt-in, off by default; fires only on an actual detected spike/trending Status; reply becomes a normal Event; one prompt per episode; suppressed if already explained; a confirmed FR-25 reset is not itself a spike.

FR-19: Custom Event/Plausibility Rules — *(Could-have)* define custom Event-to-Plausibility correlation rules. The seam future voice input plugs into.

FR-20: Generic Data-Source Column Mapping — *(Could-have, low confidence, Open Question 1)* map a new Smart Plug export format's columns without a code change. Scoped to tabular/mappable formats; feasibility must be evaluated before committing effort; may be dropped entirely.

FR-21: Tunable Threshold/Spike Settings — *(Could-have)* adjust threshold/spike-detection settings beyond FR-6's single number, via product UI/settings, never config-file editing.

FR-22: Full Data Export — export all of a Household's data in a documented format for disaster-recovery backup.

FR-23: Full Data Import — import a previously exported v2 dataset to restore/migrate an instance. Validates and rejects/reports malformed data rather than partially applying it; v2-to-v2 only, no v1 conversion; import into a Household with existing data is blocked by default, requiring explicit "replace all data" confirmation — no partial-merge mode.

FR-24: Smart-Plug Import Gap Handling — detect gaps within an import's covered range without treating them as zero consumption. Missing date = Gap; 0 kWh = valid data point, not a Gap; gap-fill values bounded (e.g. capped at preceding week's average) and visibly flagged as interpolated; a gap at a household's very first import is left unfilled/flagged missing; an entirely-gaps import is flagged for review, not wholesale-interpolated.

FR-25: Meter Reading Regression Detection — a Reading lower than its chronological predecessor is flagged and classified as *reset* or *rollover*, never fed as a negative rate into FR-3. *Reset* starts a new baseline sequence without discarding history; *rollover* computes (digit capacity − previous) + new; "immediately preceding" is timestamp order, not entry order; an unconfirmed regression excludes that Reading from baseline computation and never silently expires/defaults; a second regression while one is open queues behind it.

FR-26: Household Provisioning — the first visitor to a fresh deployment creates the Household and becomes its first member via OIDC. Routes any authenticated visitor into creation rather than a broken/empty dashboard; no second party/invite code/manual DB step required.

FR-27: Household Member Invitation — an existing member invites additional members. All members have equal, full access — no separate admin/owner role.

FR-28: Room / Power Point / Device Management — create/edit/delete Rooms, Power Points, Devices used across FR-4/FR-9/FR-16. Deleting an item with tagged historical data orphans/leaves it reassignable rather than cascade-deleting.

FR-29 *(deferred — not yet scheduled to an epic/story)*: Structure Editor Archived-Item Visibility Toggle — a show/hide toggle for archived Rooms/Power Points/Devices in the structure editor (today they stay always-visible with an "Archived" badge). View filter only — never changes the underlying soft-delete/reassignment behavior (FR-28, AD-10).

**⚠️ Note:** FR-29 exists only in `epics/requirements-inventory.md` — it is **not present in the actual PRD source** (`prd/4-features.md`). Flagged for Step 3/coverage validation and the gap-analysis step.

**Total FRs: 29** (FR-1 through FR-28 in the PRD proper; FR-29 in requirements-inventory.md only — see note above)

### Non-Functional Requirements

NFR1: Performance tiers — Tier 1 (FR-1, FR-7) ≤2s; Tier 2 ≤30s with progress hint; Tier 3 (Smart Plug imports, scheduled jobs) fully async with completion notification.

NFR2: Hosting cost-efficiency — one deployment artifact runs comfortably on modest self-hosted hardware and low-tier scale-to-zero cloud, no separate "cloud edition."

NFR3: Auth — every route authenticated; OIDC provider swappable via config only; no unauthenticated endpoints except the OIDC callback.

NFR4: Tenant isolation — all data isolated per Household, enforced at the data-access layer, not just the UI.

NFR5: i18n/locale — no hardcoded locale-specific strings/formats; data stored locale-neutral; background jobs run UTC regardless of display Locale. Launch Locales `de-DE`/`en-US`; new Locales are a resource addition, not a code change.

NFR6: Currency handling — monetary values always fixed-decimal; currency a Household-level config field, not hardcoded to EUR.

NFR7: Offline capture — Meter Reading entry (FR-1) queues locally and syncs on reconnect; the core habit never depends on live connectivity.

NFR8: Audit trail on corrections — editing a Meter Reading or Tariff entry preserves the original value as a visible correction note, never a silent overwrite.

NFR9: Recomputation policy — config-input edits affect calculations going forward only; historical computed values are never silently rewritten, unless an FR states an explicit exception.

NFR10: Data integrity under concurrent and repeated writes — concurrent writes never silently lose an update; a write path that can legitimately receive the same logical data more than once (e.g. an overlapping Smart Plug re-import) never silently duplicates it either. *(Broadened 2026-08-22 via correct-course — previously concurrency-only.)*

NFR11: Documentation as onboarding path — setup/config docs make "found this on GitHub" a real onboarding path with no direct support channel required (load-bearing for SM-5).

NFR12: Privacy — self-hosted by default, no third-party account required; no telemetry/analytics phone-home by default.

NFR13: Data ownership — a Household's data is always exportable in a documented format (FR-22); no vendor lock-in.

NFR14: Cost — no paid third-party service required for a basic self-hosted instance; optional integrations degrade gracefully.

NFR15: Product discipline ("says less, on purpose") — the dashboard Status is the headline; drill-down views existing is fine, but never a precondition for trusting the Status.

**Total NFRs: 15**

### Additional Requirements

- **Non-Goals (§5):** not real-time monitoring; not hosted/managed; not a room-by-room audit tool; not native mobile; not a cross-user admin platform; not a market-percentile tariff ranking service; not a plugin marketplace; not general life-logging; AI features never a hard dependency; not a general (tiered/time-of-use) billing engine; not multi-meter UI/logic in v2 (data model allows it, UI doesn't use it); no v1→v2 migration path.
- **MVP Scope (§6):** Must = Household & Access (FR-26–28), Pattern Detective (FR-1–9, FR-24–25), Data Export/Import (FR-22–23). Should = Tariff Savings Radar (FR-10–15), Context Capture (FR-16–18) — the tier that flexes under a build-time cut. Out of MVP: FR-19–21 (Extension Points), voice input, ambient/push notification, market-percentile ranking, native mobile, hosted offering, cross-user admin, real-time monitoring.
- **Success Metrics (§7):** SM-1–SM-6 primary/secondary, plus counter-metrics SM-C1 (insight/notification volume) and SM-C2 (drill-down engagement) that deliberately should NOT be optimized upward.
- **Open Questions (§8):** (1) FR-20 feasibility — evaluate before committing effort, may be dropped; (2) notification delivery channel for FR-7's deferred ambient/push delivery, and its relative priority; (3) whether Pattern Detective/Tariff Radar should eventually support multiple threshold profiles; (4) concrete target architecture/hosting shape (candidate captured in `addendum.md`, not yet locked at PRD time — since resolved by the Architecture phase).
- **Assumptions Index (§9):** 6 confirmed assumptions (Yearly Baseline distinct from reading history; Tariff Check Reminder in v2 scope; FR-2 presets reused from v1; FR-10 price-locking pattern reused from v1; FR-15's 3-month default cadence; FR-17 optionality/graceful-degradation).
- **Constraints & Guardrails:** Privacy (self-hosted default, no phone-home), Data ownership (always-exportable, no lock-in), Cost (no required paid third-party service), Product discipline ("says less, on purpose" — ties to SM-C2).
- **Architecture Decisions referenced by requirements-inventory.md (not the PRD itself, but binding on implementation):** AD-1 through AD-20 (see Architecture Analysis step for full coverage check) plus a Consistency Conventions block (naming, timestamp/money formats, error shape, config-selection discipline).

### PRD Completeness Assessment

The PRD is well-structured and internally consistent: every FR/NFR carries testable consequences, terminology is Glossary-anchored with no synonym drift observed across documents, and Non-Goals/MVP Scope/Success Metrics all cross-reference FR IDs rather than restating requirements in prose. Four Open Questions remain genuinely open (§8) but are appropriately deferred (feasibility spike, notification-channel decision, a "revisit once there's usage signal" item, and the hosting-architecture question already resolved downstream by the Architecture Spine).

One real gap: **FR-29 was added directly to `requirements-inventory.md` without a corresponding update to the PRD's own `4-features.md`.** This mirrors the exact kind of drift the correct-course process just closed for NFR10/AD-20 (Story 3.4) — except in FR-29's case, nothing appears to have routed it back to the PRD source. Recommend either backfilling `4-features.md` with FR-29, or documenting explicitly (e.g. in `9-assumptions-index.md` or a new PRD addendum note) that `requirements-inventory.md` is allowed to carry FRs ahead of the PRD pending a batch sync — otherwise the PRD can no longer be treated as the single source of truth for FR numbering.

## Epic Coverage Validation

### Coverage Matrix

| FR | Requirement (short) | Epic Coverage | Status |
|---|---|---|---|
| FR-1 | Meter Reading Entry | Epic 2 | ✓ Covered |
| FR-2 | Yearly Baseline Configuration | Epic 2 | ✓ Covered |
| FR-3 | Gap-Tolerant Rolling Baseline Computation | Epic 2 | ✓ Covered |
| FR-4 | Smart Plug File Import | Epic 3 | ✓ Covered |
| FR-5 | Baseline Sharpening from Smart Plug Signal | Epic 3 | ✓ Covered |
| FR-6 | Status Computation | Epic 2 | ✓ Covered |
| FR-7 | Dashboard Status Display | Epic 2 | ✓ Covered |
| FR-8 | Trend History View | Epic 4 | ✓ Covered |
| FR-9 | Per-Plug Measured Data View | Epic 4 | ✓ Covered |
| FR-10 | Tariff Configuration | Epic 5 | ✓ Covered |
| FR-11 | Candidate Tariff Comparison Entry | Epic 5 | ✓ Covered |
| FR-12 | Bonus-Decay Normalized Savings Projection | Epic 5 | ✓ Covered |
| FR-13 | Two-Way Attractiveness Signal | Epic 5 | ✓ Covered |
| FR-14 | Shared Bonus-Decay Math with Pattern Detective | Epic 5 | ✓ Covered |
| FR-15 | Tariff Check Reminder | Epic 5 | ✓ Covered |
| FR-16 | Event Logging | Epic 6 | ✓ Covered |
| FR-17 | Wattage Plausibility Correlation | Epic 6 | ✓ Covered |
| FR-18 | Proactive Weekly Recap | *(none)* | ⚠️ Deferred — documented rationale in `epic-list.md` (no scheduler/notification-channel design exists yet; product-owner decision 2026-08-09) |
| FR-19 | Custom Event/Plausibility Rules | *(none)* | ⚠️ Deferred — documented (Could-have, out of MVP per PRD §6.2) |
| FR-20 | Generic Data-Source Column Mapping | *(none)* | ⚠️ Deferred — documented (Could-have, low-confidence per PRD Open Question 1) |
| FR-21 | Tunable Threshold/Spike Settings | *(none)* | ⚠️ Deferred — documented (Could-have, out of MVP per PRD §6.2) |
| FR-22 | Full Data Export | Epic 7 | ✓ Covered |
| FR-23 | Full Data Import | Epic 7 | ✓ Covered |
| FR-24 | Smart-Plug Import Gap Handling | Epic 3 | ✓ Covered |
| FR-25 | Meter Reading Regression Detection | Epic 2 | ✓ Covered |
| FR-26 | Household Provisioning | Epic 1 | ✓ Covered |
| FR-27 | Household Member Invitation | Epic 1 | ✓ Covered |
| FR-28 | Room / Power Point / Device Management | Epic 1 (+ Epic 2 re-parenting extension) | ✓ Covered |
| FR-29 | Structure Editor Archived-Item Visibility Toggle | **NOT FOUND** | ❌ MISSING |

Epic-list.md's own "Deferred — not decomposed into an epic" section explicitly names and justifies FR-18, FR-19, FR-20, FR-21 — these are intentional, documented deferrals, not gaps. **FR-29 is absent from that same deferred list**, and absent from every epic file — it has no traceable path anywhere in the epics document set.

### Missing Requirements

#### Critical Missing FRs

None. (FR-29's own text frames it as a UI convenience/polish item — a filter toggle over already-shipped soft-delete/archive behavior, not core product functionality.)

#### High Priority Missing FRs

**FR-29** *(deferred — not yet scheduled to an epic/story)*: Structure Editor Archived-Item Visibility Toggle — a show/hide toggle for archived Rooms/Power Points/Devices in the structure editor.
- **Impact:** Low functional risk (Epic 1's Room/Power Point/Device management already ships the underlying archive/soft-delete behavior per FR-28/AD-10 — this FR only adds a view filter on top of it), but it's a genuine traceability gap: FR-29 exists in `requirements-inventory.md`, was never added to the PRD's own `4-features.md`, and was never added to `epic-list.md`'s Deferred section either (unlike FR-18–21, which are). It's neither scheduled nor formally acknowledged as deferred.
- **Recommendation:** Either (a) add FR-29 to `epic-list.md`'s Deferred section with the same rationale-documentation discipline as FR-18–21 (it reads like a natural Epic 1 follow-up, since Epic 1 owns Room/Power Point/Device management), or (b) schedule it as a small story under Epic 1 if Ralf wants it built soon. Either way, backfill `4-features.md` so the PRD stays the source of truth for FR numbering (see PRD Completeness Assessment above).

### Coverage Statistics

- Total PRD FRs: 29 (28 in the PRD proper + FR-29 in requirements-inventory.md only)
- FRs covered in epics: 24
- FRs intentionally deferred with documented rationale: 4 (FR-18, FR-19, FR-20, FR-21)
- FRs with no coverage and no documented deferral: 1 (FR-29)
- Coverage percentage (covered + documented-deferred): 28/29 = **96.6%**
- Coverage percentage (covered only): 24/29 = 82.8%

## UX Alignment Assessment

### UX Document Status

**Found.** `ux-designs/ux-energy-tracker-2026-08-08/` — `EXPERIENCE.md` (behavioral spine, updated 2026-08-16), `DESIGN/` (visual identity: brand-style, colors, components, elevation-depth, layout-spacing, shapes, typography), 10 rendered mockup HTML files, plus accessibility/validation review docs.

### UX ↔ PRD Alignment

Strong alignment, no contradictions found:
- `EXPERIENCE.md`'s Information Architecture table maps all 8 surfaces (Dashboard, Log Reading, Trend History, Tariff Radar, Log Event, Settings, Onboarding/Household Setup, Smart Plug Import) directly to FR IDs (FR-1, FR-2, FR-4, FR-6–FR-13, FR-16, FR-17, FR-22, FR-23, FR-24, FR-26–FR-28) — every cited FR exists in the PRD FR list from Step 2.
- The three PRD Key User Journeys (UJ-1, UJ-2, UJ-3, §2.3) are walked through explicitly as Key Flows in `EXPERIENCE.md`, matching the PRD's own entry-state/path/climax/resolution structure beat-for-beat.
- Voice and Tone's Do/Don't table operationalizes FR-17's "rough, never false precision" and FR-6/7's "never fabricate a recommendation" consequences directly into microcopy rules — no drift from the PRD's stated intent.
- `requirements-inventory.md` defines a citable **UX-DR1–UX-DR19** numbering scheme (mirroring its FR/NFR/AD numbering role) translating `EXPERIENCE.md`/`DESIGN.md` content into IDs epics can reference — same pattern already used for NFRs and Architecture Decisions, and used consistently by all 7 epics.

### UX ↔ Architecture Alignment

No contradictions found; architecture structurally supports what UX specifies:
- UX-DR3's offline queue-and-sync behavior (Log Reading sheet, NFR7) is backed by AD-16 (client-generated idempotency key + IndexedDB queue + upsert-by-key) — a real mechanism exists, not just a UX aspiration.
- UX-DR15's `prefers-reduced-motion` contract and UX-DR16's WCAG 2.2 AA floor are frontend-only concerns with no architecture blocker (AD-13's SPA-hosting shape and the Consistency Conventions block don't constrain either).
- UX-DR6's gap-band reuse (Trend History gaps vs. Smart-Plug-import gaps, FR-8 vs. FR-24) matches AD-7's "Trend History reads only persisted `StatusSnapshot`, never live recompute" and AD-9/`SmartPlugImportGap`'s distinct-but-visually-shared treatment — no divergence between the two gap concepts' underlying data model and their shared visual vocabulary.
- UX-DR2's Status card recompute trigger ("every new Meter Reading or completed Smart Plug import") matches AD-7's `IStatusRecomputeService` two-call-site rule exactly.

### Alignment Issues

**UX-DR11 (dark-mode-first "liquid glass" elevation system + Light-mode degradation path) is missing from Epic 2's `UX-DRs:` rollup header line — but it IS cited at the story level.** Story 2.5's own AC text cites it directly ("both render the rear/front glass panel stack... (UX-DR11)"). So this isn't a coverage gap at all — the feature is specified and built (Epic 2 status: done) — it's a pure documentation-consistency slip: Epic 2's header line (`UX-DR1, UX-DR2, UX-DR3, UX-DR4, UX-DR8, UX-DR9, UX-DR13, UX-DR14, UX-DR15, UX-DR16, UX-DR17, UX-DR18`) doesn't list every UX-DR its own stories actually reference. Trivial severity — one-line fix (add UX-DR11 to Epic 2's header) purely for header/body consistency.

### Warnings

None beyond the UX-DR11 documentation-consistency slip above. UX documentation is not missing (this section's main warning condition), and no UI-implying PRD content lacks a corresponding UX artifact.

## Epic Quality Review

All 7 epics (24 stories, plus Story 3.4 drafted today) were read in full and validated against create-epics-and-stories standards: user value focus, epic independence, story sizing/dependencies, AC quality, and DB-creation timing.

### A. User Value Focus

| Epic | Verdict |
|---|---|
| 1 — Foundation, Deployment & Household Access | ⚠️ Mixed — see note below |
| 2 — Meter Reading & Pattern Detective Status Core | ✓ User-centric throughout |
| 3 — Smart Plug Import & Baseline Sharpening | ✓ User-centric throughout |
| 4 — Trend History & Per-Plug Insight | ✓ User-centric throughout |
| 5 — Tariff Savings Radar | ✓ User-centric throughout |
| 6 — Context Capture & Wattage Plausibility | ✓ User-centric throughout |
| 7 — Data Export & Import | ✓ User-centric throughout |

**Epic 1 note:** Stories 1.1–1.4 (application skeleton, Azure IaC, CI/CD pipeline, PR review workflow) and 1.6–1.7 (deploy-idempotency fix, OIDC redirect-scheme fix) are pure technical/infrastructure work with no directly user-visible outcome — by the letter of Section 2A's red-flag list ("Infrastructure Setup," "API Development"), these would flag. **Not treated as a violation here**, because Section 5B of this same checklist explicitly sanctions exactly this pattern for a greenfield project's first epic ("initial project setup story, dev environment configuration, CI/CD pipeline setup early"), and the PRD's own Additional Requirements section mandates Story 1.1 specifically as the Structural Seed bootstrap. Stories 1.5, 1.8, 1.9 (Household provisioning, invitation, tagging management) are genuinely user-facing and carry FR-26/27/28. Epic 1's own title honestly names both halves ("Foundation, Deployment" + "Household Access") rather than hiding the technical portion behind user-value language — flagged as a minor documentation observation only, not a defect.

### B. Epic Independence

No forward dependencies found. Every epic's stated dependency is on an **earlier** epic only:

- Epic 1: depends on nothing (explicitly stated in its own description).
- Epic 2: depends on Epic 1 (Household must exist).
- Epic 3: "Builds on Epic 2's Status but never blocks it" — backward only.
- Epic 4: "Builds on Epics 2 and 3's data" — backward only.
- Epic 5: "Uses Epic 2's consumption pace; otherwise standalone" — backward only.
- Epic 6: Story 6.2 depends on Epic 2's deviation signal — backward only.
- Epic 7: "sequenced last so it captures the complete data model" (Epics 1–6) — backward only, and deliberately so.

No epic requires a later epic's output to function. **Pass, no violations.**

### C. Story Sizing & Within-Epic Dependencies

Checked every story's ordering within its own epic (28 stories across 7 epics, plus Story 3.4). Every story depends only on an earlier story in the same epic or an earlier epic — no forward references found. Notable legitimate patterns, not violations:
- Epic 1's Stories 1.6/1.7 are hardening fixes discovered after 1.2/1.3/1.5 shipped (deploy-idempotency bug, OIDC scheme bug) — sequenced after what they fix, correctly.
- Epic 3's Story 3.3 explicitly wires into **both** of Stories 3.1 and 3.2's completion paths (documented in its own epic text) — correctly sequenced last among the three.
- Epic 4's Story 4.3 explicitly browses via Story 4.1's Trend History surface — correctly sequenced after it.
- Story 3.4 (drafted today) depends only on 3.1's parser/3.2's repository — correctly sequenced last in Epic 3.

**Pass, no violations.**

### D. Acceptance Criteria Quality

Every AC across all 7 epics (approximately 180 individual Given/When/Then clauses) follows proper BDD structure with specific, testable, numerically-grounded outcomes (e.g. "≤2s," "409 conflict," "exact tie resolves to within range," "44×44pt-equivalent tap target") — no vague criteria ("user can login"-style) found anywhere. Error/edge conditions are consistently covered alongside the happy path (e.g. FR-25's regression classification, FR-24's four gap-handling variants, FR-23's malformed-import rejection). **No AC quality issues found.**

### E. Database/Entity Creation Timing

No evidence of upfront schema creation. Story 1.1's ACs describe only the solution *structure* (`Domain`/`Application`/`Infrastructure`/`Api`/`web/`), not entity/table creation — Household-related tables arrive with Story 1.5, tagging-scaffold tables with Story 1.9, Smart Plug tables with Epic 3's stories (confirmed against actual migration history: `AddSmartPlugImportInfrastructure` and `AddSmartPlugImportGaps` migrations are dated to Epic 3's own stories, not bundled into Epic 1). **Pass, tables created only when first needed.**

### F. Starter Template Requirement

Architecture's Additional Requirements section mandates: "No third-party starter template... this structural seed must be established as Epic 1 Story 1." Story 1.1 ("Deployable Application Skeleton") matches this exactly. **Compliant.**

### G. Greenfield Indicators

Initial project setup (1.1), dev environment configuration (1.1's Docker Compose), and CI/CD pipeline setup early (1.2–1.4) are all present in Epic 1, as expected for a greenfield project. No brownfield integration/migration stories are needed or present (correct — this is confirmed greenfield, "v2 is a ground-up rebuild, not a migration" per PRD §5 Non-Goals). **Compliant.**

### Quality Assessment Documentation

#### 🔴 Critical Violations

None found.

#### 🟠 Major Issues

None found.

#### 🟡 Minor Concerns

1. Epic 1 mixes sanctioned greenfield infrastructure stories with user-facing stories under one epic — documentation observation only (Section A above), not a defect; already correctly named in the epic's own title.
2. UX-DR11 present in Story 2.5's ACs but missing from Epic 2's rollup header line (Step 4 finding, restated here for completeness) — one-line fix.
3. FR-29 has no epic/story coverage and no documented-deferral entry in `epic-list.md` (Step 3 finding, restated here) — needs either scheduling or formal deferral.
4. FR-29 is absent from the PRD's own `4-features.md` (Step 2 finding, restated here) — PRD/requirements-inventory.md drift.

## Summary and Recommendations

### Overall Readiness Status

**READY.**

The PRD, Architecture, Epics, and UX documents are internally consistent, cross-referenced accurately, and free of critical or major defects. Today's correct-course pass (NFR10 broadened, AD-20 added, Story 3.4 drafted) is already fully reflected across every document — the exact kind of drift this check exists to catch was, in that case, already closed before this assessment ran. The one real traceability gap found (FR-29) is low-impact and easy to close.

### Critical Issues Requiring Immediate Action

None. No critical or major violations were found in any of the five assessment steps.

### Recommended Next Steps

1. **Close the FR-29 gap** — Structure Editor Archived-Item Visibility Toggle exists in `requirements-inventory.md` but nowhere else. Either (a) backfill it into the PRD's `4-features.md` and add it to `epic-list.md`'s Deferred section with documented rationale (matching FR-18–21's treatment), or (b) schedule it as a small Epic 1 follow-up story if Ralf wants it built. Either resolves the traceability gap; (a) is lower-effort if it's genuinely not a near-term priority.
2. **Fix Epic 2's header line** — add `UX-DR11` alongside `UX-DR1` so the header matches what Story 2.5's ACs already cite. One-line documentation fix, no functional change.
3. **Proceed with Story 3.4** (Incremental Smart-Plug Import) as planned — it's fully traceable to NFR10 and AD-20, both already in place, and Epic 3's own quality bar (Section Epic Quality Review) holds for it same as 3.1–3.3.
4. No action needed on Epic 1's mixed technical/user-value framing — confirmed compliant with this project's greenfield status, not a defect.

### Final Note

This assessment identified 4 issues, all Minor, across 3 categories (PRD completeness, epic coverage, UX/epic-header consistency). No Critical or Major issues were found in PRD analysis, epic coverage, UX alignment, or epic quality review. These findings are safe to batch into routine cleanup — none of them block proceeding with implementation, including Story 3.4.

---
**Assessed by:** Winston (System Architect), acting in the Implementation Readiness workflow's Product Manager role
**Date:** 2026-08-22
