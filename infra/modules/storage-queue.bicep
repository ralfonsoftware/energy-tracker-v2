@description('Name of the storage account (lowercase alphanumeric only, globally unique)')
param name string

@description('Azure region for the storage account')
param location string

@description('Name of the queue backing the AD-6 AzureStorageQueue job-queue adapter')
param queueName string = 'jobs'

resource storageAccount 'Microsoft.Storage/storageAccounts@2026-04-01' = {
  name: name
  location: location
  kind: 'StorageV2'
  // Standard_LRS — cheapest redundancy tier, cost discipline per NFR2/NFR14.
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2026-04-01' = {
  parent: storageAccount
  name: 'default'
}

resource queue 'Microsoft.Storage/storageAccounts/queueServices/queues@2026-04-01' = {
  parent: queueService
  name: queueName
}

output id string = storageAccount.id
output name string = storageAccount.name
output queueName string = queue.name

// Deliberately returned to the caller (main.bicep) to pass into the Container App as a
// secretRef-backed secret, never a plain env var — see container-app.bicep.
#disable-next-line outputs-should-not-contain-secrets
output connectionString string = 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${storageAccount.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
