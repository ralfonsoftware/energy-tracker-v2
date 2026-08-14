using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class ArchiveRoomTests
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

    [Fact]
    public async Task Archives_an_active_room()
    {
        var room = MakeRoom();
        _repository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        var sut = new ArchiveRoom(_repository);

        var before = DateTimeOffset.UtcNow;
        var result = await sut.ExecuteAsync(room.Id, TestContext.Current.CancellationToken);
        var after = DateTimeOffset.UtcNow;

        result.ArchivedAt.ShouldNotBeNull();
        result.ArchivedAt!.Value.ShouldBeInRange(before, after);
        await _repository.Received(1).UpdateRoomAsync(room, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Archiving_an_already_archived_room_is_an_idempotent_no_op()
    {
        var archivedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var room = MakeRoom(archivedAt);
        _repository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        var sut = new ArchiveRoom(_repository);

        var result = await sut.ExecuteAsync(room.Id, TestContext.Current.CancellationToken);

        result.ArchivedAt.ShouldBe(archivedAt);
        await _repository.DidNotReceive().UpdateRoomAsync(Arg.Any<Room>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Throws_not_found_for_a_nonexistent_room()
    {
        var roomId = Guid.NewGuid();
        _repository.FindRoomAsync(roomId, Arg.Any<CancellationToken>()).Returns((Room?)null);
        var sut = new ArchiveRoom(_repository);

        await Should.ThrowAsync<TaggingScaffoldNotFoundException>(
            () => sut.ExecuteAsync(roomId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Archiving_a_room_does_not_archive_its_power_points()
    {
        var room = MakeRoom();
        _repository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        var sut = new ArchiveRoom(_repository);

        await sut.ExecuteAsync(room.Id, TestContext.Current.CancellationToken);

        await _repository.DidNotReceive().UpdatePowerPointAsync(Arg.Any<PowerPoint>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().FindPowerPointAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().ListPowerPointsAsync(Arg.Any<CancellationToken>());
    }
}
