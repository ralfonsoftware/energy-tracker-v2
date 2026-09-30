using System.Net;
using System.Net.Http.Json;
using EnergyTracker.Api.Endpoints;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class HouseholdLocaleEndpointsTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private async Task<(HttpClient Client, Guid HouseholdId)> CreateHouseholdAsync()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var response = await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var created = await response.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        return (client, created!.Id);
    }

    [Fact]
    public async Task PUT_locale_persists_the_locale_without_bumping_Version_and_session_reports_it()
    {
        var (client, householdId) = await CreateHouseholdAsync();

        var putResponse = await client.PutAsJsonAsync($"/api/households/{householdId}/locale", new { locale = "en-US" }, TestContext.Current.CancellationToken);

        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await putResponse.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        updated!.Locale.ShouldBe("en-US");
        updated.Version.ShouldBe(0);

        var refetched = await client.GetFromJsonAsync<HouseholdResponse>($"/api/households/{householdId}", TestContext.Current.CancellationToken);
        refetched!.Locale.ShouldBe("en-US");

        var session = await client.GetFromJsonAsync<SessionResponse>("/api/session", TestContext.Current.CancellationToken);
        session!.Locale.ShouldBe("en-US");
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("de-de")]
    [InlineData("")]
    public async Task PUT_locale_with_an_unsupported_value_is_rejected_and_the_stored_locale_is_unchanged(string locale)
    {
        var (client, householdId) = await CreateHouseholdAsync();

        var response = await client.PutAsJsonAsync($"/api/households/{householdId}/locale", new { locale }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var current = await client.GetFromJsonAsync<HouseholdResponse>($"/api/households/{householdId}", TestContext.Current.CancellationToken);
        current!.Locale.ShouldBe("de-DE");
    }

    [Fact]
    public async Task PUT_locale_with_a_null_locale_is_rejected()
    {
        var (client, householdId) = await CreateHouseholdAsync();

        var response = await client.PutAsJsonAsync($"/api/households/{householdId}/locale", new { locale = (string?)null }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_principal_cannot_change_another_Households_locale()
    {
        var (_, householdId) = await CreateHouseholdAsync();
        var (otherClient, _) = await CreateHouseholdAsync();

        var response = await otherClient.PutAsJsonAsync($"/api/households/{householdId}/locale", new { locale = "en-US" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PUT_locale_without_authentication_returns_401()
    {
        var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/api/households/{Guid.NewGuid()}/locale", new { locale = "en-US" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_second_member_who_joined_by_invite_can_change_the_locale_and_the_first_member_sees_it()
    {
        var (clientA, householdId) = await CreateHouseholdAsync();
        var inviteResponse = await clientA.PostAsync("/api/household-invites", null, TestContext.Current.CancellationToken);
        var invite = await inviteResponse.Content.ReadFromJsonAsync<HouseholdInviteResponse>(TestContext.Current.CancellationToken);
        var clientB = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        (await clientB.PostAsync($"/api/household-invites/{invite!.Token}/accept", null, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var putResponse = await clientB.PutAsJsonAsync($"/api/households/{householdId}/locale", new { locale = "en-US" }, TestContext.Current.CancellationToken);

        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sessionA = await clientA.GetFromJsonAsync<SessionResponse>("/api/session", TestContext.Current.CancellationToken);
        sessionA!.Locale.ShouldBe("en-US");
    }

    [Fact]
    public async Task Changing_the_locale_leaves_the_Yearly_Baseline_and_Meter_Readings_unchanged()
    {
        var (client, householdId) = await CreateHouseholdAsync();
        (await client.PutAsJsonAsync($"/api/households/{householdId}/yearly-baseline", new { yearlyBaselineKwh = 3500m, version = 0 }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var timestamp = new DateTimeOffset(2026, 8, 1, 9, 15, 0, TimeSpan.FromHours(2));
        var created = await client.PostAsJsonAsync(
            "/api/meter-readings",
            new { kwhValue = 4821.5m, readingTimestamp = timestamp, idempotencyKey = Guid.NewGuid() },
            TestContext.Current.CancellationToken);
        var before = await created.Content.ReadFromJsonAsync<MeterReadingResponse>(TestContext.Current.CancellationToken);

        (await client.PutAsJsonAsync($"/api/households/{householdId}/locale", new { locale = "en-US" }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var household = await client.GetFromJsonAsync<HouseholdResponse>($"/api/households/{householdId}", TestContext.Current.CancellationToken);
        household!.YearlyBaselineKwh.ShouldBe(3500m);
        household.Version.ShouldBe(1);
        var history = await client.GetFromJsonAsync<MeterReadingHistoryPageResponse>("/api/meter-readings", TestContext.Current.CancellationToken);
        var reading = history!.Items.Single(i => i.Id == before!.Id);
        reading.KwhValue.ShouldBe(4821.5m);
        reading.ReadingTimestamp.ShouldBe(timestamp);
    }
}
