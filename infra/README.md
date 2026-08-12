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

## One-time identity bootstrap (already done — reference only)

The workflow authenticates to Azure via OIDC federated-credential login
(`azure/login@v2`, no client secret). That identity necessarily has to exist *before* the
workflow can run at all — Bicep can't create the identity that deploys it — so this is a manual,
one-time step, done once outside of any pipeline.

**This has already been done for this repository.** The three GitHub repository secrets the
workflow depends on (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`) already exist,
and the identity they point at already has a federated credential trusting this repo's
`main`-branch subject, plus a role assignment scoping it to the target resource group. Do not
recreate or re-provision any of this — the commands below are reference documentation for a
*future* environment or fork that needs to set this up from scratch.

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
#    stored secret.
az identity federated-credential create \
  --name repo-branch-main \
  --identity-name <identity-name> \
  --resource-group <bootstrap-resource-group> \
  --issuer https://token.actions.githubusercontent.com \
  --subject repo:<org>/<repo>:ref:refs/heads/main \
  --audiences api://AzureADTokenExchange

# 5. Set the three GitHub repository secrets from the identity's IDs.
gh secret set AZURE_CLIENT_ID --body <identity-client-id>
gh secret set AZURE_TENANT_ID --body <tenant-id>
gh secret set AZURE_SUBSCRIPTION_ID --body <subscription-id>
```

The equivalent classic App Registration shape (`az ad app create`, `az ad sp create`, then the
same role assignment and `az ad app federated-credential create` with the same
subject/issuer/audience) works identically from the workflow's point of view.

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
