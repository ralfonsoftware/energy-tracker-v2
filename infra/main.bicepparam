using 'main.bicep'

// Non-secret environment values only (AC #4). databaseAdministratorPassword is @secure() in
// main.bicep and is read here from a *process environment variable*, never a literal — the
// value itself comes from the GitHub Actions secret DATABASE_ADMIN_PASSWORD, exported into the
// deploy step's environment (see .github/workflows/infra-deploy.yml), and is never written into
// this checked-in file. resourceToken is left at its resource-group-derived default.

param databaseAdministratorPassword = readEnvironmentVariable('DATABASE_ADMIN_PASSWORD', '')

// Story 1.5 (household provisioning via OIDC) — oidcAuthority/oidcClientId are non-secret, so
// (unlike the password below) they're literal values here, not environment-variable-sourced.
// No real OIDC provider (Entra ID app registration, Auth0 tenant, etc.) has been provisioned
// yet — that's an external dependency this story cannot self-provision — so both are left blank
// for now; the app runs with OIDC unconfigured (Program.cs treats a blank ClientId as "not
// configured yet" rather than failing every request) until real values are filled in here.
// oidcClientSecret mirrors the databaseAdministratorPassword pattern exactly: read from a
// process environment variable (OIDC_CLIENT_SECRET, exported by
// .github/workflows/infra-deploy.yml from the GitHub Actions secret), never a literal here.
// NOTE: readEnvironmentVariable('OIDC_CLIENT_SECRET') requires the GitHub Actions secret to
// exist (see infra-deploy.yml) — it does not yet; deploys will fail until a repo admin creates
// it (flagged explicitly rather than silently deploying against an 'unset' sentinel — Task 6).
param oidcAuthority = readEnvironmentVariable('OIDC_AUTHORITY', '')
param oidcClientId = readEnvironmentVariable('OIDC_CLIENT_ID', '')
param oidcClientSecret = readEnvironmentVariable('OIDC_CLIENT_SECRET', '')

// Pinned explicitly rather than left at the resource group's own location (germanywestcentral):
// Postgres Flexible Server provisioning is subscription-restricted in that region at the time of
// writing ("Provisioning is restricted in this region" — az postgres flexible-server list-skus).
// A resource's location is independent of its containing resource group's location in Azure, so
// this keeps every resource in one region that is verified to support all of them.
param location = 'westeurope'

param environmentName = 'prod'
param customDomainName = 'energytracker.ralfonsoftware.de'
param customDomainCertificateReady = true

// Azure SQL Basic DTU (AD-2) — the smallest/cheapest DTU model, per NFR2/NFR14 cost discipline.
// sqlServerSkuTier is derived from sqlServerSkuName in main.bicep, not a separate parameter.
param databaseProvider = 'SqlServer'
param sqlServerSkuName = 'Basic'
param sqlServerMaxSizeBytes = 2147483648

// Postgres params below are inert while databaseProvider = 'SqlServer' (main.bicep only deploys
// the module matching databaseProvider), kept here so switching providers is a one-line change.
param postgresSkuName = 'Standard_B1ms'
param postgresStorageSizeGB = 32

param logAnalyticsRetentionInDays = 30
param databaseAdministratorLogin = 'etadmin'
param containerAppMinReplicas = 0
param containerAppMaxReplicas = 1

// AD-19 OTel extension (epic-1 retro action items 4-6). Application Insights itself deploys
// unconditionally (layered on the existing Log Analytics workspace at no separate ingestion
// cap), but the cap-triggered alerts do not: otelAlertNotificationEmail mirrors the
// oidcAuthority/oidcClientId pattern above — left blank because no notification address has
// been decided yet (an external, not-self-provisionable choice), so main.bicep's
// monitorAlert module simply doesn't deploy until this is filled in.
// Raised from 1 to 2 GB/day (2026-09-01) after a production incident (queue-redelivery storm,
// see bugfix/queue-visibility-timeout-redelivery) generated ~2.7M log lines in 16 minutes and
// blew through the 1 GB cap, leaving the workspace OverQuota and Azure Monitor blind for the
// rest of that day. Still a spike safeguard, not a routine cost-control lever — see
// log-analytics.bicep's own param doc comment.
param otelDailyIngestionCapGb = 2
param otelAlertNotificationEmail = readEnvironmentVariable('AZURE_ALERT_OTEL_CAP_EMAIL', '')
