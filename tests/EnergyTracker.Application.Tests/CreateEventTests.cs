using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class CreateEventTests
{
    private readonly IEventRepository _repository = Substitute.For<IEventRepository>();
    private readonly ITaggingScaffoldRepository _taggingScaffoldRepository = Substitute.For<ITaggingScaffoldRepository>();
    private readonly IBackgroundJobQueue _jobQueue = Substitute.For<IBackgroundJobQueue>();

    private CreateEvent Sut() => new(_repository, _taggingScaffoldRepository, _jobQueue);

    public CreateEventTests()
    {
        _repository.AddAsync(Arg.Any<Event>(), Arg.Any<CancellationToken>()).Returns(callInfo => callInfo.Arg<Event>());
    }

    // ArchiveRoom does not cascade, so CreateEvent checks a PowerPoint's/Device's ancestors too —
    // these helpers stub a live hierarchy so the tests below isolate the behavior they name.
    private Guid StubLiveRoom()
    {
        var roomId = Guid.NewGuid();
        _taggingScaffoldRepository.FindRoomAsync(roomId, Arg.Any<CancellationToken>())
            .Returns(new Room { Id = roomId, HouseholdId = Guid.NewGuid(), Name = "Kitchen", CreatedAtUtc = DateTimeOffset.UtcNow, ArchivedAt = null });
        return roomId;
    }

    private Guid StubLivePowerPoint(Guid roomId, string name = "Counter outlet")
    {
        var powerPointId = Guid.NewGuid();
        _taggingScaffoldRepository.FindPowerPointAsync(powerPointId, Arg.Any<CancellationToken>())
            .Returns(new PowerPoint { Id = powerPointId, HouseholdId = Guid.NewGuid(), RoomId = roomId, Name = name, CreatedAtUtc = DateTimeOffset.UtcNow, ArchivedAt = null });
        return powerPointId;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_a_blank_Description(string description)
    {
        var sut = Sut();

        await Should.ThrowAsync<EventValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), description, DateTimeOffset.UtcNow, null, null, TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().AddAsync(Arg.Any<Event>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_a_Description_over_the_500_character_cap()
    {
        var sut = Sut();
        var tooLong = new string('a', 501);

        await Should.ThrowAsync<EventValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), tooLong, DateTimeOffset.UtcNow, null, null, TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().AddAsync(Arg.Any<Event>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_backfilled_past_timestamp_is_accepted()
    {
        var householdId = Guid.NewGuid();
        var sut = Sut();
        var occurredAt = DateTimeOffset.UtcNow.AddDays(-14);

        var result = await sut.ExecuteAsync(householdId, "away 2 weeks", occurredAt, null, null, TestContext.Current.CancellationToken);

        result.OccurredAt.ShouldBe(occurredAt);
        result.HouseholdId.ShouldBe(householdId);
    }

    [Fact]
    public async Task Rejects_a_timestamp_more_than_the_clock_skew_allowance_in_the_future()
    {
        var sut = Sut();

        await Should.ThrowAsync<EventValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), "cooked 2h", DateTimeOffset.UtcNow.AddHours(1), null, null, TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().AddAsync(Arg.Any<Event>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_a_timestamp_unreasonably_far_in_the_past()
    {
        var sut = Sut();

        await Should.ThrowAsync<EventValidationException>(() =>
            sut.ExecuteAsync(
                Guid.NewGuid(), "cooked 2h", new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero), null, null, TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().AddAsync(Arg.Any<Event>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tagging_an_archived_Room_throws_EventTaggedEntityArchivedException()
    {
        var roomId = Guid.NewGuid();
        _taggingScaffoldRepository.FindRoomAsync(roomId, Arg.Any<CancellationToken>())
            .Returns(new Room { Id = roomId, HouseholdId = Guid.NewGuid(), Name = "Kitchen", CreatedAtUtc = DateTimeOffset.UtcNow, ArchivedAt = DateTimeOffset.UtcNow });
        var sut = Sut();

        await Should.ThrowAsync<EventTaggedEntityArchivedException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), "cooked 2h", DateTimeOffset.UtcNow, "Room", roomId, TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().AddAsync(Arg.Any<Event>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tagging_a_nonexistent_PowerPoint_throws_EventTaggedEntityNotFoundException()
    {
        var powerPointId = Guid.NewGuid();
        _taggingScaffoldRepository.FindPowerPointAsync(powerPointId, Arg.Any<CancellationToken>()).Returns((PowerPoint?)null);
        var sut = Sut();

        await Should.ThrowAsync<EventTaggedEntityNotFoundException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), "cooked 2h", DateTimeOffset.UtcNow, "PowerPoint", powerPointId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tagging_a_live_Device_snapshots_its_current_Name()
    {
        var powerPointId = StubLivePowerPoint(StubLiveRoom());
        var deviceId = Guid.NewGuid();
        _taggingScaffoldRepository.FindDeviceAsync(deviceId, Arg.Any<CancellationToken>())
            .Returns(new Device { Id = deviceId, HouseholdId = Guid.NewGuid(), PowerPointId = powerPointId, Name = "Induction cooktop", CreatedAtUtc = DateTimeOffset.UtcNow, ArchivedAt = null });
        var sut = Sut();

        var result = await sut.ExecuteAsync(Guid.NewGuid(), "cooked 2h", DateTimeOffset.UtcNow, "Device", deviceId, TestContext.Current.CancellationToken);

        result.TaggedEntityType.ShouldBe("Device");
        result.TaggedEntityId.ShouldBe(deviceId);
        result.TaggedEntityName.ShouldBe("Induction cooktop");
    }

    [Fact]
    public async Task Tagging_a_live_Room_snapshots_its_current_Name()
    {
        var roomId = Guid.NewGuid();
        _taggingScaffoldRepository.FindRoomAsync(roomId, Arg.Any<CancellationToken>())
            .Returns(new Room { Id = roomId, HouseholdId = Guid.NewGuid(), Name = "Kitchen", CreatedAtUtc = DateTimeOffset.UtcNow, ArchivedAt = null });
        var sut = Sut();

        var result = await sut.ExecuteAsync(Guid.NewGuid(), "cooked 2h", DateTimeOffset.UtcNow, "Room", roomId, TestContext.Current.CancellationToken);

        result.TaggedEntityName.ShouldBe("Kitchen");
    }

    [Fact]
    public async Task Tagging_a_live_PowerPoint_snapshots_its_current_Name()
    {
        var powerPointId = StubLivePowerPoint(StubLiveRoom());
        var sut = Sut();

        var result = await sut.ExecuteAsync(Guid.NewGuid(), "cooked 2h", DateTimeOffset.UtcNow, "PowerPoint", powerPointId, TestContext.Current.CancellationToken);

        result.TaggedEntityName.ShouldBe("Counter outlet");
    }

    [Fact]
    public async Task An_untagged_Event_persists_with_TaggedEntityType_Id_and_Name_all_null()
    {
        var sut = Sut();

        var result = await sut.ExecuteAsync(Guid.NewGuid(), "away 2 weeks", DateTimeOffset.UtcNow, null, null, TestContext.Current.CancellationToken);

        result.TaggedEntityType.ShouldBeNull();
        result.TaggedEntityId.ShouldBeNull();
        result.TaggedEntityName.ShouldBeNull();
    }

    [Fact]
    public async Task An_unrecognized_TaggedEntityType_throws_EventValidationException()
    {
        var sut = Sut();

        await Should.ThrowAsync<EventValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), "cooked 2h", DateTimeOffset.UtcNow, "Toaster", Guid.NewGuid(), TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().AddAsync(Arg.Any<Event>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_TaggedEntityType_without_a_matching_TaggedEntityId_throws_EventValidationException()
    {
        var sut = Sut();

        await Should.ThrowAsync<EventValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), "cooked 2h", DateTimeOffset.UtcNow, "Room", null, TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().AddAsync(Arg.Any<Event>(), Arg.Any<CancellationToken>());
    }

    // JSON binding supplies null for a non-nullable ref-type property (STJ's
    // RespectNullableAnnotations is opt-in and off), so a null Description must reach the same 400
    // path as a blank one rather than dereferencing into a 500.
    [Fact]
    public async Task Rejects_a_null_Description()
    {
        var sut = Sut();

        var exception = await Should.ThrowAsync<EventValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), null, DateTimeOffset.UtcNow, null, null, TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe("event.description_blank");
        await _repository.DidNotReceive().AddAsync(Arg.Any<Event>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tagging_a_nonexistent_Room_throws_EventTaggedEntityNotFoundException_not_Archived()
    {
        var roomId = Guid.NewGuid();
        _taggingScaffoldRepository.FindRoomAsync(roomId, Arg.Any<CancellationToken>()).Returns((Room?)null);
        var sut = Sut();

        var exception = await Should.ThrowAsync<EventTaggedEntityNotFoundException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), "cooked 2h", DateTimeOffset.UtcNow, "Room", roomId, TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe("event.tag_not_found");
        await _repository.DidNotReceive().AddAsync(Arg.Any<Event>(), Arg.Any<CancellationToken>());
    }

    // ArchiveRoom deliberately does not cascade, so a live PowerPoint can outlive its Room's
    // archive — tagging into an already-retired hierarchy is still rejected.
    [Fact]
    public async Task Tagging_a_live_PowerPoint_under_an_archived_Room_is_rejected()
    {
        var roomId = Guid.NewGuid();
        _taggingScaffoldRepository.FindRoomAsync(roomId, Arg.Any<CancellationToken>())
            .Returns(new Room { Id = roomId, HouseholdId = Guid.NewGuid(), Name = "Old Kitchen", CreatedAtUtc = DateTimeOffset.UtcNow, ArchivedAt = DateTimeOffset.UtcNow });
        var powerPointId = StubLivePowerPoint(roomId);
        var sut = Sut();

        var exception = await Should.ThrowAsync<EventTaggedEntityArchivedException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), "cooked 2h", DateTimeOffset.UtcNow, "PowerPoint", powerPointId, TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe("event.tag_parent_archived");
        await _repository.DidNotReceive().AddAsync(Arg.Any<Event>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tagging_a_live_Device_under_an_archived_PowerPoint_is_rejected()
    {
        var roomId = StubLiveRoom();
        var powerPointId = Guid.NewGuid();
        _taggingScaffoldRepository.FindPowerPointAsync(powerPointId, Arg.Any<CancellationToken>())
            .Returns(new PowerPoint { Id = powerPointId, HouseholdId = Guid.NewGuid(), RoomId = roomId, Name = "Retired outlet", CreatedAtUtc = DateTimeOffset.UtcNow, ArchivedAt = DateTimeOffset.UtcNow });
        var deviceId = Guid.NewGuid();
        _taggingScaffoldRepository.FindDeviceAsync(deviceId, Arg.Any<CancellationToken>())
            .Returns(new Device { Id = deviceId, HouseholdId = Guid.NewGuid(), PowerPointId = powerPointId, Name = "Induction cooktop", CreatedAtUtc = DateTimeOffset.UtcNow, ArchivedAt = null });
        var sut = Sut();

        var exception = await Should.ThrowAsync<EventTaggedEntityArchivedException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), "cooked 2h", DateTimeOffset.UtcNow, "Device", deviceId, TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe("event.tag_parent_archived");
        await _repository.DidNotReceive().AddAsync(Arg.Any<Event>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Creates_and_persists_an_Event_for_the_callers_own_Household()
    {
        var householdId = Guid.NewGuid();
        var sut = Sut();
        var occurredAt = DateTimeOffset.UtcNow;

        var result = await sut.ExecuteAsync(householdId, "  cooked 2h  ", occurredAt, null, null, TestContext.Current.CancellationToken);

        result.HouseholdId.ShouldBe(householdId);
        result.Description.ShouldBe("cooked 2h");
        result.OccurredAt.ShouldBe(occurredAt);
        await _repository.Received(1).AddAsync(Arg.Is<Event>(e => e.HouseholdId == householdId), Arg.Any<CancellationToken>());
    }

    // Story 6.3 (AC #6, #7): unconditional — CreateEvent itself never checks
    // Household.AiPlausibilityEnabled or whether an AI backend is configured; that check lives
    // solely inside CorrelateEvent (AD-8's anti-hard-branch rule).
    [Fact]
    public async Task Unconditionally_enqueues_a_CorrelateEvent_job_after_persisting()
    {
        var householdId = Guid.NewGuid();
        var sut = Sut();
        var occurredAt = DateTimeOffset.UtcNow;

        var result = await sut.ExecuteAsync(householdId, "cooked 2h", occurredAt, null, null, TestContext.Current.CancellationToken);

        await _jobQueue.Received(1).EnqueueAsync(
            Arg.Is<JobEnvelope<CorrelateEventPayload>>(envelope =>
                envelope.HouseholdId == householdId &&
                envelope.JobType == JobTypes.CorrelateEvent &&
                envelope.Payload.EventId == result.Id &&
                envelope.Payload.HouseholdId == householdId &&
                envelope.Payload.OccurredAt == occurredAt &&
                envelope.Payload.Description == "cooked 2h"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_not_enqueue_a_job_when_validation_fails_before_persisting()
    {
        var sut = Sut();

        await Should.ThrowAsync<EventValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), "", DateTimeOffset.UtcNow, null, null, TestContext.Current.CancellationToken));

        await _jobQueue.DidNotReceive().EnqueueAsync(Arg.Any<JobEnvelope<CorrelateEventPayload>>(), Arg.Any<CancellationToken>());
    }
}
