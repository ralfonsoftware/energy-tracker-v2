using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.6 (AC #3, #7; AD-17): the real composition root fails closed outside Development. No
// database is needed: nothing in this path touches one (the revocation setting is left unset, so the
// key ring is never read), the same shape as OidcConfigurationTests.
public class DataProtectionStartupTests
{
    [Fact]
    public void A_production_host_with_no_protector_and_no_opt_out_refuses_to_start()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

        var exception = Should.Throw<Exception>(() => factory.CreateClient());

        MessagesOf(exception).ShouldContain(
            message => message.Contains("DataProtection:AllowUnprotectedKeys"),
            "Production without a Data Protection protector must fail with the actionable message from DataProtectionKeyProtection.");
    }

    [Fact]
    public async Task A_production_host_with_the_explicit_opt_out_starts_and_answers_health()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("DataProtection:AllowUnprotectedKeys", "true");
        });

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue();
    }

    [Fact]
    public async Task The_default_test_environment_still_starts_with_nothing_configured()
    {
        // Every other API test host runs in Development and configures no Data Protection setting;
        // fail closed must not reach them.
        await using var factory = new WebApplicationFactory<Program>();

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue();
    }

    private static IEnumerable<string> MessagesOf(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current.Message;
        }
    }
}
