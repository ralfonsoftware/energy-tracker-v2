---
title: 'Fix ChangeTracker.Clear() orphaning BackgroundJob rows + bump SQL command timeout'
type: 'bugfix'
created: '2026-09-05'
status: 'done'
review_loop_iteration: 0
context: []
baseline_commit: '72b1e927d2ad9f2ed03d0376dff412b6f795e99e'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `SmartPlugImportRepository.AddAsync` calls `dbContext.ChangeTracker.Clear()` on any failure inside `AddAsyncCore`. This wipes every tracked entity on the shared scoped `DbContext`, including the `BackgroundJob` row `BackgroundJobProcessor.ProcessAsync` (the caller two frames up) is tracking. Its subsequent `job.Status = Failed` mutation then targets a detached entity, so `SaveChangesAsync()` silently no-ops — the row stays `Processing` forever, the queue message is deleted as if processing succeeded, and the job becomes permanently unrecoverable. Reproduced in production 2026-09-05 10:56 UTC: Azure SQL Basic tier (5 DTU) hit 100% during a bulk `SmartPlugReadings` insert, the command hit the default 30s timeout, and this bug then swallowed the resulting failure status update.

**Approach:** (1) Narrow the clear to only the entity `AddAsyncCore` itself added (`import`), via `dbContext.Entry(import).State = EntityState.Detached` instead of `ChangeTracker.Clear()`. (2) Raise `CommandTimeout` for both DB providers so a slow-but-legitimate bulk insert on Basic tier doesn't trip the default 30s ceiling in the first place.

## Boundaries & Constraints

**Always:** Preserve the original intent of the `ChangeTracker.Clear()` fix (Story 3.9 review) — `PersistFailedImportAsync`'s follow-up `AddAsync` of a new `SmartPlugImport` with the same `Id` must still not throw "already being tracked". Any entity added inside `AddAsyncCore` before a failure point must be detached; entities the caller tracked before calling `AddAsync` must survive untouched. Apply the `CommandTimeout` change to both `case "sqlserver"` and `case "postgres"` in `Program.cs` (AD-2 dual-provider symmetry).

**Ask First:** None — scope, files, and exact code changes are already fixed by this spec.

**Never:** Do not touch `BackgroundJobProcessor`'s own tracking logic, the queue-visibility-timeout/scale-to-zero behavior, or the Azure SQL service tier — out of scope for this fix. Do not add a `Database:CommandTimeoutSeconds` config knob — hardcode with a comment, matching this file's existing `MaxBatchSize` comment style.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Bulk-insert failure after `import` row saved | `AddAsyncCore` throws while inserting `readings` (e.g. FK violation), with an unrelated `BackgroundJob` entity already tracked as `Modified` on the same `DbContext` | Only `import`'s tracker entry is detached; the `BackgroundJob` entity's `Modified` state and pending property changes are untouched | Exception still rethrown, unchanged |
| Retry re-adds a `SmartPlugImport` with the same `Id` | `PersistFailedImportAsync` calls `AddAsync` again after the above | No "already being tracked" `InvalidOperationException` | N/A |
| Large/slow bulk insert on Basic-tier SQL | A `SmartPlugReadings` bulk insert takes >30s but <configured timeout | Command completes; no `Execution Timeout Expired` | N/A |

</frozen-after-approval>

## Code Map

- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` -- `AddAsync` (~L33-56): replace `ChangeTracker.Clear()` with targeted detach of `import`.
- `src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs` -- caller whose tracked `job` entity was getting orphaned; read-only reference for the test, no change expected.
- `src/EnergyTracker.Api/Program.cs` -- `ConfigureDbContext` (~L124-147): add `CommandTimeout` to both provider branches.
- `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs` -- add regression test using the existing Testcontainers-Postgres pattern.
- `tests/EnergyTracker.Infrastructure.Tests/BackgroundJobProcessorTests.cs` -- reference for existing fixture/DI setup conventions only.

## Tasks & Acceptance

**Execution:**
- [x] `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` -- In `AddAsync`'s catch block, replace `dbContext.ChangeTracker.Clear();` with `dbContext.Entry(import).State = EntityState.Detached;` -- fixes the orphaning without changing the original collision-avoidance intent.
- [x] `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs` -- Add a test: seed a `Household`+`BackgroundJob`, track the `BackgroundJob` as `Modified` on the same `DbContext` the repository under test uses, force `AddAsyncCore` to fail after the `import` row's own insert succeeds (e.g. a reading referencing a non-existent `PowerPointId` to trigger an FK violation during the bulk-insert step), then assert: the `BackgroundJob` entity's tracked state/changes survive, and a follow-up `AddAsync` call reusing the same `SmartPlugImport.Id` does not throw -- proves the fix without needing the full `ProcessSmartPlugImport`/`BackgroundJobProcessor` pipeline.
- [x] `src/EnergyTracker.Api/Program.cs` -- Add `.CommandTimeout(120)` to the `UseSqlServer` options action (`case "sqlserver"`) and the `UseNpgsql` options action (`case "postgres"`), each with a short comment tying it to this incident's 100%-DTU/30s-timeout evidence and noting a DB tier upgrade is the durable fix if 120s ever isn't enough -- prevents the triggering condition from this specific incident while `MaxBatchSize(1000)` stays as-is.

**Acceptance Criteria:**
- Given a `DbContext` with an unrelated tracked entity and an `AddAsyncCore` call that fails partway through, when `AddAsync`'s catch block runs, then only the `import` entity is detached and the unrelated entity's tracked state is unchanged.
- Given a prior `AddAsync` failure for a given `SmartPlugImport.Id`, when `AddAsync` is called again with a new instance carrying the same `Id`, then no "already being tracked" exception is thrown.
- Given the SQL Server or Postgres provider branch in `Program.cs`, when `ConfigureDbContext` runs, then the resulting `DbContextOptions` has `CommandTimeout == 120`.

## Design Notes

The detach must happen even when `import` was never actually added (e.g. if a hypothetically earlier line threw before `AddAsync(import, ...)` ran) — `dbContext.Entry(import)` is safe to call regardless of tracking state; if `import` isn't tracked, `.State = Detached` is a no-op. No guard needed.

## Verification

**Commands:**
- `dotnet test tests/EnergyTracker.Infrastructure.Tests --filter "FullyQualifiedName~SmartPlugImportRepositoryTests"` -- expected: all pass, including the new regression test (red before the fix, green after).
- `dotnet build` -- expected: no warnings/errors from the `Program.cs` change (nullable/implicit usings enabled).

## Suggested Review Order

**The detach fix (ChangeTracker orphaning)**

- Entry point — snapshot-then-diff replaces the naive single-entity detach after review found it misses entities that already advanced past `Added` before the failure.
  [`SmartPlugImportRepository.cs:37`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L37)

- Detach anything newly tracked since the snapshot, not filtered by current state — the actual fix.
  [`SmartPlugImportRepository.cs:80`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L80)

- Proves the caller's own tracked `BackgroundJob` entity survives and its Failed status actually persists.
  [`SmartPlugImportRepositoryTests.cs:678`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs#L678)

- Proves the harder case review surfaced: a `boundaryCorrection`'s internally-created `AuditCorrection` also gets cleaned up, with no phantom row left behind.
  [`SmartPlugImportRepositoryTests.cs:755`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs#L755)

**The timeout fix (two settings, not one)**

- `BulkCopyTimeout` is the setting that actually governs the bulk insert that timed out in production — `CommandTimeout` alone doesn't reach it.
  [`SmartPlugImportRepository.cs:163`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L163)

- `CommandTimeout(120)` covers ordinary EF-generated commands only (SQL Server branch); comment now states what it does and doesn't cover.
  [`Program.cs:137`](../../src/EnergyTracker.Api/Program.cs#L137)

- Same bump for Postgres, honestly framed as precautionary symmetry rather than incident-evidenced.
  [`Program.cs:159`](../../src/EnergyTracker.Api/Program.cs#L159)
