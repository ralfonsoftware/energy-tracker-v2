using System.Security.Claims;
using EnergyTracker.Api;
using EnergyTracker.Application;
using EnergyTracker.Infrastructure;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.12 (audit C2, AD-17): the middleware alone, on a DefaultHttpContext, with the resolver
// substituted. Which requests pay for the lookup, that the result lands in HttpContext.Items, and that a
// failing lookup is never swallowed into "no Household".
public class HouseholdMembershipMiddlewareTests
{
    private static ClaimsPrincipal Authenticated() =>
        new(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "subject-1"),
                new Claim(HouseholdClaimTypes.ValidatedIssuer, "https://issuer.example"),
            },
            authenticationType: "Test"));

    private static HouseholdMembershipResolver SubstituteResolver() =>
        Substitute.For<HouseholdMembershipResolver>(Substitute.For<IDbContextFactory<HouseholdMembershipDbContext>>());

    private static RequestDelegate Pipeline(IServiceProvider services, Action onNext)
    {
        var builder = new ApplicationBuilder(services);
        builder.UseHouseholdMembership();
        builder.Run(_ =>
        {
            onNext();
            return Task.CompletedTask;
        });
        return builder.Build();
    }

    private static async Task<(DefaultHttpContext Http, int NextCalls)> RunAsync(
        HouseholdMembershipResolver resolver, string path, ClaimsPrincipal user, CancellationToken aborted = default)
    {
        var services = new ServiceCollection().AddSingleton(resolver).BuildServiceProvider();
        var nextCalls = 0;
        var pipeline = Pipeline(services, () => nextCalls++);
        var http = new DefaultHttpContext { RequestServices = services, User = user, RequestAborted = aborted };
        http.Request.Path = path;

        await pipeline(http);
        return (http, nextCalls);
    }

    [Fact]
    public async Task An_authenticated_api_request_stores_the_resolvers_result_in_Items_and_calls_next_once()
    {
        var expected = new ResolvedHouseholdMembership(Guid.NewGuid(), Guid.NewGuid());
        var resolver = SubstituteResolver();
        resolver.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(expected);

        var (http, nextCalls) = await RunAsync(resolver, "/api/status", Authenticated(), TestContext.Current.CancellationToken);

        nextCalls.ShouldBe(1);
        ResolvedHouseholdMembership.TryGet(http, out var stored).ShouldBeTrue();
        stored.ShouldBe(expected);
        await resolver.Received(1).ResolveAsync(http.User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_resolved_no_household_is_stored_too_so_the_rest_of_the_request_does_not_look_again()
    {
        var resolver = SubstituteResolver();
        resolver.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(ResolvedHouseholdMembership.None);

        var (http, _) = await RunAsync(resolver, "/api/session", Authenticated(), TestContext.Current.CancellationToken);

        ResolvedHouseholdMembership.TryGet(http, out var stored).ShouldBeTrue();
        stored.ShouldBe(ResolvedHouseholdMembership.None);
    }

    [Fact]
    public async Task The_resolver_receives_the_requests_abort_token()
    {
        var resolver = SubstituteResolver();
        resolver.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(ResolvedHouseholdMembership.None);
        using var aborted = new CancellationTokenSource();

        await RunAsync(resolver, "/api/rooms", Authenticated(), aborted.Token);

        await resolver.Received(1).ResolveAsync(Arg.Any<ClaimsPrincipal>(), aborted.Token);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/health")]
    [InlineData("/login")]
    [InlineData("/logout")]
    [InlineData("/assets/index.js")]
    [InlineData("/settings/members")]
    [InlineData("/apix")]
    [InlineData("/apix/session")]
    public async Task A_path_outside_api_does_not_call_the_resolver_and_still_calls_next(string path)
    {
        var resolver = SubstituteResolver();

        var (http, nextCalls) = await RunAsync(resolver, path, Authenticated(), TestContext.Current.CancellationToken);

        nextCalls.ShouldBe(1);
        ResolvedHouseholdMembership.TryGet(http, out _).ShouldBeFalse();
        await resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_unauthenticated_principal_does_not_call_the_resolver_and_still_calls_next()
    {
        var resolver = SubstituteResolver();

        var (http, nextCalls) = await RunAsync(resolver, "/api/rooms", new ClaimsPrincipal(new ClaimsIdentity()), TestContext.Current.CancellationToken);

        nextCalls.ShouldBe(1);
        ResolvedHouseholdMembership.TryGet(http, out _).ShouldBeFalse();
        await resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/")]
    [InlineData("/API/session")]
    [InlineData("/Api/Rooms")]
    public async Task The_api_path_check_follows_StartsWithSegments_semantics_case_insensitively(string path)
    {
        var resolver = SubstituteResolver();
        resolver.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(ResolvedHouseholdMembership.None);

        var (http, _) = await RunAsync(resolver, path, Authenticated(), TestContext.Current.CancellationToken);

        ResolvedHouseholdMembership.TryGet(http, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task A_failing_lookup_propagates_and_is_never_swallowed_into_no_household()
    {
        var resolver = SubstituteResolver();
        resolver.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns<ResolvedHouseholdMembership>(_ => throw new InvalidOperationException("database unavailable"));
        var nextCalls = 0;
        var services = new ServiceCollection().AddSingleton(resolver).BuildServiceProvider();
        var pipeline = Pipeline(services, () => nextCalls++);
        var http = new DefaultHttpContext { RequestServices = services, User = Authenticated() };
        http.Request.Path = "/api/status";

        var thrown = await Record.ExceptionAsync(() => pipeline(http));

        thrown.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("database unavailable");
        nextCalls.ShouldBe(0);
        ResolvedHouseholdMembership.TryGet(http, out _).ShouldBeFalse();
    }
}
