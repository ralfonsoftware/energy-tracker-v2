using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

// Story 11.2 (AC #7): terminal CorrelateEvent BackgroundJob rows (one per Meter Reading write,
// Story 10.2) are swept with the existing 30-day retention. Postgres is enough here: the delete
// uses portable LINQ only (Take for ids, then ExecuteDelete over Contains).
public class BackgroundJobRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private sealed class FixedHouseholdAccessor(Guid householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;

        public Guid? HouseholdMemberId => null;
    }

    private async Task<EnergyTrackerDbContext> OpenAsync(Guid householdId, CancellationToken ct)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));
        var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
        await dbContext.Database.MigrateAsync(ct);
        return dbContext;
    }

    private static BackgroundJob Job(Guid householdId, string jobType, BackgroundJobStatus status, DateTimeOffset? completedAt) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        JobType = jobType,
        Status = status,
        CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-60),
        CompletedAtUtc = completedAt,
    };

    [Fact]
    public async Task DeleteTerminalByJobTypeAsync_deletes_only_old_terminal_rows_of_that_type_in_that_Household()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        var otherHouseholdId = Guid.NewGuid();
        await using var dbContext = await OpenAsync(householdId, ct);
        await using var otherContext = await OpenAsync(otherHouseholdId, ct);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        dbContext.Households.Add(new Household { Id = otherHouseholdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var oldCompleted = Job(householdId, JobTypes.CorrelateEvent, BackgroundJobStatus.Completed, now.AddDays(-40));
        var oldFailed = Job(householdId, JobTypes.CorrelateEvent, BackgroundJobStatus.Failed, now.AddDays(-31));
        var recent = Job(householdId, JobTypes.CorrelateEvent, BackgroundJobStatus.Completed, now.AddDays(-5));
        var queued = Job(householdId, JobTypes.CorrelateEvent, BackgroundJobStatus.Queued, null);
        var processing = Job(householdId, JobTypes.CorrelateEvent, BackgroundJobStatus.Processing, null);
        var oldImport = Job(householdId, JobTypes.ProcessSmartPlugImport, BackgroundJobStatus.Completed, now.AddDays(-40));
        dbContext.BackgroundJobs.AddRange(oldCompleted, oldFailed, recent, queued, processing, oldImport);
        var otherHouseholdOld = Job(otherHouseholdId, JobTypes.CorrelateEvent, BackgroundJobStatus.Completed, now.AddDays(-40));
        otherContext.BackgroundJobs.Add(otherHouseholdOld);
        await dbContext.SaveChangesAsync(ct);
        await otherContext.SaveChangesAsync(ct);
        var repository = new BackgroundJobRepository(dbContext);

        var deleted = await repository.DeleteTerminalByJobTypeAsync(householdId, JobTypes.CorrelateEvent, now.AddDays(-30), ct);

        deleted.ShouldBe(2);
        var remainingIds = await dbContext.BackgroundJobs.AsNoTracking().Select(j => j.Id).ToListAsync(ct);
        remainingIds.ShouldBe([recent.Id, queued.Id, processing.Id, oldImport.Id], ignoreOrder: true);
        (await otherContext.BackgroundJobs.AsNoTracking().AnyAsync(j => j.Id == otherHouseholdOld.Id, ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteTerminalByJobTypeAsync_with_no_cutoff_deletes_every_terminal_row_but_never_an_active_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenAsync(householdId, ct);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        var recent = Job(householdId, JobTypes.CorrelateEvent, BackgroundJobStatus.Completed, DateTimeOffset.UtcNow.AddMinutes(-1));
        var queued = Job(householdId, JobTypes.CorrelateEvent, BackgroundJobStatus.Queued, null);
        dbContext.BackgroundJobs.AddRange(recent, queued);
        await dbContext.SaveChangesAsync(ct);

        var deleted = await new BackgroundJobRepository(dbContext).DeleteTerminalByJobTypeAsync(householdId, JobTypes.CorrelateEvent, null, ct);

        deleted.ShouldBe(1);
        (await dbContext.BackgroundJobs.AsNoTracking().Select(j => j.Id).ToListAsync(ct)).ShouldBe([queued.Id]);
    }

    [Fact]
    public async Task DeleteTerminalByJobTypeAsync_deletes_at_most_one_batch_of_200_per_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenAsync(householdId, ct);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        for (var i = 0; i < 205; i++)
        {
            dbContext.BackgroundJobs.Add(Job(householdId, JobTypes.CorrelateEvent, BackgroundJobStatus.Completed, DateTimeOffset.UtcNow.AddDays(-40).AddMinutes(i)));
        }

        await dbContext.SaveChangesAsync(ct);
        var repository = new BackgroundJobRepository(dbContext);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);

        (await repository.DeleteTerminalByJobTypeAsync(householdId, JobTypes.CorrelateEvent, cutoff, ct)).ShouldBe(200);
        (await dbContext.BackgroundJobs.AsNoTracking().CountAsync(ct)).ShouldBe(5);
        (await repository.DeleteTerminalByJobTypeAsync(householdId, JobTypes.CorrelateEvent, cutoff, ct)).ShouldBe(5);
        (await dbContext.BackgroundJobs.AsNoTracking().CountAsync(ct)).ShouldBe(0);
    }
}
