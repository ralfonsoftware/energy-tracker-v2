---
baseline_commit: b503218
---

# Story 10.1: Migration Safety on Deploy — Restore Point, Rollback Runbook, Expand/Contract Rule

Status: in-progress

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

**Origin:** promoted from `deferred-work.md` ("Deferred from: code review of spec-azure-sql-ci-migration-firewall (2026-08-13)" → "The CI migration step runs `dotnet ef database update` on every deploy with no pre-migration backup/snapshot and no expand/contract discipline documented — a bad migration commits directly against production with no rollback story"). Promoted by Ralf in the 2026-10-02 triage (`spec-deferred-work-triage.md`, decision 2). Real household data is live in production, so the "worth a dedicated migration-safety pass once the app has real user data at stake" trigger written into that entry has fired. Epic 10 ("Deferred-work hardening") is a new epic created for the three promoted items; this is its first story.

## Story

As Ralf (Project Lead and sole operator),
I want every production deploy that runs EF Core migrations to leave a recorded, verified restore point and a rehearsed rollback procedure behind it, together with a written rule for what a migration may do,
so that a bad migration can be undone in minutes with a known data-loss window instead of by improvisation against live household data.

## Acceptance Criteria

1. **Given** a push to `main` (or `workflow_dispatch`) that reaches the "Apply pending EF Core migrations" step of `app-deploy.yml`, **when** that step starts and **before** its first `dotnet ef database update` attempt, **then** the job writes a "Pre-migration restore point" block to `$GITHUB_STEP_SUMMARY` containing: SQL server name, database name, the UTC timestamp captured at that moment (ISO 8601 `Z`), the list of pending migrations (or "none pending"), the database's `earliestRestoreDate`, and the exact `az sql db restore` command (with the timestamp substituted) that would recreate the pre-migration state as a new database. The timestamp is captured **once** (not per retry attempt).
2. **Given** the optional lookups in AC #1 (pending list via `dotnet ef migrations list`, `earliestRestoreDate` via `az sql db show`) fail or return empty (e.g. firewall rule still propagating), **when** the step runs, **then** the block says "unavailable" for that field plus a `::warning::`, and the deploy proceeds exactly as today — only the **timestamp, server and database name** are mandatory. A failure to *write the summary itself* must not fail the deploy either.
3. **Given** the existing migration behavior, **when** this story is done, **then** it is unchanged: migrations still run before "Deploy new revision"; the run-scoped firewall rule is still created before and removed (`if: always()`, `continue-on-error`) after; the 5-attempt/30s retry loop, `Command Timeout=600`, `timeout-minutes: 20` and the `Authentication=Active Directory Default` connection string (AD-21) are byte-for-byte as they are now; the health-check image rollback is untouched; no secret, token or connection string is echoed to logs or the summary.
4. **Given** a future edit to `app-deploy.yml`, **when** the .NET test suite runs, **then** a guard test fails if (a) the restore-point block is removed or no longer writes to `GITHUB_STEP_SUMMARY`, (b) it appears *after* `dotnet ef database update` inside the migration step, or (c) the "Apply pending EF Core migrations" step no longer precedes "Deploy new revision".
5. **Given** a bad migration reached production, **when** Ralf follows the new runbook in `infra/README.md` ("Rolling back a bad migration"), **then** it covers, in order: decide forward-fix vs restore; restore the marker timestamp to a **new** database with `az sql db restore`; verify it (connect as the CI identity, `dotnet ef migrations list` shows the pre-migration set); swap names with `az sql db rename` (old → `energytracker-bad-<runid>`, restored → `energytracker`); verify `/health` and a real page; the **data-loss window** (everything written between the marker and the swap is lost — the old revision keeps serving writes while migrations run); cleanup (delete the bad and any scratch databases; Basic databases bill hourly); and why `Down()` migrations are not the rollback mechanism.
6. **Given** the runbook, **when** this story is verified, **then** it has been **rehearsed live against Azure** (see Task 6): a PITR restore of the real production database into a scratch database, connectivity verified as the CI identity, a rename round-trip performed on scratch databases only, and the scratch databases deleted — with the observed restore duration, the exact commands that worked, and any deviation from the runbook text recorded in Completion Notes and folded back into the runbook.
7. **Given** a migration author, **when** they read `project-context.md` (Migrations rules, auto-loaded for dev-story and code-review), **then** the **expand/contract rule** is there in one short block: a migration must be safe for the **previous** application image to run against, because the health-check rollback redeploys the previous image against an already-migrated schema and a failed multi-migration run can leave the schema partway through; destructive changes (drop/rename column or table, narrowing `AlterColumn`, new NOT NULL without default) ship in a **later** deploy than the code that stops using the old shape.
8. **Given** the above is in place, **when** the first real deploy after merge runs, **then** its Actions run shows the restore-point block with a real timestamp and `earliestRestoreDate` (live-verification gate, Task 7).
9. **And** (only if Ralf approves Open Question 3) the PITR retention is declared explicitly in `infra/modules/database-sqlserver.bicep` as a `backupShortTermRetentionPolicies` child resource with `retentionDays: 7`, the PR's `what-if` is clean, and a live `infra-deploy.yml` run leaves the database at 7 days. If Ralf declines, this AC is dropped and the story states that retention rests on the Azure default.
10. **And** out of scope and unchanged: the Postgres/self-host path, the Container App, the `infra-deploy.yml` workflow, any migration file, any application code, and any `Down()` method.

