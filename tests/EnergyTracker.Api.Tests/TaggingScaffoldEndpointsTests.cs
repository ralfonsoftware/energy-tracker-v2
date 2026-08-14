using System.Net;
using System.Net.Http.Json;
using EnergyTracker.Api.Endpoints;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class TaggingScaffoldEndpointsTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private static async Task<HttpClient> CreateClientWithHouseholdAsync(EnergyTrackerApiFactory factory)
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        return client;
    }

    [Fact]
    public async Task AC1_A_member_can_create_a_room_then_a_power_point_then_a_device_scoped_to_their_own_household()
    {
        var clientA = await CreateClientWithHouseholdAsync(factory);

        var roomResponse = await clientA.PostAsJsonAsync("/api/rooms", new { name = "Kitchen" }, TestContext.Current.CancellationToken);
        roomResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);

        var powerPointResponse = await clientA.PostAsJsonAsync("/api/power-points", new { roomId = room!.Id, name = "Counter outlet" }, TestContext.Current.CancellationToken);
        powerPointResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var powerPoint = await powerPointResponse.Content.ReadFromJsonAsync<PowerPointResponse>(TestContext.Current.CancellationToken);

        var deviceResponse = await clientA.PostAsJsonAsync("/api/devices", new { powerPointId = powerPoint!.Id, name = "Kettle" }, TestContext.Current.CancellationToken);
        deviceResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var device = await deviceResponse.Content.ReadFromJsonAsync<DeviceResponse>(TestContext.Current.CancellationToken);

        var rooms = await clientA.GetFromJsonAsync<List<RoomResponse>>("/api/rooms", TestContext.Current.CancellationToken);
        rooms!.ShouldContain(r => r.Id == room.Id);

        var powerPoints = await clientA.GetFromJsonAsync<List<PowerPointResponse>>("/api/power-points", TestContext.Current.CancellationToken);
        powerPoints!.ShouldContain(p => p.Id == powerPoint.Id);

        var devices = await clientA.GetFromJsonAsync<List<DeviceResponse>>("/api/devices", TestContext.Current.CancellationToken);
        devices!.ShouldContain(d => d.Id == device!.Id);

        // A second, distinct principal with their own Household sees none of A's rows (tenant
        // isolation — also covers AC #2's read side).
        var clientB = await CreateClientWithHouseholdAsync(factory);
        var roomsForB = await clientB.GetFromJsonAsync<List<RoomResponse>>("/api/rooms", TestContext.Current.CancellationToken);
        roomsForB!.ShouldNotContain(r => r.Id == room.Id);
        var powerPointsForB = await clientB.GetFromJsonAsync<List<PowerPointResponse>>("/api/power-points", TestContext.Current.CancellationToken);
        powerPointsForB!.ShouldNotContain(p => p.Id == powerPoint.Id);
        var devicesForB = await clientB.GetFromJsonAsync<List<DeviceResponse>>("/api/devices", TestContext.Current.CancellationToken);
        devicesForB!.ShouldNotContain(d => d.Id == device!.Id);
    }

    [Fact]
    public async Task AC2_A_principal_cannot_edit_or_delete_another_Households_Room_PowerPoint_or_Device()
    {
        var clientA = await CreateClientWithHouseholdAsync(factory);
        var roomResponse = await clientA.PostAsJsonAsync("/api/rooms", new { name = "Kitchen" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        var powerPointResponse = await clientA.PostAsJsonAsync("/api/power-points", new { roomId = room!.Id, name = "Counter outlet" }, TestContext.Current.CancellationToken);
        var powerPoint = await powerPointResponse.Content.ReadFromJsonAsync<PowerPointResponse>(TestContext.Current.CancellationToken);
        var deviceResponse = await clientA.PostAsJsonAsync("/api/devices", new { powerPointId = powerPoint!.Id, name = "Kettle" }, TestContext.Current.CancellationToken);
        var device = await deviceResponse.Content.ReadFromJsonAsync<DeviceResponse>(TestContext.Current.CancellationToken);

        var clientB = await CreateClientWithHouseholdAsync(factory);

        (await clientB.PutAsJsonAsync($"/api/rooms/{room.Id}", new { name = "Hijacked" }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clientB.DeleteAsync($"/api/rooms/{room.Id}", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clientB.PutAsJsonAsync($"/api/power-points/{powerPoint.Id}", new { name = "Hijacked" }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clientB.DeleteAsync($"/api/power-points/{powerPoint.Id}", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clientB.PutAsJsonAsync($"/api/devices/{device!.Id}", new { name = "Hijacked" }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clientB.DeleteAsync($"/api/devices/{device.Id}", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A's own edits on A's own rows succeed and are reflected in a subsequent GET.
        var renameResponse = await clientA.PutAsJsonAsync($"/api/rooms/{room.Id}", new { name = "Renamed kitchen" }, TestContext.Current.CancellationToken);
        renameResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rooms = await clientA.GetFromJsonAsync<List<RoomResponse>>("/api/rooms", TestContext.Current.CancellationToken);
        rooms!.Single(r => r.Id == room.Id).Name.ShouldBe("Renamed kitchen");
    }

    [Fact]
    public async Task AC3_Deleting_soft_deletes_the_row_and_a_second_delete_is_idempotent()
    {
        var client = await CreateClientWithHouseholdAsync(factory);
        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Kitchen" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);

        var firstDelete = await client.DeleteAsync($"/api/rooms/{room!.Id}", TestContext.Current.CancellationToken);
        firstDelete.StatusCode.ShouldBe(HttpStatusCode.OK);
        var archivedRoom = await firstDelete.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        archivedRoom!.ArchivedAt.ShouldNotBeNull();

        // Still exists — not gone, not 404 — proving soft-delete, not hard-delete.
        var rooms = await client.GetFromJsonAsync<List<RoomResponse>>("/api/rooms", TestContext.Current.CancellationToken);
        rooms!.ShouldContain(r => r.Id == room.Id);

        var secondDelete = await client.DeleteAsync($"/api/rooms/{room.Id}", TestContext.Current.CancellationToken);
        secondDelete.StatusCode.ShouldBe(HttpStatusCode.OK);
        var secondArchivedRoom = await secondDelete.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        secondArchivedRoom!.ArchivedAt.ShouldBe(archivedRoom.ArchivedAt);
    }

    [Fact]
    public async Task AC4_Creating_under_an_archived_parent_returns_409_and_the_archived_parent_still_resolves_via_GET()
    {
        var client = await CreateClientWithHouseholdAsync(factory);

        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Kitchen" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        await client.DeleteAsync($"/api/rooms/{room!.Id}", TestContext.Current.CancellationToken);

        var powerPointUnderArchivedRoom = await client.PostAsJsonAsync("/api/power-points", new { roomId = room.Id, name = "Counter outlet" }, TestContext.Current.CancellationToken);
        powerPointUnderArchivedRoom.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var rooms = await client.GetFromJsonAsync<List<RoomResponse>>("/api/rooms", TestContext.Current.CancellationToken);
        rooms!.ShouldContain(r => r.Id == room.Id);

        // Same for an archived Power Point and POST /api/devices.
        var activeRoomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Utility room" }, TestContext.Current.CancellationToken);
        var activeRoom = await activeRoomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        var powerPointResponse = await client.PostAsJsonAsync("/api/power-points", new { roomId = activeRoom!.Id, name = "Wall outlet" }, TestContext.Current.CancellationToken);
        var powerPoint = await powerPointResponse.Content.ReadFromJsonAsync<PowerPointResponse>(TestContext.Current.CancellationToken);
        await client.DeleteAsync($"/api/power-points/{powerPoint!.Id}", TestContext.Current.CancellationToken);

        var deviceUnderArchivedPowerPoint = await client.PostAsJsonAsync("/api/devices", new { powerPointId = powerPoint.Id, name = "Fridge" }, TestContext.Current.CancellationToken);
        deviceUnderArchivedPowerPoint.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var powerPoints = await client.GetFromJsonAsync<List<PowerPointResponse>>("/api/power-points", TestContext.Current.CancellationToken);
        powerPoints!.ShouldContain(p => p.Id == powerPoint.Id);
    }

    [Fact]
    public async Task A_principal_with_no_Household_gets_403_from_every_route()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var someId = Guid.NewGuid();

        (await client.PostAsJsonAsync("/api/rooms", new { name = "Kitchen" }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/rooms", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync($"/api/rooms/{someId}", new { name = "Kitchen" }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.DeleteAsync($"/api/rooms/{someId}", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await client.PostAsJsonAsync("/api/power-points", new { roomId = someId, name = "Outlet" }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/power-points", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync($"/api/power-points/{someId}", new { name = "Outlet" }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.DeleteAsync($"/api/power-points/{someId}", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await client.PostAsJsonAsync("/api/devices", new { powerPointId = someId, name = "Kettle" }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/devices", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync($"/api/devices/{someId}", new { name = "Kettle" }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.DeleteAsync($"/api/devices/{someId}", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
