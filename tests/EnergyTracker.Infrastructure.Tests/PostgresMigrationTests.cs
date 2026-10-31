using System.Data.Common;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

public class PostgresMigrationTests : IAsyncLifetime
{
    private readonly PostgresTestDatabase _container = new();

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

    // Story 5.4 added TariffCheckCadenceMonths to Household. Same reasoning as
    // InsertBackgroundJobPreStory36Async above: tests seeding a Household at a migration
    // checkpoint before this column existed can't use EF's current-model tracked insert (it would
    // reference a column absent from the physical schema at that point in history) — every other
    // column here has a DB-level default (or is nullable), so this raw INSERT only needs to supply
    // the four that don't.
    private static async Task InsertHouseholdPreStory54Async(EnergyTrackerDbContext dbContext, Guid id, string locale, string currency, DateTimeOffset createdAtUtc, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Households" ("Id", "Locale", "Currency", "CreatedAtUtc")
            VALUES ({id}, {locale}, {currency}, {createdAtUtc})
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
        // Raw insert (not tracked EF Add) — see InsertHouseholdPreStory54Async's own comment.
        // Committed alone, ahead of the raw BackgroundJob inserts below, whose HouseholdId FK
        // requires the row to already exist (not merely tracked).
        await InsertHouseholdPreStory54Async(dbContext, householdId, "en-US", "USD", now, TestContext.Current.CancellationToken);
        var room = new Room { Id = Guid.NewGuid(), HouseholdId = householdId, Name = "Kitchen", CreatedAtUtc = now };
        dbContext.Rooms.Add(room);
        var powerPoint = new PowerPoint { Id = Guid.NewGuid(), HouseholdId = householdId, RoomId = room.Id, Name = "Fridge", CreatedAtUtc = now };
        dbContext.PowerPoints.Add(powerPoint);
        var olderJob = new BackgroundJob
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Completed,
            CreatedAtUtc = now,
            CompletedAtUtc = now,
        };
        var newerJob = new BackgroundJob
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Completed,
            CreatedAtUtc = now,
            CompletedAtUtc = now,
        };
        await InsertBackgroundJobPreStory36Async(dbContext, olderJob, TestContext.Current.CancellationToken);
        await InsertBackgroundJobPreStory36Async(dbContext, newerJob, TestContext.Current.CancellationToken);
        var olderImport = new SmartPlugImport
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            BackgroundJobId = olderJob.Id,
            VendorFormat = SmartPlugVendorFormat.EveHome,
            OriginalFileName = "older.xlsx",
            Status = SmartPlugImportStatus.Completed,
            DeviceTag = "Fridge",
            CreatedAtUtc = now.AddDays(-1),
            CompletedAtUtc = now.AddDays(-1),
        };
        var newerImport = new SmartPlugImport
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            BackgroundJobId = newerJob.Id,
            VendorFormat = SmartPlugVendorFormat.EveHome,
            OriginalFileName = "newer.xlsx",
            Status = SmartPlugImportStatus.Completed,
            DeviceTag = "Fridge",
            CreatedAtUtc = now,
            CompletedAtUtc = now,
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
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                SmartPlugImportId = olderImport.Id,
                PowerPointId = powerPoint.Id,
                RoomName = "Kitchen",
                PowerPointName = "Fridge",
                DeviceName = "Fridge",
                IntervalStart = duplicateIntervalStart,
                IntervalEnd = duplicateIntervalStart,
                KwhValue = 0.1m,
            },
            new SmartPlugReading
            {
                Id = survivorId,
                HouseholdId = householdId,
                SmartPlugImportId = newerImport.Id,
                PowerPointId = powerPoint.Id,
                RoomName = "Kitchen",
                PowerPointName = "Fridge",
                DeviceName = "Fridge",
                IntervalStart = duplicateIntervalStart,
                IntervalEnd = duplicateIntervalStart,
                KwhValue = 0.2m,
            },
            new SmartPlugReading
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                SmartPlugImportId = olderImport.Id,
                PowerPointId = null,
                RoomName = string.Empty,
                PowerPointName = "Unmapped Plug",
                DeviceName = "Unmapped Plug",
                IntervalStart = duplicateIntervalStart,
                IntervalEnd = duplicateIntervalStart,
                KwhValue = 0.4m,
            },
            new SmartPlugReading
            {
                Id = nullPowerPointSurvivorId,
                HouseholdId = householdId,
                SmartPlugImportId = newerImport.Id,
                PowerPointId = null,
                RoomName = string.Empty,
                PowerPointName = "Unmapped Plug",
                DeviceName = "Unmapped Plug",
                IntervalStart = duplicateIntervalStart,
                IntervalEnd = duplicateIntervalStart,
                KwhValue = 0.5m,
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
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            SmartPlugImportId = newerImport.Id,
            PowerPointId = powerPoint.Id,
            RoomName = "Kitchen",
            PowerPointName = "Fridge",
            DeviceName = "Fridge",
            IntervalStart = duplicateIntervalStart,
            IntervalEnd = duplicateIntervalStart,
            KwhValue = 0.3m,
        });
        await Should.ThrowAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(TestContext.Current.CancellationToken));
        dbContext.ChangeTracker.Clear();

        dbContext.SmartPlugReadings.Add(new SmartPlugReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            SmartPlugImportId = newerImport.Id,
            PowerPointId = null,
            RoomName = string.Empty,
            PowerPointName = "Unmapped Plug",
            DeviceName = "Unmapped Plug",
            IntervalStart = duplicateIntervalStart,
            IntervalEnd = duplicateIntervalStart,
            KwhValue = 0.6m,
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
            Id = Guid.NewGuid(),
            HouseholdId = otherHouseholdId,
            JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Completed,
            CreatedAtUtc = now,
            CompletedAtUtc = now,
        };
        dbContext.BackgroundJobs.Add(otherJob);
        var otherImport = new SmartPlugImport
        {
            Id = Guid.NewGuid(),
            HouseholdId = otherHouseholdId,
            BackgroundJobId = otherJob.Id,
            VendorFormat = SmartPlugVendorFormat.EveHome,
            OriginalFileName = "other-household.xlsx",
            Status = SmartPlugImportStatus.AwaitingPowerPointMapping,
            DeviceTag = "Unmapped Plug",
            CreatedAtUtc = now,
            CompletedAtUtc = now,
        };
        dbContext.SmartPlugImports.Add(otherImport);
        dbContext.SmartPlugReadings.Add(new SmartPlugReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = otherHouseholdId,
            SmartPlugImportId = otherImport.Id,
            PowerPointId = null,
            RoomName = string.Empty,
            PowerPointName = "Unmapped Plug",
            DeviceName = "Unmapped Plug",
            IntervalStart = duplicateIntervalStart,
            IntervalEnd = duplicateIntervalStart,
            KwhValue = 0.7m,
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
        // Raw insert (not tracked EF Add) — see InsertHouseholdPreStory54Async's own comment.
        // Committed alone, ahead of the raw BackgroundJob inserts below, whose HouseholdId FK
        // requires the row to already exist (not merely tracked).
        await InsertHouseholdPreStory54Async(dbContext, householdId, "en-US", "USD", now, TestContext.Current.CancellationToken);
        var room = new Room { Id = Guid.NewGuid(), HouseholdId = householdId, Name = "Kitchen", CreatedAtUtc = now };
        dbContext.Rooms.Add(room);
        var powerPoint = new PowerPoint { Id = Guid.NewGuid(), HouseholdId = householdId, RoomId = room.Id, Name = "Fridge", CreatedAtUtc = now };
        dbContext.PowerPoints.Add(powerPoint);
        var mappedJob = new BackgroundJob
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Completed,
            CreatedAtUtc = now,
            CompletedAtUtc = now,
        };
        var orphanedJob = new BackgroundJob
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Completed,
            CreatedAtUtc = now,
            CompletedAtUtc = now,
        };
        var stillAwaitingJob = new BackgroundJob
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Completed,
            CreatedAtUtc = now,
            CompletedAtUtc = now,
        };
        await InsertBackgroundJobPreStory36Async(dbContext, mappedJob, TestContext.Current.CancellationToken);
        await InsertBackgroundJobPreStory36Async(dbContext, orphanedJob, TestContext.Current.CancellationToken);
        await InsertBackgroundJobPreStory36Async(dbContext, stillAwaitingJob, TestContext.Current.CancellationToken);
        var mappedImport = new SmartPlugImport
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            BackgroundJobId = mappedJob.Id,
            VendorFormat = SmartPlugVendorFormat.EveHome,
            OriginalFileName = "mapped.xlsx",
            Status = SmartPlugImportStatus.Completed,
            DeviceTag = "Fridge",
            CreatedAtUtc = now,
            CompletedAtUtc = now,
        };
        var orphanedImport = new SmartPlugImport
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            BackgroundJobId = orphanedJob.Id,
            VendorFormat = SmartPlugVendorFormat.EveHome,
            OriginalFileName = "orphaned.xlsx",
            Status = SmartPlugImportStatus.Completed,
            DeviceTag = "Fridge",
            CreatedAtUtc = now,
            CompletedAtUtc = now,
        };
        var stillAwaitingImport = new SmartPlugImport
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            BackgroundJobId = stillAwaitingJob.Id,
            VendorFormat = SmartPlugVendorFormat.EveHome,
            OriginalFileName = "still-awaiting.xlsx",
            Status = SmartPlugImportStatus.AwaitingPowerPointMapping,
            DeviceTag = "HiFi",
            CreatedAtUtc = now,
            CompletedAtUtc = null,
        };
        dbContext.SmartPlugImports.AddRange(mappedImport, orphanedImport, stillAwaitingImport);

        var duplicateIntervalStart = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var mappedReadingId = Guid.NewGuid();
        var orphanedReadingId = Guid.NewGuid();
        var stillAwaitingReadingId = Guid.NewGuid();
        dbContext.SmartPlugReadings.AddRange(
            new SmartPlugReading
            {
                Id = mappedReadingId,
                HouseholdId = householdId,
                SmartPlugImportId = mappedImport.Id,
                PowerPointId = powerPoint.Id,
                RoomName = "Kitchen",
                PowerPointName = "Fridge",
                DeviceName = "Fridge",
                IntervalStart = duplicateIntervalStart,
                IntervalEnd = duplicateIntervalStart,
                KwhValue = 0.5m,
            },
            new SmartPlugReading
            {
                // Exact duplicate of the mapped reading above (same HouseholdId/DeviceName/
                // IntervalStart/IntervalEnd/KwhValue), left orphaned by the pre-Story-3.7
                // conflict-tolerant fallback — this is the row the migration must delete.
                Id = orphanedReadingId,
                HouseholdId = householdId,
                SmartPlugImportId = orphanedImport.Id,
                PowerPointId = null,
                RoomName = "Kitchen",
                PowerPointName = "Fridge",
                DeviceName = "Fridge",
                IntervalStart = duplicateIntervalStart,
                IntervalEnd = duplicateIntervalStart,
                KwhValue = 0.5m,
            },
            new SmartPlugReading
            {
                // A different device tag, different IntervalStart, no mapped twin anywhere —
                // genuinely still AwaitingPowerPointMapping, must survive untouched.
                Id = stillAwaitingReadingId,
                HouseholdId = householdId,
                SmartPlugImportId = stillAwaitingImport.Id,
                PowerPointId = null,
                RoomName = string.Empty,
                PowerPointName = "HiFi",
                DeviceName = "HiFi",
                IntervalStart = duplicateIntervalStart.AddDays(1),
                IntervalEnd = duplicateIntervalStart.AddDays(1),
                KwhValue = 0.9m,
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
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Completed,
            CreatedAtUtc = now,
            CompletedAtUtc = now,
        };
        dbContext.BackgroundJobs.Add(job);
        var import = new SmartPlugImport
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            BackgroundJobId = job.Id,
            VendorFormat = SmartPlugVendorFormat.EveHome,
            OriginalFileName = "export.xlsx",
            Status = SmartPlugImportStatus.Completed,
            DeviceTag = "Fridge",
            CreatedAtUtc = now,
            CompletedAtUtc = now,
        };
        dbContext.SmartPlugImports.Add(import);
        var readingId = Guid.NewGuid();
        var intervalStart = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        dbContext.SmartPlugReadings.Add(new SmartPlugReading
        {
            Id = readingId,
            HouseholdId = householdId,
            SmartPlugImportId = import.Id,
            PowerPointId = powerPoint.Id,
            RoomName = "Kitchen",
            PowerPointName = "Fridge",
            DeviceName = "Fridge",
            IntervalStart = intervalStart,
            IntervalEnd = intervalStart,
            KwhValue = 0.5m,
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

    // --- Story 11.4, AD-20 (amended 2026-10-10), audit C9: deploy N narrows SmartPlugReadings.DeviceName
    // to a bounded column and adds the (HouseholdId, DeviceName, IntervalStart) WHERE PowerPointId IS NULL
    // unique index next to the old one (the old index is dropped by deploy N+1). Located by name suffix so
    // the tests do not depend on the generated timestamp. ---

    private const string NarrowMigrationSuffix = "_NarrowSmartPlugReadingDeviceNameKeyUnmappedByDevice";

    private EnergyTrackerDbContext NewDeviceNameKeyDbContext(Guid householdId)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));
        return new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
    }

    private static (string Previous, string Narrow) ResolveNarrowMigration(EnergyTrackerDbContext dbContext)
    {
        var migrations = dbContext.Database.GetMigrations().ToList();
        var narrow = migrations.Single(m => m.EndsWith(NarrowMigrationSuffix, StringComparison.Ordinal));
        return (migrations[migrations.IndexOf(narrow) - 1], narrow);
    }

    private static async Task SeedUnmappedReadingsAsync(
        EnergyTrackerDbContext dbContext, Guid householdId, CancellationToken cancellationToken, params string[] deviceNames)
    {
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        var start = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < deviceNames.Length; i++)
        {
            dbContext.SmartPlugReadings.Add(new SmartPlugReading
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                SmartPlugImportId = null,
                PowerPointId = null,
                RoomName = string.Empty,
                PowerPointName = "Unmapped",
                DeviceName = deviceNames[i],
                IntervalStart = start.AddMinutes(10 * i),
                IntervalEnd = start.AddMinutes(10 * i),
                KwhValue = 0.5m,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    [Fact]
    public async Task NarrowSmartPlugReadingDeviceNameKey_migration_bounds_the_column_keeps_stored_values_and_adds_the_new_unmapped_index_beside_the_old_one()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = NewDeviceNameKeyDbContext(householdId);
        var migrator = dbContext.GetInfrastructure().GetRequiredService<IMigrator>();
        var (previous, narrow) = ResolveNarrowMigration(dbContext);
        await migrator.MigrateAsync(previous, TestContext.Current.CancellationToken);
        var longestAllowed = new string('x', 200);
        await SeedUnmappedReadingsAsync(dbContext, householdId, TestContext.Current.CancellationToken, longestAllowed, "Fridge");

        await migrator.MigrateAsync(narrow, TestContext.Current.CancellationToken);

        dbContext.ChangeTracker.Clear();
        var names = await dbContext.SmartPlugReadings.AsNoTracking()
            .OrderBy(r => r.IntervalStart).Select(r => r.DeviceName).ToListAsync(TestContext.Current.CancellationToken);
        names.ShouldBe([longestAllowed, "Fridge"]);
        (await dbContext.Database.SqlQueryRaw<int>("SELECT character_maximum_length::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'SmartPlugReadings' AND column_name = 'DeviceName'").SingleAsync(TestContext.Current.CancellationToken))
            .ShouldBe(200);

        var indexes = await dbContext.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'SmartPlugReadings'").ToListAsync(TestContext.Current.CancellationToken);
        indexes.Count(d => d.Contains("UNIQUE") && d.Contains("(\"HouseholdId\", \"DeviceName\", \"IntervalStart\")") && d.Contains("WHERE (\"PowerPointId\" IS NULL)")).ShouldBe(1);
        // AC #2: expand only. The old key stays through deploy N so the previous image and a rollback keep their arbiter.
        indexes.Count(d => d.Contains("UNIQUE") && d.Contains("(\"HouseholdId\", \"IntervalStart\")") && d.Contains("WHERE (\"PowerPointId\" IS NULL)")).ShouldBe(1);
    }

    [Fact]
    public async Task NarrowSmartPlugReadingDeviceNameKey_migration_fails_loudly_and_changes_nothing_when_a_stored_DeviceName_is_over_200_characters()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = NewDeviceNameKeyDbContext(householdId);
        var migrator = dbContext.GetInfrastructure().GetRequiredService<IMigrator>();
        var (previous, narrow) = ResolveNarrowMigration(dbContext);
        await migrator.MigrateAsync(previous, TestContext.Current.CancellationToken);
        // Plain EF insert: the column is still unbounded at this checkpoint.
        await SeedUnmappedReadingsAsync(dbContext, householdId, TestContext.Current.CancellationToken, "Fridge", new string('y', 201));

        var failure = await Should.ThrowAsync<DbException>(() => migrator.MigrateAsync(narrow, TestContext.Current.CancellationToken));

        failure.Message.ShouldContain("SmartPlugReadings");
        failure.Message.ShouldContain("DeviceName");
        failure.Message.ShouldContain("200");
        failure.Message.ShouldContain("201");
        // Never truncated, and the whole Up() rolled back: the migration is not recorded and the column is unchanged.
        var storedLengths = await dbContext.Database.SqlQueryRaw<int>("SELECT length(\"DeviceName\") AS \"Value\" FROM \"SmartPlugReadings\"").ToListAsync(TestContext.Current.CancellationToken);
        storedLengths.OrderBy(l => l).ToList().ShouldBe([6, 201]);
        (await dbContext.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).ShouldNotContain(narrow);
        (await dbContext.Database.SqlQueryRaw<int?>("SELECT character_maximum_length::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'SmartPlugReadings' AND column_name = 'DeviceName'").SingleAsync(TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    // --- Story 11.4 Part B (deploy N+1): the contract half drops the old (HouseholdId, IntervalStart)
    // WHERE PowerPointId IS NULL index. Located by name suffix; indexes asserted by definition. ---

    private const string DropOldIndexMigrationSuffix = "_DropOldUnmappedSmartPlugReadingIndex";

    [Fact]
    public async Task DropOldUnmappedSmartPlugReadingIndex_migration_drops_only_the_old_unmapped_index_and_keeps_the_new_and_the_mapped_ones()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = NewDeviceNameKeyDbContext(householdId);
        var migrator = dbContext.GetInfrastructure().GetRequiredService<IMigrator>();
        var migrations = dbContext.Database.GetMigrations().ToList();
        var dropOld = migrations.Single(m => m.EndsWith(DropOldIndexMigrationSuffix, StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(dropOld) - 1];
        await migrator.MigrateAsync(previous, TestContext.Current.CancellationToken);
        const string indexesSql = "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'SmartPlugReadings'";
        var before = await dbContext.Database.SqlQueryRaw<string>(indexesSql).ToListAsync(TestContext.Current.CancellationToken);
        before.Count(IsOldUnmappedIndex).ShouldBe(1);

        await migrator.MigrateAsync(dropOld, TestContext.Current.CancellationToken);

        var after = await dbContext.Database.SqlQueryRaw<string>(indexesSql).ToListAsync(TestContext.Current.CancellationToken);
        after.Count(IsOldUnmappedIndex).ShouldBe(0);
        after.Count(d => d.Contains("UNIQUE") && d.Contains("(\"HouseholdId\", \"DeviceName\", \"IntervalStart\")") && d.Contains("WHERE (\"PowerPointId\" IS NULL)")).ShouldBe(1);
        // The mapped (PowerPointId, IntervalStart) key is a unique index or constraint-backed index, untouched.
        after.Count(d => d.Contains("UNIQUE") && d.Contains("(\"PowerPointId\", \"IntervalStart\")")).ShouldBe(1);
    }

    private static bool IsOldUnmappedIndex(string indexDef) =>
        indexDef.Contains("UNIQUE") && indexDef.Contains("(\"HouseholdId\", \"IntervalStart\")") && indexDef.Contains("WHERE (\"PowerPointId\" IS NULL)");

    // --- Story 11.9 (AD-26, audit S10): expand-only. TokenHash is added and Token becomes nullable. ---

    private const string TokenHashMigrationSuffix = "_AddHouseholdInviteTokenHash";

    [Fact]
    public async Task AddHouseholdInviteTokenHash_migration_adds_TokenHash_relaxes_Token_keeps_legacy_rows_and_allows_many_hash_only_invites()
    {
        var householdId = Guid.NewGuid();
        var legacyId = Guid.NewGuid();
        await using var dbContext = NewDeviceNameKeyDbContext(householdId);
        var migrator = dbContext.GetInfrastructure().GetRequiredService<IMigrator>();
        var migrations = dbContext.Database.GetMigrations().ToList();
        var tokenHash = migrations.Single(m => m.EndsWith(TokenHashMigrationSuffix, StringComparison.Ordinal));
        await migrator.MigrateAsync(migrations[migrations.IndexOf(tokenHash) - 1], TestContext.Current.CancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Households" ("Id", "Locale", "Currency", "CreatedAtUtc") VALUES ({householdId}, 'en-US', 'USD', {DateTimeOffset.UtcNow})
            """, TestContext.Current.CancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "HouseholdInvites" ("Id", "HouseholdId", "Token", "CreatedAtUtc", "ExpiresAtUtc", "Version")
            VALUES ({legacyId}, {householdId}, 'legacytoken000000000000000001', {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow.AddDays(1)}, 0)
            """, TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(tokenHash, TestContext.Current.CancellationToken);

        (await dbContext.Database.SqlQueryRaw<int>("SELECT character_maximum_length::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'HouseholdInvites' AND column_name = 'TokenHash'").SingleAsync(TestContext.Current.CancellationToken))
            .ShouldBe(64);
        (await dbContext.Database.SqlQueryRaw<string>("SELECT is_nullable AS \"Value\" FROM information_schema.columns WHERE table_name = 'HouseholdInvites' AND column_name = 'Token'").SingleAsync(TestContext.Current.CancellationToken))
            .ShouldBe("YES");
        dbContext.ChangeTracker.Clear();
        var legacy = await dbContext.HouseholdInvites.AsNoTracking().SingleAsync(i => i.Id == legacyId, TestContext.Current.CancellationToken);
        legacy.Token.ShouldBe("legacytoken000000000000000001");
        legacy.TokenHash.ShouldBeNull();

        dbContext.HouseholdInvites.AddRange(
            new HouseholdInvite { Id = Guid.NewGuid(), HouseholdId = householdId, TokenHash = new string('a', 64), CreatedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7) },
            new HouseholdInvite { Id = Guid.NewGuid(), HouseholdId = householdId, TokenHash = new string('b', 64), CreatedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7) });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The legacy Token column stays unique for its own rows.
        dbContext.HouseholdInvites.Add(new HouseholdInvite { Id = Guid.NewGuid(), HouseholdId = householdId, Token = "legacytoken000000000000000001", CreatedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7) });
        await Should.ThrowAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    // --- Story 11.14 (AD-6, audit C10): AddActiveJobGuard fails the surplus active restore/cleanup rows of a
    // Household (keeping the newest by CreatedAtUtc, then Id) and then creates the filtered unique index. ---

    private const string ActiveJobGuardMigrationSuffix = "_AddActiveJobGuard";

    private static (string Previous, string Guard) ResolveActiveJobGuardMigration(EnergyTrackerDbContext dbContext)
    {
        var migrations = dbContext.Database.GetMigrations().ToList();
        var guard = migrations.Single(m => m.EndsWith(ActiveJobGuardMigrationSuffix, StringComparison.Ordinal));
        return (migrations[migrations.IndexOf(guard) - 1], guard);
    }

    private static BackgroundJob Job(
        Guid id, Guid householdId, string jobType, BackgroundJobStatus status, DateTimeOffset createdAt, DateTimeOffset? completedAt = null) => new()
        {
            Id = id,
            HouseholdId = householdId,
            JobType = jobType,
            Status = status,
            CreatedAtUtc = createdAt,
            CompletedAtUtc = completedAt,
        };

    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static async Task SeedHouseholdsAsync(EnergyTrackerDbContext dbContext, CancellationToken cancellationToken, params Guid[] householdIds)
    {
        foreach (var householdId in householdIds)
        {
            dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    [Fact]
    public async Task AddActiveJobGuard_migration_fails_only_the_surplus_active_rows_keeps_the_newest_and_then_creates_the_index()
    {
        var ct = TestContext.Current.CancellationToken;
        var h1 = Guid.NewGuid();
        var h2 = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        await using var dbContext = NewDeviceNameKeyDbContext(h1);
        var migrator = dbContext.GetInfrastructure().GetRequiredService<IMigrator>();
        var (previous, guard) = ResolveActiveJobGuardMigration(dbContext);
        await migrator.MigrateAsync(previous, ct);
        await SeedHouseholdsAsync(dbContext, ct, h1, h2);
        dbContext.BackgroundJobs.AddRange(
            // H1 restore: three active rows, the Processing one is the newest and survives.
            Job(Id(1), h1, "RestoreHouseholdData", BackgroundJobStatus.Queued, t0),
            Job(Id(2), h1, "RestoreHouseholdData", BackgroundJobStatus.Queued, t0.AddMinutes(1)),
            Job(Id(3), h1, "RestoreHouseholdData", BackgroundJobStatus.Processing, t0.AddMinutes(2)),
            // H1 cleanup: same CreatedAtUtc, so the greater Id survives.
            Job(Id(4), h1, "CleanUpSmartPlugImportJobs", BackgroundJobStatus.Queued, t0),
            Job(Id(5), h1, "CleanUpSmartPlugImportJobs", BackgroundJobStatus.Queued, t0),
            // H2: one active restore, nothing to fail.
            Job(Id(6), h2, "RestoreHouseholdData", BackgroundJobStatus.Queued, t0),
            // Terminal rows of a guarded type and several active rows of unguarded types: never touched.
            Job(Id(7), h1, "RestoreHouseholdData", BackgroundJobStatus.Completed, t0.AddMinutes(-30), t0.AddMinutes(-29)),
            Job(Id(8), h1, "RestoreHouseholdData", BackgroundJobStatus.Failed, t0.AddMinutes(-20), t0.AddMinutes(-19)),
            Job(Id(9), h1, "ProcessSmartPlugImport", BackgroundJobStatus.Queued, t0),
            Job(Id(10), h1, "ProcessSmartPlugImport", BackgroundJobStatus.Processing, t0.AddMinutes(1)));
        await dbContext.SaveChangesAsync(ct);
        dbContext.ChangeTracker.Clear();

        await migrator.MigrateAsync(guard, ct);

        var rows = await dbContext.BackgroundJobs.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(j => j.Id, ct);
        foreach (var failed in new[] { Id(1), Id(2), Id(4) })
        {
            rows[failed].Status.ShouldBe(BackgroundJobStatus.Failed);
            rows[failed].ErrorMessage.ShouldBe("job-interrupted");
            rows[failed].CompletedAtUtc.ShouldNotBeNull();
        }

        foreach (var kept in new[] { Id(3), Id(5), Id(6), Id(9), Id(10) })
        {
            rows[kept].Status.ShouldNotBe(BackgroundJobStatus.Failed);
            rows[kept].ErrorMessage.ShouldBeNull();
            rows[kept].CompletedAtUtc.ShouldBeNull();
        }

        rows[Id(3)].Status.ShouldBe(BackgroundJobStatus.Processing);
        rows[Id(7)].Status.ShouldBe(BackgroundJobStatus.Completed);
        rows[Id(7)].CompletedAtUtc.ShouldBe(t0.AddMinutes(-29));
        rows[Id(8)].Status.ShouldBe(BackgroundJobStatus.Failed);
        rows[Id(8)].ErrorMessage.ShouldBeNull();
        rows.Count.ShouldBe(10);
        (await CountActiveJobGuardIndexAsync(dbContext, ct)).ShouldBe(1);

        // The index is live: a second active restore for H1 is now rejected by the database itself.
        dbContext.BackgroundJobs.Add(Job(Id(11), h1, "RestoreHouseholdData", BackgroundJobStatus.Queued, t0.AddMinutes(5)));
        await Should.ThrowAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task AddActiveJobGuard_migration_updates_nothing_on_a_database_without_duplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        var h1 = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        await using var dbContext = NewDeviceNameKeyDbContext(h1);
        var migrator = dbContext.GetInfrastructure().GetRequiredService<IMigrator>();
        var (previous, guard) = ResolveActiveJobGuardMigration(dbContext);
        await migrator.MigrateAsync(previous, ct);
        await SeedHouseholdsAsync(dbContext, ct, h1);
        dbContext.BackgroundJobs.AddRange(
            Job(Id(1), h1, "RestoreHouseholdData", BackgroundJobStatus.Processing, t0),
            Job(Id(2), h1, "CleanUpSmartPlugImportJobs", BackgroundJobStatus.Queued, t0),
            Job(Id(3), h1, "RestoreHouseholdData", BackgroundJobStatus.Completed, t0.AddMinutes(-5), t0.AddMinutes(-4)));
        await dbContext.SaveChangesAsync(ct);
        dbContext.ChangeTracker.Clear();

        await migrator.MigrateAsync(guard, ct);

        var rows = await dbContext.BackgroundJobs.IgnoreQueryFilters().AsNoTracking().OrderBy(j => j.CreatedAtUtc).ThenBy(j => j.Id).ToListAsync(ct);
        rows.Select(j => j.Status).OrderBy(s => s).ShouldBe([BackgroundJobStatus.Processing, BackgroundJobStatus.Completed, BackgroundJobStatus.Queued]);
        rows.ShouldAllBe(j => j.ErrorMessage == null);
        (await CountActiveJobGuardIndexAsync(dbContext, ct)).ShouldBe(1);
    }

    private static Task<int> CountActiveJobGuardIndexAsync(EnergyTrackerDbContext dbContext, CancellationToken cancellationToken) =>
        dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM pg_indexes WHERE indexname = 'UX_BackgroundJobs_ActiveExclusive' AND indexdef LIKE 'CREATE UNIQUE INDEX%' AND indexdef LIKE '% WHERE %'")
            .SingleAsync(cancellationToken);
}
