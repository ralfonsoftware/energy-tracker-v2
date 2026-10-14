# Infrastructure

Bicep templates for the Energy Tracker Azure environment, deployed by
[`.github/workflows/infra-deploy.yml`](../.github/workflows/infra-deploy.yml).

## What gets deployed

`main.bicep` is a resource-group-scoped orchestrator that wires together the modules under
`modules/`:

- `log-analytics.bicep` — Log Analytics workspace (30-day retention)
- `container-apps-environment.bicep` — Container Apps managed environment (Consumption workload
  profile), wired to the Log Analytics workspace
- `container-registry.bicep` — Azure Container Registry, Basic SKU, admin user disabled
- `storage-queue.bicep` — Storage account + queue, the AD-6 `AzureStorageQueue` job-queue
  adapter's backing store
- `database-postgres.bicep` / `database-sqlserver.bicep` — exactly one is deployed, selected by
  the `databaseProvider` parameter (`'Postgres'` | `'SqlServer'`), matching AD-2
- `key-vault.bicep` — Key Vault (Standard, RBAC permission model, no access policies) holding one
  RSA key, `data-protection`, that wraps the ASP.NET Core Data Protection key ring (AD-17, audit
  S2; see "Data Protection key encryption" below)
- `container-app.bicep` — the Container App itself: system-assigned managed identity, `AcrPull`
  role assignment against the registry, a `Key Vault Crypto Service Encryption User` role
  assignment scoped to the Data Protection key only, and `DataProtection__KeyVaultKeyId` as a
  plain env var, scale-to-zero (`minReplicas: 0`)

Environment-specific values live in `main.bicepparam` — no secrets. Secret-shaped values
(`databaseAdministratorPassword`) are `@secure()` parameters supplied only at deploy time from
GitHub Actions secrets; they are never written into a parameter file.

### Switching `databaseProvider` leaves the old DB server running — delete it manually

`main.bicep` deploys exactly one of `database-postgres.bicep` / `database-sqlserver.bicep`,
selected by the `databaseProvider` parameter. Azure Resource Manager's incremental deployment
mode **never deletes a resource whose conditional module branch is dropped** — so switching
`databaseProvider` (e.g. `Postgres` → `SqlServer`) deploys the new database server but leaves the
previous one running and billed. After switching providers, manually delete the orphaned server
(e.g. `az postgres flexible-server delete` / `az sql server delete`) once you've confirmed the new
one is live. This isn't hypothetical — it happened during this story's own implementation; see the
story file's Change Log.

### Azure SQL Entra-only auth cutover: never redeploy the flip before the manual grant script has run

Per architecture spine AD-21, `database-sqlserver.bicep` gets a Microsoft Entra Admin and
(eventually) `azureADOnlyAuthentication: true`. Those two must **never** land in the same Bicep
deploy on a server that doesn't already have both the Container App's and CI's identities
provisioned as contained database users — doing so locks both out immediately, with no SQL-auth
fallback left. The required sequence, every time (first cutover **and** any from-scratch SQL
Server redeploy — disaster recovery, resource-group rebuild, region move):

