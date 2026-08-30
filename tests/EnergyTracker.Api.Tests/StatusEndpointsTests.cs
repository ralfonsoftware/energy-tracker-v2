using System.Net;
using System.Net.Http.Json;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class StatusEndpointsTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private async Task<(HttpClient Client, Guid HouseholdId, int Version)> CreateHouseholdAsync()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var response = await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var created = await response.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        return (client, created!.Id, created.Version);
    }

    // AD-3's query filter needs an HttpContext-bound CurrentHouseholdAccessor, absent in this raw
    // scope — IgnoreQueryFilters is required to count/read directly against the table.
    private async Task<int> CountStatusSnapshotRowsAsync(Guid householdId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        return await dbContext.StatusSnapshots.IgnoreQueryFilters()
            .CountAsync(s => s.HouseholdId == householdId, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> PostReadingAsync(HttpClient client, decimal kwhValue, DateTimeOffset readingTimestamp) =>
        client.PostAsJsonAsync(
            "/api/meter-readings",
            new { kwhValue, readingTimestamp, idempotencyKey = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> SetYearlyBaselineAsync(HttpClient client, Guid householdId, decimal yearlyBaselineKwh, int version) =>
        client.PutAsJsonAsync(
            $"/api/households/{householdId}/yearly-baseline",
            new { yearlyBaselineKwh, version },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task GET_status_returns_a_null_body_when_no_Yearly_Baseline_is_set()
    {
        var (client, _, _) = await CreateHouseholdAsync();
        await PostReadingAsync(client, 1000m, DateTimeOffset.UtcNow.AddDays(-1));
        await PostReadingAsync(client, 1100m, DateTimeOffset.UtcNow);

        var response = await client.GetAsync("/api/status", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task GET_status_returns_a_null_body_when_fewer_than_two_readings_exist()
    {
        var (client, householdId, version) = await CreateHouseholdAsync();
        await SetYearlyBaselineAsync(client, householdId, 3650m, version);
        await PostReadingAsync(client, 1000m, DateTimeOffset.UtcNow);

        var response = await client.GetAsync("/api/status", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task GET_status_returns_a_computed_Status_once_a_baseline_and_two_readings_exist()
    {
        var (client, householdId, version) = await CreateHouseholdAsync();
        await SetYearlyBaselineAsync(client, householdId, 3650m, version);
        var latest = DateTimeOffset.UtcNow;
        var baseline = latest.AddDays(-182.5);
        await PostReadingAsync(client, 1000m, baseline);
        await PostReadingAsync(client, 2825m, latest);

        var response = await client.GetAsync("/api/status", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<StatusResponse>(TestContext.Current.CancellationToken);
        body.ShouldNotBeNull();
        body.Status.ShouldBe("withinRange");
        body.PaceToDateKwh.ShouldBe(1825m);
    }

    [Fact]
    public async Task A_households_Status_is_never_affected_by_another_households_readings_or_baseline()
    {
        var (ownerClient, ownerHouseholdId, ownerVersion) = await CreateHouseholdAsync();
        await SetYearlyBaselineAsync(ownerClient, ownerHouseholdId, 3650m, ownerVersion);
        var latest = DateTimeOffset.UtcNow;
        var baseline = latest.AddDays(-182.5);
        await PostReadingAsync(ownerClient, 1000m, baseline);
        await PostReadingAsync(ownerClient, 2825m, latest);

        var (otherClient, otherHouseholdId, otherVersion) = await CreateHouseholdAsync();
        await SetYearlyBaselineAsync(otherClient, otherHouseholdId, 100m, otherVersion);
        await PostReadingAsync(otherClient, 0m, baseline);
        await PostReadingAsync(otherClient, 5000m, latest);

        var ownerResponse = await ownerClient.GetAsync("/api/status", TestContext.Current.CancellationToken);
        var ownerBody = await ownerResponse.Content.ReadFromJsonAsync<StatusResponse>(TestContext.Current.CancellationToken);

        ownerBody.ShouldNotBeNull();
        ownerBody.PaceToDateKwh.ShouldBe(1825m);
        ownerBody.Status.ShouldBe("withinRange");
    }

    [Fact]
    public async Task A_principal_without_a_Household_is_forbidden_from_reading_status()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.GetAsync("/api/status", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GET_status_detail_returns_a_null_body_when_no_Yearly_Baseline_is_set()
    {
        var (client, _, _) = await CreateHouseholdAsync();
        await PostReadingAsync(client, 1000m, DateTimeOffset.UtcNow.AddDays(-1));
        await PostReadingAsync(client, 1100m, DateTimeOffset.UtcNow);

        var response = await client.GetAsync("/api/status/detail", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task GET_status_detail_returns_a_null_body_when_fewer_than_two_readings_exist()
    {
        var (client, householdId, version) = await CreateHouseholdAsync();
        await SetYearlyBaselineAsync(client, householdId, 3650m, version);
        await PostReadingAsync(client, 1000m, DateTimeOffset.UtcNow);

        var response = await client.GetAsync("/api/status/detail", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task GET_status_detail_returns_the_detail_figures_once_a_baseline_and_two_readings_exist()
    {
        var (client, householdId, version) = await CreateHouseholdAsync();
        await SetYearlyBaselineAsync(client, householdId, 3650m, version);
        var latest = DateTimeOffset.UtcNow;
        var baseline = latest.AddDays(-182.5);
        await PostReadingAsync(client, 1000m, baseline);
        await PostReadingAsync(client, 2825m, latest);

        var response = await client.GetAsync("/api/status/detail", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<StatusDetailResponse>(TestContext.Current.CancellationToken);
        body.ShouldNotBeNull();
        body.Status.ShouldBe("withinRange");
        body.PaceToDateKwh.ShouldBe(1825m);
        body.BaselineToDateKwh.ShouldBe(1825m);
        body.ElapsedDays.ShouldBe(182.5, tolerance: 0.1);
        body.TrendingThresholdKwh.ShouldBe(100m);
        body.IsLowConfidence.ShouldBeFalse();
        body.DaysSinceLastReading.ShouldBeLessThan(1);
        body.LowConfidenceGapDaysThreshold.ShouldBe(45);
    }

    [Fact]
    public async Task A_households_Status_detail_is_never_affected_by_another_households_readings_or_baseline()
    {
        var (ownerClient, ownerHouseholdId, ownerVersion) = await CreateHouseholdAsync();
        await SetYearlyBaselineAsync(ownerClient, ownerHouseholdId, 3650m, ownerVersion);
        var latest = DateTimeOffset.UtcNow;
        var baseline = latest.AddDays(-182.5);
        await PostReadingAsync(ownerClient, 1000m, baseline);
        await PostReadingAsync(ownerClient, 2825m, latest);

        var (otherClient, otherHouseholdId, otherVersion) = await CreateHouseholdAsync();
        await SetYearlyBaselineAsync(otherClient, otherHouseholdId, 100m, otherVersion);
        await PostReadingAsync(otherClient, 1m, baseline);
        await PostReadingAsync(otherClient, 5000m, latest);

        var ownerResponse = await ownerClient.GetAsync("/api/status/detail", TestContext.Current.CancellationToken);
        var ownerBody = await ownerResponse.Content.ReadFromJsonAsync<StatusDetailResponse>(TestContext.Current.CancellationToken);

        ownerBody.ShouldNotBeNull();
        ownerBody.PaceToDateKwh.ShouldBe(1825m);
        ownerBody.Status.ShouldBe("withinRange");

        // Symmetric proof: the other Household's own detail view must reflect its own data, not
        // bleed the owner's figures either.
        var otherResponse = await otherClient.GetAsync("/api/status/detail", TestContext.Current.CancellationToken);
        var otherBody = await otherResponse.Content.ReadFromJsonAsync<StatusDetailResponse>(TestContext.Current.CancellationToken);

        otherBody.ShouldNotBeNull();
        otherBody.PaceToDateKwh.ShouldBe(4999m);
        otherBody.Status.ShouldBe("trending");
    }

    [Fact]
    public async Task A_principal_without_a_Household_is_forbidden_from_reading_status_detail()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.GetAsync("/api/status/detail", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Saving_a_reading_that_makes_Status_definite_persists_a_StatusSnapshot_row()
    {
        var (client, householdId, version) = await CreateHouseholdAsync();
        await SetYearlyBaselineAsync(client, householdId, 3650m, version);
        var latest = DateTimeOffset.UtcNow;
        var baseline = latest.AddDays(-100);

        // First reading alone leaves Status undefined (AC #6) — no snapshot yet.
        await PostReadingAsync(client, 1000m, baseline);
        (await CountStatusSnapshotRowsAsync(householdId)).ShouldBe(0);

        // Second reading makes Status definite — AC #8: the recompute writes an immutable snapshot.
        // 100 days elapsed -> baseline-to-date = 3650 * 100/365 = 1000 kWh exactly; pace = 1050 kWh,
        // 50 kWh over baseline and comfortably clear of both the WithinRange/BelowBaseline (0) and
        // WithinRange/Trending (100) boundaries — this test only needs a clean WithinRange result to
        // verify AC #8's persistence, not to pin the exact-tie boundary (that's PatternDetectiveCalculatorTests'
        // job, in-memory and free of the JSON/Postgres timestamp round-trip's microsecond precision limits).
        await PostReadingAsync(client, 2050m, latest);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        var snapshot = await dbContext.StatusSnapshots.IgnoreQueryFilters()
            .SingleAsync(s => s.HouseholdId == householdId, TestContext.Current.CancellationToken);

        snapshot.Status.ShouldBe(Status.WithinRange);
        snapshot.PaceToDateKwh.ShouldBe(1050m);
        snapshot.BaselineToDateKwh.ShouldBe(1000m);
    }

    [Fact]
    public async Task GET_status_history_returns_an_empty_array_when_no_snapshots_exist()
    {
        var (client, _, _) = await CreateHouseholdAsync();

        var response = await client.GetAsync("/api/status/history", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("[]");
    }

    [Fact]
    public async Task GET_status_history_returns_entries_ordered_by_ComputedAtUtc_ascending()
    {
        var (client, householdId, version) = await CreateHouseholdAsync();
        await SetYearlyBaselineAsync(client, householdId, 3650m, version);
        var latest = DateTimeOffset.UtcNow;

        // Each additional reading after the first two makes Status definite again and writes
        // another StatusSnapshot row (AC #8's recompute path) — three readings gives two snapshots
        // to check the ordering of.
        await PostReadingAsync(client, 1000m, latest.AddDays(-100));
        await PostReadingAsync(client, 2000m, latest.AddDays(-50));
        await PostReadingAsync(client, 3200m, latest);

        var response = await client.GetAsync("/api/status/history", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<StatusHistoryEntryResponse>>(TestContext.Current.CancellationToken);
        body.ShouldNotBeNull();
        body.Count.ShouldBe(2);
        body[0].ComputedAtUtc.ShouldBeLessThan(body[1].ComputedAtUtc);
        body[0].GapBeforeThisEntry.ShouldBeFalse();
    }

    [Fact]
    public async Task A_households_status_history_is_never_affected_by_another_households_snapshots()
    {
        var (ownerClient, ownerHouseholdId, ownerVersion) = await CreateHouseholdAsync();
        await SetYearlyBaselineAsync(ownerClient, ownerHouseholdId, 3650m, ownerVersion);
        var latest = DateTimeOffset.UtcNow;
        await PostReadingAsync(ownerClient, 1000m, latest.AddDays(-100));
        await PostReadingAsync(ownerClient, 2050m, latest);

        var (otherClient, otherHouseholdId, otherVersion) = await CreateHouseholdAsync();
        await SetYearlyBaselineAsync(otherClient, otherHouseholdId, 100m, otherVersion);
        await PostReadingAsync(otherClient, 1m, latest.AddDays(-100));
        await PostReadingAsync(otherClient, 5000m, latest);

        var ownerResponse = await ownerClient.GetAsync("/api/status/history", TestContext.Current.CancellationToken);
        var ownerBody = await ownerResponse.Content.ReadFromJsonAsync<List<StatusHistoryEntryResponse>>(TestContext.Current.CancellationToken);
        ownerBody.ShouldNotBeNull();
        ownerBody.Count.ShouldBe(1);
        ownerBody[0].PaceToDateKwh.ShouldBe(1050m);

        var otherResponse = await otherClient.GetAsync("/api/status/history", TestContext.Current.CancellationToken);
        var otherBody = await otherResponse.Content.ReadFromJsonAsync<List<StatusHistoryEntryResponse>>(TestContext.Current.CancellationToken);
        otherBody.ShouldNotBeNull();
        otherBody.Count.ShouldBe(1);
        otherBody[0].PaceToDateKwh.ShouldBe(4999m);
    }

    [Fact]
    public async Task A_principal_without_a_Household_is_forbidden_from_reading_status_history()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.GetAsync("/api/status/history", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
