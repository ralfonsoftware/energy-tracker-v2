---
baseline_commit: 09875c4327151a12d12bb6b992e07c3d4787def2
---

# Story 3.7: Smart-Plug Reading Duplicate Cleanup on Late Power-Point Mapping

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member whose Smart Plug import once sat AwaitingPowerPointMapping before being resolved,
I want the readings from that resolution to never leave a duplicate, permanently-unmapped copy behind,
so that my Smart Plug data stays accurate and doesn't waste storage on dead rows nobody will ever read.

## Context (why this is needed)

Confirmed live in production on 2026-08-26 (Azure SQL audit against `energytracker-prod-qvc6vfmtp5-sql`, run by Amelia at Ralf's request): `MapSmartPlugImportToPowerPoint`'s per-row conflict-tolerant fallback (`SmartPlugImportRepository.UpdateMappingPerRowWithConflictToleranceAsync`, added by Story 3.4's Dev Notes Open Question #4, "fix it now") intentionally *skips* a reading — leaving its `PowerPointId` `NULL` forever — when mapping it would collide with the `(PowerPointId, IntervalStart)` unique index (AD-20) against an already-mapped reading. `MapSmartPlugImportToPowerPoint.ExecuteAsync` still unconditionally marks the import `Completed` regardless of how many readings the fallback skipped, so nothing (status, UI, data) signals that orphaned rows were left behind — only a `LogWarning` line does, and nothing reads it.

AD-20's own Dev Notes already named this exact gap: "the DB constraint is what protects paths the [watermark] optimization can't reach (e.g. `AwaitingPowerPointMapping` readings persisted before a Power Point, and thus a watermark, is known)" — but the constraint itself only guards *insert-time* collisions, not this *update-time* (mapping-resolution) one, and that specific sub-case was left unaddressed pending real evidence.

That evidence now exists. Live query results (household `cef40b7f-3280-4cfe-a73e-b8c349adad06`, `energytracker` DB):
- 467,787 total `SmartPlugReading` rows; 288,032 with `PowerPointId IS NULL`.
- 122,154 of those unmapped rows (DeviceName "Netzwerk") have an exact mapped twin: same `HouseholdId`/`DeviceName`/`IntervalStart`/`IntervalEnd`/`KwhValue`, differing only in `PowerPointId`/`RoomName`/`PowerPointName`/`SmartPlugImportId`.
- 57,170 more (DeviceName "Steckdose Tür") have the same pattern.
- The remaining 108,703 unmapped rows (DeviceName "Steckdose HiFi") have **no** mapped twin — a Power Point named "HiFi" exists but nothing has resolved this import yet; these are genuinely still awaiting mapping, not duplicates, and must not be touched.
- Both existing unique indexes (`IX_SmartPlugReadings_PowerPointId_IntervalStart`, and the partial `(HouseholdId, IntervalStart) WHERE PowerPointId IS NULL` index) report zero violations — confirming neither one, by design, guards this specific mapped/unmapped boundary.

This is PRD NFR10's "no-silent-duplication" guarantee failing at exactly the boundary AD-20 flagged as unprotected — this story closes it, both going forward (code fix) and retroactively (cleanup migration).

## Acceptance Criteria

1. **Given** `UpdateMappingPerRowWithConflictToleranceAsync` skips a reading because it collides with an already-mapped reading at the same `(PowerPointId, IntervalStart)`, **when** the skipped reading's `KwhValue` and `IntervalEnd` exactly match the already-mapped reading's, **then** the skipped reading is deleted instead of left behind with `PowerPointId` still `NULL` — no orphaned duplicate survives a mapping operation (closes AD-20's named gap).
2. **Given** the same collision, **when** the skipped reading's `KwhValue` or `IntervalEnd` does NOT match the already-mapped reading's (an unexpected divergence, e.g. a DST fall-back duplicate local timestamp with genuinely different data), **then** today's tolerant behavior is unchanged — the reading is left unmapped, the existing `LogWarning` fires — since this case must never silently discard data that might actually differ.
3. **Given** the live production data this story was written from, **when** the one-time cleanup migration runs, **then** every unmapped `SmartPlugReading` row that has an exact mapped twin (same `HouseholdId`, `DeviceName`, `IntervalStart`, `IntervalEnd`, `KwhValue`, differing only in `PowerPointId`/`RoomName`/`PowerPointName`/`SmartPlugImportId`) is deleted, and rows with no such twin (e.g. a device tag still genuinely `AwaitingPowerPointMapping`) are left untouched.
4. **Given** the cleanup migration, **when** authored, **then** it is added via `scripts/add-migration.sh` to both `Infrastructure.Migrations.Postgres` and `Infrastructure.Migrations.SqlServer` projects (AD-2) and verified against both engines via Testcontainers (matching Story 3.4's own migration-testing discipline) — same delete-only shape as `20260822165109_AddSmartPlugReadingUniqueIndex`'s cleanup step, except this migration adds no new index, it is cleanup only.

## Tasks / Subtasks

- [x] **Task 1: Close the root cause — delete-on-exact-match instead of skip-and-orphan** (AC: #1, #2)
  - [x] In `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs`'s `UpdateMappingPerRowWithConflictToleranceAsync` (~line 276), the existing `catch (DbUpdateException)` block already detaches `reading` and confirms the conflict via `conflictConfirmed`. Once confirmed, **before** logging and moving on: look up the existing mapped reading at `(powerPointId, reading.IntervalStart)` (the row `conflictConfirmed`'s query already found — reuse/extend that query to also pull its `KwhValue`/`IntervalEnd` rather than issuing a second one).
  - [x] If `KwhValue` and `IntervalEnd` match exactly: delete the skipped `reading` — it's already detached from the change tracker (a prior `SaveChangesAsync` attempt failed on it), so use a set-based `dbContext.SmartPlugReadings.Where(r => r.Id == reading.Id).ExecuteDeleteAsync(cancellationToken)` (mirrors the existing `ExecuteUpdateAsync` set-based style in the same class) rather than re-attaching it to the tracker. Keep the `LogWarning` (reword to say "deleted duplicate", not "skipped mapping") — this is now a resolution, not a silent gap.
  - [x] If either field differs: keep today's exact behavior — reading stays unmapped, untouched, existing log message unchanged (AC #2). Do not delete or otherwise mutate it.
  - [x] `MapSmartPlugImportToPowerPoint.ExecuteAsync` needs no change — it already reads back readings via `ListReadingsByImportIdAsync` *after* `UpdateMappingAsync` completes, and a deleted row simply won't be in that read-back (matches `CompleteSmartPlugImportProcessing`'s existing empty/partial-set handling, no new code path).

- [x] **Task 2: One-time cleanup migration for existing orphaned duplicates** (AC: #3, #4)
  - [x] Run `scripts/add-migration.sh <Name>` (e.g. `CleanupOrphanedUnmappedSmartPlugReadingDuplicates`) against both provider projects — this is a pure-DML migration (no `CreateIndex`/`AddColumn`), so the scaffolded `Up()`/`Down()` bodies will be empty; hand-write both as raw `migrationBuilder.Sql(...)` calls, same idiom as `20260822165109_AddSmartPlugReadingUniqueIndex.cs`'s cleanup step.
  - [x] Postgres `Up()` — delete every unmapped row that has an exact mapped twin:
    ```sql
    DELETE FROM "SmartPlugReadings" AS u
    USING "SmartPlugReadings" AS m
    WHERE u."PowerPointId" IS NULL
      AND m."PowerPointId" IS NOT NULL
      AND m."HouseholdId" = u."HouseholdId"
      AND m."DeviceName" = u."DeviceName"
      AND m."IntervalStart" = u."IntervalStart"
      AND m."IntervalEnd" = u."IntervalEnd"
      AND m."KwhValue" = u."KwhValue";
    ```
  - [x] SQL Server `Up()` — equivalent T-SQL:
    ```sql
    DELETE u
    FROM [SmartPlugReadings] u
    JOIN [SmartPlugReadings] m
      ON m.[HouseholdId] = u.[HouseholdId]
      AND m.[DeviceName] = u.[DeviceName]
      AND m.[IntervalStart] = u.[IntervalStart]
      AND m.[IntervalEnd] = u.[IntervalEnd]
      AND m.[KwhValue] = u.[KwhValue]
    WHERE u.[PowerPointId] IS NULL
      AND m.[PowerPointId] IS NOT NULL;
    ```
  - [x] This join shape was empirically run against the real production table during this story's own investigation (a hash join over all 467,787 rows on the live Basic-tier/5-DTU SQL Server instance) and returned correctly (122,154 + 57,170 matches) — it is not a theoretical query, it's already validated against the exact data shape it will run against in production.
  - [x] `Down()`: mirror `20260822165109`'s precedent — a comment stating the delete is irreversible, no attempt to restore rows (same as that migration's own `Down()` doc comment).
  - [x] No `SmartPlugReadingConfiguration.cs`/model-snapshot change needed — this migration touches no schema, only data, unlike Story 3.4's migration which added an index in the same file. Confirmed: `dotnet ef migrations add` produced no snapshot diff.
  - [x] Testcontainers coverage on both providers (Task 3) is required before this is safe to ship — same reasoning as Story 3.4's Task 4: raw-SQL isn't guaranteed byte-identical across engines the way portable `migrationBuilder` calls are.

- [x] **Task 3: Tests** (AC: all)
  - [x] `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs`: update the existing `UpdateMappingAsync_persists_the_import_status_and_skips_only_the_colliding_reading_on_a_unique_constraint_conflict` test (line ~213) — it currently asserts the colliding reading survives with `PowerPointId.ShouldBeNull()`; that assertion is exactly what this story changes. Its seeded data already gives the colliding pair identical `KwhValue`/`IntervalEnd` (via the shared `MakeReading` helper's fixed `KwhValue = 0.5m`), so this is the AC #1 (delete) case — update the assertion to confirm the colliding reading's row no longer exists (`persistedReadings` should only contain the non-colliding one), and rename the test to reflect the new behavior (e.g. `..._deletes_the_colliding_reading_on_an_exact_duplicate_conflict`).
  - [x] Add a new test for AC #2: seed a colliding pair via `MakeReading` but with a different `KwhValue` (or `IntervalEnd`) on the awaiting-mapping side than the already-mapped side — assert the colliding reading still exists afterward, still `PowerPointId == null`, unchanged from today's behavior.
  - [x] New migration-cleanup Testcontainers tests, one per provider, mirroring `PostgresMigrationTests.cs`/`SqlServerMigrationTests.cs`'s existing pattern (`IAsyncLifetime`, real container, migrate up to the migration *before* this one, seed data, then apply this migration): seed (a) an unmapped/mapped exact-duplicate pair → asserts the unmapped one is deleted after migrating; (b) an unmapped row with no mapped twin → asserts it survives untouched. Cover both providers per AD-2's dual-provider discipline.
  - [x] `.NET`: xUnit v3 MTP, Shouldly, NSubstitute against ports, `TestContext.Current.CancellationToken` — same conventions as every existing Smart Plug import test in this codebase (see project-context.md Testing Rules).

### Review Findings

- [x] [Review][Patch] Exact-duplicate delete check omits `DeviceName`, so two different devices' readings could be wrongly treated as duplicates if `KwhValue`/`IntervalEnd` coincidentally match [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:303-320]
- [x] [Review][Patch] `ExecuteDeleteAsync`'s affected-row count is discarded — the "deleted duplicate" log fires even when zero rows were actually deleted [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:322-330]
- [x] [Review][Patch] `SingleOrDefaultAsync` throws `InvalidOperationException` if more than one row ever matches `(PowerPointId, IntervalStart)` — a stricter, less tolerant regression from the prior `AnyAsync` check, working against AD-20's own stated "don't over-trust the DB constraint alone" rationale [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:303-306]
- [x] [Review][Patch] AC #2's divergence test only exercises a `KwhValue` mismatch — the `IntervalEnd`-divergence branch of the `&&` condition is unverified [tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs]
- [x] [Review][Patch] Migration's `DeviceName` equality is collation-dependent — SQL Server's typically case-insensitive default collation vs. Postgres's byte-exact default could delete different rows per engine for case-varying data (AD-2 dual-provider parity) [src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260826192744_CleanupOrphanedUnmappedSmartPlugReadingDuplicates.cs]
- [x] [Review][Defer] TOCTOU window between the conflict-confirmation read and the delete — nothing pins the colliding row between the read and the write [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:303-330] — deferred, pre-existing: this check-then-act pattern already existed in this method (and in `AnyMappingConflictAsync`'s pre-check for the `ExecuteUpdateAsync` fast path) before this story; this story changes only what happens after the conflict is confirmed, not the underlying race.

## Dev Notes

- **Root cause, not symptom:** Task 1 is the actual fix; Task 2 is a one-time cleanup of data the bug already produced before this story shipped. Shipping only Task 2 without Task 1 would leave the bug live and require re-running cleanup indefinitely.
- **AD-11 (audit trail) does not apply:** like Story 3.4's own dedup cleanup, this is a data-integrity fix, not a user-initiated edit — it deliberately does not route through `AuditCorrection`/`IAuditCorrectionRecorder`.
- **AD-7 (immutable history) still applies:** this does not retroactively recompute any `SmartPlugImportGap`/`StatusSnapshot` rows already computed before this story ships — deleting a duplicate `SmartPlugReading` row does not touch derived history, only correctness going forward. Same discipline as Story 3.4's cleanup.
- **AD-10 (by-value snapshot) is unaffected:** the deleted row is a stale duplicate of the mapped survivor, not a live-FK re-derivation — nothing here reintroduces a live join.
- **AD-2 (dual-provider):** the migration's raw SQL is written once per provider dialect inside each provider's own migration file — this does not violate AD-2, which only forbids provider-specific code in the *shared* `SmartPlugReadingConfiguration.cs`/`EnergyTrackerDbContext`, not in per-provider migration files (Story 3.4's own migration already established this exact precedent).
- **Explicitly out of scope:** a second, unconfirmed finding from the same audit — two Power Points both named "Tür" in different Rooms in the audited household. Do not fold into this story.
- **Open question for dev-story activation:** `MapSmartPlugImportToPowerPoint.ExecuteAsync` marks an import `Completed` even when the AC #2 case still leaves genuinely-divergent rows unmapped. This story treats that as a non-goal (a new import-status value affecting the frontend/Story 3.6's job-status list is out of proportion to this story's backend data-hygiene scope) — confirm with Ralf if that should change before or during implementation.
- **Basic-tier (5 DTU) Azure SQL performance:** the live audit found simple point queries fast but GROUP BY/self-join queries over the full 467k-row table took several minutes. The cleanup migration's join (Task 2) is the same shape and was verified to complete (not to time out) against production-scale data during this story's investigation, but is still a full-table join — consider whether `UpdateMappingAsync`'s existing `SetCommandTimeout(TimeSpan.FromSeconds(180))` precedent (same file, same class) needs to be applied to whatever DbContext runs this migration, or whether EF migrations already use a longer default command timeout than a request-scoped DbContext. Confirm empirically via the Testcontainers test rather than assuming either way.

### Project Structure Notes

- Backend files to modify: `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` (Task 1 only — `MapSmartPlugImportToPowerPoint.cs` and `AnyMappingConflictAsync` need no change).
- New files (migration pair, via `scripts/add-migration.sh`): `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/<timestamp>_CleanupOrphanedUnmappedSmartPlugReadingDuplicates.cs` (+ `.Designer.cs`), `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/<timestamp>_CleanupOrphanedUnmappedSmartPlugReadingDuplicates.cs` (+ `.Designer.cs`). No `EnergyTrackerDbContextModelSnapshot.cs` change expected (no schema change) — confirm the scaffolder doesn't emit a no-op snapshot diff; if it does, keep it (harmless) rather than hand-editing it out.
- Tests to modify: `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs`.
- Tests to add to: `tests/EnergyTracker.Infrastructure.Tests/PostgresMigrationTests.cs`, `tests/EnergyTracker.Infrastructure.Tests/SqlServerMigrationTests.cs`.
- No frontend changes — confirmed no UX impact, same as Story 3.4.
- Fits the existing flat, one-class-per-file convention; no new non-migration files/folders.

### References

- [Source: `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:201-311`] — `UpdateMappingAsync`/`AnyMappingConflictAsync`/`UpdateMappingPerRowWithConflictToleranceAsync`, the exact code this story modifies.
- [Source: `src/EnergyTracker.Application/MapSmartPlugImportToPowerPoint.cs`] — caller; confirmed unconditionally sets `Completed` and reads back via `ListReadingsByImportIdAsync` after `UpdateMappingAsync`.
- [Source: `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs:83-95,212-258`] — `MakeReading` helper and the existing test this story's Task 3 updates.
- [Source: `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260822165109_AddSmartPlugReadingUniqueIndex.cs`, `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260822165112_AddSmartPlugReadingUniqueIndex.cs`] — precedent for a hand-added raw-SQL cleanup step inside a scaffolded migration, and for a `Down()` that documents irreversibility rather than attempting restoration.
- [Source: `tests/EnergyTracker.Infrastructure.Tests/PostgresMigrationTests.cs`, `SqlServerMigrationTests.cs`] — exact Testcontainers migration-test pattern this story's Task 3 extends.
- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md:127-130`] — AD-20 full text, including the Dev Notes line this story directly closes.
- [Source: `_bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/cross-cutting-nfrs.md:12`] — NFR10's exact "no-silent-duplication" wording.
- [Source: `_bmad-artifacts/implementation/3-4-incremental-smart-plug-import.md`] — previous story in this epic; source of the "fix it now" decision that created the code this story now closes the gap in (Open Question #4, Completion Notes).
- [Source: `_bmad-artifacts/project-context.md`] — project-wide coding/testing conventions (AD-2 dual-provider migrations via `scripts/add-migration.sh`, Testcontainers discipline, xUnit v3 MTP/Shouldly/NSubstitute, AD-7/AD-10/AD-11/AD-20).
- [Source: live Azure SQL audit, 2026-08-26, `energytracker-prod-qvc6vfmtp5-sql`/`energytracker` DB, household `cef40b7f-3280-4cfe-a73e-b8c349adad06`] — the production counts cited in Context/AC #3 above (122,154 + 57,170 confirmed exact duplicates; 108,703 confirmed non-duplicate genuinely-unmapped rows; zero violations of either existing unique index).

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

None — no debugger/log-file artifacts beyond standard `dotnet test`/`dotnet build` output captured during implementation.

### Completion Notes List

**Open Design Question — confirmed with Ralf during dev-story activation:**
- `MapSmartPlugImportToPowerPoint.ExecuteAsync` marking an import `Completed` even when AC #2's genuinely-divergent case still leaves a reading unmapped: kept as a non-goal (Ralf confirmed) — no new import-status value added, the reworded `LogWarning` remains the only signal for that specific edge case.

**Implementation notes:**
- Root-cause fix (Task 1) touches only `UpdateMappingPerRowWithConflictToleranceAsync`'s existing `catch (DbUpdateException)` branch in `SmartPlugImportRepository.cs` — extended the existing conflict-confirmation query to also read the colliding mapped reading's `KwhValue`/`IntervalEnd` (no second round-trip), then branches: exact match → `ExecuteDeleteAsync` on the skipped reading (set-based, mirrors the class's existing `ExecuteUpdateAsync` idiom since the reading is already detached from the change tracker); divergent → unchanged skip-and-log behavior (AC #2). `MapSmartPlugImportToPowerPoint.ExecuteAsync` needed no change, confirmed by the existing test passing unmodified logic there.
- TDD discipline followed literally: updated the pre-existing `UpdateMappingAsync_persists_the_import_status_and_skips_only_the_colliding_reading_on_a_unique_constraint_conflict` test's assertion to the new expected (delete) behavior first, confirmed it failed against the unmodified code (RED — `persistedReadings should have single item but had 2`), then implemented the fix (GREEN, confirmed via `dotnet test ... --filter-query`).
- Cleanup migration (Task 2) scaffolded via `scripts/add-migration.sh CleanupOrphanedUnmappedSmartPlugReadingDuplicates` — as expected for a pure-DML change, both providers' scaffolded `Up()`/`Down()` were empty and no `EnergyTrackerDbContextModelSnapshot.cs` diff was produced; hand-wrote the delete-join SQL (dialect-specific per provider, same AD-2-compliant precedent as `20260822165109_AddSmartPlugReadingUniqueIndex`).
- Dev Notes' open question about `UpdateMappingAsync`'s existing `SetCommandTimeout(180s)` precedent applying to the migration's DbContext: not applicable — EF Core migrations run via `IMigrator`/`Database.MigrateAsync`, a separate code path from the request-scoped `SmartPlugImportRepository` instance that sets that timeout; the migration test suite's timing (~5-13s locally against a full 4-row seed) gave no indication of a timeout risk at the tested scale. Flagging for whoever runs this against the actual 179k-row production table: if the real migration run is slow on the Basic-tier (5 DTU) instance, increase the deploy/migration-runner's own command timeout rather than modifying this migration file.
- Verification: full backend suite green — 375 tests total (Api.Tests, Infrastructure.Tests via Testcontainers Postgres+SqlServer, Application.Tests, Architecture.Tests), `dotnet build EnergyTracker.sln --configuration Release` clean (0 errors, pre-existing `NU1903` SSH.NET advisory only, unrelated to this story). No frontend files touched (confirmed via `git status` — matches the story's "no UX impact" scope).

**Code review (Blind Hunter + Edge Case Hunter + Acceptance Auditor, 2026-08-26):** Acceptance Auditor found zero spec violations. 5 patch findings applied (see Review Findings above): added `DeviceName` to the exact-duplicate match criteria (a real correctness gap — a Power Point can receive manually-mapped readings from more than one distinct device over time, so `KwhValue`/`IntervalEnd` alone could coincidentally match across genuinely different devices); stopped logging "deleted" when `ExecuteDeleteAsync` affected 0 rows; swapped `SingleOrDefaultAsync` for `FirstOrDefaultAsync` so a genuine multi-row conflict falls through to the existing rethrow path instead of throwing `InvalidOperationException`; added the missing AC #2 `IntervalEnd`-divergence test plus a new `DeviceName`-divergence regression test proving the first fix; fixed the SQL Server migration's `DeviceName` equality to a `varbinary` byte-exact cast so it can't diverge from Postgres's default byte-exact `=` on case-varying data. 1 finding deferred (TOCTOU window in the conflict-confirmation read, pre-existing pattern in this method, not introduced by this story — logged in `deferred-work.md`). Full suite re-verified green after patches: 377/377 (2 new tests).

### File List

**Backend — modified:**
- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs`

**Backend — new (migration pair, via `scripts/add-migration.sh`):**
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260826192741_CleanupOrphanedUnmappedSmartPlugReadingDuplicates.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260826192741_CleanupOrphanedUnmappedSmartPlugReadingDuplicates.Designer.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260826192744_CleanupOrphanedUnmappedSmartPlugReadingDuplicates.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260826192744_CleanupOrphanedUnmappedSmartPlugReadingDuplicates.Designer.cs`

**Tests — modified:**
- `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs`
- `tests/EnergyTracker.Infrastructure.Tests/PostgresMigrationTests.cs`
- `tests/EnergyTracker.Infrastructure.Tests/SqlServerMigrationTests.cs`

No frontend files touched.

## Change Log

- 2026-08-26: Story implemented (Amelia, dev-story). Root-cause fix + one-time cleanup migration + tests, all ACs satisfied, full backend suite green (375/375). Status → review.
- 2026-08-26: Code review (Blind Hunter + Edge Case Hunter + Acceptance Auditor). 0 decision-needed, 5 patch (all applied), 1 defer, 8 dismissed as noise. Full suite green (377/377). Status → done.