0. One-time (per environment) prerequisite: set the `AZURE_SQL_ENTRA_ADMIN_LOGIN`,
   `AZURE_SQL_ENTRA_ADMIN_OBJECT_ID`, and `AZURE_SQL_ENTRA_ADMIN_TENANT_ID` GitHub repository
   variables (a human Entra ID account — the project owner — never a service identity; see
   `infra/main.bicepparam`'s own comment for the sourcing rationale). `infra-deploy.yml` reads
   them into its `az deployment` steps' environment, matching `OIDC_AUTHORITY`/`OIDC_CLIENT_ID`'s
   existing treatment. Left unset, the Entra Admin addition below silently deploys with a blank
   admin — harmless (`azureADOnlyAuthenticationEnabled` stays `false` regardless, so this alone
   can't lock anyone out) but incomplete; step 2 below cannot succeed until these are set and a
   redeploy has actually applied a real Entra Admin.
1. Deploy the Entra Admin addition with the `azureADOnlyAuthenticationEnabled` Bicep param left at
   its default (`false`) — SQL auth still works.
2. Run `infra/sql/grant-entra-db-users.sql` by hand (`sqlcmd`/Azure Data Studio, as the Entra
   Admin) to provision both identities as contained database users, then verify both can actually
   connect.
3. Only then redeploy with `azureADOnlyAuthenticationEnabled=true` passed as a one-off CLI
   parameter override (never committed to `main.bicepparam` — see its own comment), as its own
   separate change.

See `docs/local-vs-azure-deltas.md#D6` and `ARCHITECTURE-SPINE/invariants-rules.md#ad-21` for the
full reasoning — this is the same class of Bicep-incremental-mode gap as the `databaseProvider`
switch warning above (a step Bicep can't enforce that must be run out of band before/after a
declarative change), not a hypothetical concern.

### `infra-deploy.yml` preserves the currently-running Container App image

`container-app.bicep` always declares `image: placeholderImage` in its template, so — before this
was fixed — every `infra-deploy.yml` run unconditionally reset the live Container App image back
to the placeholder (`mcr.microsoft.com/k8se/quickstart:latest`), reverting whatever image
`app-deploy.yml` had most recently deployed. `infra-deploy.yml` now reads back the currently
running image (`az containerapp show`) before deploying and passes it through as a
`placeholderImage` CLI parameter override, so re-running `infra-deploy.yml` (e.g. to rotate a
secret) no longer takes the app down. On a brand-new environment with no Container App yet, that
lookup is empty and the parameter's own default (the placeholder image) is used, same as before —
the Container App still bootstraps correctly on first deploy, and only starts running a real image
once `app-deploy.yml` first runs.

### Rolling back a bad migration (Azure SQL point-in-time restore)

`app-deploy.yml` applies EF Core migrations **before** it deploys the new revision, and the old
revision keeps serving traffic (and writes) while they run. Azure SQL already takes automatic
backups and supports point-in-time restore (PITR); the migration step records a restore marker in
the run's **Summary** tab ("Pre-migration restore point": UTC timestamp, server, database, pending
migrations, `earliestRestoreDate`, and the ready-made `az sql db restore` command). Retention is
declared in `modules/database-sqlserver.bicep` (`backupShortTermRetentionPolicies`, 7 days — the
Basic-tier maximum). This is the runbook for using that marker. Azure cannot overwrite an existing
database during a restore, so a rollback is **restore to a new database, then swap names** — the
Container App's connection string names the database `energytracker`, which is why the swap uses
`az sql db rename` instead of repointing the app.

1. **Decide: forward-fix or restore.** A restore throws away every write made since the marker —
   including writes the *old* revision accepted while the migration ran. If the bad migration only
   needs a corrective follow-up migration (and no data was lost), prefer a forward-fix deploy. Restore
   only when data or schema is damaged in a way a new migration cannot repair. Other recovery
   options with a smaller blast radius: a household can use its own export (Story 7.1) and restore
   from export (Story 7.2); a database restore recovers every household at once.
2. **Restore the marker timestamp to a new database.** Take `Restore point (UTC)` from the failed
   run's summary and subtract about a minute to absorb runner/Azure clock skew (the command printed
   in the summary already does). The marker must not be older than `Earliest restore date` from the
   same summary; if it is, PITR cannot reach it. The restored database is billed at normal rates
   until it is deleted. `--service-objective Basic` assumes the production database is still on the
   Basic tier — use its current service objective if it has been scaled.

   ```bash
   az sql db restore --resource-group <rg> --server <server> \
     --name energytracker --dest-name energytracker-restore-<runid> \
     --time <marker-minus-1min, e.g. 2026-10-02T13:24:00Z> --service-objective Basic
   ```

3. **Verify the restored database before swapping.** Connect as yourself, with your own Entra
   account (a temporary firewall rule is needed — copy the pattern from `app-deploy.yml`, and
   remove it afterwards) and check that `dotnet ef migrations list` shows the **pre-migration** set, with the bad migration
   `(Pending)`:

   ```bash
   dotnet ef migrations list \
     --project src/EnergyTracker.Infrastructure.Migrations.SqlServer \
     --startup-project src/EnergyTracker.Infrastructure.Migrations.SqlServer \
     --context EnergyTrackerDbContext \
     --connection "Server=tcp:<server-fqdn>,1433;Database=energytracker-restore-<runid>;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
   ```

4. **Swap the names** (old → bad, restored → live). Between the two commands there is no database
   called `energytracker`; the Container App's connections fail and must reconnect.

   ```bash
   az sql db rename --resource-group <rg> --server <server> \
     --name energytracker --new-name energytracker-bad-<runid>
   az sql db rename --resource-group <rg> --server <server> \
     --name energytracker-restore-<runid> --new-name energytracker
   ```

   If the second rename fails, no database is called `energytracker`: immediately rename
   `energytracker-bad-<runid>` back to `energytracker` to restore service, then investigate.

5. **Do not redeploy the same commit.** `app-deploy.yml` applies pending migrations on every run, so
   deploying again before the bad migration is reverted or fixed in `main` re-applies it to the
   restored database.
6. **Verify the app.** `GET /health` is liveness-only (it does not touch the database), so also load a
   real page that reads data and log in.
7. **Data-loss window.** Everything written between the marker and the swap is lost. The old revision
   keeps serving writes during the migration, so the window is at least the migration's duration plus
   however long steps 1–4 take.
8. **Clean up.** Delete `energytracker-bad-<runid>` once it is no longer needed for forensics, plus any
   scratch databases (`az sql db delete --yes ...`). Basic databases bill hourly, so a forgotten copy
   costs money. Confirm with `az sql db list`.

This runbook uses the operator's own Azure login throughout. It uses neither CI identity and does
not depend on the identity split (Story 11.5, "Workflow identities and their permissions" below).

**Why not `dotnet ef database update <previous migration>`?** The `Down()` methods are scaffolded and
never rehearsed, and they cannot restore data that a destructive `Up()` already dropped. PITR is the
rollback mechanism; migrations are written to be safe instead (expand/contract, see
`_bmad-artifacts/project-context.md`, Migrations).

_Drill (2026-10-02, Story 10.1, production `energytracker` restored from a point 15 minutes earlier into a
scratch database on the same server):_

