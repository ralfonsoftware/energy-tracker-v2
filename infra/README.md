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
- `container-app.bicep` — the Container App itself: system-assigned managed identity, `AcrPull`
  role assignment against the registry, scale-to-zero (`minReplicas: 0`)

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

## One-time identity bootstrap (already done — reference only)

The workflow authenticates to Azure via OIDC federated-credential login
(`azure/login@v2`, no client secret). That identity necessarily has to exist *before* the
workflow can run at all — Bicep can't create the identity that deploys it — so this is a manual,
one-time step, done once outside of any pipeline.

**This has already been done for this repository.** The three GitHub repository secrets the
workflow depends on (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`) already exist,
and the identity they point at already has **two** federated credentials, plus a role assignment
scoping it to the target resource group. Do not recreate or re-provision any of this — the
commands below are reference documentation for a *future* environment or fork that needs to set
this up from scratch.

- `repo-branch-main` — trusts the `main`-branch push subject. Used by `infra-deploy.yml` and
  `app-deploy.yml` (deploy-capable runs only).
- `repo-pr` — trusts the `pull_request` subject. Used by `.github/workflows/pr-review.yml`'s
  `validate-infra` job for read-only `what-if` validation on same-repo PRs only (fork-originated
  `pull_request` runs never receive an OIDC token from GitHub regardless of this credential's
  existence, so this cannot be used to deploy from a fork).

Both credentials' subjects use GitHub's **immutable subject format** (owner/repo database IDs
appended after `@`, not just names) — this repository was created after GitHub's July 15, 2026
cutover to that default, so `ref:refs/heads/main`/`pull_request` subjects are suffixed with
`@<owner-id>` / `@<repo-id>` rather than the plain `repo:owner/repo:...` form older docs and
tooling may still show:

```text
repo-branch-main:  repo:ralfonsoftware@121377414/energy-tracker-v2@1327052942:ref:refs/heads/main
repo-pr:            repo:ralfonsoftware@121377414/energy-tracker-v2@1327052942:pull_request
```

Verified 2026-08-12 via `az identity federated-credential list --identity-name
energy-tracker-devops-uami --resource-group energy-tracker-devops-rg`, cross-checked against
`gh api repos/ralfonsoftware/energy-tracker-v2` (`owner.id` / `id`).

This deployment uses a **user-assigned managed identity** as the OIDC subject rather than a
classic Entra ID App Registration + service principal — `azure/login@v2` accepts either shape
identically (it only cares about `client-id` / `tenant-id` / `subscription-id` plus a federated
credential trusting the workflow's subject). The managed-identity route needs one fewer resource
(no separate service principal object) and is used below; an App Registration would look
functionally the same otherwise.

```bash
# 1. Create the resource group this workflow deploys into (one-time; the workflow does not create it).
az group create --name <resource-group-name> --location <region>

# 2. Create the user-assigned managed identity that GitHub Actions authenticates as.
az identity create \
  --name <identity-name> \
  --resource-group <bootstrap-resource-group>

# 3. Grant it a role scoped to the target resource group (Owner used here since it also needs to
#    create the AcrPull role assignment in Task 1's container-app.bicep — Contributor + User
#    Access Administrator is an equally valid, more scoped alternative).
az role assignment create \
  --assignee-object-id <identity-principal-id> \
  --assignee-principal-type ServicePrincipal \
  --role Owner \
  --scope /subscriptions/<subscription-id>/resourceGroups/<resource-group-name>

# 4. Federated credential trusting this repo's main-branch subject — this is what lets
#    azure/login@v2 exchange a GitHub Actions OIDC token for an Azure access token, with no
#    stored secret. For repos created after GitHub's July 15, 2026 cutover to the immutable
#    subject format, use repo:<org>@<org-id>/<repo>@<repo-id>:ref:refs/heads/main instead —
#    check with `gh api repos/<org>/<repo> --jq '{owner: .owner.id, repo: .id}'`.
az identity federated-credential create \
  --name repo-branch-main \
  --identity-name <identity-name> \
  --resource-group <bootstrap-resource-group> \
  --issuer https://token.actions.githubusercontent.com \
  --subject repo:<org>/<repo>:ref:refs/heads/main \
  --audiences api://AzureADTokenExchange

# 4b. Second federated credential trusting the pull_request subject — lets pr-review.yml's
#     validate-infra job authenticate for read-only what-if validation on same-repo PRs. Fork
#     PRs never receive an OIDC token from GitHub, so this credential alone cannot be used to
#     authenticate a deploy-capable run from a fork. For repos created after GitHub's July 15,
#     2026 cutover to the immutable subject format, use repo:<org>@<org-id>/<repo>@<repo-id>:
#     pull_request instead — check with `gh api repos/<org>/<repo> --jq '{owner: .owner.id,
#     repo: .id}'`.
az identity federated-credential create \
  --name repo-pr \
  --identity-name <identity-name> \
  --resource-group <bootstrap-resource-group> \
  --issuer https://token.actions.githubusercontent.com \
  --subject repo:<org>/<repo>:pull_request \
  --audiences api://AzureADTokenExchange

# 5. Set the three GitHub repository secrets from the identity's IDs.
gh secret set AZURE_CLIENT_ID --body <identity-client-id>
gh secret set AZURE_TENANT_ID --body <tenant-id>
gh secret set AZURE_SUBSCRIPTION_ID --body <subscription-id>
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
`main`.

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
