using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class CreatePowerPointTests
{
    private readonly ITaggingScaffoldRepository _repository = Substitute.For<ITaggingScaffoldRepository>();

    private static Room MakeRoom(DateTimeOffset? archivedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = Guid.NewGuid(),
        Name = "Kitchen",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        ArchivedAt = archivedAt,
    };

    public CreatePowerPointTests()
    {
        _repository.ListPowerPointsAsync(Arg.Any<CancellationToken>()).Returns(new List<PowerPoint>());
    }

    [Fact]
    public async Task Creates_and_persists_an_active_power_point_under_the_room()
    {
        var room = MakeRoom();
        _repository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        var sut = new CreatePowerPoint(_repository);

        var powerPoint = await sut.ExecuteAsync(room.HouseholdId, room.Id, "Counter outlet", TestContext.Current.CancellationToken);

        powerPoint.HouseholdId.ShouldBe(room.HouseholdId);
        powerPoint.RoomId.ShouldBe(room.Id);
        powerPoint.Name.ShouldBe("Counter outlet");
        powerPoint.ArchivedAt.ShouldBeNull();
        await _repository.Received(1).AddPowerPointAsync(Arg.Is<PowerPoint>(p => p.Id == powerPoint.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Throws_not_found_for_a_nonexistent_room()
    {
        var roomId = Guid.NewGuid();
        _repository.FindRoomAsync(roomId, Arg.Any<CancellationToken>()).Returns((Room?)null);
        var sut = new CreatePowerPoint(_repository);

        await Should.ThrowAsync<TaggingScaffoldNotFoundException>(
            () => sut.ExecuteAsync(Guid.NewGuid(), roomId, "Counter outlet", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_blank_name()
    {
        var room = MakeRoom();
        _repository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        var sut = new CreatePowerPoint(_repository);

        await Should.ThrowAsync<TaggingScaffoldValidationException>(
            () => sut.ExecuteAsync(room.HouseholdId, room.Id, "   ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Throws_parent_archived_when_the_room_is_archived()
    {
        var room = MakeRoom(archivedAt: DateTimeOffset.UtcNow);
        _repository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        var sut = new CreatePowerPoint(_repository);

        await Should.ThrowAsync<TaggingScaffoldParentArchivedException>(
            () => sut.ExecuteAsync(room.HouseholdId, room.Id, "Counter outlet", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_duplicate_name_within_the_room()
    {
        var room = MakeRoom();
        _repository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        _repository.ListPowerPointsAsync(Arg.Any<CancellationToken>()).Returns(new List<PowerPoint>
        {
            new() { Id = Guid.NewGuid(), HouseholdId = room.HouseholdId, RoomId = room.Id, Name = "Counter outlet", CreatedAtUtc = DateTimeOffset.UtcNow, ArchivedAt = null },
        });
        var sut = new CreatePowerPoint(_repository);

        await Should.ThrowAsync<TaggingScaffoldValidationException>(
            () => sut.ExecuteAsync(room.HouseholdId, room.Id, "Counter outlet", TestContext.Current.CancellationToken));
    }
}
