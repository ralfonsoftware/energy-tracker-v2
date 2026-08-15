using System.Net;
using System.Net.Http.Json;
using EnergyTracker.Api.Endpoints;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class YearlyBaselineEndpointsTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private async Task<(HttpClient Client, Guid HouseholdId)> CreateHouseholdAsync()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var response = await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var created = await response.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        return (client, created!.Id);
    }

    [Fact]
    public async Task GET_households_id_reports_no_Yearly_Baseline_by_default()
    {
        var (client, householdId) = await CreateHouseholdAsync();

        var response = await client.GetFromJsonAsync<HouseholdResponse>($"/api/households/{householdId}", TestContext.Current.CancellationToken);

        response!.YearlyBaselineKwh.ShouldBeNull();
        response.Version.ShouldBe(0);
    }

    [Fact]
    public async Task PUT_yearly_baseline_persists_the_value_and_increments_Version()
    {
        var (client, householdId) = await CreateHouseholdAsync();

        var putResponse = await client.PutAsJsonAsync(
            $"/api/households/{householdId}/yearly-baseline",
            new { yearlyBaselineKwh = 3500m, version = 0 },
            TestContext.Current.CancellationToken);
        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await putResponse.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        updated!.YearlyBaselineKwh.ShouldBe(3500m);
        updated.Version.ShouldBe(1);

        var refetched = await client.GetFromJsonAsync<HouseholdResponse>($"/api/households/{householdId}", TestContext.Current.CancellationToken);
        refetched!.YearlyBaselineKwh.ShouldBe(3500m);
        refetched.Version.ShouldBe(1);
    }

    [Fact]
    public async Task PUT_yearly_baseline_with_a_non_positive_value_is_rejected()
    {
        var (client, householdId) = await CreateHouseholdAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/households/{householdId}/yearly-baseline",
            new { yearlyBaselineKwh = 0m, version = 0 },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PUT_yearly_baseline_with_a_stale_version_returns_409_on_the_second_writer()
    {
        // Two sequential PUTs against the same stale version, deterministically simulating a
        // two-writer conflict — the second submit must not silently overwrite the first
        // (AC #4, NFR10).
        var (client, householdId) = await CreateHouseholdAsync();

        var first = await client.PutAsJsonAsync(
            $"/api/households/{householdId}/yearly-baseline",
            new { yearlyBaselineKwh = 3500m, version = 0 },
            TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await client.PutAsJsonAsync(
            $"/api/households/{householdId}/yearly-baseline",
            new { yearlyBaselineKwh = 4250m, version = 0 },
            TestContext.Current.CancellationToken);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var current = await client.GetFromJsonAsync<HouseholdResponse>($"/api/households/{householdId}", TestContext.Current.CancellationToken);
        current!.YearlyBaselineKwh.ShouldBe(3500m);
    }

    [Fact]
    public async Task A_principal_cannot_read_or_edit_another_Households_Yearly_Baseline()
    {
        var (_, householdId) = await CreateHouseholdAsync();
        var (otherClient, _) = await CreateHouseholdAsync();

        var getResponse = await otherClient.GetAsync($"/api/households/{householdId}", TestContext.Current.CancellationToken);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var putResponse = await otherClient.PutAsJsonAsync(
            $"/api/households/{householdId}/yearly-baseline",
            new { yearlyBaselineKwh = 3500m, version = 0 },
            TestContext.Current.CancellationToken);
        putResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
