using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class ArchivePowerPointTests
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

    [Fact]
    public async Task Archives_an_active_power_point()
    {
        var powerPoint = MakePowerPoint();
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        var sut = new ArchivePowerPoint(_repository);

        var result = await sut.ExecuteAsync(powerPoint.Id, TestContext.Current.CancellationToken);

        result.ArchivedAt.ShouldNotBeNull();
        await _repository.Received(1).UpdatePowerPointAsync(powerPoint, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Archiving_an_already_archived_power_point_is_an_idempotent_no_op()
    {
        var archivedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var powerPoint = MakePowerPoint(archivedAt);
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        var sut = new ArchivePowerPoint(_repository);

        var result = await sut.ExecuteAsync(powerPoint.Id, TestContext.Current.CancellationToken);

        result.ArchivedAt.ShouldBe(archivedAt);
        await _repository.DidNotReceive().UpdatePowerPointAsync(Arg.Any<PowerPoint>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Throws_not_found_for_a_nonexistent_power_point()
    {
        var powerPointId = Guid.NewGuid();
        _repository.FindPowerPointAsync(powerPointId, Arg.Any<CancellationToken>()).Returns((PowerPoint?)null);
        var sut = new ArchivePowerPoint(_repository);

        await Should.ThrowAsync<TaggingScaffoldNotFoundException>(
            () => sut.ExecuteAsync(powerPointId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Archiving_a_power_point_does_not_archive_its_devices()
    {
        var powerPoint = MakePowerPoint();
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        var sut = new ArchivePowerPoint(_repository);

        await sut.ExecuteAsync(powerPoint.Id, TestContext.Current.CancellationToken);

        await _repository.DidNotReceive().UpdateDeviceAsync(Arg.Any<Device>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().FindDeviceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().ListDevicesAsync(Arg.Any<CancellationToken>());
    }
}