## Tasks / Subtasks

- [x] **Task 1: Kickoff checks (mandatory, before any edit)**
  - [x] Re-read the whole `Apply pending EF Core migrations` step (`.github/workflows/app-deploy.yml:164-204`) and the firewall steps around it; confirm the line numbers in this story still match (the file is edited often).
  - [x] `grep -rn suppressTransaction src/` — at story-writing time there were **zero** hits, so every migration runs in a single EF transaction; note the result in Completion Notes (it determines whether a *single* migration can ever be half-applied; it can still stop *between* migrations).
  - [x] Confirm with Ralf which of the Open Questions below are decided before starting Task 2; do not guess Q1 or Q3.

- [x] **Task 2: Guard test first, red (AC #4)** — new `tests/EnergyTracker.Architecture.Tests/MigrationDeployWorkflowRestorePointGuardTests.cs`
  - [x] Copy the repo-root locator idiom from `EveHomeXlsxParserUsesZeroOffsetForUtcNormalizationConventionTests.FindRepoRoot()` (walk up from `AppContext.BaseDirectory` to `EnergyTracker.sln`); read `.github/workflows/app-deploy.yml` as text; split into steps on lines matching `^\s*- name:`. No YAML library (none is referenced; Architecture.Tests deliberately stays dependency-light like its siblings).
  - [x] Strip whole-line `#` comments before searching (same precedent as the sibling guard tests) so the explanatory comments in the workflow cannot satisfy or break an assertion.
  - [x] Assert: step `Apply pending EF Core migrations` exists; inside it the index of `GITHUB_STEP_SUMMARY` is < the index of `dotnet ef database update`; the step `Deploy new revision` index is greater than the migration step's. Failure messages name the step and the expectation.
  - [x] Run it: it must fail for the right reason (summary block missing) before Task 3. Also mutate once after green (move the block below the `dotnet ef` call) and confirm it fails; record in Completion Notes.

- [x] **Task 3: Restore-point block in the migration step, green (AC #1-#3)** — `.github/workflows/app-deploy.yml`
  - [x] Inside the existing step (do **not** add a second connection-string construction), after `CONNECTION_STRING` is built and **before** `for attempt in …`:
    - `RESTORE_POINT_UTC=$(date -u +%Y-%m-%dT%H:%M:%SZ)`
    - `EARLIEST=$(az sql db show --resource-group … --server … --name energytracker --query earliestRestoreDate -o tsv || true)`
    - `PENDING=$(dotnet ef migrations list … --connection "$CONNECTION_STRING" 2>/dev/null | grep -F '(Pending)' || true)` (EF prints pending migrations with a `(Pending)` suffix — **verify the exact format locally against a fresh Postgres/SqlServer container before relying on it**; if the output format differs, adjust the grep and the test's expectations, not the AC).
    - Write the block with `{ … } >> "$GITHUB_STEP_SUMMARY" || echo "::warning::could not write restore-point summary"`.
  - [x] The database name is the literal already present in the connection string (`energytracker`); the server name is `${{ steps.sqlserver.outputs.name }}`. Do not introduce a new variable for either.
  - [x] Emit `::warning::` for each unavailable optional field; never `exit 1` for them (AC #2).
  - [x] Add a comment above the block explaining *why* (restore marker for PITR; link to the infra/README runbook section), in this file's existing comment style, and mention it in the step's header comment.
  - [x] Re-run the Task 2 test: green. `actionlint` is **not** installed locally; do a manual YAML review (indentation inside the `run: |` block, `${{ }}` expressions only where the existing step already uses them) and state in Completion Notes that actionlint was not available. Check that `$GITHUB_STEP_SUMMARY` markdown renders (fenced code for the restore command).
  - [x] Confirm by diff that nothing else in the file changed (AC #3).

- [ ] **Task 4 (conditional on Open Question 3): explicit PITR retention in Bicep (AC #9)** — `infra/modules/database-sqlserver.bicep`
  - [x] Add `resource shortTermRetention 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2025-01-01' = { parent: sqlDatabase, name: 'default', properties: { retentionDays: 7 } }`. Do **not** set `diffBackupIntervalInHours` (DTU default is 24h; not this story's concern). Verify the resource type/API version against the Bicep reference before writing; the module already uses `@2025-01-01` for sibling resources.
  - [x] No new parameter unless Ralf wants it tunable; 7 is the Basic-tier maximum (Azure docs: Basic is configurable 1–7 days), so a larger value would fail.
  - [ ] `pr-review.yml`'s what-if runs automatically on the PR (infra changed); it must show only the new child resource. Merging triggers `infra-deploy.yml` (path-filtered on `infra/**`) — note this redeploy also exercises the "preserve running image" logic; watch that the Container App image is not reset (`infra/README.md`, "infra-deploy.yml preserves the currently-running Container App image").

- [x] **Task 5: Runbook + expand/contract rule (AC #5, #7)** — docs only
  - [x] `infra/README.md`: new section "Rolling back a bad migration (Azure SQL point-in-time restore)" next to the Entra-only auth runbook (same file, same tone: numbered steps, exact `az` commands with `<placeholders>`, "why" paragraph). Required content is AC #5. Commands to include (verify flags with `az sql db restore --help` / `az sql db rename --help` while writing; they are the documented CLI surface at story-writing time, not guaranteed identical):
    - restore: `az sql db restore --resource-group <rg> --server <server> --name energytracker --dest-name energytracker-restore-<runid> --time <marker-minus-1min> --service-objective Basic` (restore **to a new database on the same server**; Azure cannot overwrite an existing database; the restored DB is billed at normal rates until deleted)
    - swap: `az sql db rename … --name energytracker --new-name energytracker-bad-<runid>` then `… --name energytracker-restore-<runid> --new-name energytracker`
    - Use the marker **minus ~1 minute** to absorb runner/Azure clock skew; state that the earliest/latest restorable point is Azure-managed and the drill (Task 6) is what confirms the real granularity.
    - Contained Entra users (`infra/sql/grant-entra-db-users.sql`) live inside the database; the drill must **confirm** they survive the restore (do not assert it in the doc until observed). If they do not, the runbook gets a "re-run grant-entra-db-users.sql" step.
    - The Container App's connection string names the database `energytracker`, which is why the swap uses rename instead of repointing the app.
    - Why not `dotnet ef database update <previous>`: `Down()` methods are scaffolded, never rehearsed, and cannot restore data a destructive `Up()` dropped.
    - Expected downtime during the two renames, and that the Container App's EF connections must reconnect (scale-to-zero may make this invisible; observe in the drill).
  - [x] `_bmad-artifacts/project-context.md`: extend the **Migrations** block (currently one bullet, line ~137) with the expand/contract rule from AC #7 and a one-line pointer to the runbook and to the step-summary restore marker. Keep it to a short block — this file is auto-loaded into every dev-story/code-review run.
  - [x] Cross-link: one line in `docs/local-vs-azure-deltas.md` is **not** needed (this is Azure-only operations, not a local/Azure structural delta); do not add one.

- [ ] **Task 6: Live rehearsal of the restore (AC #6)** — human-run or human-supervised; **Ralf's Azure login needed, the dev agent has no `az` session**
  - [ ] Using the runbook text verbatim, restore production `energytracker` at a timestamp ~10+ minutes in the past into `energytracker-drill-<yyyymmdd>`; record start/finish time (restore duration is the headline number for the data-loss/downtime discussion).
  - [ ] Verify connect as the CI identity (`dotnet ef migrations list --connection "…Database=energytracker-drill-…;Authentication=Active Directory Default…"`) — the run needs the temporary firewall rule pattern from `app-deploy.yml`; remove it afterwards.
  - [ ] Verify the Container App identity's access if feasible (or record that contained-user presence was checked via `SELECT name FROM sys.database_principals WHERE type = 'E' OR type = 'X'` instead).
  - [ ] Rename round-trip on **scratch** databases only (never production): drill DB → `…-b` → back; record elapsed time.
  - [ ] Delete the drill databases and any firewall rule; confirm with `az sql db list`. Basic is hourly-billed; leaving a drill DB running is the only cost risk in this story.
  - [ ] Fold every deviation (flag names, timing, user survival) back into the runbook text. Record the evidence (commands + outputs, secrets redacted) in Completion Notes.

- [ ] **Task 7: Live verification of the deploy step (AC #8) — gate, same standard as the Auth0/Chrome gate in `project-context.md`**
  - [ ] After merge, observe the first `App Deploy` run: open the run's Summary tab and confirm the restore-point block (timestamp, server, DB, pending/none, `earliestRestoreDate`, restore command) renders correctly. A unit/guard test alone does not satisfy this; if it cannot be observed (no push happens), trigger `workflow_dispatch` on `main`.
  - [ ] Confirm the remaining steps (firewall removal, ACR login, deploy, health check) behaved exactly as before, and that no secret appears in the log.
  - [ ] The story stays in `review` until this is observed; do not mark Task 7 complete from the guard test.

- [ ] **Task 8: Housekeeping**
  - [ ] Mark the `deferred-work.md` entry resolved per the new convention (`[resolved: 10-1-migration-safety-on-deploy]` → move per `project-context.md`'s deferred-work rules: resolved entries are deleted, with the story referenced in the commit). Also update the *promoted* index line in `deferred-work.md`.
  - [ ] Update `sprint-status.yaml` (`10-1-migration-safety-on-deploy`) and the File List below.

## Dev Notes

### Scope Reality Check (stale or imprecise premises in the source entry)

- **"No pre-migration backup/snapshot" is not literally true.** Azure SQL takes automatic backups for every database and supports point-in-time restore with **7 days of retention by default; Basic-tier databases are configurable 1–7 days** (Microsoft Learn, "Automated backups in Azure SQL Database"). Nothing in `infra/` configures or mentions backups (grep of `infra/`, `docs/`, `project-context.md` finds none), so the safety net exists **by default, undeclared, undocumented, and never rehearsed**. This story makes it a first-class, verified part of the deploy rather than adding a second backup system.
- **PITR cannot overwrite the live database.** Restore always creates a *new* database on the same server (Learn: "You can't overwrite an existing database during restore"); replacing production means restore → rename old → rename new. Hence the runbook is a swap, and the database name `energytracker` baked into the Container App's connection string stays valid.
- **The old app keeps serving writes during the migration** (migrate runs before "Deploy new revision"; the old revision is live throughout). A restore therefore **loses every write made between the marker and the swap**. This is the real cost of the rollback and is why the runbook leads with "forward-fix vs restore", not "restore".
- **"A bad migration commits directly" is only partly right.** No migration uses `suppressTransaction`, so each migration is one EF transaction and a failing migration rolls itself back. What *can* persist is an **intermediate schema**: migrations apply one at a time, so a run of N pending migrations that fails on #k leaves 1..k-1 applied while the old image still serves — and the deploy job fails before the new image ships. Expand/contract protects exactly this window and the health-check-rollback window (below).
- **The health-check rollback is image-only** (`app-deploy.yml:332-340` redeploys `steps.pre.outputs.image`). If a new image passes migrations but fails `/health`, the *previous* image is redeployed against the *already-migrated* schema. That is the second reason for the expand/contract rule and the one most likely to bite.
- **Auth is Entra-only now (AD-21), not SQL-password.** The deploy step authenticates with `Authentication=Active Directory Default` as `energy-tracker-devops-uami`, which holds `db_datareader`/`db_datawriter`/`db_ddladmin` inside the database (`infra/sql/grant-entra-db-users.sql`) and **Owner on the resource group** (`infra/README.md`, one-time identity bootstrap). Consequence: the CI identity already has the ARM rights for `az sql db show/restore/rename/copy`; no RBAC change is needed. `DATABASE_ADMIN_PASSWORD` remains break-glass only and must not be used by anything added here.
- **Self-host/Postgres is out of scope (AC #10).** Separately, while grounding this story, a grep for migration handling in `docker-compose.yml`, `Dockerfile` and `docs/self-hosting.md` found **none**, and the API never calls `Database.Migrate()` (only tests do). That suggests the documented self-host path has no step that creates the schema. **Unverified beyond grep — not in this story.** See Open Question 5.
- **No migration lint exists** (`PendingModelChanges`/destructive-operation checks: grep finds none). Every existing migration `.cs` matches `Drop*`/`AlterColumn` because EF scaffolds them into `Down()`, so a naive "no Drop in migrations" scan is useless; any enforcement must parse `Up()` only. That is why enforcement is an Open Question, not a task.

### What the dev must preserve (current state of each file touched)

| File | UPDATE/NEW | Current state | This story changes | Must not break |
|---|---|---|---|---|
| `.github/workflows/app-deploy.yml` | UPDATE | `Apply pending EF Core migrations` (L164-204): `dotnet tool restore`; resolves `SQL_FQDN`; builds `CONNECTION_STRING` (Entra `Active Directory Default`, `Command Timeout=600`); 5-attempt loop with 30s sleep; `timeout-minutes: 20`. Preceded by run-scoped firewall create, followed by `if: always()` delete. Concurrency group `infra-app-deploy` shared with `infra-deploy.yml`. | Insert the restore-point block between `CONNECTION_STRING=` and the loop. | Order, retry loop, timeout, connection string, firewall create/delete, health-check rollback, concurrency group, `permissions`. |
| `tests/EnergyTracker.Architecture.Tests/MigrationDeployWorkflowRestorePointGuardTests.cs` | NEW | — | Text-scan guard (AC #4). | Project stays dependency-light; no YAML package; `FindRepoRoot` idiom copied, not shared (siblings copy it too). |
| `infra/README.md` | UPDATE | Holds the Entra-only cutover runbook and the "preserves running image" section; Azure ops runbooks live here. | New rollback runbook section. | Existing sections and their anchors (`docs/local-vs-azure-deltas.md` links into them). |
| `_bmad-artifacts/project-context.md` | UPDATE | `**Migrations:**` block = one bullet (`add-migration.sh`). | Add expand/contract + restore-point pointer. | Keep the file's size discipline; it is an auto-loaded persistent fact. |
| `infra/modules/database-sqlserver.bicep` | UPDATE (Task 4 only) | `sqlDatabase` resource has `sku` and `maxSizeBytes` only; no backup policy. | Optional child resource. | The Entra/administrators `union(...)` logic and `azureADOnlyAuthentication` ordering (AD-21 Deploy A/B warnings); output `connectionString`. |
| `deferred-work.md`, `sprint-status.yaml` | UPDATE | Entry marked `[promoted: needs story]`. | Resolve per convention. | — |

### Architecture compliance

- **AD-2:** migrations remain dual-provider; no migration is added or changed here. Never add a migration to one provider project only (`scripts/add-migration.sh`). This story adds no migration at all.
- **AD-6/AD-7:** single replica, scale-to-zero. Nothing here touches the Container App or queue. (The recompute lock's `maxReplicas = 1` assumption is a separate open deferred item.)
- **AD-19:** no new secret; nothing sensitive in the step summary. **AD-21:** do not reintroduce SQL password auth anywhere; the CI identity's contained-user roles are unchanged.
- **Project rule — no fix to shipped code without a spec/story trail:** this story *is* the trail. **Process gate:** deploy-pipeline changes require a live observation (Task 7), mirroring the Auth0/Chrome gate's intent.

### Testing requirements

- **Guard test (Task 2)** is the only automated test; it is a source-text scan like the other `Architecture.Tests` guards. Match their conventions: comment-stripping, assertion messages that name the file/step and expected vs. found, and mutation-verify once (move the block, see red).
- Existing `PostgresMigrationTests` / `SqlServerMigrationTests` stay untouched and green. `dotnet test EnergyTracker.sln` must pass (the guard lives in the solution run by CI).
- No frontend change → no Vitest/Playwright work. No i18n strings.
- There is no way to unit-test the shell inside the `run: |` block; the verification for it is Task 6 (restore rehearsal) and Task 7 (observed run). Say so plainly in Completion Notes; do not claim shell coverage the guard does not provide.

### Open Questions / Ask First (Ralf's decisions — ask before the marked task)

1. **Q1 — Restore-point mechanism (before Task 3). Recommendation: PITR marker (this story as written).** Alternative: a real pre-migration copy with `az sql db copy --dest-name energytracker-premigrate-<runid>` (same server, same edition). Copy pros: a ready-to-rename database, no restore wait. Cons: a second full database is billed per hour on Basic, the copy runs on a **5 DTU** database concurrently with live traffic (the migrations themselves have already timed out at that scale — see the 300→600s `Command Timeout` history), it needs a pruning step to bound cost, and it still loses the writes since the copy. The marker costs nothing and adds no load. Choose copy only if restore time in the Task 6 drill proves unacceptable.
2. **Q2 — Manual approval gate for migration-bearing deploys (GitHub Environment required reviewer).** Recommendation: **no**. Every push to `main` deploys; a gate on every deploy is friction for a solo operator, and a gate only when migrations are pending needs the workflow split into two jobs (scope creep). Revisit if a second contributor appears. This answers the other half of the original deferred entry.
3. **Q3 — Declare 7-day PITR retention in Bicep (Task 4/AC #9)?** Recommendation: **yes** — tiny, what-if-validated, removes the dependence on an unstated default. Cost: an `infra-deploy.yml` run to apply it (watch the image-preservation logic). If no, drop Task 4 and AC #9.
4. **Q4 — Expand/contract: document-only (this story) or enforce?** Recommendation: **document-only now.** Enforcement means a guard parsing `Up()` for destructive operations with a per-migration `// destructive-ok: <reason>` escape and a baseline for the 27 existing migrations — a plausible follow-up story, brittle for a single-developer repo until a destructive migration is actually planned.
5. **Q5 — Self-host schema creation gap.** Grep found no migration step for the Docker self-host path (see Scope Reality Check). Recommendation: confirm by trying a clean `docker compose up`, and if real, log it as a new deferred entry/story — it is a different audience (self-hosters, NFR11) and a different fix (documented step or migrate-on-start for Postgres). Not folded in here.
6. **Q6 — Who runs the Task 6 drill?** It needs an Azure login with rights on the resource group; the dev agent has none. Recommendation: Ralf (or a supervised session) runs the commands the dev agent prepares, and pastes the outputs for the Completion Notes.

### Scope cuts considered (to keep this one PR)

- **Cut:** automatic rollback in CI (a "restore on failure" job). Rolling back data automatically is a decision a human must make because of the data-loss window; the story gives the operator the marker and the commands instead.
- **Cut:** long-term retention (LTR), geo-restore and cross-region DR. Not required for the stated problem and not verified for the Basic tier in this story; separate decision if ever wanted.
- **Cut:** migration lint/enforcement (Q4), approval gate (Q2), self-host fixes (Q5), Postgres equivalent.
- **Kept small on purpose:** one workflow block, one guard test, one runbook section, one project-context block, optional five-line Bicep resource.

### Project Structure Notes

- Tests for repo-level conventions live in `tests/EnergyTracker.Architecture.Tests/` (`Exe` output type, xunit + Shouldly, `net10.0`); new file follows the `…Tests.cs` naming of its siblings. Workflows are scanned as text — keep the guard tolerant of step-order-neutral edits (it asserts only the three relative orderings in AC #4).
- Ops docs: Azure runbooks belong in `infra/README.md`, local/self-host material in `docs/`. This story is Azure-only; it intentionally does not touch `docs/self-hosting.md`.
- Migrations project paths (do not modify): `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/` (27 migrations, latest `20260925160743_AddSmartPlugReadingHouseholdIntervalStartIdIndex`), `…Migrations.Postgres/Migrations/` (27 migrations plus the model snapshot).

### Previous-work intelligence

- `spec-azure-sql-ci-migration-firewall.md` (2026-08-13) introduced the whole migration-apply step and its boundaries ("migration runs before Deploy new revision"; "never echo the connection string or password"; "Ask First" on RBAC failures) — those boundaries still hold and AC #3 restates them.
- Commits `9a1076d` (raise SQL command timeout, 300s→600s) and `95e4fa1` (temp index for the Story 3.7 dedup DELETE) show migrations have already hit the 5-DTU ceiling in production at ~179k-row scale; this is the evidence that a snapshot-by-copy (Q1) is not free on Basic.
- Story 1.11 / AD-21 changed the DB auth path since the original entry was written; the runbook must use Entra auth in every connection example.
- `spec-household-export-oom-fix` / Story 7.1 gave the household a self-service **export**; Story 7.2 a self-service **restore from export**. Those are a *household-level* recovery path and are complementary, not a substitute for a database-level restore: a database restore recovers every household at once; an export/import recovers one. Mention both in the runbook's "other recovery options" line so Ralf has the full picture.

### References

- [Source: `_bmad-artifacts/implementation/deferred-work.md` — promoted entry, "code review of spec-azure-sql-ci-migration-firewall (2026-08-13)"]
- [Source: `_bmad-artifacts/implementation/spec-deferred-work-triage.md` — decision 2, promote list]
- [Source: `_bmad-artifacts/implementation/spec-azure-sql-ci-migration-firewall.md` — original boundaries]
- [Source: `.github/workflows/app-deploy.yml:109-222` migration + firewall steps; `:291-340` pre-deploy image capture and health-check rollback]
- [Source: `infra/modules/database-sqlserver.bicep` — Basic SKU, 2 GB cap, no backup policy; `infra/main.bicep:43-47`]
- [Source: `infra/README.md` — Entra-only cutover runbook; identity bootstrap (Owner on RG); image-preservation section]
- [Source: `infra/sql/grant-entra-db-users.sql` — CI identity DB roles]
- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md` — AD-2, AD-6, AD-19, AD-21; `consistency-conventions.md` Migrations row]
- [Source: `_bmad-artifacts/project-context.md` — Migrations (L137-138), Process gates, deferred-work convention]
- [Source: Microsoft Learn — "Automated backups in Azure SQL Database" (PITR 7 days default; Basic 1–7 days configurable); "Restore a database from a backup in Azure SQL Database" (restore creates a new database on the same server, cannot overwrite; restored DB billed at normal rates; rename to replace; `az sql db restore`); "Copy a transactionally consistent copy of a database" (`az sql db copy`, same edition)]

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5.5 (claude-sonnet-5-5)

### Debug Log References

- Task 2 red: `MigrationDeployWorkflowRestorePointGuardTests` failed with "…must write the pre-migration restore point to $GITHUB_STEP_SUMMARY (AC #4a) — the block is missing." (the intended reason).
- Task 2 mutation: moved the summary write below the retry loop → test failed with "writes $GITHUB_STEP_SUMMARY AFTER 'dotnet ef database update' (AC #4b)"; restored the file → green.
- `dotnet test EnergyTracker.sln`: 887 passed, 0 failed.

### Completion Notes List

**Done (Tasks 1, 2, 3, 5 and the code part of Task 4):**
- Decisions (Ralf, this session): Q1 = PITR marker (not `az sql db copy`); Q3 = yes, declare 7-day retention in Bicep. Q2/Q4 stay "no / document-only" per the story's recommendations; Q5/Q6 unanswered but not blocking (Q5 already logged as a deferred entry at story creation; Q6: Ralf runs the drill).
- `grep -rn suppressTransaction src/` → zero hits, so every migration is a single EF transaction (a single migration cannot be half-applied; a run can still stop between migrations).
- Workflow line numbers in the story (164-204) were slightly stale; the step content matched.
- `app-deploy.yml`: pure additions (53 lines, 0 deletions); the connection string, retry loop, `Command Timeout=600`, `timeout-minutes: 20`, firewall steps and health-check rollback are byte-identical. The block captures `RESTORE_POINT_UTC` once before the loop, reads `earliestRestoreDate` (`az sql db show`, `|| true`) and the pending list (`dotnet ef migrations list` + `grep -F '(Pending)'`), emits `::warning::` and writes "unavailable" for either on failure, and writes the summary with `|| echo "::warning::…"` so a summary-write failure cannot fail the deploy. No secret or connection string is written to the summary.
- EF pending format verified locally against a throwaway Postgres 17 container (own password, port 55432, stopped afterwards): fresh DB lists `<id> (Pending)` for all 27 migrations; after `database update` the `grep -F '(Pending)'` finds nothing, which the block turns into "none pending". The Postgres project was used for the format check only; the SqlServer provider uses the same EF command/output code.
- Shell block was exercised locally with stubbed `az`/`dotnet` (az failing → "unavailable" + warning; dotnet listing a pending migration) and `bash -n`; the rendered summary markdown was inspected. `actionlint` is not installed; YAML parses (PyYAML) and was reviewed manually. **There is no automated coverage of the shell inside `run: |` beyond the guard's ordering checks** — real verification is Tasks 6 and 7.
- Bicep: `shortTermRetention` child resource (`retentionDays: 7`) added to `database-sqlserver.bicep`; `az bicep build` compiles it into `Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies` with `retentionDays: 7`. The local Bicep CLI has no types for `@2025-01-01` and emits BCP081 warnings for every `Microsoft.Sql` resource in the module (pre-existing, not just the new one), so the PR `what-if` is the real validation.
- Runbook added to `infra/README.md` (flags verified against `az sql db restore/rename/delete --help`); expand/contract + restore-point pointer added to `project-context.md` (Migrations).

**Not done — needs Ralf (the dev agent has no `az` session):**
- Task 4 remainder (AC #9): clean PR `what-if`, then a live `infra-deploy.yml` run leaving the DB at 7 days, watching that the Container App image is not reset.
- Task 6 (AC #6) — DONE, evidence here (box left unchecked only because Task 6 subtasks were not individually ticked): drill run by Ralf 2026-10-02: restore 285 s; contained users `energytracker-prod-app` and `energy-tracker-devops-uami` survived (EXTERNAL_USER); two renames 26 s; `ef migrations list` on the restored DB showed history through `20260925160743_…`. Deviations folded into the runbook: the first draft's `short-term-retention-policy` command was wrong (correct: `az sql db str-policy show`); the check ran as the operator's identity, not the CI identity. Cleanup confirmed by Ralf: `az sql db list` shows only `master` and `energytracker`; the drill firewall rule is gone. Pre-Bicep `str-policy` showed `retentionDays: 7` (Azure default), so declaring it in Bicep should be a what-if no-op on the value; the new child resource itself is still what AC #9's what-if/live run must confirm.
- Task 7 (AC #8): observe the restore-point block in the first real `App Deploy` run.
- Task 8: resolve the `deferred-work.md` entry (delete it and its promoted index line, referencing this story in the commit) and set `sprint-status.yaml` to `review`/`done` — deliberately deferred until the live gates pass, so a failed live check doesn't leave the item deleted.

### File List

- `.github/workflows/app-deploy.yml` (modified)
- `tests/EnergyTracker.Architecture.Tests/MigrationDeployWorkflowRestorePointGuardTests.cs` (new)
- `infra/modules/database-sqlserver.bicep` (modified)
- `infra/README.md` (modified)
- `_bmad-artifacts/project-context.md` (modified)
- `_bmad-artifacts/implementation/10-1-migration-safety-on-deploy.md` (story file)
- `_bmad-artifacts/implementation/sprint-status.yaml` (modified)

### Change Log

- 2026-10-02: Added pre-migration restore-point summary block, guard test, 7-day PITR retention in Bicep, rollback runbook and expand/contract rule. Live gates (Tasks 4-remainder, 6, 7, 8) outstanding.
