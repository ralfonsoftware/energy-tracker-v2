---
baseline_commit: e2fa931
---

# Story 11.5: CI Identity Split and Supply-Chain Hygiene

Status: in-progress

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- Epic 11 "Codebase audit hardening" (planning/epics/epic-11-codebase-audit-hardening.md). Origin: codebase audit 2026-10-10 (docs/codebase-audit-2026-10-10.md) finding S11 (Low). Architecture: AD-21 (Entra-only SQL auth; the CI identity is a contained DB user), AD-19 (secrets only via env/Actions secrets), project-context.md "CI" and "Migrations" rules. Retro 10 note (2026-10-10): the Story 10.1 rollback runbook uses the operator's own Azure login, not the CI identity; keep runbook and permission list consistent. This file cites the AD and audit IDs, which satisfies the spec-per-fix gate (project-context.md). -->
<!-- Branch already exists: feature/11-5-ci-identity-split (current checkout). One PR. -->
<!-- Story 11.6 depends on this story (the Reader-scoped identity must exist before Key Vault holds anything). -->
<!-- NO Auth0/Chrome live gate (no auth flow, no browser behaviour). A DIFFERENT live gate applies: Ralf-run Azure steps (Part R) and a first real run of each changed workflow on GitHub. The story stays in review until Part R is done (same spirit as the Auth0 gate: a unit/guard test alone does not close it). -->
<!-- Mostly CI/infra/docs: almost no application code. Do not touch src/ except Directory.Packages.props-driven fallout (Task A6). -->

## Story

As Ralf (operator),
I want PR pipelines to run with read-only Azure rights and dependencies to be pinned and watched,
so that a branch push can't act as the deploy identity, and vulnerable dependencies surface automatically (audit S11).

## Acceptance Criteria

