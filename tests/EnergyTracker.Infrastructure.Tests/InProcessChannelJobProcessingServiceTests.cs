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

// Story 11.2 / AD-6 (amended): the startup sweep runs once before the channel loop reads, and a
// failing sweep (database unreachable or not yet migrated) must never stop the host nor the loop.
public class InProcessChannelJobProcessingServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private sealed class JobContextAccessor(JobHouseholdContext context) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId => context.HouseholdId;

        public Guid? HouseholdMemberId => null;
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddDbContext<EnergyTrackerDbContext>(o => o.UseNpgsql(
            _container.GetConnectionString(), n => n.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres")));
        services.AddScoped<JobHouseholdContext>();
        services.AddScoped<ICurrentHouseholdAccessor, JobContextAccessor>();
        services.AddSingleton(JobLifecycleTimings.Default);
        services.AddScoped<IBackgroundJobLifecycle, BackgroundJobLifecycle>();
        return services.BuildServiceProvider();
    }

    private static async Task SeedHouseholdAsync(ServiceProvider provider, Guid householdId, CancellationToken ct)
    {
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<JobHouseholdContext>().HouseholdId = householdId;
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        await dbContext.Database.MigrateAsync(ct);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync(ct);
    }

    private static async Task<BackgroundJob?> LoadAsync(ServiceProvider provider, Guid householdId, Guid jobId, CancellationToken ct)
    {
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<JobHouseholdContext>().HouseholdId = householdId;
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        return await dbContext.BackgroundJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, ct);
    }

    [Fact]
    public async Task ExecuteAsync_sweeps_rows_created_before_the_process_started_then_reads_the_channel()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices();
        await SeedHouseholdAsync(provider, householdId, ct);
        Guid orphanId;
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<JobHouseholdContext>().HouseholdId = householdId;
            var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
            orphanId = Guid.NewGuid();
            dbContext.BackgroundJobs.Add(new BackgroundJob
            {
                Id = orphanId, HouseholdId = householdId, JobType = "UnknownJobType", Status = BackgroundJobStatus.Queued,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            });
            await dbContext.SaveChangesAsync(ct);
        }

        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var queue = new InProcessChannelJobQueue(new BackgroundJobEnqueueRecorder(scopeFactory));
        var service = new InProcessChannelJobProcessingService(
            queue,
            new BackgroundJobProcessor(scopeFactory, JobLifecycleTimings.Default, NullLogger<BackgroundJobProcessor>.Instance),
            new InProcessJobStartupSweep(scopeFactory, NullLogger<InProcessJobStartupSweep>.Instance),
            NullLogger<InProcessChannelJobProcessingService>.Instance);
        await service.StartAsync(ct);
        try
        {
            // A message enqueued by this process is never swept and is processed (UnknownJobType -> Failed, no error text).
            var newJobId = Guid.NewGuid();
            await queue.EnqueueAsync(new JobEnvelope<string>(newJobId, householdId, "UnknownJobType", "{}", QueuedByHouseholdMemberId: null, OriginalFileName: null), ct);

            BackgroundJob? processed = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                processed = await LoadAsync(provider, householdId, newJobId, ct);
                if (processed?.Status == BackgroundJobStatus.Failed)
                {
                    break;
                }

                await Task.Delay(100, ct);
            }

            processed!.Status.ShouldBe(BackgroundJobStatus.Failed);
            processed.ErrorMessage.ShouldBeNull();
            var orphan = (await LoadAsync(provider, householdId, orphanId, ct))!;
            orphan.Status.ShouldBe(BackgroundJobStatus.Failed);
            orphan.ErrorMessage.ShouldBe("job-interrupted");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_a_throwing_sweep_is_logged_and_the_channel_is_still_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices();
        await SeedHouseholdAsync(provider, householdId, ct);

        var workingScopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var brokenScopeFactory = Substitute.For<IServiceScopeFactory>();
        brokenScopeFactory.CreateScope().Returns(_ => throw new InvalidOperationException("database unreachable"));

        var queue = new InProcessChannelJobQueue(new BackgroundJobEnqueueRecorder(workingScopeFactory));
        var service = new InProcessChannelJobProcessingService(
            queue,
            new BackgroundJobProcessor(workingScopeFactory, JobLifecycleTimings.Default, NullLogger<BackgroundJobProcessor>.Instance),
            new InProcessJobStartupSweep(brokenScopeFactory, NullLogger<InProcessJobStartupSweep>.Instance),
            NullLogger<InProcessChannelJobProcessingService>.Instance);
        await service.StartAsync(ct);
        try
        {
            var jobId = Guid.NewGuid();
            await queue.EnqueueAsync(new JobEnvelope<string>(jobId, householdId, "UnknownJobType", "{}", QueuedByHouseholdMemberId: null, OriginalFileName: null), ct);

            BackgroundJob? processed = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                processed = await LoadAsync(provider, householdId, jobId, ct);
                if (processed?.Status == BackgroundJobStatus.Failed)
                {
                    break;
                }

                await Task.Delay(100, ct);
            }

            processed!.Status.ShouldBe(BackgroundJobStatus.Failed);
            service.ExecuteTask!.IsFaulted.ShouldBeFalse();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
