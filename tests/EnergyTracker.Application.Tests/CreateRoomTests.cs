using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class CreateRoomTests
{
    private readonly ITaggingScaffoldRepository _repository = Substitute.For<ITaggingScaffoldRepository>();

    public CreateRoomTests()
    {
        _repository.ListRoomsAsync(Arg.Any<CancellationToken>()).Returns(new List<Room>());
    }

    [Fact]
    public async Task Creates_and_persists_an_active_room_scoped_to_the_household()
    {
        var householdId = Guid.NewGuid();
        var sut = new CreateRoom(_repository);

        var before = DateTimeOffset.UtcNow;
        var room = await sut.ExecuteAsync(householdId, "Kitchen", TestContext.Current.CancellationToken);
        var after = DateTimeOffset.UtcNow;

        room.HouseholdId.ShouldBe(householdId);
        room.Name.ShouldBe("Kitchen");
        room.ArchivedAt.ShouldBeNull();
        room.CreatedAtUtc.ShouldBeInRange(before, after);

        await _repository.Received(1).AddRoomAsync(
            Arg.Is<Room>(r => r.Id == room.Id && r.HouseholdId == householdId && r.Name == "Kitchen"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Trims_the_name()
    {
        var sut = new CreateRoom(_repository);

        var room = await sut.ExecuteAsync(Guid.NewGuid(), "  Kitchen  ", TestContext.Current.CancellationToken);

        room.Name.ShouldBe("Kitchen");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_a_blank_name(string name)
    {
        var sut = new CreateRoom(_repository);

        await Should.ThrowAsync<TaggingScaffoldValidationException>(
            () => sut.ExecuteAsync(Guid.NewGuid(), name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_name_over_200_characters()
    {
        var sut = new CreateRoom(_repository);
        var name = new string('a', 201);

        await Should.ThrowAsync<TaggingScaffoldValidationException>(
            () => sut.ExecuteAsync(Guid.NewGuid(), name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_duplicate_name_within_the_household()
    {
        var householdId = Guid.NewGuid();
        _repository.ListRoomsAsync(Arg.Any<CancellationToken>()).Returns(new List<Room>
        {
            new() { Id = Guid.NewGuid(), HouseholdId = householdId, Name = "Kitchen", CreatedAtUtc = DateTimeOffset.UtcNow, ArchivedAt = null },
        });
        var sut = new CreateRoom(_repository);

        await Should.ThrowAsync<TaggingScaffoldValidationException>(
            () => sut.ExecuteAsync(householdId, "Kitchen", TestContext.Current.CancellationToken));
    }
}
