using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.7 (AC #1, #6; AD-27, audit S3): production-only security headers on every response, HSTS
// over HTTPS, and none of it in Development. Hosts run as Production with the Data Protection
// opt-out (AD-17, Story 11.6); no database is needed for anything except the error-response case.
public class SecurityHeadersTests : IDisposable
{
    private const string ExpectedCsp =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self'; " +
        "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    private readonly string _webRoot = Directory.CreateTempSubdirectory("energy-tracker-webroot-").FullName;

    public SecurityHeadersTests()
    {
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>t</title>");
        File.WriteAllText(Path.Combine(_webRoot, "theme-init.js"), "/* theme */");
    }

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);

    private WebApplicationFactory<Program> ProductionFactory(Action<IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.UseSetting("DataProtection:AllowUnprotectedKeys", "true");
            builder.UseWebRoot(_webRoot);
            configure?.Invoke(builder);
        });

    private static void AssertHardeningHeaders(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Content-Security-Policy", out var csp).ShouldBeTrue("Content-Security-Policy is missing.");
        csp!.Single().ShouldBe(ExpectedCsp);
        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        response.Headers.GetValues("Referrer-Policy").Single().ShouldBe("same-origin");
    }

    [Fact]
    public async Task Production_health_response_carries_the_hardening_headers()
    {
        await using var factory = ProductionFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertHardeningHeaders(response);
    }

    [Fact]
    public async Task Production_unauthenticated_api_response_carries_the_hardening_headers()
    {
        await using var factory = ProductionFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/session", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        AssertHardeningHeaders(response);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/theme-init.js")]
    [InlineData("/join/abc")]
    public async Task Production_static_file_and_spa_fallback_responses_carry_the_hardening_headers(string path)
    {
        await using var factory = ProductionFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertHardeningHeaders(response);
    }

    [Fact]
    public async Task Production_error_response_written_by_the_exception_handler_carries_the_hardening_headers()
    {
        // An authenticated request whose Household lookup cannot reach the database: the failure is
        // thrown inside the pipeline and UseExceptionHandler writes the 500 ProblemDetails after it
        // reset the response. Headers set before next() would be lost there.
        await using var factory = ProductionFactory(builder =>
        {
            builder.UseSetting("Database:Provider", "Postgres");
            builder.UseSetting("ConnectionStrings:Default", "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1");
            builder.ConfigureTestServices(services => services
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, "error-subject");

        var response = await client.GetAsync("/api/session", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        AssertHardeningHeaders(response);
    }

    [Fact]
    public async Task Production_https_response_carries_hsts_and_plain_http_does_not()
    {
        await using var factory = ProductionFactory();
        using var https = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://energytracker.example") });
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://energytracker.example") });

        var secure = await https.GetAsync("/health", TestContext.Current.CancellationToken);
        var plain = await http.GetAsync("/health", TestContext.Current.CancellationToken);

        secure.Headers.Contains("Strict-Transport-Security").ShouldBeTrue("HSTS must be sent over HTTPS outside Development.");
        plain.Headers.Contains("Strict-Transport-Security").ShouldBeFalse("HSTS must never be sent over plain HTTP.");
    }

    [Fact]
    public async Task Development_responses_carry_none_of_the_hardening_headers()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseWebRoot(_webRoot));
        using var https = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://energytracker.example") });

        foreach (var path in new[] { "/health", "/api/session", "/theme-init.js" })
        {
            var response = await https.GetAsync(path, TestContext.Current.CancellationToken);

            foreach (var header in new[] { "Content-Security-Policy", "X-Content-Type-Options", "Referrer-Policy", "Strict-Transport-Security" })
            {
                response.Headers.Contains(header).ShouldBeFalse($"{header} must not be set in Development ({path}).");
            }
        }
    }
}
