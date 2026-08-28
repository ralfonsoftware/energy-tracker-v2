using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

public class PostgresMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private sealed class FixedHouseholdAccessor(Guid householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;

        public Guid? HouseholdMemberId => null;
    }

    // Story 3.6/AD-6 extension added OriginalFileName/QueuedByHouseholdMemberId to BackgroundJob.
    // Tests below seed data at a migration checkpoint BEFORE that migration — EF's own
    // (current-model) tracked insert would reference columns that don't exist yet in the
    // physical schema at that point in history, so this raw INSERT is scoped to only the columns
    // that checkpoint's schema actually has. The Household row this job's HouseholdId FK
    // references must already be committed (not merely tracked) before this runs.
    private static async Task InsertBackgroundJobPreStory36Async(EnergyTrackerDbContext dbContext, BackgroundJob job, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "BackgroundJobs" ("Id", "HouseholdId", "JobType", "Status", "CreatedAtUtc", "CompletedAtUtc")
            VALUES ({job.Id}, {job.HouseholdId}, {job.JobType}, {(int)job.Status}, {job.CreatedAtUtc}, {job.CompletedAtUtc})
            """, cancellationToken);

    [Fact]
    public async Task Postgres_migrations_apply_cleanly_to_a_real_database()
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));

        await using var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, null!);

        await dbContext.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var appliedMigrations = await dbContext.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        appliedMigrations.ShouldContain(m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
        appliedMigrations.ShouldContain(m => m.EndsWith("_AddHouseholdAndDataProtectionKeys", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddSmartPlugReadingUniqueIndex_migration_dedups_existing_duplicates_and_the_new_index_rejects_a_fresh_one()
    {
        // Story 3.4 AC #8/#9/#10 — seeds duplicate (PowerPointId, IntervalStart) rows across two
        // different SmartPlugImports (different CreatedAtUtc) BEFORE the migration under test is
        // applied (migrate up to the one immediately before it, insert data, then apply it), then
        // asserts exactly one row per duplicate group survives — the one belonging to the
        // more-recently-created import — and that the new unique index now rejects a fresh
        // duplicate insert attempt.
        var householdId = Guid.NewGuid();
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));

        await using var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
        var migrator = dbContext.GetInfrastructure().GetRequiredService<IMigrator>();

        await migrator.MigrateAsync("20260820102446_AddSmartPlugImportGaps", TestContext.Current.CancellationToken);

        var now = DateTimeOffset.UtcNow;
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = now });
        // Committed alone, ahead of the raw BackgroundJob inserts below, whose HouseholdId FK
        // requires the row to already exist (not merely tracked).
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var room = new Room { Id = Guid.NewGuid(), HouseholdId = householdId, Name = "Kitchen", CreatedAtUtc = now };
        dbContext.Rooms.Add(room);
        var powerPoint = new PowerPoint { Id = Guid.NewGuid(), HouseholdId = householdId, RoomId = room.Id, Name = "Fridge", CreatedAtUtc = now };
        dbContext.PowerPoints.Add(powerPoint);
        var olderJob = new BackgroundJob
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Completed, CreatedAtUtc = now, CompletedAtUtc = now,
        };
        var newerJob = new BackgroundJob
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Completed, CreatedAtUtc = now, CompletedAtUtc = now,
        };
        await InsertBackgroundJobPreStory36Async(dbContext, olderJob, TestContext.Current.CancellationToken);
        await InsertBackgroundJobPreStory36Async(dbContext, newerJob, TestContext.Current.CancellationToken);
        var olderImport = new SmartPlugImport
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, BackgroundJobId = olderJob.Id,
            VendorFormat = SmartPlugVendorFormat.EveHome, OriginalFileName = "older.xlsx",
            Status = SmartPlugImportStatus.Completed, DeviceTag = "Fridge",
            CreatedAtUtc = now.AddDays(-1), CompletedAtUtc = now.AddDays(-1),
        };
        var newerImport = new SmartPlugImport
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, BackgroundJobId = newerJob.Id,
            VendorFormat = SmartPlugVendorFormat.EveHome, OriginalFileName = "newer.xlsx",
            Status = SmartPlugImportStatus.Completed, DeviceTag = "Fridge",
            CreatedAtUtc = now, CompletedAtUtc = now,
        };
        dbContext.SmartPlugImports.AddRange(olderImport, newerImport);
        var duplicateIntervalStart = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var survivorId = Guid.NewGuid();
        // Dev Notes Open Question #3 ("fix it now"): also seed a duplicate pair of
        // AwaitingPowerPointMapping rows (PowerPointId IS NULL) sharing an IntervalStart, across
        // the same two imports — the composite index above never protects these; the migration's
        // second, NULL-scoped cleanup + partial unique index does.
        var nullPowerPointSurvivorId = Guid.NewGuid();
        dbContext.SmartPlugReadings.AddRange(
            new SmartPlugReading
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, SmartPlugImportId = olderImport.Id, PowerPointId = powerPoint.Id,
                RoomName = "Kitchen", PowerPointName = "Fridge", DeviceName = "Fridge",
                IntervalStart = duplicateIntervalStart, IntervalEnd = duplicateIntervalStart, KwhValue = 0.1m,
            },
            new SmartPlugReading
            {
                Id = survivorId, HouseholdId = householdId, SmartPlugImportId = newerImport.Id, PowerPointId = powerPoint.Id,
                RoomName = "Kitchen", PowerPointName = "Fridge", DeviceName = "Fridge",
                IntervalStart = duplicateIntervalStart, IntervalEnd = duplicateIntervalStart, KwhValue = 0.2m,
            },
            new SmartPlugReading
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, SmartPlugImportId = olderImport.Id, PowerPointId = null,
                RoomName = string.Empty, PowerPointName = "Unmapped Plug", DeviceName = "Unmapped Plug",
                IntervalStart = duplicateIntervalStart, IntervalEnd = duplicateIntervalStart, KwhValue = 0.4m,
            },
            new SmartPlugReading
            {
                Id = nullPowerPointSurvivorId, HouseholdId = householdId, SmartPlugImportId = newerImport.Id, PowerPointId = null,
                RoomName = string.Empty, PowerPointName = "Unmapped Plug", DeviceName = "Unmapped Plug",
                IntervalStart = duplicateIntervalStart, IntervalEnd = duplicateIntervalStart, KwhValue = 0.5m,
            });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        var survivingReadings = await dbContext.SmartPlugReadings
            .Where(r => r.PowerPointId == powerPoint.Id && r.IntervalStart == duplicateIntervalStart)
            .ToListAsync(TestContext.Current.CancellationToken);
        survivingReadings.Count.ShouldBe(1);
        survivingReadings[0].Id.ShouldBe(survivorId);
        survivingReadings[0].SmartPlugImportId.ShouldBe(newerImport.Id);

        var survivingNullPowerPointReadings = await dbContext.SmartPlugReadings
            .Where(r => r.PowerPointId == null && r.IntervalStart == duplicateIntervalStart)
            .ToListAsync(TestContext.Current.CancellationToken);
        survivingNullPowerPointReadings.Count.ShouldBe(1);
        survivingNullPowerPointReadings[0].Id.ShouldBe(nullPowerPointSurvivorId);
        survivingNullPowerPointReadings[0].SmartPlugImportId.ShouldBe(newerImport.Id);

        dbContext.SmartPlugReadings.Add(new SmartPlugReading
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, SmartPlugImportId = newerImport.Id, PowerPointId = powerPoint.Id,
            RoomName = "Kitchen", PowerPointName = "Fridge", DeviceName = "Fridge",
            IntervalStart = duplicateIntervalStart, IntervalEnd = duplicateIntervalStart, KwhValue = 0.3m,
        });
        await Should.ThrowAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(TestContext.Current.CancellationToken));
        dbContext.ChangeTracker.Clear();

        dbContext.SmartPlugReadings.Add(new SmartPlugReading
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, SmartPlugImportId = newerImport.Id, PowerPointId = null,
            RoomName = string.Empty, PowerPointName = "Unmapped Plug", DeviceName = "Unmapped Plug",
            IntervalStart = duplicateIntervalStart, IntervalEnd = duplicateIntervalStart, KwhValue = 0.6m,
        });
        await Should.ThrowAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(TestContext.Current.CancellationToken));
        dbContext.ChangeTracker.Clear();

        // Regression guard for the bug this test caught during dev-story activation: a DIFFERENT
        // Household's unmapped reading at the exact same IntervalStart must NOT collide — the
        // partial index is keyed by (HouseholdId, IntervalStart), not IntervalStart alone.
        var otherHouseholdId = Guid.NewGuid();
        dbContext.Households.Add(new Household { Id = otherHouseholdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = now });
        var otherJob = new BackgroundJob
        {
            Id = Guid.NewGuid(), HouseholdId = otherHouseholdId, JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Completed, CreatedAtUtc = now, CompletedAtUtc = now,
        };
        dbContext.BackgroundJobs.Add(otherJob);
        var otherImport = new SmartPlugImport
        {
            Id = Guid.NewGuid(), HouseholdId = otherHouseholdId, BackgroundJobId = otherJob.Id,
            VendorFormat = SmartPlugVendorFormat.EveHome, OriginalFileName = "other-household.xlsx",
            Status = SmartPlugImportStatus.AwaitingPowerPointMapping, DeviceTag = "Unmapped Plug",
            CreatedAtUtc = now, CompletedAtUtc = now,
        };
        dbContext.SmartPlugImports.Add(otherImport);
        dbContext.SmartPlugReadings.Add(new SmartPlugReading
        {
            Id = Guid.NewGuid(), HouseholdId = otherHouseholdId, SmartPlugImportId = otherImport.Id, PowerPointId = null,
            RoomName = string.Empty, PowerPointName = "Unmapped Plug", DeviceName = "Unmapped Plug",
            IntervalStart = duplicateIntervalStart, IntervalEnd = duplicateIntervalStart, KwhValue = 0.7m,
        });
        await Should.NotThrowAsync(() => dbContext.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CleanupOrphanedUnmappedSmartPlugReadingDuplicates_migration_deletes_only_unmapped_rows_with_an_exact_mapped_twin()
    {
        // Story 3.7 AC #3 — seeds, BEFORE the migration under test is applied (migrate up to the
        // one immediately before it): (a) a mapped reading and an unmapped exact duplicate of it
        // (same HouseholdId/DeviceName/IntervalStart/IntervalEnd/KwhValue, differing only in
        // PowerPointId/RoomName/PowerPointName/SmartPlugImportId) — this is the orphaned-duplicate
        // shape Story 3.4's AwaitingPowerPointMapping -> later-mapped path could produce before
        // this story's Task 1 fix; and (b) an unmapped reading with no mapped twin at all (a
        // genuinely still-unmapped device). Asserts only (a)'s unmapped row is deleted; (b)
        // survives untouched, and the mapped row itself is untouched.
        var householdId = Guid.NewGuid();
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));

        await using var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
        var migrator = dbContext.GetInfrastructure().GetRequiredService<IMigrator>();

        await migrator.MigrateAsync("20260824100238_AddMeterReadingMainMeterReadingTimestampIndex", TestContext.Current.CancellationToken);

        var now = DateTimeOffset.UtcNow;
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = now });
        // Committed alone, ahead of the raw BackgroundJob inserts below, whose HouseholdId FK
        // requires the row to already exist (not merely tracked).
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var room = new Room { Id = Guid.NewGuid(), HouseholdId = householdId, Name = "Kitchen", CreatedAtUtc = now };
        dbContext.Rooms.Add(room);
        var powerPoint = new PowerPoint { Id = Guid.NewGuid(), HouseholdId = householdId, RoomId = room.Id, Name = "Fridge", CreatedAtUtc = now };
        dbContext.PowerPoints.Add(powerPoint);
        var mappedJob = new BackgroundJob
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Completed, CreatedAtUtc = now, CompletedAtUtc = now,
        };
        var orphanedJob = new BackgroundJob
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Completed, CreatedAtUtc = now, CompletedAtUtc = now,
        };
        var stillAwaitingJob = new BackgroundJob
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Completed, CreatedAtUtc = now, CompletedAtUtc = now,
        };
        await InsertBackgroundJobPreStory36Async(dbContext, mappedJob, TestContext.Current.CancellationToken);
        await InsertBackgroundJobPreStory36Async(dbContext, orphanedJob, TestContext.Current.CancellationToken);
        await InsertBackgroundJobPreStory36Async(dbContext, stillAwaitingJob, TestContext.Current.CancellationToken);
        var mappedImport = new SmartPlugImport
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, BackgroundJobId = mappedJob.Id,
            VendorFormat = SmartPlugVendorFormat.EveHome, OriginalFileName = "mapped.xlsx",
            Status = SmartPlugImportStatus.Completed, DeviceTag = "Fridge",
            CreatedAtUtc = now, CompletedAtUtc = now,
        };
        var orphanedImport = new SmartPlugImport
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, BackgroundJobId = orphanedJob.Id,
            VendorFormat = SmartPlugVendorFormat.EveHome, OriginalFileName = "orphaned.xlsx",
            Status = SmartPlugImportStatus.Completed, DeviceTag = "Fridge",
            CreatedAtUtc = now, CompletedAtUtc = now,
        };
        var stillAwaitingImport = new SmartPlugImport
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, BackgroundJobId = stillAwaitingJob.Id,
            VendorFormat = SmartPlugVendorFormat.EveHome, OriginalFileName = "still-awaiting.xlsx",
            Status = SmartPlugImportStatus.AwaitingPowerPointMapping, DeviceTag = "HiFi",
            CreatedAtUtc = now, CompletedAtUtc = null,
        };
        dbContext.SmartPlugImports.AddRange(mappedImport, orphanedImport, stillAwaitingImport);

        var duplicateIntervalStart = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var mappedReadingId = Guid.NewGuid();
        var orphanedReadingId = Guid.NewGuid();
        var stillAwaitingReadingId = Guid.NewGuid();
        dbContext.SmartPlugReadings.AddRange(
            new SmartPlugReading
            {
                Id = mappedReadingId, HouseholdId = householdId, SmartPlugImportId = mappedImport.Id, PowerPointId = powerPoint.Id,
                RoomName = "Kitchen", PowerPointName = "Fridge", DeviceName = "Fridge",
                IntervalStart = duplicateIntervalStart, IntervalEnd = duplicateIntervalStart, KwhValue = 0.5m,
            },
            new SmartPlugReading
            {
                // Exact duplicate of the mapped reading above (same HouseholdId/DeviceName/
                // IntervalStart/IntervalEnd/KwhValue), left orphaned by the pre-Story-3.7
                // conflict-tolerant fallback — this is the row the migration must delete.
                Id = orphanedReadingId, HouseholdId = householdId, SmartPlugImportId = orphanedImport.Id, PowerPointId = null,
                RoomName = "Kitchen", PowerPointName = "Fridge", DeviceName = "Fridge",
                IntervalStart = duplicateIntervalStart, IntervalEnd = duplicateIntervalStart, KwhValue = 0.5m,
            },
            new SmartPlugReading
            {
                // A different device tag, different IntervalStart, no mapped twin anywhere —
                // genuinely still AwaitingPowerPointMapping, must survive untouched.
                Id = stillAwaitingReadingId, HouseholdId = householdId, SmartPlugImportId = stillAwaitingImport.Id, PowerPointId = null,
                RoomName = string.Empty, PowerPointName = "HiFi", DeviceName = "HiFi",
                IntervalStart = duplicateIntervalStart.AddDays(1), IntervalEnd = duplicateIntervalStart.AddDays(1), KwhValue = 0.9m,
            });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        var remainingReadingIds = await dbContext.SmartPlugReadings
            .Where(r => r.HouseholdId == householdId)
            .Select(r => r.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        remainingReadingIds.ShouldBe([mappedReadingId, stillAwaitingReadingId], ignoreOrder: true);
    }

    [Fact]
    public async Task SmartPlugImportId_FK_is_ON_DELETE_SET_NULL_so_the_reading_survives_when_its_import_is_deleted()
    {
        // Story 3.6/AD-6 extension Task 3 — confirms the FK behavior the retention sweep depends
        // on directly against the real engine, not just the EF model: deleting a SmartPlugImport
        // row must never fail (Restrict would throw a FK-violation, Cascade would silently
        // destroy the reading data AD-20 requires survive) — the reading must survive with
        // SmartPlugImportId nulled and every other field untouched.
        var householdId = Guid.NewGuid();
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));

        await using var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
        await dbContext.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var now = DateTimeOffset.UtcNow;
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = now });
        var room = new Room { Id = Guid.NewGuid(), HouseholdId = householdId, Name = "Kitchen", CreatedAtUtc = now };
        dbContext.Rooms.Add(room);
        var powerPoint = new PowerPoint { Id = Guid.NewGuid(), HouseholdId = householdId, RoomId = room.Id, Name = "Fridge", CreatedAtUtc = now };
        dbContext.PowerPoints.Add(powerPoint);
        var job = new BackgroundJob
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Completed, CreatedAtUtc = now, CompletedAtUtc = now,
        };
        dbContext.BackgroundJobs.Add(job);
        var import = new SmartPlugImport
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, BackgroundJobId = job.Id,
            VendorFormat = SmartPlugVendorFormat.EveHome, OriginalFileName = "export.xlsx",
            Status = SmartPlugImportStatus.Completed, DeviceTag = "Fridge",
            CreatedAtUtc = now, CompletedAtUtc = now,
        };
        dbContext.SmartPlugImports.Add(import);
        var readingId = Guid.NewGuid();
        var intervalStart = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        dbContext.SmartPlugReadings.Add(new SmartPlugReading
        {
            Id = readingId, HouseholdId = householdId, SmartPlugImportId = import.Id, PowerPointId = powerPoint.Id,
            RoomName = "Kitchen", PowerPointName = "Fridge", DeviceName = "Fridge",
            IntervalStart = intervalStart, IntervalEnd = intervalStart, KwhValue = 0.5m,
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        await dbContext.SmartPlugImports.Where(i => i.Id == import.Id).ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        dbContext.ChangeTracker.Clear();
        var survivingReading = await dbContext.SmartPlugReadings.SingleAsync(r => r.Id == readingId, TestContext.Current.CancellationToken);
        survivingReading.SmartPlugImportId.ShouldBeNull();
        survivingReading.PowerPointId.ShouldBe(powerPoint.Id);
        survivingReading.RoomName.ShouldBe("Kitchen");
        survivingReading.PowerPointName.ShouldBe("Fridge");
        survivingReading.DeviceName.ShouldBe("Fridge");
        survivingReading.IntervalStart.ShouldBe(intervalStart);
        survivingReading.KwhValue.ShouldBe(0.5m);
    }
}
