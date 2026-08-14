using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class RenameDeviceTests
{
    private readonly ITaggingScaffoldRepository _repository = Substitute.For<ITaggingScaffoldRepository>();

    private static Device MakeDevice(DateTimeOffset? archivedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = Guid.NewGuid(),
        PowerPointId = Guid.NewGuid(),
        Name = "Old name",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        ArchivedAt = archivedAt,
    };

    public RenameDeviceTests()
    {
        _repository.ListDevicesAsync(Arg.Any<CancellationToken>()).Returns(new List<Device>());
    }

    [Fact]
    public async Task Renames_and_persists_an_existing_device()
    {
        var device = MakeDevice();
        _repository.FindDeviceAsync(device.Id, Arg.Any<CancellationToken>()).Returns(device);
        var sut = new RenameDevice(_repository);

        var result = await sut.ExecuteAsync(device.Id, "New name", TestContext.Current.CancellationToken);

        result.Name.ShouldBe("New name");
        await _repository.Received(1).UpdateDeviceAsync(device, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Renaming_an_archived_device_is_allowed()
    {
        var device = MakeDevice(archivedAt: DateTimeOffset.UtcNow);
        _repository.FindDeviceAsync(device.Id, Arg.Any<CancellationToken>()).Returns(device);
        var sut = new RenameDevice(_repository);

        var result = await sut.ExecuteAsync(device.Id, "New name", TestContext.Current.CancellationToken);

        result.Name.ShouldBe("New name");
        result.ArchivedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Rejects_a_duplicate_name_on_the_power_point()
    {
        var device = MakeDevice();
        _repository.FindDeviceAsync(device.Id, Arg.Any<CancellationToken>()).Returns(device);
        _repository.ListDevicesAsync(Arg.Any<CancellationToken>()).Returns(new List<Device>
        {
            device,
            new() { Id = Guid.NewGuid(), HouseholdId = device.HouseholdId, PowerPointId = device.PowerPointId, Name = "Toaster", CreatedAtUtc = DateTimeOffset.UtcNow, ArchivedAt = null },
        });
        var sut = new RenameDevice(_repository);

        await Should.ThrowAsync<TaggingScaffoldValidationException>(
            () => sut.ExecuteAsync(device.Id, "Toaster", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Throws_not_found_for_a_nonexistent_device()
    {
        var deviceId = Guid.NewGuid();
        _repository.FindDeviceAsync(deviceId, Arg.Any<CancellationToken>()).Returns((Device?)null);
        var sut = new RenameDevice(_repository);

        await Should.ThrowAsync<TaggingScaffoldNotFoundException>(
            () => sut.ExecuteAsync(deviceId, "New name", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_blank_name()
    {
        var device = MakeDevice();
        _repository.FindDeviceAsync(device.Id, Arg.Any<CancellationToken>()).Returns(device);
        var sut = new RenameDevice(_repository);

        await Should.ThrowAsync<TaggingScaffoldValidationException>(
            () => sut.ExecuteAsync(device.Id, "   ", TestContext.Current.CancellationToken));
    }
}
