using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

// AD-24: Energy Tracker supports one active instance. This service only OBSERVES that: every minute it
// records "this process is alive" in the AppInstances table and logs a warning when another instance
// has been alive alongside it for more than 15 minutes (a deploy swap lasts seconds, so it stays
// silent). It never blocks startup (the first await comes before any database work), never fails
// /health, never decides anything (AD-7) and never lets an exception escape ExecuteAsync, because a
// faulted ExecuteAsync stops the host. A beat that fails (table not migrated yet, database down) is
// logged once per run of failures and retried at the next interval.
public class InstanceHeartbeatService(
    AppInstanceIdentity identity,
    InstanceHeartbeatTimings timings,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<InstanceHeartbeatService> logger)
    : BackgroundService
{
    // Observation only, so in memory is enough: after a restart a still-existing overlap warns again.
    private readonly HashSet<Guid> _warnedAbout = [];
    private bool _startedLogged;
    private bool _failing;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Returns control to the host before any database work, so StartAsync completes at once.
        await Task.Yield();

        try
        {
            using var timer = new PeriodicTimer(timings.Interval, timeProvider);
            do
            {
                await BeatAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown, not a failure.
        }
        catch (Exception ex)
        {
            // BeatAsync catches its own failures, so this is the timer itself failing; stop beating
            // rather than fault the host.
            logger.LogError(ex, "Instance heartbeat for instance {InstanceId} stopped unexpectedly; the app keeps running.", identity.Id);
        }
    }

    // Best-effort removal of this process's own row at graceful stop (a revision swap or scale-in).
    // A killed process leaves a row that goes stale after AliveAfter and is purged after RetainFor.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            await RemoveOwnRowAsync();
        }
    }

    private async Task BeatAsync(CancellationToken stoppingToken)
    {
        try
        {
            IReadOnlyList<InstanceOverlapObservation> overlaps;
            using (var scope = scopeFactory.CreateScope())
            {
                overlaps = await scope.ServiceProvider.GetRequiredService<RecordInstanceHeartbeat>()
                    .ExecuteAsync(identity.Id, identity.StartedAtUtc, stoppingToken);
            }

            OnBeatSucceeded(overlaps);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            if (!_failing)
            {
                _failing = true;
                logger.LogWarning(
                    ex,
                    "Instance heartbeat failed for instance {InstanceId}; retrying every {IntervalSeconds:F0} s. Startup and requests are not affected (AD-24).",
                    identity.Id,
                    timings.Interval.TotalSeconds);
            }
        }
    }

    private void OnBeatSucceeded(IReadOnlyList<InstanceOverlapObservation> overlaps)
    {
        if (!_startedLogged)
        {
            _startedLogged = true;
            logger.LogInformation("Instance heartbeat started for instance {InstanceId}", identity.Id);
        }

        if (_failing)
        {
            _failing = false;
            logger.LogInformation("Instance heartbeat recovered for instance {InstanceId}", identity.Id);
        }

        foreach (var overlap in overlaps)
        {
            if (_warnedAbout.Add(overlap.OtherInstanceId))
            {
                logger.LogWarning(
                    "Instance {InstanceId} has been running alongside instance {OtherInstanceId} (started {OtherStartedAtUtc:o}) for {OverlapMinutes:F0} minutes. "
                    + "Energy Tracker supports one active instance (AD-24); see docs/self-hosting.md, 'Run exactly one instance'.",
                    identity.Id,
                    overlap.OtherInstanceId,
                    overlap.OtherStartedAtUtc,
                    overlap.Overlap.TotalMinutes);
            }
        }
    }

    private async Task RemoveOwnRowAsync()
    {
        try
        {
            // Its own token: the host's stopping token is already cancelled by now.
            using var cancellation = new CancellationTokenSource(timings.ShutdownDeleteBound);
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAppInstanceRepository>();
            await repository.DeleteAsync(identity.Id, cancellation.Token).WaitAsync(cancellation.Token);
            logger.LogInformation("Instance heartbeat stopped for instance {InstanceId}; row removed", identity.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Instance heartbeat stopped for instance {InstanceId}; its row could not be removed ({Reason}) and goes stale by itself.",
                identity.Id,
                ex.GetType().Name);
        }
    }
}
