@description('Name of the Key Vault (3-24 chars, alphanumeric and hyphens, globally unique)')
@minLength(3)
@maxLength(24)
param name string

@description('Azure region for the Key Vault')
param location string

@description('Name of the single RSA key that wraps the ASP.NET Core Data Protection key ring (AD-17)')
param keyName string = 'data-protection'

// AD-17 (amendment 2026-10-10, audit S2): the Data Protection key ring in the database is wrapped by
// this one key, so database read access alone can no longer forge a session cookie. The Container
// App's system-assigned identity gets "Key Vault Crypto Service Encryption User" on the KEY only;
// that role assignment lives in container-app.bicep (it needs the app's principalId, and the app
// needs the key URI from here, so assigning it here would be circular — same reason AcrPull lives
// there).
resource vault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: name
  location: location
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    // RBAC permission model: data-plane access follows Azure RBAC only; no access policies.
    enableRbacAuthorization: true
    // Soft delete is always on. Purge protection is deliberately left off: it is irreversible and
    // blocks re-creating a same-named vault after a resource group rebuild
    // (docs/local-vs-azure-deltas.md D6); losing this key only invalidates sessions.
    //
    // publicNetworkAccess stays enabled and there are no networkAcls: a Container App on the
    // Consumption profile has no fixed outbound IP, so a firewall here would lock the app out.
    // Access is gated by RBAC + the managed identity instead.
  }
}

// Software-protected RSA key, wrap/unwrap only. No expiry and no rotation policy: an expired or
// rotated-out key must never make the stored key ring undecryptable (older key versions stay usable
// for unwrap anyway; each ring element records the exact key version it was wrapped with).
resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2024-11-01' = {
  parent: vault
  name: keyName
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: [
      'wrapKey'
      'unwrapKey'
    ]
    attributes: {
      enabled: true
    }
  }
}

output id string = vault.id
output name string = vault.name
output keyName string = dataProtectionKey.name
// Versionless on purpose (https://<vault>.vault.azure.net/keys/<name>): the app resolves it to the
// latest version when it creates a new ring key, while existing ring keys carry their own version.
output keyUri string = dataProtectionKey.properties.keyUri
