using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

// AD-23's two write paths (primary BulkInsertOrUpdateAsync match key, and the
// AwaitingPowerPointMapping raw-SQL upsert — AD-2's one narrow named exception) are genuinely
// provider-specific under the hood (BulkInsertOrUpdateAsync's SqlBulkCopy+MERGE vs. COPY BINARY+
// ON CONFLICT internals; the raw-SQL upsert's own hand-written MERGE vs. INSERT...ON CONFLICT
// statements). Most of SmartPlugImportRepositoryTests.cs only exercises Postgres (existing
// codebase convention) — this file follows MeterReadingRepositoryTests.cs's own dual-provider
// pattern instead, specifically for AddAsync, since a bug here would otherwise only ever surface
// against one provider's real database, never the other's.
public abstract class SmartPlugImportRepositoryAddAsyncDualProviderTestsBase
{
    private sealed class FixedHouseholdAccessor(Guid householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;

        public Guid? HouseholdMemberId => null;
    }

    protected abstract Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken);

    protected static EnergyTrackerDbContext NewDbContext(DbContextOptions<EnergyTrackerDbContext> options, Guid householdId) =>
        new(options, new FixedHouseholdAccessor(householdId));

    private static async Task<Guid> SeedPowerPointAsync(EnergyTrackerDbContext dbContext, Guid householdId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = now });
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

    private static SmartPlugReading MakeReading(
        Guid householdId, Guid smartPlugImportId, Guid? powerPointId, DateTimeOffset intervalStart, decimal kwhValue = 0.5m) => new()
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
        KwhValue = kwhValue,
    };

    [Fact]
    public async Task AddAsync_inserts_and_upserts_via_the_primary_PowerPointId_IntervalStart_match_key()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var existingBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var existingImport = MakeImport(householdId, existingBackgroundJobId);
        var collidingIntervalStart = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        dbContext.SmartPlugImports.Add(existingImport);
        var existingReading = MakeReading(householdId, existingImport.Id, powerPointId, collidingIntervalStart, kwhValue: 0.1m);
        dbContext.SmartPlugReadings.Add(existingReading);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, NullLogger<SmartPlugImportRepository>.Instance);

        var newBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var newImport = MakeImport(householdId, newBackgroundJobId);
        var collidingReading = MakeReading(householdId, newImport.Id, powerPointId, collidingIntervalStart, kwhValue: 0.9m);
        var newReading = MakeReading(householdId, newImport.Id, powerPointId, collidingIntervalStart.AddDays(1));

        await repository.AddAsync(newImport, [collidingReading, newReading], TestContext.Current.CancellationToken);

        await using var verifyDbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var persisted = await verifyDbContext.SmartPlugReadings
            .Where(r => r.PowerPointId == powerPointId)
            .ToListAsync(TestContext.Current.CancellationToken);
        persisted.Count.ShouldBe(2);
        var upserted = persisted.Single(r => r.IntervalStart == collidingIntervalStart);
        upserted.Id.ShouldBe(existingReading.Id);
        upserted.KwhValue.ShouldBe(0.9m);
        upserted.SmartPlugImportId.ShouldBe(newImport.Id);
        persisted.Any(r => r.IntervalStart == newReading.IntervalStart).ShouldBeTrue();
    }

    [Fact]
    public async Task AddAsync_inserts_and_upserts_an_AwaitingPowerPointMapping_batch_via_the_raw_SQL_path()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var existingBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var existingImport = MakeImport(householdId, existingBackgroundJobId, deviceTag: "Unknown Plug");
        var collidingIntervalStart = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        dbContext.SmartPlugImports.Add(existingImport);
        var existingReading = MakeReading(householdId, existingImport.Id, powerPointId: null, collidingIntervalStart, kwhValue: 0.1m);
        dbContext.SmartPlugReadings.Add(existingReading);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, NullLogger<SmartPlugImportRepository>.Instance);

        var newBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var newImport = MakeImport(householdId, newBackgroundJobId, deviceTag: "Unknown Plug");
        var collidingReading = MakeReading(householdId, newImport.Id, powerPointId: null, collidingIntervalStart, kwhValue: 0.9m);
        var newReading = MakeReading(householdId, newImport.Id, powerPointId: null, collidingIntervalStart.AddDays(1));

        await repository.AddAsync(newImport, [collidingReading, newReading], TestContext.Current.CancellationToken);

        await using var verifyDbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var persisted = await verifyDbContext.SmartPlugReadings
            .Where(r => r.HouseholdId == householdId && r.PowerPointId == null)
            .ToListAsync(TestContext.Current.CancellationToken);
        persisted.Count.ShouldBe(2);
        var upserted = persisted.Single(r => r.IntervalStart == collidingIntervalStart);
        upserted.Id.ShouldBe(existingReading.Id);
        upserted.KwhValue.ShouldBe(0.9m);
        upserted.SmartPlugImportId.ShouldBe(newImport.Id);
        persisted.Any(r => r.IntervalStart == newReading.IntervalStart).ShouldBeTrue();
    }

    [Fact]
    public async Task AddAsync_never_confuses_an_AwaitingPowerPointMapping_reading_with_a_same_timestamp_mapped_reading()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var mappedBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var mappedImport = MakeImport(householdId, mappedBackgroundJobId);
        var sharedIntervalStart = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        dbContext.SmartPlugImports.Add(mappedImport);
        dbContext.SmartPlugReadings.Add(MakeReading(householdId, mappedImport.Id, powerPointId, sharedIntervalStart, kwhValue: 0.4m));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, NullLogger<SmartPlugImportRepository>.Instance);

        var newBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var newImport = MakeImport(householdId, newBackgroundJobId, deviceTag: "Unknown Plug");
        var unmappedReading = MakeReading(householdId, newImport.Id, powerPointId: null, sharedIntervalStart, kwhValue: 0.8m);

        await repository.AddAsync(newImport, [unmappedReading], TestContext.Current.CancellationToken);

        await using var verifyDbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var allAtTimestamp = await verifyDbContext.SmartPlugReadings
            .Where(r => r.HouseholdId == householdId && r.IntervalStart == sharedIntervalStart)
            .ToListAsync(TestContext.Current.CancellationToken);
        allAtTimestamp.Count.ShouldBe(2);
        allAtTimestamp.Single(r => r.PowerPointId == powerPointId).KwhValue.ShouldBe(0.4m);
        allAtTimestamp.Single(r => r.PowerPointId == null).KwhValue.ShouldBe(0.8m);
    }

    [Fact]
    public async Task AddAsync_wraps_the_parent_import_and_readings_in_one_transaction_that_rolls_back_on_cancellation()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, NullLogger<SmartPlugImportRepository>.Instance);
        var backgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var import = MakeImport(householdId, backgroundJobId);
        var readings = Enumerable.Range(0, 200)
            .Select(i => MakeReading(householdId, import.Id, powerPointId, DateTimeOffset.UtcNow.AddMinutes(-i)))
            .ToList();
        using var alreadyCancelled = new CancellationTokenSource();
        await alreadyCancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            repository.AddAsync(import, readings, alreadyCancelled.Token));

        await using var verifyDbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        (await verifyDbContext.SmartPlugImports.SingleOrDefaultAsync(
            i => i.Id == import.Id, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await verifyDbContext.SmartPlugReadings.CountAsync(
            r => r.PowerPointId == powerPointId, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task AddAsync_does_not_duplicate_or_throw_when_the_incoming_batch_has_a_within_batch_match_key_collision()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, NullLogger<SmartPlugImportRepository>.Instance);
        var backgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var import = MakeImport(householdId, backgroundJobId);
        var sharedIntervalStart = new DateTimeOffset(2026, 11, 1, 2, 30, 0, TimeSpan.Zero);
        var duplicateA = MakeReading(householdId, import.Id, powerPointId, sharedIntervalStart, kwhValue: 0.3m);
        var duplicateB = MakeReading(householdId, import.Id, powerPointId, sharedIntervalStart, kwhValue: 0.5m);

        await repository.AddAsync(import, [duplicateA, duplicateB], TestContext.Current.CancellationToken);

        await using var verifyDbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var persisted = await verifyDbContext.SmartPlugReadings
            .Where(r => r.PowerPointId == powerPointId && r.IntervalStart == sharedIntervalStart)
            .ToListAsync(TestContext.Current.CancellationToken);
        persisted.Count.ShouldBe(1);
    }
}

public class PostgresSmartPlugImportRepositoryAddAsyncDualProviderTests : SmartPlugImportRepositoryAddAsyncDualProviderTestsBase, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    protected override async Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));
        var dbContext = NewDbContext(optionsBuilder.Options, householdId);
        await dbContext.Database.MigrateAsync(cancellationToken);
        return dbContext;
    }
}

public class SqlServerSmartPlugImportRepositoryAddAsyncDualProviderTests : SmartPlugImportRepositoryAddAsyncDualProviderTestsBase, IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    protected override async Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer"));
        var dbContext = NewDbContext(optionsBuilder.Options, householdId);
        await dbContext.Database.MigrateAsync(cancellationToken);
        return dbContext;
    }
}
