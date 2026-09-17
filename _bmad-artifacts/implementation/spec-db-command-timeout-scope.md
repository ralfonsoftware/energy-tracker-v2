---
title: 'Scope elevated SQL CommandTimeout to the Smart Plug import write path, not the whole DbContext'
type: 'bugfix'
created: '2026-09-17'
status: 'ready-for-dev'
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `spec-background-job-changetracker-orphan-fix` (2026-09-05) raised `CommandTimeout` from ADO.NET's 30s default to 120s globally, in `Program.cs`'s `ConfigureDbContext`, for the whole `DbContextOptionsBuilder` — both provider branches. That was a deliberate incident fix (Basic-tier Azure SQL hitting 100% DTU during a large Smart Plug import bulk insert), but it applies to every command on every request app-wide, not just the Smart Plug import write path that actually needed the headroom. An unrelated slow or blocked query anywhere else in the app now waits up to 120s instead of failing fast at 30s. That review explicitly deferred narrowing this, since the frozen spec at the time named `Program.cs`'s provider configuration as the fix target and switching to a per-call pattern was "a real design change worth its own scoped decision" — this spec is that decision.

**Approach:** Remove `.CommandTimeout(120)` from both provider branches in `Program.cs`, reverting the app-wide default back to 30s. Add `dbContext.Database.SetCommandTimeout(TimeSpan.FromSeconds(120))` inside `SmartPlugImportRepository.AddAsyncCore`, before its `SaveChangesAsync` call — mirroring the exact per-call pattern `UpdateMappingAsync` already uses at line 429 (`SetCommandTimeout(180)`), including that method's own reasoning: the elevated timeout applies "for the rest of this scoped DbContext's request too," since `dbContext` is a single DI-scoped instance shared by every method this repository calls for the lifetime of one background job (`AD-6`: one job, one DI scope, one worker).

## Boundaries & Constraints

**Always:** The elevated 120s timeout must still cover every EF-generated command in a `ProcessSmartPlugImport` job's lifecycle that previously relied on the global setting — confirmed to be `AddAsyncCore`'s own `SaveChangesAsync` (the `SmartPlugImport` row insert), plus, via the same shared `dbContext` instance, `PersistFailedImportAsync`'s `SaveChangesAsync` and `CompleteSmartPlugImportProcessing`'s `SaveChangesAsync` later in the same job. Apply the removal symmetrically to both `case "postgres"` and `case "sqlserver"` in `Program.cs` (AD-2 dual-provider symmetry) — do not leave one provider elevated and the other not.

**Ask First:** If, while implementing, another EF `SaveChangesAsync`/query call site on the Smart Plug import write path is found that isn't already covered by `AddAsyncCore`'s new `SetCommandTimeout` call (i.e., runs on a *different* DbContext instance/DI scope), halt and confirm whether it needs its own explicit `SetCommandTimeout` before narrowing the global default.

**Never:** Do not introduce a `Database:CommandTimeoutSeconds` config knob — this repo's convention (see `MaxBatchSize`'s own comment) is a hardcoded value with an explanatory comment, not a new config surface. Do not change the 120s figure itself, `UpdateMappingAsync`'s existing 180s, `BulkCopyTimeout`, or `MaxBatchSize` — this spec only relocates *where* the 120s applies, not any of the values themselves.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Ordinary request outside the Smart Plug import path (e.g. a Meter Reading write) hits a slow/blocked query | Any non-import DbContext usage | Command times out at the ADO.NET default (~30s), unchanged from pre-2026-09-05 behavior | `Execution Timeout Expired` surfaces same as before this repo ever raised the global default |
| `ProcessSmartPlugImport` job runs `AddAsyncCore` on Basic-tier Azure SQL under DTU pressure | Large import, slow `SmartPlugImport` row insert | Command has 120s of headroom, same as today | N/A |
| Same job later calls `CompleteSmartPlugImportProcessing`/`PersistFailedImportAsync` on the same DbContext instance | Later stage of the same background job | Still has 120s headroom (inherited from the same scoped `dbContext`, not re-set) | N/A |
| `UpdateMappingAsync` runs in a separate request/DI scope from any import job | Household maps a Power Point | Unaffected — already sets its own 180s independently of this change | N/A |

</frozen-after-approval>

## Code Map

- `src/EnergyTracker.Api/Program.cs:124-160` -- `ConfigureDbContext`: remove `.CommandTimeout(120)` from both the `case "postgres"` (line 137) and `case "sqlserver"` (line 159) branches; delete/rewrite the now-inaccurate comments explaining the global bump.
- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:87-121` -- `AddAsyncCore`: add `dbContext.Database.SetCommandTimeout(TimeSpan.FromSeconds(120));` before the `SaveChangesAsync` call at line 121, with a comment mirroring `UpdateMappingAsync`'s (line 423-429) explaining why and citing this spec.
- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:429` -- `UpdateMappingAsync`: unchanged; reference pattern only.
- `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs:461` -- existing `dbContext.Database.GetCommandTimeout().ShouldBe(180)` assertion for `UpdateMappingAsync`; add a sibling assertion for `AddAsyncCore`'s new 120s.

## Tasks & Acceptance

**Execution:**
- [ ] `src/EnergyTracker.Api/Program.cs` -- Remove `.CommandTimeout(120)` from both provider branches in `ConfigureDbContext`, restoring the ADO.NET/Npgsql default -- closes the "every query app-wide gets 120s" exposure.
- [ ] `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` -- Add `dbContext.Database.SetCommandTimeout(TimeSpan.FromSeconds(120))` at the top of `AddAsyncCore`, before its `SaveChangesAsync` -- restores the headroom for exactly the path the 2026-09-05 incident needed it for.
- [ ] `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs` -- Add a test asserting `dbContext.Database.GetCommandTimeout()` is `120` after `AddAsyncCore` runs, and (new) a test on a non-import repository (e.g. `MeterReadingRepositoryTests`) asserting the DbContext's command timeout is *not* elevated -- proves both halves of the scoping.

**Acceptance Criteria:**
- Given the `sqlserver` or `postgres` provider branch in `Program.cs`, when `ConfigureDbContext` runs, then the resulting `DbContextOptions` no longer sets an explicit `CommandTimeout` (defaults apply).
- Given a `ProcessSmartPlugImport` job's `AddAsyncCore` call, when it runs against either provider, then `dbContext.Database.GetCommandTimeout()` is `120` for the remainder of that job's DbContext scope.
- Given any non-Smart-Plug-import repository call (e.g. `MeterReadingRepository`), when it runs, then its DbContext's command timeout reflects the provider default, not 120.

## Verification

**Commands:**
- `dotnet test tests/EnergyTracker.Infrastructure.Tests --filter "FullyQualifiedName~SmartPlugImportRepositoryTests"` -- expected: all pass, including the new `AddAsyncCore` timeout assertion.
- `dotnet test tests/EnergyTracker.Infrastructure.Tests --filter "FullyQualifiedName~MeterReadingRepositoryTests"` -- expected: passes, including the new non-elevated-timeout assertion.
- `dotnet build` -- expected: no warnings/errors.
