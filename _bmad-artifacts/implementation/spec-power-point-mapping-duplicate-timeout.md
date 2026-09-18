---
title: 'Bulk-Resolve Power Point Mapping Conflicts (Fix Mapping Request Timeout)'
type: 'bugfix'
created: '2026-09-18'
status: 'done'
baseline_commit: 'b52170686bfd35c23475a4fa26d5cdb7cc0419ed'
review_loop_iteration: 0
context: ['{project-root}/_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md']
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `POST /api/smart-plug-imports/{id}/power-point-mapping` resolves unique-index conflicts (`IX_SmartPlugReadings_PowerPointId_IntervalStart`) one reading at a time (`SaveChangesAsync` per row, plus an extra query+delete per collision). For a full-history Eve Home re-export ("2026-09-18_Tür_Gesamtverbrauch.xlsx") that collided on nearly every row against a Power Point's existing year+ of data, this ran for 4 minutes before Azure Container Apps' ~240s ingress idle timeout killed the connection (`HTTP 499`), surfacing to the user as a failed mapping — even though the conflict-resolution logic itself (Story 3.7: delete exact duplicates, skip genuinely divergent data) worked correctly.

**Approach:** Replace the per-row loop with a handful of set-based DB operations — bulk-classify conflicts by comparing projected columns, then one `ExecuteUpdateAsync` for non-conflicting rows and one `ExecuteDeleteAsync` for exact duplicates — so resolution time no longer scales with collision count.

## Boundaries & Constraints

**Always:**
- Preserve today's exact classification rules: colliding reading with identical `DeviceName`/`KwhValue`/`IntervalEnd` → delete it; any divergence in those fields → leave it unmapped (`PowerPointId` stays null) and log a warning citing reading id, import id, Power Point id, and `IntervalStart`; no collision → attach via set-based update.
- Never load full `SmartPlugReading` entities into the change tracker for this path — projected selects plus `ExecuteUpdateAsync`/`ExecuteDeleteAsync` only.
- Keep `UpdateMappingAsync`'s existing 180s `SetCommandTimeout`, the `AnyMappingConflictAsync` pre-check, and the transaction wrapping in `MapSmartPlugImportToPowerPoint.ExecuteAsync` untouched.
- Portable relational subset only (AD-2) — no provider-specific SQL.

**Ask First:** If the three-way classification can't be expressed via projected in-memory sets without risking excessive memory use for pathological import sizes, stop and ask before adding a new dependency (e.g. a temp table).

