# Epic 3: Smart Plug Import & Baseline Sharpening

Adds Smart Plug data (Eve Home `.xlsx`, Meross `.csv`) as an optional, additive signal that sharpens the Status Epic 2 already delivers — async import with completion notification, gap-tolerant parsing that never fabricates measured data. Builds on Epic 2's Status but never blocks it.

**FRs covered:** FR-4 (amended 2026-08-26), FR-5, FR-24, FR-32 (added 2026-08-26)
**NFRs:** NFR1 (Tier 3 async), NFR10 (no-silent-duplication on repeated writes)
**Architecture:** AD-6 (extended 2026-08-26 — `BackgroundJobStatus.Queued`, lazy read-triggered retention sweep), AD-9, AD-10, AD-11 (extended 2026-09-03 — Smart Plug reading `KwhValue` boundary-row corrections), AD-20, AD-22 (added 2026-09-03 — watermark corruption detection), AD-23 (added 2026-09-03 — bulk-write via EFCore.BulkExtensions)
**UX-DRs:** UX-DR6 (gap-band reuse), UX-DR9 (amended — Smart Plug Import off the nav-adjacent Settings path), UX-DR12 (amended — dual icon entry points, one shared screen), UX-DR14 (extended — six Job Status states + empty state), UX-DR20 (entry icon button + multi-file queue), UX-DR21 (Job Status & History list), UX-DR22 (accessibility follow-up)

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

## Story 3.5: Dual Entry Points & Multi-File Import Queuing

As a Household member,
I want to start a Smart Plug import from wherever I already am — Dashboard or Trend History — and queue several files at once,
So that I don't have to detour through Settings, and uploading a batch of exports doesn't mean waiting for each one before starting the next.

**Acceptance Criteria:**

**Given** the Dashboard or Trend History screen
**When** rendered
**Then** a Smart Plug Import entry point (icon button) is visible on both, and Settings no longer hosts a separate upload entry (FR-4 amendment, UX-DR20)

**Given** either entry point is tapped
**When** the screen opens
**Then** it is the same shared Smart Plug Import screen regardless of which one was tapped — not two different screens or routes (FR-4, UX-DR12)

**Given** the file picker/dropzone
**When** a Household member selects or drops multiple files in one action
**Then** each file is enqueued as its own independent `BackgroundJob`/`SmartPlugImport` job immediately, not queued to be enqueued sequentially after the previous one finishes (FR-4)

**Given** several files enqueued in one action
**When** one file fails to parse or needs Power Point mapping
**Then** the other files' processing is unaffected — one file's outcome never blocks or cancels another's (FR-4)

**Given** a newly enqueued job
**When** the queue is rendered
**Then** it appears immediately with a Waiting or Processing indicator — the UI reflects queue state without waiting for any file to complete (FR-4, AD-6)

**Given** the existing single-file async behavior from Story 3.1
**When** multiple files are involved
**Then** each file's async lifecycle (upload confirms immediately, parsing runs in background, completion polled via `GET /api/jobs/{id}`) is unchanged per file — this story only adds starting several at once, not a new processing model (AD-6)

## Story 3.6: Smart Plug Import Job Status & History

As a Household member,
I want to see the status of every Smart Plug import my household has ever run — not just the one I'm watching finish — in six clearly distinct states,
So that I can find and resolve one that needs my attention without having to remember who uploaded what.

**Acceptance Criteria:**

**Given** any Household member
**When** they open the Smart Plug Import screen
**Then** they see every import job queued by any member of their Household, not only their own (FR-32, AD-3 tenant isolation still scopes this to one Household, never cross-Household)

**Given** a job's lifecycle
**When** rendered in the list
**Then** it shows exactly one of six states — Waiting, Processing, Success, Error, Needs Mapping, Flagged for Review — never folded into one another or a generic pending/done (FR-32, UX-DR21)

**Given** a job enqueued but not yet dequeued (e.g. a cold start, or a later file in a multi-file queue)
**When** the list is read
**Then** it shows as Waiting — backed by a new `BackgroundJobStatus.Queued` value and a `BackgroundJob` row created at enqueue time, not only once dequeued (AD-6 extension)

