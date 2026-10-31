using EnergyTracker.Application;
using EnergyTracker.Application.Ports;

namespace EnergyTracker.Api.Endpoints;

public static class SmartPlugReadingEndpoints
{
    public static RouteGroupBuilder MapSmartPlugReadingEndpoints(this RouteGroupBuilder api)
    {
        var household = api.MapGroup(string.Empty).RequireHousehold();
        // Read-only display drill-down (AC #1, #2, Story 4.2): the Room -> Power Point -> Device
        // measured-data tree. "No Smart Plug data imported yet" is FR-9's normal starting state for
        // every new Household, not an error — always 200 with a (possibly empty) array, never null,
        // same discipline as GET /status/history.
        household.MapGet("/smart-plug-readings", async (
            ICurrentHouseholdAccessor householdAccessor,
            GetPerPlugMeasuredData getPerPlugMeasuredData,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

            var rooms = await getPerPlugMeasuredData.ExecuteAsync(householdId, cancellationToken);
            return Results.Ok(rooms.Select(ToResponse).ToList());
        });

        return api;
    }

    private static RoomMeasuredDataResponse ToResponse(RoomMeasuredData room) => new(
        RoomName: room.RoomName,
        TotalKwh: room.TotalKwh,
        PowerPoints: room.PowerPoints.Select(ToResponse).ToList());

    private static PowerPointMeasuredDataResponse ToResponse(PowerPointMeasuredData powerPoint) => new(
        PowerPointName: powerPoint.PowerPointName,
        TotalKwh: powerPoint.TotalKwh,
        Devices: powerPoint.Devices.Select(ToResponse).ToList());

    private static DeviceMeasuredDataResponse ToResponse(DeviceMeasuredData device) => new(
        DeviceName: device.DeviceName,
        TotalKwh: device.TotalKwh);
}

public record RoomMeasuredDataResponse(string RoomName, decimal TotalKwh, IReadOnlyList<PowerPointMeasuredDataResponse> PowerPoints);

public record PowerPointMeasuredDataResponse(string PowerPointName, decimal TotalKwh, IReadOnlyList<DeviceMeasuredDataResponse> Devices);

public record DeviceMeasuredDataResponse(string DeviceName, decimal TotalKwh);
