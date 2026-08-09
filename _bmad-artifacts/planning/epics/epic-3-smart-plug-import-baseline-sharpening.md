# Epic 3: Smart Plug Import & Baseline Sharpening

Adds Smart Plug data (Eve Home `.xlsx`, Meross `.csv`) as an optional, additive signal that sharpens the Status Epic 2 already delivers — async import with completion notification, gap-tolerant parsing that never fabricates measured data. Builds on Epic 2's Status but never blocks it.

**FRs covered:** FR-4, FR-5, FR-24
**NFRs:** NFR1 (Tier 3 async)
**Architecture:** AD-6, AD-9, AD-10
**UX-DRs:** UX-DR6 (gap-band reuse), UX-DR12 (Smart Plug Import surface), UX-DR14 (import empty/edge states)

## Story 3.1: Smart Plug File Upload & Async Parsing

As a Household member,
I want to upload my Smart Plug export file (Eve Home `.xlsx` or Meross `.csv`) and have it parsed in the background,
So that I don't have to wait for processing to finish before continuing to use the app.

**Acceptance Criteria:**

**Given** a Smart Plug export file (Eve Home `.xlsx` or Meross `.csv`)
**When** I upload it
**Then** the upload confirms immediately and parsing runs asynchronously via the job queue — the UI never blocks on parsing (FR-4, AD-6)

**Given** an in-progress import
**When** processing completes
**Then** I receive a completion notification, learned by the client polling `GET /api/jobs/{id}`, never via WebSocket/SSE (AD-6)

**Given** an Eve Home `.xlsx` file
**When** parsed
**Then** its timestamps are interpreted as local time, never UTC-converted (FR-4, AD-9)

**Given** a Meross `.csv` file
**When** parsed
**Then** its Device/Power Point identity is matched via the documented filename pattern (`Power Monitor Day Data - {device} - {YYYYMMDD}.csv`), not by trusting in-file metadata alone (FR-4, AD-9)

**Given** each vendor format
**When** parsed
**Then** it goes through its own adapter (`EveHomeXlsxParser`, `MerossCsvParser`) behind the single `ISmartPlugParser` port — no vendor-specific parsing logic leaks outside the adapter (AD-9)

**Given** the async job processing
**When** it runs
**Then** it resolves `ICurrentHouseholdAccessor` from the enqueued job's `HouseholdId` field, never bypassing tenant isolation via `IgnoreQueryFilters()` or a raw lookup (AD-3, AD-6)

## Story 3.2: Import-to-Power-Point Mapping

As a Household member,
I want an import tagged to a Power Point that doesn't exist yet to prompt me to create or map it,
So that my data isn't silently dropped or misfiled.

**Acceptance Criteria:**

**Given** an import file tagged (by device name/filename) to a Power Point that doesn't yet exist in my Household
**When** the import is processed
**Then** I'm prompted to create it or map it to an existing Power Point/Device, rather than the import silently failing (FR-4)

**Given** I create or map the Power Point during this flow
**When** the import completes
**Then** the `SmartPlugReading` rows are associated with that Power Point

**Given** the import's Room/Power Point/Device tag
**When** the data is written
**Then** the tag identity is snapshotted by value at write time (denormalized display fields) — a later retag of the Power Point's Room does not rewrite this import's historical attribution (AD-10)

## Story 3.3: Smart-Plug Import Gap Handling & Baseline Sharpening

As a Household member,
I want my Smart Plug data to sharpen my Status without ever being treated as more certain than it is,
So that gaps in coverage don't quietly corrupt my baseline.

**Acceptance Criteria:**

**Given** a Household with zero Smart Plug coverage
**When** Status is computed
**Then** it still gets a fully functional Status from Meter Readings alone (FR-5)

**Given** imported Smart Plug data
**When** Status is computed
**Then** the data sharpens the Status as an additional signal — it is never summed against or reconciled to the Main Meter total (FR-5, AD-14)

**Given** a Smart Plug import completes
**When** processing finishes
**Then** Status recomputes immediately via the same `IStatusRecomputeService` used for new Meter Readings (Story 2.4), writing a fresh `StatusSnapshot` row — never left to a fixed schedule (FR-6, AD-7)

**Given** an import's covered date range
**When** a date within it has no interval data
**Then** it's treated as a Gap; a 0 kWh reading is treated as a valid data point, never as a Gap (FR-24)

**Given** a Gap used to sharpen the baseline
**When** its value is filled
**Then** it's bounded (e.g. capped at the preceding week's average) and visibly flagged as interpolated, never presented as measured data (FR-24)

**Given** a Gap at the very start of a household's first-ever import, with no preceding week to average
**When** detected
**Then** it's left unfilled and flagged as missing, not interpolated from nothing (FR-24)

**Given** an import file whose data is entirely Gaps
**When** processed
**Then** it's flagged for review rather than wholesale-interpolated (FR-24)

**Given** the gap-flagged visual treatment
**When** rendered
**Then** it reuses the same gap-band / status-trending color vocabulary as Trend History gaps rather than inventing a new one (UX-DR6)
