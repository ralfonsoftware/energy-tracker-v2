---
title: 'CI pipeline runtime: shared test databases and parallel deploy jobs'
type: 'chore'
created: '2026-10-23'
status: 'done'
baseline_commit: '9c4b8e08913d656e73be548f62d8a662b31e7092'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-artifacts/project-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `app-deploy.yml` takes 13-17 minutes (run 39972356398: 13m22s) as one sequential job. 9 of those minutes are `Test .NET solution`, 8m57s of it `EnergyTracker.Infrastructure.Tests` (the other test projects finish within 1m34s). Its classes put `IAsyncLifetime` on the class, so xUnit starts a fresh Postgres or SQL Server container for every test method; `pr-review.yml` pays the same. The image build (about 40s) waits behind the tests, and 33s per deploy re-applies Container App settings that are already correct.

**Approach:** Start one Postgres and one SQL Server container per test run and give each test its own freshly created database on it. AMENDED 2026-10-23 (Ralf, Edit at Checkpoint 1): split `app-deploy.yml` into parallel `frontend-tests`, `backend-tests` and `image-build` jobs and a `deploy` job that needs all three; only `deploy` migrates, pushes the image and deploys. Make the registry/ingress step skip its writes when the live app already matches.

## Boundaries & Constraints

**Always:** Every test gets its own empty database (unique name) and still calls `MigrateAsync` itself. Both providers stay covered against real engines (AD-2). `image-build` has no Azure login, no secrets and no `id-token`; nothing reaches the registry, the database or the app unless frontend and backend tests and the image build all succeeded. Step names and order that `WorkflowPreflightGuardTests` and `MigrationDeployWorkflowRestorePointGuardTests` pin stay unchanged inside `deploy`. The registry/ingress skip falls back to the current writes when the live state differs or cannot be read.

**Ask First:** Raising `maxParallelThreads` above 8. Touching `EnergyTracker.Api.Tests` container usage. Splitting `backend-tests` per test project. Restructuring `pr-review.yml` beyond the faster tests.

**Never:** Weakening, deleting or deferring tests. Sharing one database between tests. Sharing the server in the minimal-permissions test (it creates a server-wide login). Changing `pr-review.yml` job ids (`build-test-lint` is a required check). Pushing an image before the tests pass.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Parallel first use | Several classes ask for SQL Server at once | One container starts; each gets its own database | N/A |
| Transient start failure | Container exits on start (CoInitializeSecurity crash, 2026-09-13) | Start retried up to 3 times with a new container | After 3 failures the original exception surfaces |
| Test teardown | Test ends with DbContexts closed | Database dropped, pooled connections cleared | Drop failure swallowed (the server dies with the run) |
| Any gate red | Frontend, backend or image job fails | `deploy` is skipped; no migration, push or deploy | N/A |

</frozen-after-approval>

## Code Map

- `tests/EnergyTracker.Infrastructure.Tests/*Tests.cs` -- 29 `new PostgreSqlBuilder(...)`/`new MsSqlBuilder(...)` fields; the minimal-permissions test keeps its own container
- `tests/EnergyTracker.Architecture.Tests/WorkflowPreflightGuardTests.cs` -- scans steps by name; its app-deploy case scans the whole file (`job: null`)
- `.github/workflows/app-deploy.yml` -- single job today; workflow-level `id-token: write` and `infra-app-deploy` concurrency
- `Dockerfile` -- builds the frontend itself, so the image job needs no test output
- `_bmad-artifacts/project-context.md` -- Testing Rules, CI

## Tasks & Acceptance

**Execution:**
- [x] `tests/EnergyTracker.Infrastructure.Tests/SharedDatabaseServers.cs` -- add an xUnit assembly fixture owning lazily started, retried Postgres and SQL Server containers, plus `PostgresTestDatabase`/`SqlServerTestDatabase` with the `StartAsync()`, `GetConnectionString()`, `DisposeAsync()` subset the tests use -- one-line swap per class
- [x] `tests/EnergyTracker.Infrastructure.Tests/*Tests.cs` -- swap each container field and `PostgreSqlContainer`/`MsSqlContainer` parameter type; test bodies untouched
- [x] `tests/EnergyTracker.Infrastructure.Tests/xunit.runner.json` -- `maxParallelThreads` 3 to 8 -- AMENDED 2026-10-23 (Ralf, added): the cap of 1b1960c throttled container starts, which no longer happen per test
- [x] `.github/workflows/app-deploy.yml` -- jobs: `frontend-tests` (npm ci, test), `backend-tests` (restore, build, frontend build for `wwwroot`, `dotnet test`), `image-build` (buildx, `build-push-action` with `push: false`, `cache-to: type=gha,scope=app-image,mode=max`), `deploy` (`needs` all three; `id-token: write` on this job only; existing steps, `build-push-action` with `cache-from` the same scope so the push reuses the layers; restore the migrations project explicitly); every job starts with the main-branch check -- tests, image and deploy tail overlap
- [x] `.github/workflows/app-deploy.yml` -- "Ensure ACR pull credential and target port": one `az containerapp show`, writes only on mismatch or unreadable output -- saves about 30s
- [x] `tests/EnergyTracker.Architecture.Tests/` -- scope the app-deploy preflight case to job `deploy`; add guards: Infrastructure tests build containers only in `SharedDatabaseServers.cs` and the minimal-permissions test; `deploy` needs all three jobs; `image-build` has no `azure/login` or `id-token`; "Build and push image" exists only in `deploy`
- [x] `_bmad-artifacts/project-context.md` -- add the shared-server rule and the new job layout

