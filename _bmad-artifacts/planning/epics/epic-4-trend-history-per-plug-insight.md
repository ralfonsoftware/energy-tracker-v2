# Epic 4: Trend History & Per-Plug Insight

The calm-evening drill-down surface (UJ-3): browse Status/pace trend over time and per-device measured context organized by Room → Power Point → Device — explicitly framed as context, never a reconciled breakdown of the Main Meter total. Builds on Epics 2 and 3's data.

**Epic-definition update (2026-08-23):** per the Epic 3 Retrospective's Significant Discovery, this epic's definition was updated before Story 4.1 drafting began — see `_bmad-artifacts/implementation/epic-3-retro-2026-08-23.md`. Story 2.8 (Epic 2, shipped 2026-08-23) built a standalone Meter Reading History page that Story 4.3 as originally written didn't account for. Ralf's decision: consolidate rather than duplicate — Story 4.1 absorbs Story 2.8's browsable-list functionality, and Story 4.3's ACs are otherwise unchanged (edit-in-place, 409, `AuditCorrection` — all already built in 2.8 and reused here), with the recompute-forward `IStatusRecomputeService` wiring as the one net-new piece. The Dashboard's standalone "History" text-link (2.8) is removed once the merge lands.

**FRs covered:** FR-8, FR-9, FR-31 (absorbed from Epic 2 — Story 2.8's browsable Meter Reading list migrates into Story 4.1's surface, per the 2026-08-23 epic-definition update above)
**NFRs:** NFR8 (audit trail), NFR9 (recomputation policy), NFR10 (concurrency), NFR15 (says-less / drill-down discipline)
**Architecture:** AD-4, AD-7 (snapshot reads), AD-10 (display integrity), AD-11 (audit correction), AD-14
**UX-DRs:** UX-DR6 (trend chart), UX-DR12 (IA), UX-DR19 (responsive layout)

## Story 4.1: Trend History View

As a Household member,
I want to view my Status/pace trend over time — and browse and correct my individual Meter Readings from the same surface,
So that I can see how my consumption pace has evolved and drill into or fix the raw entries behind it, without a second parallel page.

