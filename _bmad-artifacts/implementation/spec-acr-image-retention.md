---
title: 'ACR image retention: weekly workflow keeps the newest 7 images on the Basic tier'
type: 'chore'
created: '2026-10-23'
status: 'done'
review_loop_iteration: 0
baseline_commit: 'c6729ae5300ca460c7888bb24ff1927359f0fa14'
context:
  - '{project-root}/_bmad-artifacts/project-context.md'
  - '{project-root}/infra/README.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Every `app-deploy.yml` run pushes a uniquely tagged image to the Basic-tier ACR and nothing ever deletes one. The registry already holds 125 tagged images plus 250 untagged child manifests (~130 MB each), and storage grows with every deploy. ACR's built-in retention policy is Premium-only, so it cannot be used.

**Approach:** A dedicated workflow, `acr-retention.yml`, runs weekly (Sunday 01:00 UTC, plus manual dispatch with a dry-run option). It runs `acr purge` as an ACR Task (available on Basic) to delete every `energy-tracker` tag except the 7 newest, then sweeps the manifests those deletions orphan. No SKU change, no new Azure resource, no Bicep change, `app-deploy.yml` untouched.

## Boundaries & Constraints

**Always:** Scope is the `energy-tracker` repository only. The newest 7 tags, and every manifest they reference, survive. Before deleting anything the workflow checks that the image the Container App runs is among the 7 newest tags and aborts, deleting nothing, if it is not (failed deploys also push images). The orphan sweep uses `--ago 1h`, so a push in flight is never touched. Own concurrency group, not `infra-app-deploy` (a queued purge would replace a queued deploy there). Deploy identity via OIDC with the `Preflight: identity permissions` step (new mode `retention`) before any deletion. A failed run fails the workflow; no `continue-on-error`.

**Ask First:** Any change to the keep count of 7 or the schedule. A permission denial for the deploy identity on `az acr run` (grant a role deliberately). A dedicated, narrower identity (for example AcrDelete only) instead of the deploy identity.

**Never:** Upgrade the registry SKU. Use `--include-locked`. Purge without a repository filter or without `--ago`. Trigger on `push` or `pull_request`. Touch image tags, the build step or `provenance` settings.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Weekly run | 9 tagged images, running image among the newest 7 | 2 oldest tags deleted, then their indexes and child manifests; 7 images remain | N/A |
| First run | 125 tagged images, 250 untagged manifests | 118 tags deleted, ~354 manifests reclaimed over the passes | Leftovers go on the next run |
| Manual dry run | `dry_run: true` | Same output with `--dry-run`; nothing deleted | N/A |
| Running image too old | 7+ failed deploys pushed newer tags; running tag not in the newest 7 | Workflow fails before any purge, message names the tag | Operator redeploys, then re-runs |
| Deploy mid-push | Child manifests pushed, index tag not yet written | Sweep skips them (younger than 1 h) | N/A |
| Task cannot run | `az acr run` denied or Tasks paused on the subscription | Preflight or purge step fails the run, GitHub notifies | Message names the README section |

</frozen-after-approval>

## Code Map

- `.github/workflows/app-deploy.yml` -- template for the OIDC login, ACR resolve, vars/secrets and pinned action SHAs (copy the SHAs, do not invent new ones)
- `scripts/ci/azure-preflight.sh` -- modes `deploy-app|deploy-infra|pr`; add `retention`
- `tests/EnergyTracker.Architecture.Tests/WorkflowPreflightGuardTests.cs`, `MigrationDeployWorkflowRestorePointGuardTests.cs` -- guard patterns to extend and copy
- `infra/README.md` -- identity/preflight tables and runbook; `_bmad-artifacts/project-context.md` -- CI rules

## Tasks & Acceptance

**Execution:**
- [x] `.github/workflows/acr-retention.yml` -- new: `schedule` cron `0 1 * * 0` + `workflow_dispatch` (`dry_run` boolean), `id-token: write`, own concurrency group, steps: main-only check, checkout, Azure login, resolve ACR, `Preflight: identity permissions`, running-image guard, prune (generated task with `acr purge --filter 'energy-tracker:.*' --ago 0d --keep 7`, then three `acr purge --filter 'energy-tracker:.*' --untagged-only --ago 1h`; each step gets a 30-minute timeout), usage summary -- one agent start-up; the extra passes free manifests only after their index is gone
- [x] `scripts/ci/azure-preflight.sh` -- mode `retention`: read the registry, resource-group scope probe, and a harmless `az acr run ... acr purge --untagged-only --dry-run` that proves the Tasks right -- fails before deletion, not during it
- [x] `tests/EnergyTracker.Architecture.Tests/AcrRetentionWorkflowGuardTests.cs` -- guard: triggers are `schedule` and `workflow_dispatch` only, cron exact; preflight and running-image guard precede the prune step; no `continue-on-error`; `--keep 7`, filter scoped to `energy-tracker:`, every purge has `--ago`, sweeps use `--ago 1h` or more, no `--include-locked`
- [x] `tests/EnergyTracker.Architecture.Tests/WorkflowPreflightGuardTests.cs` -- add the `retention` case for the new workflow
- [x] `infra/README.md` -- identity table row and `retention` preflight rows; section "Registry image retention": why not the Premium policy, schedule (UTC), first-run effect, rollback depth 7, how to run a dry run, what a failure means
- [x] `_bmad-artifacts/project-context.md` -- one CI bullet naming the workflow and its guard

