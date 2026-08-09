# Epic 4: Trend History & Per-Plug Insight

The calm-evening drill-down surface (UJ-3): browse Status/pace trend over time and per-device measured context organized by Room → Power Point → Device — explicitly framed as context, never a reconciled breakdown of the Main Meter total. Builds on Epics 2 and 3's data.

**FRs covered:** FR-8, FR-9
**NFRs:** NFR8 (audit trail), NFR9 (recomputation policy), NFR10 (concurrency), NFR15 (says-less / drill-down discipline)
**Architecture:** AD-4, AD-7 (snapshot reads), AD-10 (display integrity), AD-11 (audit correction), AD-14
**UX-DRs:** UX-DR6 (trend chart), UX-DR12 (IA), UX-DR19 (responsive layout)

## Story 4.1: Trend History View

As a Household member,
I want to view my Status/pace trend over time,
So that I can browse how my consumption pace has evolved beyond just the current snapshot.

**Acceptance Criteria:**

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
