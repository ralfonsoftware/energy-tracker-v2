---
baseline_commit: 4e1c596
---

# Story 11.21: Self-Host Migration Path

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- Epic 11 "Codebase audit hardening" (planning/epics/epic-11-codebase-audit-hardening.md), tier "Triage follow-ups", promoted from deferred-work.md (Story 10.1 create-story entry, line ~92 index row and ~482 body). Architecture: AD-13 (one image, compose file is the self-host reference), AD-2 (dual provider, migrations in both projects), AD-24 (one instance per database). This file cites the AD IDs, which satisfies the spec-per-fix gate (project-context.md). -->
<!-- Branch: feature/11-21-self-host-migration-path (current checkout, clean, at 4e1c596). ONE PR. No new migration, no new NuGet or npm package, no wire change. -->
<!-- ASK FIRST DECIDED 2026-11-01: Ralf chose option B (one-shot compose `migrate` service built from a new Dockerfile target, running an EF migrations bundle; `api` waits on it with `service_completed_successfully`). Options A (script) and C (startup migration, was the create-story recommendation) rejected. ACs below are marked AMENDED per project-context.md. -->
<!-- Live gate: YES. A clean-volume run of the real stack (Task 6) is the AC. First-household sign-in through the live OIDC provider (Auth0 test user, Chrome) must pass before review moves to done; it is not deferred. -->

## Story

As a self-hoster,
I want a documented, working way to apply EF Core migrations,
so that a fresh install reaches a usable database (promoted from `deferred-work.md`, Story 10.1 create-story entry).

## Acceptance Criteria

