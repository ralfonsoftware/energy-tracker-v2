---
baseline_commit: 4395ddb67b2291bdfc43e262480c82dfc1807d8d
---

# Story 1.1: Deployable Application Skeleton (Local Dev & Self-Host)

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Self-Hoster,
I want to bring up a running Energy Tracker instance from the repo via Docker Compose,
so that I have a working, healthy self-hosted deployment before any feature exists.

## Acceptance Criteria

1. **Given** a fresh clone of the repository, **when** I run `docker-compose up`, **then** the API and Postgres containers start and the API's `/health` endpoint returns 200 (liveness only, no DB/dependency check).
2. **Given** the Docker Compose stack, **when** run on modest self-hosted hardware (e.g. a low-power NAS/single-board-computer), **then** it runs comfortably, using the same container image and Compose file that also serves as the self-host reference deployment — no separate "cloud edition" exists (NFR2).
3. **Given** the running instance, **when** inspected for outbound calls, **then** it makes no telemetry/analytics phone-home by default, and requires no third-party account to view the Household's own data (NFR12).
4. **Given** the solution structure, **when** inspected, **then** it matches the Architecture Spine's Structural Seed (`Domain`, `Application`, `Infrastructure`, `Infrastructure.Migrations.Postgres`, `Infrastructure.Migrations.SqlServer`, `Api`, `web/`), and `Domain` has zero external package references beyond the BCL (AD-1).
5. **Given** `scripts/add-migration.sh <Name>`, **when** run against a model change, **then** a migration is generated in both provider migrations projects atomically, never just one (AD-2).
6. **Given** the repository and running containers, **when** inspected for secrets (DB connection string, OIDC client secret, AI API key), **then** none are committed to source control or baked into the image — all are supplied via environment variables or a self-host `.env` file (AD-19).
7. **Given** the written setup docs alone, **when** a new Self-Hoster follows them with no other support channel, **then** they can go from clone to a running instance (NFR11).
8. **Given** the API process, **when** it logs anything, **then** output is structured and goes to stdout/stderr only, with no environment-specific branching in logging code (AD-19).

## Tasks / Subtasks

