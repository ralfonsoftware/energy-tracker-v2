# Epic List

## Epic 1: Foundation, Deployment & Household Access
Establishes the buildable/deployable skeleton (the architecture's Structural Seed — layered .NET solution, React/Vite/shadcn frontend, dual-provider DB, Docker Compose) and lets the first Household come into existence: a fresh deployment routes an authenticated visitor into Household creation, existing members can invite others, and the Room → Power Point → Device tagging scaffold used by later epics is manageable. Every subsequent epic depends on this one; it depends on nothing.
**FRs covered:** FR-26, FR-27, FR-28, FR-29

## Epic 2: Meter Reading & Pattern Detective Status Core
The product's non-negotiable core loop: a Household member logs a Meter Reading in under a minute (with offline queuing), sets a Yearly Baseline, and sees a single trustworthy Status (within range / below baseline / trending) on the dashboard — computed from a gap-tolerant rolling baseline, with meter-rollover/reset regressions caught and classified rather than silently corrupting the pace. Fully functional with zero Smart Plug coverage. Realizes UJ-1 and UJ-2's Status half.
**FRs covered:** FR-1, FR-2, FR-3, FR-6, FR-7, FR-25, FR-28 (extension — re-parenting only; FR-28's core CRUD remains Epic 1), FR-30 (added post-Epic-2-retro 2026-08-18, extending Story 2.5's Status card), FR-31 (added 2026-08-23, dedicated Meter Reading history/browse surface — Story 2.8, shipped as a standalone page then absorbed into Epic 4's Story 4.1 per the Epic 3 retro 2026-08-23 — see Epic 4 below)

## Epic 3: Smart Plug Import & Baseline Sharpening
Adds Smart Plug data (Eve Home `.xlsx`, Meross `.csv`) as an optional, additive signal that sharpens the Status Epic 2 already delivers — async import with completion notification, gap-tolerant parsing that never fabricates measured data. Builds on Epic 2's Status but never blocks it.
**FRs covered:** FR-4, FR-5, FR-24

## Epic 4: Trend History & Per-Plug Insight
The calm-evening drill-down surface (UJ-3): browse Status/pace trend over time and per-device measured context organized by Room → Power Point → Device — explicitly framed as context, never a reconciled breakdown of the Main Meter total. Builds on Epics 2 and 3's data. Story 4.1 also absorbs Story 2.8's browsable/editable Meter Reading list (FR-31), consolidated here rather than left as Epic 2's separate standalone page — see the Epic 3 retro (2026-08-23) and the epic's own file for the full rationale.
**FRs covered:** FR-8, FR-9, FR-31 (absorbed from Epic 2's Story 2.8)

## Epic 5: Tariff Savings Radar
Answers the product's second core question — is the current Tariff still worth staying on — via bonus-decay-normalized comparison against a candidate tariff, shown as a two-way signal, with a proactive reminder gated to contract-end timing. Shares its normalization math with Epic 2's pace threshold. Uses Epic 2's consumption pace; otherwise standalone.
**FRs covered:** FR-10, FR-11, FR-12, FR-13, FR-14, FR-15

## Epic 6: Context Capture & Wattage Plausibility
Lets a household explain a spike or dip the Status surfaced, by logging fast text/tap-first Events for unmeasurable appliances and getting a rough, honest AI-assisted correlation against the deviation Epic 2 already computed — optional and gracefully degradable throughout.
**FRs covered:** FR-16, FR-17

## Epic 7: Data Export & Import (Disaster Recovery)
Full-household backup/restore in a documented format, covering every entity type introduced by Epics 1–6 (Readings, Tariff history, Events, Smart Plug data, settings) — the safety net underneath the whole product, sequenced last so it captures the complete data model rather than growing piecemeal alongside each feature epic.
**FRs covered:** FR-22, FR-23

**Deferred — not decomposed into an epic:**
- FR-19 (Custom Event/Plausibility Rules) — Could-have, explicitly out of MVP scope per PRD §6.2; no rule engine designed yet
- FR-20 (Generic Data-Source Column Mapping) — Could-have, explicitly out of MVP scope per PRD §6.2; PRD itself flags as low-confidence pending a feasibility spike (Open Question 1)
- FR-21 (Tunable Threshold/Spike Settings beyond FR-6) — Could-have, explicitly out of MVP scope per PRD §6.2; no settings-surface design yet
- FR-18 (Proactive Weekly Recap) — PRD lists this in MVP scope (Should tier), but the Architecture Spine (AD-7 and the Deferred section) explicitly calls it out as not buildable yet: it needs a real externally-triggered scheduler (Container Apps scheduled Jobs or a KEDA cron rule — no design decided) and a notification delivery channel that's still an open PRD question (Open Question 2). Deferred here per product-owner decision (2026-08-09) rather than force-fitting an under-specified story; revisit once both are resolved.

These remain in the FR Coverage Map below for completeness but are recommended as backlog items to revisit after Epics 1–7 ship and real usage signal exists, per the Architecture Spine's own Deferred section.
