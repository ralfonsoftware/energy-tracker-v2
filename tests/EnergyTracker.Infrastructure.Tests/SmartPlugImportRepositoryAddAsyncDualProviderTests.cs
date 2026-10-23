using System.Data.Common;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

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

    // Opens the database migrated only up to the (single) migration whose name ends in the given
    // suffix — the checkpoint mechanism PostgresMigrationTests/SqlServerMigrationTests already use.
    protected abstract Task<EnergyTrackerDbContext> OpenDbContextAtMigrationAsync(
        Guid householdId, string migrationNameSuffix, CancellationToken cancellationToken);

    protected static async Task MigrateToAsync(EnergyTrackerDbContext dbContext, string migrationNameSuffix, CancellationToken cancellationToken)
    {
        var target = dbContext.Database.GetMigrations().Single(m => m.EndsWith(migrationNameSuffix, StringComparison.Ordinal));
        await dbContext.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(target, cancellationToken);
    }

    // Mirrors UpsertAwaitingMappingReadingsAsync's own per-provider chunk size (Story 3.9 review
    // fix) — deliberately kept as a second, independent literal here rather than reflecting into
    // the production constant, so this test still catches a chunk-size change that wasn't also
    // reflected here as a real assertion failure, not a silently-adjusted expectation.
    protected abstract int AwaitingMappingUpsertChunkSize { get; }

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
        Guid householdId, Guid smartPlugImportId, Guid? powerPointId, DateTimeOffset intervalStart, decimal kwhValue = 0.5m,
        string deviceName = "Fridge") => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        SmartPlugImportId = smartPlugImportId,
        PowerPointId = powerPointId,
        RoomName = "Kitchen",
        PowerPointName = "Fridge",
        DeviceName = deviceName,
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
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

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
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

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
    public async Task AddAsync_upserts_an_AwaitingPowerPointMapping_batch_spanning_more_than_one_chunk()
    {
        // Regression guard for the live-E2E-found bug (Completion Notes): an unmatched batch large
        // enough to blow past a single raw-SQL statement's parameter limit was silently unchunked
        // before the fix. This proves the chunking loop itself — every row across the chunk
        // boundary persists exactly once, none dropped, none duplicated — not just that a single
        // chunk's worth of rows works.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);
        var backgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var import = MakeImport(householdId, backgroundJobId, deviceTag: "Unknown Plug");
        var rowCount = AwaitingMappingUpsertChunkSize + 250;
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        IReadOnlyList<SmartPlugReading> readings = Enumerable.Range(0, rowCount)
            .Select(i => MakeReading(householdId, import.Id, powerPointId: null, start.AddMinutes(10 * i)))
            .ToList();

        await repository.AddAsync(import, readings, TestContext.Current.CancellationToken);

        await using var verifyDbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var persistedCount = await verifyDbContext.SmartPlugReadings
            .CountAsync(r => r.SmartPlugImportId == import.Id, TestContext.Current.CancellationToken);
        persistedCount.ShouldBe(rowCount);
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
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

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
    public async Task AddAsync_persists_nothing_when_already_cancelled_before_the_call()
    {
        // Story 3.9 review fix: this only proves a pre-cancelled token stops AddAsync before any
        // row is touched (BeginTransactionAsync itself throws OperationCanceledException) — it
        // does NOT exercise cancellation mid-write (a genuinely in-flight BulkInsertOrUpdateAsync/
        // raw-SQL upsert being interrupted), despite this file's Completion Notes once describing
        // it that way. The actual rollback guarantee for a real mid-write failure is instead
        // covered by AddAsync's own `await using` transaction (no CommitAsync call is ever reached
        // before an exception propagates) and doesn't depend on cancellation specifically — any
        // exception thrown while a chunked upsert is in flight rolls back the same way.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);
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
    public async Task AddAsync_rolls_back_the_entire_batch_when_a_later_chunk_fails_after_an_earlier_chunk_already_wrote()
    {
        // The genuine mid-write rollback test the pre-cancellation test above cannot be: two rows
        // deliberately share the same Id (the primary key) across the chunk boundary — the first
        // chunk's statement succeeds and physically writes inside the ambient transaction, then the
        // second chunk's statement throws a primary-key violation. Asserts the whole transaction —
        // the parent import row AND every reading from the already-succeeded first chunk — rolls
        // back, not just the failing second chunk. Forces a genuinely multi-chunk batch via
        // AwaitingMappingUpsertChunkSize so the PK-colliding row actually lands in a later chunk.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);
        var backgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var import = MakeImport(householdId, backgroundJobId, deviceTag: "Unknown Plug");
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var duplicateId = Guid.NewGuid();
        var readings = Enumerable.Range(0, AwaitingMappingUpsertChunkSize + 10)
            .Select(i => MakeReading(householdId, import.Id, powerPointId: null, start.AddMinutes(10 * i)))
            .ToList();
        readings[0] = new SmartPlugReading
        {
            Id = duplicateId,
            HouseholdId = householdId,
            SmartPlugImportId = import.Id,
            PowerPointId = null,
            RoomName = readings[0].RoomName,
            PowerPointName = readings[0].PowerPointName,
            DeviceName = readings[0].DeviceName,
            IntervalStart = readings[0].IntervalStart,
            IntervalEnd = readings[0].IntervalEnd,
            KwhValue = readings[0].KwhValue,
        };
        // Same Id as readings[0] — placed in a later chunk (chunk boundary is
        // AwaitingMappingUpsertChunkSize rows) so the first chunk's INSERT already succeeded
        // server-side before this one's primary-key violation throws.
        readings[AwaitingMappingUpsertChunkSize + 5] = new SmartPlugReading
        {
            Id = duplicateId,
            HouseholdId = householdId,
            SmartPlugImportId = import.Id,
            PowerPointId = null,
            RoomName = readings[AwaitingMappingUpsertChunkSize + 5].RoomName,
            PowerPointName = readings[AwaitingMappingUpsertChunkSize + 5].PowerPointName,
            DeviceName = readings[AwaitingMappingUpsertChunkSize + 5].DeviceName,
            IntervalStart = readings[AwaitingMappingUpsertChunkSize + 5].IntervalStart,
            IntervalEnd = readings[AwaitingMappingUpsertChunkSize + 5].IntervalEnd,
            KwhValue = readings[AwaitingMappingUpsertChunkSize + 5].KwhValue,
        };

        await Should.ThrowAsync<Exception>(() => repository.AddAsync(import, readings, TestContext.Current.CancellationToken));

        await using var verifyDbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        (await verifyDbContext.SmartPlugImports.SingleOrDefaultAsync(
            i => i.Id == import.Id, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await verifyDbContext.SmartPlugReadings.CountAsync(
            r => r.HouseholdId == householdId, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task AddAsync_clears_the_change_tracker_on_failure_so_the_same_DbContext_can_retry_AddAsync_with_the_same_import_Id()
    {
        // Regression guard for the live-E2E-found masked-exception bug (Completion Notes): without
        // dbContext.ChangeTracker.Clear() in AddAsync's catch block, a caller reusing the same
        // scoped DbContext to persist a NEW SmartPlugImport with the SAME Id after AddAsync's own
        // DB transaction rolled back — exactly ProcessSmartPlugImport.PersistFailedImportAsync's
        // real pattern, same importId as the SmartPlugImportId the whole job carries — hits a
        // second, unrelated "already being tracked" InvalidOperationException that masks the real
        // one. Forces the first AddAsync to fail via a within-batch primary-key collision (two
        // readings sharing the same Id, different IntervalStart, so DeduplicateByMatchKey doesn't
        // drop either) rather than relying on any specific exception type.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);
        var backgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var importId = Guid.NewGuid();
        var import = new SmartPlugImport
        {
            Id = importId,
            HouseholdId = householdId,
            BackgroundJobId = backgroundJobId,
            VendorFormat = SmartPlugVendorFormat.EveHome,
            OriginalFileName = "export.xlsx",
            Status = SmartPlugImportStatus.Completed,
            DeviceTag = "Fridge",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
        var readingA = MakeReading(householdId, importId, powerPointId, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var readingB = new SmartPlugReading
        {
            Id = readingA.Id,
            HouseholdId = householdId,
            SmartPlugImportId = importId,
            PowerPointId = powerPointId,
            RoomName = readingA.RoomName,
            PowerPointName = readingA.PowerPointName,
            DeviceName = readingA.DeviceName,
            IntervalStart = readingA.IntervalStart.AddDays(1),
            IntervalEnd = readingA.IntervalStart.AddDays(1),
            KwhValue = readingA.KwhValue,
        };

        await Should.ThrowAsync<Exception>(() => repository.AddAsync(import, [readingA, readingB], TestContext.Current.CancellationToken));

        var failedImport = new SmartPlugImport
        {
            Id = importId,
            HouseholdId = householdId,
            BackgroundJobId = backgroundJobId,
            VendorFormat = SmartPlugVendorFormat.EveHome,
            OriginalFileName = "export.xlsx",
            Status = SmartPlugImportStatus.Failed,
            DeviceTag = string.Empty,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };

        // The regression this test guards: without ChangeTracker.Clear(), this throws a masking
        // "already being tracked" InvalidOperationException instead of persisting cleanly.
        await repository.AddAsync(failedImport, [], TestContext.Current.CancellationToken);

        await using var verifyDbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var persisted = await verifyDbContext.SmartPlugImports.SingleAsync(
            i => i.Id == importId, TestContext.Current.CancellationToken);
        persisted.Status.ShouldBe(SmartPlugImportStatus.Failed);
    }

    [Fact]
    public async Task AddAsync_does_not_duplicate_or_throw_when_the_incoming_batch_has_a_within_batch_match_key_collision()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);
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

    [Fact]
    public async Task AddAsync_AwaitingPowerPointMapping_upsert_never_cross_matches_between_two_different_households()
    {
        // AD-3 (Story 3.9 review coverage gap): the raw-SQL upsert path bypasses EF Core's global
        // query filter by construction (it's not a LINQ query) — its own ON CONFLICT/MERGE
        // predicate must include HouseholdId itself to stay tenant-isolated. Proves it empirically
        // rather than only asserting it in a code comment: two households' unmapped readings share
        // the exact same IntervalStart, and neither write is allowed to match/overwrite the other's.
        var householdA = Guid.NewGuid();
        var householdB = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdA, TestContext.Current.CancellationToken);
        await SeedPowerPointAsync(dbContext, householdA, TestContext.Current.CancellationToken);
        await SeedPowerPointAsync(dbContext, householdB, TestContext.Current.CancellationToken);
        var backgroundJobIdA = await SeedBackgroundJobAsync(dbContext, householdA, TestContext.Current.CancellationToken);
        var backgroundJobIdB = await SeedBackgroundJobAsync(dbContext, householdB, TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);
        var sharedIntervalStart = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        var importB = MakeImport(householdB, backgroundJobIdB, deviceTag: "Unknown Plug");
        var existingReadingB = MakeReading(householdB, importB.Id, powerPointId: null, sharedIntervalStart, kwhValue: 0.2m);
        await repository.AddAsync(importB, [existingReadingB], TestContext.Current.CancellationToken);

        var importA = MakeImport(householdA, backgroundJobIdA, deviceTag: "Unknown Plug");
        var newReadingA = MakeReading(householdA, importA.Id, powerPointId: null, sharedIntervalStart, kwhValue: 0.9m);
        await repository.AddAsync(importA, [newReadingA], TestContext.Current.CancellationToken);

        await using var verifyContextA = await OpenMigratedDbContextAsync(householdA, TestContext.Current.CancellationToken);
        var persistedA = await verifyContextA.SmartPlugReadings
            .Where(r => r.HouseholdId == householdA && r.IntervalStart == sharedIntervalStart)
            .ToListAsync(TestContext.Current.CancellationToken);
        persistedA.ShouldHaveSingleItem();
        persistedA[0].Id.ShouldBe(newReadingA.Id);
        persistedA[0].KwhValue.ShouldBe(0.9m);

        await using var verifyContextB = await OpenMigratedDbContextAsync(householdB, TestContext.Current.CancellationToken);
        var persistedB = await verifyContextB.SmartPlugReadings
            .Where(r => r.HouseholdId == householdB && r.IntervalStart == sharedIntervalStart)
            .ToListAsync(TestContext.Current.CancellationToken);
        persistedB.ShouldHaveSingleItem();
        persistedB[0].Id.ShouldBe(existingReadingB.Id);
        // Untouched by household A's write — the regression this test guards against.
        persistedB[0].KwhValue.ShouldBe(0.2m);
    }

    // --- Story 11.4, AD-20 (amended 2026-10-10), audit C9: the unmapped-reading key is
    // (HouseholdId, DeviceName, IntervalStart) WHERE PowerPointId IS NULL, so two different plugs'
    // files covering the same period keep their own readings until each is mapped. ---

    // The migration that adds the (HouseholdId, DeviceName, IntervalStart) index beside the old one.
    // Pinned here so the transitional-state test stays valid after the old index is dropped.
    protected const string DeployNMigrationSuffix = "_NarrowSmartPlugReadingDeviceNameKeyUnmappedByDevice";

    private static readonly DateTimeOffset PlugPeriodStart = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private static SmartPlugImportRepository NewRepository(EnergyTrackerDbContext dbContext) =>
        new(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

    private static List<SmartPlugReading> MakePlugReadings(
        Guid householdId, Guid importId, string deviceName, decimal firstKwhValue, int count = 3) =>
        Enumerable.Range(0, count)
            .Select(i => MakeReading(
                householdId, importId, powerPointId: null, PlugPeriodStart.AddMinutes(10 * i),
                kwhValue: firstKwhValue + 0.1m * i, deviceName: deviceName))
            .ToList();

    private static async Task<Guid> SeedAdditionalPowerPointAsync(
        EnergyTrackerDbContext dbContext, Guid householdId, string name, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var room = new Room { Id = Guid.NewGuid(), HouseholdId = householdId, Name = $"{name} room", CreatedAtUtc = now };
        dbContext.Rooms.Add(room);
        var powerPoint = new PowerPoint { Id = Guid.NewGuid(), HouseholdId = householdId, RoomId = room.Id, Name = name, CreatedAtUtc = now };
        dbContext.PowerPoints.Add(powerPoint);
        await dbContext.SaveChangesAsync(cancellationToken);
        return powerPoint.Id;
    }

    private static async Task<(SmartPlugImport Import, List<SmartPlugReading> Readings)> AddUnmappedPlugImportAsync(
        EnergyTrackerDbContext dbContext, SmartPlugImportRepository repository, Guid householdId, string deviceName,
        decimal firstKwhValue, CancellationToken cancellationToken)
    {
        var backgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, cancellationToken);
        var import = MakeImport(householdId, backgroundJobId, deviceTag: deviceName);
        var readings = MakePlugReadings(householdId, import.Id, deviceName, firstKwhValue);
        await repository.AddAsync(import, readings, cancellationToken);
        return (import, readings);
    }

    [Fact]
    public async Task AddAsync_keeps_each_plugs_unmapped_readings_when_two_plugs_cover_the_same_period_and_attributes_them_once_mapped()
    {
        // AC #5 main case. Before this story the second plug's rows replaced the first plug's for every
        // shared IntervalStart (audit C9): 3 rows survived, all attributed to plug B.
        var householdId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, ct);
        var powerPointX = await SeedPowerPointAsync(dbContext, householdId, ct);
        var powerPointY = await SeedAdditionalPowerPointAsync(dbContext, householdId, "Lamp", ct);
        var repository = NewRepository(dbContext);

        var (importA, readingsA) = await AddUnmappedPlugImportAsync(dbContext, repository, householdId, "Plug A", firstKwhValue: 0.1m, ct);
        var (importB, readingsB) = await AddUnmappedPlugImportAsync(dbContext, repository, householdId, "Plug B", firstKwhValue: 0.7m, ct);

        await using (var unmappedContext = await OpenMigratedDbContextAsync(householdId, ct))
        {
            var unmapped = await unmappedContext.SmartPlugReadings
                .Where(r => r.HouseholdId == householdId && r.PowerPointId == null)
                .ToListAsync(ct);
            unmapped.Count.ShouldBe(6);
            var fromA = unmapped.Where(r => r.DeviceName == "Plug A").OrderBy(r => r.IntervalStart).ToList();
            fromA.Select(r => r.Id).ShouldBe(readingsA.Select(r => r.Id), ignoreOrder: true);
            fromA.ShouldAllBe(r => r.SmartPlugImportId == importA.Id);
            fromA.Select(r => r.KwhValue).ShouldBe([0.1m, 0.2m, 0.3m]);
            var fromB = unmapped.Where(r => r.DeviceName == "Plug B").OrderBy(r => r.IntervalStart).ToList();
            fromB.Select(r => r.Id).ShouldBe(readingsB.Select(r => r.Id), ignoreOrder: true);
            fromB.ShouldAllBe(r => r.SmartPlugImportId == importB.Id);
            fromB.Select(r => r.KwhValue).ShouldBe([0.7m, 0.8m, 0.9m]);
        }

        await repository.UpdateMappingAsync(importA, powerPointX, "Fridge", "Kitchen", ct);
        await repository.UpdateMappingAsync(importB, powerPointY, "Lamp", "Lamp room", ct);

        await using var verifyContext = await OpenMigratedDbContextAsync(householdId, ct);
        var all = await verifyContext.SmartPlugReadings.Where(r => r.HouseholdId == householdId).ToListAsync(ct);
        all.Count.ShouldBe(6);
        var mappedA = all.Where(r => r.DeviceName == "Plug A").ToList();
        mappedA.Count.ShouldBe(3);
        mappedA.ShouldAllBe(r => r.PowerPointId == powerPointX && r.SmartPlugImportId == importA.Id);
        mappedA.Select(r => r.Id).ShouldBe(readingsA.Select(r => r.Id), ignoreOrder: true);
        var mappedB = all.Where(r => r.DeviceName == "Plug B").ToList();
        mappedB.Count.ShouldBe(3);
        mappedB.ShouldAllBe(r => r.PowerPointId == powerPointY && r.SmartPlugImportId == importB.Id);
        mappedB.Select(r => r.Id).ShouldBe(readingsB.Select(r => r.Id), ignoreOrder: true);
    }

    [Fact]
    public async Task AddAsync_updates_the_same_plugs_unmapped_readings_in_place_when_its_file_is_uploaded_again()
    {
        var householdId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, ct);
        await SeedPowerPointAsync(dbContext, householdId, ct);
        var repository = NewRepository(dbContext);
        var (_, originalReadingsA) = await AddUnmappedPlugImportAsync(dbContext, repository, householdId, "Plug A", firstKwhValue: 0.1m, ct);
        var (importB, readingsB) = await AddUnmappedPlugImportAsync(dbContext, repository, householdId, "Plug B", firstKwhValue: 0.7m, ct);

        var (reuploadA, _) = await AddUnmappedPlugImportAsync(dbContext, repository, householdId, "Plug A", firstKwhValue: 0.5m, ct);

        await using var verifyContext = await OpenMigratedDbContextAsync(householdId, ct);
        var all = await verifyContext.SmartPlugReadings.Where(r => r.HouseholdId == householdId).ToListAsync(ct);
        all.Count.ShouldBe(6);
        var plugA = all.Where(r => r.DeviceName == "Plug A").OrderBy(r => r.IntervalStart).ToList();
        plugA.Select(r => r.Id).ShouldBe(originalReadingsA.OrderBy(r => r.IntervalStart).Select(r => r.Id));
        plugA.ShouldAllBe(r => r.SmartPlugImportId == reuploadA.Id && r.PowerPointId == null);
        plugA.Select(r => r.KwhValue).ShouldBe([0.5m, 0.6m, 0.7m]);
        var plugB = all.Where(r => r.DeviceName == "Plug B").OrderBy(r => r.IntervalStart).ToList();
        plugB.Select(r => r.Id).ShouldBe(readingsB.OrderBy(r => r.IntervalStart).Select(r => r.Id));
        plugB.ShouldAllBe(r => r.SmartPlugImportId == importB.Id);
        plugB.Select(r => r.KwhValue).ShouldBe([0.7m, 0.8m, 0.9m]);
    }

    [Fact]
    public async Task At_the_deploy_N_schema_an_overlapping_second_plug_fails_loudly_and_never_overwrites_the_first_plugs_rows()
    {
        // The accepted transitional state of the sprint change proposal (section 3): at deploy N the old
        // (HouseholdId, IntervalStart) index still exists, so a second plug's overlapping unmapped import
        // is rejected by the database (never a silent overwrite) until deploy N+1 drops it. Pinned to N's
        // own migration so it stays valid once N+1 exists.
        var householdId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        await using var dbContext = await OpenDbContextAtMigrationAsync(householdId, DeployNMigrationSuffix, ct);
        await SeedPowerPointAsync(dbContext, householdId, ct);
        var repository = NewRepository(dbContext);
        var (importA, readingsA) = await AddUnmappedPlugImportAsync(dbContext, repository, householdId, "Plug A", firstKwhValue: 0.1m, ct);

        var backgroundJobIdB = await SeedBackgroundJobAsync(dbContext, householdId, ct);
        var importB = MakeImport(householdId, backgroundJobIdB, deviceTag: "Plug B");
        var readingsB = MakePlugReadings(householdId, importB.Id, "Plug B", firstKwhValue: 0.7m);
        await Should.ThrowAsync<DbException>(() => repository.AddAsync(importB, readingsB, ct));

        await using (var afterFailureContext = await OpenDbContextAtMigrationAsync(householdId, DeployNMigrationSuffix, ct))
        {
            var rows = await afterFailureContext.SmartPlugReadings.Where(r => r.HouseholdId == householdId).OrderBy(r => r.IntervalStart).ToListAsync(ct);
            rows.Select(r => r.Id).ShouldBe(readingsA.OrderBy(r => r.IntervalStart).Select(r => r.Id));
            rows.ShouldAllBe(r => r.DeviceName == "Plug A" && r.SmartPlugImportId == importA.Id);
            rows.Select(r => r.KwhValue).ShouldBe([0.1m, 0.2m, 0.3m]);
            (await afterFailureContext.SmartPlugImports.AnyAsync(i => i.Id == importB.Id, ct)).ShouldBeFalse();
        }

        // The same plug's re-upload still works at this schema: only the foreign plug is rejected.
        var (reuploadA, _) = await AddUnmappedPlugImportAsync(dbContext, repository, householdId, "Plug A", firstKwhValue: 0.5m, ct);

        await using var verifyContext = await OpenDbContextAtMigrationAsync(householdId, DeployNMigrationSuffix, ct);
        var afterReupload = await verifyContext.SmartPlugReadings.Where(r => r.HouseholdId == householdId).OrderBy(r => r.IntervalStart).ToListAsync(ct);
        afterReupload.Select(r => r.Id).ShouldBe(readingsA.OrderBy(r => r.IntervalStart).Select(r => r.Id));
        afterReupload.ShouldAllBe(r => r.SmartPlugImportId == reuploadA.Id);
        afterReupload.Select(r => r.KwhValue).ShouldBe([0.5m, 0.6m, 0.7m]);
    }

    [Fact]
    public async Task UpdateMappingAsync_still_collides_on_PowerPointId_IntervalStart_and_leaves_a_different_plugs_divergent_rows_unmapped()
    {
        // AC #4: mapping keeps (PowerPointId, IntervalStart) as its collision key. Two plugs mapped to the
        // SAME Power Point at the same intervals take the classification path (divergent DeviceName/KwhValue
        // = never silently discarded), not a unique-violation 500.
        var householdId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, ct);
        var powerPointX = await SeedPowerPointAsync(dbContext, householdId, ct);
        var repository = NewRepository(dbContext);
        var (importA, readingsA) = await AddUnmappedPlugImportAsync(dbContext, repository, householdId, "Plug A", firstKwhValue: 0.1m, ct);
        var (importB, readingsB) = await AddUnmappedPlugImportAsync(dbContext, repository, householdId, "Plug B", firstKwhValue: 0.7m, ct);

        await repository.UpdateMappingAsync(importA, powerPointX, "Fridge", "Kitchen", ct);
        await Should.NotThrowAsync(() => repository.UpdateMappingAsync(importB, powerPointX, "Fridge", "Kitchen", ct));

        await using var verifyContext = await OpenMigratedDbContextAsync(householdId, ct);
        var all = await verifyContext.SmartPlugReadings.Where(r => r.HouseholdId == householdId).ToListAsync(ct);
        all.Count.ShouldBe(6);
        var plugA = all.Where(r => r.DeviceName == "Plug A").ToList();
        plugA.Select(r => r.Id).ShouldBe(readingsA.Select(r => r.Id), ignoreOrder: true);
        plugA.ShouldAllBe(r => r.PowerPointId == powerPointX);
        var plugB = all.Where(r => r.DeviceName == "Plug B").ToList();
        plugB.Select(r => r.Id).ShouldBe(readingsB.Select(r => r.Id), ignoreOrder: true);
        plugB.ShouldAllBe(r => r.PowerPointId == null);
    }

    [Fact]
    public async Task AddAsync_does_not_duplicate_or_throw_when_an_unmapped_batch_has_a_within_batch_match_key_collision()
    {
        var householdId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, ct);
        await SeedPowerPointAsync(dbContext, householdId, ct);
        var repository = NewRepository(dbContext);
        var backgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, ct);
        var import = MakeImport(householdId, backgroundJobId, deviceTag: "Plug A");
        var sharedIntervalStart = new DateTimeOffset(2026, 11, 1, 2, 30, 0, TimeSpan.Zero);
        var first = MakeReading(householdId, import.Id, powerPointId: null, sharedIntervalStart, kwhValue: 0.3m, deviceName: "Plug A");
        var second = MakeReading(householdId, import.Id, powerPointId: null, sharedIntervalStart, kwhValue: 0.5m, deviceName: "Plug A");

        await repository.AddAsync(import, [first, second], ct);

        await using var verifyContext = await OpenMigratedDbContextAsync(householdId, ct);
        var persisted = await verifyContext.SmartPlugReadings
            .Where(r => r.HouseholdId == householdId && r.PowerPointId == null && r.IntervalStart == sharedIntervalStart)
            .ToListAsync(ct);
        persisted.ShouldHaveSingleItem().Id.ShouldBe(first.Id);
    }
}