**Never:**
- Do not adopt `BulkInsertOrUpdateAsync`/EFCore.BulkExtensions — Story 3.9/AD-23 deliberately carved `UpdateMappingAsync` out of that as a different operation shape; don't revisit that decision here.
- Do not convert the endpoint to the async job/polling pattern (AD-6) — considered and explicitly deferred for this fix.
- Do not change the duplicate/divergence classification rules themselves, only how they're applied.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| No conflicts | import's `IntervalStart`s don't collide with the target Power Point's existing readings | Unchanged: `AnyMappingConflictAsync` fast path, single `ExecuteUpdateAsync` | N/A |
| All exact duplicates | every colliding row's `DeviceName`/`KwhValue`/`IntervalEnd` matches the existing mapped row | One `ExecuteDeleteAsync` removes all colliding rows; import still reaches `Completed` | N/A |
| All divergent (this incident's shape) | thousands of colliding rows differ from the existing mapped row in at least one field | All left unmapped, each logged; request completes in low seconds | N/A |
| Mixed batch | some rows no-conflict, some exact-duplicate, some divergent | Each subset resolved by its own bulk op; final per-row outcome matches today's per-row logic | N/A |
| Concurrent delete/update race | a targeted row is already gone by the time the bulk op runs | 0-rows-affected is not an error, same tolerance as today's `deletedCount > 0` check | N/A |

</frozen-after-approval>

## Code Map

- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` -- `UpdateMappingPerRowWithConflictToleranceAsync` (~L495-593) is the per-row loop to replace.
- `src/EnergyTracker.Application/Ports/ISmartPlugImportRepository.cs` -- doc comment above `UpdateMappingAsync` describes the retired per-row fallback; needs updating.
- `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs` -- existing `UpdateMappingAsync_*` tests (~L257-470) assert only final DB state, must keep passing unmodified.

## Tasks & Acceptance

**Execution:**
- [x] `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` -- Rewrite the per-row fallback into a set-based method: project the import's readings' `Id`/`IntervalStart`/`DeviceName`/`KwhValue`/`IntervalEnd` (no tracking); bulk-fetch existing readings at the target `PowerPointId` whose `IntervalStart` is in that set; partition in memory into no-conflict / exact-duplicate / divergent; `ExecuteUpdateAsync` the no-conflict ids, `ExecuteDeleteAsync` the exact-duplicate ids, log each divergent id -- removes the O(n) `SaveChangesAsync`-per-row round trips that caused the timeout.
- [x] `src/EnergyTracker.Application/Ports/ISmartPlugImportRepository.cs` -- Update the doc comment above `UpdateMappingAsync` to describe the set-based fallback.
- [x] `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs` -- Add a test seeding a large (500+) mixed batch of no-conflict/exact-duplicate/divergent readings, asserting the same per-category outcome as the existing single-row tests.
- [x] `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md` -- Append a dated amendment to AD-23's carve-out note: the "small/bounded" volume assumption behind leaving `UpdateMappingAsync` untouched was invalidated by a full-history re-import colliding on ~100% of rows; the method is now internally set-based too, but still not `BulkInsertOrUpdateAsync` (carve-out reasoning unchanged).

**Acceptance Criteria:**
- Given an import whose readings collide with existing mapped readings at every `IntervalStart`, when mapped, then the request completes in low single-digit seconds and returns 200 with the import `Completed`.
- Given a mixed batch of no-conflict, exact-duplicate, and divergent-conflict readings, when mapped, then each reading ends in the same state (attached / deleted / left unmapped) as under the old per-row logic.
- Given zero conflicts, when mapped, then the existing fast-path behavior is unchanged.
- Given a divergent-conflict reading, when resolved, then a warning is still logged identifying the reading, import, Power Point, and `IntervalStart`.

## Spec Change Log

## Design Notes

The in-memory partition step is safe even at large N: it's a dictionary keyed by `IntervalStart` built from one bulk-fetched result set, not a per-row query. Both bulk writes then filter by a projected `Id` list (EF Core 10 translates `List<Guid>.Contains` as a single array-typed parameter on both providers — SQL Server via OPENJSON, Npgsql via `= ANY(@array)` — not one parameter per id, so this doesn't hit SQL Server's 2100-parameter ceiling even for very large imports).

The "existing reading" lookup must exclude the current import's own rows (`r.SmartPlugImportId != smartPlugImportId`) — otherwise a second mapping call for the same import (a double-click, or a retry racing an earlier commit) would see its own just-mapped readings as "existing" and delete them as self-matched duplicates (review finding, fixed before merge).

## Verification

**Commands:**
- `dotnet test tests/EnergyTracker.Infrastructure.Tests --filter FullyQualifiedName~SmartPlugImportRepositoryTests` -- expected: all existing + new `UpdateMappingAsync` tests pass.
- `dotnet build EnergyTracker.sln` -- expected: clean build.

**Manual checks (if no CLI):**
- After deploy, re-run the mapping for an equivalent large, fully-colliding import against the Azure environment and confirm the POST returns 200 within a few seconds instead of timing out at 240s.

## Suggested Review Order

**The set-based rewrite**

- Entry point: both callers of the retired per-row loop now call the set-based fallback instead.
  [`SmartPlugImportRepository.cs:446`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L446)

- Core rewrite: bulk-classify every colliding reading in one pass instead of one round trip per row.
  [`SmartPlugImportRepository.cs:513`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L513)

- Critical fix from this story's own review loop: exclude the current import's own rows from the "existing" lookup, or a retry/double-click self-deletes an already-mapped import.
  [`SmartPlugImportRepository.cs:553`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L553)

- Bulk attach the non-conflicting subset in one `ExecuteUpdateAsync`, same idiom as the existing fast path above it.
  [`SmartPlugImportRepository.cs:596`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L596)

- Bulk-delete exact duplicates and log honestly from the actual affected-row count, not an assumed one.
  [`SmartPlugImportRepository.cs:616`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L616)

**Architecture record**

- Amendment documenting why the AD-23 carve-out's volume assumption (not its conclusion) was wrong, and the self-collision fix found during review.
  [`invariants-rules.md:179`](../../_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#L179)

**Tests**

- Regression test proving the fix: a second mapping call for the same import must not delete its own already-mapped readings.
  [`SmartPlugImportRepositoryTests.cs:556`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs#L556)

- Scale/classification test: 550 mixed no-conflict/exact-duplicate/divergent readings resolved correctly in one call.
  [`SmartPlugImportRepositoryTests.cs:467`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs#L467)

- Closes Acceptance Criterion #4: asserts the divergent-conflict warning still carries reading/import/PowerPoint/IntervalStart context.
  [`SmartPlugImportRepositoryTests.cs:604`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs#L604)
