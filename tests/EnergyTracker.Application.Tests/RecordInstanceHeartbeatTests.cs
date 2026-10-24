using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace EnergyTracker.Application.Tests;

// Story 11.13 (AD-24, AC #2, #6, #7): one beat writes this process's own row, purges old rows and
// reports the alive OTHER instances whose overlap with this one exceeds the warn threshold. The use
// case only produces evidence (AD-7); the hosted service decides what to log.
public class RecordInstanceHeartbeatTests
{
    private static readonly DateTimeOffset SelfStartedAtUtc = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    // Distinct from the production defaults, so a use case that ignores the injected timings fails.
    private static readonly InstanceHeartbeatTimings Timings = new(
        Interval: TimeSpan.FromMilliseconds(10),
        AliveAfter: TimeSpan.FromMinutes(7),
        OverlapWarnAfter: TimeSpan.FromMinutes(15),
        RetainFor: TimeSpan.FromHours(2));

    private readonly IAppInstanceRepository _repository = Substitute.For<IAppInstanceRepository>();
    private readonly TimeProvider _timeProvider = Substitute.For<TimeProvider>();
    private readonly Guid _selfId = Guid.NewGuid();

    private RecordInstanceHeartbeat CreateUseCase(DateTimeOffset now)
    {
        _timeProvider.GetUtcNow().Returns(now);
        return new RecordInstanceHeartbeat(_repository, Timings, _timeProvider);
    }

    private static AppInstance Other(DateTimeOffset startedAtUtc, DateTimeOffset lastSeenAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        StartedAtUtc = startedAtUtc,
        LastSeenAtUtc = lastSeenAtUtc,
    };