1. **Given** `pr-review.yml`'s `validate-infra` job, **when** it authenticates to Azure, **then** it uses its own Reader-scoped federated identity (credential subject `pull_request`). The Owner-scoped deploy identity is reachable only from `main`. `infra/README.md` documents both, after Ralf first verifies the current role mapping.
2. **Given** every workflow, **when** it references an action, **then** the action is pinned to a commit SHA with a version comment.
3. **Given** the repo, **when** this story is done, **then** `dependabot.yml` covers nuget, npm, github-actions and docker; a CodeQL workflow covers C# and JS/TS; and CI scans the container image.
4. **Given** current dependencies, **when** this story is done, **then** `npm audit fix` is applied, ASP.NET Core and EF Core are bumped together to the latest 10.0.x patch, and OpenTelemetry is bumped to current. `dotnet list package --vulnerable` and `npm audit --omit=dev` are clean or have documented exceptions.
5. **Given** each workflow identity (the Reader-scoped PR identity and the Owner-scoped deploy identity), **when** this story is done, **then** `infra/README.md` lists the exact permissions each one needs and why. Each workflow starts with a fail-fast preflight step that checks them by behaviour, with a harmless call: for example a read of the production database with the deploy identity, and a denied write with the Reader identity. A missing or excess right fails the run with a clear message before any migration or deployment step. *(Added by Epic 10 retro, 2026-10-10; Ralf's proposal.)*
6. **Implied (not in the epic text; AC #1 cannot work without it):** the PR what-if runs with `--validation-level ProviderNoRbac`. By Microsoft's documentation the default level (`Provider`) requires the same permissions as the deployment itself, so a Reader-only identity would be denied; `ProviderNoRbac` checks read permissions only (Azure CLI 2.76.0+; ubuntu-latest ships 2.90 at the time of writing). If it still fails for a Reader-class identity, the sanctioned fallback is a custom role "Reader + `Microsoft.Resources/deployments/*`" at the resource-group scope. **Never** Contributor/Owner, never `listKeys` rights (`listKeys()` is not evaluated by what-if, per the same docs).
7. **Implied (not in the epic text; "pinned" must stay true):** two executable guards in `EnergyTracker.Architecture.Tests`: (a) every `uses:` in `.github/workflows/*.yml` is a 40-hex SHA with a trailing `# vX.Y.Z` comment (local `./` actions excepted); (b) in `app-deploy.yml`, `infra-deploy.yml` and `pr-review.yml`'s `validate-infra`, the preflight step comes before the first mutating step (list in Dev Notes), mirroring `MigrationDeployWorkflowRestorePointGuardTests`.
8. **Implied (not in the epic text; SHA pins change what Dependabot proposes):** today `dependabot.yml` says actions are pinned to major tags, so Dependabot only proposes major bumps. With SHA pins it proposes minor/patch bumps too, and the grouped-PR rule in `dependabot-automerge.yml` would auto-merge them, and merging to `main` deploys with the Azure identity. Default (Ralf may overrule, see Questions): GitHub Actions updates are **not** auto-merged; `dependabot-automerge.yml` skips `package-ecosystem == 'github_actions'` and `dependabot.yml`'s comment is corrected. The `spec-dependabot-version-updates-auto-merge.md` "Ask First" boundary is about *widening*; this narrows it.
9. **Implied (not in the epic text; docs that would otherwise go stale):** `infra/README.md` ("`azure/login@v2`", the single-identity bootstrap text, the Dependabot section), `dependabot.yml`/`pr-review.yml` comments that mention major-tag pinning or the shared identity, and `_bmad-artifacts/project-context.md` (CI section; the dependency versions in "Technology Stack") describe the new state.

## Tasks / Subtasks

This story has a repo part (A, one PR) and a Ralf-run Azure part (R). They interleave; the order is fixed (see "Rollout order"). Do not merge Part A before R1 is done.

### Part R: Ralf's out-of-band Azure steps (the dev prepares the exact commands in the PR description and `infra/README.md`; the dev does not run them)

- [ ] Task R0: Verify the current role mapping (AC #1, before anything is changed). Read-only. Record the output in Completion Notes.
  - [ ] R0.1 `az identity show -n energy-tracker-devops-uami -g energy-tracker-devops-rg --query "{clientId:clientId, principalId:principalId}"`, then `az role assignment list --assignee <principalId> --all -o table`. Expected per README: **Owner**, scope = the target resource group only. Record any broader scope (subscription) or any extra role: it is an "excess right" finding for the deploy identity and must be fixed or accepted before 11.6.
  - [ ] R0.2 `az identity federated-credential list --identity-name energy-tracker-devops-uami -g energy-tracker-devops-rg -o table`: expected `repo-branch-main` and `repo-pr` (immutable-subject format, see README). This confirms the audit's S11 concern: the `pull_request` subject maps to the **Owner** identity today.
  - [ ] R0.3 `gh secret list` / `gh variable list`: confirm which secrets exist (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `DATABASE_ADMIN_PASSWORD`, `OIDC_CLIENT_SECRET`).
- [ ] Task R1: Create the PR identity (AC #1). Before the Part A PR is merged. Naming below is the default; the dev's README commands must use exactly these so the story and the runbook agree.
  - [ ] R1.1 User-assigned managed identity `energy-tracker-pr-reader-uami` in `energy-tracker-devops-rg` (same resource group as the deploy identity).
  - [ ] R1.2 Role assignment **Reader** (built-in), scope = the target resource group (**not** the subscription). Nothing else. No database user: the PR identity never touches SQL.
  - [ ] R1.3 One federated credential `repo-pr` on the **new** identity, issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, subject = the immutable `pull_request` subject from the README (`repo:ralfonsoftware@121377414/energy-tracker-v2@1327052942:pull_request`; re-check with `gh api repos/ralfonsoftware/energy-tracker-v2 --jq '{owner: .owner.id, repo: .id}'`).
  - [ ] R1.4 Repository secret `AZURE_PR_CLIENT_ID` = the new identity's client id. (`AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID` are shared.) Client ids are not secret in a federated setup, but the repo keeps them as secrets; stay consistent.
  - [ ] R1.5 The old `repo-pr` credential stays on the deploy identity **for now** (open PRs and the first verification still need a working path). It is removed in R3.
- [ ] Task R2: Verify after the Part A PR is merged (AC #1, #5, #6).
  - [ ] R2.1 The merge triggers `app-deploy.yml` (its own file changed): watch the run. The preflight step must pass and print the probe results; then migrations and deploy proceed as usual. Record the run id. If the preflight fails with a message that points at a wrong probe (not a wrong right), fix forward in a small PR before anything else deploys.
  - [ ] R2.2 Verify the PR identity with a throw-away PR that changes a comment in `infra/main.bicep` (this PR's own diff does not touch `.bicep`, so its `validate-infra` skips the Azure steps and proves nothing). `validate-infra` must log in as `energy-tracker-pr-reader-uami`, pass the preflight (read ok, write denied) and complete `what-if` with `ProviderNoRbac`. Record the run id. Close the PR unmerged. If `what-if` is denied, apply the AC #6 fallback role, rerun, and record it.
- [ ] Task R3: Remove the old path, only after R2 is green (AC #1). `az identity federated-credential delete --identity-name energy-tracker-devops-uami -g energy-tracker-devops-rg --name repo-pr --yes`; then `federated-credential list` shows only `repo-branch-main`. Re-run the R2.2 PR check once more (it must still pass: proves it does not depend on the removed credential) and record both lists in Completion Notes.
- [ ] Task R4: Drill the excess-right check (AC #5). Locally, signed in with your own (broad) account, run `scripts/ci/azure-preflight.sh pr` (Task A3). It must **fail** with the "excess right" message. Run `deploy-app`/`deploy-infra` modes the same way where meaningful. Record the output. This proves the denied-write probe actually fires.
- [ ] Task R5: Merge/close checklist: after R3, tick the story's tasks and move the sprint status to `done` (see A9). Until then it stays `review`.

### Part A: Repo changes (one PR, branch `feature/11-5-ci-identity-split`)

Write each guard test first and see it **red** against the current workflows before the implementation tasks (Story 11.1/11.3/11.4 practice). Record the red run in Debug Log References.

- [ ] Task A0: Preconditions (verify, read; do not re-create)
  - [x] A0.1 `git log` shows `e2fa931` (11.4 closed). Dependabot PR **#112** ("nuget-minor-patch group with 15 updates", auto-merge enabled, checks green on 2026-10-11) bumps most of the .NET packages A6 needs; check whether it has merged and rebase this branch on `main` before A6 (see Dev Notes, "Overlap with Dependabot #112"). Do not hand-edit the same lines in parallel.
  - [x] A0.2 Read, completely, before editing: the three workflows, `dependabot.yml`, `dependabot-automerge.yml`, `infra/README.md`, `Dockerfile`, `Directory.Packages.props`, `.config/dotnet-tools.json`, `MigrationDeployWorkflowRestorePointGuardTests.cs`.
  - [x] A0.3 Docker is running (Testcontainers) for the full `dotnet test` in A8.
- [x] Task A1: Guard tests, red first (AC: #2, #5, #7)
  - [x] A1.1 `tests/EnergyTracker.Architecture.Tests/WorkflowActionsArePinnedToCommitShaTests.cs`: scan `.github/workflows/*.yml`, strip whole-line `#` comments (same helper idea as the sibling guard; copy the small `FindRepoRoot`/strip helpers, the siblings do not share one), and for every `uses:` value that does not start with `./` require `@[0-9a-f]{40}` and a trailing `# v\d+(\.\d+){0,2}` comment on the same line. The failure message names file, line and the offending reference. Red today: 14 unpinned references across 4 files.
  - [x] A1.2 `tests/EnergyTracker.Architecture.Tests/WorkflowPreflightGuardTests.cs`: for `app-deploy.yml`, `infra-deploy.yml` and `pr-review.yml` (job `validate-infra`) a step named `Preflight: identity permissions` exists, invokes `scripts/ci/azure-preflight.sh` with the expected mode argument (`deploy-app`, `deploy-infra`, `pr`), and appears **before** every mutating step listed in Dev Notes. Step parsing follows the existing `ReadSteps` approach; `validate-infra` is one job in `pr-review.yml`, so scope the scan to that job's lines. Red today: no such step.
  - [ ] A1.3 Optional but cheap: a guard that `dependabot.yml` lists exactly the ecosystems `npm`, `nuget`, `github-actions`, `docker` (AC #3); skip if it feels like testing config with config.
- [x] Task A2: SHA-pin every action (AC: #2)
  - [x] A2.1 Resolve each action's current release to a commit at implementation time (do not copy SHAs from anywhere, including 2026-10-11 notes below, which go stale): `gh api repos/<owner>/<repo>/releases/latest --jq .tag_name`, then `gh api repos/<owner>/<repo>/commits/<tag> --jq .sha`. Use the **commit** SHA (`/commits/<tag>` dereferences annotated tags; `/git/ref/tags/<tag>` returns a tag object for some actions, e.g. `azure/login`, which is not what `uses:` needs). Format: `uses: actions/checkout@<40-hex> # v7.0.1` (the full `vX.Y.Z`, so Dependabot can update SHA and comment together).
  - [x] A2.2 References to pin (14 in 4 files at baseline): `actions/checkout`, `actions/setup-dotnet`, `actions/setup-node`, `azure/login`, `docker/setup-buildx-action`, `docker/build-push-action`, `dependabot/fetch-metadata`, plus the two new ones from A4/A5 (`github/codeql-action/*`, the scanner). Keep each action on the same **major** it uses today; this is a pin, not an upgrade (the bump to the exact latest minor within that major is fine, the major is not).
  - [x] A2.3 Update the comments that say "pinned to major tags" (`dependabot.yml` line ~43) and the `pr-review.yml`/`app-deploy.yml` comments that say the Node version "matches the Dockerfile's node:22-alpine" (false since `cec1531`, see Dev Notes, "Node drift").
- [x] Task A3: Identity split in the workflows + preflight (AC: #1, #5, #6)
  - [x] A3.1 `pr-review.yml` `validate-infra`: `azure/login` uses `client-id: ${{ secrets.AZURE_PR_CLIENT_ID }}`; the what-if call gets `--validation-level ProviderNoRbac`; rewrite the comment above the login (it currently says "the same identity's second federated credential"). Update the stale comment "Reuses the same three repository secrets".
  - [x] A3.2 **Secrets in the PR job (implied, audit S11 names `DATABASE_ADMIN_PASSWORD`):** what-if does not evaluate secure parameter values, so pass inert placeholders for `DATABASE_ADMIN_PASSWORD` and `OIDC_CLIENT_SECRET` in the PR what-if step instead of the repository secrets. Verify `main.bicepparam` and the `@secure()` params accept them (`@minLength`, complexity); if what-if rejects a placeholder, keep the secret for that one value and record why in Completion Notes. This removes the secrets from the maintained workflow; it does **not** stop someone who can push a branch from editing the workflow to read them (that needs a GitHub Environment with a branch restriction, left out, see Dev Notes and the deferred entry in A9).
  - [x] A3.3 New `scripts/ci/azure-preflight.sh <deploy-app|deploy-infra|pr>` (bash, `set -euo pipefail`, shellcheck-clean; reads `AZURE_RESOURCE_GROUP_NAME`, `AZURE_SUBSCRIPTION_ID` from env). One function per probe, each prints `OK:`/`FAIL:` plus **what right is missing or in excess and which README section lists it**; exits non-zero after running all probes so one run shows every problem. Never prints secret values (discard output of anything that could contain one). Probe table (Dev Notes has the reasoning; verify each command empirically, adapt, and keep the README table in sync):

    | Mode | Must succeed (missing right otherwise) | Must be denied (excess right otherwise) |
    |---|---|---|
    | `pr` | `az group show -n $RG`; `az containerapp list -g $RG` | harmless write: `az tag update --resource-id /subscriptions/$SUB/resourceGroups/$RG --operation Delete --tags energy-tracker-preflight` (deleting an absent key changes nothing even if it is allowed); visible resource groups == exactly `$RG` (`az group list`) |
    | `deploy-app` | ARM reads: `az sql server list`, `az acr list`, `az containerapp list` (all non-empty); **production DB read**: `dotnet ef migrations list` against the SQL Server migrations project with the existing connection string, with the migration step's retry/backoff (firewall rule propagation takes minutes); optional `IS_ROLEMEMBER('db_datareader'/'db_datawriter'/'db_ddladmin')` if `sqlcmd` exists on the runner | visible resource groups == exactly `$RG` (no subscription-wide reach) |
    | `deploy-infra` | `az group show`; `az role assignment list -g $RG` (proves `Microsoft.Authorization/roleAssignments/read`, needed because the template creates role assignments); `az deployment group what-if` at the **default** validation level (the only harmless call that checks deploy-class write rights, including role-assignment write) | visible resource groups == exactly `$RG` |

    A denied probe must match an authorization failure (`AuthorizationFailed`, `does not have authorization`, `Forbidden`, 403); any other error (network, throttling) is a **failed preflight with its own message**, never a pass. Push rights to ACR and `containerapp update` have no harmless probe: say so in the README table (checked by the step that uses them).
  - [x] A3.4 Add the step `Preflight: identity permissions` (exact name, the guard in A1.2 matches it): `app-deploy.yml` **after** "Whitelist runner IP for migration" and **before** "Apply pending EF Core migrations" (the DB read needs the firewall rule); `infra-deploy.yml` after "Azure login (OIDC)" and before any `az deployment` step; `pr-review.yml` inside the already-conditional Azure steps, after login and before "What-if validation". Pass `AZURE_RESOURCE_GROUP_NAME` through `env:` (do not interpolate untrusted values into the script body). Make `az acr login` fail fast too: move it (and "Resolve ACR login server") before the migration step, or add a read probe for it; a registry problem found after migrations have run is the late failure this AC wants gone.
  - [x] A3.5 `app-deploy.yml`/`infra-deploy.yml` `paths:` add `scripts/ci/**`, so a preflight change is exercised by a real run.
  - [x] A3.6 Do not change `permissions:` blocks, the concurrency groups, the migration step, the restore-point block (guarded by `MigrationDeployWorkflowRestorePointGuardTests`, must stay green) or branch protection's required checks.
- [ ] Task A4: Dependabot and CodeQL (AC: #3, #8)
  - [x] A4.1 `dependabot.yml` already has npm (`/web`), nuget (`/`), github-actions (`/`) and docker (`/`) from `7f62b69`/`spec-dependabot-version-updates-auto-merge.md`; **verify, do not rewrite**. Only the github-actions comment changes (A2.3), and the `actions-minor-patch` group stays (one weekly PR for all action bumps).
  - [x] A4.2 `dependabot-automerge.yml` (AC #8): add to both the enable and the "leave for manual review" conditions a `steps.metadata.outputs.package-ecosystem != 'github_actions'` (the `fetch-metadata` output; check the exact value in its README for the pinned version) so Actions PRs always get the notice, never auto-merge. Update its header comment and `infra/README.md`'s Dependabot section.
  - [x] A4.3 New `.github/workflows/codeql.yml`: triggers `push` to `main`, `pull_request` to `main`, weekly `schedule`; matrix `csharp` and `javascript-typescript`, both `build-mode: none` (no .NET build needed; the repo is public, so CodeQL is free; `none` needs NuGet access, which the runner has); `permissions: contents: read, security-events: write, actions: read`; `github/codeql-action/{init,analyze}` pinned per A2; job skipped for `github.actor == 'dependabot[bot]'` on `pull_request` (Dependabot runs get a read-only token and could not upload; `push` to `main` still scans the merged result); default query suite; **not** a required status check and the job id/name must not collide with `build-test-lint`/`validate-infra`. No `id-token`, no secrets.
  - [ ] A4.4 Verify on the PR that the CodeQL run is green or, if C# `none` mode misbehaves on .NET 10 (the docs only say to verify), switch that language to `manual` with `dotnet build EnergyTracker.sln -c Release`. Record which mode shipped and any alerts raised (triage: fix, or `[open]` deferred entry; do not suppress silently).
- [ ] Task A5: Container image scan (AC: #3)
  - [x] A5.1 New `.github/workflows/container-scan.yml`, **separate from `app-deploy.yml`** (the scanner runs third-party code; it must never share a job with `id-token: write`): triggers `pull_request` (paths: `Dockerfile`, `src/**`, `web/**`, `Directory.Packages.props`, `global.json`, the workflow itself), `push` to `main`, weekly `schedule` (new CVEs in unchanged base images). `permissions: contents: read` (+ `security-events: write` only if SARIF upload is used). No secrets.
  - [x] A5.2 Steps: checkout, `docker build -t energy-tracker:scan .` (plain `docker build`; the buildx/`build-push-action` pair is not needed), scan with Trivy (image scan, `severity: HIGH,CRITICAL`, `ignore-unfixed: true`, `exit-code: 1`, `vuln-type: os,library`). Pin the action to a commit **verified against the official advisory**: in March 2026 `aquasecurity/trivy-action` had its version tags force-pushed to malicious commits (75 of 76 tags), so a tag, or a SHA taken from an unverified source, is exactly the wrong thing to trust. If you prefer not to depend on it, `anchore/scan-action` (Grype) is an acceptable substitute; record the choice. Work around Trivy DB rate limits only if they bite (`TRIVY_DB_REPOSITORY` mirror); do not add retries blindly.
  - [ ] A5.3 First run: expect findings in the base images (`node:25-alpine` build stage is not shipped; the shipped layer is `mcr.microsoft.com/dotnet/aspnet:10.0` + the publish output). Triage findings that are fixable by a version bump now; for unfixable or base-image-only ones prefer `ignore-unfixed` over a `.trivyignore` file; every ignore entry gets a reason and a revisit trigger. A permanently red scan is worse than none: if the baseline cannot be made green without ignoring half of it, stop and ask (Questions).
  - [x] A5.4 Do not add the scan to branch protection's required checks in this story (it would block every Dependabot PR on third-party base-image noise); say in the README that Ralf may require it later.
- [x] Task A6: Dependency bumps (AC: #4)
  - [x] A6.1 **Order:** check Dependabot #112 first (Dev Notes, "Overlap"). If merged, rebase, and A6 reduces to the verification and the leftovers; if open, either let it merge first or finish the leftovers on top; never duplicate its edits.
  - [x] A6.2 NuGet, target = the latest 10.0.x patch (10.0.12 on 2026-10-11): `Microsoft.EntityFrameworkCore`, `.Design`, `.Relational`, `.SqlServer`, `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`, `Microsoft.AspNetCore.Authentication.OpenIdConnect`, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Extensions.Logging.Abstractions` (pinned `10.0.0` today; align to the line) **and the `dotnet-ef` tool in `.config/dotnet-tools.json`** (10.0.10 today; it must match EF Core, `app-deploy.yml` runs it against Azure SQL). All the same patch, in one change (project-context: "bump together, never individually"). `Npgsql.EntityFrameworkCore.PostgreSQL` is on its own line (10.0.3): bump only if a 10.0.x with the matching EF patch exists and restore stays clean.
  - [x] A6.3 OpenTelemetry "to current" (1.19.x on 2026-10-11): `Exporter.OpenTelemetryProtocol` and `Extensions.Hosting` (1.19.1), `Instrumentation.AspNetCore`, `.Http`, `.Runtime` (1.19.0), and `OpenTelemetry.Instrumentation.EntityFrameworkCore` to its newest prerelease (`1.19.1-beta.1` exists; still no stable release, so the "deliberately pinned to a beta" rationale stays and its comment in `Directory.Packages.props` is updated). Dependabot skips `.Http`, `.Runtime` and the beta in its group PR, so a version skew between OTel packages is the failure mode to check: `dotnet list package --include-transitive | grep -i opentelemetry` must show one consistent line. Check `Azure.Monitor.OpenTelemetry.AspNetCore` (1.6.0) still restores without NU1608/NU1605 against OTel 1.19.
  - [x] A6.4 Re-verify `Microsoft.Data.SqlClient`'s resolved version before and after (AD-21 / AD-23 note: a bump can move it; the Entra `Authentication=Active Directory Default` mode used by `app-deploy.yml` must keep working). Record before/after in Completion Notes. The only real proof is the first `app-deploy.yml` migration step on Azure after merge: Ralf watches it (R2.1).
  - [x] A6.5 npm: `npm --prefix web audit fix` (**never `--force`**). Expected on the committed lockfile (measured 2026-10-11 in a clean `npm ci`): 9 advisories (7 high, 2 moderate); `audit fix` clears the two moderate (`fast-uri` 3.1.7→3.1.8, `qs` 6.15.3→6.16.0) and leaves **7 high, all dev-only and all reached through the `shadcn` CLI devDependency** (`shadcn` → `@shadcn/registry`, `fast-glob` → `micromatch` → `braces`; `ts-morph` → `@ts-morph/common`). npm's own suggested fix is a major *downgrade* of `shadcn` (to 1.0.0): refuse it. `npm audit --omit=dev` is already clean (0). Record those 7 as `[accepted: ...]` entries in `deferred-work-accepted.md` (reason: dev tooling only, never in the shipped bundle or image; revisit trigger: a `shadcn` release that updates `fast-glob`, or any of them moving to `--omit=dev`). Measure with a clean `npm ci` (a stale local `node_modules` reports different numbers).
  - [x] A6.6 Update `_bmad-artifacts/project-context.md` "Technology Stack" versions that this task changed (it also still says Testcontainers 4.13.0 while the repo is on 4.15.0: fix what you touch, note what you notice) and the OTel/EF lines in "Version constraints".
  - [x] A6.7 Only dependency *versions* change in `src/`. If a bump needs a code change (API drift), keep it minimal and note it; if it needs more than a few lines, stop and ask.
- [x] Task A7: Docs (AC: #1, #5, #9) `infra/README.md`
  - [x] A7.1 Rewrite "One-time identity bootstrap" for **two** identities: `energy-tracker-devops-uami` (deploy; Owner on the target RG; one credential `repo-branch-main`; contained DB user, `infra/sql/grant-entra-db-users.sql`) and `energy-tracker-pr-reader-uami` (Reader on the target RG; one credential `repo-pr`; no DB access). Include the exact `az` commands from R1/R3, the secrets (`AZURE_CLIENT_ID` = deploy, `AZURE_PR_CLIENT_ID` = PR), fix `azure/login@v2` → current major, and mark the section "done 2026-xx-xx, reference only" with R0's verified output.
  - [x] A7.2 New section "Workflow identities and their permissions": one table per identity with columns right / needed by / why / how the preflight checks it (the table from A3.3 made human-readable), including what is deliberately **not** probed (ACR push, container-app update) and the Reader-plus-`ProviderNoRbac` rationale (AC #6) and fallback.
  - [x] A7.3 "Rolling back a bad migration": keep it consistent (Retro 10 note). It already uses the operator's own login; add one sentence saying the runbook does not use either CI identity and does not depend on the split, and update step 3's "Connect as the CI identity" only if wording now misleads.
  - [x] A7.4 Add a short "Scanning and supply chain" section: SHA pins and how to update them (Dependabot PR, review the SHA diff, never merge an Actions PR unread), CodeQL, the image scan, the npm audit gate, where findings are triaged.
  - [x] A7.5 `docs/codebase-audit-2026-10-10.md` is a dated snapshot: leave it untouched.
- [ ] Task A8: Regression and finish Part A
  - [x] A8.1 `dotnet restore EnergyTracker.sln` (NuGet Audit gate green), `dotnet build`, full `dotnet test` (Docker running), `npm --prefix web ci && npm --prefix web run build && npm --prefix web run test && npm --prefix web run lint` green on the bumped dependencies.
  - [x] A8.2 `actionlint` (or equivalent YAML syntax check) on all workflows if available; at minimum every YAML parses. `shellcheck scripts/ci/azure-preflight.sh`.
  - [x] A8.3 CI-only npm audit gate (implied, parity with the NuGet Audit gate): `pr-review.yml` `build-test-lint` gets `npm --prefix web audit --omit=dev --audit-level=moderate` after "Install frontend dependencies". The step name does not change the job id. If it proves flaky (registry outage), make that step `continue-on-error` with a warning and say so; do not drop the check silently.
  - [ ] A8.4 Open the PR (title `ci: split CI identities, pin actions, add CodeQL and image scan (Story 11.5)`), body lists R1-R5 and which are done, references Story 11.5, AD-21 and audit S11, and carries the commit/PR attribution. Do not merge before R1.
- [ ] Task A9: Close-out
  - [x] A9.1 Append to `deferred-work.md` (end of file, project-context convention, `[open]` marker): (a) Node drift (Dev Notes); (b) PR-branch access to repo secrets needs a GitHub Environment with a branch restriction (`DATABASE_ADMIN_PASSWORD`, `OIDC_CLIENT_SECRET`) and Dockerfile base images pinned by digest, both out of scope here; (c) anything CodeQL/Trivy found that was deferred.
  - [ ] A9.2 Sprint status `11-5-ci-identity-split-supply-chain-hygiene` stays `review` until R2 and R3 are done and recorded, then `done`. Tick the tasks.

## Dev Notes

### What this fixes and why it is safe to change

- **S11 (Low, verified):** `pr-review.yml` `validate-infra` logs in with `AZURE_CLIENT_ID`, the **same** identity `app-deploy.yml`/`infra-deploy.yml` use (Owner on the target RG), through its second federated credential `repo-pr` (subject `pull_request`). Anyone who can push a branch to this repo can add a workflow step that runs arbitrary `az` commands as Owner, and the identity is also a contained SQL user with `db_datawriter`/`db_ddladmin` on production. The fix is an identity with *less power*, not a guard in the workflow (workflow code on the branch is attacker-controlled by construction).
- Everything else in S11 is hygiene: Actions pinned to tags, no CodeQL, no image scan, 9 npm advisories, patch-level lag (10.0.10 vs 10.0.12, OTel 1.17 vs 1.19). Dependabot version updates already exist (`7f62b69`); the audit text predates that.
- The repo is **public** (`gh repo view`): CodeQL is free, fork PRs never receive OIDC tokens or secrets, and `secret_scanning` plus push protection are already enabled.

### Rollout order (this is the part that breaks things if done wrong)

1. R0 (read-only) -> R1 (new identity, Reader, `repo-pr` credential on it, `AZURE_PR_CLIENT_ID`). Nothing in production changes.
2. Merge Part A. The merge itself changes `app-deploy.yml`, which triggers a real production deploy: the first execution of the new preflight is a real run. A wrongly written probe fails the run **before** migrations, so production is not changed (fail-closed), but the deploy is blocked until a fix-forward PR; the `workflow_dispatch` trigger exists for re-runs.
3. R2.2 (throw-away infra PR) proves the PR path with the new identity. Only then R3 removes `repo-pr` from the deploy identity. Doing R3 first would make every infra PR's `validate-infra` fail on login.
4. PRs opened before the merge run the workflow from their merge commit; after the merge and a rebase they use the new one. Until R3 both credentials exist, so nothing breaks in between.

### Current state of each file you will touch (read before editing)

- `.github/workflows/pr-review.yml` (196 lines): `build-test-lint` (no `name:`, job id is the required check context; do not rename) and `validate-infra` (job-level `id-token: write`; steps: checkout `fetch-depth: 0`, "Check for infra changes", "Check if PR is from a fork", a notice step with `if: always()`, "Azure login (OIDC)" + "Resolve current Container App image" + "What-if validation", the last three guarded by `infra_changed == 'true' && is_fork == 'false'`). The what-if step passes `DATABASE_ADMIN_PASSWORD`, `OIDC_CLIENT_SECRET` and vars via `env:` for `main.bicepparam`'s `readEnvironmentVariable`. Workflow-level `concurrency` cancels superseded runs. `node-version: '22'`.
- `.github/workflows/app-deploy.yml` (402 lines): single job `build-test-deploy`; step order: verify main, checkout, setup-dotnet, restore, build, setup-node, npm ci, build frontend, test frontend, test .NET, buildx, **Azure login**, Resolve SQL Server name, Determine runner IP, **Whitelist runner IP for migration**, **Apply pending EF Core migrations** (restore-point block, retries x5 with 30 s backoff, `Command Timeout=600`), Remove runner IP (`always()`), Resolve ACR login server, **ACR login**, Compute image tag, **Build and push image**, Resolve Container App name, Ensure ACR pull credential and target port, Capture current image, **Deploy new revision**, Health check (with rollback). `paths:` filter includes the workflow file itself. Job-level `id-token: write`.
- `.github/workflows/infra-deploy.yml` (135 lines): job `deploy` guarded `if: github.ref == 'refs/heads/main'`; Azure login, Resolve current image, "What-if (manual runs only)", Deploy. Shares `concurrency: infra-app-deploy` with `app-deploy.yml`.
- `.github/workflows/dependabot-automerge.yml` (55 lines): `pull_request`, `github.actor == 'dependabot[bot]'`, `dependabot/fetch-metadata@v3`, `gh pr merge --auto --squash` when `dependency-group != ''` or update-type patch/minor. `permissions: contents: write, pull-requests: write` (job runs no third-party code with secrets beyond the pinned fetch-metadata).
- `.github/dependabot.yml` (71 lines): four ecosystems, weekly Monday 06:00 Europe/Berlin, 3-day cooldown, one `*-minor-patch` group each (`open-pull-requests-limit: 10` on npm/nuget).
- `infra/README.md` (376 lines): "Rolling back a bad migration" runbook (operator login), "One-time identity bootstrap (already done)" with the single-identity text and `azure/login@v2`, branch protection (`build-test-lint` and `validate-infra` required, `strict: true`), Dependabot section.
- `Directory.Packages.props`: central versions (EF 10.0.10 family, OTel 1.17.0, `OpenTelemetry.Instrumentation.EntityFrameworkCore` `1.17.0-beta.1`, Testcontainers 4.15.0). `.config/dotnet-tools.json`: `dotnet-ef` 10.0.10. `Directory.Build.props`: NuGet Audit gate (`moderate`, NU1902-NU1904 as errors).
- `Dockerfile`: `node:25-alpine` (frontend build), `mcr.microsoft.com/dotnet/sdk:10.0` (build), `mcr.microsoft.com/dotnet/aspnet:10.0` (runtime). Runs as root today (Story 11.11 changes that, so the scan will report it if configured to; do not fix it here).

### What must be preserved (regression guard)

- The required status-check contexts `build-test-lint` and `validate-infra` keep their names; no new job is added to `pr-review.yml` (a new `name:`d job could never be required without a branch-protection change, and branch protection is not changed here).
- `app-deploy.yml`: migrations before "Deploy new revision"; the restore-point summary written before the first `dotnet ef database update` (`MigrationDeployWorkflowRestorePointGuardTests`); the `always()` firewall-rule cleanup; the health-check rollback; `Command Timeout=600`; the concurrency group `infra-app-deploy`.
- `infra-deploy.yml`: re-applies the currently running image (Story 1.6); the manual-only what-if keeps working.
- A fork PR still gets no Azure access (the fork guard stays, and stays visible in YAML).
- The deploy identity's contained SQL user and `grant-entra-db-users.sql` are untouched (AD-21). The PR identity gets **no** database user.
- Dependabot auto-merge for npm/nuget/docker patch+minor keeps working exactly as before.
- No secret values in logs: the preflight discards output of any call that could return a key or password.

### Mutating steps the preflight must precede (guard A1.2)

`app-deploy.yml`: "Apply pending EF Core migrations", "Build and push image", "Ensure ACR pull credential and target port", "Deploy new revision". `infra-deploy.yml`: "What-if (manual runs only)" may follow it, "Deploy" must. `pr-review.yml` `validate-infra`: "What-if validation". The firewall-rule creation in `app-deploy.yml` is a deliberate exception: the DB read probe needs it (a harmless, temporary, run-scoped rule that the `always()` step removes).

### Design decisions already made (do not re-litigate)

- **Two identities, not one with fewer credentials.** The PR identity is a separate UAMI with Reader on the target RG; the deploy identity keeps Owner and only the `repo-branch-main` credential. (Epic 11 text, sprint change proposal §2.)
- **PR what-if uses `ProviderNoRbac`; fallback = Reader + `Microsoft.Resources/deployments/*` custom role at RG scope; never Contributor/Owner** (AC #6). Source: Microsoft Learn, "Bicep what-if" (validation levels; "The what-if operation has the same permission requirements" as the deployment; list functions are not evaluated).
- **The preflight is a script, not inline YAML**: testable locally (R4), shellcheck-able, one place for all three workflows. The guards test the *workflow* wiring; the script is proven by the R4 drill and by the live runs.
- **SHA pin + `# vX.Y.Z` comment** (Dependabot-compatible form). Pin to the same major.
- **GitHub Actions updates are never auto-merged** (AC #8 default). Cooldown stays 3 days.
- **Scanner and CodeQL live in their own workflows with no secrets and no `id-token`.** They are not required checks and not part of the deploy path (a new base-image CVE must not block an unrelated deploy; the weekly schedule is what surfaces it).
- **No Docker digest pinning, no `HEALTHCHECK`, no `USER` change** here (audit S5/S7 belong to 11.11).

### Technical details the dev must get right

- **Azure CLI on the runner:** `ProviderNoRbac` needs `az` >= 2.76.0 (`az version`). Print the version in the preflight step when it fails. `ubuntu-latest` was 2.90.0 on 2026-10-11.
- **Immutable OIDC subjects:** the repo uses GitHub's immutable subject format (`repo:ralfonsoftware@121377414/energy-tracker-v2@1327052942:pull_request`); the plain `repo:owner/repo:pull_request` form will not match. The README already explains this; reuse its wording.
- **Reader sees what the template reads:** what-if for `main.bicep` reads existing resources with `Microsoft.*/read`. `storage-queue.bicep` and `container-apps-environment.bicep` call `listKeys()`; what-if does not evaluate resource functions, so no key-listing right is needed. If a what-if run nevertheless demands one, **stop**: handing the PR identity a key-listing right defeats the purpose; the decision is Ralf's.
- **`az group list` as an "excess reach" probe** returns only the groups the principal can see. With Owner/Reader scoped to one RG it must be exactly `$AZURE_RESOURCE_GROUP_NAME`; a subscription-level role shows every group. It is read-only and secret-free.
- **Denied-write probe:** `az tag update --operation Delete` on the RG's tag resource of a key that does not exist is a no-op when allowed and `AuthorizationFailed` when not. Verify once with your own account and with the Reader identity; if `az tag` behaves differently, choose another no-op write (e.g. deleting a non-existent firewall rule is **not** suitable: it needs SQL server scope, not the RG's tag resource). The probe must not create anything.
- **DB read probe and firewall propagation:** a new firewall rule can take up to ~5 minutes (existing comment in `app-deploy.yml`); reuse the migration step's loop shape (5 attempts, 30 s). A failure after the retries is a hard fail with a message naming "firewall propagation or missing contained-user rights".
- **`dotnet ef migrations list` needs the tool restored** (`dotnet tool restore`, the migration step does it first); the preflight step must do the same, or run after a shared restore.
- **YAML gotchas:** keep `uses:` values on one line with the comment; `actions/*` pins do not change inputs. After pinning, run the whole `pr-review.yml` on the PR: a pin to the wrong commit shows up as a failing or changed step immediately.
- **CodeQL:** `github/codeql-action/init` and `/analyze` must use the **same** pinned commit; `analyze` needs `category` only when running several configurations. Both are tagged `v4.x.y`; resolve with A2.1.
- **Trivy:** scan the image you built (`image-ref: energy-tracker:scan`), `scanners: vuln` (secret/misconfig scanning is out of scope), `format: table` for the log plus SARIF only if you also grant `security-events: write`.

### Overlap with Dependabot #112

On 2026-10-11 Dependabot PR #112 (`nuget-minor-patch-256fda9b45`, auto-merge enabled, `build-test-lint` and `validate-infra` green) updates: `Azure.Storage.Queues` 12.22.0→12.28.1, `dotnet-ef` 10.0.10→10.0.12, `OpenIdConnect`, `DataProtection.EntityFrameworkCore`, `Mvc.Testing`, `EntityFrameworkCore.Design`/`Relational`/`SqlServer` → 10.0.12, `Microsoft.Extensions.Logging.Abstractions` 10.0.0→10.0.12, `NSubstitute` 6.2.0, `OpenTelemetry.Exporter.OpenTelemetryProtocol`/`Extensions.Hosting` 1.19.1, `Instrumentation.AspNetCore` 1.19.0 (13 of 15 packages listed; read the PR for the other two). It does **not** touch `Microsoft.EntityFrameworkCore` (verify), `OpenTelemetry.Instrumentation.Http`/`Runtime` (1.19.0 available), the EF-Core OTel beta (`1.19.1-beta.1`), `Testcontainers.*` (4.16.0) or the xunit major (#113, manual). So even if #112 merges first, the AC "bumped together" is **not** met until the leftovers are aligned: the likely end state to check is `Microsoft.EntityFrameworkCore` = Design = Relational = SqlServer = DataProtection.EFCore = OpenIdConnect = Mvc.Testing = dotnet-ef = same 10.0.x, and all OTel packages on one 1.19 line.

### Node drift (found while reading; not fixed here)

`cec1531` (Dependabot, 2026-10-11) moved the Dockerfile frontend stage to `node:25-alpine`, but `pr-review.yml` and `app-deploy.yml` still test and build the frontend with `node-version: '22'`, and their comments claim the versions match exactly. CI therefore no longer builds what the image builds (Node 25 is also not an LTS line). Out of scope for 11.5 (the Dockerfile is 11.11's; the decision is Ralf's: pin the Dockerfile back, or move CI). This story only stops repeating the false comment (A2.3) and records the drift in `deferred-work.md` (A9.1). If Ralf decides during review, the fix is one line in each place.

### Considered and left out (do not add to this story)

- GitHub Environments with required reviewers or branch restrictions to protect repo secrets from PR branches (real fix for "a pusher can read `DATABASE_ADMIN_PASSWORD`"; changes federated-credential subjects to `environment:`; deferred entry only).
- Docker base-image digest pinning, `HEALTHCHECK`, non-root user (11.11).
- Making CodeQL/scan required checks, SARIF-based PR annotations tuning, SBOM generation, artifact attestations, `dotnet format`/analyzers (11.18).
- Replacing the shadcn CLI devDependency to clear the 7 dev-only advisories.
- Moving secrets to Key Vault references or changing how `main.bicepparam` reads secrets (11.6 introduces Key Vault for Data Protection only).
- A major bump of `xunit.v3.mtp-v2` (4.x, PR #113) or the other open major Dependabot PRs (#107-#111): separate, manual.
- Changing `Command Timeout`, retry counts, or the restore-point block.

### Architecture compliance

- **AD-21:** the deploy identity stays a contained Entra database user (`grant-entra-db-users.sql`); the new PR identity gets none. Connection-string mode `Active Directory Default` is unchanged; re-verify after the `Microsoft.Data.SqlClient` resolution check (A6.4).
- **AD-19:** secrets only via Actions secrets/env; no value is committed, echoed or baked into an image; the preflight discards secret-bearing output.
- **AD-13 / project-context CI rules:** `pr-review.yml` never deploys; `build-test-lint` has no `name:` override; deploys only from `app-deploy.yml`/`infra-deploy.yml` on `main`.
- **project-context Migrations / NuGet Audit gate:** do not suppress NU190x to unblock a bump; bump or replace. EF Core family bumped together.
- **project-context process gates:** this story file is the spec trail for the S11 fixes; the PR links it. Amend ACs at decision time (if Ralf changes AC #6/#8 defaults in review, edit the AC here with `AMENDED <date, decision>` and the struck-through old text).
- **Conventions:** Conventional-Commits prefix (`ci:`/`build(deps):`/`docs:`), reference "Story 11.5, audit S11" in commits and the PR.

### Testing standards (from project-context.md)

- Architecture guards: `Snake_case_with_underscores` names, Shouldly, `{Subject}Tests` file per class, text scanning with whole-line comments stripped, no YAML library (dependency-light sibling precedent). Assert messages that tell the next person what to do.
- No Testcontainers needed for new tests; the full `dotnet test` still runs them for regression (Docker up).
- Mutation-check each guard once (revert one pin / move the preflight after the migration step, see the test fail, restore) and note it in Completion Notes (Epic 10 retro practice).
- Live evidence replaces unit tests where behaviour is on GitHub/Azure (R2, R4): record run ids and the printed probe output.

### Project Structure Notes

- New files: `scripts/ci/azure-preflight.sh`, `.github/workflows/codeql.yml`, `.github/workflows/container-scan.yml`, two Architecture tests. Everything else is edits in place. `scripts/` already holds `run-api.sh`, `migrate.sh`, `add-migration.sh`; `ci/` is a new subfolder (document it in `docs/local-development.md` only if it lists scripts; otherwise skip).
- No application code, no migration, no frontend change (aside from lockfile bumps).

### Previous work intelligence

- **Story 10.1:** the restore-point summary and rollback runbook; its guard test is the template for A1.2. The runbook uses the operator's login; keep it that way.
- **spec-dependabot-version-updates-auto-merge (2026-10-11):** its "Ask First" covers widening; "Known Risks" says the test suite is the only gate for auto-merged updates because merges to `main` deploy. AC #8 narrows for Actions.
- **spec-nuget-audit-ci-gate / spec-ssh-net-cve-fix (Epic 6 retro):** precedent for turning an advisory into a failing restore; AC #4 adds the npm counterpart (A8.3).
- **Story 11.4 (done):** deploy N/N+1 ran through `app-deploy.yml` with Command Timeout 600; the migration step is load-bearing. Do not reorder around it, only insert the preflight before it.
- **Story 11.1-11.4 practice:** tests red first, record the red run; Ralf-run steps are listed explicitly and the story is not `done` before they are; tick Completion Notes with run ids.
- **Git (last commits on `main`):** `e2fa931` 11.4 close-out, `963175d`/`65e5478` 11.4 deploys N+1/N, `940b9b0` `dependabot/fetch-metadata` 2→3, `cec1531` Node 22→25 in the Dockerfile, `95b57a9` npm group bump, `7f62b69` Dependabot config.

### References

- [Source: _bmad-artifacts/planning/epics/epic-11-codebase-audit-hardening.md#Story 11.5]
- [Source: docs/codebase-audit-2026-10-10.md#S11 and the "Soon" row in the fix-order table]
- [Source: _bmad-artifacts/planning/sprint-change-proposal-2026-10-10.md] (11.6 depends on 11.5; Ralf verifies the PR what-if identity's role)
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-21, #AD-19, #AD-23 (SqlClient note)]
- [Source: _bmad-artifacts/project-context.md] ("CI", "Migrations", NuGet Audit gate, process gates)
- [Source: _bmad-artifacts/implementation/10-1-migration-safety-on-deploy.md], [Source: _bmad-artifacts/implementation/spec-dependabot-version-updates-auto-merge.md], [Source: _bmad-artifacts/implementation/epic-10-retro-2026-10-10.md]
- [Source: https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/deploy-what-if] (required permissions, `--validation-level`, unevaluated `listKeys()`)
- [Source: docs/local-vs-azure-deltas.md] (read before touching the deploy pipeline)

## Questions for Ralf (defaults above apply unless you overrule; none block starting Part A)

1. **PR identity role (AC #6):** Reader + `ProviderNoRbac`, with a pre-approved fallback of a custom role "Reader + `Microsoft.Resources/deployments/*`" on the RG. OK, or do you want to approve the fallback only if needed?
2. **Actions updates not auto-merged (AC #8):** one weekly PR for all action bumps, merged by hand after reading the SHA diff. OK?
3. **Image scanner:** Trivy (pinned to an advisory-verified commit, own workflow, no secrets) vs Grype (`anchore/scan-action`). Default Trivy.
4. **Names:** identity `energy-tracker-pr-reader-uami`, secret `AZURE_PR_CLIENT_ID`. OK?
5. **Node drift:** pin the Dockerfile back to an LTS Node or move CI to match? (Not fixed here.)

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5.5 (claude-sonnet-5-5)

### Debug Log References

- **Red run (A1), 2026-10-11:** `dotnet test tests/EnergyTracker.Architecture.Tests -c Release` against the unchanged workflows: 29 total, 4 failed (`WorkflowActionsArePinnedToCommitShaTests.Every_action_reference_is_pinned...` listing every unpinned `uses:`, and the three `WorkflowPreflightGuardTests` with "has no step named 'Preflight: identity permissions'"). After A2/A3: 29 passed.
- **Mutation checks (guards):** reverting one pin (`actions/checkout@v7` in `infra-deploy.yml`) failed the pin guard naming `infra-deploy.yml:67`; moving the `app-deploy.yml` preflight step after the migration step failed the preflight guard ("must come BEFORE 'Apply pending EF Core migrations'"). Both restored; 29 pass.
- **Preflight script logic:** `shellcheck` clean (docker `koalaman/shellcheck`); `actionlint` (docker `rhysd/actionlint`) clean for all workflows except one pre-existing SC2046 warning on the `az acr login` line (unchanged by this story, only moved). The `pr` mode was exercised offline with a fake `az` on PATH: pass; tag write allowed -> FAIL "ALLOWED. Excess right"; non-authorization error -> FAIL (not a pass); extra visible resource group -> FAIL; CLI 2.60.1 -> FAIL; target group invisible -> FAIL. `deploy-app` and `deploy-infra` modes and the real `az tag update --operation Delete` behaviour are NOT exercised: they need Azure (R2/R4).
- **Baseline before bumps (A6.4):** `Microsoft.Data.SqlClient` resolves to 6.1.4; EF Core 10.0.10; OTel 1.17.0; `dotnet list package --vulnerable` clean.
- **npm baseline/after (A6.5), clean `npm ci`:** 9 advisories (7 high, 2 moderate) -> after `npm audit fix` (no `--force`) 7 high, all dev-only through the `shadcn` CLI; `npm audit --omit=dev`: 0 both times. Lockfile diff: `fast-uri` 3.1.7->3.1.8, `qs` 6.15.3->6.16.0.

### Completion Notes List

- **Action pins (A2):** 14 existing references pinned, same majors: checkout v7.0.1, setup-dotnet v6.0.0, setup-node v7.1.0, azure/login v3.1.0, setup-buildx v4.4.1, build-push v7.4.0, fetch-metadata v3.1.0 (SHAs resolved with `gh api repos/<r>/commits/<tag>` on 2026-10-11). New: `github/codeql-action` v4.38.3 (init and analyze same commit), `aquasecurity/trivy-action` v0.36.0.
- **Scanner choice (Question 3, default kept): Trivy**, pinned to `ed142fd0673e97e23eac54620cfb913e5ce36c25` (v0.36.0). Verified against the March 2026 incident (issue aquasecurity/trivy-action#541): maintainers state tags before 0.35.0 were compromised and restored, 0.35.0 is safe; v0.36.0 (2026-04-22) is a signed commit by a maintainer, an ancestor of `master`, and its `action.yaml` pins `setup-trivy` and `actions/cache` to SHAs. Default Trivy version `v0.70.0` (after the second incident). Grype (`anchore/scan-action` v7.4.2 `27805bf...`) is the substitute if Ralf prefers.
- **Preflight placement (A3.4):** `app-deploy.yml`: after "Whitelist runner IP for migration", before the migration. "Resolve ACR login server" and "ACR login" moved to right after "Azure login (OIDC)" so a registry problem fails before the migration. `infra-deploy.yml`: right after login. `pr-review.yml`: after login, inside the already-conditional Azure steps.
- **PR what-if (A3.1/A3.2):** `--validation-level ProviderNoRbac`; `DATABASE_ADMIN_PASSWORD` and `OIDC_CLIENT_SECRET` are inert placeholders. Whether what-if accepts the placeholders and the Reader-only identity is UNVERIFIED until R2.2 (needs Azure). Fallback role in AC #6 / README if denied.
- **AC #8:** `dependabot-automerge.yml` skips `package-ecosystem == 'github_actions'` (spelling confirmed from Dependabot branch names `dependabot/github_actions/...`); the notice step now covers Actions PRs. Not exercised until an Actions PR arrives.
- **Node drift resolved (Ralf's decision, 2026-10-11):** `node-version` in `pr-review.yml` and `app-deploy.yml` is now `'25'` to match the Dockerfile's `node:25-alpine`; the deferred-work entry was deleted as resolved. Verification in `node:22-alpine` and `node:25-alpine` containers: on Node 25 the unchanged suite failed 18 tests in `color-scheme.test.ts` because Node 25 enables its own global `localStorage`, shadowing jsdom's (`window.localStorage.getItem is not a function`). Fix: `execArgv: ['--no-experimental-webstorage']` in the vitest config in `web/vite.config.ts`. After it: 569/569 tests pass on both Node 22 and Node 25, `tsc -b` clean, `npm run build` ok, lint 0 errors, `npm audit --omit=dev` 0. Playwright e2e (`npm run test:e2e`, CI mode, Chromium) run in a `node:25-bookworm` container after `npm run build`: 97 passed, none flaky. Outside the story's task list; added at Ralf's request.
- **A6 NuGet/OTel (done after #112):** Ralf merged Dependabot #112 (EF Core family, `dotnet-ef`, DataProtection.EFCore, OpenIdConnect, Mvc.Testing, Logging.Abstractions all 10.0.12; OTel 1.19.x incl. Http/Runtime; Azure.Storage.Queues 12.28.1; NSubstitute 6.2.0). This branch was fast-forwarded onto `main` (be2c333) and adds the leftover `OpenTelemetry.Instrumentation.EntityFrameworkCore` 1.19.1-beta.1 (newest; still beta-only, comment updated). `Npgsql.EntityFrameworkCore.PostgreSQL` stays 10.0.3 (newest 10.x). Testcontainers 4.16.0 left to Dependabot (not in AC #4). Checks: restore clean (NuGet Audit gate), `dotnet list package --vulnerable --include-transitive` clean for all 10 projects, one consistent OTel 1.19 line (no NU1608/NU1605 with `Azure.Monitor.OpenTelemetry.AspNetCore` 1.6.0), no `src/` code change.
- **SqlClient (A6.4):** `Microsoft.Data.SqlClient` resolved **6.1.4 before, 6.1.6 after** (moved by the EF Core 10.0.12 bump). The `Authentication=Active Directory Default` migration path has not run against Azure with 6.1.6 yet; the first proof is the post-merge `app-deploy.yml` migration step (R2.1).
- **Finding on #112 and deploys:** #112 was merged by `app/github-actions` (auto-merge via `GITHUB_TOKEN`), and GitHub does not start push-triggered workflows for pushes made with `GITHUB_TOKEN`. No `App Deploy` run exists for be2c333 (the run Ralf linked, 38162825854, is a "Dependabot Updates" run). So the new package versions are on `main` but not deployed; the next human merge (this PR) deploys them together with the CI changes. Dependabot auto-merged PRs earlier may have behaved the same way.
- **A8.1 regression:** `dotnet build` 0 errors; full `dotnet test` 1109 passed, 0 failed (Docker up; Infrastructure.Tests alone took 8 min 37 s); frontend on the new lockfile: `npm ci`, build, 569/569 tests, lint 0 errors; `npm audit --omit=dev` 0; full `npm audit` 7 high, all the accepted `shadcn` chain (unchanged by the TypeScript 7 bump). `project-context.md` updated (EF 10.0.12, OTel 1.19.x, NSubstitute 6.2.0, Testcontainers 4.15.0, Mvc.Testing; plus new CI rules). Noticed, not changed: its frontend versions (React 19.2, TypeScript ~6.0, Vite 8.2, Playwright 1.62, oxlint 1.75) lag `web/package.json` (React ^19.3, TypeScript ~7.0, Vite ^8.3, Playwright ^1.64, oxlint ^1.87).
- **Not done / waiting:**
  - **A4.4, A5.3:** the first real CodeQL and Trivy runs happen on the PR; findings triage and C# build-mode decision are recorded then.
  - **A8.4, A9.2:** the PR, then close-out after Part R.
  - **Part R (R0-R5):** Ralf-run Azure steps; commands are in `infra/README.md`. README has a marked blank for R0's verified output.

### File List

- `.github/workflows/app-deploy.yml` (modified)
- `.github/workflows/infra-deploy.yml` (modified)
- `.github/workflows/pr-review.yml` (modified)
- `.github/workflows/dependabot-automerge.yml` (modified)
- `.github/workflows/codeql.yml` (new)
- `.github/workflows/container-scan.yml` (new)
- `.github/dependabot.yml` (modified, comments only)
- `scripts/ci/azure-preflight.sh` (new)
- `tests/EnergyTracker.Architecture.Tests/WorkflowActionsArePinnedToCommitShaTests.cs` (new)
- `tests/EnergyTracker.Architecture.Tests/WorkflowPreflightGuardTests.cs` (new)
- `infra/README.md` (modified)
- `web/package-lock.json` (modified, `npm audit fix`)
- `web/vite.config.ts` (modified, vitest `execArgv` for Node 25)
- `Directory.Packages.props` (modified, OTel EF Core beta 1.19.1-beta.1 + comment)
- `_bmad-artifacts/project-context.md` (modified)
- `_bmad-artifacts/implementation/deferred-work.md` (modified)
- `_bmad-artifacts/implementation/deferred-work-accepted.md` (modified)
- `_bmad-artifacts/implementation/sprint-status.yaml` (modified, status in-progress)
- `_bmad-artifacts/implementation/11-5-ci-identity-split-supply-chain-hygiene.md` (this file)

### Change Log

- 2026-10-11: Guard tests (red first), SHA pins, identity split + preflight in the three workflows, Dependabot Actions auto-merge exclusion, CodeQL and container scan workflows, npm audit fix and CI audit gate, infra/README.md rewrite, deferred-work entries. NuGet bumps and Part R pending.
- 2026-10-11: CI Node 22 -> 25 to match the Dockerfile (Ralf's request); vitest `execArgv: ['--no-experimental-webstorage']` so the suite passes on Node 25; Node drift entry removed from deferred-work.md.
- 2026-10-11: Rebased onto main after Dependabot #112; OTel EF Core beta bumped to 1.19.1-beta.1; project-context.md updated; full regression green.
