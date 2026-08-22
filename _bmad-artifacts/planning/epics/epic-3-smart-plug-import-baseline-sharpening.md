# Epic 3: Smart Plug Import & Baseline Sharpening

Adds Smart Plug data (Eve Home `.xlsx`, Meross `.csv`) as an optional, additive signal that sharpens the Status Epic 2 already delivers — async import with completion notification, gap-tolerant parsing that never fabricates measured data. Builds on Epic 2's Status but never blocks it.

**FRs covered:** FR-4, FR-5, FR-24
**NFRs:** NFR1 (Tier 3 async), NFR10 (no-silent-duplication on repeated writes)
**Architecture:** AD-6, AD-9, AD-10, AD-20
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

## Story 3.4: Incremental Smart-Plug Import (Resource-Efficient Re-Import)

As a Household member who re-uploads a Smart Plug export periodically,
I want the import to only process data newer than what's already stored,
So that re-uploading an export with overlapping history doesn't reprocess (or duplicate) data I've already imported, and stays cheap regardless of how large the file has grown.

**Context (why this is needed):** Both vendors can hand back data the household already imported, for different reasons. Eve Home's export is cumulative, not incremental — every download contains the device's *entire* history, growing on every export (confirmed against `sample-data/eve`'s three dated HiFi samples: 108,715 rows on 2026-06-20 → 114,815 on 2026-08-01 → 117,782 on 2026-08-22, still reaching back to the plug's first-ever reading on 2023-05-27). Meross instead lets the household pick an arbitrary export date range by hand — with no record of the last import's end date surfaced anywhere, picking a range that overlaps a prior import (Ralf: "I select there a time period for exports, but I never check specific date of last export") is the expected normal case, not a rare mistake. Either way, a household that re-uploads periodically — the normal, expected usage pattern for both vendors — re-parses and re-persists data it already has today, and because `SmartPlugReading` carries no uniqueness constraint, each overlapping re-upload silently **duplicates** already-stored rows rather than just wasting time on them, inflating `SmartPlugGapDetector`'s daily kWh totals and the Status-sharpening signal a little more with every re-import.

**Acceptance Criteria:**

**Given** an Eve Home import for a device tag that already matches an existing, unarchived Power Point with at least one prior stored `SmartPlugReading`
**When** the file is processed
**Then** the Power Point match is resolved from the file's header (device/room rows) before the data body is read at all, and only rows newer than that Power Point's latest stored `IntervalStart` are parsed and persisted — rows at or older than that watermark are never materialized into memory (AD-9)

**Given** Eve Home's data rows are strictly newest-first (confirmed against the sample files)
**When** the watermark is reached during parsing
**Then** parsing stops immediately rather than continuing to filter the remaining (already-imported) rows (AD-9)

**Given** a Meross import for a device tag that already matches an existing, unarchived Power Point with at least one prior stored reading
**When** the file is processed
**Then** rows are filtered against that Power Point's stored watermark before persisting — not early-stopped, since Meross's CSV row order carries no documented ordering guarantee, unlike Eve Home's (AD-9)

**Given** a device tag with no existing Power Point match (AwaitingPowerPointMapping) or a Power Point with no prior stored readings yet (first-ever import)
**When** the file is processed
**Then** the full file is parsed exactly as it is today — this story only optimizes the repeat-import path, not the first import, and never changes AwaitingPowerPointMapping/first-import behavior

**Given** the Eve Home parser's current full-DOM row load (`worksheet.Descendants<Row>().ToList()`)
**When** a file's rows still need reading past the watermark point (e.g. a first-ever import, or the small newest slice of a repeat import)
**Then** parsing uses a forward-only streaming reader instead of materializing the whole worksheet into memory, so peak memory scales with rows actually read, not the file's total historical size

**Given** any row a parser produces (regardless of the watermark optimization above)
**When** it's persisted
**Then** a uniqueness guard — a DB constraint on `(PowerPointId, IntervalStart)` plus an upsert/ignore-on-conflict write, not the watermark filter alone — prevents a duplicate `SmartPlugReading` row from ever being written, so an out-of-order file, a clock-adjusted overlap, or any other path that bypasses the watermark can't silently double-count a day's kWh in gap detection or Status sharpening (AD-2: portable relational subset, added via `scripts/add-migration.sh` to both provider projects per AD-2's dual-provider rule)

**Given** an incremental re-import of a large, long-lived Eve Home export
**When** processing completes
**Then** it finishes well within Tier 3 async expectations (NFR1) regardless of the file's total historical row count, since only the genuinely new slice is read, parsed, and written

**Given** the new `(PowerPointId, IntervalStart)` uniqueness constraint above
**When** its migration is written
**Then** it includes a one-time data-cleanup step that runs before the unique index is created (same migration, cleanup first) — grouping existing `SmartPlugReading` rows by `(PowerPointId, IntervalStart)` and deleting every row in a group but one, so the index creation itself never fails against data already duplicated by pre-this-story imports

**Given** a duplicate group found during that cleanup
**When** deciding which row survives
**Then** the row belonging to the most recently-created `SmartPlugImport` (by `CreatedAtUtc`) is kept — Eve/Meross re-exports are treated as superseding older ones, consistent with this story's own "later import wins" framing, rather than an arbitrary/first-inserted row surviving

**Given** the cleanup migration
**When** run against Postgres and SQL Server
**Then** it's verified on both providers via a Testcontainers integration test (matching this project's existing migration-testing discipline) before being treated as safe to ship — a duplicate-finding query (e.g. `ROW_NUMBER() OVER (PARTITION BY ... ORDER BY ...)`) is supported by both engines but isn't guaranteed byte-identical in a raw-SQL migration step, unlike EF's normal portable `migrationBuilder` calls

**Dev Notes / Open Questions:**
- The one-time cleanup only reconciles which *row* survives per duplicate group — it does not retroactively correct any `SmartPlugImportGap`/`StatusSnapshot` rows that were already computed against inflated (duplicate-inclusive) daily totals before this story ships. Per AD-7, `StatusSnapshot`/gap rows are immutable history and are never rewritten after the fact — the cleanup's benefit is that every Status/gap computation *from this point forward* reads correct, de-duplicated totals; past snapshots stand as recorded, same discipline as every other AD-7 history read.
- The cleanup is treated as an internal data-integrity migration, not a user-facing correction — it deliberately does **not** route through `AuditCorrection`/`IAuditCorrectionRecorder` (AD-11), since that mechanism exists for user-initiated edits to a specific entity's value, not an infra-level fix for a duplication bug the user never chose or saw.
- If duplicate rows within a group disagree on `KwhValue` (unexpected under the "same source data" assumption, but not provably impossible if Eve revises a not-yet-settled value between exports), the "most recent import wins" tie-break above also decides which `KwhValue` survives — flagged as an assumption to confirm against real duplicate data once this pass is actually run, not something to guess further at design time.
- The uniqueness-guard AC is deliberately DB-level, not just the watermark filter, because the watermark alone doesn't protect the AwaitingPowerPointMapping → later-mapped path (readings are persisted before a Power Point is known) or Meross's unordered-file case from ever producing an exact-timestamp duplicate.
- Deferred-work entry from story-3.2's review ("`ListReadingsByImportIdAsync`/`UpdateMappingAsync` load and update an import's full reading set unpaged") is related but distinct — that's about the *mapping* path's row volume, not the *parse* path this story targets; worth revisiting together since both stem from the same "Eve exports are unbounded and grow forever" root cause.