- **Restore duration: 285 s** (~5 min) for the current production size. Expect it to grow with the
  database; this is the floor for the time to recovery, before verification and the swap.
- **Contained Entra users survive the restore.** Both `energytracker-prod-app` and
  `energy-tracker-devops-uami` were present as `EXTERNAL_USER` in the restored database, so no
  `grant-entra-db-users.sql` re-run is needed.
- **Two consecutive `az sql db rename` calls took 26 s** in total — that is the window in which no
  database named `energytracker` exists. Connections from the Container App fail during it and must
  reconnect.
- `dotnet ef migrations list` against the restored database listed the migration history up to
  `20260925160743_AddSmartPlugReadingHouseholdIntervalStartIdIndex`. The drill ran against an
  up-to-date schema, so the "bad migration shows `(Pending)`" outcome in step 3 was not exercised
  with a real bad migration.
- The drill connected with the operator's own Entra identity, not `energy-tracker-devops-uami`; the
  CI identity's access was confirmed only by its contained user being present.

## Data Protection key encryption (Key Vault) and key revocation

**What and why.** ASP.NET Core Data Protection signs and encrypts the session cookie with a key
ring that `PersistKeysToDbContext` stores in the `DataProtectionKeys` table (needed so a scale-to-zero
cold start does not regenerate it). Before Story 11.6 the ring sat in that table in the clear on
Azure: anyone with database read access, or a backup, could read the master key and forge a session
cookie for any household member (audit S2). AD-17 (amendment 2026-10-10) fixes this by wrapping the
ring with one Key Vault key. Each ring element is encrypted with a fresh AES key and that AES key is
wrapped (`RSA-OAEP`) by the vault key, so the table holds only ciphertext and `kid` pointers to the
key version used. Self-hosters get the same protection from a certificate (`docs/self-hosting.md`);
outside Development the app refuses to start with neither (fail closed).

**What is deployed.** `modules/key-vault.bicep`: vault `et<env><token>kv` and key `data-protection`
(RSA 2048, `wrapKey`/`unwrapKey` only, no expiry, no rotation policy, purge protection off). In
`container-app.bicep`: the Container App's system-assigned identity gets **Key Vault Crypto Service
Encryption User** (`e147488a-f6f5-4113-8e2d-b22465e65bf6`: `keys/read`, `keys/wrap/action`,
`keys/unwrap/action`) **scoped to the key**, never to the vault, and the versionless key URI is set as
`DataProtection__KeyVaultKeyId`. The deploy identity creates the vault, key and role assignment
(all inside Owner on the resource group); the PR identity needs nothing new (Reader reads the new
resources).

### Rollout order (do not collapse)

1. **PR A (infra)** merges first; `infra-deploy.yml` creates the vault, key, role and env var. The
   running app image ignores the variable, so nothing changes for members.
2. **PR B (app)** merges second, only after PR A's `infra-deploy.yml` run is green. The new image
   selects the Key Vault protector but does not call Key Vault yet (the existing plaintext key
   still decrypts; a new key is only created after a revoke). Check that `app-deploy.yml` really ran
   for the merge commit before step 4 (Dependabot auto-merges made by `GITHUB_TOKEN` do not
   trigger deploys).
3. Why not one PR: a merge touching `src/**` and `infra/**` starts both deploy workflows in an
   unspecified order; if the app deploy ran first the new image would start with no protector,
   refuse to start (fail closed), fail the health check and roll back.
4. **Cutover and revocation** last, after the pre-cutover revision is deactivated (below).

### Verify the Azure side (read-only, after PR A's deploy)

Role assignments can take minutes to propagate; do not conclude "denied" before ten minutes.

```bash
RG=<resource-group>
VAULT=$(az keyvault list -g $RG --query "[0].name" -o tsv)
APP=$(az containerapp list -g $RG --query "[0].name" -o tsv)
PRINCIPAL=$(az containerapp show -n $APP -g $RG --query identity.principalId -o tsv)

# RBAC model on, purge protection state, public access
az keyvault show -n $VAULT --query "{rbac:properties.enableRbacAuthorization, purge:properties.enablePurgeProtection, publicAccess:properties.publicNetworkAccess}"

# Exactly one assignment on the key: the Container App's principal, role "Key Vault Crypto Service Encryption User"
az role assignment list --scope "/subscriptions/$(az account show --query id -o tsv)/resourceGroups/$RG/providers/Microsoft.KeyVault/vaults/$VAULT/keys/data-protection" -o table
# ... and nothing for that principal at vault scope
az role assignment list --scope "$(az keyvault show -n $VAULT --query id -o tsv)" --assignee $PRINCIPAL -o table

# The env var carries the versionless key URI
az containerapp show -n $APP -g $RG --query "properties.template.containers[0].env[?name=='DataProtection__KeyVaultKeyId']"

# One key version only, also after re-running infra-deploy.yml once (idempotency)
az keyvault key list-versions --vault-name $VAULT -n data-protection -o table
```

If a redeploy of the module creates a second key version, that is harmless (older versions stay
usable for unwrap and each ring element records the version it was wrapped with) but record it.

