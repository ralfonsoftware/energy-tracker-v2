@description('Name of the Container App')
param name string

@description('Azure region for the Container App')
param location string

@description('Resource ID of the Container Apps managed environment')
param containerAppsEnvironmentId string

@description('Name of the Container Registry the app pulls from (used for the AcrPull role assignment scope)')
param containerRegistryName string

@description('Initial container image. No image exists in the ACR until Story 1.3 builds/pushes one, so this deploys against a public placeholder; Story 1.3\'s CD workflow updates the revision to the real ACR image afterward.')
param placeholderImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('The config-selected database provider (AD-2) — must match whichever DB module main.bicep deployed')
@allowed([
  'Postgres'
  'SqlServer'
])
param databaseProvider string

@description('Database connection string, stored as a Container App secret, never a plain env var')
@secure()
param databaseConnectionString string

@description('Storage Queue connection string for the AD-6 AzureStorageQueue job-queue adapter, stored as a Container App secret')
@secure()
param storageQueueConnectionString string

@description('Placeholder secret value for the AD-8 OpenAI-compatible AI backend API key — the adapter exists (Story 6.3) but the real backend is undecided (Epic 9). Story 9.2 must thread a real value from main.bicep together with the AI endpoint; until then this reserves the secret slot with a non-empty sentinel (ACA rejects a secrets entry with an empty value).')
@secure()
param aiApiKeySecretValue string = 'unset'

@description('Placeholder secret value for Story 1.5\'s OIDC client secret — no real value exists yet. Story 1.5 must thread a real value from main.bicep once one exists; until then this reserves the secret slot with a non-empty sentinel (ACA rejects a secrets entry with an empty value).')
@secure()
param oidcClientSecretValue string = 'unset'

@description('Application Insights connection string (AD-19 OTel extension), stored as a Container App secret. main.bicep always supplies a real value (the appInsights module deploys unconditionally); this default is only the same non-empty ACA-required sentinel used below for the other reserved-but-possibly-unset secrets.')
@secure()
param appInsightsConnectionString string = 'unset'

@description('OIDC provider Authority URL (not secret) — blank until a real OIDC provider is registered; Program.cs treats a blank ClientId as "OIDC not configured yet" rather than failing every request.')
param oidcAuthority string = ''

@description('OIDC provider Client ID (not secret) — blank until a real OIDC provider is registered.')
param oidcClientId string = ''

@description('Custom domain hostname to claim on this Container App (e.g. app.example.com) — blank until DNS is manually verified with the external DNS provider (docs/local-vs-azure-deltas.md#D5). When non-blank, always claims the hostname on ingress; whether that claim is TLS-bound depends on customDomainCertificateReady.')
param customDomainName string = ''

@description('Whether a managed certificate for customDomainName already exists (see container-apps-environment.bicep). false: the hostname is claimed with bindingType Disabled and no certificate, which is required before Azure will let a certificate be created for it (RequireCustomHostnameInEnvironment). true: the claim is upgraded to bindingType SniEnabled with the real certificate. Never set via main.bicepparam.')
param customDomainCertificateReady bool = false

@description('Resource ID of the managed certificate to bind for customDomainName — sourced from container-apps-environment.bicep\'s managedCertificateId output. Ignored when customDomainCertificateReady is false.')
param managedCertificateId string = ''

@description('Scale-to-zero minimum replica count (AD-6/AD-7)')
param minReplicas int = 0

@description('Maximum replica count. Keep it at 1: the app is correct only with one active instance (AD-24), because the upload registry, recompute lock, temp-file payloads, in-process queue, startup sweep and rate limiter are process-local. See docs/self-hosting.md, "Run exactly one instance".')
param maxReplicas int = 1

@description('Port the ingress health-checks and routes traffic to — must match whatever image is currently deployed (placeholderImage listens on 80; the real app image listens on 8080 per Dockerfile ASPNETCORE_HTTP_PORTS)')
param targetPort int = 8080

@description('Name of the Key Vault that holds the Data Protection wrapping key (AD-17). Blank leaves the key-vault role assignment out, so this module stays usable on its own.')
param dataProtectionKeyVaultName string = ''

