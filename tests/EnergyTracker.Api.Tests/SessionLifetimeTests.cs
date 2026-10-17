using System.Net;
using System.Security.Claims;
using EnergyTracker.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.8 (AC #4, #9; AD-17, audit S13): an explicit 14-day sliding window and a 90-day absolute cap
// from an issued-at stamp carried in the ticket. A cookie without a readable stamp is rejected (Ralf's
// decision at create-story review: no grandfathering).
public class SessionLifetimeTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static readonly AuthenticationScheme CookieScheme =
        new(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));

    private sealed class SignOutRecorder : IAuthenticationService
    {
        public List<string?> SignedOutSchemes { get; } = [];

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => throw new NotSupportedException();
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignedOutSchemes.Add(scheme);
            return Task.CompletedTask;
        }
    }

    private static (HttpContext Http, SignOutRecorder Recorder) HttpContextAt(DateTimeOffset now)
    {
        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(now);
        var recorder = new SignOutRecorder();
        var services = new ServiceCollection()
            .AddSingleton(clock)
            .AddSingleton<IAuthenticationService>(recorder)
            .AddLogging()
            .BuildServiceProvider();
        return (new DefaultHttpContext { RequestServices = services }, recorder);
    }

    private static CookieValidatePrincipalContext ValidationContext(HttpContext http, string? stamp)
    {
        var properties = new AuthenticationProperties();
        if (stamp is not null)
        {
            properties.Items[SessionLifetime.IssuedAtItemKey] = stamp;
        }

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "x")], "Cookies")), properties, "Cookies");
        return new CookieValidatePrincipalContext(http, CookieScheme, new CookieAuthenticationOptions(), ticket);
    }

    private static string Stamp(DateTimeOffset when) => when.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void The_sliding_window_and_the_cap_are_written_down()
    {
        SessionLifetime.SlidingWindow.ShouldBe(TimeSpan.FromDays(14));
        SessionLifetime.AbsoluteCap.ShouldBe(TimeSpan.FromDays(90));

        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        options.ExpireTimeSpan.ShouldBe(TimeSpan.FromDays(14));
        options.SlidingExpiration.ShouldBeTrue();
        options.Events.OnSigningIn.ShouldNotBeNull();
        options.Events.OnValidatePrincipal.ShouldNotBeNull();
    }

    [Fact]
    public void A_new_sign_in_is_stamped_with_the_current_time()
    {
        var (http, _) = HttpContextAt(Now);
        var properties = new AuthenticationProperties();
        var context = new CookieSigningInContext(http, CookieScheme, new CookieAuthenticationOptions(),
            new ClaimsPrincipal(), properties, new CookieOptions());

        SessionLifetime.StampIssuedAt(context);

        properties.Items[SessionLifetime.IssuedAtItemKey].ShouldBe(Stamp(Now));
    }

    [Fact]
    public async Task A_session_89_days_old_is_accepted()
    {
        var (http, recorder) = HttpContextAt(Now);
        var context = ValidationContext(http, Stamp(Now.AddDays(-89)));

        await SessionLifetime.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
        recorder.SignedOutSchemes.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_session_91_days_old_is_rejected_and_signed_out()
    {
        var (http, recorder) = HttpContextAt(Now);
        var context = ValidationContext(http, Stamp(Now.AddDays(-91)));

        await SessionLifetime.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        recorder.SignedOutSchemes.ShouldBe([CookieAuthenticationDefaults.AuthenticationScheme]);
    }

    [Fact]
    public async Task A_session_exactly_90_days_old_is_rejected()
    {
        var (http, _) = HttpContextAt(Now);
        var context = ValidationContext(http, Stamp(Now.AddDays(-90)));

        await SessionLifetime.ValidateAsync(context);

        context.Principal.ShouldBeNull();
    }

    [Fact]
    public async Task A_cookie_without_a_stamp_is_rejected_and_signed_out()
    {
        var (http, recorder) = HttpContextAt(Now);
        var context = ValidationContext(http, stamp: null);

        await SessionLifetime.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        recorder.SignedOutSchemes.ShouldBe([CookieAuthenticationDefaults.AuthenticationScheme]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("2026-13-45T00:00:00Z")]
    public async Task An_unparsable_stamp_is_rejected(string stamp)
    {
        var (http, recorder) = HttpContextAt(Now);
        var context = ValidationContext(http, stamp);

        await SessionLifetime.ValidateAsync(context);

        context.Principal.ShouldBeNull();
        recorder.SignedOutSchemes.ShouldBe([CookieAuthenticationDefaults.AuthenticationScheme]);
    }

    [Fact]
    public async Task A_stamp_in_the_future_is_accepted()
    {
        var (http, _) = HttpContextAt(Now);
        var context = ValidationContext(http, Stamp(Now.AddMinutes(5)));

        await SessionLifetime.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
    }

    [Fact]
    public async Task Sliding_renewal_keeps_the_stamp()
    {
        var (http, _) = HttpContextAt(Now);
        var issued = Stamp(Now.AddDays(-30));
        var context = ValidationContext(http, issued);
        context.Properties.IssuedUtc = Now.AddDays(-1);
        context.Properties.ExpiresUtc = Now.AddDays(13);

        await SessionLifetime.ValidateAsync(context);

        context.Principal.ShouldNotBeNull();
        context.Properties.Items[SessionLifetime.IssuedAtItemKey].ShouldBe(issued);
    }

    // Wiring: proves Program.cs registered the events on the real cookie scheme. The default scheme is
    // restored to Cookies for this host, and a ticket is minted through the real TicketDataFormat.
    private HttpClient CookieClient(out Func<DateTimeOffset?, string> mint)
    {
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = o.DefaultAuthenticateScheme = o.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })));
        var cookieOptions = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        mint = issuedAt =>
        {
            var properties = new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(14) };
            if (issuedAt is not null)
            {
                properties.Items[SessionLifetime.IssuedAtItemKey] = Stamp(issuedAt.Value);
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "wiring-subject"), new Claim(HouseholdClaimTypes.ValidatedIssuer, "https://test-issuer.example")],
                CookieAuthenticationDefaults.AuthenticationScheme));
            return cookieOptions.TicketDataFormat.Protect(new AuthenticationTicket(principal, properties, CookieAuthenticationDefaults.AuthenticationScheme));
        };
        return host.CreateClient();
    }

    private static async Task<HttpStatusCode> GetSessionAsync(HttpClient client, string ticket)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        request.Headers.Add("Cookie", $".AspNetCore.Cookies={ticket}");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    [Fact]
    public async Task Wiring_a_real_cookie_91_days_old_answers_401_and_a_fresh_one_answers_200()
    {
        using var client = CookieClient(out var mint);

        (await GetSessionAsync(client, mint(DateTimeOffset.UtcNow.AddDays(-91)))).ShouldBe(HttpStatusCode.Unauthorized);
        (await GetSessionAsync(client, mint(DateTimeOffset.UtcNow.AddDays(-1)))).ShouldBe(HttpStatusCode.OK);
        (await GetSessionAsync(client, mint(null))).ShouldBe(HttpStatusCode.Unauthorized);
    }
}
