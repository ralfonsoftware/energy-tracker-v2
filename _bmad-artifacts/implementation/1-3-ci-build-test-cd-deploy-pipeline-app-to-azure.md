---
baseline_commit: 38ff97a5504cc3c2b615ed9dca1167b83bfac699
---

# Story 1.3: CI Build/Test & CD Deploy Pipeline (App to Azure)

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a developer,
I want a GitHub Actions workflow that builds, tests, and deploys the application container to Azure on merge to main,
so that every change to main reaches the running Azure environment without a manual build/push/deploy step.

## Acceptance Criteria

1. **Given** a push/merge to `main`, **when** the workflow runs, **then** it restores/builds the .NET solution and the `web/` frontend, runs the full test suite, and fails the workflow (blocking deploy) on any test failure.
2. **Given** a successful build and test pass, **when** the workflow continues, **then** it builds the multi-stage Docker image (`web/` build → .NET build → runtime, AD-13) and pushes it to the Azure Container Registry provisioned by Story 1.2.
3. **Given** the pushed image, **when** the workflow deploys, **then** it updates the Azure Container App to a new revision referencing that image tag, authenticating via OIDC federated-credential login scoped to the `main`-branch subject (no stored client secret).
4. **Given** a completed deployment, **when** the workflow finishes, **then** it checks the Container App's `/health` endpoint and fails/reports if it doesn't return 200 within a reasonable timeout.
5. **Given** the image tagging strategy, **when** a deploy happens, **then** the image is tagged with a human-readable, chronologically-sortable tag that embeds both the build timestamp and the triggering commit's short SHA (e.g. `20260812T1923-38ff97a5504`), so a deployed revision can be visually placed in time and mapped back to source at a glance — not just traced back via an opaque full-length SHA.

## Tasks / Subtasks