@description('Name of the Key Vault key that wraps the Data Protection key ring (AD-17). Required together with dataProtectionKeyVaultName.')
param dataProtectionKeyName string = ''

@description('Versionless URI of the Data Protection wrapping key, handed to the app as DataProtection__KeyVaultKeyId. A URI, not a secret.')
param dataProtectionKeyUri string = ''

// Well-known built-in role definition ID for "AcrPull" — stable across subscriptions/tenants.
var acrPullRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')

// Well-known built-in role definition ID for "Key Vault Crypto Service Encryption User": grants
// keys/read, keys/wrap and keys/unwrap data actions and nothing else — exactly what the Data
// Protection Key Vault encryptor needs (AD-17).
var keyVaultCryptoServiceEncryptionUserRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'e147488a-f6f5-4113-8e2d-b22465e65bf6')

var hasDataProtectionKey = !empty(dataProtectionKeyVaultName) && !empty(dataProtectionKeyName)

// Trimmed so a whitespace-only override (e.g. accidental ' ') doesn't pass !empty() and bind a
// garbage hostname — matches the same guard in container-apps-environment.bicep.
var trimmedCustomDomainName = trim(customDomainName)

resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' existing = {
  name: containerRegistryName
}

// `existing` references are only evaluated where something uses them, so with the params left
// blank (module used on its own) nothing is looked up and the role assignment below is not deployed.
resource dataProtectionVault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: dataProtectionKeyVaultName
}

resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2024-11-01' existing = {
  parent: dataProtectionVault
  name: dataProtectionKeyName
}