**Acceptance Criteria:**
- Given the weekly run, when it finishes, then only the 7 newest `energy-tracker` tags remain, none of their manifests is deleted, and the running image still exists.
- Given the running image is not among the 7 newest tags, when the workflow runs, then it fails before any deletion.
- Given someone drops `--keep`, `--ago` or adds `--include-locked`, or adds a `push` trigger, when the architecture tests run, then the build fails.

## Design Notes

Live dry-runs against the registry (2026-10-23): `--keep 7` spared the 7 newest indexes and all 14 children; `--untagged-only` with every image tagged deleted 0 manifests; `--ago 1h` is accepted; the running tag is the newest. Each push is an OCI index plus an image and an attestation manifest, so one purge cannot finish: a deleted tag leaves an untagged index, and its children are freed only once the index is gone. Hence the sweeps; anything missed goes next week. GitHub cron is UTC (01:00 UTC is 02:00 or 03:00 in Berlin) and can start late.

## Verification

**Commands:**
- `dotnet test tests/EnergyTracker.Architecture.Tests` -- expected: all pass, including the new guard
- `AZURE_RESOURCE_GROUP_NAME=<rg> scripts/ci/azure-preflight.sh retention` (signed in) -- expected: registry read, app read and task-run probes OK; the scope probe fails for a personal account that sees other resource groups (CI's deploy identity sees only its own)

**Manual checks (if no CLI):**
- After merge, run the workflow once with `dry_run: true`: the log lists 118 tags and nothing is deleted. Then run it for real: `az acr manifest list-metadata -r <acr> -n energy-tracker` shows 7 tagged images and at most 14 untagged manifests, and `/health` answers 200.

## Suggested Review Order

**The purge itself**

- Entry point: one task run, tag purge keeps 7, three sweeps free the orphaned manifests.
  [`acr-retention.yml:130`](../../.github/workflows/acr-retention.yml#L130)

- Refuses to purge when the running image is not among the 7 newest; fails closed.
  [`acr-retention.yml:77`](../../.github/workflows/acr-retention.yml#L77)

- Weekly Sunday 01:00 UTC plus a manual dry run.
  [`acr-retention.yml:11`](../../.github/workflows/acr-retention.yml#L11)

- Own concurrency group, so a purge cannot displace a queued deploy.
  [`acr-retention.yml:28`](../../.github/workflows/acr-retention.yml#L28)

- Run summary shows tag count, leftover untagged manifests and registry usage.
  [`acr-retention.yml:146`](../../.github/workflows/acr-retention.yml#L146)

**Permissions fail before deletion**

- Preflight step runs before the guard and the purge.
  [`acr-retention.yml:66`](../../.github/workflows/acr-retention.yml#L66)

- Dry-run purge proves the ACR Tasks right without deleting anything.
  [`azure-preflight.sh:180`](../../scripts/ci/azure-preflight.sh#L180)

- New `retention` mode: registry and app reads, scope check, task run.
  [`azure-preflight.sh:257`](../../scripts/ci/azure-preflight.sh#L257)

**Guards and docs**

- Pins schedule, `--keep 7`, repository filter, `--ago` bounds, no `--include-locked`.
  [`AcrRetentionWorkflowGuardTests.cs:74`](../../tests/EnergyTracker.Architecture.Tests/AcrRetentionWorkflowGuardTests.cs#L74)

- Triggers limited to schedule and manual; no push or pull request.
  [`AcrRetentionWorkflowGuardTests.cs:28`](../../tests/EnergyTracker.Architecture.Tests/AcrRetentionWorkflowGuardTests.cs#L28)

- Preflight order guard extended to the new workflow.
  [`WorkflowPreflightGuardTests.cs:62`](../../tests/EnergyTracker.Architecture.Tests/WorkflowPreflightGuardTests.cs#L62)

- Runbook: schedule, first-run effect, rollback depth, failure causes.
  [`README.md:385`](../../infra/README.md#L385)

- Project rule for future agents.
  [`project-context.md:138`](../project-context.md#L138)
