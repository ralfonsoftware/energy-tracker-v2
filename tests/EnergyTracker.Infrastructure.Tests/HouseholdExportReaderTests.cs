using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

// AC #3/#4: proves the export read path actually respects the AD-3 global query filter end to end
// (an NSubstitute-mocked Application-layer test cannot catch a mistakenly-scoped query — see
// project-context.md's Story 5.1 lesson on why a real DbContext matters for this class of bug) and
// returns identical results shaped from both providers (AD-2).
public abstract class HouseholdExportReaderTestsBase
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
        Locale = "de-DE",
        Currency = "EUR",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        YearlyBaselineKwh = 3500m,
    };

    [Fact]
    public async Task Returns_the_Households_own_data_across_every_entity_category()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(NewHousehold(householdId));
        var member = new HouseholdMember
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            ExternalIssuer = "https://issuer.test/",
            ExternalSubjectId = "sub-1",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        dbContext.HouseholdMembers.Add(member);
        var mainMeter = new MainMeter { Id = Guid.NewGuid(), HouseholdId = householdId, CreatedAtUtc = DateTimeOffset.UtcNow };
        dbContext.MainMeters.Add(mainMeter);
        var room = new Room { Id = Guid.NewGuid(), HouseholdId = householdId, Name = "Kitchen", CreatedAtUtc = DateTimeOffset.UtcNow };
        dbContext.Rooms.Add(room);
        var powerPoint = new PowerPoint
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, RoomId = room.Id, Name = "Outlet",
            CreatedAtUtc = DateTimeOffset.UtcNow, ArchivedAt = DateTimeOffset.UtcNow,
        };
        dbContext.PowerPoints.Add(powerPoint);
        var device = new Device
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, PowerPointId = powerPoint.Id, Name = "Kettle", CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        dbContext.Devices.Add(device);
        var reading = new MeterReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = mainMeter.Id,
            KwhValue = 100m,
            ReadingTimestamp = DateTimeOffset.UtcNow,
            IdempotencyKey = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        dbContext.MeterReadings.Add(reading);
        var tariff = new Tariff
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MonthlyBaseFee = 8.5m,
            PricePerKwh = 0.32m,
            Currency = "EUR",
            ContractStartDate = DateTimeOffset.UtcNow,
            ContractPeriodMonths = 12,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        dbContext.Tariffs.Add(tariff);
        var @event = new Event
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, Description = "cooked 2h",
            OccurredAt = DateTimeOffset.UtcNow, CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        dbContext.Events.Add(@event);
        var smartPlugReading = new SmartPlugReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            PowerPointId = powerPoint.Id,
            RoomName = "Kitchen",
            PowerPointName = "Outlet",
            DeviceName = "Kettle",
            IntervalStart = DateTimeOffset.UtcNow.AddHours(-1),
            IntervalEnd = DateTimeOffset.UtcNow,
            KwhValue = 0.5m,
        };
        dbContext.SmartPlugReadings.Add(smartPlugReading);
        var snapshot = new StatusSnapshot
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, Status = Status.WithinRange,
            PaceToDateKwh = 10m, BaselineToDateKwh = 12m, IsLowConfidence = false, ComputedAtUtc = DateTimeOffset.UtcNow,
        };
        dbContext.StatusSnapshots.Add(snapshot);
        var correction = new AuditCorrection
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, EntityType = "MeterReading", EntityId = reading.Id,
            FieldName = "KwhValue", OldValue = "90", NewValue = "100", CorrectedAtUtc = DateTimeOffset.UtcNow,
        };
        dbContext.AuditCorrections.Add(correction);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var reader = new HouseholdExportReader(dbContext);

        var result = await reader.GetExportDataAsync(householdId, TestContext.Current.CancellationToken);

        result.Household.Id.ShouldBe(householdId);
        result.HouseholdMembers.Single().Id.ShouldBe(member.Id);
        result.MainMeter.ShouldNotBeNull();
        result.MainMeter!.Id.ShouldBe(mainMeter.Id);
        result.Rooms.Single().Id.ShouldBe(room.Id);
        result.PowerPoints.Single().Id.ShouldBe(powerPoint.Id);
        result.PowerPoints.Single().ArchivedAt.ShouldNotBeNull();
        result.Devices.Single().Id.ShouldBe(device.Id);
        result.MeterReadings.Single().Id.ShouldBe(reading.Id);
        result.Tariffs.Single().Id.ShouldBe(tariff.Id);
        result.Events.Single().Id.ShouldBe(@event.Id);
        result.SmartPlugReadings.Single().Id.ShouldBe(smartPlugReading.Id);
        result.StatusSnapshots.Single().Id.ShouldBe(snapshot.Id);
        result.AuditCorrections.Single().Id.ShouldBe(correction.Id);
    }

    // AC #3: the entire correctness of tenant isolation here rests on the AD-3 global query filter
    // (Room/PowerPoint/Device/MainMeter/MeterReading/MeterRegressionPrompt/Tariff/Event/
    // SmartPlugReading/StatusSnapshot/AuditCorrection) plus an explicit HouseholdId match for
    // HouseholdMember (which carries no query filter). Every one of those is its own hand-written
    // `Where(x.HouseholdId == householdId)` clause in HouseholdExportReader — seed and assert all
    // twelve so a single mistyped clause can't silently leak another Household's data.
    [Fact]
    public async Task Never_returns_another_Households_data()
    {
        var householdId = Guid.NewGuid();
        var otherHouseholdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(NewHousehold(householdId));
        dbContext.Households.Add(NewHousehold(otherHouseholdId));
        dbContext.HouseholdMembers.Add(new HouseholdMember
        {
            Id = Guid.NewGuid(), HouseholdId = otherHouseholdId, ExternalIssuer = "https://issuer.test/",
            ExternalSubjectId = "sub-other", CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        var otherRoom = new Room { Id = Guid.NewGuid(), HouseholdId = otherHouseholdId, Name = "NotYours", CreatedAtUtc = DateTimeOffset.UtcNow };
        dbContext.Rooms.Add(otherRoom);
        var otherPowerPoint = new PowerPoint
        {
            Id = Guid.NewGuid(), HouseholdId = otherHouseholdId, RoomId = otherRoom.Id, Name = "NotYoursOutlet", CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        dbContext.PowerPoints.Add(otherPowerPoint);
        dbContext.Devices.Add(new Device
        {
            Id = Guid.NewGuid(), HouseholdId = otherHouseholdId, PowerPointId = otherPowerPoint.Id, Name = "NotYoursKettle", CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        var otherMainMeter = new MainMeter { Id = Guid.NewGuid(), HouseholdId = otherHouseholdId, CreatedAtUtc = DateTimeOffset.UtcNow };
        dbContext.MainMeters.Add(otherMainMeter);
        var otherReading = new MeterReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = otherHouseholdId,
            MainMeterId = otherMainMeter.Id,
            KwhValue = 999m,
            ReadingTimestamp = DateTimeOffset.UtcNow,
            IdempotencyKey = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        dbContext.MeterReadings.Add(otherReading);
        var otherPreviousReading = new MeterReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = otherHouseholdId,
            MainMeterId = otherMainMeter.Id,
            KwhValue = 998m,
            ReadingTimestamp = DateTimeOffset.UtcNow.AddDays(-1),
            IdempotencyKey = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        };
        dbContext.MeterReadings.Add(otherPreviousReading);
        dbContext.MeterRegressionPrompts.Add(new MeterRegressionPrompt
        {
            Id = Guid.NewGuid(),
            HouseholdId = otherHouseholdId,
            MainMeterId = otherMainMeter.Id,
            MeterReadingId = otherReading.Id,
            PreviousMeterReadingId = otherPreviousReading.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        dbContext.Tariffs.Add(new Tariff
        {
            Id = Guid.NewGuid(),
            HouseholdId = otherHouseholdId,
            MonthlyBaseFee = 5m,
            PricePerKwh = 0.25m,
            Currency = "EUR",
            ContractStartDate = DateTimeOffset.UtcNow,
            ContractPeriodMonths = 12,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        dbContext.Events.Add(new Event
        {
            Id = Guid.NewGuid(), HouseholdId = otherHouseholdId, Description = "NotYoursEvent",
            OccurredAt = DateTimeOffset.UtcNow, CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        dbContext.SmartPlugReadings.Add(new SmartPlugReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = otherHouseholdId,
            PowerPointId = otherPowerPoint.Id,
            RoomName = "NotYours",
            PowerPointName = "NotYoursOutlet",
            DeviceName = "NotYoursKettle",
            IntervalStart = DateTimeOffset.UtcNow.AddHours(-1),
            IntervalEnd = DateTimeOffset.UtcNow,
            KwhValue = 0.5m,
        });
        dbContext.StatusSnapshots.Add(new StatusSnapshot
        {
            Id = Guid.NewGuid(), HouseholdId = otherHouseholdId, Status = Status.WithinRange,
            PaceToDateKwh = 1m, BaselineToDateKwh = 2m, IsLowConfidence = false, ComputedAtUtc = DateTimeOffset.UtcNow,
        });
        dbContext.AuditCorrections.Add(new AuditCorrection
        {
            Id = Guid.NewGuid(), HouseholdId = otherHouseholdId, EntityType = "MeterReading", EntityId = otherReading.Id,
            FieldName = "KwhValue", OldValue = "998", NewValue = "999", CorrectedAtUtc = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var reader = new HouseholdExportReader(dbContext);

        var result = await reader.GetExportDataAsync(householdId, TestContext.Current.CancellationToken);

        result.HouseholdMembers.ShouldBeEmpty();
        result.Rooms.ShouldBeEmpty();
        result.PowerPoints.ShouldBeEmpty();
        result.Devices.ShouldBeEmpty();
        result.MainMeter.ShouldBeNull();
        result.MeterReadings.ShouldBeEmpty();
        result.MeterRegressionPrompts.ShouldBeEmpty();
        result.Tariffs.ShouldBeEmpty();
        result.Events.ShouldBeEmpty();
        result.SmartPlugReadings.ShouldBeEmpty();
        result.StatusSnapshots.ShouldBeEmpty();
        result.AuditCorrections.ShouldBeEmpty();
    }
}

public class PostgresHouseholdExportReaderTests : HouseholdExportReaderTestsBase, IAsyncLifetime
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

public class SqlServerHouseholdExportReaderTests : HouseholdExportReaderTestsBase, IAsyncLifetime
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
