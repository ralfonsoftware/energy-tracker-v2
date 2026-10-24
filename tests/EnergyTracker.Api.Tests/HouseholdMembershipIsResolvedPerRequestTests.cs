using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.12 (audit C2, AD-3, AD-17, AD-26): membership is resolved once per authenticated /api request,
// asynchronously, in middleware, and never carried across requests. The host's membership factory is
// wrapped by a counting decorator whose synchronous CreateDbContext() throws, so a lookup that blocks a
// thread cannot pass, and the number of lookups per request is observable. Every client authenticates with
// the TestAuthHandler headers, not a cookie: the tests prove the path and authentication rules of the
// middleware, and that no code writes a Set-Cookie; the AD-17 "nothing in the cookie" rule itself is
// carried by HouseholdMembershipIsResolvedOncePerRequestTests (no household claim is ever added).
public class HouseholdMembershipIsResolvedPerRequestTests : IClassFixture<EnergyTrackerApiFactory>, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _host;
    private readonly CountingMembershipDbContextFactory _lookups;
    private readonly SetCookieSink _cookies = new();

    public HouseholdMembershipIsResolvedPerRequestTests(EnergyTrackerApiFactory factory)
    {
        _host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var registered = services.Single(d => d.ServiceType == typeof(IDbContextFactory<HouseholdMembershipDbContext>));
            services.Remove(registered);
            services.AddSingleton<IDbContextFactory<HouseholdMembershipDbContext>>(provider =>
                new CountingMembershipDbContextFactory(ResolveInner(provider, registered)));
        }));
        _lookups = (CountingMembershipDbContextFactory)_host.Services.GetRequiredService<IDbContextFactory<HouseholdMembershipDbContext>>();
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static IDbContextFactory<HouseholdMembershipDbContext> ResolveInner(IServiceProvider provider, ServiceDescriptor registered) =>
        (IDbContextFactory<HouseholdMembershipDbContext>)(registered.ImplementationFactory is not null
            ? registered.ImplementationFactory(provider)
            : ActivatorUtilities.CreateInstance(provider, registered.ImplementationType!));

    private sealed record Principal(string Subject, HttpClient Client);

    private Principal NewPrincipal(string? name = null, WebApplicationFactory<Program>? host = null)
    {
        var subject = Guid.NewGuid().ToString();
        var client = (host ?? _host).CreateDefaultClient(_cookies.NewHandler());
        client.DefaultRequestHeaders.Add("X-Requested-With", "fetch");
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, subject);
        client.DefaultRequestHeaders.Add(TestAuthHandler.IssuerHeader, TestAuthHandler.DefaultIssuer);
        if (name is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, name);
        }

        return new Principal(subject, client);
    }

    private static async Task<HouseholdResponse> CreateHouseholdAsync(Principal principal)
    {
        var response = await principal.Client.PostAsJsonAsync("/api/households", new { locale = "en-US", currency = "EUR" }, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken))!;
    }

    private static async Task<HouseholdInviteResponse> CreateInviteAsync(Principal principal)
    {
        var response = await principal.Client.PostAsync("/api/household-invites", null, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<HouseholdInviteResponse>(TestContext.Current.CancellationToken))!;
    }

    private static Task<HttpResponseMessage> AcceptAsync(Principal joiner, string token) =>
        joiner.Client.PostAsync($"/api/household-invites/{token}/accept", null, TestContext.Current.CancellationToken);

    private static async Task<SessionResponse> SessionOfAsync(Principal principal)
    {
        var response = await principal.Client.GetAsync("/api/session", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SessionResponse>(TestContext.Current.CancellationToken))!;
    }

    private static async Task<List<HouseholdMemberResponse>> ListMembersAsync(Principal principal)
    {
        var body = await principal.Client.GetFromJsonAsync<HouseholdMembersResponse>("/api/household-members", Json, TestContext.Current.CancellationToken);
        return body!.Members.ToList();
    }

    // Same shape as HouseholdMemberEndpointsTests.TwoMemberHouseholdAsync: the real create and accept paths.
    private async Task<(Principal A, Principal B, Guid HouseholdId, Guid BId)> TwoMemberHouseholdAsync()
    {
        var a = NewPrincipal("Alex");
        var b = NewPrincipal("Bea");
        var household = await CreateHouseholdAsync(a);
        var invite = await CreateInviteAsync(a);
        (await AcceptAsync(b, invite.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var members = await ListMembersAsync(a);
        return (a, b, household.Id, members.Single(m => !m.IsCurrentMember).Id);
    }

    private async Task<Principal> PrincipalWithHouseholdAsync()
    {
        var principal = NewPrincipal();
        await CreateHouseholdAsync(principal);
        return principal;
    }

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/rooms")]
    public async Task One_authenticated_api_request_runs_exactly_one_asynchronous_membership_lookup(string path)
    {
        var principal = await PrincipalWithHouseholdAsync();
        var before = _lookups.Snapshot();

        var response = await principal.Client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _lookups.Since(before).ShouldBe((Async: 1, Sync: 0));
    }

    [Fact]
    public async Task Two_requests_by_the_same_principal_run_two_lookups_never_one_cached_across_requests()
    {
        var principal = await PrincipalWithHouseholdAsync();
        var before = _lookups.Snapshot();

        await SessionOfAsync(principal);
        await SessionOfAsync(principal);

        _lookups.Since(before).ShouldBe((Async: 2, Sync: 0));
    }

    // signedIn means the test auth headers (no cookie round trip); the lookup rule depends on the path and on
    // whether the principal is authenticated, not on how it authenticated.
    [Theory]
    [InlineData("GET", "/api/rooms", false)]
    [InlineData("GET", "/health", true)]
    [InlineData("GET", "/login", true)]
    [InlineData("POST", "/logout", true)]
    [InlineData("GET", "/", true)]
    [InlineData("GET", "/assets/index.js", true)]
    [InlineData("GET", "/settings/members", true)]
    public async Task Requests_that_are_not_authenticated_api_calls_run_no_membership_lookup(string method, string path, bool signedIn)
    {
        var client = signedIn ? NewPrincipal().Client : _host.CreateDefaultClient(_cookies.NewHandler());
        var before = _lookups.Snapshot();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), TestContext.Current.CancellationToken);

        if (!signedIn)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        _lookups.Since(before).ShouldBe((Async: 0, Sync: 0));
    }

    [Fact]
    public async Task A_removed_member_is_cut_off_on_the_very_next_request_and_nothing_carries_over()
    {
        var (a, b, householdId, bId) = await TwoMemberHouseholdAsync();
        (await SessionOfAsync(b)).HouseholdId.ShouldBe(householdId);

        (await a.Client.DeleteAsync($"/api/household-members/{bId}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var before = _lookups.Snapshot();
        var session = await SessionOfAsync(b);
        _lookups.Since(before).ShouldBe((Async: 1, Sync: 0));
        before = _lookups.Snapshot();
        var rooms = await b.Client.GetAsync("/api/rooms", TestContext.Current.CancellationToken);
        _lookups.Since(before).ShouldBe((Async: 1, Sync: 0));

        session.HasHousehold.ShouldBeFalse();
        rooms.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Creating_a_household_answers_as_before_and_the_next_request_sees_it()
    {
        var principal = NewPrincipal();
        (await SessionOfAsync(principal)).HasHousehold.ShouldBeFalse();

        var created = await CreateHouseholdAsync(principal);

        var next = await SessionOfAsync(principal);
        next.HasHousehold.ShouldBeTrue();
        next.HouseholdId.ShouldBe(created.Id);
    }

    [Fact]
    public async Task Accepting_an_invite_answers_as_before_and_the_next_request_sees_the_inviters_household()
    {
        var inviter = NewPrincipal("Alex");
        var invitee = NewPrincipal("Bea");
        var household = await CreateHouseholdAsync(inviter);
        var invite = await CreateInviteAsync(inviter);
        (await SessionOfAsync(invitee)).HasHousehold.ShouldBeFalse();

        var accepted = await AcceptAsync(invitee, invite.Token);

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var next = await SessionOfAsync(invitee);
        next.HasHousehold.ShouldBeTrue();
        next.HouseholdId.ShouldBe(household.Id);
    }

    [Fact]
    public async Task Removing_a_member_answers_as_before_and_the_removed_members_next_request_has_no_household()
    {
        var (a, b, _, bId) = await TwoMemberHouseholdAsync();

        var removed = await a.Client.DeleteAsync($"/api/household-members/{bId}", TestContext.Current.CancellationToken);

        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await SessionOfAsync(b)).HasHousehold.ShouldBeFalse();
        (await SessionOfAsync(a)).HasHousehold.ShouldBeTrue();
    }

    [Fact]
    public async Task A_principal_without_a_member_row_has_no_household_and_costs_one_lookup_per_request()
    {
        var principal = NewPrincipal();
        var before = _lookups.Snapshot();

        var session = await SessionOfAsync(principal);
        var rooms = await principal.Client.GetAsync("/api/rooms", TestContext.Current.CancellationToken);

        session.HasHousehold.ShouldBeFalse();
        rooms.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _lookups.Since(before).ShouldBe((Async: 2, Sync: 0));
        // Header-authenticated client: this only shows that no code of these flows writes a cookie.
        _cookies.SetCookieHeaders.ShouldBeEmpty("no code of the membership flow writes a Set-Cookie (AD-17).");
    }

    [Fact]
    public async Task A_throttled_request_runs_no_membership_lookup()
    {
        // MemberRemoval allows 10 requests per identity (HttpRateLimiting); a random member id answers cheaply.
        var principal = NewPrincipal();
        for (var i = 0; i < 10; i++)
        {
            await principal.Client.DeleteAsync($"/api/household-members/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        }

        var before = _lookups.Snapshot();
        var throttled = await principal.Client.DeleteAsync($"/api/household-members/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        throttled.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        _lookups.Since(before).ShouldBe((Async: 0, Sync: 0));
    }

    [Fact]
    public async Task A_database_failure_during_the_lookup_answers_the_usual_problem_details_500()
    {
        await using var failing = _host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDbContextFactory<HouseholdMembershipDbContext>>();
            services.AddSingleton<IDbContextFactory<HouseholdMembershipDbContext>, UnavailableMembershipDbContextFactory>();
        }));
        var principal = NewPrincipal(host: failing);

        var response = await principal.Client.GetAsync("/api/session", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("status").GetInt32().ShouldBe(500);
    }

    // The membership database is down: only the async path is used by the middleware, so only it throws.
    private sealed class UnavailableMembershipDbContextFactory : IDbContextFactory<HouseholdMembershipDbContext>
    {
        public HouseholdMembershipDbContext CreateDbContext() => throw new InvalidOperationException("membership database unavailable");

        public Task<HouseholdMembershipDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("membership database unavailable");
    }

    // Counts CreateDbContextAsync and refuses CreateDbContext: the whole request flow must pass without a
    // synchronous membership lookup (audit C2). Sync is counted before it throws, so a test can read it.
    private sealed class CountingMembershipDbContextFactory(IDbContextFactory<HouseholdMembershipDbContext> inner)
        : IDbContextFactory<HouseholdMembershipDbContext>
    {
        private int _async;
        private int _sync;

        public (int Async, int Sync) Snapshot() => (Volatile.Read(ref _async), Volatile.Read(ref _sync));

        public (int Async, int Sync) Since((int Async, int Sync) before)
        {
            var now = Snapshot();
            return (now.Async - before.Async, now.Sync - before.Sync);
        }

        public HouseholdMembershipDbContext CreateDbContext()
        {
            Interlocked.Increment(ref _sync);
            throw new InvalidOperationException("synchronous membership lookup");
        }

        public Task<HouseholdMembershipDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _async);
            return inner.CreateDbContextAsync(cancellationToken);
        }
    }

    // Every Set-Cookie header any client of this host received; one handler per client (a handler
    // instance cannot be chained into two clients), one shared list.
    private sealed class SetCookieSink
    {
        private readonly List<string> _headers = [];

        public IReadOnlyList<string> SetCookieHeaders
        {
            get
            {
                lock (_headers)
                {
                    return _headers.ToList();
                }
            }
        }

        public DelegatingHandler NewHandler() => new Recorder(this);

        private sealed class Recorder(SetCookieSink sink) : DelegatingHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = await base.SendAsync(request, cancellationToken);
                if (response.Headers.TryGetValues("Set-Cookie", out var values))
                {
                    lock (sink._headers)
                    {
                        sink._headers.AddRange(values);
                    }
                }

                return response;
            }
        }
    }
}
