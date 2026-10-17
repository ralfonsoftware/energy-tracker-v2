using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.7 (AC #4, #7, #8; AD-27, audit S3): the built-in limiter returns 429 ProblemDetails above
// the limits in the story's rate-limit table, partitions per OIDC identity (issuer + subject) or per
// client IP for /login, and never lets one policy, identity or /health affect another. The limiter is
// not environment-gated, so the standard Development host is enough. Requests are answered cheaply
// (403 without a Household, 404 for an unknown token) because the limiter runs before the handler.
public class RateLimitingTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private static readonly Dictionary<string, int> Limits = new()
    {
        ["Login"] = 20,
        ["Upload"] = 60,
        ["Export"] = 5,
        ["InviteCreate"] = 10,
        ["InviteRedeem"] = 20,
    };

    private static HttpRequestMessage Request(string policy, int index = 0) => policy switch
    {
        "Login" => new HttpRequestMessage(HttpMethod.Get, "/login"),
        "Upload" => MultipartRequest("/api/smart-plug-imports"),
        "Export" => new HttpRequestMessage(HttpMethod.Get, "/api/household-export"),
        "InviteCreate" => new HttpRequestMessage(HttpMethod.Post, "/api/household-invites"),
        "InviteRedeem" => index % 2 == 0
            ? new HttpRequestMessage(HttpMethod.Get, "/api/household-invites/does-not-exist")
            : new HttpRequestMessage(HttpMethod.Post, "/api/household-invites/does-not-exist/accept"),
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null),
    };

    private static HttpRequestMessage MultipartRequest(string path)
    {
        var content = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "export.csv" } };
        return new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
    }

    private static string UniqueIp() => $"10.{Random.Shared.Next(0, 256)}.{Random.Shared.Next(0, 256)}.{Random.Shared.Next(1, 255)}";

    // /login is IP-keyed and the TestServer peer has no IP, so every test sets its own forwarded client IP.
    private static HttpRequestMessage WithClientIp(HttpRequestMessage request, string ip)
    {
        request.Headers.Add("X-Forwarded-For", ip);
        return request;
    }

    private HttpClient ClientFor(string policy) =>
        policy == "Login" ? factory.CreateClient() : factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        // /login throws inside the host when OIDC is unconfigured (every test host): that is a 500
        // after the limiter ran, which is all these tests need.
        try
        {
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            return response.StatusCode;
        }
        catch (InvalidOperationException)
        {
            return HttpStatusCode.InternalServerError;
        }
    }

    [Theory]
    [InlineData("Login")]
    [InlineData("Upload")]
    [InlineData("Export")]
    [InlineData("InviteCreate")]
    [InlineData("InviteRedeem")]
    public async Task Exceeding_the_limit_returns_429_problem_details_with_retry_after(string policy)
    {
        var limit = Limits[policy];
        var ip = UniqueIp();
        var client = ClientFor(policy);

        for (var i = 0; i < limit; i++)
        {
            var status = await SendAsync(client, WithClientIp(Request(policy, i), ip));
            status.ShouldNotBe(HttpStatusCode.TooManyRequests, $"{policy} request {i + 1} of {limit} must still be allowed.");
        }

        using var rejected = await client.SendAsync(WithClientIp(Request(policy, limit), ip), TestContext.Current.CancellationToken);

        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        rejected.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        rejected.Headers.Contains("Retry-After").ShouldBeTrue();
        int.Parse(rejected.Headers.GetValues("Retry-After").Single()).ShouldBeGreaterThan(0);
        var body = await rejected.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("status").GetInt32().ShouldBe(429);
        body.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Upload_routes_share_one_budget_per_identity()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        for (var i = 0; i < 30; i++)
        {
            (await SendAsync(client, MultipartRequest("/api/smart-plug-imports"))).ShouldNotBe(HttpStatusCode.TooManyRequests);
            (await SendAsync(client, MultipartRequest("/api/household-import"))).ShouldNotBe(HttpStatusCode.TooManyRequests);
        }

        var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/household-import/{Guid.NewGuid()}/confirm");
        (await SendAsync(client, confirm)).ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Invite_preview_and_accept_share_one_budget_per_identity()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        for (var i = 0; i < Limits["InviteRedeem"]; i++)
        {
            (await SendAsync(client, Request("InviteRedeem", i))).ShouldNotBe(HttpStatusCode.TooManyRequests);
        }

        (await SendAsync(client, Request("InviteRedeem", 0))).ShouldBe(HttpStatusCode.TooManyRequests);
        (await SendAsync(client, Request("InviteRedeem", 1))).ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task One_identity_hitting_its_limit_does_not_limit_another_identity_or_another_policy_or_health()
    {
        var subject = Guid.NewGuid().ToString();
        var exhausted = factory.CreateAuthenticatedClient(subject);
        var other = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        for (var i = 0; i < Limits["Export"]; i++)
        {
            await SendAsync(exhausted, Request("Export"));
        }

        (await SendAsync(exhausted, Request("Export"))).ShouldBe(HttpStatusCode.TooManyRequests);

        (await SendAsync(other, Request("Export"))).ShouldNotBe(HttpStatusCode.TooManyRequests);
        (await SendAsync(exhausted, Request("InviteCreate"))).ShouldNotBe(HttpStatusCode.TooManyRequests);
        (await SendAsync(exhausted, new HttpRequestMessage(HttpMethod.Get, "/api/session"))).ShouldNotBe(HttpStatusCode.TooManyRequests);
        (await SendAsync(exhausted, new HttpRequestMessage(HttpMethod.Get, "/health"))).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_same_subject_under_a_different_issuer_is_a_separate_partition()
    {
        var subject = Guid.NewGuid().ToString();
        var first = factory.CreateAuthenticatedClient(subject, issuer: "https://issuer-one.example");
        var second = factory.CreateAuthenticatedClient(subject, issuer: "https://issuer-two.example");
        for (var i = 0; i < Limits["Export"]; i++)
        {
            await SendAsync(first, Request("Export"));
        }

        (await SendAsync(first, Request("Export"))).ShouldBe(HttpStatusCode.TooManyRequests);
        (await SendAsync(second, Request("Export"))).ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Login_is_limited_per_client_ip_and_a_second_client_is_unaffected()
    {
        var client = factory.CreateClient();
        var firstIp = UniqueIp();
        var secondIp = UniqueIp();
        for (var i = 0; i < Limits["Login"]; i++)
        {
            await SendAsync(client, WithClientIp(Request("Login"), firstIp));
        }

        (await SendAsync(client, WithClientIp(Request("Login"), firstIp))).ShouldBe(HttpStatusCode.TooManyRequests);
        (await SendAsync(client, WithClientIp(Request("Login"), secondIp))).ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Rotating_the_leftmost_forwarded_for_entry_does_not_escape_the_login_limit()
    {
        // Ingress appends the real client IP as the rightmost entry; ForwardLimit stays 1, so only that
        // entry counts. A client that forges the leftmost entries keeps landing in the same bucket
        // (the property Task R1 confirms live on Azure).
        var client = factory.CreateClient();
        var realClientIp = UniqueIp();
        for (var i = 0; i < Limits["Login"]; i++)
        {
            await SendAsync(client, WithClientIp(Request("Login"), $"203.0.113.{i + 1}, {realClientIp}"));
        }

        (await SendAsync(client, WithClientIp(Request("Login"), $"203.0.113.200, {realClientIp}"))).ShouldBe(HttpStatusCode.TooManyRequests);
    }
}
