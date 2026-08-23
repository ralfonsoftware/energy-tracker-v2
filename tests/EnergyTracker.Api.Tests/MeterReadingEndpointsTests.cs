using System.Net;
using System.Net.Http.Json;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class MeterReadingEndpointsTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private async Task<(HttpClient Client, Guid HouseholdId)> CreateHouseholdAsync()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var response = await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var created = await response.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        return (client, created!.Id);
    }

    private async Task<int> CountMeterReadingRowsAsync(Guid householdId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        // No HttpContext in this scope, so AD-3's query filter (bound to CurrentHouseholdAccessor)
        // resolves to no rows — IgnoreQueryFilters is required to count directly against the table.
        // Scoped to this test's own Household — the fixture's database is shared across every test
        // method in this class, so an unscoped count would pick up rows other tests inserted.
        return await dbContext.MeterReadings.IgnoreQueryFilters()
            .CountAsync(r => r.HouseholdId == householdId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task POST_meter_readings_returns_200_on_create()
    {
        var (client, _) = await CreateHouseholdAsync();

        var response = await client.PostAsJsonAsync(
            "/api/meter-readings",
            new { kwhValue = 4821.5m, readingTimestamp = DateTimeOffset.UtcNow, idempotencyKey = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeterReadingResponse>(TestContext.Current.CancellationToken);
        body!.KwhValue.ShouldBe(4821.5m);
    }

    [Fact]
    public async Task Replaying_the_same_idempotencyKey_returns_the_same_reading_id_with_no_second_row_in_the_table()
    {
        var (client, householdId) = await CreateHouseholdAsync();
        var idempotencyKey = Guid.NewGuid();
        var readingTimestamp = DateTimeOffset.UtcNow;
        var request = new { kwhValue = 4821.5m, readingTimestamp, idempotencyKey };

        var first = await client.PostAsJsonAsync("/api/meter-readings", request, TestContext.Current.CancellationToken);
        var firstBody = await first.Content.ReadFromJsonAsync<MeterReadingResponse>(TestContext.Current.CancellationToken);

        var second = await client.PostAsJsonAsync("/api/meter-readings", request, TestContext.Current.CancellationToken);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var secondBody = await second.Content.ReadFromJsonAsync<MeterReadingResponse>(TestContext.Current.CancellationToken);

        secondBody!.Id.ShouldBe(firstBody!.Id);
        (await CountMeterReadingRowsAsync(householdId)).ShouldBe(1);
    }

    [Fact]
    public async Task Two_readings_with_different_idempotency_keys_on_the_same_calendar_day_both_land_as_distinct_rows()
    {
        var (client, householdId) = await CreateHouseholdAsync();
        // A day in the past — the +3 hour offset below must still land safely before "now" given
        // Story 2.4's ReadingTimestamp bounds validation.
        var readingTimestamp = DateTimeOffset.UtcNow.AddDays(-1);

        var first = await client.PostAsJsonAsync(
            "/api/meter-readings",
            new { kwhValue = 4821.5m, readingTimestamp, idempotencyKey = Guid.NewGuid() },
            TestContext.Current.CancellationToken);
        var second = await client.PostAsJsonAsync(
            "/api/meter-readings",
            new { kwhValue = 4830.2m, readingTimestamp = readingTimestamp.AddHours(3), idempotencyKey = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadFromJsonAsync<MeterReadingResponse>(TestContext.Current.CancellationToken);
        var secondBody = await second.Content.ReadFromJsonAsync<MeterReadingResponse>(TestContext.Current.CancellationToken);
        firstBody!.Id.ShouldNotBe(secondBody!.Id);
        (await CountMeterReadingRowsAsync(householdId)).ShouldBe(2);
    }

    [Fact]
    public async Task A_reading_with_an_earlier_ReadingTimestamp_than_the_most_recent_one_is_accepted()
    {
        var (client, _) = await CreateHouseholdAsync();
        var latest = DateTimeOffset.UtcNow;
        var earlier = latest.AddDays(-1);

        var latestResponse = await client.PostAsJsonAsync(
            "/api/meter-readings",
            new { kwhValue = 4830m, readingTimestamp = latest, idempotencyKey = Guid.NewGuid() },
            TestContext.Current.CancellationToken);
        var backfillResponse = await client.PostAsJsonAsync(
            "/api/meter-readings",
            new { kwhValue = 4700m, readingTimestamp = earlier, idempotencyKey = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

        latestResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        backfillResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task POST_meter_readings_with_a_non_positive_kwhValue_is_rejected()
    {
        var (client, _) = await CreateHouseholdAsync();

        var response = await client.PostAsJsonAsync(
            "/api/meter-readings",
            new { kwhValue = 0m, readingTimestamp = DateTimeOffset.UtcNow, idempotencyKey = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_principal_without_a_Household_is_forbidden_from_logging_a_reading()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync(
            "/api/meter-readings",
            new { kwhValue = 100m, readingTimestamp = DateTimeOffset.UtcNow, idempotencyKey = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GET_meter_readings_returns_a_paginated_page_ordered_by_timestamp_descending()
    {
        var (client, _) = await CreateHouseholdAsync();
        var baseline = DateTimeOffset.UtcNow.AddDays(-1);
        var first = await PostReadingAsync(client, 100m, baseline);
        var firstBody = await first.Content.ReadFromJsonAsync<MeterReadingResponse>(TestContext.Current.CancellationToken);
        var second = await PostReadingAsync(client, 200m, baseline.AddHours(1));
        var secondBody = await second.Content.ReadFromJsonAsync<MeterReadingResponse>(TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/meter-readings?page=1&pageSize=20", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<MeterReadingHistoryPageResponse>(TestContext.Current.CancellationToken);
        page!.TotalCount.ShouldBe(2);
        page.Items.Select(i => i.Id).ShouldBe([secondBody!.Id, firstBody!.Id]);
    }

    [Fact]
    public async Task GET_meter_readings_reflects_the_pending_flag_for_a_reading_under_an_open_regression_prompt()
    {
        var (client, _) = await CreateHouseholdAsync();
        var baseline = DateTimeOffset.UtcNow.AddDays(-1);
        await PostReadingAsync(client, 14302m, baseline);
        var lowerResponse = await PostReadingAsync(client, 412m, baseline.AddHours(1));
        var lowerReading = await lowerResponse.Content.ReadFromJsonAsync<MeterReadingResponse>(TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/meter-readings?page=1&pageSize=20", TestContext.Current.CancellationToken);

        var page = await response.Content.ReadFromJsonAsync<MeterReadingHistoryPageResponse>(TestContext.Current.CancellationToken);
        page!.Items.Count(i => i.IsPendingRegression).ShouldBe(1);
        page.Items.Single(i => i.IsPendingRegression).Id.ShouldBe(lowerReading!.Id);
    }

    [Fact]
    public async Task PUT_meter_readings_id_edits_the_value_and_records_a_correction_note_visible_on_the_next_GET()
    {
        var (client, _) = await CreateHouseholdAsync();
        var created = await PostReadingAsync(client, 100m, DateTimeOffset.UtcNow);
        var createdBody = await created.Content.ReadFromJsonAsync<MeterReadingResponse>(TestContext.Current.CancellationToken);

        var putResponse = await client.PutAsJsonAsync(
            $"/api/meter-readings/{createdBody!.Id}",
            new { kwhValue = 150m, version = createdBody.Version },
            TestContext.Current.CancellationToken);

        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await putResponse.Content.ReadFromJsonAsync<MeterReadingResponse>(TestContext.Current.CancellationToken);
        updated!.KwhValue.ShouldBe(150m);

        var getResponse = await client.GetAsync("/api/meter-readings?page=1&pageSize=20", TestContext.Current.CancellationToken);
        var page = await getResponse.Content.ReadFromJsonAsync<MeterReadingHistoryPageResponse>(TestContext.Current.CancellationToken);
        var item = page!.Items.Single(i => i.Id == createdBody.Id);
        item.CorrectedFromKwhValue.ShouldBe(100m);
        item.CorrectedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task PUT_meter_readings_id_with_a_stale_Version_returns_409()
    {
        var (client, _) = await CreateHouseholdAsync();
        var created = await PostReadingAsync(client, 100m, DateTimeOffset.UtcNow);
        var createdBody = await created.Content.ReadFromJsonAsync<MeterReadingResponse>(TestContext.Current.CancellationToken);

        var response = await client.PutAsJsonAsync(
            $"/api/meter-readings/{createdBody!.Id}",
            new { kwhValue = 150m, version = createdBody.Version + 1 },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PUT_meter_readings_id_for_a_reading_that_does_not_exist_returns_404()
    {
        var (client, _) = await CreateHouseholdAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/meter-readings/{Guid.NewGuid()}",
            new { kwhValue = 150m, version = 0 },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PUT_meter_readings_id_with_an_out_of_range_kwhValue_returns_400()
    {
        var (client, _) = await CreateHouseholdAsync();
        var created = await PostReadingAsync(client, 100m, DateTimeOffset.UtcNow);
        var createdBody = await created.Content.ReadFromJsonAsync<MeterReadingResponse>(TestContext.Current.CancellationToken);

        var response = await client.PutAsJsonAsync(
            $"/api/meter-readings/{createdBody!.Id}",
            new { kwhValue = 0m, version = createdBody.Version },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_households_meter_reading_history_is_never_affected_by_another_households_readings()
    {
        var (clientA, _) = await CreateHouseholdAsync();
        var (clientB, _) = await CreateHouseholdAsync();
        await PostReadingAsync(clientA, 100m, DateTimeOffset.UtcNow);
        await PostReadingAsync(clientB, 200m, DateTimeOffset.UtcNow);

        var pageA = await (await clientA.GetAsync("/api/meter-readings?page=1&pageSize=20", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<MeterReadingHistoryPageResponse>(TestContext.Current.CancellationToken);
        var pageB = await (await clientB.GetAsync("/api/meter-readings?page=1&pageSize=20", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<MeterReadingHistoryPageResponse>(TestContext.Current.CancellationToken);

        pageA!.Items.ShouldAllBe(i => i.KwhValue == 100m);
        pageB!.Items.ShouldAllBe(i => i.KwhValue == 200m);
    }

    [Fact]
    public async Task A_principal_without_a_Household_is_forbidden_from_reading_the_history()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.GetAsync("/api/meter-readings?page=1&pageSize=20", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_principal_without_a_Household_is_forbidden_from_editing_a_reading()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.PutAsJsonAsync(
            $"/api/meter-readings/{Guid.NewGuid()}",
            new { kwhValue = 100m, version = 0 },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static Task<HttpResponseMessage> PostReadingAsync(HttpClient client, decimal kwhValue, DateTimeOffset readingTimestamp) =>
        client.PostAsJsonAsync(
            "/api/meter-readings",
            new { kwhValue, readingTimestamp, idempotencyKey = Guid.NewGuid() },
            TestContext.Current.CancellationToken);
}
