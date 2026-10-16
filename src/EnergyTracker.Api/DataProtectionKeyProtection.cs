using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace EnergyTracker.Api;

internal enum DataProtectionKeyProtectionMode
{
    AzureKeyVault,
    Certificate,
    UnprotectedOptOut,
    DevelopmentUnprotected,
}

// Warnings are logged at warning level once the host is built (Serilog is only a logger after
// UseSerilog ran), Description names the active protector and never contains anything secret.
internal sealed record DataProtectionKeyProtectionResult(
    DataProtectionKeyProtectionMode Mode,
    string Description,
    IReadOnlyList<string> Warnings,
    DateTimeOffset? RevokeKeysCreatedBefore);

// AD-17 (amendment 2026-10-10), audit S2: the Data Protection key ring that signs and encrypts the
// session cookie is stored in the database by PersistKeysToDbContext, so it must not sit there in
// the clear. Exactly one protector is chosen from DataProtection:* configuration, read once here at
// the composition root. Outside Development the app fails closed: with no protector and no explicit
// opt-out it refuses to start, so leaving a setting out can never silently select the weaker mode.
internal static class DataProtectionKeyProtection
{
    private const string KeyVaultKeyIdSetting = "DataProtection:KeyVaultKeyId";
    private const string CertificateSetting = "DataProtection:CertificateBase64";
    private const string CertificatePasswordSetting = "DataProtection:CertificatePassword";
    private const string AllowUnprotectedSetting = "DataProtection:AllowUnprotectedKeys";
    private const string RevokeSetting = "DataProtection:RevokeKeysCreatedBeforeUtc";

    // credential is only for tests (a stub, so registering the Key Vault protector makes no network
    // call). In production DefaultAzureCredential resolves to the Container App's system-assigned
    // identity on Azure (no AZURE_CLIENT_ID is set, so there is no user-assigned ambiguity) and to
    // the developer's own credentials locally; its first token fetch is lazy, at first key use.
    public static DataProtectionKeyProtectionResult Configure(
        IDataProtectionBuilder builder,
        IConfiguration configuration,
        IHostEnvironment environment,
        TokenCredential? credential = null)
    {
        var keyVaultKeyId = configuration[KeyVaultKeyIdSetting]?.Trim() ?? string.Empty;
        var certificateBase64 = configuration[CertificateSetting]?.Trim() ?? string.Empty;
        var allowUnprotectedValue = configuration[AllowUnprotectedSetting];
        var allowUnprotected = string.Equals(allowUnprotectedValue, "true", StringComparison.OrdinalIgnoreCase);
        var revokeKeysCreatedBefore = ParseRevokeCutoff(configuration[RevokeSetting]);

        var hasKeyVault = keyVaultKeyId.Length > 0;
        var hasCertificate = certificateBase64.Length > 0;
        var warnings = new List<string>();

        if (hasKeyVault)
        {
            if (!Uri.TryCreate(keyVaultKeyId, UriKind.Absolute, out var keyUri) || keyUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException(
                    $"{KeyVaultKeyIdSetting} must be an absolute https URI of a Key Vault key, for example " +
                    "https://<vault>.vault.azure.net/keys/<name>.");
            }

            if (hasCertificate)
            {
                warnings.Add($"{CertificateSetting} is ignored because {KeyVaultKeyIdSetting} is set; Azure Key Vault protects the keys.");
            }

            if (allowUnprotected)
            {
                warnings.Add($"{AllowUnprotectedSetting}=true is ignored because a protector is configured.");
            }

            builder.ProtectKeysWithAzureKeyVault(keyUri, credential ?? new DefaultAzureCredential());
            return new DataProtectionKeyProtectionResult(
                DataProtectionKeyProtectionMode.AzureKeyVault,
                $"Azure Key Vault (vault {keyUri.Host}, key {KeyName(keyUri)})",
                warnings,
                revokeKeysCreatedBefore);
        }

        if (hasCertificate)
        {
            if (allowUnprotected)
            {
                warnings.Add($"{AllowUnprotectedSetting}=true is ignored because a protector is configured.");
            }

            var certificateBytes = Convert.FromBase64String(certificateBase64);
            var certificate = X509CertificateLoader.LoadPkcs12(certificateBytes, configuration[CertificatePasswordSetting] ?? string.Empty);
            builder.ProtectKeysWithCertificate(certificate);
            return new DataProtectionKeyProtectionResult(
                DataProtectionKeyProtectionMode.Certificate,
                $"certificate (subject {certificate.Subject}, thumbprint {certificate.Thumbprint})",
                warnings,
                revokeKeysCreatedBefore);
        }

        if (allowUnprotected)
        {
            warnings.Add($"{AllowUnprotectedSetting}=true: Data Protection keys are stored unprotected in the database. " +
                "Anyone who can read the database or a backup can forge a session cookie.");
            return new DataProtectionKeyProtectionResult(
                DataProtectionKeyProtectionMode.UnprotectedOptOut,
                "none (explicit opt-out)",
                warnings,
                revokeKeysCreatedBefore);
        }

        if (environment.IsDevelopment())
        {
            warnings.Add("Development: Data Protection keys are stored unprotected in the database. " +
                $"Configure {KeyVaultKeyIdSetting} or {CertificateSetting} to protect them.");
            return new DataProtectionKeyProtectionResult(
                DataProtectionKeyProtectionMode.DevelopmentUnprotected,
                "none (Development)",
                warnings,
                revokeKeysCreatedBefore);
        }

        var notExactlyTrue = string.IsNullOrWhiteSpace(allowUnprotectedValue)
            ? string.Empty
            : $" {AllowUnprotectedSetting} is set but is not exactly 'true', so it is not honoured.";
        throw new InvalidOperationException(
            "Data Protection keys must be protected outside Development, and none of the options is configured." +
            notExactlyTrue +
            $" Set {KeyVaultKeyIdSetting} (an Azure Key Vault key URI), or {CertificateSetting} with " +
            $"{CertificatePasswordSetting} (a base64 PKCS12 certificate), or, to deliberately store the keys " +
            $"unprotected, {AllowUnprotectedSetting}=true. See docs/self-hosting.md, section 'Data Protection keys'.");
    }

