---
baseline_commit: 690c68bf6b42e9f2c46c48bcf8acfdf618a0ca2d
---

# Story 1.6: CI/CD Deploy Idempotency — Container App Image Preservation

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a platform operator,
I want `infra-deploy.yml` to never revert the Container App to the placeholder image,
so that running an infrastructure-only change (e.g. rotating a secret) doesn't silently take production down.

## Acceptance Criteria

1. **Given** the Container App is currently running a real deployed image (not the placeholder), **when** `infra-deploy.yml` runs (push to `infra/**` or manual `workflow_dispatch`), **then** it continues running that same image afterward — `infra-deploy.yml` never overwrites `properties.template.containers[0].image` back to `placeholderImage`.
2. **Given** a brand-new environment with no image ever deployed yet, **when** `infra-deploy.yml` runs for the first time, **then** it still provisions the Container App successfully using the placeholder image (Story 1.2's original bootstrap behavior is preserved).
3. **Given** `infra-deploy.yml` and `app-deploy.yml` are both idempotent, **when** either runs multiple times in sequence in any order, **then** the final state is always: Container App running the most recently app-deployed image, with whatever secrets/config `infra-deploy.yml` most recently applied.

## Tasks / Subtasks

- [x] Task 1: Resolve the currently-deployed Container App image at the start of the job (AC #1, #2)
  - [x] Add a new step to `.github/workflows/infra-deploy.yml`, placed after "Azure login (OIDC)" and before "What-if (manual runs only)", named e.g. "Resolve current Container App image (if any)", `id: currentimage`.
  - [x] First check whether the Container App exists yet at all: `az containerapp list --resource-group ${{ vars.AZURE_RESOURCE_GROUP_NAME }} --query "[0].name" -o tsv` — mirror `app-deploy.yml`'s existing "Resolve Container App name" step (`.github/workflows/app-deploy.yml:252-260`) exactly for this lookup pattern (runtime resolution, never hardcode the name).
  - [x] If no Container App name comes back (brand-new environment, AC #2), set the step output `image=` (empty) and stop there — do **not** call `az containerapp show` against a resource that doesn't exist.
  - [x] If a Container App name is found, read its current image: `az containerapp show --name "$APP_NAME" --resource-group ${{ vars.AZURE_RESOURCE_GROUP_NAME }} --query properties.template.containers[0].image -o tsv` — mirror `app-deploy.yml`'s existing "Capture current image (pre-deploy, for rollback)" step (`.github/workflows/app-deploy.yml:284-291`) exactly for this query. Set it as the step output `image=$CURRENT_IMAGE`.
  - [x] Do not echo the resolved image conditionally through `secrets`-style masking — it is not secret data, plain `echo`/`GITHUB_OUTPUT` is fine (same as every other non-secret runtime-resolved value in both workflows).

- [x] Task 2: Thread the resolved image into both the What-if and Deploy Bicep invocations (AC #1, #2)
  - [x] In both the "What-if (manual runs only)" and "Deploy" steps, build the `--parameters` argument list so that `infra/main.bicep`'s existing `placeholderImage` parameter (`infra/main.bicep:50-51`, threaded straight through to `infra/modules/container-app.bicep:13-14,120`) is overridden with `steps.currentimage.outputs.image` **only when that output is non-empty**. When it is empty (AC #2's brand-new-environment case), pass no override and let the parameter's own default (`mcr.microsoft.com/k8se/quickstart:latest`) apply, exactly as today.
  - [x] CLI parameter overrides passed after `--parameters infra/main.bicepparam` take precedence over the `.bicepparam` file's own values — no `.bicepparam` file, module, or Bicep resource logic changes are needed. Do not attempt an `existing` resource / conditional-create pattern in Bicep itself; that's a materially riskier rewrite of `container-app.bicep`'s resource declaration for no additional benefit over a CLI parameter override.
  - [x] Concretely, something like (adapt to this repo's existing `az deployment group create`/`what-if` step shape, don't restructure unrelated parts of the step):
    ```bash
    DEPLOY_PARAMS=(infra/main.bicepparam)
    if [ -n "${{ steps.currentimage.outputs.image }}" ]; then
      DEPLOY_PARAMS+=(placeholderImage="${{ steps.currentimage.outputs.image }}")
    fi
    az deployment group create \
      --name "infra-deploy-${{ github.run_id }}" \
      --resource-group "${{ vars.AZURE_RESOURCE_GROUP_NAME }}" \
      --template-file infra/main.bicep \
      --parameters "${DEPLOY_PARAMS[@]}"
    ```
    Apply the identical pattern to the "What-if" step too — a stale What-if diff that shows the image "changing" to the placeholder (when it actually wouldn't, post-fix) would be actively misleading to whoever reads a manual run's output.
  - [x] Reusing the existing `placeholderImage` parameter name (rather than introducing a second parameter) is deliberate: it already means exactly "the image this deployment should set," and every other consumer of it (`container-app.bicep`'s `image: placeholderImage` line) needs no change.

- [x] Task 3: Confirm cross-workflow idempotency reasoning holds (AC #3) — no code change, verification only
  - [x] Trace through both orderings by inspection and note the conclusion in Dev Notes/Completion Notes: (a) `app-deploy.yml` then `infra-deploy.yml` — the Container App is now running the app-deployed image; `infra-deploy.yml`'s new resolve-step reads that exact image back and re-applies it, so it survives. (b) `infra-deploy.yml` then `app-deploy.yml` — `infra-deploy.yml` re-applies whatever image was already running (unchanged); `app-deploy.yml` then deploys its own newly-built image on top, as it always does. Neither ordering regresses the image, and `infra-deploy.yml`'s own job (secrets/config) still applies every time regardless of which image parameter value was threaded through.
  - [x] This story does not change `app-deploy.yml` — it already deploys images correctly (see `.github/workflows/app-deploy.yml`'s own "Deploy new revision" step); only `infra-deploy.yml` has the bug.

- [x] Task 4: Documentation (NFR11 — docs are a real onboarding/operational path)
  - [x] Add a short note to `infra/README.md` (near the existing "Switching `databaseProvider` leaves the old DB server running" callout, `infra/README.md:26-33`, which documents a related idempotency gotcha in the same file) explaining that `infra-deploy.yml` now preserves the currently-running image by reading it back before redeploying, and that a brand-new environment still bootstraps with the placeholder image until `app-deploy.yml` first runs.

- [x] Task 5: Verify against every AC
  - [x] AC #1/#2: Since this only touches a GitHub Actions workflow (no application code, no test framework applies), verify via `actionlint .github/workflows/infra-deploy.yml` if the tool is available locally; otherwise a careful manual read of the diff is the fallback (same escape hatch `spec-azure-sql-ci-migration-firewall.md`'s own Verification section used for the same file). Confirm by inspection: the new step never runs `az containerapp show` when no Container App exists; the override is only added to `DEPLOY_PARAMS`/equivalent when the resolved image is non-empty; both What-if and Deploy steps get the same treatment.
  - [x] AC #3: satisfied by the Task 3 reasoning trace — no separate test artifact exists for this (no local Azure environment to run either workflow against in this session).
  - [x] **This story cannot be verified end-to-end against live Azure in this session** — same honesty-discipline precedent Stories 1.2–1.5 established for infra changes: state plainly in Completion Notes that the fix is verified by code/YAML inspection and the idempotency reasoning trace, not by an actual `infra-deploy.yml` run against the real `energy-tracker-prod` resource group. Flag that Ralf should do one real manual `workflow_dispatch` run of `infra-deploy.yml` after merge (when the Container App is running a real image, not the placeholder) and confirm via `az containerapp show --query properties.template.containers[0].image` that the image is unchanged afterward.

### Review Findings

- [x] [Review][Patch] Cross-workflow concurrency race can reintroduce the exact incident this story fixes, probabilistically — `infra-deploy.yml` and `app-deploy.yml` run under separate concurrency groups (`infra-deploy` vs `app-deploy`, `.github/workflows/infra-deploy.yml:19-21` vs `.github/workflows/app-deploy.yml:26-28`), so nothing prevents them from executing simultaneously against the same resource group (e.g. a single push touching both `infra/**` and `src/**`/`web/**` paths triggers both). If `app-deploy.yml`'s `az containerapp update` lands a new image at the same moment `infra-deploy.yml`'s new "Resolve current Container App image" step (`infra-deploy.yml:52-64`) has already read the *old* image, `infra-deploy.yml` then reapplies that stale image via `az deployment group create`, clobbering the just-deployed new revision. **Decision (Ralf, 2026-08-14): merge both workflows onto one shared concurrency group** (e.g. `group: infra-app-deploy`) in both `.github/workflows/infra-deploy.yml` and `.github/workflows/app-deploy.yml` so they can never run simultaneously.
- [x] [Review][Patch] Script-injection anti-pattern: `${{ steps.currentimage.outputs.image }}` spliced directly into `run:` script body instead of threaded through `env:` [.github/workflows/infra-deploy.yml:78-79, 95-96]
- [x] [Review][Patch] New "Resolve current Container App image" step has no log output distinguishing the image-preserved path from the first-deploy (no app found) path, unlike other runtime-resolved steps in this repo — makes the story's own required manual post-merge verification (Task 5) harder to confirm from the run log [.github/workflows/infra-deploy.yml:52-64]
- [x] [Review][Defer] `az containerapp list --query "[0].name"` silently picks an arbitrary Container App if more than one ever exists in the resource group, rather than filtering by an identifying name/tag [.github/workflows/infra-deploy.yml:55] — deferred, pre-existing pattern copied from `app-deploy.yml:255`'s identical lookup ("Exactly one exists in this resource group by design"); this diff duplicates rather than introduces the assumption.
- [x] [Review][Defer] No `timeout-minutes` set on `infra-deploy.yml`'s job or any of its steps, including the new "Resolve current Container App image" step [.github/workflows/infra-deploy.yml] — deferred, pre-existing gap across the whole workflow file, not unique to this diff's new step.

## Dev Notes

- **This story exists because of a real production incident, not a hypothetical.** On 2026-08-13, running `infra-deploy.yml` to sync a rotated `DATABASE_ADMIN_PASSWORD` secret silently swapped the running Container App image back to `mcr.microsoft.com/k8se/quickstart:latest`, taking the real app down until a follow-up `app-deploy.yml` run restored it. Full incident writeup: `_bmad-artifacts/implementation/deferred-work.md`, section "Deferred from: code review of spec-azure-sql-ci-migration-firewall (2026-08-13)", third bullet.
- **Root cause, precisely:** `infra/modules/container-app.bicep:120` declares `image: placeholderImage` as a required property of the `Microsoft.App/containerApps` resource's `properties.template.containers[0]` block. Azure Resource Manager's "incremental" deployment mode only *adds/preserves* resources and properties that are **absent** from the template — it does not merge or preserve a property that **is** present in the template but happens to differ from live state. Since `image` is always explicitly present in the template (bound to `placeholderImage`, which defaults to the quickstart image and is never overridden anywhere today — `infra/main.bicepparam` doesn't set it), every `az deployment group create` run against this template unconditionally sets the live image back to whatever `placeholderImage` resolves to. This is true regardless of "incremental" mode and is not a bug in ARM/Bicep — it's this template never being told what the *current* image actually is.
- **Fix shape is a CLI-level parameter override, not a Bicep rewrite.** `infra/main.bicep`'s `placeholderImage` parameter (default `mcr.microsoft.com/k8se/quickstart:latest`) already exists for exactly this purpose — it just needs to be told the real current value when one exists, the same way `databaseAdministratorPassword`/`oidcClientSecret` are threaded in from GitHub Actions secrets today (`infra/main.bicepparam:9,25` via `readEnvironmentVariable`). Since the *current* image is runtime state (only knowable via `az containerapp show`), it can't live in the checked-in `.bicepparam` file the way secrets do — it has to be resolved in the workflow and passed as an inline `--parameters key=value` CLI override, which takes precedence over the `.bicepparam` file. Do not attempt to solve this by making the Bicep resource conditionally read live state internally (e.g. an `existing` resource reference merged with a new one) — Bicep's `existing` keyword can't be conditionally combined with a full resource declaration of the same name in one template in a way that's simpler or safer than the CLI-override approach; every other "resolve-at-runtime" fact in this repo's workflows already uses the CLI-override/runtime-lookup pattern (SQL server name, ACR login server, Container App name — see `app-deploy.yml`), so this fix is consistent with established convention, not a new pattern.
- **This is exactly the fix the incident's own review already recommended** (`deferred-work.md`, same section): *"`infra-deploy.yml` should either thread through the currently-deployed image (e.g. read it via `az containerapp show` before redeploying) or `app-deploy.yml` should be documented as a mandatory follow-up after every `infra-deploy.yml` run."* This story implements the first (structural) option, which is strictly better than the second (procedural/documentation-only) option, since it removes the failure mode entirely rather than relying on a human remembering an extra manual step every time.
- **`app-deploy.yml` already has the two building-block patterns this fix reuses** — read them directly before implementing, don't re-derive from scratch:
  - "Resolve Container App name" (`app-deploy.yml:252-260`) — `az containerapp list --resource-group ... --query "[0].name" -o tsv`, with an explicit empty-result error guard. This story's Task 1 needs the *same* lookup, but must treat an empty result as the **expected, valid** first-deploy case (AC #2), not an error to fail on — that's the one behavioral difference from the existing pattern, so don't copy the `exit 1`-on-empty guard verbatim.
  - "Capture current image (pre-deploy, for rollback)" (`app-deploy.yml:284-291`) — `az containerapp show ... --query properties.template.containers[0].image -o tsv`. This is the literal query this story's fix needs; only the calling context differs (infra-deploy.yml reads it to *preserve* the image, app-deploy.yml reads it to have a rollback target).
- **AD-19 (Operational baseline) governs this story** — it's a pure CI/CD-pipeline reliability fix with no `FR` binding; nearest architecture anchor is AD-19's "self-host and Azure needing... no environment-specific branching" operational-baseline discipline and the Consistency Conventions table's "Config-driven adapter selection... exactly one config value read once" pattern this repo's workflows already follow for every other runtime-resolved value.
- **`pr-review.yml`'s `validate-infra` job runs an equivalent `az deployment group what-if --parameters infra/main.bicepparam` (`.github/workflows/pr-review.yml:164-168`) and has the same theoretical "will show the image reverting" cosmetic issue in its diff output** — this is deliberately **out of scope** for this story: the AC only binds `infra-deploy.yml`, that job never actually applies anything (`what-if` only), and it runs under a different, more restricted federated credential (`repo-pr`, not `repo-branch-main`) than `infra-deploy.yml`/`app-deploy.yml` share. Don't touch `pr-review.yml` — note it as a possible future follow-up only if asked.
- **No test framework applies to this story.** Nothing here touches `src/`, `web/`, or any project with an existing test suite — this is GitHub Actions YAML only. Do not add a fabricated "test" for it; verification is `actionlint` (if available) plus manual review plus the one real post-merge `workflow_dispatch` run called out in Task 5.
- **Constraints that still apply, unchanged:** AD-19 (no environment-specific branching in workflow logic — this fix branches only on "does a Container App exist yet," which is a legitimate first-deploy-vs-steady-state distinction, not an environment branch), never echo secret values (the image reference is not secret, so this doesn't apply here, but don't let that habit slip when editing nearby steps that do handle secrets).

### Project Structure Notes

Files this story touches — small, surgical change, no new files:

```text
energy-tracker-v2/
  .github/workflows/
    infra-deploy.yml    # modified — new "Resolve current Container App image" step;
                         # What-if and Deploy steps both gain a conditional placeholderImage override
  infra/
    README.md            # modified — short note on the new image-preservation behavior
```

No changes to `infra/main.bicep`, `infra/modules/container-app.bicep`, `infra/main.bicepparam`, or `app-deploy.yml`. If implementation reveals a need to touch any of those, stop and reconsider — the whole point of this story's design is that the existing `placeholderImage` parameter is already sufficient.

### References

- [Source: _bmad-artifacts/planning/epics/epic-1-foundation-deployment-household-access.md#Story 1.6] — story statement and acceptance criteria (verbatim origin)
- [Source: _bmad-artifacts/implementation/deferred-work.md#"Deferred from: code review of spec-azure-sql-ci-migration-firewall (2026-08-13)"] — the real 2026-08-13 production incident this story fixes, and the two candidate fixes it already identified
- [Source: _bmad-artifacts/implementation/spec-azure-sql-ci-migration-firewall.md] — the spec whose code review surfaced this gap; shows this repo's established spec/verification-section conventions for workflow-only changes
- [Source: .github/workflows/infra-deploy.yml] — the file this story modifies; existing What-if/Deploy step structure to extend, not restructure
- [Source: .github/workflows/app-deploy.yml:252-260] — "Resolve Container App name" step, the runtime-lookup pattern Task 1 reuses
- [Source: .github/workflows/app-deploy.yml:284-291] — "Capture current image (pre-deploy, for rollback)" step, the exact `az containerapp show` query Task 1 reuses
- [Source: infra/main.bicep:50-51,141-159] — `placeholderImage` parameter definition and its threading into the `containerApp` module call
- [Source: infra/modules/container-app.bicep:13-14,120] — `placeholderImage` parameter and the `image: placeholderImage` line that gets unconditionally re-applied on every deployment (the root cause)
- [Source: infra/main.bicepparam] — confirms `placeholderImage` is never overridden here today (so the Bicep default always wins on every real deploy)
- [Source: infra/README.md:26-33] — existing "Switching `databaseProvider` leaves the old DB server running" callout in the same file, the pattern this story's Task 4 doc note follows
- [Source: .github/workflows/pr-review.yml:164-168] — `validate-infra`'s equivalent `what-if` call, explicitly out of scope (see Dev Notes)
- [Source: ...ARCHITECTURE-SPINE.md#AD-19] — operational baseline, no environment-specific branching in logging/ops code; nearest architectural anchor for this story
- [Source: ...ARCHITECTURE-SPINE.md Consistency Conventions table, "Config-driven adapter selection" row] — "exactly one config value read once" discipline this story's CLI-override approach stays consistent with

## Dev Agent Record

### Agent Model Used

claude-sonnet-5 (Claude Code)

### Debug Log References

None — no test framework applies to this GitHub Actions YAML-only change. Verification performed by static analysis only (see Completion Notes).

### Completion Notes List

- Added a new "Resolve current Container App image (if any)" step to `infra-deploy.yml`, placed between "Azure login (OIDC)" and "What-if (manual runs only)", `id: currentimage`. It mirrors `app-deploy.yml`'s "Resolve Container App name" lookup (`az containerapp list --query "[0].name"`) but, unlike that step, treats an empty result as the expected first-deploy case (AC #2) rather than failing with `exit 1`. When a Container App is found, it reads the current image via the same `az containerapp show --query properties.template.containers[0].image` query `app-deploy.yml` already uses for its rollback capture, and exposes it as step output `image`.
- Both the "What-if (manual runs only)" and "Deploy" steps now build a `DEPLOY_PARAMS` bash array starting with `infra/main.bicepparam`, and append a `placeholderImage="${{ steps.currentimage.outputs.image }}"` CLI override only when that output is non-empty. On a brand-new environment (empty output) no override is added, so `main.bicep`'s `placeholderImage` parameter default (`mcr.microsoft.com/k8se/quickstart:latest`) still applies, preserving Story 1.2's original bootstrap behavior (AC #2). No changes were needed to `infra/main.bicep`, `infra/modules/container-app.bicep`, `infra/main.bicepparam`, or `app-deploy.yml` — exactly as the story's Dev Notes anticipated.
- **AC #3 cross-workflow idempotency trace (Task 3, no code change):**
  - **`app-deploy.yml` then `infra-deploy.yml`:** After `app-deploy.yml` runs, the Container App is running its newly-built image. `infra-deploy.yml`'s new resolve-step reads that exact image back via `az containerapp show` and passes it as the `placeholderImage` override, so the subsequent `az deployment group create` re-applies the same image — no regression to the placeholder.
  - **`infra-deploy.yml` then `app-deploy.yml`:** `infra-deploy.yml` resolves whatever image is currently running (possibly still the placeholder, on a fresh environment) and re-applies that same value — a no-op for the image. `app-deploy.yml` then deploys its own newly-built image on top via `az containerapp update`, as it always has.
  - Neither ordering regresses the image, and `infra-deploy.yml`'s own responsibility (secrets/config via `main.bicepparam` and the `DATABASE_ADMIN_PASSWORD`/`OIDC_CLIENT_SECRET` env vars) is applied on every run regardless of which image value was threaded through, since only the `placeholderImage` parameter is conditionally overridden — everything else in `main.bicepparam` still applies unconditionally.
- **Verification performed (Task 5):** `actionlint` is not installed in this environment (checked via `which actionlint` and `brew list actionlint`, neither found) and could not be installed without a network-dependent package manager step outside this story's scope, so per the story's documented fallback, verification was done by: (1) `python3 -c "import yaml; yaml.safe_load(...)"` confirming `infra-deploy.yml` remains valid YAML with the new step correctly positioned between "Azure login (OIDC)" and "What-if (manual runs only)"; (2) `bash -n` syntax-checking both new `run:` blocks in isolation; (3) manual read-through confirming `az containerapp show` is never called when `APP_NAME` is empty, and confirming both the What-if and Deploy steps get the identical conditional-override treatment. **This story was not verified end-to-end against live Azure** — no local Azure environment was available in this session. Ralf should do one real manual `workflow_dispatch` run of `infra-deploy.yml` after merge (with the Container App running a real image, not the placeholder) and confirm via `az containerapp show --query properties.template.containers[0].image` that the image is unchanged afterward.
- Added a documentation note to `infra/README.md`, alongside the existing "Switching `databaseProvider`..." callout, explaining the image-preservation fix and the brand-new-environment bootstrap behavior.

### File List

- `.github/workflows/infra-deploy.yml` — modified: added "Resolve current Container App image (if any)" step; both "What-if" and "Deploy" steps now conditionally override the `placeholderImage` Bicep parameter with the resolved current image.
- `infra/README.md` — modified: added a short note documenting the image-preservation behavior.

### Change Log

- 2026-08-14: Story 1.6 implementation complete. Fixed the production incident where `infra-deploy.yml` unconditionally reset the Container App's live image back to the placeholder on every run. Added a runtime image-resolution step and threaded it as a conditional `placeholderImage` CLI override into both the What-if and Deploy steps, reusing `app-deploy.yml`'s existing lookup/query patterns. No Bicep, `.bicepparam`, or `app-deploy.yml` changes were needed. Verified by YAML/bash static analysis and an idempotency reasoning trace (Task 3); not verified end-to-end against live Azure — a real post-merge `workflow_dispatch` run is still required (see Completion Notes).
