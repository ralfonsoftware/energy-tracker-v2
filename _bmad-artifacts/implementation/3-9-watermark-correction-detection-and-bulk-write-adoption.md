---
baseline_commit: c4007b8
---

# Story 3.9: Watermark Correction Detection & Bulk-Write Adoption

Status: ready-for-dev

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

- [ ] **Task 1: Confirm Story 3.8's go/no-go verdict before touching any AD-23 code** (AC: #9 gate)
  - [ ] Read `_bmad-artifacts/implementation/spike-results/3-8-bulk-write-throughput-spike-results.md`. If it does not exist, or its recommendation is not an unambiguous "go," implement Tasks 3–6 (AD-22) only, then stop and escalate — do not write Tasks 7–9's code speculatively "to be safe."
  - [ ] If 3.8 returned "go," note its measured throughput numbers and recommended NFR1 Tier-3 budget in this story's own Completion Notes once implementation runs — Dev Notes below deliberately does not restate invented numbers.

- [ ] **Task 2: Add the `EFCore.BulkExtensions` package references** (AC: #13, #14)
  - [ ] Add `EFCore.BulkExtensions.Core`, `EFCore.BulkExtensions.SqlServer`, `EFCore.BulkExtensions.PostgreSql` at `10.0.1` to `Directory.Packages.props`; reference all three (no `Version=` in the `.csproj`, per this project's central package management rule) from `src/EnergyTracker.Infrastructure/EnergyTracker.Infrastructure.csproj`.
  - [ ] Run `dotnet list package --include-transitive` (or inspect the restored lockfile) and record the actual resolved `Microsoft.Data.SqlClient` version. Confirm AD-21's `Authentication=Active Directory Default`/`Active Directory Managed Identity` connection strings still connect cleanly against it — this needs a real check against the live Azure SQL instance (or at minimum a local smoke test against SQL auth if Entra cutover hasn't shipped yet), not just "the version number still starts with 6.1." Update AD-21's `[ADOPTED]` stale-fact note in `invariants-rules.md` with the newly-resolved version once confirmed.

- [ ] **Task 3: `FindLatestReadingIntervalStartByPowerPointAsync` return-shape change** (AC: #1)
  - [ ] Add `public sealed record SmartPlugReadingWatermark(Guid Id, DateTimeOffset IntervalStart, decimal KwhValue);` alongside `ISmartPlugImportRepository` (same placement pattern as `SmartPlugParseResult` alongside `ISmartPlugParser`).
  - [ ] Change the interface method's return type to `Task<SmartPlugReadingWatermark?>`; update `SmartPlugImportRepository`'s implementation to project `Id`, `IntervalStart`, `KwhValue` instead of `IntervalStart` alone (still `OrderByDescending(r => r.IntervalStart)`, still `null` only when no reading exists for the Power Point).
  - [ ] Update every call site — currently only `ProcessSmartPlugImport.cs:54` — for the new return shape (Task 6 below).

- [ ] **Task 4: `EveHomeXlsxParser` boundary-row inclusion** (AC: #2)
  - [ ] Change the early-stop comparison at `EveHomeXlsxParser.cs`'s data-row loop from `reading.IntervalStart <= watermark` to `reading.IntervalStart < watermark`. Update the adjacent comment (currently describing "rows at or older than that watermark are never materialized") to reflect that the row *at* the watermark is now included, and re-verify (via a test, Task 10) that the loop still stops one row after emitting it — not two.

- [ ] **Task 5: `MerossCsvParser` boundary-row inclusion** (AC: #3)
  - [ ] Change the filter comparison at `MerossCsvParser.cs`'s per-row loop from `dayStart <= watermark` to `dayStart < watermark`. Confirm (via a test, Task 10) the row exactly at the watermark's day is now present in the returned readings list.

- [ ] **Task 6: `ProcessSmartPlugImport` — boundary-row comparison, narrow update, audit-correction call** (AC: #4, #5, #6, #7, #8)
  - [ ] Inject `IAuditCorrectionRecorder` into `ProcessSmartPlugImport`'s primary constructor (new parameter, alongside the four existing ones).
  - [ ] Change the local `watermark` variable's type from `DateTimeOffset?` to `SmartPlugReadingWatermark?`; pass `watermark?.IntervalStart` into `parser.Parse(...)` (AC #4 — the parser's own signature is unchanged).
  - [ ] Immediately after `parser.Parse` returns and **before** the existing `if (readings.Count == 0)` special-case block, perform the boundary-row resolution: if `watermark is not null`, find the first row in `parseResult.Readings` (in parse order) whose `IntervalStart == watermark.IntervalStart`. If found:
    - Compare its `KwhValue` to `watermark.KwhValue`. Equal → drop it from the working readings list (nothing to write). Different → call a new repository method (Task 7) to update the existing stored row's `KwhValue` by `watermark.Id`, then call `auditCorrectionRecorder.RecordAsync(householdId, "SmartPlugReading", watermark.Id, "KwhValue", <old value formatted>, <new value formatted>, cancellationToken)` — match the exact old/new-value string formatting convention the existing Meter Reading/Tariff `IAuditCorrectionRecorder` call sites already use (grep `RecordAsync(` before writing this call; don't invent a new formatting convention here).
    - Either way, drop the row from the working readings list — it must never reach the AD-23 bulk-write path (AC #5's closing clause).
    - **DST-fold discipline (AC #7):** if more than one row in `parseResult.Readings` shares that exact `IntervalStart`, drop every one of them from the working list (only the first is compared/corrected as above), and log a warning distinguishing "multiple rows at the watermark boundary, DST-fold assumed" from an ordinary single-row correction.
  - [ ] Recompute `readings.Count == 0` using the post-drop working list before entering the existing zero-rows branch — a batch that had exactly one row (the boundary row, exact match, now dropped) must fall into the existing "nothing new, Completed with zero readings" branch, not a new bespoke path.
  - [ ] This task's logic only ever runs when `matchedPowerPoint is not null` (the only condition under which `watermark` is ever resolved non-null today) — no new branch on vendor is introduced (AD-9 stays intact).

- [ ] **Task 7: `SmartPlugImportRepository.AddAsync` — bulk-write replacement** (AC: #9, #10, #11) — **gated on Task 1's go verdict**
  - [ ] Add a new method (e.g. `UpdateReadingKwhValueAsync(Guid readingId, decimal newKwhValue, CancellationToken)`) implementing Task 6's narrow update as a set-based `ExecuteUpdateAsync` (`dbContext.SmartPlugReadings.Where(r => r.Id == readingId).ExecuteUpdateAsync(s => s.SetProperty(r => r.KwhValue, newKwhValue), cancellationToken)`) — same set-based idiom `UpdateMappingAsync` already uses in this same class.
  - [ ] Rewrite `AddAsync`: open an explicit transaction (`await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken)`), add and save the `SmartPlugImport` parent row, then — only when `readings.Count > 0` — call `dbContext.BulkInsertOrUpdateAsync(readings, new BulkConfig { PropertiesToExclude = [nameof(SmartPlugReading.Id)], UpdateByProperties = <branch per AC #10> }, cancellationToken: cancellationToken)`, then commit the transaction. Rely on the transaction's own automatic rollback-on-dispose (never committed) for the "no partial row survives cancellation" guarantee — do not hand-write a `DeletePartiallyPersistedImportAsync`-style manual cleanup; that mechanism existed only because the old per-row fallback split one logical write into many round trips outside a single transaction, which no longer happens here.
  - [ ] Delete `AnyExistingReadingAtSameKeyAsync`, `AddWithPerRowConflictToleranceAsync`, and `DeletePartiallyPersistedImportAsync` outright (grep for any other reference before removing — expected: none, all three are private and only reachable from the old `AddAsync`).
  - [ ] **Verify empirically, via a real-database test (Task 10), a risk this AD's own text does not fully resolve:** does `BulkInsertOrUpdateAsync` tolerate two rows *within the same incoming batch* sharing the same match key (a DST-fold pair that is NOT the watermark boundary case Task 6 already filters — e.g. two brand-new rows in one file, not one new-vs-stored comparison)? The old per-row fallback handled this implicitly via sequential unique-constraint enforcement (Story 3.4 review-round-2's "keep-first" discipline); confirm what the bulk path actually does (silently keeps one deterministically, throws, or silently applies twice) before treating this as a solved problem, and de-duplicate the incoming batch before the bulk call if the library does not handle it safely on its own.
  - [ ] Add an explicit code comment on `UpdateMappingAsync` (AC #12) stating the carve-out reasoning verbatim from AD-23's own text, so a future reader doesn't mistake the omission for an oversight.

- [ ] **Task 8: Verify the second match-key branch's partial-index targeting** (AC: #10)
  - [ ] Confirm (ideally by reusing Story 3.8's own spike findings on this exact question, Task 7's AC #7) that `UpdateByProperties = [HouseholdId, IntervalStart]` only ever matches rows where `PowerPointId IS NULL` given this branch is only ever invoked for batches where every row already has `PowerPointId == null` by construction (the `AwaitingPowerPointMapping` path) — the same batch-homogeneity assumption `AnyExistingReadingAtSameKeyAsync` relied on before removal. If the library's behavior differs from what 3.8 measured (e.g. a schema/library-version drift between when 3.8 ran and now), re-verify directly rather than trusting a stale spike result.

- [ ] **Task 9: Cross-reference the resolved AD-21 SqlClient version** (AC: #14)
  - [ ] Update `invariants-rules.md`'s AD-21 `[ADOPTED]` bullet's stale-fact note with the actually-resolved `Microsoft.Data.SqlClient` version once Task 2's verification completes.

- [ ] **Task 10: Tests** (AC: all)
  - [ ] `tests/EnergyTracker.Infrastructure.Tests/EveHomeXlsxParserTests.cs`: extend the existing watermark test to assert the boundary row (`IntervalStart == watermark`) is now present in the result, and the loop still stops immediately after it (one row later than before, not two).
  - [ ] `tests/EnergyTracker.Infrastructure.Tests/MerossCsvParserTests.cs`: extend similarly — assert the row exactly at the watermark day is now included.
  - [ ] `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs`: update/add tests for `FindLatestReadingIntervalStartByPowerPointAsync`'s new `SmartPlugReadingWatermark` return type (asserts `Id`/`IntervalStart`/`KwhValue` all correct); new tests for the rewritten `AddAsync` (Testcontainers, both providers) — a plain insert, an upsert via each of the two `UpdateByProperties` configurations, the parent+child single-transaction shape, and a cancellation-mid-bulk-write test confirming zero partial rows survive (mirroring Story 3.8's own Task 5, now against the real production schema and code path); a within-batch duplicate-match-key test resolving Task 7's flagged risk one way or the other.
  - [ ] `tests/EnergyTracker.Application.Tests/ProcessSmartPlugImportTests.cs`: new tests for the boundary-row comparison — exact re-report (dropped, `IAuditCorrectionRecorder.Received(0)`, repository update method never called), divergent value (repository update called with the correct `Id`/new value, `IAuditCorrectionRecorder.RecordAsync` called with the correct `entityType`/`entityId`/`fieldName`/old/new values), and a DST-fold multi-row-at-boundary case (only the first row compared, all sharing rows dropped, distinct log). Add a regression test confirming the correction path never touches `RoomName`/`PowerPointName`/`DeviceName` (AC #6).
  - [ ] Confirm (do not just assume) that `UpdateMappingAsync`'s own existing tests still pass unmodified — a green, unchanged test file here is itself the evidence for AC #12's carve-out.
  - [ ] `.NET`: xUnit v3 MTP, Shouldly, NSubstitute against ports, `TestContext.Current.CancellationToken`, Testcontainers (both providers) for anything DB-touching — same conventions as every prior Epic 3 story.

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

### Debug Log References

### Completion Notes List

### File List
