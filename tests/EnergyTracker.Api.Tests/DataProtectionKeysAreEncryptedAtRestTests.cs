using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.6 (AC #2, #8; audit S2): with a certificate configured the key ring rows in
// DataProtectionKeys hold ciphertext, not the plaintext master key, and a second freshly built host
// can still read them. The opt-out test is the contrast that proves the assertion can fail. Real
// Postgres, like DataProtectionColdStartTests. Only counts are asserted; key XML is never printed.
public class DataProtectionKeysAreEncryptedAtRestTests : IAsyncLifetime
{
    private const string Purpose = "EnergyTracker.Tests.EncryptedAtRest";

    private readonly PostgresTestDatabase _container = new();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        await DataProtectionPostgresHost.MigrateAsync(_container.GetConnectionString(), TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task With_a_certificate_the_stored_keys_are_encrypted_and_survive_a_cold_start()
    {
        var settings = new Dictionary<string, string?>
        {
            ["CertificateBase64"] = DataProtectionTestCertificates.CreateSelfSignedPkcs12Base64(),
            ["CertificatePassword"] = DataProtectionTestCertificates.Password,
        };

        string protectedValue;
        await using (var first = DataProtectionPostgresHost.Create(_container.GetConnectionString(), settings))
        {
            using var scope = first.Services.CreateScope();
            protectedValue = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("value");
        }

        await using var second = DataProtectionPostgresHost.Create(_container.GetConnectionString(), settings);
        using var secondScope = second.Services.CreateScope();
        secondScope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(protectedValue)
            .ShouldBe("value");

        var summary = await DataProtectionPostgresHost.SummarizeAsync(second, TestContext.Current.CancellationToken);
        summary.Rows.ShouldBeGreaterThan(0);
        summary.PlaintextMasterKeyRows.ShouldBe(0, "no DataProtectionKeys row may hold a plaintext master key when a certificate is configured");
        summary.EncryptedRows.ShouldBe(summary.Rows, "every key row must be in encrypted form");
    }

    [Fact]
    public async Task With_only_the_explicit_opt_out_the_stored_key_is_plaintext_which_is_the_contrast_the_assertions_rely_on()
    {
        await using var host = DataProtectionPostgresHost.Create(_container.GetConnectionString(), new() { ["AllowUnprotectedKeys"] = "true" });
        using (var scope = host.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("value");
        }

        var summary = await DataProtectionPostgresHost.SummarizeAsync(host, TestContext.Current.CancellationToken);
        summary.PlaintextMasterKeyRows.ShouldBeGreaterThan(0);
        summary.EncryptedRows.ShouldBe(0);
    }
}
