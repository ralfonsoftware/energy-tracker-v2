using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

// Story 3.6/AD-6 extension: BackgroundJobEnqueueRecorder persists a Queued row at enqueue time.
// Story 11.2/AD-6 (amended): BackgroundJobProcessor.ProcessAsync drives every status change
// through IBackgroundJobLifecycle (conditional update + ownership token), heartbeats from its own
// scope, and never resurrects or overwrites a row.
public class BackgroundJobProcessorTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private sealed class FixedHouseholdAccessor(Guid householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;

        public Guid? HouseholdMemberId => null;
    }

    private readonly ISmartPlugImportRepository _smartPlugImportRepository = Substitute.For<ISmartPlugImportRepository>();

    // A short heartbeat interval lets the heartbeat tests observe a tick without sleeping a minute.
    private static readonly JobLifecycleTimings FastHeartbeat = new(TimeSpan.FromMilliseconds(50), TimeSpan.FromMinutes(5));

    private ServiceProvider BuildServices(Guid householdId, JobLifecycleTimings? timings = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<EnergyTrackerDbContext>(o => o.UseNpgsql(
            _container.GetConnectionString(), n => n.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres")));
        services.AddScoped<JobHouseholdContext>();
        services.AddSingleton<ICurrentHouseholdAccessor>(new FixedHouseholdAccessor(householdId));
        services.AddSingleton(timings ?? JobLifecycleTimings.Default);
        services.AddScoped<IBackgroundJobLifecycle, BackgroundJobLifecycle>();
        // The CleanUpSmartPlugImportJobs use case is the controllable stand-in for "a job that runs":
        // its repository calls are NSubstitute ports the test can block, fail or cancel.
        services.AddSingleton(_smartPlugImportRepository);
        services.AddSingleton(Substitute.For<IBackgroundJobRepository>());
        services.AddScoped<CleanUpSmartPlugImportJobs>();
        return services.BuildServiceProvider();
    }

    private static BackgroundJobProcessor NewProcessor(ServiceProvider provider, JobLifecycleTimings? timings = null) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), timings ?? JobLifecycleTimings.Default, NullLogger<BackgroundJobProcessor>.Instance);

    private const string CleanUpPayload = "{\"DeleteAll\":false}";

    private static async Task<Guid> SeedJobAsync(
        ServiceProvider provider, Guid householdId, BackgroundJobStatus status, string jobType,
        DateTimeOffset? startedAt = null, DateTimeOffset? heartbeatAt = null, CancellationToken cancellationToken = default)
    {
        var jobId = Guid.NewGuid();
        using var seedScope = provider.CreateScope();
        var dbContext = seedScope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        dbContext.BackgroundJobs.Add(new BackgroundJob
        {
            Id = jobId,
            HouseholdId = householdId,
            JobType = jobType,
            Status = status,
            OriginalFileName = "export.xlsx",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-20),
            StartedAtUtc = startedAt,
            HeartbeatAtUtc = heartbeatAt,
            CompletedAtUtc = status is BackgroundJobStatus.Completed or BackgroundJobStatus.Failed ? DateTimeOffset.UtcNow.AddMinutes(-1) : null,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return jobId;
    }

    private static async Task<BackgroundJob?> LoadAsync(ServiceProvider provider, Guid jobId, CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        return await dbContext.BackgroundJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken);
    }

    private static async Task<int> ThrowAfterYieldAsync(Func<Exception> exception)
    {
        await Task.Yield();
        throw exception();
    }

    private static async Task MigrateAsync(ServiceProvider provider, CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }

    private static async Task SeedHouseholdAsync(ServiceProvider provider, Guid householdId, CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Guid> SeedHouseholdMemberAsync(ServiceProvider provider, Guid householdId, CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        var member = new HouseholdMember
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            ExternalIssuer = "https://issuer.example",
            ExternalSubjectId = Guid.NewGuid().ToString(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        dbContext.HouseholdMembers.Add(member);
        await dbContext.SaveChangesAsync(cancellationToken);
        return member.Id;
    }

    [Fact]
    public async Task EnqueueAsync_persists_a_Queued_row_with_OriginalFileName_and_QueuedByHouseholdMemberId_before_dequeue()
    {
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices(householdId);
        await MigrateAsync(provider, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(provider, householdId, TestContext.Current.CancellationToken);
        var memberId = await SeedHouseholdMemberAsync(provider, householdId, TestContext.Current.CancellationToken);

        var recorder = new BackgroundJobEnqueueRecorder(provider.GetRequiredService<IServiceScopeFactory>());
        var jobId = Guid.NewGuid();
        var envelope = new JobEnvelope<string>(
            jobId, householdId, "CustomJobType", "payload", QueuedByHouseholdMemberId: memberId, OriginalFileName: "export.xlsx");

        await recorder.RecordAsync(envelope, TestContext.Current.CancellationToken);

        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        var persisted = await dbContext.BackgroundJobs.SingleAsync(j => j.Id == jobId, TestContext.Current.CancellationToken);
        persisted.Status.ShouldBe(BackgroundJobStatus.Queued);
        persisted.OriginalFileName.ShouldBe("export.xlsx");
        persisted.QueuedByHouseholdMemberId.ShouldBe(memberId);
    }

    [Fact]
    public async Task DeleteAsync_removes_a_Queued_row_left_orphaned_by_a_failed_queue_send()
    {
        // Review-round-2 patch: AzureStorageQueueJobQueue.EnqueueAsync calls this as its
        // compensating action when the queue send fails after RecordAsync already committed the
        // Queued row — otherwise it becomes a permanent phantom "Waiting" row (Waiting/Processing/
        // Needs Mapping rows are exempt from the 30-day sweep).
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices(householdId);
        await MigrateAsync(provider, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(provider, householdId, TestContext.Current.CancellationToken);

        var recorder = new BackgroundJobEnqueueRecorder(provider.GetRequiredService<IServiceScopeFactory>());
        var jobId = Guid.NewGuid();
        var envelope = new JobEnvelope<string>(jobId, householdId, "CustomJobType", "payload", QueuedByHouseholdMemberId: null, OriginalFileName: "export.xlsx");
        await recorder.RecordAsync(envelope, TestContext.Current.CancellationToken);

        await recorder.DeleteAsync(jobId, TestContext.Current.CancellationToken);

        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        (await dbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ProcessAsync_a_successful_run_ends_Completed_with_StartedAtUtc_and_HeartbeatAtUtc_set()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices(householdId);
        await MigrateAsync(provider, ct);
        await SeedHouseholdAsync(provider, householdId, ct);
        var jobId = await SeedJobAsync(provider, householdId, BackgroundJobStatus.Queued, JobTypes.CleanUpSmartPlugImportJobs, cancellationToken: ct);
        _smartPlugImportRepository.DeleteJobsAsync(householdId, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(2);

        await NewProcessor(provider).ProcessAsync(new JobMessage(jobId, householdId, JobTypes.CleanUpSmartPlugImportJobs, CleanUpPayload), ct);

        var job = (await LoadAsync(provider, jobId, ct)).ShouldNotBeNull();
        job.Status.ShouldBe(BackgroundJobStatus.Completed);
        job.StartedAtUtc.ShouldNotBeNull();
        job.HeartbeatAtUtc.ShouldNotBeNull();
        job.CompletedAtUtc.ShouldNotBeNull();
        job.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public async Task ProcessAsync_transitions_an_existing_Queued_row_instead_of_inserting_a_second_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices(householdId);
        await MigrateAsync(provider, ct);
        await SeedHouseholdAsync(provider, householdId, ct);
        var jobId = await SeedJobAsync(provider, householdId, BackgroundJobStatus.Queued, "UnknownJobType", cancellationToken: ct);

        // An unrecognized JobType throws inside ProcessAsync's own dispatch switch and is caught,
        // ending in Failed — irrelevant to what this test verifies (the transition ahead of that
        // switch, and that no second row is inserted for the same JobId).
        await NewProcessor(provider).ProcessAsync(new JobMessage(jobId, householdId, "UnknownJobType", "{}"), ct);

        using var verifyScope = provider.CreateScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        var rows = await verifyDbContext.BackgroundJobs.Where(j => j.Id == jobId).ToListAsync(ct);
        rows.ShouldHaveSingleItem();
        rows[0].Status.ShouldNotBe(BackgroundJobStatus.Queued);
        rows[0].CompletedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task ProcessAsync_two_concurrent_deliveries_of_an_existing_Queued_row_run_the_job_exactly_once()
    {
        // Two redeliveries of the same message (e.g. Azure Storage Queue visibility-timeout expiry
        // mid-processing) must resolve to one execution and one consistent terminal state — the
        // conditional TryStart is the gate, not a read-then-write.
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices(householdId);
        await MigrateAsync(provider, ct);
        await SeedHouseholdAsync(provider, householdId, ct);
        var jobId = await SeedJobAsync(provider, householdId, BackgroundJobStatus.Queued, JobTypes.CleanUpSmartPlugImportJobs, cancellationToken: ct);
        _smartPlugImportRepository.DeleteJobsAsync(householdId, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(0);
        var message = new JobMessage(jobId, householdId, JobTypes.CleanUpSmartPlugImportJobs, CleanUpPayload);

        await Task.WhenAll(NewProcessor(provider).ProcessAsync(message, ct), NewProcessor(provider).ProcessAsync(message, ct));

        await _smartPlugImportRepository.Received(1).DeleteJobsAsync(householdId, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        var job = (await LoadAsync(provider, jobId, ct)).ShouldNotBeNull();
        job.Status.ShouldBe(BackgroundJobStatus.Completed);
    }

    [Fact]
    public async Task ProcessAsync_does_nothing_when_the_row_is_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices(householdId);
        await MigrateAsync(provider, ct);
        await SeedHouseholdAsync(provider, householdId, ct);
        var jobId = Guid.NewGuid();

        await NewProcessor(provider).ProcessAsync(new JobMessage(jobId, householdId, JobTypes.CleanUpSmartPlugImportJobs, CleanUpPayload), ct);

        (await LoadAsync(provider, jobId, ct)).ShouldBeNull();
        await _smartPlugImportRepository.DidNotReceiveWithAnyArgs().DeleteJobsAsync(default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ProcessAsync_does_nothing_for_a_Processing_row_whose_owner_is_still_heartbeating()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices(householdId);
        await MigrateAsync(provider, ct);
        await SeedHouseholdAsync(provider, householdId, ct);
        var jobId = await SeedJobAsync(provider, householdId, BackgroundJobStatus.Processing, JobTypes.CleanUpSmartPlugImportJobs,
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-15), heartbeatAt: DateTimeOffset.UtcNow.AddSeconds(-10), cancellationToken: ct);

        await NewProcessor(provider).ProcessAsync(new JobMessage(jobId, householdId, JobTypes.CleanUpSmartPlugImportJobs, CleanUpPayload), ct);

        await _smartPlugImportRepository.DidNotReceiveWithAnyArgs().DeleteJobsAsync(default, default, TestContext.Current.CancellationToken);
        (await LoadAsync(provider, jobId, ct))!.Status.ShouldBe(BackgroundJobStatus.Processing);
    }

    [Fact]
    public async Task ProcessAsync_a_generic_failure_leaves_ErrorMessage_null_rather_than_hardcoded_English_text()
    {
        // Round-4 incident fix: a generic (non-validation) failure must not carry a hardcoded
        // English sentence — this backend has no concept of the polling client's locale, and every
        // caller of GET /api/jobs/{id} already falls back to its own correctly-localized `t('...')`
        // string via `errorMessage ?? t(...)`. A user reported the earlier hardcoded fallback
        // ("An unexpected error occurred while cleaning up the history.") rendering in English
        // regardless of their browser's locale.
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices(householdId);
        await MigrateAsync(provider, ct);
        await SeedHouseholdAsync(provider, householdId, ct);
        var jobId = await SeedJobAsync(provider, householdId, BackgroundJobStatus.Queued, "UnknownJobType", cancellationToken: ct);

        // "UnknownJobType" hits the dispatch switch's `default: throw new InvalidOperationException`
        // — a generic, non-validation failure, same as any unexpected internal error would be.
        await NewProcessor(provider).ProcessAsync(new JobMessage(jobId, householdId, "UnknownJobType", "{}"), ct);

        var persisted = (await LoadAsync(provider, jobId, ct)).ShouldNotBeNull();
        persisted.Status.ShouldBe(BackgroundJobStatus.Failed);
        persisted.ErrorMessage.ShouldBeNull();
        persisted.CompletedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task ProcessAsync_a_validation_exception_message_is_forwarded_as_the_ErrorMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices(householdId);
        await MigrateAsync(provider, ct);
        await SeedHouseholdAsync(provider, householdId, ct);
        var jobId = await SeedJobAsync(provider, householdId, BackgroundJobStatus.Queued, JobTypes.CleanUpSmartPlugImportJobs, cancellationToken: ct);
        _smartPlugImportRepository.DeleteJobsAsync(householdId, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ThrowAfterYieldAsync(() => new SmartPlugImportValidationException("bad file")));

        await NewProcessor(provider).ProcessAsync(new JobMessage(jobId, householdId, JobTypes.CleanUpSmartPlugImportJobs, CleanUpPayload), ct);

        var persisted = (await LoadAsync(provider, jobId, ct)).ShouldNotBeNull();
        persisted.Status.ShouldBe(BackgroundJobStatus.Failed);
        persisted.ErrorMessage.ShouldBe("bad file");
    }

    [Fact]
    public async Task ProcessAsync_skips_a_redelivered_message_against_an_already_terminal_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices(householdId);
        await MigrateAsync(provider, ct);
        await SeedHouseholdAsync(provider, householdId, ct);
        var jobId = await SeedJobAsync(provider, householdId, BackgroundJobStatus.Completed, JobTypes.CleanUpSmartPlugImportJobs, cancellationToken: ct);
        var before = (await LoadAsync(provider, jobId, ct))!;

        await NewProcessor(provider).ProcessAsync(new JobMessage(jobId, householdId, JobTypes.CleanUpSmartPlugImportJobs, CleanUpPayload), ct);

        await _smartPlugImportRepository.DidNotReceiveWithAnyArgs().DeleteJobsAsync(default, default, TestContext.Current.CancellationToken);
        var persisted = (await LoadAsync(provider, jobId, ct))!;
        persisted.Status.ShouldBe(BackgroundJobStatus.Completed);
        persisted.CompletedAtUtc.ShouldBe(before.CompletedAtUtc);
    }

    [Fact]
    public async Task ProcessAsync_refreshes_HeartbeatAtUtc_while_the_use_case_is_still_running_and_stops_after_completion()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices(householdId, FastHeartbeat);
        await MigrateAsync(provider, ct);
        await SeedHouseholdAsync(provider, householdId, ct);
        var jobId = await SeedJobAsync(provider, householdId, BackgroundJobStatus.Queued, JobTypes.CleanUpSmartPlugImportJobs, cancellationToken: ct);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _smartPlugImportRepository.DeleteJobsAsync(householdId, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                started.TrySetResult();
                return release.Task;
            });

        var run = NewProcessor(provider, FastHeartbeat)
            .ProcessAsync(new JobMessage(jobId, householdId, JobTypes.CleanUpSmartPlugImportJobs, CleanUpPayload), ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        var firstSeen = (await LoadAsync(provider, jobId, ct))!.HeartbeatAtUtc.ShouldNotBeNull();
        DateTimeOffset? advanced = null;
        for (var attempt = 0; attempt < 100 && advanced is null; attempt++)
        {
            await Task.Delay(100, ct);
            var current = (await LoadAsync(provider, jobId, ct))!.HeartbeatAtUtc;
            if (current > firstSeen)
            {
                advanced = current;
            }
        }

        advanced.ShouldNotBeNull("HeartbeatAtUtc never advanced while the use case was still running");
        release.SetResult(1);
        await run;

        var done = (await LoadAsync(provider, jobId, ct))!;
        done.Status.ShouldBe(BackgroundJobStatus.Completed);
        var heartbeatAtCompletion = done.HeartbeatAtUtc;
        await Task.Delay(300, ct);
        (await LoadAsync(provider, jobId, ct))!.HeartbeatAtUtc.ShouldBe(heartbeatAtCompletion);
    }

    [Fact]
    public async Task ProcessAsync_an_OperationCanceledException_not_caused_by_shutdown_ends_the_job_Failed()
    {
        // C7, processor half: an HttpClient/command timeout that surfaces as a cancellation is a
        // failure, not a shutdown — it must not leave the job Processing forever.
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices(householdId);
        await MigrateAsync(provider, ct);
        await SeedHouseholdAsync(provider, householdId, ct);
        var jobId = await SeedJobAsync(provider, householdId, BackgroundJobStatus.Queued, JobTypes.CleanUpSmartPlugImportJobs, cancellationToken: ct);
        _smartPlugImportRepository.DeleteJobsAsync(householdId, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ThrowAfterYieldAsync(() => new TaskCanceledException("timeout")));

        await NewProcessor(provider).ProcessAsync(new JobMessage(jobId, householdId, JobTypes.CleanUpSmartPlugImportJobs, CleanUpPayload), ct);

        var persisted = (await LoadAsync(provider, jobId, ct)).ShouldNotBeNull();
        persisted.Status.ShouldBe(BackgroundJobStatus.Failed);
        persisted.ErrorMessage.ShouldBeNull();
        persisted.CompletedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task ProcessAsync_a_shutdown_cancellation_is_rethrown_and_leaves_the_job_Processing()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices(householdId);
        await MigrateAsync(provider, ct);
        await SeedHouseholdAsync(provider, householdId, ct);
        var jobId = await SeedJobAsync(provider, householdId, BackgroundJobStatus.Queued, JobTypes.CleanUpSmartPlugImportJobs, cancellationToken: ct);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _smartPlugImportRepository.DeleteJobsAsync(householdId, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                stopping.Cancel();
                return ThrowAfterYieldAsync(() => new OperationCanceledException(stopping.Token));
            });

        await Should.ThrowAsync<OperationCanceledException>(() => NewProcessor(provider)
            .ProcessAsync(new JobMessage(jobId, householdId, JobTypes.CleanUpSmartPlugImportJobs, CleanUpPayload), stopping.Token));

        var persisted = (await LoadAsync(provider, jobId, ct)).ShouldNotBeNull();
        persisted.Status.ShouldBe(BackgroundJobStatus.Processing);
        persisted.CompletedAtUtc.ShouldBeNull();
    }

    [Fact]
    public async Task ProcessAsync_a_job_whose_row_was_deleted_mid_run_resurrects_nothing_and_throws_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices(householdId);
        await MigrateAsync(provider, ct);
        await SeedHouseholdAsync(provider, householdId, ct);
        var jobId = await SeedJobAsync(provider, householdId, BackgroundJobStatus.Queued, JobTypes.CleanUpSmartPlugImportJobs, cancellationToken: ct);
        _smartPlugImportRepository.DeleteJobsAsync(householdId, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                // What cleanup's deleteAll does to an in-flight job row.
                using var scope = provider.CreateScope();
                scope.ServiceProvider.GetRequiredService<JobHouseholdContext>().HouseholdId = householdId;
                var db = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
                await db.BackgroundJobs.Where(j => j.Id == jobId).ExecuteDeleteAsync(ct);
                return 1;
            });

        await NewProcessor(provider).ProcessAsync(new JobMessage(jobId, householdId, JobTypes.CleanUpSmartPlugImportJobs, CleanUpPayload), ct);

        (await LoadAsync(provider, jobId, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ProcessAsync_an_exception_from_TryHeartbeat_does_not_stop_the_job_or_the_heartbeat_loop()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var token = DateTimeOffset.UtcNow;
        var lifecycle = Substitute.For<IBackgroundJobLifecycle>();
        lifecycle.TryStartAsync(jobId, Arg.Any<CancellationToken>()).Returns(token);
        lifecycle.TryHeartbeatAsync(jobId, token, Arg.Any<CancellationToken>()).Returns<Task<bool>>(_ => throw new InvalidOperationException("db blip"));
        lifecycle.TryCompleteAsync(jobId, token, Arg.Any<CancellationToken>()).Returns(true);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _smartPlugImportRepository.DeleteJobsAsync(householdId, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(release.Task);

        var services = new ServiceCollection();
        services.AddScoped<JobHouseholdContext>();
        services.AddSingleton(lifecycle);
        services.AddSingleton(_smartPlugImportRepository);
        services.AddSingleton(Substitute.For<IBackgroundJobRepository>());
        services.AddScoped<CleanUpSmartPlugImportJobs>();
        await using var provider = services.BuildServiceProvider();

        var run = NewProcessor(provider, FastHeartbeat)
            .ProcessAsync(new JobMessage(jobId, householdId, JobTypes.CleanUpSmartPlugImportJobs, CleanUpPayload), ct);
        for (var attempt = 0; attempt < 100 && lifecycle.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IBackgroundJobLifecycle.TryHeartbeatAsync)) < 2; attempt++)
        {
            await Task.Delay(50, ct);
        }

        release.SetResult(0);
        await run;

        lifecycle.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IBackgroundJobLifecycle.TryHeartbeatAsync))
            .ShouldBeGreaterThanOrEqualTo(2, "the loop must keep ticking after a heartbeat exception");
        await lifecycle.Received(1).TryCompleteAsync(jobId, token, Arg.Any<CancellationToken>());
    }

    private static readonly JobLifecycleTimings FastRetry = JobLifecycleTimings.Default with { TransitionRetryDelay = TimeSpan.FromMilliseconds(1) };

    private ServiceProvider BuildLifecycleSubstituteServices(IBackgroundJobLifecycle lifecycle)
    {
        var services = new ServiceCollection();
        services.AddScoped<JobHouseholdContext>();
        services.AddSingleton(lifecycle);
        services.AddSingleton(_smartPlugImportRepository);
        services.AddSingleton(Substitute.For<IBackgroundJobRepository>());
        services.AddScoped<CleanUpSmartPlugImportJobs>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ProcessAsync_retries_a_terminal_transition_that_hits_a_transient_database_error()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var token = DateTimeOffset.UtcNow;
        var lifecycle = Substitute.For<IBackgroundJobLifecycle>();
        lifecycle.TryStartAsync(jobId, Arg.Any<CancellationToken>()).Returns(token);
        lifecycle.TryCompleteAsync(jobId, token, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<bool>(new InvalidOperationException("db blip")), _ => Task.FromResult(true));
        await using var provider = BuildLifecycleSubstituteServices(lifecycle);

        await NewProcessor(provider, FastRetry)
            .ProcessAsync(new JobMessage(jobId, householdId, JobTypes.CleanUpSmartPlugImportJobs, CleanUpPayload), ct);

        await lifecycle.Received(2).TryCompleteAsync(jobId, token, Arg.Any<CancellationToken>());
        await lifecycle.DidNotReceive().TryFailAsync(jobId, token, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_retries_TryStart_after_a_transient_database_error_and_still_runs_the_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var token = DateTimeOffset.UtcNow;
        var lifecycle = Substitute.For<IBackgroundJobLifecycle>();
        lifecycle.TryStartAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<DateTimeOffset?>(new InvalidOperationException("db blip")), _ => Task.FromResult<DateTimeOffset?>(token));
        lifecycle.TryCompleteAsync(jobId, token, Arg.Any<CancellationToken>()).Returns(true);
        await using var provider = BuildLifecycleSubstituteServices(lifecycle);

        await NewProcessor(provider, FastRetry)
            .ProcessAsync(new JobMessage(jobId, householdId, JobTypes.CleanUpSmartPlugImportJobs, CleanUpPayload), ct);

        await lifecycle.Received(2).TryStartAsync(jobId, Arg.Any<CancellationToken>());
        await lifecycle.Received(1).TryCompleteAsync(jobId, token, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_gives_up_after_three_attempts_and_surfaces_the_error_for_a_persistent_database_outage()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var token = DateTimeOffset.UtcNow;
        var lifecycle = Substitute.For<IBackgroundJobLifecycle>();
        lifecycle.TryStartAsync(jobId, Arg.Any<CancellationToken>()).Returns(token);
        lifecycle.TryCompleteAsync(jobId, token, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<bool>(new InvalidOperationException("db down")));
        await using var provider = BuildLifecycleSubstituteServices(lifecycle);

        await Should.ThrowAsync<InvalidOperationException>(() => NewProcessor(provider, FastRetry)
            .ProcessAsync(new JobMessage(jobId, householdId, JobTypes.CleanUpSmartPlugImportJobs, CleanUpPayload), ct));

        await lifecycle.Received(3).TryCompleteAsync(jobId, token, Arg.Any<CancellationToken>());
    }
}
