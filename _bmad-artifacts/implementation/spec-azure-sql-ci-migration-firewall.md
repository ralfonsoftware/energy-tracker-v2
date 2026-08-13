---
title: 'Apply Pending EF Core Migrations to Azure SQL via CI'
type: 'bugfix'
created: '2026-08-13'
status: 'done'
review_loop_iteration: 0
context: []
baseline_commit: '3d65ac36ae44c6a001ad0295cce987587dbdd068'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Production `/login` returns 500 because the Story 1.5 migration (`AddHouseholdAndDataProtectionKeys`) was never applied to the Azure SQL database — `app-deploy.yml` has no migration step and `Program.cs` never calls `Database.Migrate()`. ASP.NET Core Data Protection hits `DataProtectionKeys` during the OIDC challenge and fails with SQL Error 208 (invalid object name).

**Approach:** Add a migration-apply step group to `app-deploy.yml`, run before the new revision is deployed. Since the SQL Server firewall only allows Azure-internal traffic, dynamically whitelist the GitHub-hosted runner's own public IP for the duration of the migration, then always remove it — this repo's Contributor-scoped OIDC deploy identity (already used for ACR/Container App management in this same job) grants it.

## Boundaries & Constraints

**Always:**
- The temporary firewall rule (`gh-actions-migrate-${{ github.run_id }}`) is removed in a step guarded by `if: always()`, regardless of whether the migration step succeeded.
- Migration runs before "Deploy new revision" — the new image must never start against a schema it expects but that isn't there yet.
- Reuse the existing `DATABASE_ADMIN_PASSWORD` secret and `etadmin` login (already used by `infra-deploy.yml`/`infra/main.bicepparam`) — do not add a new secret.
- Apply migrations only via the `EnergyTracker.Infrastructure.Migrations.SqlServer` project — production's `databaseProvider` is hardcoded `'SqlServer'` in `infra/main.bicepparam`.
- Resolve the SQL server name and runner IP dynamically at runtime (matches this workflow's existing pattern for ACR/Container App name resolution) — never hardcode either.

**Ask First:** If `az sql server firewall-rule create` fails on a permissions error (RBAC insufficient for the deploy identity), HALT and ask the human rather than broadening the identity's role assignment.

**Never:**
- Never add Postgres migration-apply logic to this workflow — self-host Postgres deployments are out of scope for this Azure-only CI pipeline.
- Never echo the connection string or password to workflow logs.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Happy path | Pending migration exists | Migration applied, firewall rule removed, deploy proceeds | N/A |
| No pending migrations | Schema already current | `dotnet ef database update` no-ops | N/A |
| Migration apply fails | Bad connection / SQL error mid-migration | Job fails before Docker build/deploy runs | Firewall rule still removed via `if: always()`; no image pushed, no deploy attempted |
| Firewall create fails (RBAC) | Deploy identity lacks permission | Job halts | Per Ask First — no migration attempted, no cleanup needed (rule was never created) |

</frozen-after-approval>

## Code Map

- `.github/workflows/app-deploy.yml` -- insert new step group between "Azure login (OIDC)" and "Resolve ACR login server"
- `infra/modules/database-sqlserver.bicep:66` -- reference for the connection-string format to replicate
- `scripts/add-migration.sh` -- reference for `dotnet ef` invocation conventions (`--project`/`--startup-project`/`--context`)
- `.config/dotnet-tools.json` -- confirms `dotnet-ef` 10.0.10 is a local tool requiring `dotnet tool restore`

## Tasks & Acceptance

**Execution:**
- [x] `.github/workflows/app-deploy.yml` -- add step "Resolve SQL Server name" (`az sql server list --resource-group ... --query "[0].name" -o tsv`, step id `sqlserver`) -- mirrors the existing ACR/Container App name resolution pattern
- [x] `.github/workflows/app-deploy.yml` -- add step "Determine GitHub runner public IP" (`curl -s https://api.ipify.org`, step id `runnerip`) -- needed to scope the temporary firewall rule to exactly this run
- [x] `.github/workflows/app-deploy.yml` -- add step "Whitelist runner IP for migration" (`az sql server firewall-rule create --name gh-actions-migrate-${{ github.run_id }} --start-ip-address/--end-ip-address` from the runner IP) -- opens just enough access to run the migration
- [x] `.github/workflows/app-deploy.yml` -- add step "Apply pending EF Core migrations" (`dotnet tool restore` then `dotnet ef database update` against the SqlServer migrations project, connection string built from `az sql server show --query fullyQualifiedDomainName` + `etadmin` + `secrets.DATABASE_ADMIN_PASSWORD`) -- the actual fix for the missing tables
- [x] `.github/workflows/app-deploy.yml` -- add step "Remove GitHub runner IP from SQL firewall" (`if: always()`, `az sql server firewall-rule delete --name gh-actions-migrate-${{ github.run_id }}`) -- guarantees no lingering public firewall exposure

**Acceptance Criteria:**
- Given a push to `main` with a pending EF Core migration, when `app-deploy.yml` runs, then the migration is applied to the production Azure SQL database before the new container revision is deployed.
- Given the migration step succeeds or fails, when the job reaches its end, then the temporary GitHub-runner firewall rule is always removed from the Azure SQL server.
- Given no pending migrations, when the job runs, then `dotnet ef database update` completes as a no-op and the deploy proceeds normally.

## Design Notes

Firewall rule name is deterministic (`gh-actions-migrate-${{ github.run_id }}`) rather than a random token, so the cleanup step needs no step-output plumbing beyond the SQL server name (which is captured once via the `sqlserver` step's output and reused by both the whitelist and cleanup steps, since GitHub Actions step outputs from an earlier successful step remain readable even if a later step in the same job fails).

Connection string mirrors `database-sqlserver.bicep:66` exactly:
```
Server=tcp:<fqdn>,1433;Database=energytracker;User ID=etadmin;Password=<pwd>;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;
```

## Verification

**Commands:**
- `actionlint .github/workflows/app-deploy.yml` -- expected: no new errors introduced (skip if `actionlint` isn't installed; fall back to manual YAML review)

**Manual checks (if no CLI):**
- Read the diff and confirm: firewall create/delete step names match exactly (`gh-actions-migrate-${{ github.run_id }}`), the cleanup step has `if: always()`, no secret value is echoed, and the new steps sit before "Resolve ACR login server" / "Deploy new revision".

## Suggested Review Order

**Migration execution & connection safety**

- Entry point — resolves the server, restores the local `dotnet-ef` tool, and applies pending migrations with brace-escaped password and propagation-delay retry.
  [`app-deploy.yml:158`](../../.github/workflows/app-deploy.yml#L158)

- Password is brace-escaped per `SqlConnectionStringBuilder` convention so `;`/`=`/`}` in the secret can't corrupt the connection string.
  [`app-deploy.yml:174`](../../.github/workflows/app-deploy.yml#L174)

- Retries up to 5 times with backoff since Azure SQL firewall rule changes can take up to 5 minutes to propagate.
  [`app-deploy.yml:177`](../../.github/workflows/app-deploy.yml#L177)

**Dynamic firewall whitelist & cleanup**

- Resolves the SQL Server name at runtime, mirroring this file's existing ACR/Container App name-resolution pattern.
  [`app-deploy.yml:111`](../../.github/workflows/app-deploy.yml#L111)

- Determines the runner's public IP with retry and IPv4-format validation before it's used in a firewall rule.
  [`app-deploy.yml:125`](../../.github/workflows/app-deploy.yml#L125)

- Opens a run-scoped `/32` firewall rule just wide enough for this job to reach the database.
  [`app-deploy.yml:139`](../../.github/workflows/app-deploy.yml#L139)

- Cleanup is `if: always()` + `continue-on-error` so a flaky delete never blocks the actual app deploy that follows.
  [`app-deploy.yml:200`](../../.github/workflows/app-deploy.yml#L200)
