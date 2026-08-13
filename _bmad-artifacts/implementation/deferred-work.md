## Deferred from: code review of story-1-1-deployable-application-skeleton-local-dev-self-host (2026-08-09)

- No automated path applies pending EF Core migrations at startup or in self-host docs [src/EnergyTracker.Api/Program.cs:37] — not blocking this story since the `InitialCreate` migration is currently empty (no domain entities yet), but Story 1.5+ (or whichever story first adds real entities) will need either a `dbContext.Database.MigrateAsync()` call at startup or a documented `dotnet ef database update` step for self-hosters, since there's currently no SDK-free way to apply a schema on a fresh volume.

## Deferred from: code review of 1-2-azure-infrastructure-as-code-resource-deployment-pipeline (2026-08-12)

- DB firewall rule allows all Azure-service traffic (`AllowAzureServices`, `0.0.0.0`-`0.0.0.0`) [infra/modules/database-postgres.bicep:54, infra/modules/database-sqlserver.bicep:51] — inherent to the non-VNet-integrated Consumption-plan architecture already committed to by AD-6/AD-7; revisit if/when VNet integration or private endpoints are ever adopted.
- GitHub Actions pinned to floating version tags, not commit SHAs (`azure/login@v2`, `actions/checkout@v4`) [.github/workflows/infra-deploy.yml:22,28] — supply-chain hardening opportunity for a workflow with `id-token: write`; not required by any AC.
- Public ingress with no auth/access-control gate [infra/modules/container-app.bicep:58-62] — expected at this stage since only the public placeholder image is deployed (no real app or data yet); revisit once Story 1.5 (household/OIDC auth) lands to confirm the gate is actually wired before real data is exposed.
- No approval/environment-protection gate before the deploy step runs [.github/workflows/infra-deploy.yml] — matches AC #2/#3's literal push-to-main auto-deploy design; revisit if a staging environment or required-reviewer policy is ever wanted for this repo.

## Deferred from: code review of story-1-3-ci-build-test-cd-deploy-pipeline-app-to-azure (2026-08-12)

- Re-adding the ACR `registries` entry unconditionally in `container-app.bicep` would reintroduce the exact eager-validation 401 race Story 1.2 removed it to avoid, if `infra-deploy.yml` is ever run against a brand-new environment (Container App + AcrPull role assignment not yet existing) rather than redeployed against the current live Story 1.2 environment [infra/modules/container-app.bicep:79-84] — zero current impact since the live environment already exists; only relevant to a future from-scratch/disaster-recovery redeploy, out of this story's scope.

## Deferred from: code review of 1-4-pull-request-review-workflow (2026-08-13)

- `web/.oxlintrc.json`'s new `"no-unused-vars": "error"` has no ignore pattern for intentionally-unused vars (e.g. `_`-prefixed args) [web/.oxlintrc.json] — repo is clean today; revisit if it starts blocking legitimate code.
- Branch-protection `required_status_checks.checks` entries omit `app_id`, so GitHub matches the required check by context string from any reporting source, not only this workflow's job [infra/README.md — branch protection `gh api` payload] — low practical risk for this repo's trust model; hardening improvement, not a defect.
- GitHub's "require approval to run workflows for first-time/outside contributors" setting, if enabled, could leave a fork PR's Actions run never starting — both required checks stay perpetually pending, blocking merge indefinitely, with no code-level guard possible [.github/workflows/pr-review.yml — fork-handling design] — platform-level setting outside this diff's control; worth a doc note in a future pass.
- All actions in `pr-review.yml` pinned by mutable major-version tags (`@v7`, `@v6`, `@v3`) rather than SHA [.github/workflows/pr-review.yml] — pre-existing convention from Story 1.2/1.3, propagated rather than introduced by this diff.
- "Notice — infra changed but validation skipped (fork PR)" step's fork-skip condition relies on no earlier step being able to fail on that path — fine today, but fragile if a future edit inserts an unconditional failing step before it without adding `if: always()` [.github/workflows/pr-review.yml:120-122].

## Deferred from: code review of story-1.5 (2026-08-13)

- `GET /api/session`'s `SingleAsync` throws an unhandled exception if a resolved `HouseholdId` doesn't correspond to an existing `Households` row [src/EnergyTracker.Api/Endpoints/SessionEndpoints.cs:25] — pre-existing gap that only becomes reachable once a future household-deletion feature exists; no code path in this story can produce the inconsistent state today.