1. **Given** a fresh self-host install from `docker-compose.yml` on a clean volume (`docker compose down -v`, then `docker compose up -d --build`), **when** this story is done, **then** the database reaches the current schema with no step beyond `cp .env.example .env`, filling `.env`, and `docker compose up -d`, `docs/self-hosting.md` states how migrations are applied (new section "Database migrations"), and the path is verified end to end on a clean volume (Task 6; evidence in Completion Notes: commands, `__EFMigrationsHistory` row count, `/health`, and a first-household sign-in).
2. **Given** the chosen path (documented script, one-shot compose service, or startup migration), **when** it fails or the schema is behind, **then** the operator sees a clear message that names the cause, the setting and the command to read the log, and the decision is recorded as an Ask First before development (Task 0; Ralf's choice, date and the rejected options go in Completion Notes and the AC text is marked `AMENDED <date, decision>`).
   - **AMENDED 2026-11-01, Ralf: option B.** The path is a one-shot compose service `migrate` (new Dockerfile target `migrate`, EF migrations bundle). On failure `migrate` prints a fixed message (pinned in `scripts/self-host/migrate-entrypoint.sh`, quoted in `docs/self-hosting.md`) that names the cause class, says the `api` container was not started, and gives the commands `docker compose logs migrate` and `docker compose up -d`; it exits non-zero, `docker compose up` stops, and `api` never starts. "Schema is behind" is handled by the same service: it runs on every `docker compose up`.
3. **Implied (Azure unchanged):** the Azure production deploy keeps migrating in CI (`app-deploy.yml`, Story 10.1 restore point) and nothing in this story makes the Container App migrate itself. Whatever the chosen path, it is off by default outside the `docker-compose.yml` reference deployment.
   - **AMENDED 2026-11-01, Ralf: option B.** The `migrate` service and Dockerfile target exist only for the compose file; `app-deploy.yml`, `infra/**` and the Container App never reference the `migrate` target, and the default (last) Dockerfile stage stays `runtime`, so `docker build .` in CI/Azure still produces the API image. `AppDeployWorkflowJobsGuardTests` and `MigrationDeployWorkflowRestorePointGuardTests` stay green untouched.
4. **Implied (upgrade, not only fresh install):** an existing self-host volume that is behind (older release, some migrations missing) reaches the current schema on the next `docker compose up -d --build`, verified in Task 6 by starting from a volume migrated to an earlier migration. The docs say: back up the Postgres volume first (`docker compose exec postgres pg_dump ...` command, restore command), because a self-host install has no automatic restore point (Story 10.1 covers Azure only).
5. **Implied (carried from Story 11.20 review, deferred-work.md ~line 743):** `docs/self-hosting.md` documents that `DropHouseholdInviteTokenColumn` (Story 11.20) drops the legacy plaintext invite `Token` column, so an install that still holds an unexpired pre-Story-11.9 invite loses that link (the invite answers 404; create a new one, invites live 7 days). Include the check query for the Postgres form (`SELECT COUNT(*) FROM "HouseholdInvites" WHERE "Token" IS NOT NULL AND "ExpiresAtUtc" > NOW();`). Then delete that `[open]` entry from `deferred-work.md` (resolved by this story, per the convention in `project-context.md`).
6. **Implied (docs consistency):** the three places that tell a reader "the API does not migrate on startup" or "run `./scripts/migrate.sh`" say the right thing for the chosen path: `docs/local-development.md` (line ~32 step list, and the Troubleshooting entry ~line 311 `relation "DataProtectionKeys" does not exist`), `scripts/migrate.sh` header comment if its role changes, and `docs/self-hosting.md` section 3 ("Start the stack", it lists two containers; it now starts `api`, `postgres`, `aspire-dashboard` and a short-lived `migrate` that exits 0). The `deferred-work.md` entries tagged `[promoted: 11.21]` (index row ~line 92 and body ~line 482) are deleted.
7. **Implied (no silent drift):** a test pins the contract so the next edit cannot undo it. Known-bad sample proves the guard is non-vacuous where the guard is a text scan.
   - **AMENDED 2026-11-01, Ralf: option B.** Two text-scan guards (Task 2): the Dockerfile keeps `runtime` as the last stage and the `migrate` stage non-root; `docker-compose.yml` keeps `migrate` before `api` via `service_completed_successfully` and no workflow/infra file builds the `migrate` target.

## Tasks / Subtasks

Red first for every behaviour change; record runs in Debug Log References (Story 11.1-11.20 practice). Docker must be running for Infrastructure and Api tests (Testcontainers). One PR on `feature/11-21-self-host-migration-path`.

- [x] Task 0: Ask First decision (AC: #2). Decided 2026-11-01: Ralf chose option B; recorded in the header comment and AC #2/#3/#7. Copy decision, date and rejected options (A, C) into Completion Notes when the story starts.
- [x] Task 1: Baseline (AC: all)
  - [x] 1.1 `dotnet build EnergyTracker.sln -c Release --no-incremental -p:TreatWarningsAsErrors=true` (0 warnings), `dotnet format EnergyTracker.sln --verify-no-changes --no-restore`, `dotnet test` per project. Record counts (11.20 close: Application 605, Architecture 157, Infrastructure 565, Api 456).
  - [x] 1.2 Reproduce the bug first: with a filled `.env`, `docker compose down -v && docker compose up -d --build`, then `docker compose logs api` and `docker compose exec postgres psql -U energytracker -c '\dt'`. Record the observed failure (expected: no tables; `InstanceHeartbeatService` and Data Protection key access fail on `relation ... does not exist`). Do not code against an unverified premise (the deferred entry says "not verified end to end").
  - [x] 1.3 Spike the bundle before writing guards: in a scratch copy of the Dockerfile stage, confirm (a) `dotnet ef migrations bundle` builds from `backend-build` (it needs `.config/dotnet-tools.json`, which that stage does not copy today, and a restore of the Postgres migrations project), (b) the bundle reads `ConnectionStrings__Default` (the design-time `EnergyTrackerDbContextFactory` does) or needs `--connection`, (c) a self-contained single-file bundle runs as `$APP_UID` on `runtime-deps:10.0` (set `DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp` if it extracts), (d) `$APP_UID` is defined in that base image. Record findings; adjust Task 3 to what holds.
- [x] Task 2: Tests first (AC: #3, #7)
  - [x] 2.1 Extend `tests/EnergyTracker.Architecture.Tests/DockerImageMatchesTheVerifiedBuildTests.cs` (rules as a function over Dockerfile text, known-good and known-bad samples): `runtime` is the **last** stage; a `migrate` stage exists, is built from a stage that inherits the `global.json`/`Directory.Build.props` copy, runs as non-root (`USER $APP_UID`), and has an `ENTRYPOINT`; the `migrate` stage never ends the file. Known-bad: `migrate` last, `migrate` as root. Red first.
  - [x] 2.2 New `tests/EnergyTracker.Architecture.Tests/SelfHostMigrationIsOneShotServiceTests.cs`: `docker-compose.yml` has a `migrate` service with `build.target: migrate`, `restart: "no"`, `depends_on.postgres.condition: service_healthy`; `api.depends_on.migrate.condition` is `service_completed_successfully`. `app-deploy.yml`, `pr-review.yml` and `infra/**` never contain `target: migrate` or the service name as a build target (AC #3). Known-bad samples for each rule (api without the dependency, `restart: always`).
  - [x] 2.3 Same file: `scripts/self-host/migrate-entrypoint.sh` exists, is `COPY`ed by the Dockerfile `migrate` stage, propagates the bundle's exit code, and contains the pinned message fragments that `docs/self-hosting.md` quotes (assert the doc contains the same constant lines, so doc and script cannot drift).
- [x] Task 3: Production code (AC: #1, #2, #3)
  - [x] 3.1 `Dockerfile`: add `migrate-build` (from `backend-build`; `COPY .config ./.config`; `dotnet tool restore`; restore the Postgres migrations project; `dotnet ef migrations bundle --project src/EnergyTracker.Infrastructure.Migrations.Postgres --startup-project src/EnergyTracker.Infrastructure.Migrations.Postgres --context EnergyTrackerDbContext --self-contained -r linux-<arch> -o /app/efbundle`, `<arch>` from `ARG TARGETARCH`: `arm64` stays `arm64`, `amd64` becomes `x64`; fail the build on any other value) and `migrate` (`FROM mcr.microsoft.com/dotnet/runtime-deps:10.0`; copy bundle and entrypoint script; `USER $APP_UID`; `ENTRYPOINT`). Both stages go **before** `runtime`; `runtime` stays last so the default `docker build .` is unchanged. Keep the existing comments in the file; new comments say why in 1-3 lines.
  - [x] 3.2 `scripts/self-host/migrate-entrypoint.sh` (POSIX `sh`, `set -u`): run `/app/efbundle`, on non-zero print the pinned message to stderr (cause class: database unreachable, wrong `POSTGRES_PASSWORD` for an existing volume, or a failing migration; says `api` was not started; commands `docker compose logs migrate` and `docker compose up -d`; pointer `docs/self-hosting.md, 'Database migrations'`) and `exit` with the bundle's code. On success print one line with the outcome. Mode `0755` in git.
  - [x] 3.3 `docker-compose.yml`: add the `migrate` service (YAML in Dev Notes) with a 2-3 line comment (one-shot, why `api` waits, Postgres only); add `migrate: condition: service_completed_successfully` to `api.depends_on`. Keep the existing `postgres` and `aspire-dashboard` conditions and comments.
  - [x] 3.4 `docker-compose.sqlserver.yml` (local SQL Server override, AD-2): `depends_on` merges, so after 3.3 an override run also starts the Postgres `migrate` and still leaves the SQL Server database unmigrated. Pick and record one: leave as is and state in its header comment that SQL Server is migrated by hand with `./scripts/migrate.sh --provider sqlserver` (smallest; local-dev only, not a deployment path), or neutralise `migrate` there. Verify with `docker compose -f docker-compose.yml -f docker-compose.sqlserver.yml config`. `docker-compose.local.yml` only publishes the Postgres port and is used as `up postgres -d`; confirm it still does not start `migrate`.
  - [x] 3.5 No `Program.cs` change and no new application code: the API stays as is (never calls `Migrate`), `/health` stays DB-independent.
- [x] Task 4: Docs (AC: #1, #4, #5, #6)
  - [x] 4.1 `docs/self-hosting.md`: new section "Database migrations" after section 3: `docker compose up -d` runs the one-shot `migrate` service before `api`; first start creates the schema; every later `up` applies pending migrations (no-op when current); `docker compose restart api` does **not** migrate, use `docker compose up -d --build` after pulling a new release; run by hand with `docker compose run --rm migrate`; the failure message (quote the pinned text) and `docker compose logs migrate`; `docker compose ps -a` shows `migrate` as `Exited (0)` normally; the **backup-before-upgrade** commands (`pg_dump` to a file, restore with `psql`) since a self-host install has no automatic restore point. Section 3 now names four services (one exits). "No other software is required" stays true: the bundle is built inside the image.
  - [x] 4.2 Same file: upgrade note for AC #5 (Story 11.20 column drop, Postgres check query, what the operator loses, and that going back to an older image needs the pre-upgrade backup since `Down()` is not the rollback path).
  - [x] 4.3 `docs/local-development.md` line ~32 and Troubleshooting ~line 311: `docker compose up` migrates through `migrate`; `scripts/migrate.sh` remains for the host-run `dotnet run` flow (`run-api.sh`, Development) and for SQL Server. Fix the sentence "the API doesn't run them on startup" accordingly. `scripts/migrate.sh` header comment only if its role statement is now wrong.
  - [x] 4.4 `deferred-work.md`: delete the `[promoted: 11.21]` index row (~92) and body (~482), and the `[open]` entry at ~743 (AC #5). `docs/codebase-audit-2026-10-10.md` is not edited (dated record).
  - [x] 4.5 `_bmad-artifacts/project-context.md`, **Migrations** section: add 2-3 lines: self-host compose migrates through the `migrate` service (Dockerfile target, EF bundle, Postgres), Azure migrates in CI with the restore point; expand/contract binds both. Mention that a new Postgres provider-specific change needs no extra step because the bundle is built from the same migrations project.
- [x] Task 5: Verify and format (AC: all)
  - [x] 5.1 `dotnet format EnergyTracker.sln`, rerun the 1.1 gate, full `dotnet test` per project. Record counts and exact delta (new tests only; none removed).
  - [x] 5.2 `docker build .` (default target) still yields the API image and its size is unchanged within noise; `docker build --target migrate .` succeeds on both `--platform linux/amd64` and `--platform linux/arm64`. Record sizes.
- [ ] Task 6: End-to-end on the real stack (AC: #1, #2, #4; live gate)
  - [x] 6.1 Clean volume: `docker compose down -v`, `docker compose up -d --build`. Record `docker compose logs migrate` (applied migrations), `docker compose ps -a` (`migrate` `Exited (0)`), `SELECT COUNT(*) FROM "__EFMigrationsHistory";` equals the number of Postgres migrations, `curl -i http://localhost:8080/health` is `200`.
  - [x] 6.2 Idempotent: `docker compose up -d` again; `migrate` runs, applies nothing, exits 0, `api` is not recreated unnecessarily. Then `docker compose run --rm migrate` by hand, exit 0.
  - [x] 6.3 Behind schema (AC #4): on a throw-away volume bring the database to an earlier migration (`dotnet ef database update <earlier migration>` against the port published by `docker-compose.local.yml`, or restore a dump taken on an older commit), then `docker compose up -d --build`; confirm it reaches current and earlier rows survive.
  - [x] 6.4 Failure path (AC #2): set a wrong `POSTGRES_PASSWORD` in `.env` for an existing volume, `docker compose up -d`. Confirm the pinned message in `docker compose logs migrate`, `migrate` exit code non-zero, `api` not started (`docker compose ps -a`), and `docker compose up` itself reports the dependency failure. Record exit codes. Restore `.env` afterwards.
  - [ ] 6.5 Live sign-in on a clean volume (Chrome, test user from `.env`; `docs/local-development.md` "Live browser verification with the test user"): sign in, first household is created, one reading can be entered. This is the "usable database" proof and the gate for review to done. Use a fresh tab group per the Chrome rules in `project-context.md`.
  - [ ] 6.6 Open one PR against `main`. After merge there is no Azure step; confirm the `app-deploy.yml` run is green and its image is built from the default (`runtime`) target.

## Dev Notes

### Current state of the files being changed (read before editing)

- `docker-compose.yml`: three services. `api` builds `.` and `depends_on` postgres (healthy) and aspire-dashboard (started). **No migration step exists anywhere in it.** `api.environment` sets `Database__Provider: Postgres` and the connection string. Postgres volume is `postgres-data`. Header comment: this file is also the self-host reference deployment (AD-13).
- `Dockerfile`: three stages (`frontend-build`, `backend-build`, `runtime` on `aspnet:10.0`, user `$APP_UID`). The runtime image carries **no SDK and no `dotnet-ef`**, only the published API. This is why a script or compose one-shot cannot call `dotnet ef` in the image. `DockerImageMatchesTheVerifiedBuildTests` pins `COPY global.json Directory.Build.props` before restore and the non-root user; any Dockerfile edit (option B) must keep it green.
- `src/EnergyTracker.Api/Program.cs`: reads `Database:Provider` once (`Postgres` default or `SqlServer`), configures `UseNpgsql`/`UseSqlServer` with `MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres" | ".SqlServer")` and `MaxBatchSize(1000)`, registers `EnergyTrackerDbContext` plus a `HouseholdMembershipDbContext` factory, builds at line ~98 and runs at ~198. **It never calls `Migrate`/`MigrateAsync`.** After `Build()` the first startup actions are `DataProtectionKeyProtection.ApplyStartupActions` and `AiPlausibilityBackendConfiguration.LogSelection`.
- `scripts/migrate.sh`: host-side helper. Postgres default; builds `ConnectionStrings__Default` for `localhost` (needs the port published by `docker-compose.local.yml`, `run-api.sh` flow); runs `dotnet ef database update` against the migrations project. Requires the .NET SDK and `dotnet-ef` (`.config/dotnet-tools.json`, `dotnet-ef 10.0.13`, `rollForward: false`) on the host.
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/EnergyTrackerDbContextFactory.cs`: `IDesignTimeDbContextFactory`; reads `ConnectionStrings__Default`, `null!` household accessor (design time never queries). Relevant to option B (an EF bundle uses it).
- `docs/self-hosting.md`: ends the quick start at "3. Start the stack" (`docker compose up -d`; "starts two containers", `api` and `postgres`; it omits the `aspire-dashboard` container the compose file actually starts: fix the count while here) and "4. Verify it's running" (`/health` is DB-independent by design: "does not depend on the database"). It says "No other software is required" under Prerequisites. **It never mentions migrations.**
- `docs/local-development.md` line ~32 says `./scripts/migrate.sh` "applies EF Core migrations — the API doesn't do this on startup"; Troubleshooting ~311 repeats it.
- What must be preserved: `/health` stays DB-independent; the Azure CI migration with its restore point; AD-24 single instance; `Database:Provider` read once at the composition root; the DataProtection fail-closed startup checks (they must still run, after migration).

### Decision table (Ask First, Task 0; decided 2026-11-01: option B)

| | A. Documented script | B. One-shot compose service (chosen) | C. Startup migration (rejected) |
|---|---|---|---|
| Operator steps | clone, `.env`, **install .NET SDK + `dotnet tool restore`**, publish Postgres port, `./scripts/migrate.sh`, `docker compose up` | clone, `.env`, `docker compose up -d` | clone, `.env`, `docker compose up -d` |
| "No other software" claim | **broken** (needs SDK on the host; a NAS/Pi usually cannot) | kept | kept |
| Code change | docs only (plus publishing the port) | new Dockerfile target (SDK stage + EF migrations bundle or `dotnet ef`), new `migrate` service, `api` `depends_on: migrate: service_completed_successfully`, arm64 build for Pi (bundle is per-RID), image size/time grows, new guard in `DockerImageMatchesTheVerifiedBuildTests` | ~30 lines in `Composition/`, one setting, one compose line |
| Failure message | whatever `dotnet ef` prints | `docker compose up` stops on the failed `migrate` container; `api` never starts; log in `docker compose logs migrate`. Clean separate process exit code | our own `Critical` log from the api container; exit code depends on the 11.23 hazard (Task 3.4) |
| Behind-schema upgrade | operator must remember to run it | automatic on `up` | automatic on `up` |
| Azure risk | none | none (service not in Bicep) | none if flag stays off; guard test pins it |
| Fits the `api` image rule (runtime has no SDK) | n/a | needs a second image target | yes |

Create-story recommendation was **C** (smallest change, no second image target); Ralf chose **B** on 2026-11-01, which isolates migration failure in its own process and exit code. A was rejected: it contradicts the guide's "no other software" prerequisite.

Chosen shape (option B), reference YAML:

```yaml
services:
  migrate:
    build:
      context: .
      target: migrate
    environment:
      Database__Provider: Postgres
      ConnectionStrings__Default: "Host=postgres;Database=${POSTGRES_DB:-energytracker};Username=${POSTGRES_USER:-energytracker};Password=${POSTGRES_PASSWORD}"
    depends_on:
      postgres:
        condition: service_healthy
    restart: "no"

  api:
    depends_on:
      migrate:
        condition: service_completed_successfully
      postgres:
        condition: service_healthy
      aspire-dashboard:
        condition: service_started
```

Compose has no init-container concept; `service_completed_successfully` is the equivalent. `docker compose restart api` and `docker compose up -d api` behave differently: `up` starts dependencies (runs `migrate`), `restart` does not. The docs must say so.

Note on rollback: none of the options gives a self-hoster an automatic restore point. The doc's backup-first command is the equivalent (AC #4). Do not add `Down()`-based rollback.

### Architecture compliance

- **AD-13:** one Dockerfile, compose file is the self-host reference deployment. The `migrate` target is a build stage of the same Dockerfile, not a second repository or edition; the API image (`runtime`) is unchanged.
- **AD-2:** the bundle is built from the Postgres migrations project only (the self-host provider); SQL Server stays a local-dev variant migrated by `scripts/migrate.sh --provider sqlserver`. No new migration, so `scripts/add-migration.sh` is not used.
- **AD-24:** one instance per database. The EF bundle takes the provider migration lock; the `migrate` service does not make several instances supported, and the docs say so.
- **Expand/contract (project-context.md, Migrations):** still binds. Self-host migrates with the new image's bundle **before** `api` starts, so there is no old-image-on-new-schema window except a manual rollback to an older image; the docs say that rollback needs the backup (AC #4).
- **Dockerfile guards (Story 11.11):** `DockerImageMatchesTheVerifiedBuildTests` today expects the runtime stage as the last stage with `USER $APP_UID` before `ENTRYPOINT`. Task 2.1 extends it; do not weaken the existing rules to fit the new stages.
- **Comment rule (Story 11.19)** and **analyzer gate (Story 11.18):** build with `-p:TreatWarningsAsErrors=true`, run `dotnet format EnergyTracker.sln` before pushing.

### Testing standards

- xUnit v3 + Shouldly + NSubstitute. DB tests take `PostgresTestDatabase` / `SqlServerTestDatabase` (`tests/Shared/SharedDatabaseServers.cs`), one shared container per assembly; never `new PostgreSqlBuilder(...)` (`TestsShareDatabaseServersTests` enforces). Use `TestContext.Current.CancellationToken`.
- No new Testcontainers test: B adds no application code. The proof of behaviour is Task 6 on the real stack; Task 2 guards pin the shape that makes it work.
- Test names `Snake_case` (`CA1707` off for `tests/**`). Logging assertions use a captured logger, not console output.
- Text-scan guards use the known-good/known-bad pattern (`DockerImageMatchesTheVerifiedBuildTests`).

### Previous story intelligence

- **11.20** (latest, done): dropped `HouseholdInvites.Token`; review deferred a "no in-DB guard against live legacy invites" item that this story carries as docs (AC #5). Baseline counts: Application 605, Architecture 157, Infrastructure 565, Api 456. Its migration comment says rollback is the restore point, not `Down()`.
- **10.1** (migration safety on deploy): created the Azure restore-point block in `app-deploy.yml` and is where this gap was found. Azure's answer to "who applies migrations" is CI; the self-host answer is this story.
- **11.11** (container config correctness): fixed the runtime image to run non-root and pinned the build to CI's verified inputs. Any image change here must respect those guards.
- **11.23** (failed startup must exit, next in epic): not in play here. B's failure is a separate `migrate` process whose exit code is the bundle's, so the `Program.cs` throw/100% CPU hazard does not apply. Leave 11.23 its scope.

### Git intelligence

Last commits: `4e1c596` docs close of 11.20, `3a9c5ae` Story 11.20 (PR #160), `f55a539`/`ad78c69` Dependabot bumps in `/web`, `f213b6c` promotion of deferred items into Epic 11/12. Conventional commits with story id in the subject, for example `feat: ... (Story 11.20) (#160)`. Working tree clean on `feature/11-21-self-host-migration-path`.

### Latest technical notes

- No new package. EF Core 10.0.x, Npgsql provider as pinned in `Directory.Packages.props`; `dotnet-ef` tool 10.0.13.
- EF Core `Database.MigrateAsync()` acquires a migration lock on Postgres (advisory lock) and SQL Server (application lock), so concurrent starts serialize. Do not add your own lock.
- Bundle: `dotnet ef migrations bundle --self-contained -r linux-x64|linux-arm64` produces a per-architecture executable and takes the connection string through `--connection` or the design-time factory's `ConnectionStrings__Default` (verify in Task 1.3). Build it per `TARGETARCH` (`amd64` maps to `x64`); never hard-code amd64 (Raspberry Pi is in the supported hardware list). `TARGETARCH` needs BuildKit, the default in current Docker. `dotnet tool restore` and the bundle build need network at image build time, as the existing `npm ci`/`dotnet restore` do.
- `pr-review.yml` does not build the Docker image today, so the `migrate` target is built by no CI job: Task 2 text guards plus Task 5.2/6 are its protection. Adding a non-pushing `migrate` build to CI is out of scope; note it in Completion Notes if the dev sees it as cheap.

### Out of scope

A general "failed startup must exit" fix (Story 11.23); migrating on API startup (rejected option C); a documented-script-only path (rejected option A); a SQL Server `migrate` service; migration timeout headroom on Azure (Story 11.24); an automatic self-host restore point or backup job; making several instances supported (AD-24); SQL Server as a self-host database (documented only as a local-dev variant); moving the invite token out of the URL (Story 11.22).

### Project Structure Notes

All paths above exist at `4e1c596`. Expect: `Dockerfile` (two new stages before `runtime`), new `scripts/self-host/migrate-entrypoint.sh`, `docker-compose.yml`, `docker-compose.sqlserver.yml` (header note or override), `docs/self-hosting.md`, `docs/local-development.md`, `_bmad-artifacts/project-context.md`, `_bmad-artifacts/implementation/deferred-work.md`; tests only under `tests/EnergyTracker.Architecture.Tests/`. No change under `src/`.

### References

- [Source: _bmad-artifacts/planning/epics/epic-11-codebase-audit-hardening.md#Story 11.21]
- [Source: _bmad-artifacts/implementation/deferred-work.md lines ~92, ~482 (promoted entry), ~743 (11.20 review follow-up)]
- [Source: _bmad-artifacts/project-context.md#Migrations (expand/contract, add-migration.sh, restore-point rollback); "Amend ACs at decision time"]
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-13]
- [Source: _bmad-artifacts/implementation/11-20-drop-plaintext-invite-token-column.md] (latest migration, baseline counts)
- [Source: docker-compose.yml, Dockerfile, scripts/migrate.sh, src/EnergyTracker.Api/Program.cs, docs/self-hosting.md, docs/local-development.md]

## Dev Agent Record

### Agent Model Used

Sonnet 5.5 (claude-sonnet-5-5)

### Debug Log References

- 1.2 Repro (isolated compose project `et1121repro`, dev volume untouched): `docker compose up -d --build` on clean volume; `docker compose logs api` shows `42P01: relation "DataProtectionKeys" does not exist`; `\dt` returns `Did not find any tables.`. Bug confirmed.
- 1.3 Spike: (a) `dotnet tool restore` + `dotnet ef migrations bundle` builds from `backend-build` with `.config` copied; (b) bundle reads `ConnectionStrings__Default`; (c) bundle runs as `$APP_UID` (1654) on `runtime-deps:10.0` with no `DOTNET_BUNDLE_EXTRACT_BASE_DIR`; (d) `APP_UID=1654` defined in that image. Wrong password gave exit 1.
- Red: Architecture tests, 5 real-file guards failed before production code (Dockerfile x2, compose, entrypoint x2 incl. pinned message); known-bad sample tests passed.
- Green: Architecture 183/183.
- 6.1 Clean volume (`et1121e2e`): `migrate` `Exited (0)`, `__EFMigrationsHistory` = 34 (34 Designer files in Postgres migrations project), `/health` 200.
- 6.2 `up -d` again: "No migrations were applied. The database is already up to date.", exit 0; `docker compose run --rm migrate` exit 0.
- 6.3 Behind schema: `docker compose run --rm --entrypoint /app/efbundle migrate 20261024110142_AddAppInstances` -> 32 migrations, `Token` column back; `docker compose up -d --build` -> 34 migrations, `Token` column gone, `DataProtectionKeys` row (1) survived.
- 6.4 Failure: `POSTGRES_PASSWORD=wrong-pw docker compose up -d` -> `28P01` plus the 4 pinned lines and `migrate: exit code 1`; `docker compose up` exit 1 with `service "migrate" didn't complete successfully: exit 1`; `api` state `Created` (never started); container exit code 1.
- 5.1 `dotnet build ... -p:TreatWarningsAsErrors=true`: 0 warnings, 0 errors; `dotnet format --verify-no-changes` clean. Tests: Application 605, Architecture 183 (157 + 26 new, none removed), Infrastructure 565, Api 456; 0 failed.
- 5.2 `docker build .` default image = API (has `EnergyTracker.Api.dll`, no `efbundle`), 367 MB, same as pre-change; `--target migrate` builds on linux/amd64 (315 MB) and linux/arm64 (311 MB).

### Completion Notes List

- Task 0 decision: Ralf chose option B on 2026-11-01 (one-shot compose `migrate` service from a `migrate` Dockerfile target running an EF migrations bundle). Rejected: A (documented script; needs .NET SDK on host, breaks "no other software"), C (startup migration; create-story recommendation, rejected to isolate migration failure in its own process and exit code).
- Dockerfile gained `migrate-build` and `migrate` stages before `runtime`; `runtime` stays last. Bundle per `TARGETARCH` (amd64->x64, arm64; other values fail the build).
- Entrypoint script prints a 4-line pinned failure message plus the exit code; `docs/self-hosting.md` quotes the 4 lines; a test pins script and doc together.
- 3.4: `docker-compose.sqlserver.yml` left as is, header comment states SQL Server is migrated by hand with `./scripts/migrate.sh --provider sqlserver`. `docker-compose.local.yml` unchanged (used as `up postgres -d`, does not start `migrate`).
- Docs: new "Database migrations" section (backup/restore, failure message, invite-token note with check query), section 3 lists four services, `docs/local-development.md` and `project-context.md` updated. `scripts/migrate.sh` header still accurate, unchanged.
- deferred-work.md: removed the `[promoted: 11.21]` index row and body, and the 11.20 `[open]` invite-guard entry.
- Not done, blocks review->done: 6.5 live first-household sign-in (Claude-in-Chrome extension not connected; also Auth0 password is not typed by the agent) and 6.6 PR/CI. A clean-volume stack is running as compose project `et1121live` on :8080 for that check.
- Cheap CI idea (not done, out of scope): a non-pushing `docker build --target migrate` job in `pr-review.yml`.

### File List

- Dockerfile
- docker-compose.yml
- docker-compose.sqlserver.yml
- scripts/self-host/migrate-entrypoint.sh (new, 0755)
- docs/self-hosting.md
- docs/local-development.md
- _bmad-artifacts/project-context.md
- _bmad-artifacts/implementation/deferred-work.md
- _bmad-artifacts/implementation/sprint-status.yaml
- _bmad-artifacts/implementation/11-21-self-host-migration-path.md
- tests/EnergyTracker.Architecture.Tests/DockerImageMatchesTheVerifiedBuildTests.cs
- tests/EnergyTracker.Architecture.Tests/SelfHostMigrationIsOneShotServiceTests.cs (new)

### Change Log

- 2026-11-01: Story 11.21 implemented (option B): `migrate` service and Dockerfile target, entrypoint script, docs, guards. Live sign-in (6.5) and PR (6.6) pending.

### Review Findings

Code review 2026-11-01 (Blind Hunter, Edge Case Hunter, Acceptance Auditor). 0 decision-needed, 6 patch, 5 defer, 12 dismissed.

- [x] [Review][Patch] Git index holds an EMPTY `scripts/self-host/migrate-entrypoint.sh` (staged blob `e69de29`, 0 bytes; worktree file is 957 bytes). A plain `git commit` ships an empty entrypoint and `migrate` never runs. Fix: `git add scripts/self-host/migrate-entrypoint.sh`, then confirm `git show :scripts/self-host/migrate-entrypoint.sh | wc -c` is 957. [scripts/self-host/migrate-entrypoint.sh]
- [x] [Review][Patch] No `.gitattributes`: a Windows checkout with `core.autocrlf=true` makes the shebang `#!/bin/sh\r`, so the container fails with "no such file or directory" and `api` never starts. Add `scripts/self-host/*.sh text eol=lf` and assert the script has no `\r` in the guard test. [scripts/self-host/migrate-entrypoint.sh:1, tests/EnergyTracker.Architecture.Tests/SelfHostMigrationIsOneShotServiceTests.cs]
- [x] [Review][Patch] `sh` is PID 1 and the bundle runs as a child, so SIGTERM on `docker compose stop` or Ctrl-C is not forwarded. After the 10s timeout SIGKILL can interrupt a migration mid-run. Run the bundle in the background, `trap` TERM/INT to forward, then `wait`, keeping the exit code. [scripts/self-host/migrate-entrypoint.sh:7]
- [x] [Review][Patch] AC #7: the entrypoint guards have no known-bad sample. Nothing proves the exit-code regex rejects a script ending in `exit 0` or with no exit line. Also assert `status=$?` directly follows `/app/efbundle` and that `set -e` is absent. [tests/EnergyTracker.Architecture.Tests/SelfHostMigrationIsOneShotServiceTests.cs]
- [x] [Review][Patch] Restore command in the guide runs `psql ... < backup.sql` without `-v ON_ERROR_STOP=1`. A partial restore continues past errors and leaves a half-restored schema with no signal. Add the flag (optionally `--single-transaction`). [docs/self-hosting.md "Back up before you upgrade"]
- [x] [Review][Patch] Guard robustness: `No_workflow_or_infra_file_builds_the_migrate_target` throws if `infra` or `.github/workflows` is missing and skips `.yaml`; `BuildsTheMigrateTarget` misses quoted `target: "migrate"` and trailing comments. `Stages()` silently overwrites duplicate `AS` names and relies on `Dictionary` order for "last stage" (use an ordered list and report duplicates). `CheckMigrateBuildStage` does not assert `--self-contained` and `-r "$RID"`. [tests/EnergyTracker.Architecture.Tests/SelfHostMigrationIsOneShotServiceTests.cs, tests/EnergyTracker.Architecture.Tests/DockerImageMatchesTheVerifiedBuildTests.cs]
- [x] [Review][Defer] Empty or special-character `POSTGRES_PASSWORD` (`;`, `=`) breaks the unquoted connection string with a vague auth error; no `${POSTGRES_PASSWORD:?...}` guard. [docker-compose.yml:15, docker-compose.yml:50] — deferred, pre-existing: `api` uses the identical pattern.
- [x] [Review][Defer] On upgrade, `docker compose up -d --build` runs `migrate` while the old `api` container still serves, so a destructive migration (for example the `Token` column drop) hits a live old-version API. [docker-compose.yml] — deferred, inherent to option B as chosen by Ralf; needs a stop-then-migrate upgrade note.
- [x] [Review][Defer] Failure line 1 says "the api container was not started" even for a manual `docker compose run --rm migrate` while `api` is running. [scripts/self-host/migrate-entrypoint.sh:12] — deferred, wording is pinned by the amended AC #2.
- [x] [Review][Defer] The `ConnectionStrings__Default` string is duplicated between `api` and `migrate`; drift risk. Use a compose extension field. [docker-compose.yml:15, docker-compose.yml:50] — deferred, cosmetic.
- [x] [Review][Defer] No CI job builds or runs the `migrate` target; a regression is caught only by the Task 6 manual run. [Dockerfile] — deferred, spec accepts Task 6 as the proof (no new Testcontainers test).

Not a finding: Task 6.5 (live first-household sign-in) and 6.6 (PR and CI) are still open. They block review to done, as the Completion Notes already state.
