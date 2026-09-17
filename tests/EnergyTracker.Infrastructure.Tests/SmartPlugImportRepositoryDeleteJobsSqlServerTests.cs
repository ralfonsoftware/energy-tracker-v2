using System.Data.Common;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    // Round-3 review finding (Blind Hunter): the Postgres sibling test asserts an exact command
    // count (not just final row counts) to catch a "wired with count/volume arguments swapped,
    // still coincidentally correct" bug; this file's version of the same scenario didn't, against
    // the one provider (SqlServer) both real incidents actually hit. Small, counting-only
    // duplicate of SmartPlugImportRepositoryTests.cs's FailAfterCommandCountInterceptor — no
    // failure-injection needed here, so it's simpler than that one.
    private sealed class CountingInterceptor : DbCommandInterceptor
    {
        private int _completed;

        public int CompletedCount => Volatile.Read(ref _completed);

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _completed);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    // No migration call here deliberately — the caller must migrate first via
    // OpenMigratedDbContextAsync on an already-schema'd container/database, so the interceptor
    // attached here only ever observes the repository's own commands, never MigrateAsync's DDL.
    private EnergyTrackerDbContext OpenDbContextWithInterceptor(Guid householdId, IInterceptor interceptor)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer"));
        optionsBuilder.AddInterceptors(interceptor);
        return new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
    }

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

    [Fact]
    public async Task DeleteJobsAsync_with_no_cutoff_deletes_everything_when_a_few_imports_individually_exceed_the_reading_volume_threshold_on_SqlServer()
    {
        // Round-2 review finding (Blind Hunter): CAP-4's own success criterion requires SqlServer
        // verification (the actual production provider both incidents hit) and this file exists
        // specifically for that reason, but the first round-2 pass only added the reading-volume
        // scenario to the Postgres test file. Round-3 review finding (Blind Hunter): that first
        // SqlServer version was still materially weaker than its Postgres sibling — no gap seeded,
        // no command-count assertion. Now a full mirror, including both.
        const int readingsPerLargeImport = 8_000; // 3 * 8,000 = 24,000 > DeleteReadingVolumeThreshold (20,000)
        var householdId = Guid.NewGuid();
        var allJobIds = new List<Guid>();
        var allReadingIds = new List<Guid>();

        await using (var seedDbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken))
        {
            seedDbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });

            void SeedImportWithReadings(int readingCount, string deviceTag)
            {
                var job = new BackgroundJob
                {
                    Id = Guid.NewGuid(), HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
                    Status = BackgroundJobStatus.Completed, CreatedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = DateTimeOffset.UtcNow,
                };
                seedDbContext.BackgroundJobs.Add(job);
                var import = MakeImport(householdId, job.Id, deviceTag);
                seedDbContext.SmartPlugImports.Add(import);
                seedDbContext.SmartPlugImportGaps.Add(new SmartPlugImportGap
                {
                    Id = Guid.NewGuid(), HouseholdId = householdId, SmartPlugImportId = import.Id, PowerPointId = null,
                    StartDate = DateOnly.FromDateTime(DateTime.UtcNow), EndDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    Treatment = SmartPlugImportGapTreatment.FlaggedForReview, EstimatedTotalKwh = null, CreatedAtUtc = DateTimeOffset.UtcNow,
                });
                allJobIds.Add(job.Id);
                for (var i = 0; i < readingCount; i++)
                {
                    var reading = MakeReading(householdId, import.Id, DateTimeOffset.UtcNow.AddMinutes(-i));
                    seedDbContext.SmartPlugReadings.Add(reading);
                    allReadingIds.Add(reading.Id);
                }
            }

            for (var i = 0; i < 3; i++)
            {
                SeedImportWithReadings(readingsPerLargeImport, $"Large-{i}");
            }
            for (var i = 0; i < 2; i++)
            {
                SeedImportWithReadings(10, $"Small-{i}");
            }
            await seedDbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Exact expected count proven the same way as the Postgres sibling test: any 2 of the 3
        // "large" (8,000-reading) imports fit in one chunk together, a 3rd never does regardless of
        // what else is already in that chunk — always exactly 2 import-chunks (4 commands) + 1
        // jobs-chunk (1 command) = 5 from chunking alone, regardless of retrieval order. Round-4
        // incident fix adds a per-import reading-detach loop: ceil(8000/200)=40 UPDATE commands for
        // each of the 3 large imports (120) + ceil(10/200)=1 for each of the 2 small imports (2) =
        // 122, independent of which chunk an import lands in. Total: 5 + 122 = 127.
        var interceptor = new CountingInterceptor();
        await using var dbContext = OpenDbContextWithInterceptor(householdId, interceptor);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        var deletedCount = await repository.DeleteJobsAsync(householdId, cutoffUtc: null, TestContext.Current.CancellationToken);

        deletedCount.ShouldBe(5);
        interceptor.CompletedCount.ShouldBe(127);
        await using var verifyDbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        (await verifyDbContext.BackgroundJobs.CountAsync(j => allJobIds.Contains(j.Id), TestContext.Current.CancellationToken)).ShouldBe(0);
        (await verifyDbContext.SmartPlugImportGaps.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        var survivingReadingCount = await verifyDbContext.SmartPlugReadings
            .CountAsync(r => r.SmartPlugImportId == null, TestContext.Current.CancellationToken);
        survivingReadingCount.ShouldBe(allReadingIds.Count);
    }

    [Fact]
    public async Task DeleteJobsAsync_bounds_a_single_import_whose_own_reading_count_exceeds_the_volume_threshold_into_batched_detach_commands_on_SqlServer()
    {
        // Round-4 incident fix (2026-09-12 20:40 prod): the test above only pairs "large" imports
        // that still fit together in one chunk (8,000 each, 2 fit under the 20,000 threshold). The
        // actual incident hit a *single* import (122,158 rows) isolated alone in its own chunk —
        // the case none of round 2's SqlServer coverage exercised, and the one where the FK's
        // SetNull cascade (not import-count or volume chunking) was the real bottleneck. This is
        // the SqlServer mirror of the Postgres reproduction — SqlServer is the actual production
        // provider every round of this incident has hit.
        const int readingCount = SmartPlugImportRepository.DeleteReadingVolumeThreshold + 1; // guarantees this import lands alone in its own chunk
        var householdId = Guid.NewGuid();
        Guid jobId, importId;
        var readingIds = new List<Guid>(readingCount);

        await using (var seedDbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken))
        {
            seedDbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
            var job = new BackgroundJob
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
                Status = BackgroundJobStatus.Completed, CreatedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = DateTimeOffset.UtcNow,
            };
            seedDbContext.BackgroundJobs.Add(job);
            var import = MakeImport(householdId, job.Id, deviceTag: "Huge-import");
            seedDbContext.SmartPlugImports.Add(import);
            seedDbContext.SmartPlugImportGaps.Add(new SmartPlugImportGap
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, SmartPlugImportId = import.Id, PowerPointId = null,
                StartDate = DateOnly.FromDateTime(DateTime.UtcNow), EndDate = DateOnly.FromDateTime(DateTime.UtcNow),
                Treatment = SmartPlugImportGapTreatment.FlaggedForReview, EstimatedTotalKwh = null, CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            for (var i = 0; i < readingCount; i++)
            {
                var reading = MakeReading(householdId, import.Id, DateTimeOffset.UtcNow.AddMinutes(-i));
                seedDbContext.SmartPlugReadings.Add(reading);
                readingIds.Add(reading.Id);
            }
            await seedDbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobId = job.Id;
            importId = import.Id;
        }

        // 1 SmartPlugImportGaps delete + ceil(20001/200)=101 per-import reading-detach UPDATEs
        // (DetachReadingsForImportAsync) + 1 SmartPlugImports delete + 1 BackgroundJobs delete
        // (1 job, trivially under DeleteBatchSize) = 104 commands total, none of them a single
        // unbounded SetNull cascade over all 20,001 rows.
        var interceptor = new CountingInterceptor();
        await using var dbContext = OpenDbContextWithInterceptor(householdId, interceptor);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        var deletedCount = await repository.DeleteJobsAsync(householdId, cutoffUtc: null, TestContext.Current.CancellationToken);

        deletedCount.ShouldBe(1);
        interceptor.CompletedCount.ShouldBe(104);
        await using var verifyDbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        (await verifyDbContext.BackgroundJobs.CountAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldBe(0);
        (await verifyDbContext.SmartPlugImports.CountAsync(i => i.Id == importId, TestContext.Current.CancellationToken)).ShouldBe(0);
        (await verifyDbContext.SmartPlugImportGaps.CountAsync(g => g.SmartPlugImportId == importId, TestContext.Current.CancellationToken)).ShouldBe(0);
        var survivingReadings = await verifyDbContext.SmartPlugReadings
            .Where(r => readingIds.Contains(r.Id)).ToListAsync(TestContext.Current.CancellationToken);
        survivingReadings.Count.ShouldBe(readingCount);
        survivingReadings.ShouldAllBe(r => r.SmartPlugImportId == null);
    }

    // CAP-6 (spec-3-10-cleanup-sweep-async, 2026-09-17): SweepExpiredAsync's new bounded-per-call
    // path added a provider-specific concurrency guard (sp_getapplock on SqlServer,
    // pg_try_advisory_xact_lock on Postgres) with no prior SqlServer coverage at all — unlike
    // DeleteJobsAsync above, SweepExpiredAsync wasn't previously exercised against this file's
    // provider anywhere. This is the one targeted test proving the actual provider-specific branch
    // (TryAcquireHouseholdSweepLockSqlServerAsync's sp_getapplock call) behaves correctly against
    // the real production provider, mirroring SmartPlugImportRepositoryTests.cs's Postgres
    // equivalent (SweepExpiredAsync_no_ops_instead_of_blocking_when_another_poll_already_holds_the_
    // households_sweep_lock) rather than duplicating this file's full DeleteJobsAsync suite.
    [Fact]
    public async Task SweepExpiredAsync_no_ops_instead_of_blocking_when_another_poll_already_holds_the_households_sweep_lock_on_SqlServer()
    {
        var householdId = Guid.NewGuid();
        Guid jobId, importId;

        await using (var seedDbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken))
        {
            seedDbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
            var job = new BackgroundJob
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
                Status = BackgroundJobStatus.Completed, CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-40),
                CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-31),
            };
            seedDbContext.BackgroundJobs.Add(job);
            var import = MakeImport(householdId, job.Id, deviceTag: "Fridge");
            import.CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-31);
            seedDbContext.SmartPlugImports.Add(import);
            await seedDbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobId = job.Id;
            importId = import.Id;
        }

        await using var holderDbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await using var holderTransaction = await holderDbContext.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        // ToListAsync(), not SingleAsync() — this DECLARE/EXEC/SELECT batch is non-composable SQL,
        // and SqlQuery<T>().SingleAsync() throws trying to compose (wrap it in a subquery); see the
        // matching fix in TryAcquireHouseholdSweepLockSqlServerAsync.
        var lockResults = await holderDbContext.Database
            .SqlQuery<int>($"""
                DECLARE @LockResult int;
                EXEC @LockResult = sp_getapplock
                    @Resource = {householdId.ToString()},
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 0;
                SELECT @LockResult AS "Value";
                """)
            .ToListAsync(TestContext.Current.CancellationToken);
        lockResults.Single().ShouldBeGreaterThanOrEqualTo(0);

        await using var pollingDbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var pollingRepository = new SmartPlugImportRepository(pollingDbContext, new AuditCorrectionRecorder(pollingDbContext), NullLogger<SmartPlugImportRepository>.Instance);

        await pollingRepository.SweepExpiredAsync(householdId, DateTimeOffset.UtcNow.AddDays(-30), TestContext.Current.CancellationToken);

        // The polling call no-opped (lock unavailable) rather than blocking on or deleting rows the
        // holder transaction still has locked.
        (await pollingDbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await pollingDbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.Id == importId, TestContext.Current.CancellationToken)).ShouldNotBeNull();

        await holderTransaction.RollbackAsync(TestContext.Current.CancellationToken);

        // With the holder's lock released, the next poll succeeds normally.
        await pollingRepository.SweepExpiredAsync(householdId, DateTimeOffset.UtcNow.AddDays(-30), TestContext.Current.CancellationToken);

        (await pollingDbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await pollingDbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.Id == importId, TestContext.Current.CancellationToken)).ShouldBeNull();
    }
}