### Cutover and revocation (Ralf, once, after PR B is deployed)

The key ring is cached in-process for up to about 24 hours, so revoking while an older revision still
runs would leave that revision signing with a revoked key. The revoke itself signs every member out
once (14-day sliding cookie; no data is lost). The Container App is single-revision.

1. Confirm the active revision is the one from PR B and older revisions are inactive:
   `az containerapp revision list -n $APP -g $RG -o table`.
2. Note the cutoff (UTC, with offset): `CUTOFF=$(date -u +%Y-%m-%dT%H:%M:%SZ)`.
3. Set it. This creates a new revision; at startup the app calls `IKeyManager.RevokeAllKeys` for that
   cutoff **only if** a non-revoked key created before it exists, so a restart with the variable left
   in place is a no-op:

   ```bash
   az containerapp update -n $APP -g $RG --set-env-vars DataProtection__RevokeKeysCreatedBeforeUtc=$CUTOFF
   ```

   The first request afterwards creates a new key, wrapped by Key Vault. This is the first moment
   Key Vault is actually called.
4. Watch the logs for the revocation line and for any `Forbidden` / `403` from Key Vault:
   `az containerapp logs show -n $APP -g $RG --follow`. A 403 means the role has not propagated or
   the scope is wrong; fix it and restart the revision (sessions are already invalid, so waiting
   costs nothing extra).
5. Leave the variable in place (idempotent). Remove it with the next routine `infra-deploy.yml`
   run (Bicep does not declare it, so a redeploy drops it) or:
   `az containerapp update -n $APP -g $RG --remove-env-vars DataProtection__RevokeKeysCreatedBeforeUtc`.

### Verify the cutover (live, never skipped)

1. In Chrome against the real deployment: the old session is gone (first load redirects to sign-in),
   a fresh Auth0 sign-in works and the household loads, a reload keeps the session.
2. The new key row is encrypted. Connect as yourself (the "Rolling back a bad migration" runbook's
   method: own Entra login, temporary firewall rule, remove it afterwards) and run counts only,
   **never select or paste `Xml`**:

   ```sql
   -- only the revoked pre-cutover rows may still hold a plaintext master key (normally 1)
   SELECT COUNT(*) FROM DataProtectionKeys WHERE Xml LIKE '%<masterKey%';
   -- at least 1: a new row encrypted with the Key Vault key
   SELECT COUNT(*) FROM DataProtectionKeys WHERE Xml LIKE '%encryptedKey%' AND Xml LIKE '%vault.azure.net%';
   ```

3. The app log shows the protector line for Azure Key Vault.

### If sign-in fails after the revoke

Look at the container logs first. `Forbidden` / `403` from Key Vault: wait for role propagation
(up to ten minutes), re-check the role assignment scope with the commands above, then restart the
revision. A new key could not be created, so every request that needs a key fails until it is. Do
not widen the role to the vault; the scope must stay the key.

### Residual risk and optional cleanup

Revoked plaintext key material stays in older database backups and restore points until they age out
(the Story 10.1 restore points keep it for the retention period). Revoked keys are rejected either
way. Optionally, after the live check, delete the revoked plaintext rows
(`DELETE FROM DataProtectionKeys WHERE Xml LIKE '%<masterKey%'`); it removes live plaintext material
from the database, backups keep it until they expire, and it cannot be undone.

## Workflow identities and their permissions

Two Azure identities exist, one per trust level (Story 11.5, audit S11). A pull request must not be
able to act as the deploy identity: anyone who can push a branch can edit a workflow on it, and
workflow code on a branch is attacker-controlled by construction. So the fix is an identity with
*less power*, not a guard inside the workflow.

| Identity | Role | Federated credential (subject) | Repository secret | Used by | Database access |
|---|---|---|---|---|---|
| `energy-tracker-devops-uami` (deploy) | **Owner** on the target resource group | `repo-branch-main` (`main` push only) | `AZURE_CLIENT_ID` | `app-deploy.yml`, `infra-deploy.yml` | contained Entra user, `db_datareader`/`db_datawriter`/`db_ddladmin` (`infra/sql/grant-entra-db-users.sql`) |
| `energy-tracker-pr-reader-uami` (PR) | **Reader** plus the custom role **Energy Tracker PR What-If** (`Microsoft.Resources/deployments/whatIf/action` only), both on the target resource group | `repo-pr` (`pull_request`) | `AZURE_PR_CLIENT_ID` | `pr-review.yml` job `validate-infra` | none |

The deploy identity creates the Key Vault, its Data Protection key and the key-scoped role assignment
(Story 11.6; all inside Owner on the resource group). Nothing changes for the PR identity: Reader
reads the new resources, and it cannot grant itself any Key Vault right (AD-17 "Prerequisite").

`AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID` are shared. Fork pull requests never receive an OIDC
token or secrets from GitHub, whatever credentials exist.

Each workflow starts with a **preflight** step (`Preflight: identity permissions`, running
[`scripts/ci/azure-preflight.sh`](../scripts/ci/azure-preflight.sh) with the mode below) that checks
these rights by behaviour with harmless calls, before any migration, push or deployment. Every
probe runs, so one failed run lists all problems; a missing right and an excess right both fail the
run with a message that names the right and points at this section. A denied probe must be an
authorization failure (`AuthorizationFailed`, 403, ...); any other error is reported as its own
failure and never counts as a pass.

