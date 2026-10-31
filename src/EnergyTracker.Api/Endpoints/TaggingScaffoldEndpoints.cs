namespace EnergyTracker.Api.Endpoints;

public static class TaggingScaffoldEndpoints
{
    public static RouteGroupBuilder MapTaggingScaffoldEndpoints(this RouteGroupBuilder api)
    {
        var household = api.MapGroup(string.Empty).RequireHousehold();
        RoomEndpoints.MapRoomEndpoints(household);
        PowerPointEndpoints.MapPowerPointEndpoints(household);
        DeviceEndpoints.MapDeviceEndpoints(household);

        return api;
    }
}
