## Deferred from: code review of story-1-1-deployable-application-skeleton-local-dev-self-host (2026-08-09)

- No automated path applies pending EF Core migrations at startup or in self-host docs [src/EnergyTracker.Api/Program.cs:37] — not blocking this story since the `InitialCreate` migration is currently empty (no domain entities yet), but Story 1.5+ (or whichever story first adds real entities) will need either a `dbContext.Database.MigrateAsync()` call at startup or a documented `dotnet ef database update` step for self-hosters, since there's currently no SDK-free way to apply a schema on a fresh volume.

## Deferred from: code review of 1-2-azure-infrastructure-as-code-resource-deployment-pipeline (2026-08-12)

- DB firewall rule allows all Azure-service traffic (`AllowAzureServices`, `0.0.0.0`-`0.0.0.0`) [infra/modules/database-postgres.bicep:54, infra/modules/database-sqlserver.bicep:51] — inherent to the non-VNet-integrated Consumption-plan architecture already committed to by AD-6/AD-7; revisit if/when VNet integration or private endpoints are ever adopted.
- GitHub Actions pinned to floating version tags, not commit SHAs (`azure/login@v2`, `actions/checkout@v4`) [.github/workflows/infra-deploy.yml:22,28] — supply-chain hardening opportunity for a workflow with `id-token: write`; not required by any AC.
- Public ingress with no auth/access-control gate [infra/modules/container-app.bicep:58-62] — expected at this stage since only the public placeholder image is deployed (no real app or data yet); revisit once Story 1.5 (household/OIDC auth) lands to confirm the gate is actually wired before real data is exposed.
- No approval/environment-protection gate before the deploy step runs [.github/workflows/infra-deploy.yml] — matches AC #2/#3's literal push-to-main auto-deploy design; revisit if a staging environment or required-reviewer policy is ever wanted for this repo.
