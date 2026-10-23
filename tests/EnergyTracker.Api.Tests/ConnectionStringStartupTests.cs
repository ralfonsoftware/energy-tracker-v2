using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.11 (AC #2, #6; audit S7): outside Development a missing, empty or whitespace
// ConnectionStrings:Default stops the host with an actionable message instead of falling back to a
// hard-coded "change-me" database; in Development the fallback keeps every dev and test host starting.
// No database is needed: the failing paths throw at build, and the passing ones only answer /health,
// which is liveness-only (AD-19), so no connection is ever opened. The Data Protection opt-out is set so
// that check (later in Program.cs) can never be what throws in these tests.
public class ConnectionStringStartupTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1";
    private const string FallbackPassword = "change-me";

    private static WebApplicationFactory<Program> HostIn(string environment, string? connectionString, bool dataProtectionOptOut = true)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            if (dataProtectionOptOut)
            {
                builder.UseSetting("DataProtection:AllowUnprotectedKeys", "true");
            }

            if (connectionString is not null)
            {
                builder.UseSetting("ConnectionStrings:Default", connectionString);
            }
        });
    }

    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "")]
    [InlineData("Production", "   ")]
    [InlineData("Staging", null)]
    [InlineData("Staging", "")]
    public void A_host_outside_Development_without_a_usable_connection_string_refuses_to_start(string environment, string? connectionString)
    {
        using var factory = HostIn(environment, connectionString);

        var exception = Should.Throw<Exception>(() => factory.CreateClient());

        var messages = MessagesOf(exception).ToList();
        messages.ShouldContain(
            message => message.Contains("ConnectionStrings:Default") && message.Contains("ConnectionStrings__Default"),
            "The message must name the key and its environment-variable form for the operator who only sees `docker logs`.");
        messages.ShouldNotContain(
            message => message.Contains(FallbackPassword),
            "The message must never contain the fallback connection string or its password.");
    }

    [Fact]
    public void The_connection_string_check_runs_before_the_Data_Protection_check()
    {
        // With neither configured, the operator is told about the database first: DataProtectionStartupTests
        // sets the connection string and expects the Data Protection message, so the two checks stay in this
        // order and each test proves what its name says.
        using var factory = HostIn("Production", connectionString: null, dataProtectionOptOut: false);

        var exception = Should.Throw<Exception>(() => factory.CreateClient());

        var messages = MessagesOf(exception).ToList();
        messages.ShouldContain(message => message.Contains("ConnectionStrings:Default"));
        messages.ShouldNotContain(message => message.Contains("DataProtection:AllowUnprotectedKeys"));
    }

    [Fact]
    public async Task A_Production_host_with_a_connection_string_and_the_opt_out_starts_and_answers_health()
    {
        await using var factory = HostIn("Production", UnreachableDatabase);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_Development_host_without_a_usable_connection_string_still_starts_on_the_fallback(string? connectionString)
    {
        await using var factory = HostIn("Development", connectionString);

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
