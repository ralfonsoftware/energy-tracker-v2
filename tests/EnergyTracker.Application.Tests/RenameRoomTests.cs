using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class RenameRoomTests
{
    private readonly ITaggingScaffoldRepository _repository = Substitute.For<ITaggingScaffoldRepository>();

    private static Room MakeRoom(DateTimeOffset? archivedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = Guid.NewGuid(),
        Name = "Old name",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        ArchivedAt = archivedAt,
    };

    public RenameRoomTests()
    {
        _repository.ListRoomsAsync(Arg.Any<CancellationToken>()).Returns(new List<Room>());
    }

    [Fact]
    public async Task Renames_and_persists_an_existing_room()
    {
        var room = MakeRoom();
        _repository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        var sut = new RenameRoom(_repository);

        var result = await sut.ExecuteAsync(room.Id, "New name", TestContext.Current.CancellationToken);

        result.Name.ShouldBe("New name");
        await _repository.Received(1).UpdateRoomAsync(room, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Renaming_an_archived_room_is_allowed()
    {
        var room = MakeRoom(archivedAt: DateTimeOffset.UtcNow);
        _repository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        var sut = new RenameRoom(_repository);

        var result = await sut.ExecuteAsync(room.Id, "New name", TestContext.Current.CancellationToken);

        result.Name.ShouldBe("New name");
        result.ArchivedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Throws_not_found_for_a_nonexistent_room()
    {
        var roomId = Guid.NewGuid();
        _repository.FindRoomAsync(roomId, Arg.Any<CancellationToken>()).Returns((Room?)null);
        var sut = new RenameRoom(_repository);

        await Should.ThrowAsync<TaggingScaffoldNotFoundException>(
            () => sut.ExecuteAsync(roomId, "New name", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_blank_name()
    {
        var room = MakeRoom();
        _repository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        var sut = new RenameRoom(_repository);

        await Should.ThrowAsync<TaggingScaffoldValidationException>(
            () => sut.ExecuteAsync(room.Id, "   ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_duplicate_name_within_the_household()
    {
        var room = MakeRoom();
        _repository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        _repository.ListRoomsAsync(Arg.Any<CancellationToken>()).Returns(new List<Room>
        {
            room,
            new() { Id = Guid.NewGuid(), HouseholdId = room.HouseholdId, Name = "Living room", CreatedAtUtc = DateTimeOffset.UtcNow, ArchivedAt = null },
        });
        var sut = new RenameRoom(_repository);

        await Should.ThrowAsync<TaggingScaffoldValidationException>(
            () => sut.ExecuteAsync(room.Id, "Living room", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Allows_renaming_to_its_own_current_name()
    {
        var room = MakeRoom();
        _repository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        _repository.ListRoomsAsync(Arg.Any<CancellationToken>()).Returns(new List<Room> { room });
        var sut = new RenameRoom(_repository);

        var result = await sut.ExecuteAsync(room.Id, room.Name, TestContext.Current.CancellationToken);

        result.Name.ShouldBe(room.Name);
    }
}
