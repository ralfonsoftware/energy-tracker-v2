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

// Story 11.2 / AD-6 (amended) / AD-24: the in-process queue loses its messages on restart, so at
// startup each Household's Queued/Processing rows created before this process started are failed
// as job-interrupted. SQL correctness is proven on both providers in BackgroundJobLifecycleTests;
// this proves the per-Household scoping and the error isolation of the orchestration.
public class InProcessJobStartupSweepTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    // Mirrors CurrentHouseholdAccessor's job-path behaviour: resolved once per scope and cached,
    // so a sweep that reused one scope for several Households would apply the first Household's
    // query filter to everyone.
    private sealed class CachingJobContextAccessor(JobHouseholdContext context) : ICurrentHouseholdAccessor
    {
        private Guid? _cached;
        private bool _resolved;

        public Guid? HouseholdId
        {
            get
            {
                if (!_resolved)
                {
                    _cached = context.HouseholdId;
                    _resolved = true;
                }

                return _cached;
            }
        }

        public Guid? HouseholdMemberId => null;
    }

    private ServiceProvider BuildServices(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<EnergyTrackerDbContext>(o => o.UseNpgsql(
            _container.GetConnectionString(), n => n.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres")));
        services.AddScoped<JobHouseholdContext>();
        services.AddScoped<ICurrentHouseholdAccessor, CachingJobContextAccessor>();
        services.AddSingleton(JobLifecycleTimings.Default);
        services.AddScoped<IBackgroundJobLifecycle, BackgroundJobLifecycle>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static async Task SeedHouseholdAsync(ServiceProvider provider, Guid householdId, CancellationToken ct)
    {
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<JobHouseholdContext>().HouseholdId = householdId;
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync(ct);
    }

    private static async Task<Guid> SeedJobAsync(
        ServiceProvider provider, Guid householdId, BackgroundJobStatus status, DateTimeOffset createdAt, CancellationToken ct)
    {
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<JobHouseholdContext>().HouseholdId = householdId;
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        var job = new BackgroundJob
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, JobType = "UnknownJobType", Status = status, CreatedAtUtc = createdAt,
        };
        dbContext.BackgroundJobs.Add(job);
        await dbContext.SaveChangesAsync(ct);
        return job.Id;
    }

    private static async Task<BackgroundJob> LoadAsync(ServiceProvider provider, Guid householdId, Guid jobId, CancellationToken ct)
    {
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<JobHouseholdContext>().HouseholdId = householdId;
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        return await dbContext.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == jobId, ct);
    }

    [Fact]
    public async Task SweepAsync_fails_only_the_rows_created_before_the_cutoff_in_each_Household()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildServices();
        using (var migrate = provider.CreateScope())
        {
            await migrate.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>().Database.MigrateAsync(ct);
        }

        var cutoff = DateTimeOffset.UtcNow;
        var early = cutoff.AddMinutes(-10);
        var late = cutoff.AddMinutes(1);
        var households = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var rows = new Dictionary<Guid, (Guid EarlyQueued, Guid EarlyProcessing, Guid LateQueued, Guid LateProcessing)>();
        foreach (var householdId in households)
        {
            await SeedHouseholdAsync(provider, householdId, ct);
            rows[householdId] = (
                await SeedJobAsync(provider, householdId, BackgroundJobStatus.Queued, early, ct),
                await SeedJobAsync(provider, householdId, BackgroundJobStatus.Processing, early, ct),
                await SeedJobAsync(provider, householdId, BackgroundJobStatus.Queued, late, ct),
                await SeedJobAsync(provider, householdId, BackgroundJobStatus.Processing, late, ct));
        }

        var sweep = new InProcessJobStartupSweep(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<InProcessJobStartupSweep>.Instance);

        await sweep.SweepAsync(cutoff, ct);

        foreach (var householdId in households)
        {
            var (earlyQueued, earlyProcessing, lateQueued, lateProcessing) = rows[householdId];
            foreach (var id in new[] { earlyQueued, earlyProcessing })
            {
                var swept = await LoadAsync(provider, householdId, id, ct);
                swept.Status.ShouldBe(BackgroundJobStatus.Failed);
                swept.ErrorMessage.ShouldBe("job-interrupted");
                swept.CompletedAtUtc.ShouldNotBeNull();
            }

            (await LoadAsync(provider, householdId, lateQueued, ct)).Status.ShouldBe(BackgroundJobStatus.Queued);
            (await LoadAsync(provider, householdId, lateProcessing, ct)).Status.ShouldBe(BackgroundJobStatus.Processing);
        }
    }

    [Fact]
    public async Task SweepAsync_a_Household_whose_sweep_throws_does_not_stop_the_others()
    {
        var ct = TestContext.Current.CancellationToken;
        var failing = Guid.NewGuid();
        var healthy = Guid.NewGuid();
        await using var provider = BuildServices(services =>
        {
            services.AddScoped<IBackgroundJobLifecycle>(sp =>
            {
                var householdId = sp.GetRequiredService<JobHouseholdContext>().HouseholdId;
                if (householdId == failing)
                {
                    var throwing = Substitute.For<IBackgroundJobLifecycle>();
                    throwing.FailInterruptedBeforeAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                        .Returns<Task<int>>(_ => throw new InvalidOperationException("boom"));
                    return throwing;
                }

                return new BackgroundJobLifecycle(sp.GetRequiredService<EnergyTrackerDbContext>(), JobLifecycleTimings.Default);
            });
        });
        using (var migrate = provider.CreateScope())
        {
            await migrate.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>().Database.MigrateAsync(ct);
        }

        await SeedHouseholdAsync(provider, failing, ct);
        await SeedHouseholdAsync(provider, healthy, ct);
        var cutoff = DateTimeOffset.UtcNow;
        var healthyJob = await SeedJobAsync(provider, healthy, BackgroundJobStatus.Queued, cutoff.AddMinutes(-5), ct);
        var sweep = new InProcessJobStartupSweep(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<InProcessJobStartupSweep>.Instance);

        await sweep.SweepAsync(cutoff, ct);

        (await LoadAsync(provider, healthy, healthyJob, ct)).Status.ShouldBe(BackgroundJobStatus.Failed);
    }
}