**Given** a job in Needs Mapping (Story 3.2's create-or-map condition)
**When** a Household member selects that row
**Then** it opens directly into that import's create-or-map-Power-Point prompt — the household never has to relocate the original upload to resolve it (FR-32)

**Given** a job in Flagged for Review (Story 3.3's entirely-Gaps condition)
**When** rendered
**Then** it is visually and semantically distinct from Error — the file parsed without failure, it simply had nothing usable (FR-32, UX-DR21)

**Given** a job that reached Success, Error, or Flagged for Review
**When** 30 days have elapsed since its completion
**Then** its job/audit record is removed the next time the list is read — a lazy, read-triggered sweep piggybacked on the list query, never an `IHostedService`/`Timer`-based schedule (AD-7) — and the `SmartPlugReading` data that job already wrote is never touched (AD-6 extension, AD-20)

**Given** Waiting, Processing, or Needs Mapping jobs
**When** 30 days elapse
**Then** they are never auto-removed — only completed/resolved/failed states are subject to cleanup (FR-32)

**Given** a Household with no Smart Plug import activity in the last 30 days
**When** the screen is opened
**Then** it shows an empty state, not an error or blank space (FR-32, UX-DR21)

## Story 3.7: Smart-Plug Reading Duplicate Cleanup on Late Power-Point Mapping

As a Household member whose Smart Plug import once sat AwaitingPowerPointMapping before being resolved,
I want the readings from that resolution to never leave a duplicate, permanently-unmapped copy behind,
So that my Smart Plug data stays accurate and doesn't waste storage on dead rows nobody will ever read.

**Context (why this is needed):** Confirmed live in production on 2026-08-26 (household audit): `MapSmartPlugImportToPowerPoint`'s per-row conflict-tolerant fallback (`SmartPlugImportRepository.UpdateMappingPerRowWithConflictToleranceAsync`, added by Story 3.4's Dev Notes Open Question #4, "fix it now") intentionally *skips* a reading — leaving its `PowerPointId` `NULL` forever — when mapping it would collide with the `(PowerPointId, IntervalStart)` unique index (AD-20) against an already-mapped reading. `MapSmartPlugImportToPowerPoint.ExecuteAsync` still unconditionally marks the import `Completed` regardless of how many readings the fallback skipped, so nothing (status, UI, data) signals that orphaned rows were left behind — only a `LogWarning` line does, and nothing reads it. AD-20's own Dev Notes already named this exact gap ("doesn't protect ... the AwaitingPowerPointMapping → later-mapped path ... from ever producing an exact-timestamp duplicate") but left it unaddressed pending real evidence. That evidence now exists: 179,324 orphaned unmapped rows across two Power Points in one household (122,154 for "Netzwerk", 57,170 for "Steckdose Tür" — each an exact duplicate, matching on `HouseholdId`/`DeviceName`/`IntervalStart`/`IntervalEnd`/`KwhValue`, of an already-mapped reading), out of 467,787 total `SmartPlugReading` rows. This is NFR10's "no-silent-duplication" guarantee failing at exactly the boundary AD-20 flagged as unprotected.

**Acceptance Criteria:**

**Given** `UpdateMappingPerRowWithConflictToleranceAsync` skips a reading because it collides with an already-mapped reading at the same `(PowerPointId, IntervalStart)`
**When** the skipped reading's `KwhValue` and `IntervalEnd` exactly match the already-mapped reading's
**Then** the skipped reading is deleted instead of left behind with `PowerPointId` still `NULL` — no orphaned duplicate survives a mapping operation (closes AD-20's named gap)

**Given** the same collision
**When** the skipped reading's `KwhValue` or `IntervalEnd` does NOT match the already-mapped reading's (an unexpected divergence, e.g. a DST fall-back duplicate local timestamp with genuinely different data)
**Then** today's tolerant behavior is unchanged — the reading is left unmapped, the existing `LogWarning` fires — since this case must never silently discard data that might actually differ

**Given** the live production data this story was written from
**When** the one-time cleanup migration runs
**Then** every unmapped `SmartPlugReading` row that has an exact mapped twin (same `HouseholdId`, `DeviceName`, `IntervalStart`, `IntervalEnd`, `KwhValue`, differing only in `PowerPointId`/`RoomName`/`PowerPointName`/`SmartPlugImportId`) is deleted, and rows with no such twin (e.g. a device tag still genuinely `AwaitingPowerPointMapping`) are left untouched

**Given** the cleanup migration
**When** authored
**Then** it is added via `scripts/add-migration.sh` to both `Infrastructure.Migrations.Postgres` and `Infrastructure.Migrations.SqlServer` projects (AD-2) and verified against both engines via Testcontainers (matching Story 3.4's own migration-testing discipline), same delete-before-any-schema-change shape as `20260822165109_AddSmartPlugReadingUniqueIndex`'s precedent — except this migration adds no new index, it is cleanup only

**Dev Notes / Open Questions:**
- This is a data-integrity fix, not a user-facing correction — like Story 3.4's cleanup, it deliberately does not route through `AuditCorrection`/`IAuditCorrectionRecorder` (AD-11).
- Per AD-7, this does not retroactively recompute any `SmartPlugImportGap`/`StatusSnapshot` rows already computed before this story ships — same "immutable history, correct from this point forward" discipline as Story 3.4's cleanup.
- Out of scope: a second, unconfirmed finding from the same audit — two Power Points both named "Tür" in different Rooms in the audited household. Not folded into this story.
- Open Question for whoever picks this up: `MapSmartPlugImportToPowerPoint.ExecuteAsync` marks an import `Completed` even when the fallback above still leaves genuinely-divergent rows unmapped (the AC #2 case) — should that surface as something other than a plain `Completed` status? Treated as a non-goal here (a new import-status value is a bigger, UX-touching change than this story's data-hygiene scope) unless Ralf says otherwise during dev-story activation.

## Story 3.8: Smart Plug Bulk-Write Throughput Spike

As the team responsible for shipping AD-23's `BulkInsertOrUpdateAsync` write path safely,
I want to measure `EFCore.BulkExtensions`' real insert/upsert throughput and cancellation/rollback behavior against the project's own real Azure SQL Basic and Postgres databases, at realistic Smart Plug data scale, using dedicated disposable spike-only tables,
So that AD-23's write path is adopted at full scale only once real numbers back the "should be faster" assumption, and the NFR1 Tier-3 time budget `deferred.md` is deliberately waiting on gets set from measured data instead of a guess.

**Context (why this is needed):** AD-23 (2026-09-03 brainstorming/architecture session with Ralf) itself names a hard precondition before its `BulkInsertOrUpdateAsync` write path may ship broadly: a technical spike against the project's real Azure SQL Basic and Postgres databases, using dedicated disposable spike-only tables, never the live `SmartPlugReading` schema — verifying write throughput, that cancellation/transactional rollback still holds "no partial row survives cancellation," and that AD-23's required parent-row/reading-write transactional atomicity actually holds under a mid-write failure. Three `deferred.md` items are waiting specifically on this spike's findings: the still-undetermined **NFR1 Tier 3 concrete time budget** (explicitly deferred until this spike produces real numbers), the **device-swap gap** (explicitly out of scope for this spike), and the note that **AD-23's bulk-write contract is coupled to today's one-row-per-reading storage grain** (this spike's numbers are only valid for that grain). Sizing is derived, not arbitrary: Eve Home's single-device full-history export grew from 108,715 rows (2026-06-20) to 117,782 rows (2026-08-22) at ~143 rows/day — projecting to ~120,000 rows by this story's authoring date; the 2026-08-26 production audit behind Story 3.7 measured 467,787 total rows in one real household before its own duplicate cleanup — used here as the pre-existing-table scale a bulk write must still perform well against.

**Acceptance Criteria:**

**Given** this spike's own database objects
**When** created
**Then** they are dedicated, disposable, clearly-namespaced spike-only tables created in both the real, already-provisioned Azure SQL Basic database and the real, already-running Postgres database (not Testcontainers-ephemeral instances for either) — and no production code path or the live `SmartPlugReading`/`SmartPlugImport` schema is touched at all

**Given** the spike tables' shape
**When** designed
**Then** they reproduce `SmartPlugReading`'s structurally relevant column types/widths and both of AD-23's real match-key unique indexes — `(PowerPointId, IntervalStart)` and the partial `(HouseholdId, IntervalStart) WHERE PowerPointId IS NULL` — plus a spike parent table mirroring `SmartPlugImport`'s FK relationship, so measured numbers reflect the real constraint-checking cost

**Given** the derived sizing above
**When** synthetic data is generated
**Then** it includes a ~120,000-row single-device/first-full-import batch, a ~470,000-row pre-load baseline, a ~500-row typical-incremental-delta batch, and a ~5,000-row `PowerPointId IS NULL` batch — all synthetic, never copied from real household data

**Given** insert-heavy scenarios (an empty table, and the ~470,000-row pre-loaded table)
**When** `BulkInsertOrUpdateAsync` inserts the ~120,000-row batch into each
**Then** elapsed time and rows/sec are measured and recorded, on both Postgres and SQL Server (Basic tier)

**Given** update-heavy/upsert scenarios matching each of AD-23's two real match-key configurations
**When** `BulkInsertOrUpdateAsync` re-submits a 100%-overlapping batch and a typical incremental delta via `[PowerPointId, IntervalStart]`, and an insert-then-resubmission via `[HouseholdId, IntervalStart]` scoped to `PowerPointId IS NULL`
**Then** elapsed time and rows/sec are measured and recorded for each, on both providers, and it is explicitly confirmed the `PowerPointId IS NULL` match key never matches an already-mapped row sharing the same `(HouseholdId, IntervalStart)`

**Given** a parent-row insert and a large `BulkInsertOrUpdateAsync` call wrapped in one explicit transaction
**When** cancelled via `CancellationToken` partway through the bulk write
**Then** it is verified, on both providers, that zero reading rows and zero parent rows from that batch survive — confirming both the "no partial row survives cancellation" invariant and the explicit-transaction atomicity claim hold under a real mid-write cancellation

**Given** every scenario has run on both providers
**When** this story concludes
**Then** a written result (throughput table, cancellation/rollback finding, explicit go/no-go recommendation, and a recommended concrete NFR1 Tier-3 time budget derived from the measured numbers) is committed to `_bmad-artifacts/implementation/spike-results/3-8-bulk-write-throughput-spike-results.md`

**Given** the spike has concluded
**When** cleanup runs
**Then** every spike-only table/object is dropped from both databases, verified via a schema query confirming zero `Spike_*` objects remain

**Dev Notes / Open Questions:**
- This spike deliberately runs against real databases, not Testcontainers — the whole point is measuring Azure SQL Basic tier's real 5-DTU throttling and real network latency, which an ephemeral local container cannot reproduce. A named, one-time exception to this project's normal Testcontainers-everywhere discipline.
- A real operational caution: this shares the live Basic-tier (5 DTU) SQL Server with production traffic (only one Azure SQL Server instance exists at this project's scale) — run the Azure-SQL-side scenarios at a low-usage time and drop spike tables promptly between runs.
- Open question for Ralf: whether to request a temporary Azure SQL tier bump for the duration of this spike's Azure-SQL-side runs, and whether there's a preferred low-usage execution window. Everything else is specified precisely enough to proceed without further confirmation.
- Story 3.9 (the actual AD-22/AD-23 implementation) is explicitly blocked on this story's own written "go" recommendation — see 3.9's own header note and Dev Notes.

## Story 3.9: Watermark Correction Detection & Bulk-Write Adoption

**BLOCKED BY: Story 3.8.** This story's AD-23 tasks (bulk-write adoption) may not begin until Story 3.8 has been dev-agent-executed and its written result states a "go" recommendation. AD-22's tasks (watermark correction detection) have no such dependency and may proceed independently.

As a Household member whose Smart Plug vendor occasionally revises an already-reported reading, and whose imports are growing large enough that write throughput and reliability now matter,
I want revised historical values detected and safely corrected with a full audit trail, and new/incremental Smart Plug writes to use a proven, throughput-tested bulk-write path instead of today's per-row-fallback machinery,
So that my data stays accurate without a silent, undetected correction slipping in, and large or repeated imports stay fast and reliable regardless of how much history has accumulated.

**Context (why this is needed):** AD-22 and AD-23 (2026-09-03 brainstorming/architecture session with Ralf, both reviewed and corrected by a 4-pass reviewer gate before landing in the spine) close two related gaps in Epic 3's existing Smart Plug import machinery. AD-22: today's watermark (Story 3.4/AD-9/AD-20) only compares `IntervalStart` — if a vendor ever revises a `KwhValue` it already reported at an already-stored timestamp, nothing notices; the fix extends the watermark to also carry the stored `KwhValue`, has both parsers include (not skip) the exact boundary row, and moves the comparison/correction decision into the orchestration layer (`SmartPlugImportRepository`/`ProcessSmartPlugImport`) — never the parser, which cannot structurally call `IAuditCorrectionRecorder` (an early draft had the parser doing this; the reviewer gate caught it as structurally impossible). AD-23: `SmartPlugImportRepository.AddAsync`'s existing pre-check/fast-path/per-row-fallback machinery (built incrementally across Stories 3.4/3.7's review rounds) is replaced by one `BulkInsertOrUpdateAsync` call via `EFCore.BulkExtensions`, applied uniformly regardless of batch size, branching its match key between AD-20's two real unique indexes; `UpdateMappingAsync` is an explicit, named carve-out, untouched by this AD. The two ADs interact at one point: a boundary-row correction is excluded from whatever batch AD-23's bulk path handles — never re-inserted through it.

**Acceptance Criteria:**

**Given** `FindLatestReadingIntervalStartByPowerPointAsync`
**When** its return shape is extended
**Then** it returns the latest stored reading's `Id`, `IntervalStart`, and `KwhValue` together (a named triple), not `IntervalStart` alone

**Given** `EveHomeXlsxParser`'s early-stop condition and `MerossCsvParser`'s per-row filter condition
**When** changed
**Then** each changes from `<= watermark` to `< watermark` — the row exactly at the watermark's `IntervalStart`/day is now read and included in the parsed result instead of excluded, with `ISmartPlugParser.Parse`'s own public signature unchanged

**Given** a parsed result that includes a boundary row
**When** `ProcessSmartPlugImport` compares its `KwhValue` to the resolved watermark's stored `KwhValue`
**Then** an exact match drops the row (nothing to write); a divergent value performs a narrow, `KwhValue`-only update of the existing stored row plus an `IAuditCorrectionRecorder.RecordAsync` call — never touching `RoomName`/`PowerPointName`/`DeviceName` (AD-10) — and either way the row is excluded from whatever path handles genuinely new rows

**Given** more than one row shares the exact watermark `IntervalStart` (a DST-fold case)
**When** resolved
**Then** the first-encountered row in parse order is treated as the boundary row, every other row sharing that `IntervalStart` is also dropped with a distinguishing log entry, and at most one correction attempt is made

**Given** Story 3.8 has returned a "go" recommendation
**When** `SmartPlugImportRepository.AddAsync` is rewritten
**Then** its existing pre-check/fast-path/per-row-fallback machinery is replaced by one `BulkInsertOrUpdateAsync` call, applied uniformly regardless of batch size, with `PropertiesToExclude=[Id]` and `UpdateByProperties` branching between AD-20's two real unique indexes on the same condition `ProcessSmartPlugImport` already branches on

**Given** the parent `SmartPlugImport` row and its readings' bulk write
**When** persisted
**Then** both are wrapped in one explicit database transaction, since `BulkInsertOrUpdateAsync` does not participate in `SaveChangesAsync`'s pipeline

**Given** `UpdateMappingAsync`
**When** this story ships
**Then** it is left completely untouched, with an explicit code comment stating why (small/bounded re-tagging volume, not a bulk insert-or-upsert-by-content decision)

**Given** the new `EFCore.BulkExtensions.SqlServer` dependency forces `Microsoft.Data.SqlClient` past AD-21's currently-verified version
**When** the package is added
**Then** the actual resolved version is re-verified against the build lockfile and AD-21's Entra-auth connection-string modes are confirmed to still work, never assumed

**Dev Notes / Open Questions:**
- This story is genuinely blocked on Story 3.8's real output. No spike numbers exist yet at the time this story was drafted — its exact throughput figures, go/no-go verdict, and recommended NFR1 Tier-3 budget are deliberately not fabricated here. Whoever picks this up must read Story 3.8's actual written result before writing any AD-23 code.
- The device-swap gap and the storage-grain dependency (`deferred.md`) are both explicitly out of scope for this story, same as they were for Story 3.8.
- No settings/UI surface, no retroactive re-detection of gaps/Status against corrected readings (AD-7 discipline, same as every prior Epic 3 cleanup story).