    private void OthersAre(params AppInstance[] others) =>
        _repository.ListAliveOthersAsync(_selfId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(others);

    // Operational constants (AD-24, Q3): a self-hoster has no reason to tune them, so they are code, not config.
    [Fact]
    public void The_default_timings_are_one_minute_three_minutes_fifteen_minutes_and_twenty_four_hours()
    {
        var defaults = InstanceHeartbeatTimings.Default;

        defaults.Interval.ShouldBe(TimeSpan.FromMinutes(1));
        defaults.AliveAfter.ShouldBe(TimeSpan.FromMinutes(3));
        defaults.OverlapWarnAfter.ShouldBe(TimeSpan.FromMinutes(15));
        defaults.RetainFor.ShouldBe(TimeSpan.FromHours(24));
        defaults.ShutdownDeleteBound.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_beat_upserts_this_process_with_its_real_start_time_and_the_current_time()
    {
        var now = SelfStartedAtUtc.AddMinutes(4);
        OthersAre();

        await CreateUseCase(now).ExecuteAsync(_selfId, SelfStartedAtUtc, TestContext.Current.CancellationToken);

        await _repository.Received(1).BeatAsync(_selfId, SelfStartedAtUtc, now, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_beat_purges_rows_older_than_the_retention_window()
    {
        var now = SelfStartedAtUtc.AddMinutes(4);
        OthersAre();

        await CreateUseCase(now).ExecuteAsync(_selfId, SelfStartedAtUtc, TestContext.Current.CancellationToken);

        await _repository.Received(1).DeleteOlderThanAsync(now - Timings.RetainFor, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_beat_asks_for_other_instances_alive_within_the_alive_window()
    {
        var now = SelfStartedAtUtc.AddMinutes(4);
        OthersAre();

        await CreateUseCase(now).ExecuteAsync(_selfId, SelfStartedAtUtc, TestContext.Current.CancellationToken);

        await _repository.Received(1).ListAliveOthersAsync(_selfId, now - Timings.AliveAfter, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task With_no_other_instance_there_is_nothing_to_report()
    {
        OthersAre();

        var observations = await CreateUseCase(SelfStartedAtUtc.AddMinutes(30))
            .ExecuteAsync(_selfId, SelfStartedAtUtc, TestContext.Current.CancellationToken);

        observations.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_five_minute_deploy_swap_overlap_reports_nothing()
    {
        // The new revision (self) started at 12:00; the old one (other) ran for hours and is still
        // alive at 12:05. Overlap = 5 minutes.
        var now = SelfStartedAtUtc.AddMinutes(5);
        OthersAre(Other(SelfStartedAtUtc.AddHours(-6), now.AddSeconds(-30)));

        var observations = await CreateUseCase(now).ExecuteAsync(_selfId, SelfStartedAtUtc, TestContext.Current.CancellationToken);

        observations.ShouldBeEmpty();
    }

    // The boundary: the overlap is min(self now, other last seen) - max(starts). Other last seen =
    // self start + overlap, and "now" is 30 seconds later (a beat is up to a minute old).
    [Theory]
    [InlineData(14 * 60 + 59, false)]
    [InlineData(15 * 60, false)]
    [InlineData(15 * 60 + 1, true)]
    public async Task The_warn_threshold_is_strictly_greater_than_fifteen_minutes(int overlapSeconds, bool expectObservation)
    {
        var otherLastSeen = SelfStartedAtUtc.AddSeconds(overlapSeconds);
        var now = otherLastSeen.AddSeconds(30);
        OthersAre(Other(SelfStartedAtUtc.AddHours(-6), otherLastSeen));

        var observations = await CreateUseCase(now).ExecuteAsync(_selfId, SelfStartedAtUtc, TestContext.Current.CancellationToken);

        observations.Count.ShouldBe(expectObservation ? 1 : 0);
    }

    [Fact]
    public async Task A_long_overlap_names_the_other_instance_its_start_time_and_the_overlap()
    {
        var now = SelfStartedAtUtc.AddMinutes(40);
        var other = Other(SelfStartedAtUtc.AddHours(-1), now.AddSeconds(-20));
        OthersAre(other);

        var observations = await CreateUseCase(now).ExecuteAsync(_selfId, SelfStartedAtUtc, TestContext.Current.CancellationToken);

        var observation = observations.ShouldHaveSingleItem();
        observation.OtherInstanceId.ShouldBe(other.Id);
        observation.OtherStartedAtUtc.ShouldBe(other.StartedAtUtc);
        // Self started 12:00, the other was last seen 12:39:40: 39 minutes 40 seconds.
        observation.Overlap.ShouldBe(TimeSpan.FromMinutes(39) + TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task Two_long_lived_others_yield_two_observations()
    {
        var now = SelfStartedAtUtc.AddMinutes(30);
        var first = Other(SelfStartedAtUtc.AddHours(-3), now.AddSeconds(-10));
        var second = Other(SelfStartedAtUtc.AddMinutes(-20), now.AddSeconds(-40));
        OthersAre(first, second);

        var observations = await CreateUseCase(now).ExecuteAsync(_selfId, SelfStartedAtUtc, TestContext.Current.CancellationToken);

        observations.Select(o => o.OtherInstanceId).ShouldBe([first.Id, second.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task A_stale_other_is_not_returned_by_the_repository_so_it_never_yields_an_observation()
    {
        // The alive filter lives in the repository query (cutoff = now - AliveAfter): a stale row,
        // however long it once overlapped, is simply not in the list.
        OthersAre();

        var observations = await CreateUseCase(SelfStartedAtUtc.AddHours(5))
            .ExecuteAsync(_selfId, SelfStartedAtUtc, TestContext.Current.CancellationToken);

        observations.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failing_purge_does_not_cost_the_overlap_check()
    {
        var now = SelfStartedAtUtc.AddMinutes(40);
        var other = Other(SelfStartedAtUtc.AddHours(-1), now.AddSeconds(-20));
        OthersAre(other);
        _repository.DeleteOlderThanAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("lock wait timeout"));

        var observations = await CreateUseCase(now).ExecuteAsync(_selfId, SelfStartedAtUtc, TestContext.Current.CancellationToken);

        observations.ShouldHaveSingleItem().OtherInstanceId.ShouldBe(other.Id);
    }

    [Fact]
    public async Task A_failing_list_is_not_caught_here_because_the_hosted_service_owns_that()
    {
        _repository.ListAliveOthersAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database is down"));

        await Should.ThrowAsync<InvalidOperationException>(() => CreateUseCase(SelfStartedAtUtc.AddMinutes(1))
            .ExecuteAsync(_selfId, SelfStartedAtUtc, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_repository_failure_is_not_caught_here_because_the_hosted_service_owns_that()
    {
        _repository.BeatAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database is down"));

        await Should.ThrowAsync<InvalidOperationException>(() => CreateUseCase(SelfStartedAtUtc.AddMinutes(1))
            .ExecuteAsync(_selfId, SelfStartedAtUtc, TestContext.Current.CancellationToken));
    }
}
