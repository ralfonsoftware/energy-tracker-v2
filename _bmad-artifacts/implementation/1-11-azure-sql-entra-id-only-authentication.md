# Story 1.11: Azure SQL Access via Microsoft Entra ID-Only Authentication

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a platform operator,
I want the production Azure SQL database to accept only Microsoft Entra ID authentication instead of SQL username/password,
so that the Container App and CI no longer depend on a shared SQL password as a production credential (AD-21).

## Acceptance Criteria

1. **Given** the existing Azure SQL Server (`infra/modules/database-sqlserver.bicep`) **when** a Bicep deploy adds a Microsoft Entra Admin (inline `administrators` block) with `azureADOnlyAuthentication` omitted/`false` **then** SQL authentication continues to work unchanged — this deploy is safe to run and re-run any time, on its own, with no other step required first (AD-21 "Deploy A").
2. **Given** that same deploy **when** it lands **then** `database-sqlserver.bicep`'s `connectionString` output (Azure environment only) becomes `Authentication=Active Directory Managed Identity` (system-assigned, no `User Id`), and `infra/main.bicep`/`infra/modules/container-app.bicep` carry that string through to the Container App's `db-connection-string` secret / `ConnectionStrings__Default` unchanged.
3. **Given** `.github/workflows/app-deploy.yml`'s "Apply pending EF Core migrations" step **when** it builds the migration connection string **then** it uses `Authentication=Active Directory Default;` (no `User Id`/`Password`), authenticating as `energy-tracker-devops-uami` via the `azure/login@v3` OIDC session the same job already establishes earlier (`DefaultAzureCredential`'s `AzureCliCredential` fallback) — the existing runner-IP firewall whitelist/retry/cleanup steps around it are unchanged.
4. **Given** `infra/sql/grant-entra-db-users.sql` (new, committed script, using placeholder tokens for the two identities' object IDs — never literal IDs from a live environment) **when** a human runs it by hand (`sqlcmd`/Azure Data Studio, authenticated as the Entra Admin) after Deploy A **then** it creates a contained database user for the Container App's system-assigned identity (`db_datareader` + `db_datawriter`) and for `energy-tracker-devops-uami` (`db_datareader` + `db_datawriter` + `db_ddladmin`) — this step is manual and out of band, never wired into Bicep or a GitHub Actions step.
5. **Given** both database users have been created and verified able to connect (a Container App revision restart; a successful CI migration run or no-op `dotnet ef database update`) **when** a separate, deliberate Bicep deploy flips `azureADOnlyAuthentication: true` **then** it ships only after that verification — never bundled into the same deploy/PR as the Entra Admin addition (AD-21 "Deploy B"); once flipped, SQL logins (including the original admin login) stop working entirely.
6. **Given** `DATABASE_ADMIN_PASSWORD` (GitHub secret) and the SQL admin login **when** this story's cutover completes **then** both are left live and unretired, as a break-glass fallback for at least one full deploy cycle — retiring them requires a separate follow-up change to `database-sqlserver.bicep`/`main.bicepparam` and is explicitly out of scope for this story.
7. **Given** local self-host (`docker-compose.sqlserver.yml`) **when** inspected after this story **then** it is unchanged — still a plain containerized SQL Server on `sa`/password, since Entra-only authentication is an Azure SQL Database feature that doesn't apply to it.

## Tasks / Subtasks

- [ ] Task 1: Write the manual DB-user provisioning script (AC: #4)
  - [ ] Create `infra/sql/grant-entra-db-users.sql` with two `CREATE USER [<placeholder>] FROM EXTERNAL PROVIDER;` statements plus role grants (`db_datareader`, `db_datawriter` for the app identity; `db_datareader`, `db_datawriter`, `db_ddladmin` for the CI identity), using placeholder tokens (e.g. `<CONTAINER_APP_PRINCIPAL_ID>`, `<CI_UAMI_PRINCIPAL_ID>`) — never literal object IDs
  - [ ] Add a short header comment: how to resolve the real object IDs at run time (`az containerapp show ... --query identity.principalId`, `az identity show --name energy-tracker-devops-uami ... --query principalId`), and that this script is run manually, never by CI/Bicep
- [ ] Task 2: Bicep — Entra Admin + connection-string rewrite ("Deploy A") (AC: #1, #2)
  - [ ] `infra/modules/database-sqlserver.bicep`: add a `properties.administrators` block (`ServerExternalAdministrator`: `administratorType: 'ActiveDirectory'`, `login`, `sid`, `tenantId`, `principalType`) sourced from new Bicep params — do not hardcode a tenant-specific value in the template. **Leave `azureADOnlyAuthentication` off this block entirely** (not `false` — omitted): per Microsoft's own `Microsoft.Sql/servers` reference, that field is *not* reliably settable via `administrators` on an update — "to update the `azureADOnlyAuthentication` property, [an] individual API must be used." Verified against current Microsoft Learn docs (`learn.microsoft.com/azure/templates/microsoft.sql/servers`); do not re-introduce `azureADOnlyAuthentication: true` on `administrators` based on older blog posts/examples that show it there — it won't reliably apply to an already-existing server.
  - [ ] Same file: add a **new, separate resource** `Microsoft.Sql/servers/azureADOnlyAuthentications` (`name: 'Default'`, `parent: sqlServer`, `properties.azureADOnlyAuthentication: <bool param>`), gated by a new bool param (e.g. `azureADOnlyAuthenticationEnabled`, default `false`). This is the actual Deploy B mechanism (Task 4) — the same template serves both Deploy A (param `false`) and Deploy B (param `true` on a later, separate deploy), no code branch needed, just a param-value change between the two deploys.
  - [ ] Decide and document how the Entra Admin's `sid`/`tenantId`/`login` params are sourced in `main.bicepparam` — this repo has two existing patterns for a non-secret, environment-specific value: a checked-in literal default (like `customDomainName`) or `readEnvironmentVariable(...)` (like `oidcAuthority`). Pick one and follow it; do not invent a third pattern.
  - [ ] Same file: change the module's `connectionString` output to `Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${databaseName};Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;` — drop `User ID=`/`Password=` entirely; the `#disable-next-line outputs-should-not-contain-secrets` suppression comment on this output can be removed since the string is no longer secret-shaped. **Do not** change the `@secure()` decorator on the `databaseConnectionString` param in `main.bicep`/`container-app.bicep`, and do not change the `db-connection-string` Container App secret into a plain env var — AD-19 keeps it a `secretRef` for composition-root uniformity across both DB providers (Postgres stays password-based), even though the Azure SQL variant itself no longer carries a password.
  - [ ] Confirm `infra/main.bicep`/`infra/modules/container-app.bicep` need no further change beyond what already flows through (`databaseConnectionString` is passed through opaquely today) — verify, don't assume
  - [ ] Validate with `bicep build` and `az deployment group what-if` (same command `pr-review.yml`'s infra `what-if` job already runs) before either Deploy A or Deploy B is applied live
- [ ] Task 3: CI migration step — Entra auth ("Deploy A", same change as Task 2) (AC: #3)
  - [ ] `.github/workflows/app-deploy.yml`: change the "Apply pending EF Core migrations" step's `CONNECTION_STRING` construction to `Server=tcp:${SQL_FQDN},1433;Database=energytracker;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;Command Timeout=600;` — remove `User ID=`/`Password=`/the `DATABASE_ADMIN_PASSWORD` env var reference from this step specifically (the secret itself stays defined at the repo level per AC #6, just unused by this one step)
  - [ ] Do not touch the firewall whitelist/retry/cleanup steps around it — they stay exactly as they are (network reachability is orthogonal to auth mode)
- [ ] Task 4: Manual cutover execution and verification (AC: #4, #5) — **human-executed, not something this story's automated tests cover**
  - [ ] After Task 2/3 ship and deploy: resolve both identities' real object IDs and run `infra/sql/grant-entra-db-users.sql` against the production database as the Entra Admin
  - [ ] Verify the Container App can connect (restart/redeploy a revision, confirm `/health` and a real request succeed)
  - [ ] Verify CI can connect (re-run `app-deploy.yml`'s migration step, or trigger it against a database already at the latest migration and confirm it succeeds as a no-op)
  - [ ] Only once both are confirmed: run the separate "Deploy B" deploy — flip Task 2's `azureADOnlyAuthenticationEnabled` param to `true` (deploys the `Microsoft.Sql/servers/azureADOnlyAuthentications` resource), on its own, never bundled with a Deploy A change
- [ ] Task 5: Doc/runbook cross-references (already drafted this session — verify they still match the shipped implementation) (AC: all)
  - [ ] `docs/local-vs-azure-deltas.md#D6` and `infra/README.md`'s "Azure SQL Entra-only auth cutover" section already reference this story as 1.11 and describe the Deploy A / manual step / Deploy B sequence — re-read both once implementation is final and correct any drift between what was planned and what actually shipped (e.g. exact param names)

## Dev Notes

- This is a pure infrastructure/CI story — **no application code changes**. `Program.cs:140`'s `UseSqlServer(connectionString, ...)` is already 100% driven by `ConnectionStrings__Default` (AD-2's composition-root config pattern); it does not care what auth mode the string encodes.
- **No new NuGet dependencies.** Verified against the resolved build lockfile: `Microsoft.Data.SqlClient` resolves to `6.1.1` (`src/EnergyTracker.Infrastructure.Migrations.SqlServer/obj/project.assets.json`), which already transitively carries `Azure.Identity 1.14.2` — the `Authentication=Active Directory *` connection-string keywords work today with zero new package references. (Not relevant to this story, but noted for whoever next bumps `Microsoft.Data.SqlClient`: version 7.0 is already GA and splits Entra auth support into a separate `Microsoft.Data.SqlClient.Extensions.Azure` package.)
- **The three-phase sequence in the Tasks above is not a suggestion — it's the architecture invariant (AD-21).** Never combine Task 2/3's Bicep change with a `azureADOnlyAuthentication: true` flip in the same deploy/PR. Never let `azureADOnlyAuthentication: true` become a permanent, unconditional part of `database-sqlserver.bicep` without also making the Entra Admin block equally permanent and idempotent — a from-scratch SQL Server redeploy must re-run Task 4's manual script before Deploy B's flip is safe to (re)apply against the new server. See `docs/local-vs-azure-deltas.md#D6` for the full failure-mode writeup.
- **`db_ddladmin` for the CI identity is deliberate, not over-provisioning.** The migration history includes real data migrations against live tables (e.g. `AddSmartPlugReadingUniqueIndex`'s dedup `DELETE`), not just DDL — `db_ddladmin` alone grants no DML rights, so `db_datareader`+`db_datawriter` are genuinely needed alongside it for CI. This is also a *reduction* in blast radius vs. today's baseline, where CI authenticates as the SQL Server's own admin login (broader than `db_ddladmin` — can also manage server-level firewall rules and other databases).
- **Entra Admin principal is a human Entra ID account (the project owner), not a service identity.** Do not default the Bicep param to `energy-tracker-devops-uami` or the Container App's own identity — database administration stays separate from the two service identities, which only ever get scoped data-plane/migration roles.
- **`energy-tracker-devops-uami` is not managed by this repo's Bicep** — it's a pre-provisioned User-Assigned Managed Identity referenced by name in `.github/workflows/*.yml` (`infra/README.md`'s "One-time identity bootstrap" section). Resolve its object ID via `az identity show`, don't assume it's derivable from anything in `infra/`.
- **Local self-host is genuinely out of scope, not an oversight.** `docker-compose.sqlserver.yml` runs a plain containerized SQL Server (`mcr.microsoft.com/mssql/server:2022-latest`), not Azure SQL — Entra-only authentication is an Azure SQL Database/Managed Instance feature with no equivalent for a local container. Do not touch that file.
- **`DATABASE_ADMIN_PASSWORD` retirement is explicitly out of scope for this story.** `infra/main.bicepparam` reads it via `readEnvironmentVariable('DATABASE_ADMIN_PASSWORD', '')` and `database-sqlserver.bicep`'s `administratorLoginPassword` is a required, non-optional `@secure()` parameter sent on **every** `infra-deploy.yml` run (incremental mode redeploys the whole template, not just at server creation) — deleting the secret today, or making this param optional without also changing how the server resource is declared, would break `infra-deploy.yml`. Leave the secret, the param, and the SQL admin login exactly as they are; a future story retires them together.
- Network exposure is unaffected by this story: `AllowAzureServices` and the CI runner-IP whitelist/retry/cleanup logic in `app-deploy.yml` stay exactly as they are — they govern *reachability*, this story only changes what *credentials* are accepted once reached.
- **Cold-start latency, accepted, not a defect:** Managed Identity/Entra token acquisition adds a token round-trip on top of the connection cost the Container App already pays after a scale-to-zero cold start (AD-7). No mitigation needed at this project's traffic scale — noted so it isn't mistaken for a regression if the first request after idle is observably slower post-cutover.
- **This pattern generalizes to any future identity.** If a later story splits out a worker process (`Deferred.md`'s "Worker/API process split") or otherwise needs a new identity to reach Azure SQL, it gets a contained database user added to `infra/sql/grant-entra-db-users.sql` with the same least-privilege pattern — never a reintroduced SQL-auth credential for just that one case.

### Project Structure Notes

- New file: `infra/sql/grant-entra-db-users.sql` (new `infra/sql/` directory — doesn't exist yet).
- Modified: `infra/modules/database-sqlserver.bicep`, `infra/main.bicep`, possibly `infra/main.bicepparam` (new Entra Admin param), `.github/workflows/app-deploy.yml`.
- Modified (already done this session, verify against final implementation): `docs/local-vs-azure-deltas.md` (new `## D6` section + table row), `infra/README.md` (new runbook section after "Switching `databaseProvider` leaves the old DB server running").
- No changes anywhere under `src/` or `web/`.

### References

- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-21] — the full rule this story implements, including the fixed Deploy A / manual step / Deploy B sequence, least-privilege role grants, Entra Admin principal rationale, and the `DATABASE_ADMIN_PASSWORD` retirement deferral. Finalized 2026-09-02 after a 3-reviewer Reviewer Gate pass (version-currency, adversarial-divergence, security) — see that same architecture run's `.memlog.md` for the full decision trail and which reviewer findings were folded in.
- [Source: docs/local-vs-azure-deltas.md#D6] — the from-scratch-redeploy lockout failure mode this story's sequencing exists to prevent, and why it recurs on every future SQL Server redeploy, not just this first cutover.
- [Source: infra/README.md] — "Azure SQL Entra-only auth cutover" runbook section (new); "Switching `databaseProvider` leaves the old DB server running" (existing, same class of Bicep-incremental-mode gap); "One-time identity bootstrap" section for how `energy-tracker-devops-uami` is provisioned/referenced.
- [Source: infra/modules/database-sqlserver.bicep] — current SQL-auth-only server/database/firewall resource and its `connectionString` output, to be changed by Task 2.
- [Source: .github/workflows/app-deploy.yml, "Apply pending EF Core migrations" step] — current SQL-auth connection-string construction and the surrounding firewall whitelist/retry/cleanup steps, to be changed (only the connection-string line) by Task 3.
- [Source: src/EnergyTracker.Api/Program.cs:121-145] — confirms `UseSqlServer(connectionString, ...)` is fully connection-string-driven; no code change needed.
- [Source: https://learn.microsoft.com/azure/templates/microsoft.sql/servers?pivots=deployment-language-bicep] — verified current `Microsoft.Sql/servers` `administrators`/`ServerExternalAdministrator` schema (confirms `principalType` is a valid field) and the explicit doc warning that `azureADOnlyAuthentication` isn't reliably settable via `administrators` on an update, only via the separate `Microsoft.Sql/servers/azureADOnlyAuthentications` resource — the basis for Task 2's two-resource split.
- [Source: src/EnergyTracker.Infrastructure.Migrations.SqlServer/obj/project.assets.json] — confirms resolved `Microsoft.Data.SqlClient` 6.1.1 / `Azure.Identity` 1.14.2, the basis for "no new NuGet dependency" above.

## Dev Agent Record

### Agent Model Used

{{agent_model_name_version}}

### Debug Log References

### Completion Notes List

### File List
