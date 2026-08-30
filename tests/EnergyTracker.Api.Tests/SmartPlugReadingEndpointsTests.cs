using System.Net;
using System.Net.Http.Json;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class SmartPlugReadingEndpointsTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private async Task<(HttpClient Client, Guid HouseholdId)> CreateHouseholdAsync()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var response = await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var created = await response.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        return (client, created!.Id);
    }

    private async Task<Guid> CreateRoomAsync(HttpClient client, string roomName)
    {
        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = roomName }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        return room!.Id;
    }

    private async Task<Guid> CreatePowerPointAsync(HttpClient client, string roomName, string powerPointName)
    {
        var roomId = await CreateRoomAsync(client, roomName);
        return await CreatePowerPointInRoomAsync(client, roomId, powerPointName);
    }

    private async Task<Guid> CreatePowerPointInRoomAsync(HttpClient client, Guid roomId, string powerPointName)
    {
        var powerPointResponse = await client.PostAsJsonAsync(
            "/api/power-points", new { roomId, name = powerPointName }, TestContext.Current.CancellationToken);
        powerPointResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var powerPoint = await powerPointResponse.Content.ReadFromJsonAsync<PowerPointResponse>(TestContext.Current.CancellationToken);
        return powerPoint!.Id;
    }

    // Seeded directly against the DbContext rather than driven through the full parser/upload
    // pipeline — same precedent as HouseholdInviteTests' directly-seeded expired invite — since
    // this endpoint's own concern is the read/grouping shape, not import parsing (already covered
    // by SmartPlugImportEndpointsTests).
    private async Task SeedReadingAsync(
        Guid householdId, Guid? powerPointId, string roomName, string powerPointName, string deviceName, decimal kwhValue)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        dbContext.SmartPlugReadings.Add(new SmartPlugReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            PowerPointId = powerPointId,
            RoomName = roomName,
            PowerPointName = powerPointName,
            DeviceName = deviceName,
            IntervalStart = DateTimeOffset.UtcNow.AddHours(-1),
            IntervalEnd = DateTimeOffset.UtcNow,
            KwhValue = kwhValue,
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GET_smart_plug_readings_returns_an_empty_array_when_no_readings_exist()
    {
        var (client, _) = await CreateHouseholdAsync();

        var response = await client.GetAsync("/api/smart-plug-readings", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("[]");
    }

    [Fact]
    public async Task Readings_with_a_PowerPointId_are_grouped_and_summed_by_Room_Power_Point_and_Device()
    {
        var (client, householdId) = await CreateHouseholdAsync();
        var powerPointId = await CreatePowerPointAsync(client, "Living Room", "TV Power Point");

        await SeedReadingAsync(householdId, powerPointId, "Living Room", "TV Power Point", "Smart TV", 20m);
        await SeedReadingAsync(householdId, powerPointId, "Living Room", "TV Power Point", "Smart TV", 18m);
        await SeedReadingAsync(householdId, powerPointId, "Living Room", "TV Power Point", "Games Console", 22m);

        var response = await client.GetAsync("/api/smart-plug-readings", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<RoomMeasuredDataResponse>>(TestContext.Current.CancellationToken);
        body.ShouldNotBeNull();
        body.Count.ShouldBe(1);
        var room = body[0];
        room.RoomName.ShouldBe("Living Room");
        room.TotalKwh.ShouldBe(60m);
        room.PowerPoints.Count.ShouldBe(1);
        var powerPoint = room.PowerPoints[0];
        powerPoint.PowerPointName.ShouldBe("TV Power Point");
        powerPoint.TotalKwh.ShouldBe(60m);
        powerPoint.Devices.Count.ShouldBe(2);
        powerPoint.Devices.Single(d => d.DeviceName == "Smart TV").TotalKwh.ShouldBe(38m);
        powerPoint.Devices.Single(d => d.DeviceName == "Games Console").TotalKwh.ShouldBe(22m);
    }

    [Fact]
    public async Task Readings_still_AwaitingPowerPointMapping_are_excluded()
    {
        var (client, householdId) = await CreateHouseholdAsync();

        // No Power Point mapping yet — PowerPointId stays null (Story 3.2's AwaitingPowerPointMapping state).
        await SeedReadingAsync(householdId, powerPointId: null, "Unmapped Room", "Unmapped Power Point", "Some Device", 99m);

        var response = await client.GetAsync("/api/smart-plug-readings", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("[]");
    }

    [Fact]
    public async Task A_households_readings_are_never_affected_by_another_households_readings()
    {
        var (ownerClient, ownerHouseholdId) = await CreateHouseholdAsync();
        var ownerPowerPointId = await CreatePowerPointAsync(ownerClient, "Living Room", "TV Power Point");
        await SeedReadingAsync(ownerHouseholdId, ownerPowerPointId, "Living Room", "TV Power Point", "Smart TV", 38m);

        var (otherClient, otherHouseholdId) = await CreateHouseholdAsync();
        var otherPowerPointId = await CreatePowerPointAsync(otherClient, "Kitchen", "Fridge Circuit");
        await SeedReadingAsync(otherHouseholdId, otherPowerPointId, "Kitchen", "Fridge Circuit", "Fridge-Freezer", 164m);

        var ownerResponse = await ownerClient.GetAsync("/api/smart-plug-readings", TestContext.Current.CancellationToken);
        var ownerBody = await ownerResponse.Content.ReadFromJsonAsync<List<RoomMeasuredDataResponse>>(TestContext.Current.CancellationToken);
        ownerBody.ShouldNotBeNull();
        ownerBody.Count.ShouldBe(1);
        ownerBody[0].RoomName.ShouldBe("Living Room");

        var otherResponse = await otherClient.GetAsync("/api/smart-plug-readings", TestContext.Current.CancellationToken);
        var otherBody = await otherResponse.Content.ReadFromJsonAsync<List<RoomMeasuredDataResponse>>(TestContext.Current.CancellationToken);
        otherBody.ShouldNotBeNull();
        otherBody.Count.ShouldBe(1);
        otherBody[0].RoomName.ShouldBe("Kitchen");
    }

    [Fact]
    public async Task A_principal_without_a_Household_is_forbidden_from_reading_smart_plug_readings()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.GetAsync("/api/smart-plug-readings", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // AD-10 regression guard: a retagged Power Point's already-imported readings must keep the
    // RoomName/PowerPointName that were snapshotted at import time — the tree is built by grouping
    // on those denormalized string columns only, never a live join to the Room/PowerPoint tables,
    // so a later rename must not silently move this history to the new name.
    [Fact]
    public async Task A_retagged_Power_Points_historical_readings_keep_their_original_snapshotted_RoomName_and_PowerPointName()
    {
        var (client, householdId) = await CreateHouseholdAsync();
        var powerPointId = await CreatePowerPointAsync(client, "Living Room", "TV Power Point");
        await SeedReadingAsync(householdId, powerPointId, "Living Room", "TV Power Point", "Smart TV", 38m);

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/power-points/{powerPointId}", new { name = "Home Cinema Power Point" }, TestContext.Current.CancellationToken);
        renameResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await client.GetAsync("/api/smart-plug-readings", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<RoomMeasuredDataResponse>>(TestContext.Current.CancellationToken);
        body.ShouldNotBeNull();
        body.Count.ShouldBe(1);
        // Still the pre-rename name — the retag must not have moved this history.
        body[0].PowerPoints.Single().PowerPointName.ShouldBe("TV Power Point");
        body[0].PowerPoints.ShouldNotContain(pp => pp.PowerPointName == "Home Cinema Power Point");
    }

    // AD-10 regression guard, Room-rename path: renaming the Room a Power Point belongs to is just
    // as much a "retag" as renaming the Power Point itself — already-imported readings must keep
    // the RoomName snapshotted at import time, not follow the live Room's new name.
    [Fact]
    public async Task A_renamed_Rooms_historical_readings_keep_their_original_snapshotted_RoomName()
    {
        var (client, householdId) = await CreateHouseholdAsync();
        var roomId = await CreateRoomAsync(client, "Living Room");
        var powerPointResponse = await client.PostAsJsonAsync(
            "/api/power-points", new { roomId, name = "TV Power Point" }, TestContext.Current.CancellationToken);
        var powerPoint = await powerPointResponse.Content.ReadFromJsonAsync<PowerPointResponse>(TestContext.Current.CancellationToken);
        await SeedReadingAsync(householdId, powerPoint!.Id, "Living Room", "TV Power Point", "Smart TV", 38m);

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/rooms/{roomId}", new { name = "Family Room" }, TestContext.Current.CancellationToken);
        renameResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await client.GetAsync("/api/smart-plug-readings", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<RoomMeasuredDataResponse>>(TestContext.Current.CancellationToken);
        body.ShouldNotBeNull();
        body.Count.ShouldBe(1);
        // Still the pre-rename Room name — the retag must not have moved this history.
        body[0].RoomName.ShouldBe("Living Room");
        body.ShouldNotContain(r => r.RoomName == "Family Room");
    }

    // AD-10 regression guard, Power-Point-moved-to-a-different-Room path — arguably the most
    // natural "retag" scenario AC #3 is guarding against: the Power Point's Room assignment
    // literally changes. Already-imported readings must keep the RoomName active at import time.
    [Fact]
    public async Task A_Power_Points_historical_readings_keep_their_original_snapshotted_RoomName_after_being_moved_to_a_different_Room()
    {
        var (client, householdId) = await CreateHouseholdAsync();
        var powerPointId = await CreatePowerPointAsync(client, "Living Room", "TV Power Point");
        await SeedReadingAsync(householdId, powerPointId, "Living Room", "TV Power Point", "Smart TV", 38m);
        var newRoomId = await CreateRoomAsync(client, "Office");

        var moveResponse = await client.PutAsJsonAsync(
            $"/api/power-points/{powerPointId}/room", new { roomId = newRoomId }, TestContext.Current.CancellationToken);
        moveResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await client.GetAsync("/api/smart-plug-readings", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<RoomMeasuredDataResponse>>(TestContext.Current.CancellationToken);
        body.ShouldNotBeNull();
        body.Count.ShouldBe(1);
        // Still the pre-move Room name — the retag must not have moved this history.
        body[0].RoomName.ShouldBe("Living Room");
        body.ShouldNotContain(r => r.RoomName == "Office");
    }

    // Code-review fix regression guard: renaming PP-A away from a name frees that name for reuse
    // (RenamePowerPoint's uniqueness check only blocks currently-live collisions in the same Room,
    // not a name a live sibling has since been renamed away from) — a genuinely reachable sequence
    // through normal endpoints, no deletion involved. PP-A's and PP-B's history must not merge into
    // one tree node just because they now share a display-string tuple; the grouping disambiguates
    // by the underlying PowerPointId.
    [Fact]
    public async Task Two_different_Power_Points_that_end_up_sharing_the_same_name_via_reuse_are_not_merged()
    {
        var (client, householdId) = await CreateHouseholdAsync();
        var roomId = await CreateRoomAsync(client, "Living Room");
        var originalPowerPointId = await CreatePowerPointInRoomAsync(client, roomId, "TV Power Point");
        await SeedReadingAsync(householdId, originalPowerPointId, "Living Room", "TV Power Point", "Smart TV", 38m);

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/power-points/{originalPowerPointId}", new { name = "Home Cinema Power Point" }, TestContext.Current.CancellationToken);
        renameResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // A different, later Power Point takes over the now-freed "TV Power Point" name in the
        // same Room.
        var laterPowerPointId = await CreatePowerPointInRoomAsync(client, roomId, "TV Power Point");
        await SeedReadingAsync(householdId, laterPowerPointId, "Living Room", "TV Power Point", "Soundbar", 12m);

        var response = await client.GetAsync("/api/smart-plug-readings", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<RoomMeasuredDataResponse>>(TestContext.Current.CancellationToken);
        body.ShouldNotBeNull();
        body.Count.ShouldBe(1);
        var livingRoom = body[0];
        livingRoom.TotalKwh.ShouldBe(50m);
        // Two separate nodes, not one merged "TV Power Point" node holding both devices.
        livingRoom.PowerPoints.Count.ShouldBe(2);
        livingRoom.PowerPoints.ShouldAllBe(pp => pp.PowerPointName == "TV Power Point");
        livingRoom.PowerPoints.Single(pp => pp.Devices.Single().DeviceName == "Smart TV").TotalKwh.ShouldBe(38m);
        livingRoom.PowerPoints.Single(pp => pp.Devices.Single().DeviceName == "Soundbar").TotalKwh.ShouldBe(12m);
    }
}
