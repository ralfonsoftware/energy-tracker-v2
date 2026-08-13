using System.Net;
using System.Net.Http.Json;
using EnergyTracker.Api.Endpoints;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class SessionAndHouseholdCreationTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    [Fact]
    public async Task GET_api_session_for_a_principal_with_no_HouseholdMember_row_reports_no_Household()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var session = await client.GetFromJsonAsync<SessionResponse>("/api/session", TestContext.Current.CancellationToken);

        session!.HasHousehold.ShouldBeFalse();
        session.HouseholdId.ShouldBeNull();
    }

    [Theory]
    [InlineData("de-DE", "EUR")]
    [InlineData("en-US", "USD")]
    public async Task Creating_a_Household_requires_only_the_one_POST_call_and_the_session_then_reflects_it(string locale, string currency)
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var createResponse = await client.PostAsJsonAsync("/api/households", new { locale, currency }, TestContext.Current.CancellationToken);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var created = await createResponse.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        // The stored value always matches what was explicitly submitted, never a server default.
        created!.Locale.ShouldBe(locale);
        created.Currency.ShouldBe(currency);

        var session = await client.GetFromJsonAsync<SessionResponse>("/api/session", TestContext.Current.CancellationToken);
        session!.HasHousehold.ShouldBeTrue();
        session.HouseholdId.ShouldBe(created.Id);
        session.Locale.ShouldBe(locale);
        session.Currency.ShouldBe(currency);
    }

    [Theory]
    [InlineData("", "EUR")]
    [InlineData("fr-FR", "EUR")]
    [InlineData("de-DE", "")]
    [InlineData("de-DE", "EURO")]
    public async Task Creating_a_Household_without_a_valid_explicit_Locale_or_Currency_is_rejected(string locale, string currency)
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/households", new { locale, currency }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_principal_that_already_has_a_Household_cannot_create_a_second_one()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var first = await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await client.PostAsJsonAsync("/api/households", new { locale = "en-US", currency = "USD" }, TestContext.Current.CancellationToken);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Two_different_principals_can_each_provision_their_own_Household()
    {
        // Per-principal resolution, not system-wide (Glossary: a deployment may hold more than
        // one Household) — a second, unrelated authenticated principal must not be blocked just
        // because a Household already exists elsewhere in the deployment.
        var firstClient = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var secondClient = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var firstResponse = await firstClient.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var secondResponse = await secondClient.PostAsJsonAsync("/api/households", new { locale = "en-US", currency = "USD" }, TestContext.Current.CancellationToken);

        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var firstCreated = await firstResponse.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        var secondCreated = await secondResponse.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        firstCreated!.Id.ShouldNotBe(secondCreated!.Id);
    }
}