### Deploy identity (`deploy-app` and `deploy-infra` modes)

| Right | Needed by | Why | How the preflight checks it |
|---|---|---|---|
| Read the SQL server, container registry and container app in the target resource group | `app-deploy.yml` | resolves their generated names at runtime | `az sql server list`, `az acr list`, `az containerapp list`, each must return a name (`deploy-app`) |
| Read the production database through the contained Entra user | `app-deploy.yml` migration step | `dotnet ef database update` connects as this identity (AD-21) | `dotnet ef migrations list` against the production database with the migration step's retry loop (5 x 30 s, firewall rule propagation) (`deploy-app`) |
| Database write and DDL (`db_datawriter`, `db_ddladmin`) | `app-deploy.yml` migration step | applying migrations | **not probed**: no harmless write exists; the migration step itself fails if it is missing |
| Push to the container registry (`AcrPush` or Owner) | `app-deploy.yml` | `docker push` of the new image | **not probed**: no harmless push; `az acr login` runs before the migration to catch the common failure early |
| Update the container app and its registry/ingress settings | `app-deploy.yml` | deploy a new revision, ensure pull credential and port | **not probed**: no harmless probe; checked by the steps that use them |
| Read role assignments on the resource group | `infra-deploy.yml` | the template creates role assignments (AcrPull) | `az role assignment list -g <rg>` (`deploy-infra`) |
| Deployment and role-assignment write on the resource group | `infra-deploy.yml` | `az deployment group create` | `az deployment group what-if` at the **default** validation level, the only harmless call that checks deploy-class write rights (`deploy-infra`) |
| **Not allowed:** any reach beyond the target resource group | | least privilege | `az group list` must show exactly the target resource group (both modes) |

### PR identity (`pr` mode)

| Right | Needed by | Why | How the preflight checks it |
|---|---|---|---|
| Read the target resource group and its container app | `validate-infra` | the what-if reads existing resources; the job also resolves the running image | `az group show`, `az containerapp list` |
| **Not allowed:** any write | | a PR must not change anything | `az tag update --operation Delete --tags energy-tracker-preflight` on the resource group must be denied (deleting a tag key that does not exist changes nothing even if it were allowed) |
| **Not allowed:** any reach beyond the target resource group | | least privilege | `az group list` must show exactly the target resource group |
| Run a what-if on the target resource group (`Microsoft.Resources/deployments/whatIf/action`, custom role "Energy Tracker PR What-If") | `validate-infra` | Reader alone is denied the what-if call itself, even with `ProviderNoRbac` (live, Story 11.5 R2.2) | **not probed**: no harmless probe other than the what-if itself; a missing right fails the "What-if validation" step with `AuthorizationFailed` |
| Azure CLI 2.76.0 or newer on the runner | `validate-infra` | `--validation-level ProviderNoRbac` | `az version` |

**Why Reader alone is not enough, and what the PR identity gets on top.** By Microsoft's
documentation the what-if has the same permission requirements as the deployment itself at the
default validation level (`Provider`), so `pr-review.yml` uses `--validation-level ProviderNoRbac`
(Azure CLI 2.76.0+), which skips the per-resource write checks. Verified live (Story 11.5 R2.2,
2026-10-11): even then, Azure denies a Reader the call itself (`AuthorizationFailed` for
`Microsoft.Resources/deployments/whatIf/action` on the deployment scope). The PR identity therefore
also holds the custom role **Energy Tracker PR What-If** on the target resource group, which grants
exactly that one action and nothing else (no deployment write, no resource write). The what-if does
not evaluate `listKeys()` (used by `storage-queue.bicep` and `container-apps-environment.bicep`), so
no key-listing right is needed. The PR what-if passes inert placeholders for
`DATABASE_ADMIN_PASSWORD` and `OIDC_CLIENT_SECRET` instead of the repository secrets, because
what-if never shows or deploys secure parameter values.

**If a what-if ever demands more.** Widen the custom role to `Microsoft.Resources/deployments/*` on
the resource group, and nothing beyond that. **Never** Contributor or Owner, and never a right that
lists keys: if a what-if ever demands one, stop and decide deliberately, because handing the PR
identity a key-listing right defeats the split.

**Running the preflight by hand** (this is also the drill that proves a denied probe really fires:
signed in with your own, broader account, `pr` mode must **fail** with the "ALLOWED. Excess right"
message):

```bash
export AZURE_RESOURCE_GROUP_NAME=<target-resource-group>
scripts/ci/azure-preflight.sh pr
# deploy-app also needs the firewall rule from app-deploy.yml for the database read; for a quick local
# run without it: PREFLIGHT_DB_ATTEMPTS=1 scripts/ci/azure-preflight.sh deploy-app
```

## One-time identity bootstrap (already done — reference only)

The workflows authenticate to Azure via OIDC federated-credential login (`azure/login`, pinned to a
commit SHA, no client secret). The identities necessarily have to exist *before* a workflow can run
at all — Bicep can't create the identity that deploys it — so this is a manual, one-time step, done
once outside of any pipeline.

