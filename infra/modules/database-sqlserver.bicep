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

resource sqlServer 'Microsoft.Sql/servers@2025-01-01' = {
  name: name
  location: location
  properties: {
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorLoginPassword
    minimalTlsVersion: '1.2'
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
// secretRef-backed secret, never a plain env var — see container-app.bicep. A @secure() output
// decorator would mask this in deployment history too, but requires Bicep CLI >=0.29; this
// environment's installed CLI (0.24.24) rejects that syntax with a hard BCP129 error, so the
// lint suppression is kept instead — see the story's Review Findings for the follow-up.
#disable-next-line outputs-should-not-contain-secrets
output connectionString string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${databaseName};User ID=${administratorLogin};Password=${administratorLoginPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