    // Runs once, right after Build() and before the first request can create or read a key.
    // Logs the active protector, then (only if DataProtection:RevokeKeysCreatedBeforeUtc is set) retires
    // the old key ring with IKeyManager.RevokeAllKeys. Idempotent on purpose: it revokes only if a
    // non-revoked key created before the cutoff still exists, so a restart with the setting left in
    // place adds no further revocation row. With the setting absent the database is never touched.
    public static void ApplyStartupActions(IServiceProvider services, DataProtectionKeyProtectionResult result, ILogger logger)
    {
        if (result.Mode is DataProtectionKeyProtectionMode.AzureKeyVault or DataProtectionKeyProtectionMode.Certificate)
        {
            logger.LogInformation("Data Protection key ring is protected by {Protector}", result.Description);
        }

        foreach (var warning in result.Warnings)
        {
            logger.LogWarning("{DataProtectionWarning}", warning);
        }

        if (result.RevokeKeysCreatedBefore is not { } cutoff)
        {
            return;
        }

        var keyManager = services.GetRequiredService<IKeyManager>();
        var staleKeys = keyManager.GetAllKeys().Count(key => !key.IsRevoked && key.CreationDate < cutoff);
        if (staleKeys == 0)
        {
            logger.LogInformation(
                "Data Protection: no unrevoked keys were created before {Cutoff:o}; nothing to revoke", cutoff);
            return;
        }

        keyManager.RevokeAllKeys(cutoff, "Story 11.6 cutover: plaintext key ring retired");
        logger.LogWarning(
            "Data Protection: revoked {Count} key(s) created before {Cutoff:o}; members sign in again once",
            staleKeys,
            cutoff);
    }

    private static string KeyName(Uri keyUri)
    {
        // https://<vault>.vault.azure.net/keys/<name>[/<version>]
        var segments = keyUri.Segments.Select(s => s.Trim('/')).Where(s => s.Length > 0).ToArray();
        return segments.Length >= 2 ? segments[1] : string.Empty;
    }

    private static DateTimeOffset? ParseRevokeCutoff(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // The offset is required: a bare local-looking timestamp would be read in the server's time
        // zone, and a wrong cutoff revokes the wrong keys.
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            && HasExplicitOffset(value))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"{RevokeSetting} must be an ISO 8601 timestamp with a UTC offset, for example 2026-10-14T10:30:00Z.");
    }

    private static bool HasExplicitOffset(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.EndsWith('Z') || trimmed.EndsWith('z'))
        {
            return true;
        }

        var timeStart = trimmed.IndexOf('T');
        return timeStart >= 0 && trimmed.AsSpan(timeStart).IndexOfAny('+', '-') >= 0;
    }
}