- [x] Task 1: Close the two gaps Story 1.2 deliberately left open so a real deployed image can actually run (blocks AC #3/#4 — see Dev Notes "Two silent traps")
  - [x] Re-add the `registries` entry to `infra/modules/container-app.bicep` (server: the ACR's `loginServer`, `identity: 'system'`) — Story 1.2 removed this because on a from-scratch deployment the `AcrPull` role assignment can't exist before the Container App does, causing eager credential validation to fail; that race no longer applies now that the Container App and its role assignment already exist live in Azure.
  - [x] Flip `containerAppTargetPort`'s default from `80` to `8080` in `infra/main.bicep` and `infra/main.bicepparam` — the real app image listens on `8080` (Dockerfile `ASPNETCORE_HTTP_PORTS`), not the placeholder's `80`.
  - [x] These two Bicep edits alone do **not** take effect until `infra-deploy.yml` runs again (it's `infra/**`-path-triggered) — do not rely on merging this story's PR to fix them. In the new workflow this story adds, also apply both settings directly via Azure CLI (`az containerapp registry set ... --identity system`, `az containerapp ingress update --target-port 8080`) so the running Container App is corrected immediately on this story's first deploy, independent of when/whether someone next triggers `infra-deploy.yml`. Both CLI calls are idempotent — safe to run on every deploy, not just once.
  - [x] Do not modify `infra-deploy.yml` itself and do not trigger it from the new workflow — keep the two pipelines independent (infra vs. app), per Story 1.2's established scope boundary.
- [x] Task 2: Author the CI build-and-test job (AC #1)
  - [x] New workflow file `.github/workflows/app-deploy.yml`, trigger: `push` to `main`, no path filter (every merge to `main` rebuilds/redeploys — unlike `infra-deploy.yml`'s `infra/**` filter)
  - [x] `permissions: { id-token: write, contents: read }`; `concurrency: { group: app-deploy, cancel-in-progress: false }` (same rationale as `infra-deploy.yml`: don't let overlapping pushes race each other against the same Container App revision)
  - [x] `actions/checkout@v7`
  - [x] `actions/setup-dotnet@v6` with `global-json-file: global.json` (picks up SDK `10.0.100`/`rollForward: latestFeature` automatically — do not hardcode a `dotnet-version` that could drift from `global.json`)
  - [x] `dotnet restore`/`dotnet build` against `EnergyTracker.sln` (or the `Api` project — restoring the whole solution is simplest and matches `dotnet test EnergyTracker.sln` below)
  - [x] `dotnet test EnergyTracker.sln` — runs all three test projects (`EnergyTracker.Api.Tests`, `EnergyTracker.Infrastructure.Tests`, `EnergyTracker.Architecture.Tests`). No extra CI service containers needed: `Infrastructure.Tests` uses Testcontainers (`Testcontainers.PostgreSql`/`Testcontainers.MsSql`) which drives Docker directly — `ubuntu-latest` runners have Docker pre-installed, so this works with zero `services:` config. A non-zero exit here must fail the job and block every later step (AC #1).
  - [x] `actions/setup-node@v7` with `node-version: '22'` — match the Dockerfile's `node:22-alpine` build stage exactly, even though `web/package.json`'s `@types/node` devDependency is `^24` (a type-checking version, not the actual Node runtime the shipped image builds/runs with — don't let CI drift from what the Dockerfile actually uses).
  - [x] `npm --prefix web ci` (not `npm install` — reproducible, matches `Dockerfile`'s frontend stage), `npm --prefix web run build`, `npm --prefix web run test` (Vitest unit/component tests, single run).
  - [x] `npm --prefix web run test:e2e` (Playwright) is **not** included in this blocking CI gate — the epic AC only names "the test suite" without specifying e2e, and Playwright's browser-install + server-spinup cost/flakiness isn't justified here; capture this as a scope decision in Completion Notes rather than silently omitting it. Story 1.4 (PR workflow) may revisit e2e/lint gating separately.
- [x] Task 3: Build and push the multi-stage Docker image (AC #2, #5) — same job, only after Task 2's steps all pass
  - [x] `docker/setup-buildx-action@v4`
  - [x] `azure/login@v3` (OIDC) using the **same three repository secrets `infra-deploy.yml` already uses** — `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` — this is the same pre-provisioned user-assigned managed identity and its `main`-branch federated credential (AC #3's "no stored client secret" is already satisfied by this existing identity; do not create a second one).
  - [x] Resolve the ACR login server dynamically — `az acr list --resource-group ${{ vars.AZURE_RESOURCE_GROUP_NAME }} --query "[0].loginServer" -o tsv` — never hardcode the registry name. Story 1.2's ACR name embeds `uniqueString(resourceGroup().id)`, a token not known at workflow-authoring time.
  - [x] `az acr login --name <registry-short-name>` (the part of the login server before `.azurecr.io`) to obtain Docker push credentials from the already-OIDC-authenticated `az` session.
  - [x] Compute the tag in a step before the build (so both the build-push step and Task 4's deploy step reference the identical value): `IMAGE_TAG=$(date -u +%Y%m%dT%H%M)-$(git rev-parse --short=11 HEAD)` (UTC, matches the `20260812T1923-38ff97a5504` shape — `YYYYMMDDTHHMM` then `-` then an 11-char short SHA), written to `$GITHUB_OUTPUT` so later steps/jobs can reference it.
  - [x] `docker/build-push-action@v7`: `context: .`, `file: Dockerfile`, `push: true`, `tags: <loginServer>/energy-tracker:<IMAGE_TAG>` (AC #5's tagging requirement). Optionally also set `labels: org.opencontainers.image.revision=${{ github.sha }}` on the same step — cheap to add, and keeps the *exact* full SHA machine-queryable (`docker inspect`/`az acr manifest`) even though the human-readable tag already carries the short form. An additional `:latest` tag is optional/not required by the AC.
- [x] Task 4: Deploy the new revision and gate on health (AC #3, #4) — same job, after the image push succeeds
  - [x] Resolve the Container App name dynamically — `az containerapp list --resource-group ${{ vars.AZURE_RESOURCE_GROUP_NAME }} --query "[0].name" -o tsv` — do not hardcode `energytracker-prod-app`, even though it's deterministic today (defensive against a future `environmentName` change).
  - [x] Apply Task 1's idempotent fixes here: `az containerapp registry set` (ACR pull auth via system identity) and `az containerapp ingress update --target-port 8080`.
  - [x] `az containerapp update --name <app> --resource-group ${{ vars.AZURE_RESOURCE_GROUP_NAME }} --image <loginServer>/energy-tracker:<IMAGE_TAG>` (same `IMAGE_TAG` value Task 3 computed and pushed — do not recompute it here, a re-run of `date -u +%Y%m%dT%H%M` in a later step would drift by a minute and reference a tag that was never pushed) — creates the new revision (AC #3).
  - [x] Resolve the FQDN — `az containerapp show ... --query properties.configuration.ingress.fqdn -o tsv` — then poll `https://<fqdn>/health` with a retry loop (e.g. every 10s, up to ~5 minutes) until it returns `200`; fail the job if the timeout is exceeded (AC #4). Give this a generous timeout: `minReplicas: 0` (scale-to-zero) means the very first request after a deploy may need to cold-start the revision.
- [ ] Task 5: Verify against every AC
  - [ ] AC #1: push a commit to `main` (or `workflow_dispatch` if added) and confirm the workflow fails at the test step when a test is deliberately broken, and that no later step (Docker build/deploy) runs when it does.
  - [ ] AC #2: confirm a new tag lands in the ACR (`az acr repository show-tags --name <registry> --repository energy-tracker`) after a successful run, and that the pushed image was built from `Dockerfile` (multi-stage: frontend → backend → runtime).
  - [ ] AC #3: confirm the workflow's Azure login step has no `client-secret:` input, and that `az containerapp show` reflects the new image reference after a run.
  - [ ] AC #4: confirm the workflow step that curls `/health` actually gates the job (deliberately point it at a wrong port/path once during verification to confirm it fails the run, then revert).
  - [ ] AC #5: confirm the deployed image's tag matches `YYYYMMDDTHHMM-<11-char short SHA>` and that the short SHA segment is a prefix of the triggering commit's full SHA (`git rev-parse HEAD` at trigger time vs. `az containerapp show --query properties.template.containers[0].image`); confirm the ACR (`az acr repository show-tags`) and the deployed Container App agree on the exact same tag string (guards against the Task 3/Task 4 timestamp-drift trap above).
  - [ ] Confirm the two Task 1 fixes actually stuck: `az containerapp show` reports `configuration.ingress.targetPort: 8080` and a `registries` entry referencing the ACR with `identity: system`.

## Dev Notes

- **Two silent traps this story must close, not just its literal ACs (see Task 1).** Story 1.2 deliberately shipped the Container App in a half-configured state and said so explicitly in its own Dev Notes/Review Findings: (a) no `registries` credential entry — pulling from the real ACR image this story pushes would 401 without it — and (b) `containerAppTargetPort` defaulted to `80` (the placeholder image's port) instead of `8080` (the real app's port, per `Dockerfile`'s `ASPNETCORE_HTTP_PORTS=8080`). Neither is stated as a literal AC in this story, but AC #3 ("updates... to a new revision") and AC #4 ("checks... `/health`... within a reasonable timeout") cannot actually pass without both being fixed — a revision referencing an unpullable image, or a healthy image behind the wrong ingress port, both fail the health gate. Fix both in Bicep (for future `infra-deploy.yml` runs) **and** via direct `az` CLI calls inside this story's own workflow (so the fix applies immediately, without depending on someone separately re-running `infra-deploy.yml`).
- **Possible RBAC gap to verify early, not assume away.** The federated identity `azure/login` authenticates as (the same one `infra-deploy.yml` uses) was bootstrapped with a role assignment scoped to the resource group for *deploying* resources (ARM/control-plane operations via Bicep). Pushing a Docker image to ACR is a *data-plane* operation gated by the `AcrPush` RBAC role, which is separate from control-plane `Contributor`-style access and was never explicitly granted in Story 1.2 (only the Container App's own managed identity got `AcrPull`, for pulling — not this workflow's identity, for pushing). Verify this early: if `az acr login`/`docker push` fails with a `401`/`403`, the identity needs `AcrPush` granted on the ACR (`az role assignment create --assignee <principal-id> --role AcrPush --scope <acr-resource-id>`, or declare it as a Bicep `roleAssignment` resource in `infra/modules/container-registry.bicep` for a version-controlled, idempotent fix — the latter is preferable if you have the identity's principal ID handy, mirroring how `container-app.bicep` already declares the `AcrPull` assignment). Document whichever fix path you take the same way Story 1.2 documented its identity bootstrap (`infra/README.md`) — this is exactly the kind of one-time Azure-side setup gap Story 1.2's Dev Notes warned would recur.
- **Testing this story requires a real Azure subscription** (the same `energy-tracker-rg` resource group / repository secrets Story 1.2 verified live against) to confirm AC #2–#4 end-to-end. If no live Azure credentials are available in this environment, verify what's mechanically checkable without them (workflow YAML structure, that the test-failure path blocks later steps by running `dotnet test`/`npm run test` locally, `docker build` succeeding locally against `Dockerfile`) and say explicitly in Completion Notes which ACs were verified live vs. structurally only — do not claim full end-to-end verification without an actual deploy.
- **Action versions** — GitHub deprecated the Node 20 runtime older action versions depend on; the most recent commit on this branch (`38ff97a`, "Fix: bump action versions for Node v24 support") already bumped `infra-deploy.yml` to `actions/checkout@v7`/`azure/login@v3` for this reason. Use the same current versions here: `actions/checkout@v7`, `azure/login@v3`, `actions/setup-dotnet@v6`, `actions/setup-node@v7`, `docker/setup-buildx-action@v4`, `docker/build-push-action@v7`. Re-verify these are still current at implementation time (`gh api repos/<org>/<repo>/releases` or the marketplace page) rather than trusting this list blindly if much time has passed.
- **`dotnet test EnergyTracker.sln` uses the Microsoft.Testing.Platform runner** (`global.json`'s `"test": { "runner": "Microsoft.Testing.Platform" }`), not classic VSTest — this is already how local dev runs tests (`docs/local-development.md`) and needs no special CI flag beyond a matching SDK version, which `global-json-file: global.json` in `actions/setup-dotnet` handles.
- **No path filter on this workflow's trigger**, unlike `infra-deploy.yml`. Every merge to `main` — regardless of which files changed — rebuilds, retests, and redeploys the app, per AC #1's literal "a push/merge to main". Don't add an `paths:` filter by analogy with `infra-deploy.yml`; that workflow is scoped to `infra/**` for a different reason (avoid redeploying infra on every app-only change) that doesn't apply here.
- **Resource-group name comes from the repository *variable* `AZURE_RESOURCE_GROUP_NAME`** (`vars.*`, not `secrets.*`), exactly as `infra-deploy.yml` already establishes — reuse it, don't reintroduce a hardcoded resource-group name or a second variable.
- **Don't hardcode the ACR or Container App resource names.** Both are provisioned by Story 1.2's Bicep with names that include `uniqueString(resourceGroup().id)` (ACR) or are otherwise not something this story's workflow authored — resolve them at runtime via `az acr list`/`az containerapp list` scoped to the resource group (there is exactly one of each in this resource group by design).
- **Cost/architecture constraints that still apply**: AD-13 (single container image serves API + SPA — this story's Docker build step is exactly what produces that artifact for real, replacing Story 1.2's public placeholder), AD-19 (the `/health` endpoint is liveness-only, no DB check — this story's health-gate step is checking process-up, not DB-connectivity; don't misread a `/health` 200 as "database migrations succeeded" or add a DB check to satisfy this AC, that's out of scope), NFR2/NFR14 cost discipline (this story adds no new billable resources — it only uses the ACR/Container App Story 1.2 already provisioned).
- **Scale-to-zero and the health check's timeout.** `minReplicas: 0` means a brand new revision may need to cold-start before `/health` responds — size the health-check retry loop generously (Story 1.2's own live verification saw Container App provisioning/revision-healthy transitions take real time). Don't set an aggressively short timeout that would make AC #4 flaky on a legitimately-slow-but-successful cold start.
- **Tag format is `YYYYMMDDTHHMM-<11-char short SHA>` (e.g. `20260812T1923-38ff97a5504`), computed once and threaded through both the push and deploy steps.** This is a deliberate choice over a bare full-length `github.sha` tag: it's human-scannable in `az acr repository show-tags`/the Azure Portal (you can tell *when* a revision was built at a glance) and still sorts chronologically as a plain string. Compute it in exactly one step (Task 3) and pass it forward (`$GITHUB_OUTPUT` within a job, or a `needs.<job>.outputs.*` reference across jobs if the work ends up split into more than one job) — never recompute `date -u +...` a second time in Task 4, since two separate invocations a minute apart would silently produce two different strings and the deploy step would reference a tag that was never pushed.
- **No secrets are newly introduced by this story.** Reuse `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_SUBSCRIPTION_ID`/`AZURE_RESOURCE_GROUP_NAME` as-is; do not create, rotate, or re-document them (same instruction Story 1.2 followed for the identity secrets).

### Project Structure Notes

New/modified files this story introduces (nothing else in the existing `src/`/`web/`/`infra/` Structural Seed changes):

```text
energy-tracker-v2/
  .github/
    workflows/
      app-deploy.yml                      # new — push-to-main (no path filter), build+test .NET/web, docker build+push to ACR, deploy Container App revision, health-gate
      infra-deploy.yml                    # unchanged — do not modify or trigger from app-deploy.yml
  infra/
    main.bicep                            # modified — containerAppTargetPort default 80 → 8080
    main.bicepparam                       # modified — containerAppTargetPort 80 → 8080 (or remove the override if the new default suffices)
    modules/
      container-app.bicep                 # modified — re-add the `registries` entry (ACR, identity: system)
      container-registry.bicep            # possibly modified — only if granting AcrPush via a Bicep roleAssignment (see Dev Notes RBAC gap)
    README.md                             # possibly modified — document the AcrPush grant if applicable, mirroring the existing identity-bootstrap doc
```

Story 1.4 (Pull Request Review Workflow) is a separate, later story — do not add a `pull_request` trigger or any read-only `what-if` logic to `app-deploy.yml`; that workflow gets its own file and its own `pull_request`-scoped federated credential.

### References

- [Source: _bmad-artifacts/planning/epics/epic-1-foundation-deployment-household-access.md#Story 1.3] — story statement and acceptance criteria (verbatim origin)
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE.md#AD-13] — single-artifact deployment: the `Api` serves the built SPA from the same container; this story is what produces that real image
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE.md#AD-19] — `/health` is liveness-only, structured logging, secrets via env vars — unchanged by this story
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE.md#AD-6] — scale-to-zero (`minReplicas: 0`) implications for the post-deploy health check's timeout
- [Source: _bmad-artifacts/implementation/1-2-azure-infrastructure-as-code-resource-deployment-pipeline.md] — provisions the ACR/Container App this story targets; its Dev Notes/Review Findings explicitly flag the `registries` entry and `containerAppTargetPort` as deferred to this story; its Dev Agent Record's live-verification issues (regional restriction, empty-secret validation, target-port mismatch, eager ACR-credential validation) are the precedent for expecting live-Azure-only failure modes here too
- [Source: infra/modules/container-app.bicep] — current state: no `registries` entry, `targetPort` param defaults to `8080` but is overridden to `80` by `infra/main.bicepparam` today; `AcrPull` role assignment already exists for the Container App's own identity
- [Source: infra/main.bicep, infra/main.bicepparam] — `containerAppTargetPort`/`placeholderImage` parameters this story must reconcile with a real deployed image
- [Source: .github/workflows/infra-deploy.yml] — established OIDC/secrets/variable pattern (`azure/login@v3`, `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_SUBSCRIPTION_ID`, `vars.AZURE_RESOURCE_GROUP_NAME`, `concurrency:` group) this story's new workflow reuses rather than reinventing
- [Source: Dockerfile] — the exact multi-stage build (`node:22-alpine` → `dotnet/sdk:10.0` → `dotnet/aspnet:10.0`, `ASPNETCORE_HTTP_PORTS=8080`) this story's Docker build step must produce and push unchanged
- [Source: global.json] — `.NET` SDK `10.0.100`/`rollForward: latestFeature`, and the `Microsoft.Testing.Platform` test runner selection `dotnet test` depends on
- [Source: docs/local-development.md#Running tests locally] — canonical test commands (`dotnet test EnergyTracker.sln`, `npm --prefix web run test`, `npm --prefix web run test:e2e`) this story's CI job must mirror for the non-e2e subset
- [Source: web/package.json] — frontend script names (`build`, `test`, `lint`) and the Node-version-relevant `@types/node` version (not the actual runtime version — see Dev Notes)
- [Source: tests/EnergyTracker.Infrastructure.Tests/PostgresMigrationTests.cs, SqlServerMigrationTests.cs] — confirms Testcontainers-based tests need only a Docker daemon (already present on `ubuntu-latest`), not an explicit CI `services:` block
- [Source: https://learn.microsoft.com/en-us/azure/container-apps/revisions-manage] — `az containerapp update --image` as the current, documented way to roll a Container App to a new image/revision — verified during this story's creation
- [Source: https://learn.microsoft.com/en-us/azure/container-registry/container-registry-get-started-azure-cli] — `az acr login` after `azure/login` OIDC auth as the current documented ACR push-credential path — verified during this story's creation

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

### Completion Notes List

### File List

- `.github/workflows/app-deploy.yml` (new)
- `infra/modules/container-app.bicep` (modified — re-added `registries` entry)
- `infra/main.bicep` (modified — `containerAppTargetPort` default 80 → 8080)
- `_bmad-artifacts/implementation/sprint-status.yaml` (modified — story status)
