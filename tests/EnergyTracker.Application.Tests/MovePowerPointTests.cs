using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class MovePowerPointTests
{
    private readonly ITaggingScaffoldRepository _repository = Substitute.For<ITaggingScaffoldRepository>();
    private readonly Guid _householdId = Guid.NewGuid();

    private Room MakeRoom(DateTimeOffset? archivedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = _householdId,
        Name = "Kitchen",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        ArchivedAt = archivedAt,
    };

    private PowerPoint MakePowerPoint(Guid roomId, string name = "Counter outlet", DateTimeOffset? archivedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = _householdId,
        RoomId = roomId,
        Name = name,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        ArchivedAt = archivedAt,
    };

    public MovePowerPointTests()
    {
        _repository.ListPowerPointsAsync(Arg.Any<CancellationToken>()).Returns(new List<PowerPoint>());
    }

    [Fact]
    public async Task Reassigns_the_power_point_to_the_new_room()
    {
        var oldRoom = MakeRoom();
        var newRoom = MakeRoom();
        var powerPoint = MakePowerPoint(oldRoom.Id);
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        _repository.FindRoomAsync(newRoom.Id, Arg.Any<CancellationToken>()).Returns(newRoom);
        var sut = new MovePowerPoint(_repository);

        var result = await sut.ExecuteAsync(powerPoint.Id, newRoom.Id, TestContext.Current.CancellationToken);

        result.RoomId.ShouldBe(newRoom.Id);
        await _repository.Received(1).UpdatePowerPointAsync(Arg.Is<PowerPoint>(p => p.Id == powerPoint.Id && p.RoomId == newRoom.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Throws_not_found_when_the_source_power_point_does_not_exist()
    {
        var powerPointId = Guid.NewGuid();
        _repository.FindPowerPointAsync(powerPointId, Arg.Any<CancellationToken>()).Returns((PowerPoint?)null);
        var sut = new MovePowerPoint(_repository);

        await Should.ThrowAsync<TaggingScaffoldNotFoundException>(
            () => sut.ExecuteAsync(powerPointId, Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Throws_not_found_when_the_destination_room_does_not_exist()
    {
        var oldRoom = MakeRoom();
        var powerPoint = MakePowerPoint(oldRoom.Id);
        var newRoomId = Guid.NewGuid();
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        _repository.FindRoomAsync(newRoomId, Arg.Any<CancellationToken>()).Returns((Room?)null);
        var sut = new MovePowerPoint(_repository);

        await Should.ThrowAsync<TaggingScaffoldNotFoundException>(
            () => sut.ExecuteAsync(powerPoint.Id, newRoomId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Throws_parent_archived_when_the_destination_room_is_archived()
    {
        var oldRoom = MakeRoom();
        var newRoom = MakeRoom(archivedAt: DateTimeOffset.UtcNow);
        var powerPoint = MakePowerPoint(oldRoom.Id);
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        _repository.FindRoomAsync(newRoom.Id, Arg.Any<CancellationToken>()).Returns(newRoom);
        var sut = new MovePowerPoint(_repository);

        await Should.ThrowAsync<TaggingScaffoldParentArchivedException>(
            () => sut.ExecuteAsync(powerPoint.Id, newRoom.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_duplicate_name_already_existing_at_the_destination_room()
    {
        var oldRoom = MakeRoom();
        var newRoom = MakeRoom();
        var powerPoint = MakePowerPoint(oldRoom.Id, name: "Counter outlet");
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        _repository.FindRoomAsync(newRoom.Id, Arg.Any<CancellationToken>()).Returns(newRoom);
        _repository.ListPowerPointsAsync(Arg.Any<CancellationToken>()).Returns(new List<PowerPoint>
        {
            new() { Id = Guid.NewGuid(), HouseholdId = _householdId, RoomId = newRoom.Id, Name = "Counter outlet", CreatedAtUtc = DateTimeOffset.UtcNow, ArchivedAt = null },
        });
        var sut = new MovePowerPoint(_repository);

        await Should.ThrowAsync<TaggingScaffoldValidationException>(
            () => sut.ExecuteAsync(powerPoint.Id, newRoom.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Allows_moving_an_archived_power_point()
    {
        var oldRoom = MakeRoom();
        var newRoom = MakeRoom();
        var powerPoint = MakePowerPoint(oldRoom.Id, archivedAt: DateTimeOffset.UtcNow);
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        _repository.FindRoomAsync(newRoom.Id, Arg.Any<CancellationToken>()).Returns(newRoom);
        var sut = new MovePowerPoint(_repository);

        var result = await sut.ExecuteAsync(powerPoint.Id, newRoom.Id, TestContext.Current.CancellationToken);

        result.RoomId.ShouldBe(newRoom.Id);
    }

    [Fact]
    public async Task Moving_to_the_current_room_is_a_harmless_no_op()
    {
        var room = MakeRoom();
        var powerPoint = MakePowerPoint(room.Id);
        _repository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        _repository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        var sut = new MovePowerPoint(_repository);

        var result = await sut.ExecuteAsync(powerPoint.Id, room.Id, TestContext.Current.CancellationToken);

        result.RoomId.ShouldBe(room.Id);
        await _repository.Received(1).UpdatePowerPointAsync(Arg.Any<PowerPoint>(), Arg.Any<CancellationToken>());
    }
}