resource containerApp 'Microsoft.App/containerApps@2026-01-01' = {
  name: name
  location: location
  // System-assigned managed identity — used for AcrPull below; no shared/admin registry credentials.
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironmentId
    workloadProfileName: 'Consumption'
    configuration: {
      ingress: {
        external: true
        targetPort: targetPort
        transport: 'auto'
        // Claimed only once customDomainName is explicitly overridden at deploy time (never via
        // main.bicepparam — see docs/local-vs-azure-deltas.md#D5). Empty by default, so ingress
        // behavior is unchanged until DNS is manually verified with the external DNS provider.
        // The hostname claim (Disabled, no cert) must land on its own deploy before Azure will
        // allow a managed certificate to be created for it — see customDomainCertificateReady.
        customDomains: !empty(trimmedCustomDomainName) ? [
          customDomainCertificateReady ? {
            name: trimmedCustomDomainName
            certificateId: managedCertificateId
            bindingType: 'SniEnabled'
          } : {
            name: trimmedCustomDomainName
            bindingType: 'Disabled'
          }
        ] : []
      }
      // ACR pull credential via the Container App's own system-assigned identity — no shared
      // admin credentials. Story 1.2 deliberately omitted this entry on the from-scratch
      // deployment: declaring it before the AcrPull role assignment below existed made the
      // platform eagerly validate it during provisioning and fail with a 401 (the role can only
      // be created after this resource exists, since it needs containerApp.identity.principalId,
      // so it can't have propagated yet on that same first deployment). That race doesn't apply
      // on redeploys once the Container App and role assignment already exist live in Azure —
      // Story 1.3 re-adds this entry now that a real ACR image needs to authenticate to pull it.
      registries: [
        {
          server: registry.properties.loginServer
          identity: 'system'
        }
      ]
      // ai-api-key and oidc-client-secret are reserved with a non-empty placeholder value:
      // Container Apps rejects a secret whose value is an empty string (it requires a
      // non-empty value or a Key Vault reference), so a genuinely-unset secret can't be
      // declared with value: ''. Story 1.5 overwrites oidc-client-secret with a real value, and Story 9.2
      // (the real AI backend, Epic 9) overwrites ai-api-key, via aiApiKeySecretValue/oidcClientSecretValue
      // threaded from main.bicep. The AI adapter itself exists since Story 6.3; only the backend is undecided.
      secrets: [
        {
          name: 'db-connection-string'
          value: databaseConnectionString
        }
        {
          name: 'storage-queue-connection-string'
          value: storageQueueConnectionString
        }
        {
          name: 'ai-api-key'
          value: aiApiKeySecretValue
        }
        {
          name: 'oidc-client-secret'
          value: oidcClientSecretValue
        }
        {
          name: 'appinsights-connection-string'
          value: appInsightsConnectionString
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'energy-tracker'
          image: placeholderImage
          env: [
            // Database:Provider (AD-2) — sourced from the same parameter that drove which DB
            // module main.bicep deployed; the two must never disagree.
            {
              name: 'Database__Provider'
              value: databaseProvider
            }
            {
              name: 'ConnectionStrings__Default'
              secretRef: 'db-connection-string'
            }
            // JobQueue adapter selection (AD-6) — AzureStorageQueue selects the cloud adapter
            // (AzureStorageQueueJobQueue).
            {
              name: 'JobQueue__Provider'
              value: 'AzureStorageQueue'
            }
            {
              name: 'JobQueue__ConnectionString'
              secretRef: 'storage-queue-connection-string'
            }
            // AD-8 AI backend — the four Ai:* keys the code reads (AiPlausibilityBackendConfiguration).
            // Left unset: an empty Ai__Endpoint resolves to the no-op client; Story 9.2 sets the real values.
            // Model and BackendLabel are plain, non-secret values; ApiKey is a Container App secret.
            {
              name: 'Ai__Endpoint'
              value: ''
            }
            {
              name: 'Ai__ApiKey'
              secretRef: 'ai-api-key'
            }
            {
              name: 'Ai__Model'
              value: ''
            }
            {
              name: 'Ai__BackendLabel'
              value: ''
            }
            // Story 1.5 (household provisioning via OIDC) — Authority/ClientId are plain,
            // non-secret env vars; ClientSecret is a Container App secret (see 'secrets' above).
            {
              name: 'OIDC__Authority'
              value: oidcAuthority
            }
            {
              name: 'OIDC__ClientId'
              value: oidcClientId
            }
            {
              name: 'OIDC__ClientSecret'
              secretRef: 'oidc-client-secret'
            }
            // AD-19 OTel extension — Program.cs reads Otel:Exporter exactly once at the
            // composition root (Consistency Conventions) to select AzureMonitor here vs. Otlp
            // in docker-compose.yml's local dev config.
            {
              name: 'Otel__Exporter'
              value: 'AzureMonitor'
            }
            {
              name: 'Otel__AzureMonitorConnectionString'
              secretRef: 'appinsights-connection-string'
            }
            // AD-17 (audit S2): Program.cs reads DataProtection:KeyVaultKeyId once at the composition
            // root and wraps the Data Protection key ring with this key via the system-assigned
            // identity. A URI, not key material, so a plain env var rather than a secret.
            {
              name: 'DataProtection__KeyVaultKeyId'
              value: dataProtectionKeyUri
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

resource acrPullRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, containerApp.id, 'AcrPull')
  scope: registry
  properties: {
    roleDefinitionId: acrPullRoleDefinitionId
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Scoped to the KEY, not the vault (AD-17: "on that key only"): the identity can wrap/unwrap with
// this one key and nothing else in the vault. Lives here rather than in key-vault.bicep because it
// needs containerApp.identity.principalId while the app needs the key URI (would be circular).
resource dataProtectionKeyRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (hasDataProtectionKey) {
  name: guid(dataProtectionKey.id, containerApp.id, 'KeyVaultCryptoServiceEncryptionUser')
  scope: dataProtectionKey
  properties: {
    roleDefinitionId: keyVaultCryptoServiceEncryptionUserRoleDefinitionId
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output id string = containerApp.id
output name string = containerApp.name
output fqdn string = containerApp.properties.configuration.ingress.fqdn
// Available immediately after any deploy, regardless of customDomainName — needed to create the
// asuid.<subdomain> TXT record at the external DNS provider before a managed certificate can be
// requested (docs/local-vs-azure-deltas.md#D5).
output customDomainVerificationId string = containerApp.properties.customDomainVerificationId
