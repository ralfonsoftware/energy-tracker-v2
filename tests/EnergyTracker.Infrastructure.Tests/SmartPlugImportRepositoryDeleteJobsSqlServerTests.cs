using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Testcontainers.MsSql;

namespace EnergyTracker.Infrastructure.Tests;

// Round-1 review finding (Blind Hunter) on the 2026-09-12 incident fix
// (spec-3-10-cleanup-batch-delete-fix.md): the batched DeleteEligibleAsync chunking/atomicity
// logic is pure portable EF Core LINQ with no provider-specific path, matching this test project's
// established convention that Postgres-only Testcontainers coverage suffices for that class of
// change (see SmartPlugImportRepositoryTests.cs). But this specific incident hit the SqlServer-
// hosted production database, and the canonical spec
// (_bmad-artifacts/specs/spec-job-cleanup-bulk-delete-timeout/SPEC.md, CAP-1) explicitly requires
// verification against both providers — so this one targeted test proves the multi-batch delete
// against the real production provider, deliberately without duplicating the full Postgres suite.
public class SmartPlugImportRepositoryDeleteJobsSqlServerTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private sealed class FixedHouseholdAccessor(Guid householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;

        public Guid? HouseholdMemberId => null;
    }

    private async Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer"));
        var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
        await dbContext.Database.MigrateAsync(cancellationToken);
        return dbContext;
    }

    private static SmartPlugImport MakeImport(Guid householdId, Guid backgroundJobId, string deviceTag) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        BackgroundJobId = backgroundJobId,
        VendorFormat = SmartPlugVendorFormat.EveHome,
        OriginalFileName = "export.xlsx",
        Status = SmartPlugImportStatus.Completed,
        DeviceTag = deviceTag,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        CompletedAtUtc = DateTimeOffset.UtcNow,
    };

    private static SmartPlugReading MakeReading(Guid householdId, Guid smartPlugImportId, DateTimeOffset intervalStart) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        SmartPlugImportId = smartPlugImportId,
        PowerPointId = null,
        RoomName = "Kitchen",
        PowerPointName = "Fridge",
        DeviceName = "Fridge",
        IntervalStart = intervalStart,
        IntervalEnd = intervalStart,
        KwhValue = 0.5m,
    };

    [Fact]
    public async Task DeleteJobsAsync_with_no_cutoff_deletes_every_eligible_row_across_multiple_batches_on_SqlServer()
    {
        // Mirrors SmartPlugImportRepositoryTests.cs's Postgres version of this test, including the
        // reading-detach assertion (round-2 review finding: an earlier version of this file didn't
        // seed readings, so the one test proving real-provider behavior skipped the exact SetNull
        // cascade the whole fix exists to bound). DeleteBatchSize + 1 guarantees two chunks for
        // both the importIds and jobIds loops.
        const int seedCount = SmartPlugImportRepository.DeleteBatchSize + 1;
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        var jobIds = new List<Guid>(seedCount);
        var importIds = new List<Guid>(seedCount);
        var readingIds = new List<Guid>(seedCount);
        for (var i = 0; i < seedCount; i++)
        {
            var job = new BackgroundJob
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
                Status = BackgroundJobStatus.Completed, CreatedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = DateTimeOffset.UtcNow,
            };
            dbContext.BackgroundJobs.Add(job);
            var import = MakeImport(householdId, job.Id, deviceTag: $"Fridge-{i}");
            dbContext.SmartPlugImports.Add(import);
            dbContext.SmartPlugImportGaps.Add(new SmartPlugImportGap
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, SmartPlugImportId = import.Id, PowerPointId = null,
                StartDate = DateOnly.FromDateTime(DateTime.UtcNow), EndDate = DateOnly.FromDateTime(DateTime.UtcNow),
                Treatment = SmartPlugImportGapTreatment.FlaggedForReview, EstimatedTotalKwh = null, CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            var reading = MakeReading(householdId, import.Id, DateTimeOffset.UtcNow.AddMinutes(-i));
            dbContext.SmartPlugReadings.Add(reading);
            jobIds.Add(job.Id);
            importIds.Add(import.Id);
            readingIds.Add(reading.Id);
        }
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        var deletedCount = await repository.DeleteJobsAsync(householdId, cutoffUtc: null, TestContext.Current.CancellationToken);

        deletedCount.ShouldBe(seedCount);
        await using var verifyDbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        (await verifyDbContext.BackgroundJobs.CountAsync(j => jobIds.Contains(j.Id), TestContext.Current.CancellationToken)).ShouldBe(0);
        (await verifyDbContext.SmartPlugImports.CountAsync(i => importIds.Contains(i.Id), TestContext.Current.CancellationToken)).ShouldBe(0);
        (await verifyDbContext.SmartPlugImportGaps.CountAsync(g => importIds.Contains(g.SmartPlugImportId), TestContext.Current.CancellationToken)).ShouldBe(0);
        var survivingReadings = await verifyDbContext.SmartPlugReadings
            .Where(r => readingIds.Contains(r.Id)).ToListAsync(TestContext.Current.CancellationToken);
        survivingReadings.Count.ShouldBe(seedCount);
        survivingReadings.ShouldAllBe(r => r.SmartPlugImportId == null);
    }
}
