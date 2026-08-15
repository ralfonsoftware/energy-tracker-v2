@description('Name of the Application Insights component')
param name string

@description('Azure region for the component')
param location string

@description('Resource ID of the Log Analytics workspace this workspace-based component ingests into (AD-19 OTel extension) — ingestion counts against that workspace\'s dailyQuotaGb, so there is no separate cap to set here')
param logAnalyticsWorkspaceId string

// Microsoft.Insights/components hasn't had a schema-breaking release since 2020-02-02 — there is
// no newer dated API version for this resource type (unlike the LAW/ACR/Container Apps modules).
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: name
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    IngestionMode: 'LogAnalytics'
    WorkspaceResourceId: logAnalyticsWorkspaceId
  }
}

output id string = appInsights.id
// Property casing is PascalCase on this resource type's `properties` (legacy schema), unlike most newer RPs.
output connectionString string = appInsights.properties.ConnectionString
