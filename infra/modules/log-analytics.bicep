@description('Name of the Log Analytics workspace')
param name string

@description('Azure region for the workspace')
param location string

@description('Data retention in days (kept short — personal-household deployment, not a scale target, AD-19)')
param retentionInDays int = 30

@description('Daily ingestion cap in GB, shared by this workspace and any workspace-based Application Insights component layered on it (AD-19 OTel extension). -1 = unlimited. Default 2 GB/day is a spike safeguard for a personal-household deployment, not a routine cost-control lever.')
param dailyQuotaGb int = 2

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2026-03-01' = {
  name: name
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
    workspaceCapping: {
      dailyQuotaGb: dailyQuotaGb
    }
  }
}

output workspaceId string = logAnalyticsWorkspace.id
output customerId string = logAnalyticsWorkspace.properties.customerId
