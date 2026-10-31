# 0006: Mapping an import to a Power Point

Source: AD-23 (carve-out and 2026-09-18 amendment), AD-20, AD-3, Stories 3.4 and 3.7, production incident of 2026-09-18.
Code: `SmartPlugImportRepository.Mapping.cs`.

## Context

`UpdateMappingAsync` re-tags the readings of one already-persisted import with a Power Point and a Room. AD-23 first kept it off `BulkInsertOrUpdateAsync` because it is a metadata re-tag over validated rows, not an upsert of a fresh batch. The "small, bounded volume" half of that argument did not hold: a full-history re-export that collided on about 100% of an existing Power Point's rows (179,324 rows) needed about four minutes with the per-row fallback (one `SaveChangesAsync` per reading, plus a SELECT and a conditional delete per collision). That exceeded the ingress timeout of Azure Container Apps (about 240 s, HTTP 499), although the classification itself (Story 3.7) was correct.

Further facts:

- The default 30 s command timeout is too short for a set-based UPDATE over a whole import on Basic-tier Azure SQL (5 DTU). The readback in `MapSmartPlugImportToPowerPoint.ExecuteAsync` reads the same row count right after.
- `ExecuteUpdateAsync` bypasses the change tracker, so a constraint violation is not wrapped in `DbUpdateException`. The portable base type `DbException` has to be caught (AD-2).
- EF Core 10 translates `List<T>.Contains` into one JSON array parameter, so the bulk lookups do not hit the 2,100 parameter ceiling.
- A second mapping request for the same import (a double click while the first is slow) sees the first request's committed rows. A reading matches itself on `DeviceName`, `KwhValue` and `IntervalEnd`, so without a guard it would be classified as a duplicate of itself and deleted.

## Decision

- The method stays off `EFCore.BulkExtensions`. Its fallback is set-based on its own.
- Classification runs once, in memory, from a single `AsNoTracking` bulk fetch of the already-mapped readings at the target Power Point. The fetch excludes the import's own readings (`SmartPlugImportId != smartPlugImportId`).
- A reading is an exact duplicate only when `DeviceName`, `KwhValue` and `IntervalEnd` all match. `DeviceName` is part of it because a Power Point can receive readings from several devices over time (Story 3.7, AC #1).
- Exact duplicates are deleted in one statement. Divergent data at the same key (a DST fall-back, AC #2) stays unmapped and is logged from the in-memory result.
- Only the non-colliding rows are updated, in one `ExecuteUpdateAsync` over their ids.
- The fast path (one UPDATE) is tried first. A pre-check that sees a collision skips it and goes straight to the classification.
- The warning logs what was classified and attempted, not a per-row "Deleted" claim that one batched DELETE cannot prove. A row that is already gone (the 30 day sweep, a concurrent request) is not an error.
- The command timeout is 180 s for the rest of the request.

## Consequences

- The cost no longer grows with the number of collisions: two statements and one fetch.
- The tenant filter (AD-3) still applies to the projected queries and to `ExecuteUpdateAsync` and `ExecuteDeleteAsync`.
- A change to the duplicate definition has to keep the self-exclusion and the `DeviceName` condition.
