@description('Base name for the action group and alert rules')
param name string

@description('Azure region for the alert rules (action groups are always global regardless of this)')
param location string

@description('Resource ID of the Log Analytics workspace to scope/query — the same workspace log-analytics.bicep applied dailyQuotaGb to')
param logAnalyticsWorkspaceId string

@description('Email address notified when the daily ingestion cap is reached or nearly reached. Caller only deploys this module when non-empty (AD-19 OTel extension) — same blank-disables shape as the OIDC params in main.bicep.')
param notificationEmail string

@description('Daily ingestion cap in GB — must match log-analytics.bicep\'s dailyQuotaGb; the 90% early-warning threshold is derived from this')
param dailyCapGb int

// Latest stable API version for actionGroups is 2023-01-01 (a 2024-10-01-preview exists but
// nothing newer non-preview).
resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${name}-ag'
  location: 'global'
  properties: {
    groupShortName: 'otel-cap'
    enabled: true
    emailReceivers: [
      {
        name: 'notificationEmail'
        emailAddress: notificationEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

// Query/settings straight from Microsoft's "Set daily cap" doc's recommended
// alert-when-daily-cap-is-reached rule. Once this fires, data collection (and therefore every
// other alert on this workspace) stops for the rest of the day — see nearCapWarningAlert below
// for the earlier heads-up Microsoft recommends pairing it with.
resource overQuotaAlert 'Microsoft.Insights/scheduledQueryRules@2026-03-01' = {
  name: '${name}-over-quota'
  location: location
  kind: 'LogAlert'
  properties: {
    displayName: 'Log Analytics daily cap reached'
    description: 'Workspace hit its daily ingestion cap; data collection (and monitoring) stops for the rest of the day.'
    severity: 1
    enabled: true
    scopes: [
      logAnalyticsWorkspaceId
    ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    criteria: {
      allOf: [
        {
          query: '_LogOperation | where Category =~ "Ingestion" | where Detail contains "OverQuota"'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
        }
      ]
    }
    autoMitigate: false
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
  }
}

// Early warning at 90% of the cap, per Microsoft's "Analyze usage" > "Send alert when data
// collection is high" guidance — without it, the first signal you'd get is overQuotaAlert above,
// by which point monitoring has already gone dark for the day.
resource nearCapWarningAlert 'Microsoft.Insights/scheduledQueryRules@2026-03-01' = {
  name: '${name}-near-cap-warning'
  location: location
  kind: 'LogAlert'
  properties: {
    displayName: 'Log Analytics ingestion nearing daily cap (90%)'
    description: 'Billable data ingested in the trailing 24h crossed 90% of the configured daily cap, ahead of the hard cutoff.'
    severity: 2
    enabled: true
    scopes: [
      logAnalyticsWorkspaceId
    ]
    // Azure rejects a stateful rule (autoMitigate: true, below) at a frequency greater than 12
    // hours ("Stateful rules can not run in a frequency greater than 12 hours" — hit deploying
    // this exact rule). windowSize stays a rolling 24h window; only how often it's re-checked
    // changes, so this is a strict improvement over once-daily (faster detection, same query).
    evaluationFrequency: 'PT12H'
    windowSize: 'P1D'
    criteria: {
      allOf: [
        {
          query: 'Usage | where IsBillable | summarize DataGB = sum(Quantity / 1000)'
          metricMeasureColumn: 'DataGB'
          timeAggregation: 'Total'
          operator: 'GreaterThan'
          // Bicep has no float type; integer division truncates towards zero, same as ARM's div().
          threshold: (dailyCapGb * 9) / 10
        }
      ]
    }
    autoMitigate: true
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
  }
}
