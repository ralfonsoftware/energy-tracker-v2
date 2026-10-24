using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

// Story 11.13 (AD-24, AC #2, #8, #9): the heartbeat hosted service only produces log lines. It never
// delays startup, never lets an exception escape ExecuteAsync (a faulted ExecuteAsync stops the host),
// warns once per overlapping instance and removes its own row best-effort at shutdown. No database:
// a substituted IAppInstanceRepository under the real RecordInstanceHeartbeat, intervals of milliseconds.
public class InstanceHeartbeatServiceTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly IAppInstanceRepository _repository = Substitute.For<IAppInstanceRepository>();
    private readonly ILogger<InstanceHeartbeatService> _logger = Substitute.For<ILogger<InstanceHeartbeatService>>();
    private readonly List<InstanceHeartbeatService> _started = [];

    // Self has been running for an hour, so any "other" that is seen right now overlaps it for about as
    // long as that other has been alive, which the individual tests choose.
    private readonly AppInstanceIdentity _identity = new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-1));

    private static readonly InstanceHeartbeatTimings Timings = new(
        Interval: TimeSpan.FromMilliseconds(10),
        AliveAfter: TimeSpan.FromMinutes(3),
        OverlapWarnAfter: TimeSpan.FromMinutes(15),
        RetainFor: TimeSpan.FromHours(24))
    {
        ShutdownDeleteBound = TimeSpan.FromMilliseconds(300),
    };

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _started)
        {
            // Bounded, so a service that cannot stop fails its own test instead of hanging the run.
            await service.StopAsync(CancellationToken.None).WaitAsync(Wait);
            service.Dispose();
        }
    }

    private IServiceScopeFactory ScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Timings);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped(_ => _repository);
        services.AddScoped<RecordInstanceHeartbeat>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private InstanceHeartbeatService CreateService()
    {
        var service = new InstanceHeartbeatService(_identity, Timings, ScopeFactory(), TimeProvider.System, _logger);
        _started.Add(service);
        return service;
    }

    // ExecuteAsync is protected; the probe lets a test call it directly, as a host that does not
    // push it to the thread pool would (older hosts ran its synchronous part inside StartAsync).
    private sealed class ExecuteProbe(
        AppInstanceIdentity identity, InstanceHeartbeatTimings timings, IServiceScopeFactory scopeFactory,
        ILogger<InstanceHeartbeatService> logger)
        : InstanceHeartbeatService(identity, timings, scopeFactory, TimeProvider.System, logger)
    {
        public Task RunAsync(CancellationToken cancellationToken) => ExecuteAsync(cancellationToken);
    }

    private async Task StartAsync(InstanceHeartbeatService service) =>
        await service.StartAsync(TestContext.Current.CancellationToken);

    private static AppInstance AliveOther(TimeSpan aliveFor) => new()
    {
        Id = Guid.NewGuid(),
        StartedAtUtc = DateTimeOffset.UtcNow - aliveFor,
        LastSeenAtUtc = DateTimeOffset.UtcNow,
    };

    private void OthersAre(Func<IReadOnlyList<AppInstance>> others) =>
        _repository.ListAliveOthersAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ => others());

    private int BeatCount() => _repository.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAppInstanceRepository.BeatAsync));

    private static IEnumerable<(LogLevel Level, string Message)> Logged(ILogger logger) =>
        logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(c => ((LogLevel)c.GetArguments()[0]!, c.GetArguments()[2]!.ToString()!))
            .ToList();

    private int Count(LogLevel level) => Logged(_logger).Count(l => l.Level == level);

    private IEnumerable<string> Messages(LogLevel level) => Logged(_logger).Where(l => l.Level == level).Select(l => l.Message);

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {because}");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private async Task WaitForMoreBeatsAsync(int count)
    {
        var target = BeatCount() + count;
        await WaitUntilAsync(() => BeatCount() >= target, $"{count} more beats");
    }

    [Fact]
    public async Task StartAsync_returns_at_once_even_when_the_first_database_call_blocks()
    {
        // A synchronous block in the first beat (a driver that does not yield) must not hold up host
        // startup: the first await happens before any database work.
        using var gate = new ManualResetEventSlim();
        _repository.BeatAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                gate.Wait(Wait);
                return Task.CompletedTask;
            });
        var service = CreateService();

        var start = Task.Run(() => service.StartAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        gate.Set();

        finished.ShouldBeSameAs(start, "StartAsync waited for the first beat; the heartbeat must never delay startup (AD-24).");
    }

    [Fact]
    public async Task ExecuteAsync_hands_control_back_before_any_database_work()
    {
        using var gate = new ManualResetEventSlim();
        _repository.BeatAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                gate.Wait(Wait);
                return Task.CompletedTask;
            });
        var probe = new ExecuteProbe(_identity, Timings, ScopeFactory(), _logger);
        using var cancellation = new CancellationTokenSource();

        // The Action overload completes when RunAsync *returns* its task (not when the loop ends).
        Task? loop = null;
        var call = Task.Run(() => { loop = probe.RunAsync(cancellation.Token); }, TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        var returnedPromptly = finished == call;
        gate.Set();
        await cancellation.CancelAsync();
        await call.WaitAsync(Wait);
        await loop!.WaitAsync(Wait);

        returnedPromptly.ShouldBeTrue("ExecuteAsync ran database work before its first await; the heartbeat must hand control back first (AD-24).");
    }

    [Fact]
    public async Task The_first_successful_beat_logs_the_started_line_with_the_instance_id()
    {
        var service = CreateService();

        await StartAsync(service);
        await WaitUntilAsync(() => Count(LogLevel.Information) >= 1, "the started line");

        Messages(LogLevel.Information).ShouldContain(m => m == $"Instance heartbeat started for instance {_identity.Id}");
    }

    [Fact]
    public async Task A_long_overlap_logs_exactly_one_warning_for_that_instance_across_many_beats_and_one_per_new_instance()
    {
        var first = AliveOther(TimeSpan.FromHours(3));
        var second = AliveOther(TimeSpan.FromMinutes(40));
        var others = new List<AppInstance> { first };
        OthersAre(() => others.ToList());
        var service = CreateService();

        await StartAsync(service);
        await WaitUntilAsync(() => Count(LogLevel.Warning) == 1, "the first overlap warning");
        await WaitForMoreBeatsAsync(8);
        Count(LogLevel.Warning).ShouldBe(1, "the same overlapping instance must not warn on every beat");

        others.Add(second);
        await WaitUntilAsync(() => Count(LogLevel.Warning) == 2, "the second overlap warning");
        await WaitForMoreBeatsAsync(8);

        Count(LogLevel.Warning).ShouldBe(2);
        var warnings = Messages(LogLevel.Warning).ToList();
        warnings.ShouldContain(w => w.Contains(first.Id.ToString()));
        warnings.ShouldContain(w => w.Contains(second.Id.ToString()));
        warnings.ShouldAllBe(w => w.Contains(_identity.Id.ToString()));
        warnings.ShouldAllBe(w => w.Contains("AD-24") && w.Contains("docs/self-hosting.md") && w.Contains("Run exactly one instance"));
        Count(LogLevel.Error).ShouldBe(0);
    }

    [Fact]
    public async Task A_short_overlap_such_as_a_deploy_swap_logs_no_warning_and_no_error()
    {
        // Self has run for an hour, the other for 5 minutes: overlap = 5 minutes, below the 15.
        OthersAre(() => [AliveOther(TimeSpan.FromMinutes(5))]);
        var service = CreateService();

        await StartAsync(service);
        await WaitForMoreBeatsAsync(10);

        Count(LogLevel.Warning).ShouldBe(0);
        Count(LogLevel.Error).ShouldBe(0);
    }

    [Fact]
    public async Task A_failing_repository_logs_one_warning_for_the_run_keeps_beating_and_logs_recovery()
    {
        var fail = true;
        _repository.BeatAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ => fail ? Task.FromException(new InvalidOperationException("relation \"AppInstances\" does not exist")) : Task.CompletedTask);
        var service = CreateService();

        await StartAsync(service);
        await WaitUntilAsync(() => Count(LogLevel.Warning) == 1, "the failure warning");
        await WaitForMoreBeatsAsync(8);
        Count(LogLevel.Warning).ShouldBe(1, "a run of failed beats logs one warning, not one per beat");
        Count(LogLevel.Error).ShouldBe(0);
        service.ExecuteTask!.IsFaulted.ShouldBeFalse("an exception must never escape ExecuteAsync: it would stop the host");

        fail = false;
        await WaitUntilAsync(() => Messages(LogLevel.Information).Any(m => m.Contains("recovered")), "the recovery line");
        Messages(LogLevel.Information).ShouldContain(m => m == $"Instance heartbeat started for instance {_identity.Id}");

        // A second run of failures warns again.
        fail = true;
        await WaitUntilAsync(() => Count(LogLevel.Warning) == 2, "the second failure warning");
    }

    [Fact]
    public async Task A_cancellation_at_shutdown_is_not_logged_as_a_failure()
    {
        // A beat that is waiting on the database when the host stops throws OperationCanceledException.
        _repository.BeatAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()));
        var service = CreateService();
        await StartAsync(service);
        await WaitForMoreBeatsAsync(1);

        await service.StopAsync(CancellationToken.None);

        Count(LogLevel.Warning).ShouldBe(0);
        Count(LogLevel.Error).ShouldBe(0);
    }

    [Fact]
    public async Task StopAsync_removes_this_processs_row_once_with_a_token_that_is_not_already_cancelled()
    {
        bool? deleteTokenWasCancelled = null;
        _repository.DeleteAsync(_identity.Id, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                deleteTokenWasCancelled = call.Arg<CancellationToken>().IsCancellationRequested;
                return Task.CompletedTask;
            });
        var service = CreateService();
        await StartAsync(service);
        await WaitForMoreBeatsAsync(2);

        await service.StopAsync(CancellationToken.None);

        await _repository.Received(1).DeleteAsync(_identity.Id, Arg.Any<CancellationToken>());
        deleteTokenWasCancelled.ShouldBe(false, "the host's stopping token is already cancelled; the delete needs its own");
        Messages(LogLevel.Information).ShouldContain(m => m == $"Instance heartbeat stopped for instance {_identity.Id}; row removed");
    }

    [Fact]
    public async Task A_throwing_delete_at_shutdown_is_swallowed()
    {
        _repository.DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database is gone"));
        var service = CreateService();
        await StartAsync(service);
        await WaitForMoreBeatsAsync(2);

        await Should.NotThrowAsync(() => service.StopAsync(CancellationToken.None));

        Count(LogLevel.Error).ShouldBe(0);
    }

    [Fact]
    public async Task A_delete_that_never_completes_does_not_delay_shutdown_beyond_its_bound()
    {
        _repository.DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource().Task);
        var service = CreateService();
        await StartAsync(service);
        await WaitForMoreBeatsAsync(2);

        var stop = service.StopAsync(CancellationToken.None);
        var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        finished.ShouldBeSameAs(stop, "a stuck database must not delay shutdown beyond ShutdownDeleteBound");
        await stop;
    }
}
