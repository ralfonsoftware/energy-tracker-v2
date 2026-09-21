using System.Net;
using System.Net.Http.Json;
using EnergyTracker.Api.Endpoints;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// AC #5: the on/off toggle plus backend-configured/label must be readable and settable regardless
// of enablement — this test host has no AiPlausibility:BaseUrl configured, so BackendConfigured is
// always false and BackendLabel always null here; the toggle itself is still fully functional.
public class AiPlausibilityEndpointsTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private async Task<(HttpClient Client, Guid HouseholdId)> CreateHouseholdAsync()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var response = await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var created = await response.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        return (client, created!.Id);
    }

    [Fact]
    public async Task GET_ai_plausibility_defaults_to_disabled_and_reports_the_backend_as_unconfigured()
    {
        var (client, householdId) = await CreateHouseholdAsync();

        var response = await client.GetFromJsonAsync<AiPlausibilitySettingsResponse>(
            $"/api/households/{householdId}/ai-plausibility", TestContext.Current.CancellationToken);

        response!.Enabled.ShouldBeFalse();
        response.BackendConfigured.ShouldBeFalse();
        response.BackendLabel.ShouldBeNull();
        response.Version.ShouldBe(0);
    }

    [Fact]
    public async Task PUT_ai_plausibility_persists_the_toggle_and_increments_Version()
    {
        var (client, householdId) = await CreateHouseholdAsync();

        var putResponse = await client.PutAsJsonAsync(
            $"/api/households/{householdId}/ai-plausibility", new { enabled = true, version = 0 }, TestContext.Current.CancellationToken);
        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await putResponse.Content.ReadFromJsonAsync<AiPlausibilitySettingsResponse>(TestContext.Current.CancellationToken);
        updated!.Enabled.ShouldBeTrue();
        updated.Version.ShouldBe(1);

        var refetched = await client.GetFromJsonAsync<AiPlausibilitySettingsResponse>(
            $"/api/households/{householdId}/ai-plausibility", TestContext.Current.CancellationToken);
        refetched!.Enabled.ShouldBeTrue();
        refetched.Version.ShouldBe(1);
    }

    [Fact]
    public async Task PUT_ai_plausibility_with_a_stale_version_returns_409_on_the_second_writer()
    {
        var (client, householdId) = await CreateHouseholdAsync();

        var first = await client.PutAsJsonAsync(
            $"/api/households/{householdId}/ai-plausibility", new { enabled = true, version = 0 }, TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await client.PutAsJsonAsync(
            $"/api/households/{householdId}/ai-plausibility", new { enabled = false, version = 0 }, TestContext.Current.CancellationToken);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var current = await client.GetFromJsonAsync<AiPlausibilitySettingsResponse>(
            $"/api/households/{householdId}/ai-plausibility", TestContext.Current.CancellationToken);
        current!.Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task A_principal_cannot_read_or_edit_another_Households_AI_Plausibility_setting()
    {
        var (_, householdId) = await CreateHouseholdAsync();
        var (otherClient, _) = await CreateHouseholdAsync();

        var getResponse = await otherClient.GetAsync($"/api/households/{householdId}/ai-plausibility", TestContext.Current.CancellationToken);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var putResponse = await otherClient.PutAsJsonAsync(
            $"/api/households/{householdId}/ai-plausibility", new { enabled = true, version = 0 }, TestContext.Current.CancellationToken);
        putResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
