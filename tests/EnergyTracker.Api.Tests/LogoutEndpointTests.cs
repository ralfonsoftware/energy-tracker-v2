using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.8 (AC #3, #5, #6; AD-27, AD-17, audit S9): logout is a POST that needs the CSRF header and is
// answered with JSON carrying the end-session URL (a fetch cannot follow the provider's 302, and the
// production CSP's form-action would block a form POST). Hosts run as Production without a database;
// OIDC is unconfigured, which is the "local sign-out only" path. The federated path is covered by
// FederatedLogoutTests and the live Auth0 check.
public class LogoutEndpointTests : IDisposable
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1";

    private readonly string _webRoot = Directory.CreateTempSubdirectory("energy-tracker-webroot-").FullName;
    private readonly WebApplicationFactory<Program> _factory;

    public LogoutEndpointTests()
    {
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>t</title>");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.UseSetting("DataProtection:AllowUnprotectedKeys", "true");
            builder.UseSetting("Database:Provider", "Postgres");
            builder.UseSetting("ConnectionStrings:Default", UnreachableDatabase);
            builder.UseWebRoot(_webRoot);
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_webRoot, recursive: true);
    }

    private static HttpRequestMessage LogoutRequest(bool withHeader, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/logout");
        if (withHeader)
        {
            request.Headers.Add("X-Requested-With", "fetch");
        }
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return request;
    }

    private static bool ExpiresTheAuthCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
        && values.Any(v => v.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal)
                           && v.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task POST_logout_with_the_header_answers_200_json_with_a_local_redirect_url_and_expires_the_cookie()
    {
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(LogoutRequest(withHeader: true, cookie: ".AspNetCore.Cookies=stale"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        response.Headers.Location.ShouldBeNull();
        var body = await response.Content.ReadFromJsonAsync<LogoutBody>(TestContext.Current.CancellationToken);
        body!.RedirectUrl.ShouldBe("/");
        ExpiresTheAuthCookie(response).ShouldBeTrue("The auth cookie must be cleared on the logout response.");
    }

    [Fact]
    public async Task POST_logout_without_the_header_is_a_400_and_clears_nothing()
    {
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(LogoutRequest(withHeader: false, cookie: ".AspNetCore.Cookies=stale"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Fact]
    public async Task POST_logout_works_without_any_authentication()
    {
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(LogoutRequest(withHeader: true), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GET_logout_no_longer_signs_anyone_out()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/logout");
        request.Headers.Add("Cookie", ".AspNetCore.Cookies=stale");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        ExpiresTheAuthCookie(response).ShouldBeFalse();
        (response.Headers.Location?.ToString() ?? string.Empty).ShouldNotContain("logout");
        ((int)response.StatusCode).ShouldNotBeInRange(300, 399);
    }

    [Fact]
    public async Task POST_logout_carries_the_security_headers_and_is_not_rate_limited()
    {
        using var client = _factory.CreateClient();

        for (var i = 0; i < 30; i++)
        {
            using var response = await client.SendAsync(LogoutRequest(withHeader: true), TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Headers.Contains("Content-Security-Policy").ShouldBeTrue();
            response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        }
    }

    private sealed record LogoutBody(string RedirectUrl);
}
