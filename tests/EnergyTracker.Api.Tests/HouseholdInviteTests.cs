using System.Net;
using System.Net.Http.Json;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class HouseholdInviteTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    [Fact]
    public async Task A_member_can_invite_a_second_principal_who_then_shares_the_same_Household()
    {
        // AC #1: A creates a Household, sends an invite, B (a distinct principal with no
        // Household yet) previews then accepts it and ends up sharing A's Household.
        var clientA = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var householdA = await clientA.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var createdA = await householdA.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);

        var inviteResponse = await clientA.PostAsync("/api/household-invites", null, TestContext.Current.CancellationToken);
        inviteResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var invite = await inviteResponse.Content.ReadFromJsonAsync<HouseholdInviteResponse>(TestContext.Current.CancellationToken);

        var clientB = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var previewResponse = await clientB.GetAsync($"/api/household-invites/{invite!.Token}", TestContext.Current.CancellationToken);
        previewResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var acceptResponse = await clientB.PostAsync($"/api/household-invites/{invite.Token}/accept", null, TestContext.Current.CancellationToken);
        acceptResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var accepted = await acceptResponse.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        accepted!.Id.ShouldBe(createdA!.Id);
        accepted.Locale.ShouldBe(createdA.Locale);
        accepted.Currency.ShouldBe(createdA.Currency);

        var sessionB = await clientB.GetFromJsonAsync<SessionResponse>("/api/session", TestContext.Current.CancellationToken);
        sessionB!.HasHousehold.ShouldBeTrue();
        sessionB.HouseholdId.ShouldBe(createdA.Id);
    }

    [Fact]
    public async Task Accepting_an_unknown_token_returns_404()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var previewResponse = await client.GetAsync("/api/household-invites/does-not-exist", TestContext.Current.CancellationToken);
        previewResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var acceptResponse = await client.PostAsync("/api/household-invites/does-not-exist/accept", null, TestContext.Current.CancellationToken);
        acceptResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Accepting_an_expired_invite_returns_409()
    {
        var clientA = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var householdResponse = await clientA.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var household = await householdResponse.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);

        // Construct an already-expired invite directly against the DbContext — no constructor
        // parameter is exposed to shorten CreateHouseholdInvite.InviteLifetime for a test.
        var token = Guid.NewGuid().ToString("N");
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
            dbContext.HouseholdInvites.Add(new HouseholdInvite
            {
                Id = Guid.NewGuid(),
                HouseholdId = household!.Id,
                Token = token,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-8),
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var clientB = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var previewResponse = await clientB.GetAsync($"/api/household-invites/{token}", TestContext.Current.CancellationToken);
        previewResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var acceptResponse = await clientB.PostAsync($"/api/household-invites/{token}/accept", null, TestContext.Current.CancellationToken);
        acceptResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Accepting_the_same_token_twice_returns_409_on_the_second_call()
    {
        var clientA = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        await clientA.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var inviteResponse = await clientA.PostAsync("/api/household-invites", null, TestContext.Current.CancellationToken);
        var invite = await inviteResponse.Content.ReadFromJsonAsync<HouseholdInviteResponse>(TestContext.Current.CancellationToken);

        var clientB = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var firstAccept = await clientB.PostAsync($"/api/household-invites/{invite!.Token}/accept", null, TestContext.Current.CancellationToken);
        firstAccept.StatusCode.ShouldBe(HttpStatusCode.OK);

        var clientC = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var secondAccept = await clientC.PostAsync($"/api/household-invites/{invite.Token}/accept", null, TestContext.Current.CancellationToken);
        secondAccept.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Accepting_an_invite_increments_its_AD4_concurrency_token()
    {
        // AD-4 requires the concurrency token to change on every update — if it never moves,
        // two genuinely concurrent accepts of the same invite could both pass EF's
        // WHERE Version = @original check and both succeed, silently breaking single-use.
        var clientA = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        await clientA.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var inviteResponse = await clientA.PostAsync("/api/household-invites", null, TestContext.Current.CancellationToken);
        var invite = await inviteResponse.Content.ReadFromJsonAsync<HouseholdInviteResponse>(TestContext.Current.CancellationToken);

        var clientB = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var acceptResponse = await clientB.PostAsync($"/api/household-invites/{invite!.Token}/accept", null, TestContext.Current.CancellationToken);
        acceptResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        var persisted = await dbContext.HouseholdInvites.SingleAsync(i => i.Token == invite.Token, TestContext.Current.CancellationToken);
        persisted.Version.ShouldNotBe(0);
    }

    [Fact]
    public async Task A_principal_that_already_has_a_Household_cannot_accept_another_invite()
    {
        var clientA = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        await clientA.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var inviteResponse = await clientA.PostAsync("/api/household-invites", null, TestContext.Current.CancellationToken);
        var invite = await inviteResponse.Content.ReadFromJsonAsync<HouseholdInviteResponse>(TestContext.Current.CancellationToken);

        var clientB = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        await clientB.PostAsJsonAsync("/api/households", new { locale = "en-US", currency = "USD" }, TestContext.Current.CancellationToken);

        var acceptResponse = await clientB.PostAsync($"/api/household-invites/{invite!.Token}/accept", null, TestContext.Current.CancellationToken);
        acceptResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_principal_with_no_Household_gets_403_creating_an_invite()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.PostAsync("/api/household-invites", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AC2_A_newly_joined_member_has_the_same_full_access_as_the_creator_including_inviting_others()
    {
        var clientA = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var householdA = await clientA.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var createdA = await householdA.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);

        var firstInviteResponse = await clientA.PostAsync("/api/household-invites", null, TestContext.Current.CancellationToken);
        var firstInvite = await firstInviteResponse.Content.ReadFromJsonAsync<HouseholdInviteResponse>(TestContext.Current.CancellationToken);

        var clientB = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        await clientB.PostAsync($"/api/household-invites/{firstInvite!.Token}/accept", null, TestContext.Current.CancellationToken);

        // B — who joined via A's invite, not the original creator — can invite a third
        // principal identically. No creator-only/admin gate exists on invite creation (AC #2).
        var secondInviteResponse = await clientB.PostAsync("/api/household-invites", null, TestContext.Current.CancellationToken);
        secondInviteResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var secondInvite = await secondInviteResponse.Content.ReadFromJsonAsync<HouseholdInviteResponse>(TestContext.Current.CancellationToken);

        var clientC = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var thirdAccept = await clientC.PostAsync($"/api/household-invites/{secondInvite!.Token}/accept", null, TestContext.Current.CancellationToken);
        thirdAccept.StatusCode.ShouldBe(HttpStatusCode.OK);
        var joinedByC = await thirdAccept.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        joinedByC!.Id.ShouldBe(createdA!.Id);

        // B's own session reflects full, immediate access — no reduced/pending-member state.
        var sessionB = await clientB.GetFromJsonAsync<SessionResponse>("/api/session", TestContext.Current.CancellationToken);
        sessionB!.HasHousehold.ShouldBeTrue();
        sessionB.HouseholdId.ShouldBe(createdA.Id);
        sessionB.Locale.ShouldBe(createdA.Locale);
        sessionB.Currency.ShouldBe(createdA.Currency);
    }

    [Fact]
    public async Task AC3_An_unrelated_principal_never_sees_another_Households_data_once_it_has_more_than_one_member()
    {
        var clientA = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        await clientA.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var inviteResponse = await clientA.PostAsync("/api/household-invites", null, TestContext.Current.CancellationToken);
        var invite = await inviteResponse.Content.ReadFromJsonAsync<HouseholdInviteResponse>(TestContext.Current.CancellationToken);

        var clientB = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        await clientB.PostAsync($"/api/household-invites/{invite!.Token}/accept", null, TestContext.Current.CancellationToken);

        // D — never invited, no Household of its own — must not be confused with A/B's Household.
        var clientD = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var sessionD = await clientD.GetFromJsonAsync<SessionResponse>("/api/session", TestContext.Current.CancellationToken);

        sessionD!.HasHousehold.ShouldBeFalse();
        sessionD.HouseholdId.ShouldBeNull();
    }
}