- [x] Task 1: Scaffold the .NET solution matching the Structural Seed (AC: #4)
  - [x] Create `EnergyTracker.sln` at repo root
  - [x] Create `src/EnergyTracker.Domain/` — class library, **zero package references beyond the BCL** (no EF Core, no ASP.NET Core, no Azure SDK — this is the AC #4 checkpoint most likely to be violated accidentally by a stray `using` or a convenience package add)
  - [x] Create `src/EnergyTracker.Application/` — class library, references only `Domain`; define port interface stubs here later (not required by this story's ACs, but the empty project must exist so Story 1.5+ can add to it without restructuring)
  - [x] Create `src/EnergyTracker.Infrastructure/` — class library, references `Application` + `Domain`; this is where `EnergyTrackerDbContext` will live (not required to have entities yet — this story only needs the DbContext to exist enough to support a migration, see Task 3)
  - [x] Create `src/EnergyTracker.Infrastructure.Migrations.Postgres/` — migrations-only class library project
  - [x] Create `src/EnergyTracker.Infrastructure.Migrations.SqlServer/` — migrations-only class library project
  - [x] Create `src/EnergyTracker.Api/` — ASP.NET Core Minimal API project (the composition root / `Program.cs`), references `Infrastructure`
  - [x] Add all projects to `EnergyTracker.sln`
- [x] Task 2: Implement the `/health` liveness endpoint (AC: #1, #8)
  - [x] Add a Minimal API route `GET /health` in `EnergyTracker.Api` that returns 200 with no DB/dependency check (liveness only — see AD-19 rationale: a slow Postgres/Azure SQL must never fail this probe)
  - [x] Configure structured logging (Serilog or equivalent) writing to stdout/stderr only — no `if (env == "Development")`-style branching in logging setup (AD-19)
- [x] Task 3: Wire up `EnergyTrackerDbContext` and the dual-provider migration path (AC: #4, #5)
  - [x] Add `EnergyTrackerDbContext : DbContext` in `Infrastructure` (empty `DbSet`s are fine for this story — no domain entities exist yet; this story is about the skeleton, not the schema)
  - [x] Wire provider selection in `Api/Program.cs` from a single `Database:Provider` config value (`Postgres` | `SqlServer`), read once at the composition root — never branched on elsewhere (AD-2)
  - [x] Configure `Infrastructure.Migrations.Postgres` and `Infrastructure.Migrations.SqlServer` each via `.MigrationsAssembly(...)` pointing at the shared `EnergyTrackerDbContext`
  - [x] Write `scripts/add-migration.sh <Name>` — runs `dotnet ef migrations add <Name>` against **both** provider migration projects in one invocation, so a migration is never added to only one (AD-2)
  - [x] Smoke-test the script against a trivial model change to confirm both projects receive the migration atomically
- [x] Task 4: Scaffold the `web/` frontend (AC: #4)
  - [x] Initialize React 19 + Vite 8 + TypeScript project under `web/`
  - [x] Add Tailwind CSS v4 + shadcn/ui, per DESIGN.md's token foundation (base setup only — no screens are required by this story's ACs)
  - [x] Configure the Vite build output to land in `src/EnergyTracker.Api/wwwroot` so the Api project can serve it as static files
- [x] Task 5: Single-artifact Dockerfile and Docker Compose stack (AC: #1, #2, #4, #6)
  - [x] Write a multi-stage `Dockerfile`: build `web/` → build .NET solution → runtime image, producing one container image that serves the built SPA from the API (AD-13 — no separate "cloud edition" image)
  - [x] Write `docker-compose.yml`: `api` (built from the Dockerfile) + `postgres` — this is both the local dev stack and the self-host reference deployment (same file, same image)
  - [x] Write `docker-compose.sqlserver.yml` as an optional profile/override that swaps in SQL Server for local testing of that provider path
  - [x] Wire secrets (DB connection string, OIDC client secret — even if unused until Story 1.5, reserve the config key now so the shape is right — AI API key) via environment variables / a self-host `.env.example` file — verify nothing is hardcoded or baked into the image (AC #6)
  - [x] Verify no outbound telemetry/analytics calls exist anywhere in the default template/scaffold (check default ASP.NET Core telemetry, Vite/npm analytics prompts, etc. — disable any that are on by default) (AC #3)
- [x] Task 6: Self-host setup documentation (AC: #7)
  - [x] Write a top-level `docs/self-hosting.md` (or extend `README.md`) covering: prerequisites (Docker/Docker Compose), clone, `.env` setup, `docker-compose up`, and how to verify `/health` returns 200
  - [x] Do not reference any support channel as required — the doc alone must be sufficient
- [x] Task 7: Verify against every AC end-to-end
  - [x] Fresh clone in a clean directory, run `docker-compose up`, confirm `/health` returns 200 (AC #1)
  - [x] Confirm `Domain.csproj` has no `PackageReference` beyond what ships in the BCL (AC #4)
  - [x] Run `scripts/add-migration.sh SmokeTest` against a throwaway model change, confirm both migration projects updated, then revert (AC #5)
  - [x] Grep the repo and the built image for secrets; confirm `.env` (not `.env.example`) is git-ignored (AC #6)
  - [x] Tail container logs, confirm structured output on stdout/stderr (AC #8)

## Dev Notes

- **This is the first story in the project — there is no existing code.** The repo currently contains only planning artifacts (`_bmad-artifacts/`), `_docs/`, `docs/`, `sample-data/`, `README.md`, `LICENSE`, `.gitignore`. `.gitignore` already has the standard Visual Studio/.NET template, so `bin/`, `obj/`, `.vs/`, etc. are already excluded — do not duplicate these entries when scaffolding the solution. There is no `web/`, no `src/`, no `.sln` yet: Task 1 through Task 5 create the entire skeleton from nothing.
- **Do not build any feature logic in this story.** No entities, no use cases, no auth, no Household model. This story's job is *only* the buildable/deployable skeleton — `Domain` and `Application` projects can (and should) be created essentially empty, deferring content to Story 1.5 onward. Resist the temptation to pre-build ORM entities "since you're in there" — that's Story 1.5+'s job and doing it now risks guessing wrong about shapes those stories will actually specify.
- **AD-1 is the highest-risk AC to violate by accident.** `EnergyTracker.Domain` must have zero external package references beyond the BCL — not even a JSON or validation helper package. If Task 3's `EnergyTrackerDbContext` work tempts you to reference EF Core from `Domain` "just for an attribute," don't — EF Core lives only in `Infrastructure`.
- **AD-2 dual-provider migrations are the other high-risk area.** The two migrations projects are migrations-only — they should not contain the `DbContext` itself (that stays in `Infrastructure`), only the provider wiring plus `.MigrationsAssembly(...)` pointing back at it. `scripts/add-migration.sh` is not optional tooling — it's the AC #5 checkpoint, and it must add to both projects in one shell invocation so a developer physically cannot add a migration to only one provider by accident.
- **AD-13 single-artifact deployment** governs the Dockerfile: one image, multi-stage (`web/` build → .NET build → runtime), and the same image/Compose file used for local dev doubles as the self-host reference deployment — there is no separate "cloud edition" Dockerfile or Compose file. `docker-compose.sqlserver.yml` is an *additional* optional profile for testing, not a second deployment path.
- **AD-19 operational baseline**: `/health` is liveness-only by design — do not add a DB ping or dependency check to it, even though that might feel more "correct." A slow Postgres/Azure SQL failing that probe would cause Container Apps to restart-loop the app in production; this is a deliberate constraint carried from the architecture, not an oversight to "fix" later. Logging must have zero `if (environment == ...)` branches — same Serilog-to-stdout config in every environment.
- **Config-driven adapter selection (Consistency Conventions)**: the `Database:Provider` value is read exactly once, in `Api/Program.cs` (the composition root). Nothing in `Infrastructure` should re-read or branch on it independently.
- **Secrets (AC #6)**: reserve config keys now for DB connection string, OIDC client secret, and AI API key even though OIDC (Story 1.5) and AI (later epic) aren't implemented yet — this avoids reshaping the config surface later. Ship a `.env.example` with placeholder values; the real `.env` must be git-ignored.
- **No previous story exists** — this is Story 1.1, the first story of the first epic. There is no prior Dev Notes / learnings to inherit.

### Project Structure Notes

Target structure (from Architecture Spine's Structural Seed — create exactly this, nothing more):

```text
energy-tracker-v2/
  src/
    EnergyTracker.Domain/
    EnergyTracker.Application/
    EnergyTracker.Infrastructure/
    EnergyTracker.Infrastructure.Migrations.Postgres/
    EnergyTracker.Infrastructure.Migrations.SqlServer/
    EnergyTracker.Api/
  web/                                # builds into EnergyTracker.Api/wwwroot
  scripts/
    add-migration.sh
  docker-compose.yml                  # api + postgres (default provider); doubles as self-host reference
  docker-compose.sqlserver.yml        # optional profile: swap in sqlserver
  Dockerfile                          # multi-stage: web/ -> .NET -> runtime (AD-13)
  EnergyTracker.sln
```

No variance from this structure is expected or justified for this story — it is prescribed exactly by the Architecture Spine, not inferred.

### References

- [Source: _bmad-artifacts/planning/epics/epic-1-foundation-deployment-household-access.md#Story 1.1] — story statement and acceptance criteria (verbatim origin)
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE.md#AD-1] — Ports & Adapters dependency direction, Domain zero-dependency rule
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE.md#AD-2] — dual database-provider persistence shape, `scripts/add-migration.sh` requirement
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE.md#AD-13] — single-artifact deployment, backend serves the SPA
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE.md#AD-19] — operational baseline: health, logs, secrets
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE.md#Structural Seed] — exact target directory layout
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE.md#Stack] — pinned versions: .NET 10, ASP.NET Core 10 Minimal APIs, EF Core 10, Npgsql 10.0.3, React 19.x, Vite 8.x, shadcn/ui + Tailwind v4, Docker/Docker Compose current stable
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/SOLUTION-OVERVIEW.md#Frontend hosting: why not split it out] — rationale for single-container SPA hosting, do not reintroduce a Static Web Apps split
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/cross-cutting-nfrs.md] — NFR2 hosting cost-efficiency, NFR11 documentation as onboarding path, NFR12 privacy/no telemetry
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/constraints-and-guardrails.md] — privacy/no telemetry phone-home, no paid third-party service required for a basic self-hosted instance

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

- Root-caused a silent EF Core migration-discovery failure: `Infrastructure.Tests` resolved `Microsoft.EntityFrameworkCore.Relational` to 10.0.4 (Npgsql's loose floor) instead of 10.0.10, because it only referenced the Postgres migrations project and nothing forced the floor up (unlike `Api`, which also pulls the SqlServer provider). Fixed by adding centralized package version management (`Directory.Packages.props`) and an explicit `Microsoft.EntityFrameworkCore.Relational` pin.
- `dotnet new sln` on the installed SDK now defaults to the `.slnx` format; recreated with `-f sln` to match the story's `EnergyTracker.sln` filename exactly.
- shadcn/ui CLI wrote generated files to a literal `@/` directory instead of resolving the tsconfig path alias; moved `button.tsx` / `utils.ts` into `src/` manually.
- Postgres 18's official image changed its expected volume mount point from `/var/lib/postgresql/data` to `/var/lib/postgresql`; updated `docker-compose.yml` accordingly.

### Completion Notes List

- Backend test stack: xUnit v3 (Microsoft.Testing.Platform runner, per user request to use the latest major version) + Shouldly (assertions) + NSubstitute (mocking, reserved for future stories) + Testcontainers.PostgreSql (real-database integration tests) + Microsoft.AspNetCore.Mvc.Testing (WebApplicationFactory). Selections confirmed with the user before implementation.
- Frontend test stack: Vitest + React Testing Library (unit/component) + Playwright (E2E, chromium only for now). Selections confirmed with the user before implementation.
- Added `Directory.Packages.props` (central package version management) — not explicitly requested by the story, but required to fix a genuine transitive EF Core version conflict that was silently breaking migration discovery in tests; keeps all EF Core-related packages pinned to one version across every project.
- `EnergyTracker.Architecture.Tests` includes an automated regression guard for AD-1 (Domain must have zero external package references) so this constraint can't regress silently in a future story.
- `EnergyTracker.Infrastructure.Tests` applies the real `InitialCreate` Postgres migration against a Testcontainers-provisioned Postgres instance, proving the dual-provider migration pipeline actually works end-to-end, not just that `dotnet ef migrations add` succeeds.
- AC #5's migration-atomicity guarantee was smoke-tested live: added a throwaway `SmokeTestEntity`/`DbSet`, ran `scripts/add-migration.sh SmokeTest`, confirmed both provider projects received the migration, then reverted via `dotnet ef migrations remove` on both and removed the throwaway entity — repo is back to only the `InitialCreate` migration in each provider.
- All 8 acceptance criteria were verified end-to-end via a real `docker compose up --build`, not just unit tests: `/health` returns 200 (AC1), same Compose file/image serves as both dev stack and self-host reference with no separate cloud edition (AC2), no telemetry/analytics code or outbound calls present and `dotnet`/`npm` build-time telemetry explicitly disabled (AC3), structure matches the Structural Seed exactly (AC4), dual-provider migrations verified atomic (AC5), grepped repo + built image for secrets — none found, `.env` confirmed git-ignored (AC6), `docs/self-hosting.md` written to stand alone with no support-channel dependency (AC7), tailed container logs and confirmed structured Serilog output on stdout only (AC8).
- `web/README.md` (Vite's default template README) was deleted as noise; the top-level `README.md` now links to `docs/self-hosting.md`.

### File List

- `.gitignore` (modified)
- `README.md` (modified)
- `.env.example`
- `Directory.Packages.props`
- `Dockerfile`
- `docker-compose.yml`
- `docker-compose.sqlserver.yml`
- `EnergyTracker.sln`
- `global.json`
- `.config/dotnet-tools.json`
- `docs/self-hosting.md`
- `scripts/add-migration.sh`
- `src/EnergyTracker.Domain/EnergyTracker.Domain.csproj`
- `src/EnergyTracker.Application/EnergyTracker.Application.csproj`
- `src/EnergyTracker.Infrastructure/EnergyTracker.Infrastructure.csproj`
- `src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/EnergyTracker.Infrastructure.Migrations.Postgres.csproj`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/EnergyTrackerDbContextFactory.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260809151432_InitialCreate.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260809151432_InitialCreate.Designer.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/EnergyTrackerDbContextModelSnapshot.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/EnergyTracker.Infrastructure.Migrations.SqlServer.csproj`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/EnergyTrackerDbContextFactory.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260809151434_InitialCreate.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260809151434_InitialCreate.Designer.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/EnergyTrackerDbContextModelSnapshot.cs`
- `src/EnergyTracker.Api/EnergyTracker.Api.csproj`
- `src/EnergyTracker.Api/Program.cs`
- `src/EnergyTracker.Api/appsettings.json`
- `src/EnergyTracker.Api/appsettings.Development.json`
- `src/EnergyTracker.Api/Properties/launchSettings.json`
- `src/EnergyTracker.Api/wwwroot/.gitkeep`
- `tests/EnergyTracker.Architecture.Tests/EnergyTracker.Architecture.Tests.csproj`
- `tests/EnergyTracker.Architecture.Tests/DomainHasNoExternalDependenciesTests.cs`
- `tests/EnergyTracker.Architecture.Tests/xunit.runner.json`
- `tests/EnergyTracker.Infrastructure.Tests/EnergyTracker.Infrastructure.Tests.csproj`
- `tests/EnergyTracker.Infrastructure.Tests/PostgresMigrationTests.cs`
- `tests/EnergyTracker.Infrastructure.Tests/xunit.runner.json`
- `tests/EnergyTracker.Api.Tests/EnergyTracker.Api.Tests.csproj`
- `tests/EnergyTracker.Api.Tests/HealthEndpointTests.cs`
- `tests/EnergyTracker.Api.Tests/DatabaseProviderSelectionTests.cs`
- `tests/EnergyTracker.Api.Tests/xunit.runner.json`
- `web/package.json`, `web/package-lock.json`
- `web/index.html`
- `web/vite.config.ts`
- `web/tsconfig.json`, `web/tsconfig.app.json`, `web/tsconfig.node.json`
- `web/components.json`
- `web/.oxlintrc.json`, `web/.gitignore`
- `web/public/favicon.svg`
- `web/src/main.tsx`, `web/src/App.tsx`, `web/src/App.test.tsx`, `web/src/index.css`
- `web/src/components/ui/button.tsx`
- `web/src/lib/utils.ts`
- `web/src/test/setup.ts`
- `web/playwright.config.ts`
- `web/e2e/app-shell.spec.ts`

## Change Log

- 2026-08-09: Implemented the full deployable application skeleton — .NET solution scaffold (Domain/Application/Infrastructure/dual migrations/Api), `/health` liveness endpoint with Serilog structured logging, dual-provider (Postgres/SqlServer) EF Core migrations wired through `scripts/add-migration.sh`, React 19 + Vite 8 + Tailwind v4 + shadcn/ui frontend building into the Api's `wwwroot`, single-artifact multi-stage Dockerfile + Docker Compose stack (default Postgres, optional SQL Server override), self-hosting documentation, and full backend (xUnit v3 + Shouldly + NSubstitute + Testcontainers) and frontend (Vitest + React Testing Library + Playwright) test infrastructure. All 8 acceptance criteria verified end-to-end via a real `docker compose up --build`.
