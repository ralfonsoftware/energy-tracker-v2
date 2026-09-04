---
baseline_commit: c4007b8
---

# Story 3.9: Watermark Correction Detection & Bulk-Write Adoption

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

**BLOCKED BY: Story 3.8 (Smart Plug Bulk-Write Throughput Spike).** This story's AD-23 tasks (Tasks 7–9 below — replacing `AddAsync`'s machinery with `BulkInsertOrUpdateAsync`) may not begin until Story 3.8 has been dev-agent-executed and its written result (`_bmad-artifacts/implementation/spike-results/3-8-bulk-write-throughput-spike-results.md`) states a "go" recommendation. **Check that file's actual content before writing any `BulkInsertOrUpdateAsync` call — do not rely on story numbering/sequence alone to infer 3.8 ran, and do not assume "go."** AD-22's tasks (Tasks 3–6 below — watermark correction detection) have no such dependency and may proceed regardless of 3.8's outcome. If 3.8 hasn't run yet or returned no-go/go-with-caveats when this story is picked up: implement AD-22 in full, stop before AD-23's tasks, and flag the block to Ralf rather than guessing at `BulkInsertOrUpdateAsync`'s configuration from unmeasured assumptions.

## Story

As a Household member whose Smart Plug vendor occasionally revises an already-reported reading, and whose imports are growing large enough that write throughput and reliability now matter,
I want revised historical values detected and safely corrected with a full audit trail, and new/incremental Smart Plug writes to use a proven, throughput-tested bulk-write path instead of today's per-row-fallback machinery,
so that my data stays accurate without a silent, undetected correction slipping in, and large or repeated imports stay fast and reliable regardless of how much history has accumulated.

## Context (why this is needed)

AD-22 and AD-23 were added to the architecture spine in the same session (2026-09-03 brainstorming/architecture session with Ralf), closing two related gaps in Epic 3's existing Smart Plug import machinery, both already fully specified — and, per their own text, reviewed and corrected — in `invariants-rules.md`:

**AD-22 (watermark corruption detection):** today's watermark (Story 3.4/AD-9/AD-20) only compares `IntervalStart` — if Eve Home or Meross ever revises a `KwhValue` it already reported at a timestamp this system has already stored, nothing notices. The fix extends the watermark to also carry the stored `KwhValue`, has both parsers include (not skip) the exact boundary row so the orchestration layer can see it, and moves the actual comparison/correction decision into `SmartPlugImportRepository`/`ProcessSmartPlugImport` — never the parser, which structurally cannot call `IAuditCorrectionRecorder` (an early draft of this AD had the parser doing exactly that; a 4-pass reviewer gate caught it as structurally impossible and it was corrected before landing in the spine).

**AD-23 (bulk-write adoption):** `SmartPlugImportRepository.AddAsync`'s existing pre-check (`AnyExistingReadingAtSameKeyAsync`) → fast-path (`AddRangeAsync`/`SaveChangesAsync`) → per-row-fallback (`AddWithPerRowConflictToleranceAsync`) machinery — built incrementally across Stories 3.4 and 3.7's review rounds — is replaced by a single `BulkInsertOrUpdateAsync` call via `EFCore.BulkExtensions`, applied uniformly regardless of batch size, branching its match key between AD-20's two real unique indexes. `UpdateMappingAsync` is an explicit, named carve-out — it keeps its own existing mechanism untouched, because it operates on a fundamentally different, small/bounded re-tagging volume, not a bulk insert-or-upsert-by-content decision.

