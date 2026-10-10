using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

// Proves the restore write path actually respects AD-3 (every inserted row carries the CURRENT
// Household's id regardless of what the file implies) and performs a true wholesale replace (zero
// pre-existing rows survive) — an NSubstitute-mocked Application-layer test cannot verify real FK
// ordering or that the global query filter still reads consistently afterward
// (project-context.md's Story 5.1 lesson).
public abstract class HouseholdRestoreWriterTestsBase
{
    protected abstract Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken);

    protected sealed class FixedHouseholdAccessor(Guid householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;

        public Guid? HouseholdMemberId => null;
    }

    private static Household NewHousehold(Guid householdId) => new()
    {
        Id = householdId,
        Locale = "en-US",
        Currency = "USD",
        CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        YearlyBaselineKwh = null,
        TrendingThresholdKwh = 50m,
        LowConfidenceGapDays = 30,
        TariffCheckCadenceMonths = 6,
        AiPlausibilityEnabled = false,
    };

    // A minimal but complete restore data set covering every restorable entity category (membership
    // is deliberately not one of them, AD-25) plus the Household settings patch, all internally cross-referenced — used by every test below so
    // a single shared fixture proves both AD-3 rewriting and wholesale replacement together.
    private static HouseholdRestoreData BuildRestoreData(Guid householdId)
    {
        var mainMeterId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var powerPointId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var readingId = Guid.NewGuid();
        var previousReadingId = Guid.NewGuid();

        return new HouseholdRestoreData(
            householdId,
            new HouseholdSettingsPatch("de-DE", "EUR", 3500m, 100m, 45, 3, true),
            new MainMeter { Id = mainMeterId, HouseholdId = householdId, CreatedAtUtc = DateTimeOffset.UtcNow, DigitCapacityKwh = null },
            [new Room { Id = roomId, HouseholdId = householdId, Name = "Kitchen", CreatedAtUtc = DateTimeOffset.UtcNow }],
            [new PowerPoint { Id = powerPointId, HouseholdId = householdId, RoomId = roomId, Name = "Outlet", CreatedAtUtc = DateTimeOffset.UtcNow }],
            [new Device { Id = deviceId, HouseholdId = householdId, PowerPointId = powerPointId, Name = "Kettle", CreatedAtUtc = DateTimeOffset.UtcNow }],
            [
                new MeterReading
                {
                    Id = previousReadingId, HouseholdId = householdId, MainMeterId = mainMeterId, KwhValue = 90m,
                    ReadingTimestamp = DateTimeOffset.UtcNow.AddDays(-1), IdempotencyKey = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
                },
                new MeterReading
                {
                    Id = readingId, HouseholdId = householdId, MainMeterId = mainMeterId, KwhValue = 100m,
                    ReadingTimestamp = DateTimeOffset.UtcNow, IdempotencyKey = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow,
                },
            ],
            [
                new MeterRegressionPrompt
                {
                    Id = Guid.NewGuid(), HouseholdId = householdId, MainMeterId = mainMeterId, MeterReadingId = readingId,
                    PreviousMeterReadingId = previousReadingId, CreatedAtUtc = DateTimeOffset.UtcNow,
                },
            ],
            [
                new Tariff
                {
                    Id = Guid.NewGuid(), HouseholdId = householdId, MonthlyBaseFee = 8.5m, PricePerKwh = 0.32m, Currency = "EUR",
                    ContractStartDate = DateTimeOffset.UtcNow, ContractPeriodMonths = 12, CreatedAtUtc = DateTimeOffset.UtcNow,
                },
            ],
            [
                new Event
                {
                    Id = Guid.NewGuid(), HouseholdId = householdId, Description = "imported event", OccurredAt = DateTimeOffset.UtcNow,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                },
            ],
            [
                new SmartPlugReading
                {
                    Id = Guid.NewGuid(), HouseholdId = householdId, SmartPlugImportId = null, PowerPointId = powerPointId,
                    RoomName = "Kitchen", PowerPointName = "Outlet", DeviceName = "Kettle",
                    IntervalStart = DateTimeOffset.UtcNow.AddHours(-1), IntervalEnd = DateTimeOffset.UtcNow, KwhValue = 0.5m,
                },
            ],
            [
                new StatusSnapshot
                {
                    Id = Guid.NewGuid(), HouseholdId = householdId, Status = Status.WithinRange, PaceToDateKwh = 10m,
                    BaselineToDateKwh = 12m, IsLowConfidence = false, ComputedAtUtc = DateTimeOffset.UtcNow,
                },
            ],
            [
                new AuditCorrection
                {
                    Id = Guid.NewGuid(), HouseholdId = householdId, EntityType = "MeterReading", EntityId = readingId,
                    FieldName = "KwhValue", OldValue = "90", NewValue = "100", CorrectedAtUtc = DateTimeOffset.UtcNow,
                },
            ]);
    }

    [Fact]
    public async Task Every_inserted_row_carries_the_current_Households_id()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(NewHousehold(householdId));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var writer = new HouseholdRestoreWriter(dbContext);
        var data = BuildRestoreData(householdId);

        await writer.RestoreAsync(data, TestContext.Current.CancellationToken);

        var household = await dbContext.Households.SingleAsync(h => h.Id == householdId, TestContext.Current.CancellationToken);
        household.Locale.ShouldBe("de-DE");
        household.Currency.ShouldBe("EUR");
        household.YearlyBaselineKwh.ShouldBe(3500m);
        household.AiPlausibilityEnabled.ShouldBeTrue();

        // AD-25: restore never writes HouseholdMember rows (none were seeded here, so none exist).
        (await dbContext.HouseholdMembers.Where(m => m.HouseholdId == householdId).ToListAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await dbContext.MainMeters.SingleAsync(TestContext.Current.CancellationToken)).HouseholdId.ShouldBe(householdId);
        (await dbContext.Rooms.SingleAsync(TestContext.Current.CancellationToken)).HouseholdId.ShouldBe(householdId);
        (await dbContext.PowerPoints.SingleAsync(TestContext.Current.CancellationToken)).HouseholdId.ShouldBe(householdId);
        (await dbContext.Devices.SingleAsync(TestContext.Current.CancellationToken)).HouseholdId.ShouldBe(householdId);
        (await dbContext.MeterReadings.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
        (await dbContext.MeterRegressionPrompts.SingleAsync(TestContext.Current.CancellationToken)).HouseholdId.ShouldBe(householdId);
        (await dbContext.Tariffs.SingleAsync(TestContext.Current.CancellationToken)).HouseholdId.ShouldBe(householdId);
        (await dbContext.Events.SingleAsync(TestContext.Current.CancellationToken)).HouseholdId.ShouldBe(householdId);
        (await dbContext.SmartPlugReadings.SingleAsync(TestContext.Current.CancellationToken)).HouseholdId.ShouldBe(householdId);
        (await dbContext.StatusSnapshots.SingleAsync(TestContext.Current.CancellationToken)).HouseholdId.ShouldBe(householdId);
        (await dbContext.AuditCorrections.SingleAsync(TestContext.Current.CancellationToken)).HouseholdId.ShouldBe(householdId);
    }

    // AC #1/#4: restoring onto a Household with pre-existing data leaves zero pre-existing rows
    // behind afterward — a true wholesale replace, never a merge. HouseholdMember is the one
    // exception: restore never touches membership (AD-25, audit S1), so the member survives.
    [Fact]
    public async Task Restoring_onto_a_Household_with_existing_data_leaves_no_pre_existing_rows_behind()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(NewHousehold(householdId));
        var preExistingRoom = new Room { Id = Guid.NewGuid(), HouseholdId = householdId, Name = "PreExisting", CreatedAtUtc = DateTimeOffset.UtcNow };
        dbContext.Rooms.Add(preExistingRoom);
        var preExistingMainMeter = new MainMeter { Id = Guid.NewGuid(), HouseholdId = householdId, CreatedAtUtc = DateTimeOffset.UtcNow };
        dbContext.MainMeters.Add(preExistingMainMeter);
        var preExistingMember = new HouseholdMember
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, ExternalIssuer = "https://old.test/", ExternalSubjectId = "old-sub",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        dbContext.HouseholdMembers.Add(preExistingMember);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var writer = new HouseholdRestoreWriter(dbContext);
        var data = BuildRestoreData(householdId);

        await writer.RestoreAsync(data, TestContext.Current.CancellationToken);

        (await dbContext.Rooms.AnyAsync(r => r.Id == preExistingRoom.Id, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await dbContext.MainMeters.AnyAsync(m => m.Id == preExistingMainMeter.Id, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await dbContext.Rooms.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await dbContext.MainMeters.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await dbContext.HouseholdMembers.AnyAsync(m => m.Id == preExistingMember.Id, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await dbContext.HouseholdMembers.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    // Never touches another Household's rows — the delete phase must be scoped exactly like every
    // other query in this codebase (AD-3), not a blanket table-wide delete.
    [Fact]
    public async Task Never_deletes_or_overwrites_another_Households_data()
    {
        var householdId = Guid.NewGuid();
        var otherHouseholdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(NewHousehold(householdId));
        dbContext.Households.Add(NewHousehold(otherHouseholdId));
        var otherRoom = new Room { Id = Guid.NewGuid(), HouseholdId = otherHouseholdId, Name = "NotYours", CreatedAtUtc = DateTimeOffset.UtcNow };
        dbContext.Rooms.Add(otherRoom);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var writer = new HouseholdRestoreWriter(dbContext);
        var data = BuildRestoreData(householdId);

        await writer.RestoreAsync(data, TestContext.Current.CancellationToken);

        var otherHousehold = await dbContext.Households.SingleAsync(h => h.Id == otherHouseholdId, TestContext.Current.CancellationToken);
        otherHousehold.Locale.ShouldBe("en-US");
        // IgnoreQueryFilters — this dbContext's own ICurrentHouseholdAccessor is fixed to
        // `householdId` for the rest of this test file's AD-3 assertions, so a plain query would
        // filter otherRoom out regardless of whether the writer actually deleted it.
        (await dbContext.Rooms.IgnoreQueryFilters().AnyAsync(r => r.Id == otherRoom.Id, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    private sealed record MemberSnapshot(Guid Id, Guid HouseholdId, string ExternalIssuer, string ExternalSubjectId, string? DisplayName, DateTimeOffset CreatedAtUtc);

    private static Task<List<MemberSnapshot>> ReadMembersAsync(EnergyTrackerDbContext dbContext, CancellationToken cancellationToken) =>
        dbContext.HouseholdMembers.AsNoTracking()
            .OrderBy(m => m.Id)
            .Select(m => new MemberSnapshot(m.Id, m.HouseholdId, m.ExternalIssuer, m.ExternalSubjectId, m.DisplayName, m.CreatedAtUtc))
            .ToListAsync(cancellationToken);

    // AD-25 / audit S1 (the headline test): a restore file can neither grant access to a stranger
    // nor lock the members out. The file below omits both real members and lists a foreign OIDC
    // identity (legacy export shape, identity fields present); after the real use case + writer ran
    // against a real database, the membership rows are field-for-field what they were before and no
    // row for the foreign identity exists.
    [Fact]
    public async Task A_restore_file_listing_foreign_or_no_members_leaves_the_Households_membership_untouched()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(NewHousehold(householdId));
        dbContext.HouseholdMembers.AddRange(
            new HouseholdMember
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, ExternalIssuer = "https://real.test/", ExternalSubjectId = "auth0|alice",
                DisplayName = "Alice", CreatedAtUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            },
            new HouseholdMember
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, ExternalIssuer = "https://real.test/", ExternalSubjectId = "auth0|bob",
                DisplayName = null, CreatedAtUtc = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero),
            });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var membersBefore = await ReadMembersAsync(dbContext, TestContext.Current.CancellationToken);
        membersBefore.Count.ShouldBe(2);

        var foreignMemberId = Guid.NewGuid();
        var json =
            $$"""
            {
              "formatVersion": "v2",
              "exportedAtUtc": "2026-09-22T10:00:00+00:00",
              "household": {
                "id": "{{Guid.NewGuid()}}", "createdAtUtc": "2026-01-05T08:00:00+00:00", "locale": "de-DE", "currency": "EUR",
                "yearlyBaselineKwh": null, "trendingThresholdKwh": 100.0, "lowConfidenceGapDays": 45, "tariffCheckCadenceMonths": 3,
                "aiPlausibilityEnabled": false, "aiPlausibilityBackendConfigured": false, "aiPlausibilityBackendLabel": null
              },
              "householdMembers": [{
                "id": "{{foreignMemberId}}", "externalIssuer": "https://attacker.test/", "externalSubjectId": "evil|mallory",
                "displayName": "Mallory", "createdAtUtc": "2026-01-05T08:00:00+00:00"
              }],
              "mainMeter": null, "meterReadings": [], "meterRegressionPrompts": [], "tariffs": [], "events": [],
              "rooms": [], "powerPoints": [], "devices": [], "smartPlugReadings": [], "statusSnapshots": [], "auditCorrections": []
            }
            """;
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        await File.WriteAllTextAsync(tempFilePath, json, TestContext.Current.CancellationToken);

        await new RestoreHouseholdData(new HouseholdRestoreWriter(dbContext)).ExecuteAsync(
            householdId, new RestoreHouseholdDataPayload(tempFilePath, "export.json"), TestContext.Current.CancellationToken);

        var membersAfter = await ReadMembersAsync(dbContext, TestContext.Current.CancellationToken);
        membersAfter.ShouldBe(membersBefore);
        (await dbContext.HouseholdMembers.AsNoTracking().AnyAsync(
            m => m.Id == foreignMemberId || m.ExternalIssuer == "https://attacker.test/" || m.ExternalSubjectId == "evil|mallory",
            TestContext.Current.CancellationToken)).ShouldBeFalse();
        // The restore itself did run (not a vacuous pass): the Household's settings were replaced.
        (await dbContext.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, TestContext.Current.CancellationToken))
            .Locale.ShouldBe("de-DE");
    }

    // Code Review, Story 7.2 Pass 1: the single outer transaction spanning delete+insert is the
    // entire mechanism making "never partially applied" true — this had never been proven, only
    // documented. Forces a real DB-level FK violation partway through the insert phase (a Device
    // referencing a PowerPointId absent from this same restore payload — DeviceConfiguration's real
    // FK is DeleteBehavior.Restrict, IsRequired) and asserts the pre-existing data survives
    // completely intact: both the delete of the pre-existing Room AND the insert of the new Room
    // that already succeeded earlier in the SAME transaction must have been rolled back too.
    [Fact]
    public async Task A_failure_partway_through_the_insert_phase_rolls_back_the_entire_transaction()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(NewHousehold(householdId));
        var preExistingRoom = new Room { Id = Guid.NewGuid(), HouseholdId = householdId, Name = "PreExisting", CreatedAtUtc = DateTimeOffset.UtcNow };
        dbContext.Rooms.Add(preExistingRoom);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var writer = new HouseholdRestoreWriter(dbContext);
        var data = BuildRestoreData(householdId) with
        {
            Devices = [new Device { Id = Guid.NewGuid(), HouseholdId = householdId, PowerPointId = Guid.NewGuid(), Name = "Orphan", CreatedAtUtc = DateTimeOffset.UtcNow }],
        };

        await Should.ThrowAsync<DbUpdateException>(() => writer.RestoreAsync(data, TestContext.Current.CancellationToken));

        (await dbContext.Rooms.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await dbContext.Rooms.SingleAsync(TestContext.Current.CancellationToken)).Id.ShouldBe(preExistingRoom.Id);
    }

    // AD-11's explicit carve-out — a restore never leaves an AuditCorrection row for the wholesale
    // replace itself (only the ones actually included in the imported file, if any).
    [Fact]
    public async Task Never_creates_an_AuditCorrection_row_for_the_restore_operation_itself()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(NewHousehold(householdId));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var writer = new HouseholdRestoreWriter(dbContext);
        var data = BuildRestoreData(householdId) with { AuditCorrections = [] };

        await writer.RestoreAsync(data, TestContext.Current.CancellationToken);

        (await dbContext.AuditCorrections.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }
}

public class PostgresHouseholdRestoreWriterTests : HouseholdRestoreWriterTestsBase, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    protected override async Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));
        var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
        await dbContext.Database.MigrateAsync(cancellationToken);
        return dbContext;
    }
}

public class SqlServerHouseholdRestoreWriterTests : HouseholdRestoreWriterTestsBase, IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    protected override async Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer"));
        var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
        await dbContext.Database.MigrateAsync(cancellationToken);
        return dbContext;
    }
}
