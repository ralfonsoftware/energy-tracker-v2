@description('Name of the Container Apps managed environment')
param name string

@description('Azure region for the environment')
param location string

@description('Resource ID of the Log Analytics workspace to wire diagnostics to (no separate manual Portal link step)')
param logAnalyticsWorkspaceId string

@description('Customer ID (workspace ID) of the Log Analytics workspace')
param logAnalyticsCustomerId string

// Api version pinned to the workspace's api version so listKeys() resolves against the same contract.
var logAnalyticsApiVersion = '2026-03-01'

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2026-01-01' = {
  name: name
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsCustomerId
        sharedKey: listKeys(logAnalyticsWorkspaceId, logAnalyticsApiVersion).primarySharedKey
      }
    }
    // Consumption-only workload profile — scale-to-zero, no dedicated/always-on compute (AD-6/AD-7).
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

output id string = containerAppsEnvironment.id
output name string = containerAppsEnvironment.name
