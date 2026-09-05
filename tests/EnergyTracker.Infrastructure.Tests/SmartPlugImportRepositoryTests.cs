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
