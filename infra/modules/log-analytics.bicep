@description('Name of the Log Analytics workspace')
param name string

@description('Azure region for the workspace')
param location string

@description('Data retention in days (kept short — personal-household deployment, not a scale target, AD-19)')
param retentionInDays int = 30

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2026-03-01' = {
  name: name
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
  }
}

output workspaceId string = logAnalyticsWorkspace.id
output customerId string = logAnalyticsWorkspace.properties.customerId
