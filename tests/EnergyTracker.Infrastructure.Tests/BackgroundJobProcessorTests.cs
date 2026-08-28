using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

// Story 3.6/AD-6 extension: BackgroundJobEnqueueRecorder persists a Queued row at enqueue time;
// BackgroundJobProcessor.ProcessAsync now looks that row up and transitions it, rather than
// blindly inserting a fresh Processing row.
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

    private ServiceProvider BuildServices(Guid householdId)
    {
        var services = new ServiceCollection();
        services.AddDbContext<EnergyTrackerDbContext>(o => o.UseNpgsql(
            _container.GetConnectionString(), n => n.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres")));
        services.AddScoped<JobHouseholdContext>();
        services.AddSingleton<ICurrentHouseholdAccessor>(new FixedHouseholdAccessor(householdId));
        return services.BuildServiceProvider();
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
    public async Task ProcessAsync_transitions_an_existing_Queued_row_instead_of_inserting_a_second_row()
    {
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices(householdId);
        await MigrateAsync(provider, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(provider, householdId, TestContext.Current.CancellationToken);

        var jobId = Guid.NewGuid();
        using (var seedScope = provider.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
            dbContext.BackgroundJobs.Add(new BackgroundJob
            {
                Id = jobId,
                HouseholdId = householdId,
                JobType = "UnknownJobType",
                Status = BackgroundJobStatus.Queued,
                OriginalFileName = "export.xlsx",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var processor = new BackgroundJobProcessor(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<BackgroundJobProcessor>.Instance);
        var message = new JobMessage(jobId, householdId, "UnknownJobType", "{}");

        // An unrecognized JobType throws inside ProcessAsync's own dispatch switch and is caught,
        // ending in Failed — irrelevant to what this test verifies (the row-lookup+transition
        // logic ahead of that switch, and that no second row is inserted for the same JobId).
        await processor.ProcessAsync(message, TestContext.Current.CancellationToken);

        using var verifyScope = provider.CreateScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        var rows = await verifyDbContext.BackgroundJobs.Where(j => j.Id == jobId).ToListAsync(TestContext.Current.CancellationToken);
        rows.ShouldHaveSingleItem();
        rows[0].Status.ShouldNotBe(BackgroundJobStatus.Queued);
        rows[0].CompletedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task ProcessAsync_inserts_a_fresh_row_when_no_Queued_row_exists_for_the_message_defensive_fallback()
    {
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices(householdId);
        await MigrateAsync(provider, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(provider, householdId, TestContext.Current.CancellationToken);

        var processor = new BackgroundJobProcessor(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<BackgroundJobProcessor>.Instance);
        var jobId = Guid.NewGuid();
        var message = new JobMessage(jobId, householdId, "UnknownJobType", "{}");

        await processor.ProcessAsync(message, TestContext.Current.CancellationToken);

        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        var persisted = await dbContext.BackgroundJobs.SingleAsync(j => j.Id == jobId, TestContext.Current.CancellationToken);
        persisted.Status.ShouldBe(BackgroundJobStatus.Failed);
    }

    [Fact]
    public async Task ProcessAsync_skips_a_redelivered_message_against_an_already_terminal_row()
    {
        var householdId = Guid.NewGuid();
        await using var provider = BuildServices(householdId);
        await MigrateAsync(provider, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(provider, householdId, TestContext.Current.CancellationToken);

        var jobId = Guid.NewGuid();
        var completedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        using (var seedScope = provider.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
            dbContext.BackgroundJobs.Add(new BackgroundJob
            {
                Id = jobId,
                HouseholdId = householdId,
                JobType = "UnknownJobType",
                Status = BackgroundJobStatus.Completed,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                CompletedAtUtc = completedAt,
            });
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var processor = new BackgroundJobProcessor(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<BackgroundJobProcessor>.Instance);
        var message = new JobMessage(jobId, householdId, "UnknownJobType", "{}");

        await processor.ProcessAsync(message, TestContext.Current.CancellationToken);

        using var scope = provider.CreateScope();
        var dbContext2 = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        var persisted = await dbContext2.BackgroundJobs.SingleAsync(j => j.Id == jobId, TestContext.Current.CancellationToken);
        persisted.Status.ShouldBe(BackgroundJobStatus.Completed);
        persisted.CompletedAtUtc.ShouldBe(completedAt);
    }
}