**Absorbs Story 2.8 (Epic 2, shipped 2026-08-23):** Story 2.8 built a standalone "Meter Reading History" page (paginated list, ordered by timestamp descending, pending-regression flagging, edit-in-place with `AuditCorrection`/AD-4 concurrency) reachable via a Dashboard text-link. Per the Epic 3 Retro's Significant Discovery, that functionality is consolidated into this story rather than kept as a separate surface — reuse Story 2.8's backend (`GetMeterReadingHistory`, `EditMeterReading`, `GET`/`PUT /api/meter-readings`) and frontend building blocks (`MeterReadingHistoryPage`'s list/pagination markup, `EditMeterReadingDialog`) as-is; this story's job is to relocate/integrate them into the Trend History surface, not rebuild them. Story 4.3 below covers the edit-path ACs (concurrency, audit correction, recompute-forward); this story covers the browse-path ACs.

**Acceptance Criteria:**

**Given** the Trend History surface
**When** displayed
**Then** alongside the aggregate Status/pace trend, it also surfaces a browsable list of individual Meter Readings (value + timestamp, Main Meter only), ordered by timestamp descending — the functionality Story 2.8 originally shipped as a standalone page, now consolidated here (FR-31)

**Given** a Meter Reading in the browsable list that is currently under an open, unconfirmed regression classification (Story 2.3)
**When** it appears in the list
**Then** it's visibly flagged as pending rather than shown as a normal confirmed entry — unchanged from Story 2.8's original behavior (FR-31, FR-25)

**Given** the Dashboard Status card's standalone "History" text-link (built in Story 2.8)
**When** this story's merged surface ships
**Then** that link is removed — Trend History becomes the single place to browse (and, per Story 4.3, correct) Meter Readings, not two parallel entry points

**Given** historical `StatusSnapshot` rows (Story 2.4)
**When** I open Trend History
**Then** I see trend over time, not just the current point-in-time Status (FR-8)

**Given** the Trend History view
**When** rendered
**Then** it reads only persisted `StatusSnapshot` rows, never a live recomputation against current settings — a later Yearly Baseline/threshold edit cannot rewrite what's shown (FR-8, AD-7, NFR9)

**Given** gaps in the underlying Meter Reading history
**When** rendered
**Then** they show as a visible break in the trend line, never an interpolated line — distinct from FR-24's Smart-Plug-import gap interpolation (FR-8)

**Given** the trend chart
**When** displayed
**Then** Moderate density is the only shipped default (no user-facing density toggle), with status-triad line coloring for in-range vs. trending segments only — never a 4th chart-specific color (UX-DR6)

**Given** the Trend History surface
**When** accessed on a tablet/browser-width screen
**Then** it widens to that frame but stays single-column-of-cards internally — no dense multi-column grid (UX-DR19)

**Given** the product's "says less, on purpose" discipline
**When** Trend History is compared to the Dashboard Status card
**Then** checking Trend History is never presented as a precondition for trusting the Status (NFR15)

## Story 4.2: Per-Plug Measured Data View

As a Household member,
I want to view my measured Smart Plug data organized by Room → Power Point → Device,
So that I can see what's actually been measured without it being confused with my Main Meter total.

**Acceptance Criteria:**

**Given** imported Smart Plug data
**When** I open the Per-Plug view
**Then** it's organized by the Room → Power Point → Device structure it's tagged to (FR-9)

**Given** the Per-Plug view
**When** rendered
**Then** it's explicitly presented as measured context, not a reconciled attribution breakdown of the Main Meter total — nothing here is summed against or claims to explain the Main Meter's number (FR-9, AD-14)

**Given** a Device or Power Point retagged after Smart Plug data was already imported (Story 3.2's write-time snapshot)
**When** the Per-Plug view is displayed
**Then** previously imported data stays attributed to the tag that was active at import time — it does not silently move to follow the retag (FR-9, AD-10)

**Given** the Room → Power Point → Device tree
**When** displayed
**Then** it's an expandable list (shadcn `details`/accordion pattern), collapsed by default, at Moderate density (UX-DR6)

## Story 4.3: Correcting a Meter Reading

As a Household member,
I want to correct a Meter Reading I entered incorrectly, with the original value preserved and Status history brought up to date,
So that a mistake doesn't leave my trend permanently wrong or silently hidden.

**Reuses Story 2.8 (Epic 2, shipped 2026-08-23):** the edit-in-place mechanics below — optimistic concurrency (AD-4, `MeterReading.Version`), original-value preservation (AD-11, `AuditCorrection`/`IAuditCorrectionRecorder`), and the `EditMeterReading` use case / `PUT /api/meter-readings/{id}` endpoint / `EditMeterReadingDialog` component — are already built and were deliberately left unchanged by the Epic 3 Retro's decision; this story's implementation is to wire that existing edit path into Story 4.1's now-merged surface, not to rebuild it. The one **net-new** piece this story adds is the `IStatusRecomputeService` recompute-forward AC below — 2.8 explicitly deferred it (see `deferred-work.md`) as out of scope for a first pass.

**Acceptance Criteria:**

**Given** an existing Meter Reading, browsed via Trend History (Story 4.1)
**When** I edit its value
**Then** the original value is preserved and shown as a visible correction note alongside the edit, never silently overwritten, via the shared `AuditCorrection` mechanism (NFR8, AD-11)

**Given** two Household members editing the same Meter Reading concurrently
**When** both submit
**Then** the second writer receives a 409 conflict rather than silently overwriting the first (AD-4, NFR10)

**Given** a corrected Meter Reading
**When** the correction is saved
**Then** `IStatusRecomputeService` (Story 2.4) recomputes Status forward from the corrected reading through to the present, and the affected `StatusSnapshot` rows are updated to reflect the corrected value — history before the corrected reading is left untouched (AD-7)

**Given** a Meter Reading that is currently excluded from baseline computation by an unresolved regression prompt (Story 2.3)
**When** it is edited
**Then** the edit does not bypass or resolve the open regression prompt — the reading remains excluded until the prompt is classified
