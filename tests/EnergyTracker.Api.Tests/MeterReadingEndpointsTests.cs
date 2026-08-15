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
        var readingTimestamp = DateTimeOffset.UtcNow;

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
}