public class PostgresSmartPlugImportRepositoryAddAsyncDualProviderTests : SmartPlugImportRepositoryAddAsyncDualProviderTestsBase, IAsyncLifetime
{
    private readonly PostgresTestDatabase _container = new();

    protected override int AwaitingMappingUpsertChunkSize => 5_000;

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

    protected override async Task<EnergyTrackerDbContext> OpenDbContextAtMigrationAsync(
        Guid householdId, string migrationNameSuffix, CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));
        var dbContext = NewDbContext(optionsBuilder.Options, householdId);
        await MigrateToAsync(dbContext, migrationNameSuffix, cancellationToken);
        return dbContext;
    }
}

public class SqlServerSmartPlugImportRepositoryAddAsyncDualProviderTests : SmartPlugImportRepositoryAddAsyncDualProviderTestsBase, IAsyncLifetime
{
    private readonly SqlServerTestDatabase _container = new();

    protected override int AwaitingMappingUpsertChunkSize => 200;

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

    protected override async Task<EnergyTrackerDbContext> OpenDbContextAtMigrationAsync(
        Guid householdId, string migrationNameSuffix, CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer"));
        var dbContext = NewDbContext(optionsBuilder.Options, householdId);
        await MigrateToAsync(dbContext, migrationNameSuffix, cancellationToken);
        return dbContext;
    }
}
