using System.Security.Claims;
using EnergyTracker.Application;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

// Story 11.12 (audit C2, AD-3, AD-17): the accessor only reads what the membership middleware cached in
// HttpContext.Items for this request. It has no database dependency, so it cannot block a thread on a
// lookup, and it refuses to guess when an authenticated principal reaches it without that value.
public class CurrentHouseholdAccessorTests
{
    private const string Subject = "auth0|subject-that-must-never-appear-in-an-error";
    private const string Issuer = "https://issuer-that-must-never-appear-in-an-error.example/";

    private static ClaimsPrincipal PrincipalWith(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Test"));

    private static ClaimsPrincipal MemberPrincipal() =>
        PrincipalWith(
            new Claim(ClaimTypes.NameIdentifier, Subject, ClaimValueTypes.String, Issuer),
            new Claim(HouseholdClaimTypes.ValidatedIssuer, Issuer));

    private static (CurrentHouseholdAccessor Accessor, DefaultHttpContext Http, JobHouseholdContext Job) AccessorOnRequest(ClaimsPrincipal user)
    {
        var http = new DefaultHttpContext { User = user };
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        httpContextAccessor.HttpContext.Returns(http);
        var job = new JobHouseholdContext();
        return (new CurrentHouseholdAccessor(httpContextAccessor, job), http, job);
    }

    // NSubstitute auto-substitutes an abstract-class property such as HttpContext; a background job scope
    // has none, so say so explicitly.
    private static IHttpContextAccessor NoHttpContext()
    {
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        httpContextAccessor.HttpContext.Returns((HttpContext?)null);
        return httpContextAccessor;
    }

    [Fact]
    public void Without_an_HttpContext_the_job_path_applies_exactly_as_before()
    {
        var householdId = Guid.NewGuid();
        var httpContextAccessor = NoHttpContext();
        var job = new JobHouseholdContext { HouseholdId = householdId };
        var accessor = new CurrentHouseholdAccessor(httpContextAccessor, job);

        accessor.HouseholdId.ShouldBe(householdId);
        accessor.HouseholdMemberId.ShouldBeNull();
    }

    [Fact]
    public void Without_an_HttpContext_and_no_job_household_both_are_null()
    {
        var accessor = new CurrentHouseholdAccessor(NoHttpContext(), new JobHouseholdContext());

        accessor.HouseholdId.ShouldBeNull();
        accessor.HouseholdMemberId.ShouldBeNull();
    }

    [Fact]
    public void A_cached_membership_is_returned_as_is()
    {
        var memberId = Guid.NewGuid();
        var householdId = Guid.NewGuid();
        var (accessor, http, _) = AccessorOnRequest(MemberPrincipal());
        ResolvedHouseholdMembership.Store(http, new ResolvedHouseholdMembership(memberId, householdId));

        accessor.HouseholdId.ShouldBe(householdId);
        accessor.HouseholdMemberId.ShouldBe(memberId);
    }

    [Fact]
    public void A_cached_no_household_returns_null_even_when_the_principal_has_both_claims()
    {
        var (accessor, http, _) = AccessorOnRequest(MemberPrincipal());
        ResolvedHouseholdMembership.Store(http, ResolvedHouseholdMembership.None);

        accessor.HouseholdId.ShouldBeNull();
        accessor.HouseholdMemberId.ShouldBeNull();
    }

    [Fact]
    public void The_job_household_is_ignored_on_an_HTTP_request()
    {
        var (accessor, http, job) = AccessorOnRequest(MemberPrincipal());
        job.HouseholdId = Guid.NewGuid();
        ResolvedHouseholdMembership.Store(http, ResolvedHouseholdMembership.None);

        accessor.HouseholdId.ShouldBeNull();
    }

    [Fact]
    public void Reading_twice_reads_the_same_cached_value_each_time()
    {
        var householdId = Guid.NewGuid();
        var (accessor, http, _) = AccessorOnRequest(MemberPrincipal());
        ResolvedHouseholdMembership.Store(http, new ResolvedHouseholdMembership(Guid.NewGuid(), householdId));

        accessor.HouseholdId.ShouldBe(householdId);
        accessor.HouseholdId.ShouldBe(householdId);
    }

    [Fact]
    public void An_anonymous_principal_with_nothing_cached_has_no_household_and_does_not_throw()
    {
        var (accessor, _, _) = AccessorOnRequest(new ClaimsPrincipal(new ClaimsIdentity()));

        accessor.HouseholdId.ShouldBeNull();
        accessor.HouseholdMemberId.ShouldBeNull();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_principal_missing_the_subject_or_the_validated_issuer_has_no_household_and_does_not_throw(bool hasSubject, bool hasIssuer)
    {
        var claims = new List<Claim>();
        if (hasSubject)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, Subject, ClaimValueTypes.String, Issuer));
        }

        if (hasIssuer)
        {
            claims.Add(new Claim(HouseholdClaimTypes.ValidatedIssuer, Issuer));
        }

        var (accessor, _, _) = AccessorOnRequest(PrincipalWith(claims.ToArray()));

        accessor.HouseholdId.ShouldBeNull();
        accessor.HouseholdMemberId.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_principal_with_both_claims_and_nothing_cached_throws_naming_the_middleware_and_no_claim_value(bool readMemberId)
    {
        var (accessor, _, _) = AccessorOnRequest(MemberPrincipal());

        var thrown = Should.Throw<InvalidOperationException>(() => _ = readMemberId ? accessor.HouseholdMemberId : accessor.HouseholdId);

        thrown.Message.ShouldContain("UseHouseholdMembership");
        thrown.Message.ShouldContain("middleware did not run");
        thrown.Message.ShouldNotContain(Subject);
        thrown.Message.ShouldNotContain(Issuer);
    }

    [Fact]
    public void Constructing_the_accessor_never_reads_or_throws_because_Data_Protection_builds_it_before_the_middleware_runs()
    {
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        httpContextAccessor.HttpContext.Returns(new DefaultHttpContext { User = MemberPrincipal() });

        var construct = () => new CurrentHouseholdAccessor(httpContextAccessor, new JobHouseholdContext());

        construct.ShouldNotThrow();
        _ = httpContextAccessor.DidNotReceive().HttpContext;
    }

    [Fact]
    public void The_accessor_takes_exactly_the_HTTP_context_accessor_and_the_job_context_so_no_database_dependency_can_creep_back()
    {
        var parameters = typeof(CurrentHouseholdAccessor).GetConstructors().Single().GetParameters();

        parameters.Select(p => p.ParameterType).ShouldBe([typeof(IHttpContextAccessor), typeof(JobHouseholdContext)]);
    }
}
