@description('Name of the Azure SQL logical server (globally unique)')
param name string

@description('Azure region for the server')
param location string

@description('Administrator login name (not secret-shaped)')
param administratorLogin string

@description('Administrator login password')
@secure()
param administratorLoginPassword string

@description('Name of the application database')
param databaseName string = 'energytracker'

@description('DTU-model SKU name, e.g. \'Basic\' (5 DTU), \'S0\'..\'S12\' (Standard), \'P1\'..\'P15\' (Premium). Defaults to the smallest/cheapest DTU model, matching AD-2\'s "Azure SQL Basic DTU" choice — cost discipline per NFR2/NFR14.')
param skuName string = 'Basic'

@description('DTU-model SKU tier corresponding to skuName (\'Basic\', \'Standard\', or \'Premium\') — must stay in sync with skuName above.')
param skuTier string = 'Basic'

@description('Max database size in bytes. Basic tier caps at 2 GB (2147483648); raise this if skuName/skuTier move to a Standard/Premium tier with more headroom.')
param maxSizeBytes int = 2147483648

@description('Microsoft Entra ID login (UPN) of the SQL Server Entra Admin — a human Entra ID account (the project owner), never a service identity (AD-21). Sourced from an environment variable in main.bicepparam, never a committed literal.')
param entraAdminLogin string

@description('Microsoft Entra ID object ID (GUID) of the SQL Server Entra Admin principal (AD-21).')
param entraAdminObjectId string

@description('Microsoft Entra ID tenant ID (GUID) the Entra Admin principal belongs to (AD-21).')
param entraAdminTenantId string

@description('AD-21 "Deploy B": whether Azure SQL accepts only Microsoft Entra ID authentication. Must default to false and only flip to true on its own, separate deploy — never bundled with a deploy that first adds/changes the administrators block below. See infra/README.md\'s Entra-only auth cutover runbook.')
param azureADOnlyAuthenticationEnabled bool = false

resource sqlServer 'Microsoft.Sql/servers@2025-01-01' = {
  name: name
  location: location
  properties: {
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorLoginPassword
    minimalTlsVersion: '1.2'
    // AD-21 Deploy A: adds the Entra Admin. azureADOnlyAuthentication is deliberately omitted
    // here (not set to false) — per Microsoft's own Microsoft.Sql/servers reference, that field
    // is not reliably settable via this inline block on an update to an already-existing server;
    // the separate azureADOnlyAuthentications resource below is the supported way to change it.
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'User'
      login: entraAdminLogin
      sid: entraAdminObjectId
      tenantId: entraAdminTenantId
    }
  }
}

// AD-21 Deploy B mechanism: the same template serves both Deploy A (azureADOnlyAuthenticationEnabled
// false, the default) and Deploy B (true, on a later, separate deploy) — no code branch needed,
// just a param-value change between the two deploys. Never flip this to true until
// infra/sql/grant-entra-db-users.sql has been run and both identities verified able to connect
// (docs/local-vs-azure-deltas.md#D6).
resource azureADOnlyAuthentication 'Microsoft.Sql/servers/azureADOnlyAuthentications@2025-01-01' = {
  parent: sqlServer
  name: 'Default'
  properties: {
    azureADOnlyAuthentication: azureADOnlyAuthenticationEnabled
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2025-01-01' = {
  parent: sqlServer
  name: databaseName
  location: location
  sku: {
    name: skuName
    tier: skuTier
  }
  properties: {
    maxSizeBytes: maxSizeBytes
  }
}

// Allows access from Azure-hosted resources (the Container App is not VNet-integrated in this
// scale-to-zero Consumption setup) without opening the server to the public internet at large.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2025-01-01' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Deliberately returned to the caller (main.bicep) to pass into the Container App as a
// secretRef-backed secret, never a plain env var — see container-app.bicep. AD-19 keeps it a
// secretRef for composition-root uniformity across both DB providers (Postgres stays
// password-based), even though this string itself no longer carries a password (AD-21) — no
// outputs-should-not-contain-secrets suppression is needed here anymore, since
// "Authentication=Active Directory Managed Identity" has no secret-shaped substring to flag.
output connectionString string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${databaseName};Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
