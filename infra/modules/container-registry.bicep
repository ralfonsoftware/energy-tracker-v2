@description('Name of the Azure Container Registry (alphanumeric only, globally unique)')
param name string

@description('Azure region for the registry')
param location string

resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' = {
  name: name
  location: location
  // Basic SKU — cheapest tier, cost discipline per NFR2/NFR14.
  sku: {
    name: 'Basic'
  }
  properties: {
    // Pulls happen via the Container App's system-assigned managed identity + AcrPull role
    // assignment (see container-app.bicep), never shared admin credentials.
    adminUserEnabled: false
  }
}

output id string = registry.id
output name string = registry.name
output loginServer string = registry.properties.loginServer
