using System.Data.Common;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

        public Guid? HouseholdMemberId => null;
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

    // No migration call here deliberately — the caller must migrate first via
    // OpenMigratedDbContextAsync on an already-schema'd container/database, so an interceptor
    // attached here only ever observes the repository's own commands, never MigrateAsync's DDL.
    private static EnergyTrackerDbContext OpenDbContextWithInterceptor(
        PostgreSqlContainer container, Guid householdId, IInterceptor interceptor)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));
        optionsBuilder.AddInterceptors(interceptor);
        return new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
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

    private static SmartPlugReading MakeReading(
        Guid householdId, Guid smartPlugImportId, Guid? powerPointId, DateTimeOffset intervalStart,
        decimal kwhValue = 0.5m, DateTimeOffset? intervalEnd = null, string deviceName = "Fridge") => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        SmartPlugImportId = smartPlugImportId,
        PowerPointId = powerPointId,
        RoomName = "Kitchen",
        PowerPointName = "Fridge",
        DeviceName = deviceName,
        IntervalStart = intervalStart,
        IntervalEnd = intervalEnd ?? intervalStart,
        KwhValue = kwhValue,
    };

    [Fact]
    public async Task FindLatestReadingWatermarkByPowerPointAsync_returns_null_when_no_readings_exist()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        var result = await repository.FindLatestReadingWatermarkByPowerPointAsync(powerPointId, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task FindLatestReadingWatermarkByPowerPointAsync_returns_the_max_IntervalStart()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var backgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var import = MakeImport(householdId, backgroundJobId);
        dbContext.SmartPlugImports.Add(import);
        var older = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var newerReading = MakeReading(householdId, import.Id, powerPointId, newer, kwhValue: 1.25m);
        dbContext.SmartPlugReadings.AddRange(
            MakeReading(householdId, import.Id, powerPointId, older),
            newerReading);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        var result = await repository.FindLatestReadingWatermarkByPowerPointAsync(powerPointId, TestContext.Current.CancellationToken);

        // AD-22: the watermark now carries Id and KwhValue alongside IntervalStart.
        result.ShouldNotBeNull();
        result.Id.ShouldBe(newerReading.Id);
        result.IntervalStart.ShouldBe(newer);
        result.KwhValue.ShouldBe(1.25m);
    }

    [Fact]
    public async Task AddAsync_with_a_boundaryCorrection_updates_only_the_KwhValue_column_and_records_one_audit_correction()
    {
        // AD-22 AC #6/AD-11 (Story 3.9 review fix): the correction and its audit record now apply
        // via AddAsync's boundaryCorrection parameter, inside the same transaction as the rest of
        // the import — touches KwhValue and only KwhValue on the target row (never RoomName/
        // PowerPointName/DeviceName, AD-10's by-value snapshot fields), and records exactly one
        // AuditCorrection row via the shared IAuditCorrectionRecorder mechanism.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var backgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var import = MakeImport(householdId, backgroundJobId);
        dbContext.SmartPlugImports.Add(import);
        var reading = MakeReading(householdId, import.Id, powerPointId, DateTimeOffset.UtcNow, kwhValue: 0.5m);
        dbContext.SmartPlugReadings.Add(reading);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        dbContext.ChangeTracker.Clear();
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);
        var correctionBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var correctionImport = MakeImport(householdId, correctionBackgroundJobId);
        var correction = new SmartPlugReadingCorrection(householdId, reading.Id, 0.75m, "0.5", "0.75");

        await repository.AddAsync(correctionImport, [], TestContext.Current.CancellationToken, correction);

        await using var verifyDbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var updated = await verifyDbContext.SmartPlugReadings.AsNoTracking().SingleAsync(
            r => r.Id == reading.Id, TestContext.Current.CancellationToken);
        updated.KwhValue.ShouldBe(0.75m);
        updated.RoomName.ShouldBe(reading.RoomName);
        updated.PowerPointName.ShouldBe(reading.PowerPointName);
        updated.DeviceName.ShouldBe(reading.DeviceName);

        var auditCorrections = await verifyDbContext.AuditCorrections.AsNoTracking()
            .Where(a => a.EntityId == reading.Id).ToListAsync(TestContext.Current.CancellationToken);
        auditCorrections.ShouldHaveSingleItem();
        auditCorrections[0].EntityType.ShouldBe("SmartPlugReading");
        auditCorrections[0].FieldName.ShouldBe("KwhValue");
        auditCorrections[0].OldValue.ShouldBe("0.5");
        auditCorrections[0].NewValue.ShouldBe("0.75");
    }

    [Fact]
    public async Task AddAsync_with_a_boundaryCorrection_whose_target_row_no_longer_exists_records_no_audit_correction()
    {
        // Story 3.9 review fix: ExecuteUpdateAsync's own affected-row count gates the audit
        // record — if the target row is gone (no code path deletes a SmartPlugReading today, but
        // the guard is free), zero rows are actually updated and no correction is recorded for a
        // change that never happened.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var backgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var import = MakeImport(householdId, backgroundJobId);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);
        var correction = new SmartPlugReadingCorrection(householdId, Guid.NewGuid(), 0.75m, "0.5", "0.75");

        await repository.AddAsync(import, [], TestContext.Current.CancellationToken, correction);

        await using var verifyDbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var auditCorrections = await verifyDbContext.AuditCorrections.AsNoTracking()
            .Where(a => a.EntityId == correction.ReadingId).ToListAsync(TestContext.Current.CancellationToken);
        auditCorrections.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddAsync_persists_a_large_incremental_batch_when_the_power_point_already_has_prior_readings()
    {
        // AD-23 regression guard: a realistically large, entirely-non-colliding incremental batch
        // (the steady-state common case for any Power Point with prior data) inserts cleanly via
        // BulkInsertOrUpdateAsync — no row-count threshold or branch, applied uniformly.
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
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

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
    public async Task UpdateMappingAsync_persists_the_import_status_and_deletes_the_colliding_reading_on_an_exact_duplicate_conflict()
    {
        // Story 3.7 AC #1 (closes Story 3.4 Dev Notes Open Question #4's AD-20 gap): an
        // AwaitingPowerPointMapping import sits with a reading at the same IntervalStart a
        // different, already-mapped import for the same target Power Point already holds, and
        // the colliding reading's KwhValue/IntervalEnd exactly match the already-mapped one. The
        // set-based UPDATE this method normally uses would reject that as one all-or-nothing
        // statement (a unique-constraint DbUpdateException) — this asserts the per-row
        // conflict-tolerant fallback instead: the exact-duplicate colliding reading is DELETED
        // (not left behind unmapped), the non-colliding reading is attached, and the import's own
        // Status/CompletedAtUtc change is still persisted.
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

        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);
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
        persistedReadings.ShouldHaveSingleItem();
        persistedReadings.Single().IntervalStart.ShouldBe(collidingIntervalStart.AddDays(1));
        persistedReadings.Single().PowerPointId.ShouldBe(powerPointId);
    }

    [Fact]
    public async Task UpdateMappingAsync_leaves_the_colliding_reading_unmapped_when_its_KwhValue_diverges_from_the_existing_mapped_reading()
    {
        // Story 3.7 AC #2: a collision at the same (PowerPointId, IntervalStart) whose KwhValue
        // genuinely diverges from the already-mapped reading (e.g. a DST fall-back duplicate
        // local timestamp with different data) must NOT be silently deleted — today's tolerant
        // behavior (skip, stay unmapped, log) is preserved for this case.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var existingBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var existingImport = MakeImport(householdId, existingBackgroundJobId);
        var collidingIntervalStart = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        dbContext.SmartPlugImports.Add(existingImport);
        dbContext.SmartPlugReadings.Add(
            MakeReading(householdId, existingImport.Id, powerPointId, collidingIntervalStart, kwhValue: 0.5m));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var awaitingBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var awaitingImport = MakeImport(householdId, awaitingBackgroundJobId);
        awaitingImport.Status = SmartPlugImportStatus.AwaitingPowerPointMapping;
        dbContext.SmartPlugImports.Add(awaitingImport);
        dbContext.SmartPlugReadings.Add(
            MakeReading(householdId, awaitingImport.Id, powerPointId: null, collidingIntervalStart, kwhValue: 0.9m));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);
        awaitingImport.Status = SmartPlugImportStatus.Completed;
        awaitingImport.CompletedAtUtc = DateTimeOffset.UtcNow;

        await repository.UpdateMappingAsync(awaitingImport, powerPointId, "Fridge", "Kitchen", TestContext.Current.CancellationToken);

        await using var verifyDbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var persistedImport = await verifyDbContext.SmartPlugImports.SingleAsync(
            i => i.Id == awaitingImport.Id, TestContext.Current.CancellationToken);
        persistedImport.Status.ShouldBe(SmartPlugImportStatus.Completed);

        var persistedReading = await verifyDbContext.SmartPlugReadings.SingleAsync(
            r => r.SmartPlugImportId == awaitingImport.Id, TestContext.Current.CancellationToken);
        persistedReading.PowerPointId.ShouldBeNull();
        persistedReading.KwhValue.ShouldBe(0.9m);
    }

    [Fact]
    public async Task UpdateMappingAsync_leaves_the_colliding_reading_unmapped_when_its_IntervalEnd_diverges_from_the_existing_mapped_reading()
    {
        // Story 3.7 AC #2, IntervalEnd branch (review finding): same collision shape as the
        // KwhValue-divergence test above, but this time KwhValue matches and only IntervalEnd
        // diverges — must NOT be silently deleted either.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var existingBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var existingImport = MakeImport(householdId, existingBackgroundJobId);
        var collidingIntervalStart = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        dbContext.SmartPlugImports.Add(existingImport);
        dbContext.SmartPlugReadings.Add(
            MakeReading(householdId, existingImport.Id, powerPointId, collidingIntervalStart, intervalEnd: collidingIntervalStart));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var awaitingBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var awaitingImport = MakeImport(householdId, awaitingBackgroundJobId);
        awaitingImport.Status = SmartPlugImportStatus.AwaitingPowerPointMapping;
        dbContext.SmartPlugImports.Add(awaitingImport);
        dbContext.SmartPlugReadings.Add(
            MakeReading(householdId, awaitingImport.Id, powerPointId: null, collidingIntervalStart, intervalEnd: collidingIntervalStart.AddHours(1)));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);
        awaitingImport.Status = SmartPlugImportStatus.Completed;
        awaitingImport.CompletedAtUtc = DateTimeOffset.UtcNow;

        await repository.UpdateMappingAsync(awaitingImport, powerPointId, "Fridge", "Kitchen", TestContext.Current.CancellationToken);

        await using var verifyDbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var persistedReading = await verifyDbContext.SmartPlugReadings.SingleAsync(
            r => r.SmartPlugImportId == awaitingImport.Id, TestContext.Current.CancellationToken);
        persistedReading.PowerPointId.ShouldBeNull();
        persistedReading.IntervalEnd.ShouldBe(collidingIntervalStart.AddHours(1));
    }

    [Fact]
    public async Task UpdateMappingAsync_leaves_the_colliding_reading_unmapped_when_its_DeviceName_diverges_from_the_existing_mapped_reading()
    {
        // Review finding (Edge Case Hunter): the exact-duplicate check must compare DeviceName
        // too, not just KwhValue/IntervalEnd — two different devices' readings could otherwise
        // coincide on IntervalStart/KwhValue/IntervalEnd (a Power Point can receive manually
        // mapped readings from more than one distinct SmartPlugImport/device over time) and be
        // wrongly treated as the same duplicate.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var existingBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var existingImport = MakeImport(householdId, existingBackgroundJobId);
        var collidingIntervalStart = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        dbContext.SmartPlugImports.Add(existingImport);
        dbContext.SmartPlugReadings.Add(
            MakeReading(householdId, existingImport.Id, powerPointId, collidingIntervalStart, deviceName: "Old Smart Plug"));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var awaitingBackgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var awaitingImport = MakeImport(householdId, awaitingBackgroundJobId);
        awaitingImport.Status = SmartPlugImportStatus.AwaitingPowerPointMapping;
        dbContext.SmartPlugImports.Add(awaitingImport);
        dbContext.SmartPlugReadings.Add(
            MakeReading(householdId, awaitingImport.Id, powerPointId: null, collidingIntervalStart, deviceName: "New Smart Plug"));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);
        awaitingImport.Status = SmartPlugImportStatus.Completed;
        awaitingImport.CompletedAtUtc = DateTimeOffset.UtcNow;

        await repository.UpdateMappingAsync(awaitingImport, powerPointId, "Fridge", "Kitchen", TestContext.Current.CancellationToken);

        await using var verifyDbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var persistedReading = await verifyDbContext.SmartPlugReadings.SingleAsync(
            r => r.SmartPlugImportId == awaitingImport.Id, TestContext.Current.CancellationToken);
        persistedReading.PowerPointId.ShouldBeNull();
        persistedReading.DeviceName.ShouldBe("New Smart Plug");
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

        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

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

    private static async Task<(Guid JobId, Guid ImportId)> SeedJobAndImportAsync(
        EnergyTrackerDbContext dbContext, Guid householdId, BackgroundJobStatus jobStatus, SmartPlugImportStatus importStatus,
        DateTimeOffset? jobCompletedAtUtc, CancellationToken cancellationToken)
    {
        var job = new BackgroundJob
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            JobType = "ProcessSmartPlugImport",
            Status = jobStatus,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-40),
            CompletedAtUtc = jobCompletedAtUtc,
        };
        dbContext.BackgroundJobs.Add(job);
        var import = MakeImport(householdId, job.Id);
        import.Status = importStatus;
        // Matches real production behavior (ProcessSmartPlugImport stamps both rows'
        // CompletedAtUtc together at original completion time) — the sweep's cutoff comparison
        // now prefers the import row's own CompletedAtUtc over the job's (review-round-2 patch),
        // so a caller wanting an "old" seed must age both consistently.
        import.CompletedAtUtc = jobCompletedAtUtc;
        dbContext.SmartPlugImports.Add(import);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (job.Id, import.Id);
    }

    [Fact]
    public async Task SweepExpiredAsync_deletes_a_Success_import_older_than_the_cutoff_and_detaches_its_readings()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var (jobId, importId) = await SeedJobAndImportAsync(
            dbContext, householdId, BackgroundJobStatus.Completed, SmartPlugImportStatus.Completed,
            jobCompletedAtUtc: DateTimeOffset.UtcNow.AddDays(-31), TestContext.Current.CancellationToken);
        var readingId = Guid.NewGuid();
        dbContext.SmartPlugReadings.Add(new SmartPlugReading
        {
            Id = readingId, HouseholdId = householdId, SmartPlugImportId = importId, PowerPointId = powerPointId,
            RoomName = "Kitchen", PowerPointName = "Fridge", DeviceName = "Fridge",
            IntervalStart = DateTimeOffset.UtcNow, IntervalEnd = DateTimeOffset.UtcNow, KwhValue = 0.5m,
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        await repository.SweepExpiredAsync(householdId, DateTimeOffset.UtcNow.AddDays(-30), TestContext.Current.CancellationToken);

        // A separate DbContext for verification — the sweep's ExecuteDeleteAsync runs raw SQL
        // against the DB directly, bypassing this context's change tracker; the SmartPlugReading
        // entity added above is still tracked with its stale pre-sweep in-memory value, so
        // re-querying through the same context would return that cached instance instead of the
        // DB's actual (SetNull-FK-updated) row. Same idiom this file's other tests already use.
        await using var verifyDbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        (await verifyDbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await verifyDbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.Id == importId, TestContext.Current.CancellationToken)).ShouldBeNull();
        var survivingReading = await verifyDbContext.SmartPlugReadings.SingleAsync(r => r.Id == readingId, TestContext.Current.CancellationToken);
        survivingReading.SmartPlugImportId.ShouldBeNull();
        survivingReading.PowerPointId.ShouldBe(powerPointId);
    }

    [Fact]
    public async Task SweepExpiredAsync_deletes_an_Error_import_older_than_the_cutoff()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var (jobId, importId) = await SeedJobAndImportAsync(
            dbContext, householdId, BackgroundJobStatus.Failed, SmartPlugImportStatus.Failed,
            jobCompletedAtUtc: DateTimeOffset.UtcNow.AddDays(-31), TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        await repository.SweepExpiredAsync(householdId, DateTimeOffset.UtcNow.AddDays(-30), TestContext.Current.CancellationToken);

        (await dbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await dbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.Id == importId, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task SweepExpiredAsync_deletes_a_FlaggedForReview_import_older_than_the_cutoff_including_its_gap()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var (jobId, importId) = await SeedJobAndImportAsync(
            dbContext, householdId, BackgroundJobStatus.Completed, SmartPlugImportStatus.FlaggedForReview,
            jobCompletedAtUtc: DateTimeOffset.UtcNow.AddDays(-31), TestContext.Current.CancellationToken);
        var gapId = Guid.NewGuid();
        dbContext.SmartPlugImportGaps.Add(new SmartPlugImportGap
        {
            Id = gapId,
            HouseholdId = householdId,
            SmartPlugImportId = importId,
            PowerPointId = null,
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow),
            EndDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Treatment = SmartPlugImportGapTreatment.FlaggedForReview,
            EstimatedTotalKwh = null,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        await repository.SweepExpiredAsync(householdId, DateTimeOffset.UtcNow.AddDays(-30), TestContext.Current.CancellationToken);

        (await dbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await dbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.Id == importId, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await dbContext.SmartPlugImportGaps.SingleOrDefaultAsync(g => g.Id == gapId, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task SweepExpiredAsync_does_not_delete_a_NeedsMapping_import_even_though_its_BackgroundJob_is_Completed_and_old()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var (jobId, importId) = await SeedJobAndImportAsync(
            dbContext, householdId, BackgroundJobStatus.Completed, SmartPlugImportStatus.AwaitingPowerPointMapping,
            jobCompletedAtUtc: DateTimeOffset.UtcNow.AddDays(-31), TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        await repository.SweepExpiredAsync(householdId, DateTimeOffset.UtcNow.AddDays(-30), TestContext.Current.CancellationToken);

        (await dbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await dbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.Id == importId, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task SweepExpiredAsync_never_touches_a_Queued_or_Processing_job_regardless_of_age()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        var queuedJob = new BackgroundJob
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Queued, CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-40), CompletedAtUtc = null,
        };
        var processingJob = new BackgroundJob
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Processing, CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-40), CompletedAtUtc = null,
        };
        dbContext.BackgroundJobs.AddRange(queuedJob, processingJob);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        await repository.SweepExpiredAsync(householdId, DateTimeOffset.UtcNow.AddDays(-30), TestContext.Current.CancellationToken);

        (await dbContext.BackgroundJobs.CountAsync(j => j.HouseholdId == householdId, TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task SweepExpiredAsync_deletes_a_Failed_job_older_than_the_cutoff_even_with_no_paired_SmartPlugImport_row()
    {
        // Review-round-2 patch regression guard: a Failed job whose failure happened before
        // ProcessSmartPlugImport ever ran (unknown JobType, or a JSON-deserialize failure inside
        // BackgroundJobProcessor) never gets a paired SmartPlugImport row at all — the pre-patch
        // inner join silently excluded this job from the sweep forever.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        var jobId = Guid.NewGuid();
        dbContext.BackgroundJobs.Add(new BackgroundJob
        {
            Id = jobId, HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Failed, CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-40),
            CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-31),
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        await repository.SweepExpiredAsync(householdId, DateTimeOffset.UtcNow.AddDays(-30), TestContext.Current.CancellationToken);

        (await dbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task SweepExpiredAsync_does_not_delete_a_NeedsMapping_import_resolved_recently_even_though_its_BackgroundJob_CompletedAtUtc_is_old()
    {
        // Review-round-2 patch regression guard: MapSmartPlugImportToPowerPoint updates only the
        // SmartPlugImport row's CompletedAtUtc when a Needs Mapping job is resolved — the
        // BackgroundJob row's own CompletedAtUtc (set once, at original parse time) is never
        // touched. A job resolved moments ago whose original parse ran over 30 days ago must not
        // be swept on the very next list read.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        var job = new BackgroundJob
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Completed, CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-40),
            CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-40),
        };
        dbContext.BackgroundJobs.Add(job);
        var import = MakeImport(householdId, job.Id);
        import.Status = SmartPlugImportStatus.Completed;
        import.CompletedAtUtc = DateTimeOffset.UtcNow;
        dbContext.SmartPlugImports.Add(import);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        await repository.SweepExpiredAsync(householdId, DateTimeOffset.UtcNow.AddDays(-30), TestContext.Current.CancellationToken);

        (await dbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await dbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.Id == import.Id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task SweepExpiredAsync_does_not_delete_a_Success_import_younger_than_the_cutoff()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var (jobId, importId) = await SeedJobAndImportAsync(
            dbContext, householdId, BackgroundJobStatus.Completed, SmartPlugImportStatus.Completed,
            jobCompletedAtUtc: DateTimeOffset.UtcNow.AddDays(-1), TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        await repository.SweepExpiredAsync(householdId, DateTimeOffset.UtcNow.AddDays(-30), TestContext.Current.CancellationToken);

        (await dbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await dbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.Id == importId, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    // Story 3.10: DeleteJobsAsync is the manual, all-states counterpart to SweepExpiredAsync above.
    // The SweepExpiredAsync tests above are themselves the regression guard confirming the Task 1
    // shared-helper extraction left the automatic sweep's own eligibility behavior unchanged.

    [Fact]
    public async Task DeleteJobsAsync_with_no_cutoff_deletes_a_Queued_job_regardless_of_age()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        var jobId = Guid.NewGuid();
        dbContext.BackgroundJobs.Add(new BackgroundJob
        {
            Id = jobId, HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Queued, CreatedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = null,
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        var deletedCount = await repository.DeleteJobsAsync(householdId, cutoffUtc: null, TestContext.Current.CancellationToken);

        deletedCount.ShouldBe(1);
        (await dbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteJobsAsync_with_no_cutoff_deletes_a_Processing_job_regardless_of_age()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        var jobId = Guid.NewGuid();
        dbContext.BackgroundJobs.Add(new BackgroundJob
        {
            Id = jobId, HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Processing, CreatedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = null,
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        await repository.DeleteJobsAsync(householdId, cutoffUtc: null, TestContext.Current.CancellationToken);

        (await dbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteJobsAsync_with_no_cutoff_deletes_a_NeedsMapping_import_regardless_of_age()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var (jobId, importId) = await SeedJobAndImportAsync(
            dbContext, householdId, BackgroundJobStatus.Completed, SmartPlugImportStatus.AwaitingPowerPointMapping,
            jobCompletedAtUtc: DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        await repository.DeleteJobsAsync(householdId, cutoffUtc: null, TestContext.Current.CancellationToken);

        (await dbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await dbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.Id == importId, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteJobsAsync_with_no_cutoff_deletes_a_Success_import_and_detaches_its_readings()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var (jobId, importId) = await SeedJobAndImportAsync(
            dbContext, householdId, BackgroundJobStatus.Completed, SmartPlugImportStatus.Completed,
            jobCompletedAtUtc: DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        var readingId = Guid.NewGuid();
        dbContext.SmartPlugReadings.Add(new SmartPlugReading
        {
            Id = readingId, HouseholdId = householdId, SmartPlugImportId = importId, PowerPointId = powerPointId,
            RoomName = "Kitchen", PowerPointName = "Fridge", DeviceName = "Fridge",
            IntervalStart = DateTimeOffset.UtcNow, IntervalEnd = DateTimeOffset.UtcNow, KwhValue = 0.5m,
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        await repository.DeleteJobsAsync(householdId, cutoffUtc: null, TestContext.Current.CancellationToken);

        await using var verifyDbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        (await verifyDbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await verifyDbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.Id == importId, TestContext.Current.CancellationToken)).ShouldBeNull();
        var survivingReading = await verifyDbContext.SmartPlugReadings.SingleAsync(r => r.Id == readingId, TestContext.Current.CancellationToken);
        survivingReading.SmartPlugImportId.ShouldBeNull();
        survivingReading.PowerPointId.ShouldBe(powerPointId);
    }

    [Fact]
    public async Task DeleteJobsAsync_with_no_cutoff_deletes_a_Failed_job_with_no_paired_SmartPlugImport_row()
    {
        // Same class of case SweepExpiredAsync's own left-join regression guard covers above — a
        // Failed job that never got a paired SmartPlugImport row (unknown JobType, or a
        // JSON-deserialize failure before ProcessSmartPlugImport ever ran) must still be eligible.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        var jobId = Guid.NewGuid();
        dbContext.BackgroundJobs.Add(new BackgroundJob
        {
            Id = jobId, HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Failed, CreatedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        var deletedCount = await repository.DeleteJobsAsync(householdId, cutoffUtc: null, TestContext.Current.CancellationToken);

        deletedCount.ShouldBe(1);
        (await dbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteJobsAsync_with_no_cutoff_deletes_a_FlaggedForReview_import_including_its_gap()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var (jobId, importId) = await SeedJobAndImportAsync(
            dbContext, householdId, BackgroundJobStatus.Completed, SmartPlugImportStatus.FlaggedForReview,
            jobCompletedAtUtc: DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        var gapId = Guid.NewGuid();
        dbContext.SmartPlugImportGaps.Add(new SmartPlugImportGap
        {
            Id = gapId,
            HouseholdId = householdId,
            SmartPlugImportId = importId,
            PowerPointId = null,
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow),
            EndDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Treatment = SmartPlugImportGapTreatment.FlaggedForReview,
            EstimatedTotalKwh = null,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        await repository.DeleteJobsAsync(householdId, cutoffUtc: null, TestContext.Current.CancellationToken);

        (await dbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await dbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.Id == importId, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await dbContext.SmartPlugImportGaps.SingleOrDefaultAsync(g => g.Id == gapId, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteJobsAsync_with_a_cutoff_deletes_a_Queued_job_older_than_the_cutoff_via_CreatedAtUtc_fallback()
    {
        // The one genuinely new piece of logic: a Queued/Processing job has no SmartPlugImport row
        // and a null BackgroundJob.CompletedAtUtc, so the age cutoff must fall back to CreatedAtUtc.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        var jobId = Guid.NewGuid();
        dbContext.BackgroundJobs.Add(new BackgroundJob
        {
            Id = jobId, HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Queued, CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-31), CompletedAtUtc = null,
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        var deletedCount = await repository.DeleteJobsAsync(householdId, DateTimeOffset.UtcNow.AddDays(-30), TestContext.Current.CancellationToken);

        deletedCount.ShouldBe(1);
        (await dbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteJobsAsync_with_a_cutoff_does_not_delete_a_Queued_job_younger_than_the_cutoff()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        var jobId = Guid.NewGuid();
        dbContext.BackgroundJobs.Add(new BackgroundJob
        {
            Id = jobId, HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Queued, CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1), CompletedAtUtc = null,
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        var deletedCount = await repository.DeleteJobsAsync(householdId, DateTimeOffset.UtcNow.AddDays(-30), TestContext.Current.CancellationToken);

        deletedCount.ShouldBe(0);
        (await dbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == jobId, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task DeleteJobsAsync_never_deletes_another_households_jobs()
    {
        var householdId = Guid.NewGuid();
        var otherHouseholdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        dbContext.Households.Add(new Household { Id = otherHouseholdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        var otherHouseholdJobId = Guid.NewGuid();
        dbContext.BackgroundJobs.Add(new BackgroundJob
        {
            Id = otherHouseholdJobId, HouseholdId = otherHouseholdId, JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Queued, CreatedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = null,
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        var deletedCount = await repository.DeleteJobsAsync(householdId, cutoffUtc: null, TestContext.Current.CancellationToken);

        deletedCount.ShouldBe(0);
        // Verify via a context scoped to the OTHER household — dbContext above is scoped to
        // householdId, so AD-3's global query filter would hide otherHouseholdJobId from it
        // regardless of whether DeleteJobsAsync actually deleted anything.
        await using var verifyDbContext = await OpenMigratedDbContextAsync(_container, otherHouseholdId, TestContext.Current.CancellationToken);
        (await verifyDbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.Id == otherHouseholdJobId, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    // Incident fix (2026-09-12 prod): DeleteEligibleAsync used to issue one unbatched
    // ExecuteDeleteAsync per table, and on a household with enough accumulated history that
    // single command's log-write volume saturated Azure SQL Basic-tier and exceeded the 120s
    // CommandTimeout. These two tests are the regression guard: batching must still delete every
    // eligible row across a chunk boundary (below), and a failure partway through a multi-chunk
    // delete must still roll back everything, not just the chunks that hadn't run yet (below that).

    [Fact]
    public async Task DeleteJobsAsync_with_no_cutoff_deletes_every_eligible_row_across_multiple_batches()
    {
        // DeleteBatchSize + 1 guarantees exactly two chunks for both the importIds loop and the
        // jobIds loop (one full batch, one single-row remainder) — the minimal case that actually
        // exercises the chunk boundary rather than happening to fit in one iteration.
        const int seedCount = SmartPlugImportRepository.DeleteBatchSize + 1;
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
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
            // Round-1 review finding: neither original test seeded any SmartPlugReading rows, so
            // the SetNull cascade this whole fix exists to bound was never actually exercised. One
            // reading per import (unmapped, PowerPointId null) is enough to prove the cascade still
            // fires correctly across every chunk, not just the first.
            var reading = MakeReading(householdId, import.Id, powerPointId: null, DateTimeOffset.UtcNow.AddMinutes(-i));
            dbContext.SmartPlugReadings.Add(reading);
            jobIds.Add(job.Id);
            importIds.Add(import.Id);
            readingIds.Add(reading.Id);
        }
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        var deletedCount = await repository.DeleteJobsAsync(householdId, cutoffUtc: null, TestContext.Current.CancellationToken);

        deletedCount.ShouldBe(seedCount);
        await using var verifyDbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        (await verifyDbContext.BackgroundJobs.CountAsync(j => jobIds.Contains(j.Id), TestContext.Current.CancellationToken)).ShouldBe(0);
        (await verifyDbContext.SmartPlugImports.CountAsync(i => importIds.Contains(i.Id), TestContext.Current.CancellationToken)).ShouldBe(0);
        (await verifyDbContext.SmartPlugImportGaps.CountAsync(g => importIds.Contains(g.SmartPlugImportId), TestContext.Current.CancellationToken)).ShouldBe(0);
        var survivingReadings = await verifyDbContext.SmartPlugReadings
            .Where(r => readingIds.Contains(r.Id)).ToListAsync(TestContext.Current.CancellationToken);
        survivingReadings.Count.ShouldBe(seedCount);
        survivingReadings.ShouldAllBe(r => r.SmartPlugImportId == null);
    }

    [Fact]
    public async Task DeleteJobsAsync_with_no_cutoff_deletes_every_eligible_Failed_job_with_no_paired_import_across_multiple_batches()
    {
        // Round-1 review finding (Edge Case Hunter): the batching test above always pairs one job
        // to one import 1:1, so importIds.Count == jobIds.Count in every case — the jobIds
        // chunking loop was never exercised independently of the importIds loop. This seeds only
        // jobs (no paired SmartPlugImport row, mirroring the existing single-item
        // "Failed job with no paired import" case) at multi-batch scale.
        const int seedCount = SmartPlugImportRepository.DeleteBatchSize + 1;
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        var jobIds = new List<Guid>(seedCount);
        for (var i = 0; i < seedCount; i++)
        {
            var job = new BackgroundJob
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
                Status = BackgroundJobStatus.Failed, CreatedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = DateTimeOffset.UtcNow,
            };
            dbContext.BackgroundJobs.Add(job);
            jobIds.Add(job.Id);
        }
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        var deletedCount = await repository.DeleteJobsAsync(householdId, cutoffUtc: null, TestContext.Current.CancellationToken);

        deletedCount.ShouldBe(seedCount);
        await using var verifyDbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        (await verifyDbContext.BackgroundJobs.CountAsync(j => jobIds.Contains(j.Id), TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    // Round-1 review finding (Blind Hunter): counting in NonQueryExecutingAsync only proves "N
    // commands were attempted," not "N commands actually completed" — the two happen to coincide
    // in this sequential-await code path today, but that's an implicit assumption, not an observed
    // fact. Counting completions in NonQueryExecutedAsync and gating the next attempt in
    // NonQueryExecutingAsync against that observed count makes "chunk 1 already ran" something the
    // test genuinely proves rather than assumes.
    private sealed class FailAfterCommandCountInterceptor(int allowedCommandCount) : DbCommandInterceptor
    {
        private int _completed;

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (Volatile.Read(ref _completed) >= allowedCommandCount)
            {
                throw new InvalidOperationException("Simulated mid-batch failure for atomicity test.");
            }
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _completed) >= allowedCommandCount)
            {
                throw new InvalidOperationException("Simulated mid-batch failure for atomicity test.");
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _completed);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        // Round-2 review finding: the sync NonQueryExecuting override above gates on _completed
        // but nothing incremented it on the sync path — EF Core's ExecuteDeleteAsync never
        // actually exercises this (it's async-only), but leaving the sync gate wired to a counter
        // only the async path updates would silently reintroduce "gates on attempts, not
        // completions" the moment any sync command path is ever exercised. Mirroring the async
        // pair keeps both paths consistent even though only one is reachable today.
        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            Interlocked.Increment(ref _completed);
            return base.NonQueryExecuted(command, eventData, result);
        }
    }

    [Fact]
    public async Task DeleteJobsAsync_rolls_back_every_chunk_when_a_later_chunk_fails()
    {
        // Same two-chunk seed as the batching test above, plus a gap AND a reading per import
        // (round-1 review finding: the original version of this test never seeded or verified
        // SmartPlugImportGaps; round-2 review finding: it still never seeded SmartPlugReadings, so
        // it could not prove that a SetNull detach performed inside a rolled-back chunk is itself
        // rolled back — every table DeleteEligibleAsync's FK-dependency order touches is exercised
        // here now). The interceptor lets exactly the first import chunk's 2 commands
        // (SmartPlugImportGaps delete, then SmartPlugImports delete) actually complete — observed
        // via NonQueryExecutedAsync, not assumed — before blocking the next command from starting.
        // This proves the outer transaction rolls back a chunk that already ran, not just the
        // chunks queued behind the failure.
        const int seedCount = SmartPlugImportRepository.DeleteBatchSize + 1;
        var householdId = Guid.NewGuid();
        var jobIds = new List<Guid>(seedCount);
        var importIds = new List<Guid>(seedCount);
        var readingIds = new List<Guid>(seedCount);

        // Seed via a plain (non-intercepted) migrated context first — the interceptor attached
        // below must only ever see the repository's own delete commands, never MigrateAsync's DDL
        // or this seeding's own inserts.
        await using (var seedDbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken))
        {
            seedDbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
            for (var i = 0; i < seedCount; i++)
            {
                var job = new BackgroundJob
                {
                    Id = Guid.NewGuid(), HouseholdId = householdId, JobType = "ProcessSmartPlugImport",
                    Status = BackgroundJobStatus.Completed, CreatedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = DateTimeOffset.UtcNow,
                };
                seedDbContext.BackgroundJobs.Add(job);
                var import = MakeImport(householdId, job.Id, deviceTag: $"Fridge-{i}");
                seedDbContext.SmartPlugImports.Add(import);
                seedDbContext.SmartPlugImportGaps.Add(new SmartPlugImportGap
                {
                    Id = Guid.NewGuid(), HouseholdId = householdId, SmartPlugImportId = import.Id, PowerPointId = null,
                    StartDate = DateOnly.FromDateTime(DateTime.UtcNow), EndDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    Treatment = SmartPlugImportGapTreatment.FlaggedForReview, EstimatedTotalKwh = null, CreatedAtUtc = DateTimeOffset.UtcNow,
                });
                var reading = MakeReading(householdId, import.Id, powerPointId: null, DateTimeOffset.UtcNow.AddMinutes(-i));
                seedDbContext.SmartPlugReadings.Add(reading);
                jobIds.Add(job.Id);
                importIds.Add(import.Id);
                readingIds.Add(reading.Id);
            }
            await seedDbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var interceptor = new FailAfterCommandCountInterceptor(allowedCommandCount: 2);
        await using var dbContext = OpenDbContextWithInterceptor(_container, householdId, interceptor);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => repository.DeleteJobsAsync(householdId, cutoffUtc: null, TestContext.Current.CancellationToken));
        exception.Message.ShouldBe("Simulated mid-batch failure for atomicity test.");

        await using var verifyDbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        (await verifyDbContext.BackgroundJobs.CountAsync(j => jobIds.Contains(j.Id), TestContext.Current.CancellationToken)).ShouldBe(seedCount);
        (await verifyDbContext.SmartPlugImports.CountAsync(i => importIds.Contains(i.Id), TestContext.Current.CancellationToken)).ShouldBe(seedCount);
        (await verifyDbContext.SmartPlugImportGaps.CountAsync(g => importIds.Contains(g.SmartPlugImportId), TestContext.Current.CancellationToken)).ShouldBe(seedCount);
        var survivingReadings = await verifyDbContext.SmartPlugReadings
            .Where(r => readingIds.Contains(r.Id)).ToListAsync(TestContext.Current.CancellationToken);
        survivingReadings.Count.ShouldBe(seedCount);
        survivingReadings.ShouldAllBe(r => importIds.Contains(r.SmartPlugImportId!.Value));
    }

    [Fact]
    public async Task AddAsync_failure_leaves_the_callers_own_tracked_entity_intact()
    {
        // Incident regression guard (2026-09-05 prod): AddAsync's failure handler used to call
        // dbContext.ChangeTracker.Clear() on ANY AddAsyncCore failure. BackgroundJobProcessor
        // shares this same scoped DbContext and is tracking its own BackgroundJob entity across
        // the ProcessSmartPlugImport call — Clear() wiped that tracking too, so
        // BackgroundJobProcessor's subsequent `job.Status = Failed` mutation targeted a detached
        // entity and its SaveChangesAsync silently no-opped, permanently orphaning the job at
        // Status = Processing (the queue message was already deleted by then). The fix narrows the
        // detach to only the `import` entity AddAsyncCore itself added.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        // Only used for its Household-row side effect here — the returned Power Point is
        // deliberately NOT the one referenced below; the reading's PowerPointId is a fresh,
        // unrelated Guid whose only job is to violate the FK.
        await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var backgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        // Mirrors BackgroundJobProcessor.ProcessAsync's own shape: it fetches a BackgroundJob
        // entity on this same scoped DbContext and only mutates it in its OWN catch block, AFTER
        // the inner ProcessSmartPlugImport/AddAsync call has already thrown — the mutation must
        // come after AddAsync, not before, or an unrelated SaveChangesAsync inside AddAsyncCore
        // would sweep it up and mark it accepted before the transaction that carried it rolls back.
        var trackedJob = await dbContext.BackgroundJobs.SingleAsync(
            j => j.Id == backgroundJobId, TestContext.Current.CancellationToken);

        var import = MakeImport(householdId, backgroundJobId);
        // A reading whose PowerPointId doesn't exist violates SmartPlugReadingConfiguration's FK
        // (Restrict) during the bulk-insert step below — forces AddAsyncCore to fail *after* the
        // import row's own AddAsync+SaveChangesAsync already succeeded inside the still-open
        // transaction, the same shape as the production incident's DB-timeout failure on the
        // readings bulk insert (import row committed, readings insert failed).
        var reading = MakeReading(householdId, import.Id, Guid.NewGuid(), DateTimeOffset.UtcNow);

        await Should.ThrowAsync<Exception>(
            () => repository.AddAsync(import, [reading], TestContext.Current.CancellationToken));

        // Mirrors BackgroundJobProcessor.ProcessAsync's own outer catch block, which runs only
        // after the call above has thrown.
        trackedJob.Status = BackgroundJobStatus.Failed;
        trackedJob.CompletedAtUtc = DateTimeOffset.UtcNow;

        // The bug: ChangeTracker.Clear() wiped trackedJob's tracking entry, so this mutation would
        // land on a detached entity and a subsequent SaveChangesAsync would silently no-op it.
        dbContext.Entry(trackedJob).State.ShouldNotBe(EntityState.Detached);
        dbContext.Entry(trackedJob).Property(j => j.Status).IsModified.ShouldBeTrue();

        // Prove the thing that actually matters — the incident was a *silently no-op'd save* — not
        // just intermediate change-tracker bookkeeping: the mutation must actually reach the DB.
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using var verifyDbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var persistedJob = await verifyDbContext.BackgroundJobs.AsNoTracking()
            .SingleAsync(j => j.Id == backgroundJobId, TestContext.Current.CancellationToken);
        persistedJob.Status.ShouldBe(BackgroundJobStatus.Failed);
        persistedJob.CompletedAtUtc.ShouldNotBeNull();

        // The original reason ChangeTracker.Clear() was introduced (Story 3.9 review fix) must
        // still hold: ProcessSmartPlugImport.PersistFailedImportAsync's real-world follow-up —
        // adding a NEW SmartPlugImport with the SAME Id — must not throw "already being tracked".
        var retryImport = new SmartPlugImport
        {
            Id = import.Id,
            HouseholdId = householdId,
            BackgroundJobId = backgroundJobId,
            VendorFormat = SmartPlugVendorFormat.EveHome,
            OriginalFileName = "export.xlsx",
            Status = SmartPlugImportStatus.Failed,
            DeviceTag = string.Empty,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };

        await Should.NotThrowAsync(() => repository.AddAsync(retryImport, [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddAsync_failure_after_a_boundaryCorrection_leaves_no_phantom_AuditCorrection_tracked_or_persisted()
    {
        // Review-round finding on the incident fix above: `import` isn't the only entity
        // AddAsyncCore can add before a later step fails. When boundaryCorrection is set,
        // auditCorrectionRecorder.RecordAsync (called before the readings write below) adds AND
        // saves an AuditCorrection row of its own — inside this same still-open transaction, so it
        // also advances to Unchanged before the later readings-insert failure rolls everything back
        // at the DB level. A fix that only detaches `import` would leave this AuditCorrection
        // instance tracked as Unchanged, phantom-representing a correction that never actually
        // took effect — and vulnerable to being silently re-persisted by any later unrelated
        // SaveChangesAsync on this same DbContext (e.g. the very next PersistFailedImportAsync
        // retry insert).
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var powerPointId = await SeedPowerPointAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var backgroundJobId = await SeedBackgroundJobAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var existingImport = MakeImport(householdId, backgroundJobId);
        dbContext.SmartPlugImports.Add(existingImport);
        var existingReading = MakeReading(householdId, existingImport.Id, powerPointId, DateTimeOffset.UtcNow, kwhValue: 0.5m);
        dbContext.SmartPlugReadings.Add(existingReading);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        dbContext.ChangeTracker.Clear();
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);

        var newImport = MakeImport(householdId, backgroundJobId);
        var correction = new SmartPlugReadingCorrection(householdId, existingReading.Id, 0.75m, "0.5", "0.75");
        // Same FK-violation shape as the sibling test above — forces AddAsyncCore to fail during
        // the readings bulk-write, after both the boundaryCorrection's ExecuteUpdateAsync+
        // RecordAsync and the newImport row's own insert have already committed inside the
        // still-open transaction.
        var badReading = MakeReading(householdId, newImport.Id, Guid.NewGuid(), DateTimeOffset.UtcNow);

        await Should.ThrowAsync<Exception>(
            () => repository.AddAsync(newImport, [badReading], TestContext.Current.CancellationToken, correction));

        // Entries() never returns Detached entries — an empty result here proves the AuditCorrection
        // RecordAsync added is no longer tracked, not merely that it's in some other survivable state.
        dbContext.ChangeTracker.Entries<AuditCorrection>().ShouldBeEmpty();

        // Prove it two ways: no phantom row exists in the DB (the correction never actually took
        // effect, matching the transaction rollback), and a later unrelated SaveChangesAsync on this
        // same DbContext — mirroring PersistFailedImportAsync's own retry insert — doesn't
        // resurrect it by re-persisting a still-tracked stale instance.
        var retryImport = new SmartPlugImport
        {
            Id = newImport.Id,
            HouseholdId = householdId,
            BackgroundJobId = backgroundJobId,
            VendorFormat = SmartPlugVendorFormat.EveHome,
            OriginalFileName = "export.xlsx",
            Status = SmartPlugImportStatus.Failed,
            DeviceTag = string.Empty,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
        await repository.AddAsync(retryImport, [], TestContext.Current.CancellationToken);

        await using var verifyDbContext = await OpenMigratedDbContextAsync(_container, householdId, TestContext.Current.CancellationToken);
        var auditCorrections = await verifyDbContext.AuditCorrections.AsNoTracking()
            .Where(a => a.EntityId == existingReading.Id).ToListAsync(TestContext.Current.CancellationToken);
        auditCorrections.ShouldBeEmpty();
        var persistedReading = await verifyDbContext.SmartPlugReadings.AsNoTracking()
            .SingleAsync(r => r.Id == existingReading.Id, TestContext.Current.CancellationToken);
        persistedReading.KwhValue.ShouldBe(0.5m);
    }
}
