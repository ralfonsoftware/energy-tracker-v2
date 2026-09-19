using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnergyTracker.Api.Endpoints;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class EventEndpointsTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private static async Task<HttpClient> CreateClientWithHouseholdAsync(EnergyTrackerApiFactory factory)
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        return client;
    }

    [Fact]
    public async Task POST_events_returns_200_on_a_valid_untagged_create()
    {
        var client = await CreateClientWithHouseholdAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/api/events",
            new { description = "away 2 weeks", occurredAt = DateTimeOffset.UtcNow, taggedEntityType = (string?)null, taggedEntityId = (Guid?)null },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<EventResponse>(TestContext.Current.CancellationToken);
        body!.Description.ShouldBe("away 2 weeks");
        body.TaggedEntityType.ShouldBeNull();
        body.TaggedEntityName.ShouldBeNull();
    }

    // End-to-end regression for AC #6 (spec-datetimeoffset-utc-normalization.md, review loop 1/2):
    // Blind Hunter's loop-2 finding was that every prior test asserted only the repository's
    // return value, never the actual HTTP JSON response a client would see — a bug in
    // EventEndpoints.ToResponse's own mapping would not have been caught by those. This goes
    // through the real ASP.NET Core pipeline (EnergyTrackerApiFactory, a real Postgres
    // Testcontainer) and reads the wire JSON offset directly.
    [Fact]
    public async Task POST_events_with_a_non_zero_wire_offset_echoes_a_normalized_offset_in_the_response()
    {
        var client = await CreateClientWithHouseholdAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/api/events",
            new { description = "non-zero offset probe", occurredAt = new DateTimeOffset(2026, 8, 1, 9, 15, 0, TimeSpan.FromHours(2)), taggedEntityType = (string?)null, taggedEntityId = (Guid?)null },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rawJson = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var parsed = JsonDocument.Parse(rawJson);
        var wireOccurredAt = parsed.RootElement.GetProperty("occurredAt").GetString();
        wireOccurredAt.ShouldNotBeNull();
        wireOccurredAt.ShouldEndWith("+00:00");

        var body = await response.Content.ReadFromJsonAsync<EventResponse>(TestContext.Current.CancellationToken);
        body!.OccurredAt.Offset.ShouldBe(TimeSpan.Zero);
        body.OccurredAt.ShouldBe(new DateTimeOffset(2026, 8, 1, 7, 15, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task POST_events_returns_200_on_a_valid_tagged_create()
    {
        var client = await CreateClientWithHouseholdAsync(factory);
        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Kitchen" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(
            "/api/events",
            new { description = "cooked 2h", occurredAt = DateTimeOffset.UtcNow, taggedEntityType = "Room", taggedEntityId = room!.Id },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<EventResponse>(TestContext.Current.CancellationToken);
        body!.TaggedEntityType.ShouldBe("Room");
        body.TaggedEntityName.ShouldBe("Kitchen");
    }

    [Fact]
    public async Task POST_events_with_a_blank_Description_returns_400()
    {
        var client = await CreateClientWithHouseholdAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/api/events",
            new { description = "   ", occurredAt = DateTimeOffset.UtcNow, taggedEntityType = (string?)null, taggedEntityId = (Guid?)null },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task POST_events_with_a_Description_over_the_cap_returns_400()
    {
        var client = await CreateClientWithHouseholdAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/api/events",
            new { description = new string('a', 501), occurredAt = DateTimeOffset.UtcNow, taggedEntityType = (string?)null, taggedEntityId = (Guid?)null },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_principal_without_a_Household_is_forbidden_from_logging_an_Event()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync(
            "/api/events",
            new { description = "cooked 2h", occurredAt = DateTimeOffset.UtcNow, taggedEntityType = (string?)null, taggedEntityId = (Guid?)null },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task POST_events_tagging_an_already_archived_Room_returns_409()
    {
        var client = await CreateClientWithHouseholdAsync(factory);
        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Kitchen" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        await client.DeleteAsync($"/api/rooms/{room!.Id}", TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(
            "/api/events",
            new { description = "cooked 2h", occurredAt = DateTimeOffset.UtcNow, taggedEntityType = "Room", taggedEntityId = room.Id },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task POST_events_tagging_an_already_archived_PowerPoint_returns_409()
    {
        var client = await CreateClientWithHouseholdAsync(factory);
        var powerPoint = await CreatePowerPointAsync(client, "Kitchen", "Counter outlet");
        await client.DeleteAsync($"/api/power-points/{powerPoint.Id}", TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(
            "/api/events",
            new { description = "cooked 2h", occurredAt = DateTimeOffset.UtcNow, taggedEntityType = "PowerPoint", taggedEntityId = powerPoint.Id },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task POST_events_tagging_an_already_archived_Device_returns_409()
    {
        var client = await CreateClientWithHouseholdAsync(factory);
        var powerPoint = await CreatePowerPointAsync(client, "Kitchen", "Counter outlet");
        var deviceResponse = await client.PostAsJsonAsync(
            "/api/devices", new { powerPointId = powerPoint.Id, name = "Induction cooktop" }, TestContext.Current.CancellationToken);
        var device = await deviceResponse.Content.ReadFromJsonAsync<DeviceResponse>(TestContext.Current.CancellationToken);
        await client.DeleteAsync($"/api/devices/{device!.Id}", TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(
            "/api/events",
            new { description = "cooked 2h", occurredAt = DateTimeOffset.UtcNow, taggedEntityType = "Device", taggedEntityId = device.Id },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // A tag target that never existed is a 404, not the 409 reserved for one that exists but is
    // archived — the two are distinct states and the client renders different guidance for each.
    [Fact]
    public async Task POST_events_tagging_a_nonexistent_Room_returns_404()
    {
        var client = await CreateClientWithHouseholdAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/api/events",
            new { description = "cooked 2h", occurredAt = DateTimeOffset.UtcNow, taggedEntityType = "Room", taggedEntityId = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // AD-3: the DbContext query filter makes another Household's Room invisible, so this must take
    // the same not-found path as a bogus id — never succeed, and never disclose the foreign Room's
    // name or that it exists at all.
    [Fact]
    public async Task POST_events_cannot_tag_another_Households_Room()
    {
        var owner = await CreateClientWithHouseholdAsync(factory);
        var roomResponse = await owner.PostAsJsonAsync("/api/rooms", new { name = "PrivateKitchen" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);

        var outsider = await CreateClientWithHouseholdAsync(factory);
        var response = await outsider.PostAsJsonAsync(
            "/api/events",
            new { description = "cooked 2h", occurredAt = DateTimeOffset.UtcNow, taggedEntityType = "Room", taggedEntityId = room!.Id },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldNotContain("PrivateKitchen");
    }

    // JSON binding supplies null for a non-nullable ref-type property, so this must be the same 400
    // as a blank Description rather than a NullReferenceException surfacing as a 500.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task POST_events_with_a_null_or_missing_Description_returns_400(bool explicitNull)
    {
        var client = await CreateClientWithHouseholdAsync(factory);

        object payload = explicitNull
            ? new { description = (string?)null, occurredAt = DateTimeOffset.UtcNow }
            : new { occurredAt = DateTimeOffset.UtcNow };

        var response = await client.PostAsJsonAsync("/api/events", payload, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // The client localizes from this code (AD-18) instead of echoing the English detail string.
    [Fact]
    public async Task POST_events_returns_a_machine_readable_errorCode_in_the_ProblemDetails()
    {
        var client = await CreateClientWithHouseholdAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/api/events",
            new { description = new string('a', 501), occurredAt = DateTimeOffset.UtcNow },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("errorCode").GetString().ShouldBe("event.description_too_long");
    }

    [Fact]
    public async Task GET_events_returns_200_with_the_page_shape()
    {
        var client = await CreateClientWithHouseholdAsync(factory);
        await client.PostAsJsonAsync(
            "/api/events",
            new { description = "cooked 2h", occurredAt = DateTimeOffset.UtcNow, taggedEntityType = (string?)null, taggedEntityId = (Guid?)null },
            TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/events", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<EventHistoryPageResponse>(TestContext.Current.CancellationToken);
        body!.TotalCount.ShouldBe(1);
        body.Page.ShouldBe(1);
        body.PageSize.ShouldBe(20);
        body.Items.Single().Description.ShouldBe("cooked 2h");
    }

    [Fact]
    public async Task GET_events_returns_a_400_with_an_errorCode_for_a_bad_page()
    {
        var client = await CreateClientWithHouseholdAsync(factory);

        var response = await client.GetAsync("/api/events?page=0", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("errorCode").GetString().ShouldBe("event.page_invalid");
    }

    [Fact]
    public async Task GET_events_returns_a_400_with_an_errorCode_for_a_bad_pageSize()
    {
        var client = await CreateClientWithHouseholdAsync(factory);

        var response = await client.GetAsync("/api/events?pageSize=101", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("errorCode").GetString().ShouldBe("event.page_size_invalid");
    }

    [Fact]
    public async Task A_principal_without_a_Household_is_forbidden_from_reading_Event_history()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.GetAsync("/api/events", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // AC #3, end-to-end: the display path must render the AD-10 snapshot even after the tagged
    // Room is archived, never re-derive it and never 404/error.
    [Fact]
    public async Task GET_events_still_returns_the_original_taggedEntityName_after_the_tagged_Room_is_archived()
    {
        var client = await CreateClientWithHouseholdAsync(factory);
        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Kitchen" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync(
            "/api/events",
            new { description = "cooked 2h", occurredAt = DateTimeOffset.UtcNow, taggedEntityType = "Room", taggedEntityId = room!.Id },
            TestContext.Current.CancellationToken);
        await client.DeleteAsync($"/api/rooms/{room.Id}", TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/events", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<EventHistoryPageResponse>(TestContext.Current.CancellationToken);
        var entry = body!.Items.Single();
        entry.TaggedEntityType.ShouldBe("Room");
        entry.TaggedEntityName.ShouldBe("Kitchen");
    }

    private static async Task<PowerPointResponse> CreatePowerPointAsync(HttpClient client, string roomName, string powerPointName)
    {
        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = roomName }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        var powerPointResponse = await client.PostAsJsonAsync(
            "/api/power-points", new { roomId = room!.Id, name = powerPointName }, TestContext.Current.CancellationToken);
        return (await powerPointResponse.Content.ReadFromJsonAsync<PowerPointResponse>(TestContext.Current.CancellationToken))!;
    }
}
