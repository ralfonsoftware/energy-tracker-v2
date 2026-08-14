using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class ArchiveDeviceTests
{
    private readonly ITaggingScaffoldRepository _repository = Substitute.For<ITaggingScaffoldRepository>();

    private static Device MakeDevice(DateTimeOffset? archivedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = Guid.NewGuid(),
        PowerPointId = Guid.NewGuid(),
        Name = "Kettle",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        ArchivedAt = archivedAt,
    };

    [Fact]
    public async Task Archives_an_active_device()
    {
        var device = MakeDevice();
        _repository.FindDeviceAsync(device.Id, Arg.Any<CancellationToken>()).Returns(device);
        var sut = new ArchiveDevice(_repository);

        var result = await sut.ExecuteAsync(device.Id, TestContext.Current.CancellationToken);

        result.ArchivedAt.ShouldNotBeNull();
        await _repository.Received(1).UpdateDeviceAsync(device, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Archiving_an_already_archived_device_is_an_idempotent_no_op()
    {
        var archivedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var device = MakeDevice(archivedAt);
        _repository.FindDeviceAsync(device.Id, Arg.Any<CancellationToken>()).Returns(device);
        var sut = new ArchiveDevice(_repository);

        var result = await sut.ExecuteAsync(device.Id, TestContext.Current.CancellationToken);

        result.ArchivedAt.ShouldBe(archivedAt);
        await _repository.DidNotReceive().UpdateDeviceAsync(Arg.Any<Device>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Throws_not_found_for_a_nonexistent_device()
    {
        var deviceId = Guid.NewGuid();
        _repository.FindDeviceAsync(deviceId, Arg.Any<CancellationToken>()).Returns((Device?)null);
        var sut = new ArchiveDevice(_repository);

        await Should.ThrowAsync<TaggingScaffoldNotFoundException>(
            () => sut.ExecuteAsync(deviceId, TestContext.Current.CancellationToken));
    }
}