**Acceptance Criteria:**
- Given the full test run, when it finishes, then every test that passed before still passes and Infrastructure tests started one Postgres and one SQL Server container (plus the minimal-permissions one).
- Given two tests reading a whole table in parallel, then neither sees the other's rows.
- Given a push to main, when any of frontend tests, backend tests or image build fails, then `deploy` does not run. AMENDED 2026-10-23 (Ralf, added)
- Given all gates green, when `deploy` runs, then the pushed image comes from the cached layers of `image-build`'s build of the same commit. AMENDED 2026-10-23 (Ralf, added)
- Given the PR run for this change, then `Test .NET solution` takes at most 5 minutes (baseline 9m) with no container-start or timeout flake at 8 threads. AMENDED 2026-10-23 (Ralf, added)
- Given the app already uses system identity for the registry and port 8080, then the step issues no writes; given either differs, it applies both as before.

- 2026-10-23, follow-up (Ralf, after review): the Ask First item "touching `EnergyTracker.Api.Tests` container usage" was approved and done. The fixture moved to `tests/Shared/SharedDatabaseServers.cs` (linked into both test projects, namespace `EnergyTracker.TestSupport`), the five Api.Tests container sites use `PostgresTestDatabase`, and the guard became `TestsShareDatabaseServersTests` covering both projects. Local `Api.Tests`: 52s to 30s.

## Design Notes

Per-test cost remains: `CREATE DATABASE` plus 30 migrations; if the PR run shows it dominating, a follow-up can clone a pre-migrated template. The image handoff uses the Actions layer cache, not an artifact tarball: no 200 MB upload/download, and a cache miss only costs a rebuild. The `deploy` job repeats setup-dotnet and a migrations-project restore (about 20s), the price of the split.

## Verification

**Commands:**
- `dotnet test tests/EnergyTracker.Infrastructure.Tests` -- expected: all pass, one container per provider in `docker ps`
- `dotnet test tests/EnergyTracker.Architecture.Tests` -- expected: pass, including new and existing workflow guards
- `dotnet test EnergyTracker.sln --configuration Release` -- expected: all pass

**Manual checks (if no CLI):**
- PR run: `Test .NET solution` and `build-test-lint` against the 9m and about 12m baseline.
- First deploy after merge (needs your OK to merge): the jobs overlap in the run graph, `deploy` waits for all three, the push log shows `CACHED` layers, the ACR step logs a skip, the health check passes.

## Suggested Review Order

**Shared test database servers (the 9-minute fix)**

- Entry point: one lazily started, retried container per provider, disposed by the assembly fixture.
  [`SharedDatabaseServers.cs:16`](../../tests/Shared/SharedDatabaseServers.cs#L16)

- Retry on a transient container start failure, the cost of sharing one server.
  [`SharedDatabaseServers.cs:60`](../../tests/Shared/SharedDatabaseServers.cs#L60)

- Per-test Postgres database; creates serialized because they copy `template1`.
  [`SharedDatabaseServers.cs:94`](../../tests/Shared/SharedDatabaseServers.cs#L94)

- SQL Server counterpart; drop uses single-user rollback.
  [`SharedDatabaseServers.cs:161`](../../tests/Shared/SharedDatabaseServers.cs#L161)

- Typical one-line swap; test bodies untouched.
  [`BackgroundJobRepositoryTests.cs:15`](../../tests/EnergyTracker.Infrastructure.Tests/BackgroundJobRepositoryTests.cs#L15)

- Api.Tests follow-up: the web factory fixture takes a database, not a container.
  [`EnergyTrackerApiFactory.cs:19`](../../tests/EnergyTracker.Api.Tests/EnergyTrackerApiFactory.cs#L19)

- Static helper now takes the database type, not a container.
  [`SmartPlugImportRepositoryTests.cs:16`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs#L16)

- Parallelism 3 to 8, Ralf's call; no container start storm left to throttle.
  [`xunit.runner.json:4`](../../tests/EnergyTracker.Infrastructure.Tests/xunit.runner.json#L4)

**Deploy pipeline jobs**

- Image builds beside the tests, no credentials, exports the layer cache.
  [`app-deploy.yml:136`](../../.github/workflows/app-deploy.yml#L136)

- Deploy gates on all three jobs; the only one with `id-token`.
  [`app-deploy.yml:164`](../../.github/workflows/app-deploy.yml#L164)

- Push reuses the cached layers from the image job.
  [`app-deploy.yml:431`](../../.github/workflows/app-deploy.yml#L431)

- Registry and ingress writes skipped when the live app already matches.
  [`app-deploy.yml:458`](../../.github/workflows/app-deploy.yml#L458)

**Guards**

- The `needs` list cannot lose a gate.
  [`AppDeployWorkflowJobsGuardTests.cs:22`](../../tests/EnergyTracker.Architecture.Tests/AppDeployWorkflowJobsGuardTests.cs#L22)

- Gate jobs cannot reach Azure, secrets or the registry.
  [`AppDeployWorkflowJobsGuardTests.cs:36`](../../tests/EnergyTracker.Architecture.Tests/AppDeployWorkflowJobsGuardTests.cs#L36)

- No return to a container per test class, in Infrastructure.Tests or Api.Tests.
  [`TestsShareDatabaseServersTests.cs:27`](../../tests/EnergyTracker.Architecture.Tests/TestsShareDatabaseServersTests.cs#L27)

- Preflight guard now reads the `deploy` job.
  [`WorkflowPreflightGuardTests.cs:26`](../../tests/EnergyTracker.Architecture.Tests/WorkflowPreflightGuardTests.cs#L26)

**Docs**

- Shared-server rule for the next test author.
  [`project-context.md:92`](../project-context.md#L92)

- Four-job layout and cache handoff.
  [`project-context.md:135`](../project-context.md#L135)
