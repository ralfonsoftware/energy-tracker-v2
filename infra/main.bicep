targetScope = 'resourceGroup'

@description('Environment name, used in resource naming. Kept short: it feeds into the storage account name (24-char global limit), which is the tightest constraint.')
@minLength(1)
@maxLength(10)
param environmentName string = 'prod'

@description('Azure region for all resources')
param location string = resourceGroup().location

@description('Config-selected database provider (AD-2) — exactly one of the two DB modules below is deployed to match')
@allowed([
  'Postgres'
  'SqlServer'
])
param databaseProvider string = 'SqlServer'

@description('Short, unique token used in resource names for globally-unique resource types (Storage, ACR, Postgres/SQL server FQDNs). Defaults to a token derived from the resource group so names stay stable across re-deployments to the same environment. Only the first 10 characters are used (see globalToken below); if overridden, must be lowercase alphanumeric only — Storage/ACR name rules reject any other character.')
param resourceToken string = uniqueString(resourceGroup().id)

@description('Log Analytics data retention in days')
param logAnalyticsRetentionInDays int = 30

@description('Daily ingestion cap in GB, shared by the Log Analytics workspace and the workspace-based Application Insights layered on it (AD-19 OTel extension)')
param otelDailyIngestionCapGb int = 2

@description('Email address notified when the daily ingestion cap is reached or nearly reached (90%). Blank disables both alerts — same degrade-gracefully shape as the blank OIDC params below.')
param otelAlertNotificationEmail string = ''

@description('Database administrator login name (not secret-shaped)')
param databaseAdministratorLogin string = 'etadmin'

@description('Database administrator password — supplied at deploy time from a GitHub Actions secret, never committed')
@secure()
param databaseAdministratorPassword string

@description('Postgres Burstable SKU')
param postgresSkuName string = 'Standard_B1ms'

@description('Postgres storage size in GB')
param postgresStorageSizeGB int = 32

@description('Azure SQL DTU-model SKU name, e.g. \'Basic\' (5 DTU), \'S0\'..\'S12\' (Standard), \'P1\'..\'P15\' (Premium). Defaults to the smallest/cheapest DTU model.')
param sqlServerSkuName string = 'Basic'

@description('Azure SQL max database size in bytes. Basic tier caps at 2 GB (2147483648); raise this if sqlServerSkuName moves to a Standard/Premium tier with more headroom.')
param sqlServerMaxSizeBytes int = 2147483648

@description('Microsoft Entra ID login (UPN) of the Azure SQL Entra Admin (AD-21) — a human Entra ID account (the project owner), never a service identity. Non-secret but identity-linked (a real email/UPN), so — like oidcAuthority/oidcClientId below — sourced from an environment variable in main.bicepparam rather than a checked-in literal.')
param entraAdminLogin string = ''

@description('Microsoft Entra ID object ID (GUID) of the Azure SQL Entra Admin principal (AD-21). See entraAdminLogin for the sourcing rationale.')
param entraAdminObjectId string = ''

@description('Microsoft Entra ID tenant ID (GUID) the Azure SQL Entra Admin principal belongs to (AD-21). See entraAdminLogin for the sourcing rationale.')
param entraAdminTenantId string = ''

@description('AD-21 "Deploy B": whether Azure SQL accepts only Microsoft Entra ID authentication. Defaults to false (Deploy A behavior). Flipped to true only via a deliberate, standalone deploy-time override — deliberately not set in main.bicepparam, same pattern as customDomainCertificateReady below.')
param azureADOnlyAuthenticationEnabled bool = false

@description('Container App scale-to-zero minimum replica count (AD-6/AD-7)')
param containerAppMinReplicas int = 0

@description('Container App maximum replica count')
@minValue(1)
param containerAppMaxReplicas int = 1

@description('Initial placeholder container image — Story 1.3 replaces this with the real ACR image')
param placeholderImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('OIDC provider Authority URL (not secret) — a real value requires a live OIDC provider app registration (Entra ID, Auth0, etc.); left blank the app runs but /login is unusable until configured.')
param oidcAuthority string = ''

@description('OIDC provider Client ID (not secret) — see oidcAuthority.')
param oidcClientId string = ''

@description('Custom domain hostname to bind to the Container App (e.g. app.example.com) — blank until DNS is manually verified with the external DNS provider (docs/local-vs-azure-deltas.md#D5). Deliberately not set in main.bicepparam; supply as a one-off deploy-time override only after verification.')
param customDomainName string = ''

@description('Whether the managed certificate for customDomainName should be created. Must stay false on the first deploy that sets customDomainName (claims the hostname only) and only flip to true on a second, later deploy once that claim is live — Azure requires the hostname already registered before it will create a certificate for it (docs/local-vs-azure-deltas.md#D5). Deliberately not set in main.bicepparam.')
param customDomainCertificateReady bool = false

@description('OIDC provider Client Secret — supplied at deploy time from a GitHub Actions secret, never committed. Defaults to the non-empty \'unset\' sentinel container-app.bicep requires when no real value exists yet.')
@secure()
param oidcClientSecret string = 'unset'

@description('Port the ingress health-checks and routes traffic to. Matches the real app image (Dockerfile ASPNETCORE_HTTP_PORTS=8080), which Story 1.3\'s deploy workflow pushes and deploys; the placeholder image used before Story 1.3 listened on 80. Kept in sync by hand with the hardcoded `--target-port 8080` in .github/workflows/app-deploy.yml\'s "Ensure ACR pull credential and target port" step — that step reapplies 8080 on every push independent of this default, so update both if this value ever changes.')
param containerAppTargetPort int = 8080

