using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnergyTracker.Api.Endpoints;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class HouseholdExportEndpointsTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private static async Task<HttpClient> CreateClientWithHouseholdAsync(EnergyTrackerApiFactory factory)
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        return client;
    }

    [Fact]
    public async Task GET_household_export_returns_200_with_the_v2_shape_and_a_download_Content_Disposition()
    {
        var client = await CreateClientWithHouseholdAsync(factory);

        var response = await client.GetAsync("/api/household-export", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        response.Content.Headers.ContentDisposition.ShouldNotBeNull();
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        (response.Content.Headers.ContentDisposition.FileName ?? response.Content.Headers.ContentDisposition.FileNameStar)
            .ShouldNotBeNullOrEmpty();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("formatVersion").GetString().ShouldBe("v2");
        body.TryGetProperty("exportedAtUtc", out _).ShouldBeTrue();
        body.GetProperty("household").GetProperty("locale").GetString().ShouldBe("de-DE");
        body.GetProperty("household").GetProperty("currency").GetString().ShouldBe("EUR");
        body.GetProperty("householdMembers").GetArrayLength().ShouldBe(1);
        body.GetProperty("mainMeter").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("meterReadings").GetArrayLength().ShouldBe(0);
        body.GetProperty("tariffs").GetArrayLength().ShouldBe(0);
        body.GetProperty("events").GetArrayLength().ShouldBe(0);
        body.GetProperty("rooms").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task GET_household_export_includes_real_data_the_Household_actually_logged()
    {
        var client = await CreateClientWithHouseholdAsync(factory);
        await client.PostAsJsonAsync(
            "/api/meter-readings",
            new { kwhValue = 12345.6m, readingTimestamp = DateTimeOffset.UtcNow, idempotencyKey = Guid.NewGuid() },
            TestContext.Current.CancellationToken);
        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Kitchen" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync(
            "/api/events",
            new { description = "cooked 2h", occurredAt = DateTimeOffset.UtcNow, taggedEntityType = "Room", taggedEntityId = room!.Id },
            TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/household-export", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("meterReadings").GetArrayLength().ShouldBe(1);
        body.GetProperty("meterReadings")[0].GetProperty("kwhValue").GetDecimal().ShouldBe(12345.6m);
        body.GetProperty("rooms").GetArrayLength().ShouldBe(1);
        body.GetProperty("rooms")[0].GetProperty("name").GetString().ShouldBe("Kitchen");
        body.GetProperty("events").GetArrayLength().ShouldBe(1);
        body.GetProperty("events")[0].GetProperty("taggedEntityName").GetString().ShouldBe("Kitchen");
    }

    [Fact]
    public async Task A_principal_without_a_Household_is_forbidden_from_exporting()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.GetAsync("/api/household-export", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // AC #3, end-to-end: another Household's data must never appear in this Household's export.
    // Covers Room (via HouseholdExportReader's global-query-filter path) and Tariff/Event (each
    // their own hand-written HouseholdId clause) — SmartPlugReading/StatusSnapshot/AuditCorrection/
    // MeterRegressionPrompt aren't reachable through a simple direct-create endpoint, so their
    // isolation is instead proven at the Infrastructure layer (HouseholdExportReaderTests.
    // Never_returns_another_Households_data), which seeds and asserts all twelve entity categories.
    [Fact]
    public async Task GET_household_export_never_includes_another_Households_data()
    {
        var owner = await CreateClientWithHouseholdAsync(factory);
        var roomResponse = await owner.PostAsJsonAsync("/api/rooms", new { name = "PrivateKitchen" }, TestContext.Current.CancellationToken);
        roomResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tariffResponse = await owner.PostAsJsonAsync(
            "/api/tariffs",
            new { monthlyBaseFee = 8.5m, pricePerKwh = 0.32m, currency = "EUR", contractStartDate = DateTimeOffset.UtcNow, contractPeriodMonths = 12 },
            TestContext.Current.CancellationToken);
        tariffResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var eventResponse = await owner.PostAsJsonAsync(
            "/api/events",
            new { description = "PrivateEvent", occurredAt = DateTimeOffset.UtcNow },
            TestContext.Current.CancellationToken);
        eventResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var outsider = await CreateClientWithHouseholdAsync(factory);
        var response = await outsider.GetAsync("/api/household-export", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rawJson = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        rawJson.ShouldNotContain("PrivateKitchen");
        rawJson.ShouldNotContain("PrivateEvent");
        using var body = JsonDocument.Parse(rawJson);
        body.RootElement.GetProperty("tariffs").GetArrayLength().ShouldBe(0);
    }
}
