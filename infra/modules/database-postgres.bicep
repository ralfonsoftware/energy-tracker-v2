@description('Name of the Postgres Flexible Server (globally unique)')
param name string

@description('Azure region for the server')
param location string

@description('Administrator login name (not secret-shaped)')
param administratorLogin string

@description('Administrator login password')
@secure()
param administratorLoginPassword string

@description('Burstable-tier SKU, matching AD-2\'s "Postgres Flexible Server Burstable" choice')
param skuName string = 'Standard_B1ms'

@description('Storage size in GB')
param storageSizeGB int = 32

@description('Name of the application database')
param databaseName string = 'energytracker'

resource postgresServer 'Microsoft.DBforPostgreSQL/flexibleServers@2025-08-01' = {
  name: name
  location: location
  sku: {
    name: skuName
    tier: 'Burstable'
  }
  properties: {
    version: '16'
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorLoginPassword
    storage: {
      storageSizeGB: storageSizeGB
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2025-08-01' = {
  parent: postgresServer
  name: databaseName
}

// Allows access from Azure-hosted resources (the Container App is not VNet-integrated in this
// scale-to-zero Consumption setup) without opening the server to the public internet at large.
resource allowAzureServices 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2025-08-01' = {
  parent: postgresServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Deliberately returned to the caller (main.bicep) to pass into the Container App as a
// secretRef-backed secret, never a plain env var — see container-app.bicep.
#disable-next-line outputs-should-not-contain-secrets
output connectionString string = 'Host=${postgresServer.properties.fullyQualifiedDomainName};Database=${databaseName};Username=${administratorLogin};Password=${administratorLoginPassword};SSL Mode=Require'
