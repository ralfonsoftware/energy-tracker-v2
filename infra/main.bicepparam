using 'main.bicep'

// Non-secret environment values only (AC #4). databaseAdministratorPassword is @secure() in
// main.bicep and is read here from a *process environment variable*, never a literal — the
// value itself comes from the GitHub Actions secret DATABASE_ADMIN_PASSWORD, exported into the
// deploy step's environment (see .github/workflows/infra-deploy.yml), and is never written into
// this checked-in file. resourceToken is left at its resource-group-derived default.

param databaseAdministratorPassword = readEnvironmentVariable('DATABASE_ADMIN_PASSWORD')

// Pinned explicitly rather than left at the resource group's own location (germanywestcentral):
// Postgres Flexible Server provisioning is subscription-restricted in that region at the time of
// writing ("Provisioning is restricted in this region" — az postgres flexible-server list-skus).
// A resource's location is independent of its containing resource group's location in Azure, so
// this keeps every resource in one region that is verified to support all of them.
param location = 'westeurope'

param environmentName = 'prod'

// Azure SQL Basic DTU (AD-2) — the smallest/cheapest DTU model, per NFR2/NFR14 cost discipline.
param databaseProvider = 'SqlServer'
param sqlServerSkuName = 'Basic'
param sqlServerSkuTier = 'Basic'
param sqlServerMaxSizeBytes = 2147483648

// Postgres params below are inert while databaseProvider = 'SqlServer' (main.bicep only deploys
// the module matching databaseProvider), kept here so switching providers is a one-line change.
param postgresSkuName = 'Standard_B1ms'
param postgresStorageSizeGB = 32

param logAnalyticsRetentionInDays = 30
param databaseAdministratorLogin = 'etadmin'
param containerAppMinReplicas = 0
param containerAppMaxReplicas = 1
