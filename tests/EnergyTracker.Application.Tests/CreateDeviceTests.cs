using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class CreateDeviceTests
{
    private readonly ITaggingScaffoldRepository _repository = Substitute.For<ITaggingScaffoldRepository>();

    private static PowerPoint MakePowerPoint(DateTimeOffset? archivedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = Guid.NewGuid(),
        RoomId = Guid.NewGuid(),
        Name = "Counter outlet",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        ArchivedAt = archivedAt,
    };

    public CreateDeviceTests()
    {
        _repository.ListDevicesAsync(Arg.Any<CancellationToken>()).Returns(new List<Device>());
    }

    [Fact]
    public async Task Creates_and_persists_an_active_device_on_the_power_point()
    {
        var powerPoint = MakePowerPoint();
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        var sut = new CreateDevice(_repository);

        var device = await sut.ExecuteAsync(powerPoint.HouseholdId, powerPoint.Id, "Kettle", TestContext.Current.CancellationToken);

        device.HouseholdId.ShouldBe(powerPoint.HouseholdId);
        device.PowerPointId.ShouldBe(powerPoint.Id);
        device.Name.ShouldBe("Kettle");
        device.ArchivedAt.ShouldBeNull();
        await _repository.Received(1).AddDeviceAsync(Arg.Is<Device>(d => d.Id == device.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Throws_not_found_for_a_nonexistent_power_point()
    {
        var powerPointId = Guid.NewGuid();
        _repository.FindPowerPointAsync(powerPointId, Arg.Any<CancellationToken>()).Returns((PowerPoint?)null);
        var sut = new CreateDevice(_repository);

        await Should.ThrowAsync<TaggingScaffoldNotFoundException>(
            () => sut.ExecuteAsync(Guid.NewGuid(), powerPointId, "Kettle", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_blank_name()
    {
        var powerPoint = MakePowerPoint();
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        var sut = new CreateDevice(_repository);

        await Should.ThrowAsync<TaggingScaffoldValidationException>(
            () => sut.ExecuteAsync(powerPoint.HouseholdId, powerPoint.Id, "   ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Throws_parent_archived_when_the_power_point_is_archived()
    {
        var powerPoint = MakePowerPoint(archivedAt: DateTimeOffset.UtcNow);
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        var sut = new CreateDevice(_repository);

        await Should.ThrowAsync<TaggingScaffoldParentArchivedException>(
            () => sut.ExecuteAsync(powerPoint.HouseholdId, powerPoint.Id, "Kettle", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_duplicate_name_on_the_power_point()
    {
        var powerPoint = MakePowerPoint();
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        _repository.ListDevicesAsync(Arg.Any<CancellationToken>()).Returns(new List<Device>
        {
            new() { Id = Guid.NewGuid(), HouseholdId = powerPoint.HouseholdId, PowerPointId = powerPoint.Id, Name = "Kettle", CreatedAtUtc = DateTimeOffset.UtcNow, ArchivedAt = null },
        });
        var sut = new CreateDevice(_repository);

        await Should.ThrowAsync<TaggingScaffoldValidationException>(
            () => sut.ExecuteAsync(powerPoint.HouseholdId, powerPoint.Id, "Kettle", TestContext.Current.CancellationToken));
    }
}