Both ADs interact at one specific point (AD-22's own text says so explicitly): a boundary-row correction is excluded from whatever batch AD-23's bulk path handles — it is never re-inserted through `BulkInsertOrUpdateAsync`, since it's a narrow, single-row `KwhValue`-only update against an *existing* row, not a new-or-upserted one.

## Acceptance Criteria

**AD-22 — watermark correction detection**

1. **Given** `SmartPlugImportRepository.FindLatestReadingIntervalStartByPowerPointAsync`, **when** its return shape is extended, **then** it returns the latest stored reading's `Id`, `IntervalStart`, **and** `KwhValue` together (a named triple, e.g. `SmartPlugReadingWatermark(Guid Id, DateTimeOffset IntervalStart, decimal KwhValue)`) instead of `DateTimeOffset?` alone — `null` only when the Power Point has no persisted reading yet, exactly as today.
2. **Given** `EveHomeXlsxParser.Parse`'s forward-streaming early-stop condition, **when** changed, **then** it changes from `reading.IntervalStart <= watermark` to `reading.IntervalStart < watermark` — the row exactly at the watermark's `IntervalStart` is read, parsed, and added to the result before the loop breaks one row later than today.
3. **Given** `MerossCsvParser.Parse`'s per-row filter condition, **when** changed, **then** it changes from `dayStart <= watermark` to `dayStart < watermark` — the single row exactly at the watermark's `IntervalStart` is included in the result instead of skipped, with every other row still read in full (Meross's unordered-file discipline, unchanged).
4. **Given** `ISmartPlugParser.Parse`'s public signature, **when** this story ships, **then** it is unchanged (`DateTimeOffset? watermark` — the parser is never told the stored `KwhValue`, only the caller compares it) — the comparison/correction decision belongs entirely to the orchestration layer, never the parser.
5. **Given** a parsed result that includes a boundary row (its `IntervalStart` exactly equal to the resolved watermark's `IntervalStart`), **when** `ProcessSmartPlugImport` locates it (matched by `IntervalStart`, first-encountered in parse order if more than one row shares that exact `IntervalStart` — DST-fold discipline, below) and compares its `KwhValue` to the watermark's stored `KwhValue`, **then**: if equal, the row is dropped from the batch (an exact re-report, nothing to write); if different, the existing stored row (identified by the watermark's `Id`) has **only** its `KwhValue` column updated via a narrow, explicit set-based update — never a whole-row overwrite — and `IAuditCorrectionRecorder.RecordAsync` is called (`entityType="SmartPlugReading"`, `entityId=<watermark.Id>`, `fieldName="KwhValue"`, the old and new values) — either way, this row never reaches whatever path (AD-23, Task 7) handles genuinely new rows.
6. **Given** the narrow `KwhValue` update above, **when** performed, **then** it touches `KwhValue` and *only* `KwhValue` — never `RoomName`, `PowerPointName`, or `DeviceName` (AD-10's by-value snapshot fields; re-deriving them here would reproduce the exact "live join rewrites history" failure AD-10 exists to prevent).
7. **Given** more than one row in the parsed result shares the exact watermark `IntervalStart` (a DST-fold case — the same discipline `SmartPlugImportRepository`'s existing per-row-fallback comments already document for Story 3.4's ordinary insert path), **when** the boundary row is resolved, **then** the first-encountered row in parse order is treated as *the* boundary row for comparison, every other row sharing that same `IntervalStart` is also dropped from the batch with a log entry distinguishing this from an ordinary correction or an ordinary drop, and at most one correction attempt is ever made against the one stored row.
8. **Given** this AD's own stated scope limit, **when** implemented, **then** it only ever compares/corrects a revision to an already-seen `(PowerPointId, IntervalStart)` on the same device/Power Point stream — it does not attempt to detect a different physical device continuing to report under the same Power Point name at new, not-yet-seen timestamps (the device-swap gap remains explicitly open in `deferred.md`, unaddressed by this story).

**AD-23 — bulk-write adoption**

9. **Given** Story 3.8 has returned a "go" recommendation (see the blocking note above — this AC does not apply until it has), **when** `SmartPlugImportRepository.AddAsync` is rewritten, **then** its existing `AnyExistingReadingAtSameKeyAsync` pre-check, plain `AddRangeAsync`/`SaveChangesAsync` fast path, and `AddWithPerRowConflictToleranceAsync`/`DeletePartiallyPersistedImportAsync` per-row-fallback-and-cleanup machinery are all removed, replaced by one `BulkInsertOrUpdateAsync` call against `EnergyTrackerDbContext`, applied uniformly regardless of batch size (no row-count threshold or small/large branch).
10. **Given** the new `BulkInsertOrUpdateAsync` call, **when** configured, **then** `PropertiesToExclude = [nameof(SmartPlugReading.Id)]` on every call, and `UpdateByProperties` branches on the same condition `ProcessSmartPlugImport` already branches on (whether the batch's Power Point is known): `[PowerPointId, IntervalStart]` when known (matching `IX_SmartPlugReadings_PowerPointId_IntervalStart`), or `[HouseholdId, IntervalStart]` when not (matching the partial `IX_SmartPlugReadings_HouseholdId_IntervalStart_WhenPowerPointIdNull` index, scoped to `PowerPointId IS NULL` rows) — the two real unique indexes named in `SmartPlugReadingConfiguration.cs`.
11. **Given** the parent `SmartPlugImport` row and its readings' `BulkInsertOrUpdateAsync` write, **when** persisted, **then** both are wrapped in one explicit database transaction (`dbContext.Database.BeginTransactionAsync`/commit) — `BulkInsertOrUpdateAsync` does not participate in `SaveChangesAsync`'s pipeline, so without this explicit wrapping, "no partial import observable" silently disappears.
12. **Given** `UpdateMappingAsync` (Story 3.2/3.4/3.7's existing mapping-resolution machinery), **when** this story ships, **then** it is left completely untouched — a deliberate, explicit carve-out (stated in code comments, not silently omitted) because it operates on a fundamentally different, small/bounded, already-persisted-and-validated re-tagging volume, not a bulk insert-or-upsert-by-content decision over a fresh, potentially huge parsed batch.
13. **Given** the new NuGet dependency, **when** referenced, **then** `EnergyTracker.Infrastructure.csproj` references `EFCore.BulkExtensions.Core`, `EFCore.BulkExtensions.SqlServer`, and `EFCore.BulkExtensions.PostgreSql` at version `10.0.1` (added to `Directory.Packages.props` per this project's central package management convention) — **never** the umbrella `EFCore.BulkExtensions` package.
14. **Given** `EFCore.BulkExtensions.SqlServer` 10.0.1 requires `Microsoft.Data.SqlClient >= 6.1.4` (a forced transitive bump past AD-21's currently-verified `6.1.1`), **when** the package is added, **then** the actual resolved `Microsoft.Data.SqlClient` version is re-verified against the build lockfile (`dotnet list package --include-transitive`), and AD-21's `Authentication=Active Directory *` connection-string modes are confirmed to still work cleanly against it — not assumed to be a no-op because the bump stays within the `6.1.x` line.

## Tasks / Subtasks

- [x] **Task 1: Confirm Story 3.8's go/no-go verdict before touching any AD-23 code** (AC: #9 gate)
  - [x] Read `_bmad-artifacts/implementation/spike-results/3-8-bulk-write-throughput-spike-results.md`. If it does not exist, or its recommendation is not an unambiguous "go," implement Tasks 3–6 (AD-22) only, then stop and escalate — do not write Tasks 7–9's code speculatively "to be safe."
  - [x] If 3.8 returned "go," note its measured throughput numbers and recommended NFR1 Tier-3 budget in this story's own Completion Notes once implementation runs — Dev Notes below deliberately does not restate invented numbers.

- [x] **Task 2: Add the `EFCore.BulkExtensions` package references** (AC: #13, #14)
  - [x] Add `EFCore.BulkExtensions.Core`, `EFCore.BulkExtensions.SqlServer`, `EFCore.BulkExtensions.PostgreSql` at `10.0.1` to `Directory.Packages.props`; reference all three (no `Version=` in the `.csproj`, per this project's central package management rule) from `src/EnergyTracker.Infrastructure/EnergyTracker.Infrastructure.csproj`.
  - [x] Run `dotnet list package --include-transitive` (or inspect the restored lockfile) and record the actual resolved `Microsoft.Data.SqlClient` version — resolves to `6.1.4`, exactly the package's stated minimum. Updated AD-21's `[ADOPTED]` stale-fact note in `invariants-rules.md`. **Partially open:** live-Entra-auth-against-real-Azure-SQL re-verification could not be performed in this sandboxed session (no Azure credentials, same gap Story 3.8 recorded) — flagged for Ralf, see Completion Notes.

- [x] **Task 3: `FindLatestReadingIntervalStartByPowerPointAsync` return-shape change** (AC: #1)
  - [x] Add `public sealed record SmartPlugReadingWatermark(Guid Id, DateTimeOffset IntervalStart, decimal KwhValue);` alongside `ISmartPlugImportRepository` (same placement pattern as `SmartPlugParseResult` alongside `ISmartPlugParser`).
  - [x] Change the interface method's return type to `Task<SmartPlugReadingWatermark?>`; update `SmartPlugImportRepository`'s implementation to project `Id`, `IntervalStart`, `KwhValue` instead of `IntervalStart` alone (still `OrderByDescending(r => r.IntervalStart)`, still `null` only when no reading exists for the Power Point).
  - [x] Update every call site — currently only `ProcessSmartPlugImport.cs:54` — for the new return shape (Task 6 below).

- [x] **Task 4: `EveHomeXlsxParser` boundary-row inclusion** (AC: #2)
  - [x] Change the early-stop comparison at `EveHomeXlsxParser.cs`'s data-row loop from `reading.IntervalStart <= watermark` to `reading.IntervalStart < watermark`. Update the adjacent comment (currently describing "rows at or older than that watermark are never materialized") to reflect that the row *at* the watermark is now included, and re-verify (via a test, Task 10) that the loop still stops one row after emitting it — not two.

- [x] **Task 5: `MerossCsvParser` boundary-row inclusion** (AC: #3)
  - [x] Change the filter comparison at `MerossCsvParser.cs`'s per-row loop from `dayStart <= watermark` to `dayStart < watermark`. Confirm (via a test, Task 10) the row exactly at the watermark's day is now present in the returned readings list.

- [x] **Task 6: `ProcessSmartPlugImport` — boundary-row comparison, narrow update, audit-correction call** (AC: #4, #5, #6, #7, #8)
  - [x] Inject `IAuditCorrectionRecorder` into `ProcessSmartPlugImport`'s primary constructor (new parameter, alongside the four existing ones).
  - [x] Change the local `watermark` variable's type from `DateTimeOffset?` to `SmartPlugReadingWatermark?`; pass `watermark?.IntervalStart` into `parser.Parse(...)` (AC #4 — the parser's own signature is unchanged).
  - [x] Immediately after `parser.Parse` returns and **before** the existing `if (readings.Count == 0)` special-case block, perform the boundary-row resolution: if `watermark is not null`, find the first row in `parseResult.Readings` (in parse order) whose `IntervalStart == watermark.IntervalStart`. If found:
    - Compare its `KwhValue` to `watermark.KwhValue`. Equal → drop it from the working readings list (nothing to write). Different → call a new repository method (Task 7) to update the existing stored row's `KwhValue` by `watermark.Id`, then call `auditCorrectionRecorder.RecordAsync(householdId, "SmartPlugReading", watermark.Id, "KwhValue", <old value formatted>, <new value formatted>, cancellationToken)` — match the exact old/new-value string formatting convention the existing Meter Reading/Tariff `IAuditCorrectionRecorder` call sites already use (grep `RecordAsync(` before writing this call; don't invent a new formatting convention here).
    - Either way, drop the row from the working readings list — it must never reach the AD-23 bulk-write path (AC #5's closing clause).
    - **DST-fold discipline (AC #7):** if more than one row in `parseResult.Readings` shares that exact `IntervalStart`, drop every one of them from the working list (only the first is compared/corrected as above), and log a warning distinguishing "multiple rows at the watermark boundary, DST-fold assumed" from an ordinary single-row correction.
  - [x] Recompute `readings.Count == 0` using the post-drop working list before entering the existing zero-rows branch — a batch that had exactly one row (the boundary row, exact match, now dropped) must fall into the existing "nothing new, Completed with zero readings" branch, not a new bespoke path.
  - [x] This task's logic only ever runs when `matchedPowerPoint is not null` (the only condition under which `watermark` is ever resolved non-null today) — no new branch on vendor is introduced (AD-9 stays intact).

- [x] **Task 7: `SmartPlugImportRepository.AddAsync` — bulk-write replacement** (AC: #9, #10, #11) — **unblocked: Ralf resolved Finding #2's design gap (raw-SQL upsert + AD-2 amendment), see Completion Notes**
  - [x] Add a new method (e.g. `UpdateReadingKwhValueAsync(Guid readingId, decimal newKwhValue, CancellationToken)`) implementing Task 6's narrow update as a set-based `ExecuteUpdateAsync` (`dbContext.SmartPlugReadings.Where(r => r.Id == readingId).ExecuteUpdateAsync(s => s.SetProperty(r => r.KwhValue, newKwhValue), cancellationToken)`) — same set-based idiom `UpdateMappingAsync` already uses in this same class. Implemented now: this is AD-22's own AC #5/#6 narrow update, does not touch `BulkInsertOrUpdateAsync`/`EFCore.BulkExtensions` at all, and is not blocked by Finding #2 below.
  - [x] Rewrote `AddAsync`: opens an explicit transaction, adds/saves the `SmartPlugImport` parent row, then — only when `readings.Count > 0`, after de-duplicating by match key (see below) — branches per AC #10: known-PowerPoint batches go through `BulkInsertOrUpdateAsync` (`PropertiesToExcludeOnUpdate`, not the originally-specified blanket `PropertiesToExclude` — Story 3.8 spike Finding #1), `AwaitingPowerPointMapping` batches go through a hand-written, provider-native raw-SQL upsert instead (AD-2 `[AMENDED]`, Ralf's explicit decision after Story 3.8 spike Finding #2 showed `UpdateByProperties` cannot safely target that path's partial index on either provider). Commits the transaction at the end; relies on the transaction's own automatic rollback-on-dispose for "no partial row survives cancellation" — no hand-written cleanup.
  - [x] Deleted `AnyExistingReadingAtSameKeyAsync`, `AddWithPerRowConflictToleranceAsync`, and `DeletePartiallyPersistedImportAsync` outright (grepped first — no other references, all three were private and only reachable from the old `AddAsync`).
  - [x] **Verified empirically against real Postgres:** `BulkInsertOrUpdateAsync` (and the raw-SQL upsert, same underlying Postgres restriction) throws `"ON CONFLICT DO UPDATE command cannot affect row a second time"` on a within-batch match-key collision — it does not silently keep one or double-apply both. Resolved by de-duplicating the incoming batch (first-encountered-wins, logged) immediately before either write path runs.
  - [x] Added an explicit code comment on `UpdateMappingAsync` (AC #12) stating the carve-out reasoning verbatim from AD-23's own text.
  - **Two further empirical findings beyond Story 3.8's spike, both resolved and documented in `invariants-rules.md`'s AD-2/AD-23 `[AMENDED]` bullets and this file's Completion Notes:** (1) Postgres's `CREATE INDEX CONCURRENTLY` cannot run inside the required ambient transaction — fixed via a new migration (`AddSmartPlugReadingPowerPointIntervalStartUniqueConstraint`, both provider projects) promoting the existing unique index to a genuine `pg_constraint`, per Ralf's explicit choice; (2) even after that migration, the library's own constraint lookup still resolved the wrong schema on this call path (a library quirk, not a schema problem) — worked around via `BulkConfig.CustomDestinationTableName`, Postgres-only.

- [x] **Task 8: Verify the second match-key branch's partial-index targeting** (AC: #10)
  - [x] Confirmed via a dedicated dual-provider test (`AddAsync_never_confuses_an_AwaitingPowerPointMapping_reading_with_a_same_timestamp_mapped_reading`, run against real Postgres AND real SQL Server): the raw-SQL upsert's own `WHERE PowerPointId IS NULL`/`target.[PowerPointId] IS NULL` predicate never matches or overwrites a mapped reading sharing the same `(HouseholdId, IntervalStart)`. (Story 3.8's own spike result for this exact question was superseded — Finding #2 showed `UpdateByProperties` doesn't work at all here, so Task 7 uses a different mechanism than the spike measured; this test verifies the new mechanism directly rather than trusting the stale spike result.)

- [x] **Task 9: Cross-reference the resolved AD-21 SqlClient version** (AC: #14)
  - [x] Updated `invariants-rules.md`'s AD-21 `[ADOPTED]` bullet's stale-fact note with the actually-resolved `Microsoft.Data.SqlClient` version (`6.1.4`). Live-Entra-auth-against-real-Azure-SQL re-verification flagged as still open — no Azure credentials in this session.

- [x] **Task 10: Tests** (AC: all)
  - [x] `tests/EnergyTracker.Infrastructure.Tests/EveHomeXlsxParserTests.cs`: extend the existing watermark test to assert the boundary row (`IntervalStart == watermark`) is now present in the result, and the loop still stops immediately after it (one row later than before, not two).
  - [x] `tests/EnergyTracker.Infrastructure.Tests/MerossCsvParserTests.cs`: extend similarly — assert the row exactly at the watermark day is now included.
  - [x] `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs`: updated tests for `FindLatestReadingIntervalStartByPowerPointAsync`'s new `SmartPlugReadingWatermark` return type, plus a new `UpdateReadingKwhValueAsync` test. New tests for the rewritten `AddAsync` (upsert via the primary match key, a large non-colliding batch) live here (Postgres-only, matching this file's existing per-method convention); the full both-providers `AddAsync` coverage (plain insert, upsert via each match-key path, parent+child transaction shape, cancellation-mid-write, within-batch duplicate collision) lives in a new sibling file, `SmartPlugImportRepositoryAddAsyncDualProviderTests.cs`, following `MeterReadingRepositoryTests.cs`'s existing abstract-base-plus-two-provider-subclasses pattern — needed here specifically because both AD-23 write paths are genuinely provider-different under the hood, unlike most of this codebase's Postgres-only repository tests.
  - [x] `tests/EnergyTracker.Application.Tests/ProcessSmartPlugImportTests.cs`: new tests for the boundary-row comparison — exact re-report (dropped, `IAuditCorrectionRecorder.Received(0)`, repository update method never called), divergent value (repository update called with the correct `Id`/new value, `IAuditCorrectionRecorder.RecordAsync` called with the correct `entityType`/`entityId`/`fieldName`/old/new values), and a DST-fold multi-row-at-boundary case (only the first row compared, all sharing rows dropped, distinct log). Added a regression test confirming the correction path never touches `RoomName`/`PowerPointName`/`DeviceName` (AC #6).
  - [x] Confirmed (not just assumed) that `UpdateMappingAsync`'s own existing tests still pass unmodified — the full `EnergyTracker.Infrastructure.Tests` run (88/88 green) includes them unchanged, itself the evidence for AC #12's carve-out.
  - [x] `.NET`: xUnit v3 MTP, Shouldly, NSubstitute against ports, `TestContext.Current.CancellationToken`, Testcontainers (both providers) for anything DB-touching — same conventions as every prior Epic 3 story.

## Dev Notes

### This story is genuinely blocked on Story 3.8's real output — do not fabricate a substitute

Story 3.8 has not run at the time this story was drafted. Its exact throughput numbers, its go/no-go verdict, and its recommended NFR1 Tier-3 time budget are all unknown right now — this story deliberately does **not** invent placeholder numbers for any of them. Whoever picks up this story for dev-story activation must read `_bmad-artifacts/implementation/spike-results/3-8-bulk-write-throughput-spike-results.md` (Story 3.8's actual written deliverable) before writing a single line of Task 7's code, and confirm the recommendation is "go" before proceeding past Task 6. If Story 3.8 surfaced a genuine problem (e.g. cancellation doesn't cleanly roll back on one provider, or the `[HouseholdId, IntervalStart]` match key does misfire against a mapped row), that finding overrides this story's own AC #9–#14 as written — treat them as provisional pending 3.8's actual result, and escalate to Ralf rather than silently reconciling a contradiction.

### Architecture constraints (binding, not optional)

- **AD-22 (this story, full text in `invariants-rules.md`)** — the parser layer change is symmetric across both vendors and does not alter `ISmartPlugParser`'s public contract; the comparison/correction write belongs entirely to the orchestration/repository layer.
- **AD-23 (this story, full text in `invariants-rules.md`)** — one bulk-write mechanism, no row-count threshold branch, `UpdateMappingAsync` untouched by name.
- **AD-10 — historical tag integrity.** The AD-22 correction touches `KwhValue` only, never the by-value snapshot fields. The AD-23 bulk write's blanket `PropertiesToExclude=[Id]` is safe specifically because its only reachable update branch is a narrow race window where the colliding row already shares the same snapshot values (AD-23's own reasoning) — this is a *different*, narrower guarantee than AD-22's explicit single-column update, and the two must not be conflated when writing code comments.
- **AD-11 — shared audit-correction mechanism.** This story's `IAuditCorrectionRecorder.RecordAsync` call is the mechanism's third call site (after Meter Reading and Tariff edits) — reuse it exactly as-is, no bespoke "corrected KwhValue" column anywhere.
- **AD-20 — the two real unique indexes** this story's `UpdateByProperties` branches between are named exactly in `SmartPlugReadingConfiguration.cs`'s comments: `IX_SmartPlugReadings_PowerPointId_IntervalStart` and `IX_SmartPlugReadings_HouseholdId_IntervalStart_WhenPowerPointIdNull`.
- **AD-21 — re-verify, never assume,** the resolved `Microsoft.Data.SqlClient` version and Entra-auth connection-string compatibility once `EFCore.BulkExtensions.SqlServer` forces a bump.
- **AD-7 — immutable history, unaffected.** The AD-22 correction updates an already-stored row's value going forward; it does not retroactively recompute any `SmartPlugImportGap`/`StatusSnapshot` row already computed from that row's old value — same discipline Stories 3.4/3.7 already established for their own cleanup migrations.
- **AD-2 — dual-provider.** No new migration is needed for this story (no schema change — `FindLatestReadingIntervalStartByPowerPointAsync`'s new return shape is a query-projection change only, and AD-23's two match-key indexes already exist from Story 3.4's migration). If this assumption turns out wrong during implementation (e.g. `BulkInsertOrUpdateAsync` needs a schema-visible marker column), stop and confirm with Ralf before adding one speculatively.

### Existing code to reuse, not reinvent

- `UpdateMappingAsync`'s existing `ExecuteUpdateAsync` set-based idiom (`SmartPlugImportRepository.cs:229-235`) — the pattern Task 7's new narrow `KwhValue`-only update method mirrors exactly.
- The existing Meter Reading/Tariff `IAuditCorrectionRecorder.RecordAsync` call sites — the exact old/new-value string-formatting convention Task 6's new call site must match, not reinvent.
- `SmartPlugImportRepository.cs`'s existing DST-fold log-message conventions (e.g. "possibly a DST fall-back duplicate local timestamp") — reuse the same phrasing/log level for AC #7's new DST-fold-at-boundary case, for consistency with the rest of this class's logging.
- `FindFirstReadingDateByPowerPointAsync`'s unchanged shape — not touched by this story, cited here only so it isn't confused with the method this story does change.

### Known non-goals (avoid scope creep)

- **The device-swap gap (`deferred.md`) is not addressed by this story** — AD-22's own text names this as explicitly out of scope; do not attempt a plausibility check against historical usage patterns here.
- **The storage-grain dependency (`deferred.md`) is not addressed** — this story's `BulkInsertOrUpdateAsync` configuration is written against `SmartPlugReading`'s current one-row-per-reading shape, exactly as Story 3.8's spike measured it. A future storage-grain rethink is a separate, larger change.
- **No settings/UI surface for anything in this story** — purely backend, no UX impact, confirmed no mockup/UX-DR references either AD.
- **No retroactive re-detection of gaps/Status against readings corrected by this story** — same AD-7 discipline as every prior Epic 3 story's cleanup work.
- **`UpdateMappingAsync`'s own machinery is explicitly not migrated to `BulkInsertOrUpdateAsync`** — AD-23 names this as a deliberate carve-out, not an oversight to "finish later" in this story.

### Project Structure Notes

- Backend files to modify: `src/EnergyTracker.Application/Ports/ISmartPlugImportRepository.cs` (new `SmartPlugReadingWatermark` record, method signature), `src/EnergyTracker.Application/ProcessSmartPlugImport.cs` (new `IAuditCorrectionRecorder` dependency, boundary-row comparison logic), `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` (return-type change, `AddAsync` rewrite, new narrow-update method, three deleted methods), `src/EnergyTracker.Infrastructure/Adapters/EveHomeXlsxParser.cs` (one operator change), `src/EnergyTracker.Infrastructure/Adapters/MerossCsvParser.cs` (one operator change).
- Build files: `Directory.Packages.props` (three new package versions), `src/EnergyTracker.Infrastructure/EnergyTracker.Infrastructure.csproj` (three new `PackageReference`s).
- No new migration expected (see AD-2 note above) — confirm this holds during implementation rather than assuming.
- No frontend changes — confirmed no UX impact for either AD.
- Fits the existing flat, one-class-per-file convention; no new files beyond the record type living alongside its existing interface (matching `SmartPlugParseResult`'s precedent).

### Testing standards summary

- .NET: xUnit v3 MTP, Shouldly, NSubstitute against ports, `TestContext.Current.CancellationToken`, Testcontainers (real Postgres **and** SQL Server) for anything touching `SmartPlugImportRepository`'s rewritten `AddAsync` — matching every prior Epic 3 story's discipline. Unlike Story 3.8's spike, this story's tests run in normal CI (Testcontainers, not the real Azure SQL instance) — the spike's real-database numbers only inform *configuration choices*, they don't change where this story's own tests run.
- Parser tests stay in `EnergyTracker.Infrastructure.Tests` (established precedent since Story 3.1).

### References

- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-22, #AD-23`] — full, reviewed AD text this story implements verbatim; treat every clause as binding.
- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-10, #AD-11, #AD-20, #AD-21`] — cross-referenced constraints (by-value snapshot fields, shared audit mechanism, the two real unique indexes, Entra-auth re-verification).
- [Source: `_bmad-artifacts/implementation/3-8-smart-plug-bulk-write-throughput-spike.md`] — this story's blocking dependency; its written result is the actual source of truth for whether/how AD-23's tasks proceed.
- [Source: `_bmad-artifacts/implementation/3-4-incremental-smart-plug-import.md`, `3-7-smart-plug-reading-duplicate-cleanup-on-late-mapping.md`] — the exact prior code (watermark mechanism, per-row-fallback machinery, DST-fold discipline) this story extends and partially replaces.
- [Source: `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs`, `EveHomeXlsxParser.cs`, `MerossCsvParser.cs`, `src/EnergyTracker.Application/Ports/ISmartPlugParser.cs`, `ISmartPlugImportRepository.cs`, `src/EnergyTracker.Application/ProcessSmartPlugImport.cs`, `src/EnergyTracker.Application/Ports/IAuditCorrectionRecorder.cs`, `src/EnergyTracker.Infrastructure/Configurations/SmartPlugReadingConfiguration.cs`] — exact current code this story modifies; the two real index names cited in AC #10 come from this configuration file's own comments.
- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/deferred.md`] — device-swap gap and storage-grain dependency, both explicitly out of scope for this story.
- [Source: `_bmad-artifacts/project-context.md`] — central package management convention (`Directory.Packages.props`), Testcontainers/xUnit v3 MTP/Shouldly/NSubstitute testing conventions.

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

None — no debugger/log-file artifacts beyond standard `dotnet build`/`dotnet test` console output.

### Completion Notes List

**Scope of this session — story complete. AD-22 (Tasks 3–6, 10) and AD-23 (Tasks 2, 7–9) both fully
implemented and tested against real Postgres AND real SQL Server. Two rounds of mid-session
escalation to Ralf were required (Story 3.8's split go/no-go verdict, then a genuine architecture
conflict AD-2 as originally written couldn't accommodate) — both resolved with his explicit
decisions before implementation proceeded. Full regression suite: Application 224/224, Infrastructure
88/88 (incl. Testcontainers, both providers), Api 141/141, Architecture 3/3 — 456/456 green.**

**Task 1 — Story 3.8's verdict, read in full:**
`_bmad-artifacts/implementation/spike-results/3-8-bulk-write-throughput-spike-results.md` returns
**"Go, with a required amendment — split AD-23's two match-key configurations into different
outcomes"**: unambiguous go for the primary `[PowerPointId, IntervalStart]` match-key path (AC
#4/#5/#6a — Finding #1's `PropertiesToExcludeOnUpdate` correction required, not the blanket
`PropertiesToExclude` this story's own AC #10 currently specifies), but **no-go** for the
`AwaitingPowerPointMapping` `[HouseholdId, IntervalStart]` match-key path (Finding #2 — throws on
both real providers once the table holds realistic multi-device data; the spike's own text frames
this as needing "its own design decision" before it can be adopted at all). Recommended NFR1 Tier-3
budget: **15 minutes**.

This is not the unambiguous "go" Task 1's own gate requires before Tasks 7–9 may begin — it is
exactly the "go-with-caveats" case this story's Dev Notes named in advance ("If Story 3.8 surfaced
a genuine problem... that finding overrides this story's own AC #9–#14 as written... escalate to
Ralf rather than silently reconciling a contradiction"). Per Task 1's explicit instruction,
implemented Tasks 3–6 (AD-22) first and stopped before Task 7's `AddAsync` rewrite, then asked Ralf
directly how to resolve Finding #2 before proceeding — see below.

**AD-22 implementation (Tasks 3–6, 10) — complete, all ACs (#1–#8) satisfied:**
- `SmartPlugReadingWatermark(Guid Id, DateTimeOffset IntervalStart, decimal KwhValue)` record added
  alongside `ISmartPlugImportRepository`; `FindLatestReadingIntervalStartByPowerPointAsync` now
  returns it instead of a bare `DateTimeOffset?`.
- `EveHomeXlsxParser`/`MerossCsvParser`: boundary-row comparison changed from `<=` to `<` so the
  row exactly at the watermark's `IntervalStart` is now parsed and returned (not skipped); Eve
  Home's early-stop still fires immediately after emitting it (one row later than before, not two).
- `ProcessSmartPlugImport` now takes `IAuditCorrectionRecorder` and `ILogger<ProcessSmartPlugImport>`
  as new constructor dependencies (both already DI-registered; `AddScoped<ProcessSmartPlugImport>()`
  resolves them with no `Program.cs` change needed). New private `ResolveWatermarkBoundaryAsync`
  runs immediately after `parser.Parse` and before the existing zero-rows branch: resolves the
  boundary row (first-encountered in parse order on a DST-fold tie, with every tied row dropped and
  a distinguishing warning logged), compares its `KwhValue` against the watermark's stored value,
  and either drops it (exact re-report) or calls the new narrow update + records a correction via
  `IAuditCorrectionRecorder` (old/new values formatted with `CultureInfo.InvariantCulture`, matching
  `EditMeterReading.cs`'s existing convention — the only pre-existing call site found via `grep
  RecordAsync(`). The boundary row never re-enters the batch either way.
- New port method `ISmartPlugImportRepository.UpdateReadingKwhValueAsync(Guid readingId, decimal
  newKwhValue, CancellationToken)`, implemented via `ExecuteUpdateAsync` (same idiom as
  `UpdateMappingAsync` in the same class) — this is Task 7's first bullet, but it's AD-22's own
  AC #5/#6 narrow-update mechanism, needs no `EFCore.BulkExtensions` package, and doesn't touch
  `AddAsync` at all, so it was implemented now rather than left blocked alongside the rest of Task 7.
- Confirmed `UpdateMappingAsync`'s own existing tests pass completely unmodified (evidence for
  AC #12's carve-out, once Task 7 eventually proceeds).

**Ralf's decision on Finding #2 (asked directly, first escalation):** hand-written raw-SQL upsert
for the `AwaitingPowerPointMapping` path (option (b) above — true single-statement atomicity over
option (a)'s weaker "keep the old per-row fallback" or a portable-but-weaker-atomicity two-phase
LINQ upsert, both of which were also offered).

**A second, deeper blocker surfaced immediately while implementing that choice — genuinely not
covered by Story 3.8's spike, and required a second escalation:** a literal raw-SQL upsert needs
provider-specific SQL text (Postgres `ON CONFLICT` vs. SQL Server `MERGE`), which conflicts with
AD-2's own "no provider-specific SQL fragment, never branched on elsewhere" rule as originally
written. Asked Ralf directly: portable-but-weaker LINQ upsert (AD-2-compliant, no spine change) vs.
provider-conditional raw SQL with an explicit AD-2 amendment (true atomicity, a real if narrow spine
change). **He chose the amendment.** Implemented as a single, narrowly-scoped `[AMENDED
2026-09-04, Story 3.9]` bullet on AD-2 itself in `invariants-rules.md` (not a silent code-level
workaround) — see that file for the full text; it explicitly names the scope limit ("no other
method... may cite this bullet as license for a third provider branch").

**AD-23 implementation (Tasks 2, 7–9) — complete, all ACs (#9–#14) satisfied, verified against BOTH
real Postgres and real SQL Server (Testcontainers), not just one provider:**
- `EFCore.BulkExtensions.Core`/`.SqlServer`/`.PostgreSql` (10.0.1) referenced from
  `EnergyTracker.Infrastructure.csproj`; `Microsoft.EntityFrameworkCore.SqlServer`/
  `Npgsql.EntityFrameworkCore.PostgreSQL` also referenced directly there (needed for
  `Database.IsNpgsql()`/`IsSqlServer()` — see AD-2's amendment). Resolved `Microsoft.Data.SqlClient`:
  `6.1.4` (exactly the package's own stated minimum) — recorded in AD-21's stale-fact note.
- `SmartPlugImportRepository.AddAsync` rewritten: one explicit transaction wraps the parent
  `SmartPlugImport` row and the readings write; readings are de-duplicated by `(PowerPointId,
  IntervalStart)` (first-encountered wins, logged) before either write path runs; known-PowerPoint
  batches go through `BulkInsertOrUpdateAsync` (`PropertiesToExcludeOnUpdate=[Id]`,
  `UpdateByProperties=[PowerPointId, IntervalStart]`); `AwaitingPowerPointMapping` batches go
  through a new hand-written raw-SQL upsert (`ExecuteSqlRawAsync`, positional `{n}` placeholders,
  one multi-row statement per call, provider-branched via `Database.IsNpgsql()`/`IsSqlServer()`).
  `AnyExistingReadingAtSameKeyAsync`/`AddWithPerRowConflictToleranceAsync`/
  `DeletePartiallyPersistedImportAsync` deleted outright.
- **Three genuinely new empirical findings, none covered by Story 3.8's spike (its own scenarios
  never combined an ambient transaction with `UpdateByProperties` on Postgres at all), each
  confirmed via direct reproduction against a real Postgres instance with server-side statement
  logging enabled, not guessed:**
  1. **Within-batch match-key collisions throw, not silently resolve** — `"ON CONFLICT DO UPDATE
     command cannot affect row a second time"` (Postgres's own restriction). This is Task 7's own
     previously-open risk, now resolved: de-duplicate before either write path (implemented above).
  2. **Postgres's `CREATE UNIQUE INDEX CONCURRENTLY` cannot run inside any transaction block**,
     including the one AD-23 itself requires for parent+child atomicity — `EFCore.BulkExtensions`
     only skips building its own temp version of this index when the match-key columns are already
     backed by a genuine `pg_constraint`, which a plain EF Core `HasIndex(...).IsUnique()` does not
     provide. Fixed via a new migration, `AddSmartPlugReadingPowerPointIntervalStartUniqueConstraint`
     (both provider projects, AD-2 convention — Postgres: `ALTER TABLE ... ADD CONSTRAINT ... UNIQUE
     USING INDEX`, reuses the existing index in place, no rebuild; SQL Server: documented no-op).
     This contradicts this story's own Dev Notes assumption that no new migration would be needed —
     confirmed with Ralf (third, smaller decision point in this session) before adding it.
  3. **Even after that migration, the library's own constraint lookup still resolved the wrong
     schema** on this exact call path (confirmed empirically the lookup query ran with
     `nr.nspname = ''` instead of `'public'`, a library quirk unrelated to the migration) — worked
     around via `BulkConfig.CustomDestinationTableName = "public.SmartPlugReadings"`
     (Postgres-only, gated on `Database.IsNpgsql()`; SQL Server's own default-schema resolution
     already returns `"dbo"` correctly and needs no override — confirmed via a real regression, this
     workaround applied unconditionally initially broke the SQL Server path with `Invalid object
     name 'public.SmartPlugReadings'` until gated).
- `SmartPlugImportRepositoryAddAsyncDualProviderTests.cs` (new file): abstract base + Postgres/
  SQL Server subclasses (mirrors `MeterReadingRepositoryTests.cs`'s existing pattern), covering both
  write paths, the cross-path isolation guarantee (AC #10/Task 8), transactional rollback on
  cancellation, and the within-batch dedup — all run against both real databases via Testcontainers.

**AD-21's live connection-string re-verification is the one open item left** — this sandboxed
session has no Azure SQL credentials (same gap Story 3.8's own Completion Notes recorded), so only
the version-resolution half of Task 2/AC #14's instruction is done. Flagged in AD-21's own
stale-fact note and here for Ralf to run against the real environment.

**Post-completion live end-to-end verification (Ralf's explicit request, separate from this
session's own dev-story pass) — found and fixed two further real bugs, neither the Testcontainers
suite ever exercised:** ran the full stack locally (Postgres via Docker, API via
`scripts/run-api.sh`, frontend via Vite) and drove a real Eve Home import through Chrome as the
real OIDC test user, using `sample-data/eve/2026-08-22_HiFi_Gesamtverbrauch.xlsx` — a genuine
~117,770-row full-history export, not a synthetic handful of rows. Two findings, both fixed and
covered by the existing regression suite (456/456 green after the fix):
1. **The `AwaitingPowerPointMapping` raw-SQL upsert doesn't scale to a realistic unmatched batch.**
   At 9 SQL parameters/row, ~118k rows in one statement blew straight past Postgres's hard
   65535-parameter limit (confirmed live: `Failed executing DbCommand`, no partial rows persisted —
   the transaction rolled back correctly). SQL Server's own practical ceiling (~2100) is tighter
   still. Fixed by chunking `UpsertAwaitingMappingReadingsAsync` into provider-sized batches (5000
   rows for Postgres, 200 for SQL Server) — still one multi-row statement per chunk, never a
   per-row loop, still inside the caller's one ambient transaction.
2. **A masked exception on `AddAsync` failure.** The DB-level rollback (via `await using var
   transaction`) doesn't untrack entities from the DbContext's change tracker — so when
   `ProcessSmartPlugImport.PersistFailedImportAsync` reused the same scoped DbContext to persist a
   Failed import with the same Id, it hit a second, unrelated "already being tracked"
   `InvalidOperationException` that masked the real one. Fixed with `dbContext.ChangeTracker.Clear()`
   in `AddAsync`'s catch block before rethrowing the original exception.

After both fixes: the same file imported cleanly into `AwaitingPowerPointMapping` (117,770 rows,
zero duplicates), mapped correctly to a newly-created Power Point (dashboard/Trend History total —
295.78 kWh — matched the DB exactly), and a same-file re-import afterward correctly exercised
AD-22's watermark/boundary logic end-to-end: only the single boundary row was parsed (not all 117k
again), recognized as an exact re-report, and completed with zero new writes in ~54ms.

**Sprint status:** `3-9-watermark-correction-detection-and-bulk-write-adoption` set to `review`
(Step 9) — all tasks complete, full regression suite green (456/456, including the two live-E2E
fixes above).

### File List

- `src/EnergyTracker.Application/Ports/ISmartPlugImportRepository.cs` (modified — `SmartPlugReadingWatermark` record added, `FindLatestReadingIntervalStartByPowerPointAsync` return type changed, new `UpdateReadingKwhValueAsync` method)
- `src/EnergyTracker.Application/ProcessSmartPlugImport.cs` (modified — new constructor deps, `ResolveWatermarkBoundaryAsync` boundary-row logic)
- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` (modified — `FindLatestReadingIntervalStartByPowerPointAsync` projection change, new `UpdateReadingKwhValueAsync`, `AddAsync` rewritten for AD-23's two write paths + dedup, old per-row-fallback machinery deleted)
- `src/EnergyTracker.Infrastructure/Adapters/EveHomeXlsxParser.cs` (modified — boundary-row inclusion)
- `src/EnergyTracker.Infrastructure/Adapters/MerossCsvParser.cs` (modified — boundary-row inclusion)
- `src/EnergyTracker.Infrastructure/EnergyTracker.Infrastructure.csproj` (modified — `EFCore.BulkExtensions.*`, `Microsoft.EntityFrameworkCore.SqlServer`, `Npgsql.EntityFrameworkCore.PostgreSQL` package references added)
- `Directory.Packages.props` (modified — `EFCore.BulkExtensions.Core`/`.SqlServer`/`.PostgreSql` 10.0.1 versions added)
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260904181608_AddSmartPlugReadingPowerPointIntervalStartUniqueConstraint.cs` (new — promotes the unique index to a real `pg_constraint`)
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260904181608_AddSmartPlugReadingPowerPointIntervalStartUniqueConstraint.Designer.cs` (new, EF-generated)
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260904181611_AddSmartPlugReadingPowerPointIntervalStartUniqueConstraint.cs` (new — documented no-op)
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260904181611_AddSmartPlugReadingPowerPointIntervalStartUniqueConstraint.Designer.cs` (new, EF-generated)
- `tests/EnergyTracker.Infrastructure.Tests/EveHomeXlsxParserTests.cs` (modified — watermark tests updated for inclusive boundary; large-scale test updated)
- `tests/EnergyTracker.Infrastructure.Tests/MerossCsvParserTests.cs` (modified — watermark test updated for inclusive boundary)
- `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs` (modified — watermark-shape test updated; new `UpdateReadingKwhValueAsync` test; `AddAsync` collision test rewritten for upsert semantics; large-batch test comment updated)
- `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryAddAsyncDualProviderTests.cs` (new — abstract base + Postgres/SQL Server subclasses covering both `AddAsync` write paths against both real providers)
- `tests/EnergyTracker.Application.Tests/ProcessSmartPlugImportTests.cs` (modified — existing tests updated for new watermark shape/constructor deps; five new boundary-row tests added)
- `_bmad-artifacts/implementation/sprint-status.yaml` (modified — 3-9 status `ready-for-dev` → `in-progress` → `review`)
- `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md` (modified — AD-2 `[AMENDED]` twice: the raw-SQL-upsert exception and the `CustomDestinationTableName` wrinkle; AD-23 `[AMENDED]` three times: the two-write-paths correction, the migration finding, the within-batch-dedup finding; AD-21 stale-fact note updated with resolved `Microsoft.Data.SqlClient` version)

## Change Log

- 2026-09-04: AD-22 (watermark correction detection, Tasks 3–6/10) implemented and fully tested —
  boundary row now parsed/compared/corrected via the shared `IAuditCorrectionRecorder` mechanism.
- 2026-09-04: AD-23 (bulk-write adoption, Tasks 2/7–9) implemented and fully tested against both
  real Postgres and real SQL Server, after two rounds of escalation to Ralf: (1) Story 3.8's spike
  returned a split go/no-go verdict, not the unambiguous "go" this story's Task 1 gate required —
  Ralf chose a hand-written raw-SQL upsert for the blocked `AwaitingPowerPointMapping` path; (2)
  that choice surfaced a genuine AD-2 conflict (provider-specific SQL vs. "never branched on
  elsewhere") — Ralf chose a narrow, explicit AD-2 amendment over a weaker-atomicity portable
  alternative. Implementation then surfaced three further empirical findings beyond Story 3.8's
  spike coverage (within-batch match-key collisions throw and must be de-duplicated; Postgres's
  `CREATE INDEX CONCURRENTLY` needed a new migration promoting the match-key index to a real
  constraint; a library schema-resolution quirk needed a `CustomDestinationTableName` workaround),
  all confirmed via direct reproduction against real databases and resolved without further
  escalation. Story moved to `review` — all tasks complete, 456/456 tests green.
- 2026-09-04: Post-completion live end-to-end verification (Ralf's request) — full stack run
  locally, real Eve Home import (~117,770 rows) driven through Chrome as the real OIDC test user.
  Found and fixed two further real bugs neither the Testcontainers suite exercised: the
  `AwaitingPowerPointMapping` raw-SQL upsert didn't scale to a realistic unmatched batch (Postgres's
  65535-parameter limit, unchunked before this) — fixed via provider-sized chunking; and a failure
  inside `AddAsync` left a stale tracked entity that masked the real exception on the next call —
  fixed via `ChangeTracker.Clear()` in the catch path. Verified after the fix: the same file
  imports cleanly, maps correctly to a real Power Point, and a same-file re-import correctly
  exercises AD-22's watermark/boundary logic (single-row parse, zero new writes). Full regression
  suite re-run clean: 456/456.
