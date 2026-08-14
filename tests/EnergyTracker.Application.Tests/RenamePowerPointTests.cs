using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class RenamePowerPointTests
{
    private readonly ITaggingScaffoldRepository _repository = Substitute.For<ITaggingScaffoldRepository>();

    private static PowerPoint MakePowerPoint(DateTimeOffset? archivedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = Guid.NewGuid(),
        RoomId = Guid.NewGuid(),
        Name = "Old name",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        ArchivedAt = archivedAt,
    };

    public RenamePowerPointTests()
    {
        _repository.ListPowerPointsAsync(Arg.Any<CancellationToken>()).Returns(new List<PowerPoint>());
    }

    [Fact]
    public async Task Renames_and_persists_an_existing_power_point()
    {
        var powerPoint = MakePowerPoint();
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        var sut = new RenamePowerPoint(_repository);

        var result = await sut.ExecuteAsync(powerPoint.Id, "New name", TestContext.Current.CancellationToken);

        result.Name.ShouldBe("New name");
        await _repository.Received(1).UpdatePowerPointAsync(powerPoint, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Renaming_an_archived_power_point_is_allowed()
    {
        var powerPoint = MakePowerPoint(archivedAt: DateTimeOffset.UtcNow);
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        var sut = new RenamePowerPoint(_repository);

        var result = await sut.ExecuteAsync(powerPoint.Id, "New name", TestContext.Current.CancellationToken);

        result.Name.ShouldBe("New name");
        result.ArchivedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Rejects_a_duplicate_name_within_the_room()
    {
        var powerPoint = MakePowerPoint();
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        _repository.ListPowerPointsAsync(Arg.Any<CancellationToken>()).Returns(new List<PowerPoint>
        {
            powerPoint,
            new() { Id = Guid.NewGuid(), HouseholdId = powerPoint.HouseholdId, RoomId = powerPoint.RoomId, Name = "Fridge outlet", CreatedAtUtc = DateTimeOffset.UtcNow, ArchivedAt = null },
        });
        var sut = new RenamePowerPoint(_repository);

        await Should.ThrowAsync<TaggingScaffoldValidationException>(
            () => sut.ExecuteAsync(powerPoint.Id, "Fridge outlet", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Throws_not_found_for_a_nonexistent_power_point()
    {
        var powerPointId = Guid.NewGuid();
        _repository.FindPowerPointAsync(powerPointId, Arg.Any<CancellationToken>()).Returns((PowerPoint?)null);
        var sut = new RenamePowerPoint(_repository);

        await Should.ThrowAsync<TaggingScaffoldNotFoundException>(
            () => sut.ExecuteAsync(powerPointId, "New name", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_blank_name()
    {
        var powerPoint = MakePowerPoint();
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        var sut = new RenamePowerPoint(_repository);

        await Should.ThrowAsync<TaggingScaffoldValidationException>(
            () => sut.ExecuteAsync(powerPoint.Id, "   ", TestContext.Current.CancellationToken));
    }
}
