using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class MoveDeviceTests
{
    private readonly ITaggingScaffoldRepository _repository = Substitute.For<ITaggingScaffoldRepository>();
    private readonly Guid _householdId = Guid.NewGuid();

    private PowerPoint MakePowerPoint(DateTimeOffset? archivedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = _householdId,
        RoomId = Guid.NewGuid(),
        Name = "Counter outlet",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        ArchivedAt = archivedAt,
    };

    private Device MakeDevice(Guid powerPointId, string name = "Toaster", DateTimeOffset? archivedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = _householdId,
        PowerPointId = powerPointId,
        Name = name,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        ArchivedAt = archivedAt,
    };

    public MoveDeviceTests()
    {
        _repository.ListDevicesAsync(Arg.Any<CancellationToken>()).Returns(new List<Device>());
    }

    [Fact]
    public async Task Reassigns_the_device_to_the_new_power_point()
    {
        var oldPowerPoint = MakePowerPoint();
        var newPowerPoint = MakePowerPoint();
        var device = MakeDevice(oldPowerPoint.Id);
        _repository.FindDeviceAsync(device.Id, Arg.Any<CancellationToken>()).Returns(device);
        _repository.FindPowerPointAsync(newPowerPoint.Id, Arg.Any<CancellationToken>()).Returns(newPowerPoint);
        var sut = new MoveDevice(_repository);

        var result = await sut.ExecuteAsync(device.Id, newPowerPoint.Id, TestContext.Current.CancellationToken);

        result.PowerPointId.ShouldBe(newPowerPoint.Id);
        await _repository.Received(1).UpdateDeviceAsync(Arg.Is<Device>(d => d.Id == device.Id && d.PowerPointId == newPowerPoint.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Throws_not_found_when_the_source_device_does_not_exist()
    {
        var deviceId = Guid.NewGuid();
        _repository.FindDeviceAsync(deviceId, Arg.Any<CancellationToken>()).Returns((Device?)null);
        var sut = new MoveDevice(_repository);

        await Should.ThrowAsync<TaggingScaffoldNotFoundException>(
            () => sut.ExecuteAsync(deviceId, Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Throws_not_found_when_the_destination_power_point_does_not_exist()
    {
        var oldPowerPoint = MakePowerPoint();
        var device = MakeDevice(oldPowerPoint.Id);
        var newPowerPointId = Guid.NewGuid();
        _repository.FindDeviceAsync(device.Id, Arg.Any<CancellationToken>()).Returns(device);
        _repository.FindPowerPointAsync(newPowerPointId, Arg.Any<CancellationToken>()).Returns((PowerPoint?)null);
        var sut = new MoveDevice(_repository);

        await Should.ThrowAsync<TaggingScaffoldNotFoundException>(
            () => sut.ExecuteAsync(device.Id, newPowerPointId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Throws_parent_archived_when_the_destination_power_point_is_archived()
    {
        var oldPowerPoint = MakePowerPoint();
        var newPowerPoint = MakePowerPoint(archivedAt: DateTimeOffset.UtcNow);
        var device = MakeDevice(oldPowerPoint.Id);
        _repository.FindDeviceAsync(device.Id, Arg.Any<CancellationToken>()).Returns(device);
        _repository.FindPowerPointAsync(newPowerPoint.Id, Arg.Any<CancellationToken>()).Returns(newPowerPoint);
        var sut = new MoveDevice(_repository);

        await Should.ThrowAsync<TaggingScaffoldParentArchivedException>(
            () => sut.ExecuteAsync(device.Id, newPowerPoint.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_duplicate_name_already_existing_at_the_destination_power_point()
    {
        var oldPowerPoint = MakePowerPoint();
        var newPowerPoint = MakePowerPoint();
        var device = MakeDevice(oldPowerPoint.Id, name: "Toaster");
        _repository.FindDeviceAsync(device.Id, Arg.Any<CancellationToken>()).Returns(device);
        _repository.FindPowerPointAsync(newPowerPoint.Id, Arg.Any<CancellationToken>()).Returns(newPowerPoint);
        _repository.ListDevicesAsync(Arg.Any<CancellationToken>()).Returns(new List<Device>
        {
            new() { Id = Guid.NewGuid(), HouseholdId = _householdId, PowerPointId = newPowerPoint.Id, Name = "Toaster", CreatedAtUtc = DateTimeOffset.UtcNow, ArchivedAt = null },
        });
        var sut = new MoveDevice(_repository);

        await Should.ThrowAsync<TaggingScaffoldValidationException>(
            () => sut.ExecuteAsync(device.Id, newPowerPoint.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Allows_moving_an_archived_device()
    {
        var oldPowerPoint = MakePowerPoint();
        var newPowerPoint = MakePowerPoint();
        var device = MakeDevice(oldPowerPoint.Id, archivedAt: DateTimeOffset.UtcNow);
        _repository.FindDeviceAsync(device.Id, Arg.Any<CancellationToken>()).Returns(device);
        _repository.FindPowerPointAsync(newPowerPoint.Id, Arg.Any<CancellationToken>()).Returns(newPowerPoint);
        var sut = new MoveDevice(_repository);

        var result = await sut.ExecuteAsync(device.Id, newPowerPoint.Id, TestContext.Current.CancellationToken);

        result.PowerPointId.ShouldBe(newPowerPoint.Id);
    }

    [Fact]
    public async Task Moving_to_the_current_power_point_is_a_harmless_no_op()
    {
        var powerPoint = MakePowerPoint();
        var device = MakeDevice(powerPoint.Id);
        _repository.FindDeviceAsync(device.Id, Arg.Any<CancellationToken>()).Returns(device);
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        var sut = new MoveDevice(_repository);

        var result = await sut.ExecuteAsync(device.Id, powerPoint.Id, TestContext.Current.CancellationToken);

        result.PowerPointId.ShouldBe(powerPoint.Id);
        await _repository.Received(1).UpdateDeviceAsync(Arg.Any<Device>(), Arg.Any<CancellationToken>());
    }
}
