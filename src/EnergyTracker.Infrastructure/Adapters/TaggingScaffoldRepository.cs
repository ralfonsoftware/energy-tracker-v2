using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

public class TaggingScaffoldRepository(EnergyTrackerDbContext dbContext) : ITaggingScaffoldRepository
{
    public Task<Room?> FindRoomAsync(Guid roomId, CancellationToken cancellationToken) =>
        dbContext.Rooms.SingleOrDefaultAsync(r => r.Id == roomId, cancellationToken);

    public async Task<IReadOnlyList<Room>> ListRoomsAsync(CancellationToken cancellationToken) =>
        await dbContext.Rooms.ToListAsync(cancellationToken);

    public async Task AddRoomAsync(Room room, CancellationToken cancellationToken)
    {
        await dbContext.Rooms.AddAsync(room, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateRoomAsync(Room room, CancellationToken cancellationToken) =>
        dbContext.SaveChangesAsync(cancellationToken);

    public Task<PowerPoint?> FindPowerPointAsync(Guid powerPointId, CancellationToken cancellationToken) =>
        dbContext.PowerPoints.SingleOrDefaultAsync(p => p.Id == powerPointId, cancellationToken);

    public async Task<IReadOnlyList<PowerPoint>> ListPowerPointsAsync(CancellationToken cancellationToken) =>
        await dbContext.PowerPoints.ToListAsync(cancellationToken);

    public async Task AddPowerPointAsync(PowerPoint powerPoint, CancellationToken cancellationToken)
    {
        await dbContext.PowerPoints.AddAsync(powerPoint, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task UpdatePowerPointAsync(PowerPoint powerPoint, CancellationToken cancellationToken) =>
        dbContext.SaveChangesAsync(cancellationToken);

    public Task<Device?> FindDeviceAsync(Guid deviceId, CancellationToken cancellationToken) =>
        dbContext.Devices.SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);

    public async Task<IReadOnlyList<Device>> ListDevicesAsync(CancellationToken cancellationToken) =>
        await dbContext.Devices.ToListAsync(cancellationToken);

    public async Task AddDeviceAsync(Device device, CancellationToken cancellationToken)
    {
        await dbContext.Devices.AddAsync(device, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateDeviceAsync(Device device, CancellationToken cancellationToken) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