// Shared naming convention: energytracker-{resourceType}-{env}, applied via this namePrefix so no
// module hand-crafts its own name. Storage/ACR/DB-server names are globally unique DNS names with
// their own stricter character-set/length rules, so they additionally fold in resourceToken.
var namePrefix = 'energytracker-${environmentName}'
var globalToken = toLower(take(resourceToken, 10))

// Derived, not a parameter: the tier is fully determined by the SKU name, so exposing it as a
// separate param risked the two silently drifting out of sync (SQL DTU-model naming convention:
// 'Basic' is its own tier; 'S0'..'S12' are 'Standard'; 'P1'..'P15' are 'Premium').
var sqlServerSkuTier = sqlServerSkuName == 'Basic'
  ? 'Basic'
  : (startsWith(sqlServerSkuName, 'S') ? 'Standard' : 'Premium')

module logAnalytics 'modules/log-analytics.bicep' = {
  name: 'log-analytics'
  params: {
    name: '${namePrefix}-law'
    location: location
    retentionInDays: logAnalyticsRetentionInDays
    dailyQuotaGb: otelDailyIngestionCapGb
  }
}

module appInsights 'modules/app-insights.bicep' = {
  name: 'app-insights'
  params: {
    name: '${namePrefix}-appi'
    location: location
    logAnalyticsWorkspaceId: logAnalytics.outputs.workspaceId
  }
}

module monitorAlert 'modules/monitor-alert.bicep' = if (!empty(otelAlertNotificationEmail)) {
  name: 'monitor-alert'
  params: {
    name: '${namePrefix}-otel'
    location: location
    logAnalyticsWorkspaceId: logAnalytics.outputs.workspaceId
    notificationEmail: otelAlertNotificationEmail
    dailyCapGb: otelDailyIngestionCapGb
  }
}

module containerAppsEnvironment 'modules/container-apps-environment.bicep' = {
  name: 'container-apps-environment'
  params: {
    name: '${namePrefix}-cae'
    location: location
    logAnalyticsWorkspaceId: logAnalytics.outputs.workspaceId
    logAnalyticsCustomerId: logAnalytics.outputs.customerId
    customDomainName: customDomainName
    customDomainCertificateReady: customDomainCertificateReady
  }
}

module containerRegistry 'modules/container-registry.bicep' = {
  name: 'container-registry'
  params: {
    // ACR names: alphanumeric only, 5-50 chars, globally unique — no hyphens allowed.
    name: toLower('energytracker${environmentName}${globalToken}acr')
    location: location
  }
}

module storageQueue 'modules/storage-queue.bicep' = {
  name: 'storage-queue'
  params: {
    // Storage account names: lowercase alphanumeric only, max 24 chars, globally unique.
    name: toLower('et${environmentName}${globalToken}sa')
    location: location
  }
}

module databasePostgres 'modules/database-postgres.bicep' = if (databaseProvider == 'Postgres') {
  name: 'database-postgres'
  params: {
    name: '${namePrefix}-${globalToken}-psql'
    location: location
    administratorLogin: databaseAdministratorLogin
    administratorLoginPassword: databaseAdministratorPassword
    skuName: postgresSkuName
    storageSizeGB: postgresStorageSizeGB
  }
}

module databaseSqlServer 'modules/database-sqlserver.bicep' = if (databaseProvider == 'SqlServer') {
  name: 'database-sqlserver'
  params: {
    name: '${namePrefix}-${globalToken}-sql'
    location: location
    administratorLogin: databaseAdministratorLogin
    administratorLoginPassword: databaseAdministratorPassword
    skuName: sqlServerSkuName
    skuTier: sqlServerSkuTier
    maxSizeBytes: sqlServerMaxSizeBytes
    entraAdminLogin: entraAdminLogin
    entraAdminObjectId: entraAdminObjectId
    entraAdminTenantId: entraAdminTenantId
    azureADOnlyAuthenticationEnabled: azureADOnlyAuthenticationEnabled
  }
}

module containerApp 'modules/container-app.bicep' = {
  name: 'container-app'
  params: {
    name: '${namePrefix}-app'
    location: location
    containerAppsEnvironmentId: containerAppsEnvironment.outputs.id
    containerRegistryName: containerRegistry.outputs.name
    placeholderImage: placeholderImage
    databaseProvider: databaseProvider
    databaseConnectionString: databaseProvider == 'Postgres' ? databasePostgres.outputs.connectionString : databaseSqlServer.outputs.connectionString
    storageQueueConnectionString: storageQueue.outputs.connectionString
    oidcAuthority: oidcAuthority
    oidcClientId: oidcClientId
    oidcClientSecretValue: oidcClientSecret
    appInsightsConnectionString: appInsights.outputs.connectionString
    minReplicas: containerAppMinReplicas
    maxReplicas: containerAppMaxReplicas
    targetPort: containerAppTargetPort
    customDomainName: customDomainName
    customDomainCertificateReady: customDomainCertificateReady
    managedCertificateId: containerAppsEnvironment.outputs.managedCertificateId
  }
}

output containerAppFqdn string = containerApp.outputs.fqdn
output customDomainVerificationId string = containerApp.outputs.customDomainVerificationId
output containerRegistryLoginServer string = containerRegistry.outputs.loginServer
output logAnalyticsWorkspaceId string = logAnalytics.outputs.workspaceId
output appInsightsId string = appInsights.outputs.id
