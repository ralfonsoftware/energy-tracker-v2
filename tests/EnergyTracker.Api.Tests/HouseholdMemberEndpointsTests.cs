using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.9 (AD-26, AD-17, AD-3; audit A2/S10): member removal, leaving, open invites, revocation and
// hashed invite tokens on a real host. The removed-member sweep enforces the "every request from a
// stale tab fails" rule (Q3, AC #13c) for every /api route, so a future route cannot quietly work for a
// removed member. SQL Server behaviour is covered at repository level.
public class HouseholdMemberEndpointsTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Principal(string Subject, HttpClient Client);

    private Principal NewPrincipal(string? name = null)
    {
        var subject = Guid.NewGuid().ToString();
        return new Principal(subject, factory.CreateAuthenticatedClient(subject, name: name));
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

    private static async Task JoinAsync(Principal joiner, string token)
    {
        var response = await joiner.Client.PostAsync($"/api/household-invites/{token}/accept", null, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<List<HouseholdMemberResponse>> ListMembersAsync(Principal principal)
    {
        var body = await principal.Client.GetFromJsonAsync<HouseholdMembersResponse>("/api/household-members", Json, TestContext.Current.CancellationToken);
        return body!.Members.ToList();
    }

    private static async Task<List<OpenHouseholdInviteResponse>> ListInvitesAsync(Principal principal)
    {
        var body = await principal.Client.GetFromJsonAsync<OpenHouseholdInvitesResponse>("/api/household-invites", Json, TestContext.Current.CancellationToken);
        return body!.Invites.ToList();
    }

    // Two members: A (creator) and B (joined through an invite). Returns B's member id as A sees it.
    private async Task<(Principal A, Principal B, Guid HouseholdId, Guid AId, Guid BId)> TwoMemberHouseholdAsync()
    {
        var a = NewPrincipal("Alex");
        var b = NewPrincipal("Bea");
        var household = await CreateHouseholdAsync(a);
        var invite = await CreateInviteAsync(a);
        await JoinAsync(b, invite.Token);
        var members = await ListMembersAsync(a);
        return (a, b, household.Id, members.Single(m => m.IsCurrentMember).Id, members.Single(m => !m.IsCurrentMember).Id);
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private async Task<HouseholdInvite> StoredInviteAsync(Guid inviteId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        return await dbContext.HouseholdInvites.AsNoTracking().SingleAsync(i => i.Id == inviteId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Listing_shows_both_members_marks_the_caller_and_never_exposes_issuer_or_subject()
    {
        var (a, b, _, aId, bId) = await TwoMemberHouseholdAsync();

        var response = await a.Client.GetAsync("/api/household-members", TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var members = await ListMembersAsync(a);
        var asSeenByB = await ListMembersAsync(b);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        members.Select(m => m.Id).ShouldBe([aId, bId], ignoreOrder: true);
        members.Single(m => m.Id == aId).IsCurrentMember.ShouldBeTrue();
        members.Single(m => m.Id == bId).IsCurrentMember.ShouldBeFalse();
        members.Single(m => m.Id == aId).DisplayName.ShouldBe("Alex");
        asSeenByB.Single(m => m.IsCurrentMember).Id.ShouldBe(bId);
        members.Select(m => m.JoinedAtUtc).ShouldBe(members.Select(m => m.JoinedAtUtc).OrderBy(t => t));
        raw.ShouldNotContain(a.Subject);
        raw.ShouldNotContain(b.Subject);
        raw.ToLowerInvariant().ShouldNotContain("issuer");
        raw.ToLowerInvariant().ShouldNotContain("subject");
    }

    [Fact]
    public async Task Removing_another_member_204_sends_them_back_to_household_creation()
    {
        var (a, b, householdId, _, bId) = await TwoMemberHouseholdAsync();

        var response = await a.Client.DeleteAsync($"/api/household-members/{bId}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ListMembersAsync(a)).Count.ShouldBe(1);
        var sessionB = await b.Client.GetFromJsonAsync<SessionResponse>("/api/session", TestContext.Current.CancellationToken);
        sessionB!.HasHousehold.ShouldBeFalse();
        (await b.Client.GetAsync("/api/status", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var created = await b.Client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await created.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken))!.Id.ShouldNotBe(householdId);
    }

    [Fact]
    public async Task Leaving_204_behaves_the_same_for_the_caller_and_leaves_the_Household_to_the_others()
    {
        var (a, b, householdId, _, bId) = await TwoMemberHouseholdAsync();

        var response = await b.Client.DeleteAsync($"/api/household-members/{bId}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await b.Client.GetFromJsonAsync<SessionResponse>("/api/session", TestContext.Current.CancellationToken))!.HasHousehold.ShouldBeFalse();
        var sessionA = await a.Client.GetFromJsonAsync<SessionResponse>("/api/session", TestContext.Current.CancellationToken);
        sessionA!.HasHousehold.ShouldBeTrue();
        sessionA.HouseholdId.ShouldBe(householdId);
        (await ListMembersAsync(a)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Removing_a_member_of_another_Household_is_404_member_not_found_and_removes_nothing()
    {
        var (a, _, _, _, _) = await TwoMemberHouseholdAsync();
        var (_, other, _, _, otherBId) = await TwoMemberHouseholdAsync();

        var response = await a.Client.DeleteAsync($"/api/household-members/{otherBId}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ProblemAsync(response)).GetProperty("errorCode").GetString().ShouldBe("member-not-found");
        (await ListMembersAsync(other)).Count.ShouldBe(2);
        (await a.Client.DeleteAsync($"/api/household-members/{Guid.NewGuid()}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_sole_member_removing_themselves_is_409_last_member()
    {
        var a = NewPrincipal();
        await CreateHouseholdAsync(a);
        var self = (await ListMembersAsync(a)).Single();

        var response = await a.Client.DeleteAsync($"/api/household-members/{self.Id}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemAsync(response)).GetProperty("errorCode").GetString().ShouldBe("last-member");
        (await ListMembersAsync(a)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_lost_concurrency_race_maps_to_409_members_changed()
    {
        var (a, _, _, _, bId) = await TwoMemberHouseholdAsync();
        var repository = Substitute.For<IHouseholdRepository>();
        repository.RemoveMemberAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HouseholdConcurrencyConflictException(Guid.NewGuid())));
        using var racing = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddScoped(_ => repository)));
        var client = racing.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "fetch");
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, a.Subject);
        client.DefaultRequestHeaders.Add(TestAuthHandler.IssuerHeader, TestAuthHandler.DefaultIssuer);

        var response = await client.DeleteAsync($"/api/household-members/{bId}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemAsync(response)).GetProperty("errorCode").GetString().ShouldBe("members-changed");
    }

    [Fact]
    public async Task A_principal_without_a_Household_gets_403_on_every_members_and_invites_route()
    {
        var nobody = NewPrincipal();
        var someId = Guid.NewGuid();

        (await nobody.Client.GetAsync("/api/household-members", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await nobody.Client.DeleteAsync($"/api/household-members/{someId}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await nobody.Client.GetAsync("/api/household-invites", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await nobody.Client.DeleteAsync($"/api/household-invites/{someId}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_DELETE_without_the_CSRF_header_is_400()
    {
        var (_, _, _, _, bId) = await TwoMemberHouseholdAsync();
        var headerless = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString(), sendRequestedWith: false);

        (await headerless.DeleteAsync($"/api/household-members/{bId}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await headerless.DeleteAsync($"/api/household-invites/{Guid.NewGuid()}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_non_guid_member_id_is_not_a_success_and_not_a_server_error()
    {
        var (a, _, _, _, _) = await TwoMemberHouseholdAsync();

        var response = await a.Client.DeleteAsync("/api/household-members/not-a-guid", TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBeInRange(400, 499);
    }

    [Fact]
    public async Task Removal_expires_the_Households_open_invites_and_leaves_another_Households_invite_open()
    {
        var (a, _, _, _, bId) = await TwoMemberHouseholdAsync();
        var open = await CreateInviteAsync(a);
        var otherOwner = NewPrincipal();
        await CreateHouseholdAsync(otherOwner);
        var otherInvite = await CreateInviteAsync(otherOwner);
        var probe = NewPrincipal();

        (await probe.Client.GetAsync($"/api/household-invites/{open.Token}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await a.Client.DeleteAsync($"/api/household-members/{bId}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await probe.Client.GetAsync($"/api/household-invites/{open.Token}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await probe.Client.PostAsync($"/api/household-invites/{open.Token}/accept", null, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await probe.Client.GetAsync($"/api/household-invites/{otherInvite.Token}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ListInvitesAsync(a)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Revoking_an_invite_204_kills_the_link_and_drops_it_from_the_open_list()
    {
        var a = NewPrincipal();
        await CreateHouseholdAsync(a);
        var invite = await CreateInviteAsync(a);
        var listed = (await ListInvitesAsync(a)).Single();
        var probe = NewPrincipal();

        var revoke = await a.Client.DeleteAsync($"/api/household-invites/{listed.Id}", TestContext.Current.CancellationToken);

        revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await probe.Client.GetAsync($"/api/household-invites/{invite.Token}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await probe.Client.PostAsync($"/api/household-invites/{invite.Token}/accept", null, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ListInvitesAsync(a)).ShouldBeEmpty();
        var again = await a.Client.DeleteAsync($"/api/household-invites/{listed.Id}", TestContext.Current.CancellationToken);
        again.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ProblemAsync(again)).GetProperty("errorCode").GetString().ShouldBe("invite-not-found");
    }

    [Fact]
    public async Task Revoking_another_Households_invite_is_404_and_the_invite_still_works()
    {
        var a = NewPrincipal();
        await CreateHouseholdAsync(a);
        var other = NewPrincipal();
        await CreateHouseholdAsync(other);
        var otherInvite = await CreateInviteAsync(other);
        var otherInviteId = (await ListInvitesAsync(other)).Single().Id;
        var probe = NewPrincipal();

        var response = await a.Client.DeleteAsync($"/api/household-invites/{otherInviteId}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ProblemAsync(response)).GetProperty("errorCode").GetString().ShouldBe("invite-not-found");
        (await probe.Client.GetAsync($"/api/household-invites/{otherInvite.Token}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ListInvitesAsync(a)).ShouldBeEmpty();
        (await ListInvitesAsync(other)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_open_list_hides_consumed_invites_lists_newest_first_and_never_carries_a_token_or_hash()
    {
        var a = NewPrincipal();
        await CreateHouseholdAsync(a);
        var consumed = await CreateInviteAsync(a);
        await JoinAsync(NewPrincipal(), consumed.Token);
        await CreateInviteAsync(a);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        await CreateInviteAsync(a);

        var response = await a.Client.GetAsync("/api/household-invites", TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var invites = await ListInvitesAsync(a);

        invites.Count.ShouldBe(2);
        invites.Select(i => i.CreatedAtUtc).ShouldBe(invites.Select(i => i.CreatedAtUtc).OrderByDescending(t => t));
        raw.ToLowerInvariant().ShouldNotContain("token");
        raw.ToLowerInvariant().ShouldNotContain("hash");
        raw.ShouldNotContain(consumed.Token);
    }

    [Fact]
    public async Task A_new_invite_stores_only_the_hash_of_a_43_character_base64url_token()
    {
        var a = NewPrincipal();
        await CreateHouseholdAsync(a);

        var invite = await CreateInviteAsync(a);

        invite.Token.Length.ShouldBe(43);
        invite.Token.ShouldMatch("^[A-Za-z0-9_-]{43}$");
        var stored = await StoredInviteAsync((await ListInvitesAsync(a)).Single().Id);
        stored.TokenHash.ShouldBe(InviteToken.Hash(invite.Token));
    }

    [Fact]
    public async Task An_over_long_token_answers_404_on_preview_and_accept_without_echoing_it()
    {
        var probe = NewPrincipal();
        var tooLong = new string('a', InviteToken.MaxLength + 1);

        var preview = await probe.Client.GetAsync($"/api/household-invites/{tooLong}", TestContext.Current.CancellationToken);
        var accept = await probe.Client.PostAsync($"/api/household-invites/{tooLong}/accept", null, TestContext.Current.CancellationToken);

        preview.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        accept.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await accept.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain(tooLong);
    }

    [Fact]
    public async Task The_invite_error_bodies_never_echo_the_presented_token()
    {
        var a = NewPrincipal();
        await CreateHouseholdAsync(a);
        var invite = await CreateInviteAsync(a);
        var inviteId = (await ListInvitesAsync(a)).Single().Id;
        await a.Client.DeleteAsync($"/api/household-invites/{inviteId}", TestContext.Current.CancellationToken);
        var probe = NewPrincipal();
        const string unknown = "this-token-does-not-exist";

        var expired = await probe.Client.PostAsync($"/api/household-invites/{invite.Token}/accept", null, TestContext.Current.CancellationToken);
        var notFound = await probe.Client.PostAsync($"/api/household-invites/{unknown}/accept", null, TestContext.Current.CancellationToken);

        expired.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        notFound.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await expired.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain(invite.Token);
        (await notFound.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain(unknown);
    }

    // Q3 / AC #13c: a removed member's stale tab keeps its `ready` state, so every API request from it must
    // fail. Every /api route is called as the removed principal; only the routes a principal without a
    // Household legitimately uses may succeed.
    [Fact]
    public async Task No_api_route_succeeds_for_a_removed_member_except_the_ones_a_principal_without_a_Household_uses()
    {
        var (a, b, _, _, bId) = await TwoMemberHouseholdAsync();
        (await a.Client.DeleteAsync($"/api/household-members/{bId}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var allowed = new HashSet<string>
        {
            "GET /api/session",
            "POST /api/households",
            "GET /api/household-invites/{token}",
            "POST /api/household-invites/{token}/accept",
        };
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? string.Empty).StartsWith("/api", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => (Method: method, Route: e.RoutePattern.RawText!)))
            .Where(r => !allowed.Contains($"{r.Method} {r.Route}"))
            .DistinctBy(r => $"{r.Method} {r.Route}")
            .ToList();

        // Non-vacuous: enough routes, and a known read and write from each endpoint family.
        routes.Count.ShouldBeGreaterThan(40);
        string[] mustVisit =
        [
            "GET /api/rooms", "POST /api/rooms", "GET /api/devices", "POST /api/devices", "GET /api/power-points", "POST /api/power-points",
            "GET /api/meter-readings", "POST /api/meter-readings", "GET /api/tariffs", "POST /api/tariffs", "GET /api/status",
            "GET /api/events", "POST /api/events", "GET /api/smart-plug-readings", "POST /api/smart-plug-imports",
            "GET /api/smart-plug-import-jobs", "GET /api/household-export", "POST /api/household-import",
            "GET /api/household-members", "DELETE /api/household-members/{id:guid}", "GET /api/household-invites", "POST /api/household-invites",
            "PUT /api/households/{id}/yearly-baseline", "GET /api/meter-regression-prompts/open",
        ];
        foreach (var key in mustVisit)
        {
            routes.ShouldContain(r => $"{r.Method} {r.Route}" == key, $"the sweep must visit {key}");
        }

        var succeeded = new List<string>();
        foreach (var (method, route) in routes)
        {
            var url = System.Text.RegularExpressions.Regex.Replace(route, @"\{[^}]+\}", m => m.Value.Contains(":guid") ? Guid.NewGuid().ToString() : "x");
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method is "POST" or "PUT" or "PATCH")
            {
                request.Content = JsonContent.Create(new { });
            }

            using var response = await b.Client.SendAsync(request, TestContext.Current.CancellationToken);
            if ((int)response.StatusCode is >= 200 and < 300)
            {
                succeeded.Add($"{method} {route} -> {(int)response.StatusCode}");
            }
        }

        succeeded.ShouldBeEmpty("These routes still work for a removed member (story 11.9, Q3).");
    }
}