**This has already been done for this repository** (deploy identity: 2026-08-12; PR identity split
out under Story 11.5: 2026-10-11, including the removal of `repo-pr` from the deploy identity). The GitHub
repository secrets the workflows depend on (`AZURE_CLIENT_ID` for the deploy identity,
`AZURE_PR_CLIENT_ID` for the PR identity, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`) already
exist. Do not recreate or re-provision any of this — the commands below are reference
documentation for a *future* environment or fork that needs to set this up from scratch, and for
the one-time Story 11.5 split.

_Verified role mapping before the split (Story 11.5 task R0, 2026-10-11):_ the deploy identity
(`energy-tracker-devops-uami`) has exactly one role assignment, **Owner** scoped to
`/subscriptions/<subscription-id>/resourceGroups/energy-tracker-rg` (nothing at subscription scope,
no extra roles), and two federated credentials in `energy-tracker-devops-rg`: `repo-branch-main`
(`...:ref:refs/heads/main`) and `repo-pr` (`...:pull_request`). The `repo-pr` credential on the
deploy identity is the one task R3 removes once the PR identity is proven.

Both federated credentials' subjects use GitHub's **immutable subject format** (owner/repo
database IDs appended after `@`, not just names) — this repository was created after GitHub's July
15, 2026 cutover to that default, so `ref:refs/heads/main`/`pull_request` subjects are suffixed with
`@<owner-id>` / `@<repo-id>` rather than the plain `repo:owner/repo:...` form older docs and
tooling may still show:

```text
repo-branch-main (deploy identity):  repo:ralfonsoftware@121377414/energy-tracker-v2@1327052942:ref:refs/heads/main
repo-pr (PR identity):               repo:ralfonsoftware@121377414/energy-tracker-v2@1327052942:pull_request
```

Verified 2026-08-12 via `az identity federated-credential list --identity-name
energy-tracker-devops-uami --resource-group energy-tracker-devops-rg`, cross-checked against
`gh api repos/ralfonsoftware/energy-tracker-v2` (`owner.id` / `id`).

These are **user-assigned managed identities** used as the OIDC subject rather than classic Entra
ID App Registrations + service principals — `azure/login` accepts either shape identically (it
only cares about `client-id` / `tenant-id` / `subscription-id` plus a federated credential trusting
the workflow's subject). The managed-identity route needs one fewer resource (no separate service
principal object) and is used below.

```bash
# 1. Create the resource group this workflow deploys into (one-time; the workflow does not create it).
az group create --name <resource-group-name> --location <region>

# 2. Create the deploy identity (user-assigned managed identity) that main-branch workflows use.
az identity create \
  --name energy-tracker-devops-uami \
  --resource-group <bootstrap-resource-group>

# 3. Grant it Owner scoped to the target resource group (Owner since it also needs to create the
#    AcrPull role assignment in container-app.bicep — Contributor + User Access Administrator is an
#    equally valid, more scoped alternative). Never at subscription scope.
az role assignment create \
  --assignee-object-id <deploy-identity-principal-id> \
  --assignee-principal-type ServicePrincipal \
  --role Owner \
  --scope /subscriptions/<subscription-id>/resourceGroups/<resource-group-name>

# 4. Federated credential trusting this repo's main-branch subject ONLY — this is what lets the
#    deploy workflows exchange a GitHub Actions OIDC token for an Azure access token, with no
#    stored secret. For repos created after GitHub's July 15, 2026 cutover to the immutable
#    subject format, use repo:<org>@<org-id>/<repo>@<repo-id>:ref:refs/heads/main instead —
#    check with `gh api repos/<org>/<repo> --jq '{owner: .owner.id, repo: .id}'`.
az identity federated-credential create \
  --name repo-branch-main \
  --identity-name energy-tracker-devops-uami \
  --resource-group <bootstrap-resource-group> \
  --issuer https://token.actions.githubusercontent.com \
  --subject repo:<org>/<repo>:ref:refs/heads/main \
  --audiences api://AzureADTokenExchange

# 5. The PR identity (Story 11.5 task R1): a separate managed identity, Reader plus the custom
#    "Energy Tracker PR What-If" role (step 5c) on the target resource group (not the
#    subscription), nothing else, no database user.
az identity create \
  --name energy-tracker-pr-reader-uami \
  --resource-group <bootstrap-resource-group>
az role assignment create \
  --assignee-object-id <pr-identity-principal-id> \
  --assignee-principal-type ServicePrincipal \
  --role Reader \
  --scope /subscriptions/<subscription-id>/resourceGroups/<resource-group-name>

# 5c. The custom role that lets it run a what-if (Reader alone is denied, Story 11.5 R2.2): the
#     single action Microsoft.Resources/deployments/whatIf/action, nothing else.
cat > pr-whatif-role.json <<'JSON'
{
  "Name": "Energy Tracker PR What-If",
  "Description": "Run az deployment group what-if on the target resource group. No deployment write, no resource write.",
  "Actions": ["Microsoft.Resources/deployments/whatIf/action"],
  "NotActions": [],
  "AssignableScopes": ["/subscriptions/<subscription-id>/resourceGroups/<resource-group-name>"]
}
JSON
az role definition create --role-definition pr-whatif-role.json
az role assignment create \
  --assignee-object-id <pr-identity-principal-id> \
  --assignee-principal-type ServicePrincipal \
  --role "Energy Tracker PR What-If" \
  --scope /subscriptions/<subscription-id>/resourceGroups/<resource-group-name>

# 5b. Its one federated credential, for the pull_request subject. Fork PRs never receive an OIDC
#     token from GitHub, so this cannot authenticate a run from a fork. Immutable subject format as
#     in step 4 (repo:<org>@<org-id>/<repo>@<repo-id>:pull_request).
az identity federated-credential create \
  --name repo-pr \
  --identity-name energy-tracker-pr-reader-uami \
  --resource-group <bootstrap-resource-group> \
  --issuer https://token.actions.githubusercontent.com \
  --subject repo:<org>/<repo>:pull_request \
  --audiences api://AzureADTokenExchange

# 6. Set the GitHub repository secrets from the identities' IDs.
gh secret set AZURE_CLIENT_ID --body <deploy-identity-client-id>
gh secret set AZURE_PR_CLIENT_ID --body <pr-identity-client-id>
gh secret set AZURE_TENANT_ID --body <tenant-id>
gh secret set AZURE_SUBSCRIPTION_ID --body <subscription-id>
```

**Story 11.5 rollout (one time, order matters).** Before the repo change merges: R0 (read-only
check of the current role mapping), R1 (steps 5, 5b and the `AZURE_PR_CLIENT_ID` secret above).
After it merges, once the first `app-deploy.yml` run with the preflight is green and a throw-away PR
that edits a comment in `infra/main.bicep` has passed `validate-infra` as the PR identity: R3, which
removes the old path from the deploy identity. Doing R3 earlier would make every infra PR fail on
login.

```bash
az identity federated-credential delete \
  --identity-name energy-tracker-devops-uami \
  --resource-group energy-tracker-devops-rg \
  --name repo-pr --yes
az identity federated-credential list --identity-name energy-tracker-devops-uami \
  --resource-group energy-tracker-devops-rg -o table   # must show only repo-branch-main
```

The equivalent classic App Registration shape (`az ad app create`, `az ad sp create`, then the
same role assignment and `az ad app federated-credential create` with the same
subject/issuer/audience) works identically from the workflow's point of view.

## OIDC_CLIENT_SECRET — a second, unrelated "OIDC" (Story 1.5)

Don't confuse this with the "OIDC federated-credential login" described above — that's how this
workflow itself authenticates to Azure (no client secret, GitHub-Actions-token-based). Story
1.5's `OIDC_CLIENT_SECRET` is a completely different thing: the Client Secret of the end-user
sign-in OIDC provider app registration (Entra ID, Auth0, Authentik, Keycloak, etc.) that the
*deployed application* uses to authenticate household members — see
[../docs/self-hosting.md](../docs/self-hosting.md) for what to register and where its Authority/
Client ID/Client Secret get used.

`infra-deploy.yml` reads it from the GitHub repository secret `OIDC_CLIENT_SECRET` and passes it
through to `main.bicepparam` the same way `DATABASE_ADMIN_PASSWORD` already flows. **This secret
does not exist yet in this repository** — it must be created by a repo admin
(`gh secret set OIDC_CLIENT_SECRET`) once a real OIDC provider app registration exists; until
then, `infra-deploy.yml` runs will fail at the `readEnvironmentVariable('OIDC_CLIENT_SECRET')`
step in `main.bicepparam`. `oidcAuthority`/`oidcClientId` (not secret) are literal values in
`main.bicepparam` itself, left blank for the same reason — fill in real values once a provider is
registered.

## The target resource group is a repository *variable*, not a secret

`AZURE_RESOURCE_GROUP_NAME` is a GitHub Actions repository **variable**
(Settings → Secrets and variables → Actions → Variables tab — distinct from the three secrets
above), because a resource-group name isn't secret-shaped. Every place the workflow needs the
resource-group name reads this same variable — it is never hardcoded in the workflow YAML or in
`main.bicepparam` as a fallback or example value that could silently diverge from it.

The resource group itself must already exist before the workflow can deploy into it —
`az deployment group create` deploys *into* a resource group, it does not create one (step 1
above).

## Deploying by hand (e.g. for local `what-if`)

```bash
export DATABASE_ADMIN_PASSWORD="<password>"  # read by main.bicepparam via readEnvironmentVariable()
az deployment group create \
  --resource-group "$AZURE_RESOURCE_GROUP_NAME" \
  --template-file infra/main.bicep \
  --parameters infra/main.bicepparam
```

Re-running this against an already-provisioned resource group is safe: `az deployment group
create` runs in incremental mode by default, reconciling desired vs. actual state rather than
recreating unchanged resources.

## PR review workflow and branch protection

[`.github/workflows/pr-review.yml`](../.github/workflows/pr-review.yml) runs on every pull
request against `main` — build/test/lint always (`build-test-lint` job), and a read-only infra
`what-if` validation (`validate-infra` job) when `infra/**` changed and the PR isn't from a fork.
It never deploys anything; that stays exclusive to `infra-deploy.yml`/`app-deploy.yml` on push to
`main`. `validate-infra` logs in as the read-only PR identity (Reader plus the what-if role), not the deploy identity (see
"Workflow identities and their permissions").

`main` has GitHub branch protection requiring both `build-test-lint` and `validate-infra` as
passing status checks before a PR can merge (`required_status_checks.strict: true`, no required
review count or restrictions configured beyond that). Configured 2026-08-12 via:

```bash
gh api repos/ralfonsoftware/energy-tracker-v2/branches/main/protection \
  -X PUT \
  -H "Accept: application/vnd.github+json" \
  --input - <<'EOF'
{
  "required_status_checks": {
    "strict": true,
    "checks": [
      { "context": "build-test-lint" },
      { "context": "validate-infra" }
    ]
  },
  "enforce_admins": false,
  "required_pull_request_reviews": null,
  "restrictions": null
}
EOF
```

Note: `validate-infra`'s Azure-login/what-if steps are conditional (`if:`) on infra files having
changed and the PR not being from a fork — but the *job* itself still reports an overall pass
when those steps are skipped (a job's status reflects whether any of its steps failed, not
whether every step ran). Requiring `validate-infra` as a status check therefore does not block
PRs that don't touch `infra/**`, or fork PRs that do (those get an `::warning::` annotation
instead — see the workflow's own "Notice" step).

## Dependabot version updates and auto-merge

[`.github/dependabot.yml`](../.github/dependabot.yml) opens one grouped PR per ecosystem (npm in
`/web`, NuGet, GitHub Actions, Docker) every Monday for **patch and minor** updates, with a
3-day cooldown on new releases. Major updates arrive as individual PRs. Dependabot *security*
updates are a separate repo setting and keep working as before.

[`dependabot-automerge.yml`](../.github/workflows/dependabot-automerge.yml) enables GitHub
auto-merge (squash) on npm, NuGet and Docker Dependabot PRs that are patch or minor updates, so they
merge once `build-test-lint` and `validate-infra` pass. Majors are never auto-merged. **GitHub
Actions PRs are never auto-merged, whatever the update type** (Story 11.5): actions are pinned to
commit SHAs, so Dependabot proposes minor and patch bumps too, and an action runs with the deploy
identity's token once it is on `main`. Merge those by hand after reading the SHA diff. Because every
merge to `main` that touches `src/**` or `web/**` deploys to production (`app-deploy.yml`), the
allowed update types are the safety boundary; change them only together with the spec
(`_bmad-artifacts/implementation/spec-dependabot-version-updates-auto-merge.md`).

Branch protection keeps `strict: true`, so a Dependabot PR that falls behind `main` waits until
it is rebased. Rebase it by hand: comment `@dependabot rebase` on the PR. Auto-merge then
completes on its own once the checks are green again.

Repo prerequisite (already set): Settings → General → "Allow auto-merge".

## Scanning and supply chain

- **Actions are pinned to a commit SHA with a version comment**
  (`uses: actions/checkout@<40-hex> # v7.0.1`). A mutable tag lets whoever controls the upstream
  repo change what runs with this repo's OIDC token; the SHA does not move. The architecture test
  `WorkflowActionsArePinnedToCommitShaTests` fails the build for any unpinned reference. To update:
  merge the weekly Dependabot GitHub Actions PR **by hand** after reading the SHA diff (it updates the
  SHA and the comment together). To pin a new action by hand, resolve the *commit* with
  `gh api repos/<owner>/<repo>/commits/<tag> --jq .sha` (not `git/ref/tags`, which can return an
  annotated tag object). `aquasecurity/trivy-action` had its tags force-pushed to malicious commits
  in March 2026: for that action check the SHA against the maintainers' advisory before updating.
- **CodeQL** ([`codeql.yml`](../.github/workflows/codeql.yml)): C# and JavaScript/TypeScript on every
  PR to `main` (not Dependabot PRs, which cannot upload results), every push to `main` and weekly.
  Findings are under Security > Code scanning. Not a required check.
- **Container image scan** ([`container-scan.yml`](../.github/workflows/container-scan.yml)): builds
  the Dockerfile and runs Trivy for HIGH and CRITICAL findings that have a fix, on PRs touching the
  image inputs, on `main` and weekly (new CVEs in unchanged base images). It is its own workflow
  with no secrets and no `id-token`, so the scanner never shares a job with the deploy identity.
  Not a required check: a base-image CVE should not block an unrelated PR. Ralf may make it
  required later (Settings, branch protection, add `scan-image`).
- **Dependencies:** NuGet advisories fail `dotnet restore` (NuGet Audit gate, `Directory.Build.props`);
  `npm audit --omit=dev --audit-level=moderate` fails `build-test-lint` the same way for shipped
  npm packages. Dev-only advisories are listed with a reason in
  `_bmad-artifacts/implementation/deferred-work-accepted.md`.
- **Triage:** fix the finding, or write an `[open]` entry in
  `_bmad-artifacts/implementation/deferred-work.md`. Do not suppress silently; an ignore entry needs a
  reason and a revisit trigger.
