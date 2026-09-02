# Security Review — AD-21 (Azure SQL Entra ID-only Authentication)

**Reviewer:** Security-focused Reviewer Gate
**Scope:** `invariants-rules.md` AD-21 (and its interaction with AD-19), plus the currently-implemented
infra/CI code it will replace: `infra/modules/database-sqlserver.bicep`, `infra/main.bicep`,
`infra/main.bicepparam`, `.github/workflows/app-deploy.yml`, `.github/workflows/infra-deploy.yml`.
**Date:** 2026-09-02

## Verdict

Directionally sound and a net security improvement over the current SQL-auth baseline, but **not safe
to implement as literally specified**: one claim in AD-21 ("`DATABASE_ADMIN_PASSWORD` becomes retirable
once cutover completes") is contradicted by the actual Bicep/CI code and would break the infra deploy
pipeline (or silently corrupt the SQL Server's admin password) if followed as written. Two other points
need a small amendment; the rest hold up.

---

## Findings

### 1. [HIGH] Retiring `DATABASE_ADMIN_PASSWORD` immediately will break `infra-deploy.yml`, not just "become safe to do"

AD-21's closing line states the GitHub secret "becomes retirable once cutover completes." This is true
only for *authentication* (SQL logins stop working once `azureADOnlyAuthentication: true`), but it is
**not true for the deployment pipeline as it exists today**:

- `infra/main.bicepparam:9` — `param databaseAdministratorPassword = readEnvironmentVariable('DATABASE_ADMIN_PASSWORD', '')`. If the secret is deleted, this silently resolves to an **empty string** rather than failing the build.
- `infra/main.bicep:156-169` and `infra/modules/database-sqlserver.bicep:11-12,31` — `administratorLoginPassword` is a required, non-optional `@secure()` parameter on the `Microsoft.Sql/servers` resource, still passed on **every** `infra-deploy.yml` run (both the `what-if` and `Deploy` steps), because that workflow redeploys the whole template in incremental mode on every run, not just at server-creation time.
- Consequence: the next `infra-deploy.yml` run after retiring the secret sends an empty-string `administratorLoginPassword` to `Microsoft.Sql/servers`. Best case, Azure rejects the deployment outright (password complexity validation failure), permanently breaking the infra pipeline until someone notices and reverts. Worst case, depending on how ARM's provider handles an empty secure string on an existing resource, it risks disturbing the stored admin credential state on a resource that (per AD-21) is meant to keep a working SQL admin login as a documented emergency fallback.

**AD-21 does not mention updating `database-sqlserver.bicep`/`main.bicep` at all.** For the secret to be
safely retirable, the Bicep template must first change to either (a) stop declaring
`administratorLoginPassword` as required (Azure SQL supports Entra-only server creation without a SQL
admin login/password on current API versions), or (b) keep a *placeholder* password parameter that is
no longer sourced from a real secret. As written, "retirable" is a claim about the CI/deploy code that
the spine change does not actually make true. Recommend either: amend AD-21 to explicitly scope a Bicep
change alongside the secret retirement, or (safer) explicitly defer secret retirement to a follow-up
story that lands only after `database-sqlserver.bicep` no longer requires a live password.

### 2. [MEDIUM] No stated rollback/transition window for `DATABASE_ADMIN_PASSWORD` — retire-immediately is the wrong default even setting aside Finding 1

Independent of the pipeline-breakage bug above: AD-21's cutover is described as fixed and
one-directional with no rollback path if something is wrong post-flip (e.g. a database user was granted
the wrong role, or `Authentication=Active Directory Default`'s `AzureCliCredential` fallback in
`app-deploy.yml` misbehaves in a way not caught by the CI job's happy-path testing). SQL-auth admin
access is the only fallback that lets a human intervene via `sqlcmd`/Azure Data Studio if Entra auth
breaks for both service identities simultaneously (e.g. an Entra tenant/conditional-access hiccup, or a
`Microsoft.Data.SqlClient` version regression noted in AD-21's own watch item). Recommend keeping
`DATABASE_ADMIN_PASSWORD` (secret + a still-valid SQL admin login on the server) for at least one full
deploy cycle post-cutover as a break-glass path, then retiring it in a deliberate follow-up once the
Entra-only path has been exercised in production at least once. This is a low-cost insurance policy at
solo-project scale (rotate/delete one GitHub secret later) against a real single-point-of-failure risk
(no working credential at all if Entra auth breaks).

### 3. [LOW] Role grants are correctly minimal, contrary to the instinct that `db_ddladmin` is over-provisioned

Checked against the actual migration history referenced in `app-deploy.yml`'s comments (e.g. the
`AddSmartPlugReadingUniqueIndex` migration's dedup `DELETE` against live `SmartPlugReading` rows): CI's
migrations aren't pure DDL — they include data migrations. `db_ddladmin` alone grants no DML rights
(SELECT/INSERT/UPDATE/DELETE), so `db_datareader`+`db_datawriter` are genuinely needed alongside
`db_ddladmin`, not just for querying `__EFMigrationsHistory` but for the raw-SQL data-migration steps
this codebase already has. This is the right minimal role set for the CI identity, not an
over-provisioning — no change needed. Also confirmed the app's runtime identity never calls
`.Migrate()`/`EnsureCreated()` at startup (grepped `src/`, no matches) and only ever touches the DB via
EF Core LINQ CRUD plus `PersistKeysToDbContext` (AD-17) row reads/writes — both pure DML, consistent with
`db_datareader`+`db_datawriter` being sufficient and correct for it. One residual, unavoidable risk
worth naming: `db_ddladmin` can `ALTER`/`DROP` *any* table in the database, not just EF-owned ones — so
a bad migration merged and auto-applied by `app-deploy.yml` has real blast radius. This is inherent to
running unattended automated migrations at all (already true today) and is **not worse** under AD-21 —
it's actually a *reduction* in blast radius versus today's baseline, where CI authenticates as the SQL
Server's own admin login (broader than `db_owner`-equivalent: can also manage server-level firewall
rules, other databases, etc., which `db_ddladmin` cannot). Worth stating explicitly in AD-21 as a
security *improvement*, since it's easy to read the new three-role grant as "adding" risk when it's
actually narrowing it relative to the status quo.

### 4. [LOW] Entra Admin as a single human account is an acceptable trade-off, but the single point of failure is real and worth one explicit mitigation

AD-21 already reasons about this and explicitly defers group-promotion as unnecessary at solo scale —
that's a defensible call. The residual risk worth flagging (not blocking): if the named human Entra ID
account is locked out, disabled, or the owner loses access to it (lost MFA device, offboarded from the
tenant, etc.), **no one** can administer the SQL Server's Entra role grants going forward (no service
identity has `db_owner`/admin rights by design, correctly). Recommend one low-cost mitigation that
doesn't require standing up an Entra ID group: ensure the tenant's own Global Administrator / break-glass
account (which every Entra ID tenant should already have per Microsoft's own guidance, independent of
this project) can reset or reassign the SQL Server's Entra Admin if the primary account is ever
unavailable — i.e. confirm this is already true rather than assuming it. This is a note for the runbook,
not a spine change.

### 5. [LOW] Committing the provisioning `.sql` script is safe in principle, but AD-21 doesn't say it must use placeholders — verify at implementation time

Entra object IDs (`sid`s for `CREATE USER … FROM EXTERNAL PROVIDER`) and the tenant ID are not secrets
in the traditional sense (they're visible to anyone with read access to the resource in the Azure
Portal/CLI, and Entra object IDs alone don't grant access) — so committing them to source control is
lower-risk than committing a password or client secret. However, it is still unnecessary disclosure:
- The tenant ID pins the script to a specific organization, and the Container App / CI UAMI's object IDs are stable identifiers useful for targeted reconnaissance or social-engineering context if this repo is ever made public or forked.
- AD-21 already states the script's purpose is to be "repeatable if the SQL server is ever redeployed from scratch" — that goal is served equally well by a script with **placeholder tokens** (e.g. `<CONTAINER_APP_PRINCIPAL_ID>`, `<CI_UAMI_PRINCIPAL_ID>`) filled in by hand at run time, which is consistent with AD-21's own "manual, not pipeline-automated" design (a human is already running this by hand, so substituting a value costs nothing extra).

Recommend the actual `infra/sql/grant-entra-db-users.sql` (when written) use placeholders, not IDs
copy-pasted from a live `az ad sp show`/`az containerapp show` output. This is a minor hardening, not a
blocking issue — flag for the implementation story, not necessarily a spine amendment.

### 6. Cutover ordering (AD-21's fixed sequence) — no meaningful exposure widening found

Walked the three-step sequence: (1) set Entra Admin while SQL auth still works → (2) connect as admin to
create the two contained database users and grant roles → (3) flip `azureADOnlyAuthentication`. Between
steps 1 and 3, both SQL auth and Entra auth are valid simultaneously — this is a real widening of valid
authentication paths, but not a widening of the **attack surface** in any exploitable sense: setting an
Entra Admin does not by itself grant any new principal access beyond what could already authenticate via
SQL auth, and creating contained database users (step 2) only grants the two already-trusted service
identities (Container App system-assigned identity, CI UAMI) access they're intended to have permanently
— it doesn't expose anything to a new/untrusted principal. The intermediate state is strictly "old
access path still works, new access path now also works," never "something new and untrusted can get
in." No finding here; the fixed order is correct and the brief transitional dual-auth window is
intentional and low-risk, consistent with AD-21's own reasoning for why the order can't be reversed.

### 7. Firewall / runner-IP whitelist posture — unaffected in a way AD-21 should say explicitly

`AllowAzureServices` (0.0.0.0–0.0.0.0, i.e. "any Azure-internal resource") and the per-CI-run
runner-IP whitelist (`app-deploy.yml` lines 128-150, 216-225) both operate at the network layer and are
**orthogonal to credential type** — Entra-only auth does not change who can *reach* the SQL Server, only
what credentials are accepted once reached. Two implications worth naming, since AD-21 doesn't address
network exposure at all:
- **Slightly higher-value target, same surface:** before AD-21, an attacker who somehow reached the SQL Server through the `AllowAzureServices` rule (e.g. from a compromised, unrelated Azure resource in the same region) still needed a SQL password. After cutover, that same reachability instead requires a valid Entra token for one of exactly two identities — arguably a *stronger* bar (no password to phish/leak/brute-force, tokens are short-lived and tied to managed identity/federated-credential infrastructure), so this is a net improvement, not a regression. Worth stating in AD-21 as a benefit, since the firewall posture itself doesn't need to change.
- **The runner-IP whitelist step in `app-deploy.yml` is unaffected and still necessary post-cutover** — AD-21 changes *how* the CI job authenticates (`Authentication=Active Directory Default` instead of a password) but does not remove the network-reachability requirement, so the temporary firewall-rule create/delete steps (lines 142-150, 216-225) stay exactly as they are today. This should be confirmed explicitly in the implementation story so nobody assumes Entra auth also grants network bypass (it doesn't — `AllowAzureServices` only covers Azure-internal IPs, and a GitHub-hosted runner is not "Azure-internal").

---

## Summary Table

| # | Finding | Severity |
|---|---|---|
| 1 | `DATABASE_ADMIN_PASSWORD` retirement will break `infra-deploy.yml` (empty-string password sent on every redeploy) unless `database-sqlserver.bicep`/`main.bicep` also change | High |
| 2 | No stated rollback/transition window before retiring the admin password — recommend keeping it one deploy cycle post-cutover | Medium |
| 3 | Role grants (`db_datareader`+`db_datawriter` app; `+db_ddladmin` CI) are correctly minimal given real data-migrations in history; net *reduction* in CI blast radius vs. today's server-admin-login baseline | Low (informational — no change needed) |
| 4 | Single human Entra Admin is an acceptable solo-scale trade-off; verify tenant break-glass admin can recover access if the named account is ever lost | Low |
| 5 | Committing the provisioning `.sql` script with literal object/tenant IDs is low-risk but unnecessary disclosure — use placeholders | Low |
| 6 | Cutover ordering — no exposure widening found | None |
| 7 | Firewall/runner-IP posture — orthogonal to auth type, unaffected, arguably strengthened; should be stated explicitly in AD-21 | Informational |
