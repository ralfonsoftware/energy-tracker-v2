@description('Name of the Container Apps managed environment')
param name string

@description('Azure region for the environment')
param location string

@description('Resource ID of the Log Analytics workspace to wire diagnostics to (no separate manual Portal link step)')
param logAnalyticsWorkspaceId string

@description('Customer ID (workspace ID) of the Log Analytics workspace')
param logAnalyticsCustomerId string

@description('Custom domain hostname to issue a free managed certificate for (e.g. app.example.com) — blank until DNS is manually verified with the external DNS provider (docs/local-vs-azure-deltas.md#D5). Never set via main.bicepparam; supply as a one-off deploy-time override only after verification.')
param customDomainName string = ''

// Api version pinned to the workspace's api version so listKeys() resolves against the same contract.
var logAnalyticsApiVersion = '2026-03-01'

// Trimmed so a whitespace-only override (e.g. accidental ' ') doesn't pass !empty() and create a
// managedCertificates resource with a garbage subjectName.
var trimmedCustomDomainName = trim(customDomainName)

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

// Created only once customDomainName is explicitly overridden at deploy time — requires the
// CNAME/asuid TXT records to already resolve publicly at the external DNS provider, or issuance
// fails outright (docs/local-vs-azure-deltas.md#D5). Dormant (not deployed) by default.
// Name is a fixed literal, not derived from customDomainName: Microsoft.App resource names only
// allow lowercase letters, numbers, and hyphens, and a dotted hostname (e.g. app.example.com)
// would fail ARM name validation if interpolated directly. This groundwork supports exactly one
// bound custom domain, so a static name is sufficient.
resource managedCert 'Microsoft.App/managedEnvironments/managedCertificates@2026-01-01' = if (!empty(trimmedCustomDomainName)) {
  parent: containerAppsEnvironment
  name: 'custom-domain-cert'
  location: location
  properties: {
    subjectName: trimmedCustomDomainName
    domainControlValidation: 'CNAME'
  }
}

output id string = containerAppsEnvironment.id
output name string = containerAppsEnvironment.name
output managedCertificateId string = !empty(trimmedCustomDomainName) ? managedCert.id : ''
