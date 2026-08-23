using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

public class SmartPlugImportRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private sealed class FixedHouseholdAccessor(Guid householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;
    }

    private static async Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(
        PostgreSqlContainer container, Guid householdId, CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));

        var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
        await dbContext.Database.MigrateAsync(cancellationToken);
        return dbContext;
    }

    private static async Task<Guid> SeedPowerPointAsync(EnergyTrackerDbContext dbContext, Guid householdId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        dbContext.Households.Add(new Household
        {
            Id = householdId,
            Locale = "en-US",
            Currency = "USD",
            CreatedAtUtc = now,
        });
        var room = new Room { Id = Guid.NewGuid(), HouseholdId = householdId, Name = "Kitchen", CreatedAtUtc = now };
        dbContext.Rooms.Add(room);
        var powerPoint = new PowerPoint { Id = Guid.NewGuid(), HouseholdId = householdId, RoomId = room.Id, Name = "Fridge", CreatedAtUtc = now };
        dbContext.PowerPoints.Add(powerPoint);
        await dbContext.SaveChangesAsync(cancellationToken);
        return powerPoint.Id;
    }

    private static async Task<Guid> SeedBackgroundJobAsync(EnergyTrackerDbContext dbContext, Guid householdId, CancellationToken cancellationToken)
    {
        var backgroundJob = new BackgroundJob
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Completed,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
        dbContext.BackgroundJobs.Add(backgroundJob);
        await dbContext.SaveChangesAsync(cancellationToken);
        return backgroundJob.Id;
    }

    private static SmartPlugImport MakeImport(Guid householdId, Guid backgroundJobId, string deviceTag = "Fridge") => new()
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

    private static SmartPlugReading MakeReading(Guid householdId, Guid smartPlugImportId, Guid? powerPointId, DateTimeOffset intervalStart) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        SmartPlugImportId = smartPlugImportId,
        PowerPointId = powerPointId,
        RoomName = "Kitchen",
        PowerPointName = "Fridge",
        DeviceName = "Fridge",
        IntervalStart = intervalStart,
        IntervalEnd = intervalStart,
        KwhValue = 0.5m,
    };

    [Fact]
    public async Task FindLatestReadingIntervalStartByPowerPointAsync_returns_null_when_no_readings_exist()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, NullLogger<SmartPlugImportRepository>.Instance);

        var result = await repository.FindLatestReadingIntervalStartByPowerPointAsync(powerPointId, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task FindLatestReadingIntervalStartByPowerPointAsync_returns_the_max_IntervalStart()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var backgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var import = MakeImport(householdId, backgroundJobId);
        dbContext.SmartPlugImports.Add(import);
        var older = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        dbContext.SmartPlugReadings.AddRange(
            MakeReading(householdId, import.Id, powerPointId, older),
            MakeReading(householdId, import.Id, powerPointId, newer));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, NullLogger<SmartPlugImportRepository>.Instance);

        var result = await repository.FindLatestReadingIntervalStartByPowerPointAsync(powerPointId, TestContext.Current.CancellationToken);

        result.ShouldBe(newer);
    }

    [Fact]
    public async Task AddAsync_persists_the_import_and_skips_only_the_colliding_reading_on_a_unique_constraint_conflict()
    {
        // Task 3/Dev Notes Open Question #2 (Option A) — the only test that exercises the
        // conflict-tolerant fallback path at all: seeds one pre-existing SmartPlugReading at a
        // given (PowerPointId, IntervalStart), then calls AddAsync with a new import whose
        // reading set includes one row colliding on that exact key plus several non-colliding
        // rows.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var existingBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var existingImport = MakeImport(householdId, existingBackgroundJobId);
        var collidingIntervalStart = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        dbContext.SmartPlugImports.Add(existingImport);
        dbContext.SmartPlugReadings.Add(MakeReading(householdId, existingImport.Id, powerPointId, collidingIntervalStart));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, NullLogger<SmartPlugImportRepository>.Instance);

        var newBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var newImport = MakeImport(householdId, newBackgroundJobId);
        var collidingReading = MakeReading(householdId, newImport.Id, powerPointId, collidingIntervalStart);
        var nonCollidingReadings = new[]
        {
            MakeReading(householdId, newImport.Id, powerPointId, collidingIntervalStart.AddDays(1)),
            MakeReading(householdId, newImport.Id, powerPointId, collidingIntervalStart.AddDays(2)),
        };
        IReadOnlyList<SmartPlugReading> newReadings = [collidingReading, .. nonCollidingReadings];

        await repository.AddAsync(newImport, newReadings, TestContext.Current.CancellationToken);

        await using var verifyDbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        (await verifyDbContext.SmartPlugImports.SingleOrDefaultAsync(
            i => i.Id == newImport.Id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        var persistedNewImportReadings = await verifyDbContext.SmartPlugReadings
            .Where(r => r.SmartPlugImportId == newImport.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        persistedNewImportReadings.Count.ShouldBe(2);
        persistedNewImportReadings.Select(r => r.IntervalStart).ShouldBe(
            nonCollidingReadings.Select(r => r.IntervalStart), ignoreOrder: true);
    }

    [Fact]
    public async Task AddAsync_persists_a_large_incremental_batch_when_the_power_point_already_has_prior_readings()
    {
        // Review-round-2 patch regression guard: AnyExistingReadingAtSameKeyAsync's own existence
        // gate is true for essentially every incremental re-import (any Power Point with prior
        // data), not just rare races — so its intervalStarts.Contains(...) conflict pre-check runs
        // on this story's own steady-state common case. Proves it holds up for a realistically
        // large incremental catch-up batch, not just the handful of rows the collision test above
        // uses.
        const int BatchSize = 2_000;
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var existingBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var existingImport = MakeImport(householdId, existingBackgroundJobId);
        dbContext.SmartPlugImports.Add(existingImport);
        // One prior reading is enough to make AnyExistingReadingAtSameKeyAsync's existence gate
        // true for the whole batch below, exercising its intervalStarts.Contains(...) query.
        dbContext.SmartPlugReadings.Add(
            MakeReading(householdId, existingImport.Id, powerPointId, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, NullLogger<SmartPlugImportRepository>.Instance);

        var newBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var newImport = MakeImport(householdId, newBackgroundJobId);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        IReadOnlyList<SmartPlugReading> newReadings = Enumerable.Range(0, BatchSize)
            .Select(i => MakeReading(householdId, newImport.Id, powerPointId, start.AddMinutes(10 * i)))
            .ToList();

        await repository.AddAsync(newImport, newReadings, TestContext.Current.CancellationToken);

        await using var verifyDbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var persistedCount = await verifyDbContext.SmartPlugReadings
            .CountAsync(r => r.SmartPlugImportId == newImport.Id, TestContext.Current.CancellationToken);
        persistedCount.ShouldBe(BatchSize);
    }

    [Fact]
    public async Task UpdateMappingAsync_persists_the_import_status_and_skips_only_the_colliding_reading_on_a_unique_constraint_conflict()
    {
        // Dev Notes Open Question #4 ("fix it now", confirmed with Ralf during dev-story
        // activation): an AwaitingPowerPointMapping import sits with a reading at the same
        // IntervalStart a different, already-mapped import for the same target Power Point
        // already holds. The set-based UPDATE this method normally uses would reject that as one
        // all-or-nothing statement (a unique-constraint DbUpdateException) — this asserts the
        // per-row conflict-tolerant fallback instead: the colliding reading is skipped (stays
        // unmapped), the non-colliding reading is attached, and the import's own Status/
        // CompletedAtUtc change is still persisted.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var existingBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var existingImport = MakeImport(householdId, existingBackgroundJobId);
        var collidingIntervalStart = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        dbContext.SmartPlugImports.Add(existingImport);
        dbContext.SmartPlugReadings.Add(MakeReading(householdId, existingImport.Id, powerPointId, collidingIntervalStart));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var awaitingBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var awaitingImport = MakeImport(householdId, awaitingBackgroundJobId);
        awaitingImport.Status = SmartPlugImportStatus.AwaitingPowerPointMapping;
        dbContext.SmartPlugImports.Add(awaitingImport);
        dbContext.SmartPlugReadings.AddRange(
            MakeReading(householdId, awaitingImport.Id, powerPointId: null, collidingIntervalStart),
            MakeReading(householdId, awaitingImport.Id, powerPointId: null, collidingIntervalStart.AddDays(1)));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new SmartPlugImportRepository(dbContext, NullLogger<SmartPlugImportRepository>.Instance);
        awaitingImport.Status = SmartPlugImportStatus.Completed;
        awaitingImport.CompletedAtUtc = DateTimeOffset.UtcNow;

        await repository.UpdateMappingAsync(awaitingImport, powerPointId, "Fridge", "Kitchen", TestContext.Current.CancellationToken);

        await using var verifyDbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var persistedImport = await verifyDbContext.SmartPlugImports.SingleAsync(
            i => i.Id == awaitingImport.Id, TestContext.Current.CancellationToken);
        persistedImport.Status.ShouldBe(SmartPlugImportStatus.Completed);

        var persistedReadings = await verifyDbContext.SmartPlugReadings
            .Where(r => r.SmartPlugImportId == awaitingImport.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        persistedReadings.Single(r => r.IntervalStart == collidingIntervalStart).PowerPointId.ShouldBeNull();
        persistedReadings.Single(r => r.IntervalStart == collidingIntervalStart.AddDays(1)).PowerPointId.ShouldBe(powerPointId);
    }

    [Fact]
    public async Task UpdateMappingAsync_raises_the_command_timeout_past_the_30s_ADO_NET_default()
    {
        var householdId = Guid.NewGuid();
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));

        await using var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
        await dbContext.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var import = new SmartPlugImport
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            BackgroundJobId = Guid.NewGuid(),
            VendorFormat = SmartPlugVendorFormat.EveHome,
            OriginalFileName = "export.xlsx",
            Status = SmartPlugImportStatus.AwaitingPowerPointMapping,
            DeviceTag = "Kitchen Plug",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var repository = new SmartPlugImportRepository(dbContext, NullLogger<SmartPlugImportRepository>.Instance);

        // A large Eve Home export's set-based mapping UPDATE reliably exceeded the ADO.NET
        // default 30s command timeout against Basic-tier Azure SQL in production, surfacing as an
        // unhandled 500 on POST /api/smart-plug-imports/{id}/power-point-mapping. This asserts the
        // timeout the repository configures, not the query plan/duration itself — reproducing a
        // real multi-minute Basic-tier timeout in a fast test isn't practical (root cause is a
        // resource-tier/config mismatch, not app logic verifiable via a small dataset).
        await repository.UpdateMappingAsync(
            import, Guid.NewGuid(), "Fridge", "Kitchen", TestContext.Current.CancellationToken);

        dbContext.Database.GetCommandTimeout().ShouldBe(180);
    }
}
