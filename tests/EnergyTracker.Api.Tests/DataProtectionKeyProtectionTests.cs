using Azure.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.6 (AC #2, #3, #7; AD-17, audit S2): how the Data Protection key ring is protected is
// chosen from configuration at the composition root, and outside Development it fails closed. This
// is the selection matrix as a pure unit test: no host, no database, no network. The Azure Key Vault
// branch is proven by selection and by the encryptor type only (a stub credential, so nothing calls
// Key Vault); the real proof is the live check on Azure.
public class DataProtectionKeyProtectionTests
{
    private const string KeyVaultKeyIdSetting = "DataProtection:KeyVaultKeyId";
    private const string CertificateSetting = "DataProtection:CertificateBase64";
    private const string AllowUnprotectedSetting = "DataProtection:AllowUnprotectedKeys";
    private const string RevokeSetting = "DataProtection:RevokeKeysCreatedBeforeUtc";
    private const string VaultKeyUri = "https://etprodabc123kv.vault.azure.net/keys/data-protection";

    [Fact]
    public void Production_with_nothing_configured_refuses_to_start_and_names_every_way_out()
    {
        var exception = Should.Throw<InvalidOperationException>(() => Configure([]));

        exception.Message.ShouldContain(KeyVaultKeyIdSetting);
        exception.Message.ShouldContain(CertificateSetting);
        exception.Message.ShouldContain(AllowUnprotectedSetting);
        exception.Message.ShouldContain("docs/self-hosting.md");
    }

    [Fact]
    public void Production_with_the_explicit_opt_out_selects_unprotected_keys_and_installs_no_encryptor()
    {
        var (result, options) = Configure(new() { [AllowUnprotectedSetting] = "true" });

        result.Mode.ShouldBe(DataProtectionKeyProtectionMode.UnprotectedOptOut);
        options.XmlEncryptor.ShouldBeNull();
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("True")]
    public void The_opt_out_is_case_insensitive(string value)
    {
        var (result, _) = Configure(new() { [AllowUnprotectedSetting] = value });

        result.Mode.ShouldBe(DataProtectionKeyProtectionMode.UnprotectedOptOut);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yes")]
    [InlineData("1")]
    public void Anything_but_exactly_true_still_fails_closed(string value)
    {
        var exception = Should.Throw<InvalidOperationException>(() => Configure(new() { [AllowUnprotectedSetting] = value }));

        exception.Message.ShouldContain(AllowUnprotectedSetting);
    }

    [Fact]
    public void Production_with_a_certificate_selects_the_certificate_encryptor()
    {
        var (result, options) = Configure(CertificateSettings());

        result.Mode.ShouldBe(DataProtectionKeyProtectionMode.Certificate);
        options.XmlEncryptor.ShouldNotBeNull();
        options.XmlEncryptor.GetType().Name.ShouldContain("Certificate");
    }

    [Fact]
    public void Production_with_a_key_vault_key_id_selects_the_azure_key_vault_encryptor()
    {
        var (result, options) = Configure(new() { [KeyVaultKeyIdSetting] = VaultKeyUri });

        result.Mode.ShouldBe(DataProtectionKeyProtectionMode.AzureKeyVault);
        options.XmlEncryptor.ShouldNotBeNull();
        options.XmlEncryptor.GetType().Name.ShouldContain("KeyVault");
    }

    [Fact]
    public void Key_vault_wins_over_a_certificate_and_says_the_certificate_is_ignored()
    {
        var settings = CertificateSettings();
        settings[KeyVaultKeyIdSetting] = VaultKeyUri;

        var (result, options) = Configure(settings);

        result.Mode.ShouldBe(DataProtectionKeyProtectionMode.AzureKeyVault);
        options.XmlEncryptor.ShouldNotBeNull().GetType().Name.ShouldContain("KeyVault");
        result.Warnings.ShouldContain(w => w.Contains("certificate", StringComparison.OrdinalIgnoreCase) && w.Contains("ignored", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_configured_protector_beats_the_opt_out_and_the_opt_out_is_reported_as_ignored()
    {
        var (result, options) = Configure(new() { [KeyVaultKeyIdSetting] = VaultKeyUri, [AllowUnprotectedSetting] = "true" });

        result.Mode.ShouldBe(DataProtectionKeyProtectionMode.AzureKeyVault);
        options.XmlEncryptor.ShouldNotBeNull();
        result.Warnings.ShouldContain(w => w.Contains(AllowUnprotectedSetting) && w.Contains("ignored", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Development_with_nothing_configured_keeps_starting_unprotected_and_says_so()
    {
        var (result, options) = Configure([], environmentName: "Development");

        result.Mode.ShouldBe(DataProtectionKeyProtectionMode.DevelopmentUnprotected);
        options.XmlEncryptor.ShouldBeNull();
        result.Warnings.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("not-a-uri")]
    [InlineData("/keys/data-protection")]
    [InlineData("http://etprodabc123kv.vault.azure.net/keys/data-protection")]
    public void A_key_vault_key_id_that_is_not_an_absolute_https_uri_fails_naming_the_setting(string keyId)
    {
        var exception = Should.Throw<InvalidOperationException>(() => Configure(new() { [KeyVaultKeyIdSetting] = keyId }));

        exception.Message.ShouldContain(KeyVaultKeyIdSetting);
    }

    [Theory]
    [InlineData("tomorrow-ish")]
    [InlineData("2026-10-14T10:00:00")] // no offset: ambiguous, so rejected
    public void An_invalid_revocation_cutoff_fails_naming_the_setting(string cutoff)
    {
        var exception = Should.Throw<InvalidOperationException>(
            () => Configure(new() { [AllowUnprotectedSetting] = "true", [RevokeSetting] = cutoff }));

        exception.Message.ShouldContain(RevokeSetting);
    }

    [Fact]
    public void A_valid_revocation_cutoff_is_parsed_with_its_offset()
    {
        var (result, _) = Configure(new() { [AllowUnprotectedSetting] = "true", [RevokeSetting] = "2026-10-14T10:30:00+02:00" });

        result.RevokeKeysCreatedBefore.ShouldBe(new DateTimeOffset(2026, 10, 14, 8, 30, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_revocation_cutoff_means_nothing_is_revoked(string cutoff)
    {
        var (result, _) = Configure(new() { [AllowUnprotectedSetting] = "true", [RevokeSetting] = cutoff });

        result.RevokeKeysCreatedBefore.ShouldBeNull();
    }

    private static Dictionary<string, string?> CertificateSettings() => new()
    {
        [CertificateSetting] = DataProtectionTestCertificates.CreateSelfSignedPkcs12Base64(),
        ["DataProtection:CertificatePassword"] = DataProtectionTestCertificates.Password,
    };

    private static (DataProtectionKeyProtectionResult Result, KeyManagementOptions Options) Configure(
        Dictionary<string, string?> settings,
        string environmentName = "Production")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);

        var services = new ServiceCollection();
        var builder = services.AddDataProtection();

        var result = DataProtectionKeyProtection.Configure(builder, configuration, environment, Substitute.For<TokenCredential>());

        using var provider = services.BuildServiceProvider();
        return (result, provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value);
    }
}
