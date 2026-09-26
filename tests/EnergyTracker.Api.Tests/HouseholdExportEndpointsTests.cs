using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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

    private static async Task<(HttpClient Client, Guid HouseholdId)> CreateClientWithHouseholdIdAsync(EnergyTrackerApiFactory factory, string subject)
    {
        var client = factory.CreateAuthenticatedClient(subject);
        var response = await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var household = await response.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        return (client, household!.Id);
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

    // A1 (test-design P0, "must not be skipped"): a stand-in for the 2026-09-25 production
    // incident's SmartPlugReadings volume — large enough to force HouseholdExportReader's keyset
    // pagination through many page round trips (PageSize is 500 internally), not just a single one.
    // Measuring actual peak working-set isn't reliable from an in-process WebApplicationFactory
    // test (the handler can run on a different pooled thread than GC.GetAllocatedBytesForCurrentThread
    // would observe, and the TestServer transport has no container-style memory ceiling to trip
    // regardless of implementation) — so the regression guard here is completion + full-row-count
    // correctness at volume, which is what would actually fail if the old unbounded-read
    // implementation regressed back in.
    private const int IncidentVolumeSmartPlugReadingCount = 5_000;

    [Fact]
    public async Task GET_household_export_completes_and_returns_every_row_at_incident_calibrated_volume()
    {
        var (client, householdId) = await CreateClientWithHouseholdIdAsync(factory, Guid.NewGuid().ToString());
        await factory.SeedSmartPlugReadingsAsync(householdId, IncidentVolumeSmartPlugReadingCount);

        var response = await client.GetAsync("/api/household-export", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("smartPlugReadings").GetArrayLength().ShouldBe(IncidentVolumeSmartPlugReadingCount);
    }

    // A2 (test-design P0, score-9 BLOCK finding R-001): a mid-export DB fault must never present as
    // a clean, complete 200 — verified end to end through the real endpoint by substituting a
    // reader that yields one row and then throws, standing in for a DB read failing partway through
    // paging. Swaps IHouseholdExportReader via WithWebHostBuilder (a fresh factory derived from the
    // shared one, same Testcontainers Postgres connection — no second container needed) rather than
    // touching the shared fixture's DI, since IClassFixture shares that instance across every other
    // test in this class.
    [Fact]
    public async Task GET_household_export_never_completes_as_a_clean_200_when_a_mid_export_fault_occurs()
    {
        var subject = Guid.NewGuid().ToString();
        var (setupClient, householdId) = await CreateClientWithHouseholdIdAsync(factory, subject);
        _ = setupClient;

        await using var faultyFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddScoped<IHouseholdExportReader>(_ => new FaultingHouseholdExportReader())));
        var faultyClient = faultyFactory.CreateClient();
        faultyClient.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, subject);
        faultyClient.DefaultRequestHeaders.Add(TestAuthHandler.IssuerHeader, TestAuthHandler.DefaultIssuer);

        var response = await faultyClient.GetAsync(
            "/api/household-export", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);

        // Headers/status are already committed by the time the fault hits (the first array,
        // householdMembers, flushes before meterReadings — where the fault is injected — even
        // starts), so the client sees a normal 200 here; the fault must show up when reading the
        // body instead.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Should.ThrowAsync<Exception>(async () => await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private sealed class FaultingHouseholdExportReader : IHouseholdExportReader
    {
        public Task<HouseholdExportData> GetExportDataAsync(Guid householdId, CancellationToken cancellationToken)
        {
            var household = new Household
            {
                Id = householdId,
                Locale = "de-DE",
                Currency = "EUR",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };

            return Task.FromResult(new HouseholdExportData(
                household,
                Empty<HouseholdMember>(),
                null,
                FaultingMeterReadings(householdId),
                Empty<MeterRegressionPrompt>(),
                Empty<Tariff>(),
                Empty<Event>(),
                Empty<Room>(),
                Empty<PowerPoint>(),
                Empty<Device>(),
                Empty<SmartPlugReading>(),
                Empty<StatusSnapshot>(),
                Empty<AuditCorrection>()));
        }

        private static async IAsyncEnumerable<T> Empty<T>()
        {
            await Task.Yield();
            yield break;
        }

        private static async IAsyncEnumerable<MeterReading> FaultingMeterReadings(Guid householdId)
        {
            yield return new MeterReading
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                MainMeterId = Guid.NewGuid(),
                KwhValue = 1m,
                ReadingTimestamp = DateTimeOffset.UtcNow,
                IdempotencyKey = Guid.NewGuid(),
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            await Task.Yield();
            throw new InvalidOperationException("Simulated mid-export database fault (test double for a DB read failing partway through paging).");
        }
    }
}
