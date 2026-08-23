using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Testcontainers.MsSql;

namespace EnergyTracker.Infrastructure.Tests;

public class SqlServerMigrationTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private sealed class FixedHouseholdAccessor(Guid householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;
    }

    [Fact]
    public async Task SqlServer_migrations_apply_cleanly_to_a_real_database()
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer"));

        await using var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, null!);

        await dbContext.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var appliedMigrations = await dbContext.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        appliedMigrations.ShouldContain(m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
        appliedMigrations.ShouldContain(m => m.EndsWith("_AddHouseholdAndDataProtectionKeys", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddSmartPlugReadingUniqueIndex_migration_dedups_existing_duplicates_and_the_new_index_rejects_a_fresh_one()
    {
        // Story 3.4 AC #8/#9/#10 — mirrors PostgresMigrationTests's equivalent test exactly: seeds
        // duplicate (PowerPointId, IntervalStart) rows across two different SmartPlugImports
        // (different CreatedAtUtc) BEFORE the migration under test is applied, then asserts
        // exactly one row per duplicate group survives — the one belonging to the
        // more-recently-created import — and that the new unique index now rejects a fresh
        // duplicate insert attempt.
        var householdId = Guid.NewGuid();
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer"));

        await using var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
        var migrator = dbContext.GetInfrastructure().GetRequiredService<IMigrator>();

        await migrator.MigrateAsync("20260820102449_AddSmartPlugImportGaps", TestContext.Current.CancellationToken);

        var now = DateTimeOffset.UtcNow;
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = now });
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
        dbContext.BackgroundJobs.AddRange(olderJob, newerJob);
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
        // the same two imports. EF Core's SqlServer provider auto-filters the composite index
        // above to WHERE [PowerPointId] IS NOT NULL, so — contrary to Dev Notes' original
        // assumption — it never protects these either; the migration's second, NULL-scoped
        // cleanup + partial unique index does, identically to Postgres.
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
}
