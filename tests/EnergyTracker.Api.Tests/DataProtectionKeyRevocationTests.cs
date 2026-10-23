using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.6 (AC #6; AD-17 "Cutover and revocation"): DataProtection:RevokeKeysCreatedBeforeUtc is
// an opt-in startup lever that retires the old plaintext key ring exactly once. Left in place on
// later restarts it must be a no-op (no extra revocation row per restart). Real Postgres; only
// counts and round trips are asserted, never key XML.
public class DataProtectionKeyRevocationTests : IAsyncLifetime
{
    private const string Purpose = "EnergyTracker.Tests.Revocation";
    private static readonly Dictionary<string, string?> OptOut = new() { ["AllowUnprotectedKeys"] = "true" };

    private readonly PostgresTestDatabase _container = new();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        await DataProtectionPostgresHost.MigrateAsync(_container.GetConnectionString(), TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task Keys_created_before_the_cutoff_are_revoked_and_a_new_key_takes_over()
    {
        var oldValue = await ProtectOnNewHostAsync(OptOut, "old");
        var cutoff = await TakeCutoffAfterAsync();

        await using var host = DataProtectionPostgresHost.Create(_container.GetConnectionString(), WithCutoff(cutoff));
        using var scope = host.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

        Should.Throw<CryptographicException>(() => protector.Unprotect(oldValue));
        protector.Unprotect(protector.Protect("new")).ShouldBe("new");

        var summary = await DataProtectionPostgresHost.SummarizeAsync(host, TestContext.Current.CancellationToken);
        summary.RevocationRows.ShouldBe(1);
        summary.Rows.ShouldBe(3, "old key, one revocation record, one new key");
    }

    [Fact]
    public async Task Restarting_with_the_same_cutoff_still_set_revokes_nothing_more()
    {
        await ProtectOnNewHostAsync(OptOut, "old");
        var cutoff = await TakeCutoffAfterAsync();

        string newValue;
        await using (var second = DataProtectionPostgresHost.Create(_container.GetConnectionString(), WithCutoff(cutoff)))
        {
            using var scope = second.Services.CreateScope();
            newValue = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("new");
        }

        await using var third = DataProtectionPostgresHost.Create(_container.GetConnectionString(), WithCutoff(cutoff));
        using var thirdScope = third.Services.CreateScope();
        thirdScope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(newValue)
            .ShouldBe("new");

        var summary = await DataProtectionPostgresHost.SummarizeAsync(third, TestContext.Current.CancellationToken);
        summary.RevocationRows.ShouldBe(1, "a restart with the setting left in place must not add another revocation record");
        summary.Rows.ShouldBe(3);
    }

    [Fact]
    public async Task Without_the_setting_nothing_is_revoked()
    {
        var oldValue = await ProtectOnNewHostAsync(OptOut, "old");
        await TakeCutoffAfterAsync();

        await using var host = DataProtectionPostgresHost.Create(_container.GetConnectionString(), OptOut);
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(oldValue)
            .ShouldBe("old");

        var summary = await DataProtectionPostgresHost.SummarizeAsync(host, TestContext.Current.CancellationToken);
        summary.RevocationRows.ShouldBe(0);
    }

    private async Task<string> ProtectOnNewHostAsync(Dictionary<string, string?> settings, string plaintext)
    {
        await using var host = DataProtectionPostgresHost.Create(_container.GetConnectionString(), settings);
        using var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect(plaintext);
    }

    // A key's CreationDate is set by the host that created it; the cutoff has to be strictly later
    // and the next host's own new key strictly later still, so give the clock a moment either side.
    private static async Task<DateTimeOffset> TakeCutoffAfterAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        var cutoff = DateTimeOffset.UtcNow;
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        return cutoff;
    }

    private static Dictionary<string, string?> WithCutoff(DateTimeOffset cutoff) => new()
    {
        ["AllowUnprotectedKeys"] = "true",
        ["RevokeKeysCreatedBeforeUtc"] = cutoff.ToString("O", CultureInfo.InvariantCulture),
    };
}
