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

    // HouseholdExportReader's keyset pagination (spec-household-export-oom-fix.md) pages at 500
    // rows internally — must exceed that to actually exercise a page boundary rather than a single
    // round trip.
    private const int RowCountExceedingOnePage = 640;

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

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
        (await ToListAsync(result.HouseholdMembers)).Single().Id.ShouldBe(member.Id);
        result.MainMeter.ShouldNotBeNull();
        result.MainMeter!.Id.ShouldBe(mainMeter.Id);
        (await ToListAsync(result.Rooms)).Single().Id.ShouldBe(room.Id);
        var powerPoints = await ToListAsync(result.PowerPoints);
        powerPoints.Single().Id.ShouldBe(powerPoint.Id);
        powerPoints.Single().ArchivedAt.ShouldNotBeNull();
        (await ToListAsync(result.Devices)).Single().Id.ShouldBe(device.Id);
        (await ToListAsync(result.MeterReadings)).Single().Id.ShouldBe(reading.Id);
        (await ToListAsync(result.Tariffs)).Single().Id.ShouldBe(tariff.Id);
        (await ToListAsync(result.Events)).Single().Id.ShouldBe(@event.Id);
        (await ToListAsync(result.SmartPlugReadings)).Single().Id.ShouldBe(smartPlugReading.Id);
        (await ToListAsync(result.StatusSnapshots)).Single().Id.ShouldBe(snapshot.Id);
        (await ToListAsync(result.AuditCorrections)).Single().Id.ShouldBe(correction.Id);
    }

    // AC #2 (paged rewrite vs. today's unbounded read, set-equal): proves the keyset loop actually
    // spans multiple round trips (RowCountExceedingOnePage > the reader's internal page size) and
    // returns every row exactly once, in both directions (no drops, no duplicates).
    [Fact]
    public async Task Pages_across_multiple_pages_without_losing_or_duplicating_rows()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(NewHousehold(householdId));
        var mainMeter = new MainMeter { Id = Guid.NewGuid(), HouseholdId = householdId, CreatedAtUtc = DateTimeOffset.UtcNow };
        dbContext.MainMeters.Add(mainMeter);
        var expectedIds = new List<Guid>();
        for (var i = 0; i < RowCountExceedingOnePage; i++)
        {
            var reading = new MeterReading
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                MainMeterId = mainMeter.Id,
                KwhValue = i,
                ReadingTimestamp = DateTimeOffset.UtcNow.AddMinutes(-i),
                IdempotencyKey = Guid.NewGuid(),
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-i),
            };
            expectedIds.Add(reading.Id);
            dbContext.MeterReadings.Add(reading);
        }

        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var reader = new HouseholdExportReader(dbContext);

        var result = await reader.GetExportDataAsync(householdId, TestContext.Current.CancellationToken);
        var meterReadings = await ToListAsync(result.MeterReadings);

        meterReadings.Count.ShouldBe(RowCountExceedingOnePage);
        meterReadings.Select(r => r.Id).ShouldBe(expectedIds, ignoreOrder: true);
        meterReadings.Select(r => r.Id).Distinct().Count().ShouldBe(RowCountExceedingOnePage);

        // spec-household-export-observability.md: 640 rows / PageSize 500 -> 2 round trips (a full
        // 500-row page, then a short 140-row page), with the row count summed across both.
        var meterReadingStats = result.Stats.Snapshot()["meterReadings"];
        meterReadingStats.RowCount.ShouldBe(RowCountExceedingOnePage);
        meterReadingStats.PageCount.ShouldBe(2);
    }

    // spec-household-export-observability.md: an empty collection still runs exactly one query
    // (which comes back short of PageSize and stops the loop) -- that's one page round trip
    // recorded against zero rows, not zero round trips.
    [Fact]
    public async Task Stats_record_one_page_round_trip_for_an_empty_collection()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(NewHousehold(householdId));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var reader = new HouseholdExportReader(dbContext);

        var result = await reader.GetExportDataAsync(householdId, TestContext.Current.CancellationToken);
        await ToListAsync(result.Rooms);

        var roomStats = result.Stats.Snapshot()["rooms"];
        roomStats.RowCount.ShouldBe(0);
        roomStats.PageCount.ShouldBe(1);
    }

    // spec-household-export-observability.md: a Household with no MainMeter never runs a
    // MeterReadings query at all (existing short-circuit) -- the collection must still appear in
    // the stats map, pre-seeded at zero, rather than being silently absent.
    [Fact]
    public async Task Stats_pre_seed_MeterReadings_at_zero_when_no_MainMeter_exists()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(NewHousehold(householdId));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var reader = new HouseholdExportReader(dbContext);

        var result = await reader.GetExportDataAsync(householdId, TestContext.Current.CancellationToken);
        await ToListAsync(result.MeterReadings);

        var meterReadingStats = result.Stats.Snapshot()["meterReadings"];
        meterReadingStats.RowCount.ShouldBe(0);
        meterReadingStats.PageCount.ShouldBe(0);
    }

    // I/O matrix "Page-boundary duplicates": two SmartPlugReadings sharing an IntervalStart must
    // both survive — the Id tiebreaker (not IntervalStart alone) is what makes the cursor unique
    // and stable across page boundaries.
    [Fact]
    public async Task SmartPlugReadings_sharing_the_same_IntervalStart_are_not_skipped_or_duplicated()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(NewHousehold(householdId));
        // Two DIFFERENT (non-null) PowerPointIds — SmartPlugReadingConfiguration's
        // (PowerPointId, IntervalStart) unique index (Story 3.4 AD-20) forbids two MATCHED readings
        // on the SAME Power Point from sharing an IntervalStart, but two readings on different Power
        // Points legitimately can (that's the real-world case this test's scenario models).
        var room = new Room { Id = Guid.NewGuid(), HouseholdId = householdId, Name = "Kitchen", CreatedAtUtc = DateTimeOffset.UtcNow };
        dbContext.Rooms.Add(room);
        var powerPointA = new PowerPoint
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, RoomId = room.Id, Name = "Outlet A", CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var powerPointB = new PowerPoint
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, RoomId = room.Id, Name = "Outlet B", CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        dbContext.PowerPoints.AddRange(powerPointA, powerPointB);
        var sharedIntervalStart = DateTimeOffset.UtcNow.AddHours(-1);
        var first = new SmartPlugReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            PowerPointId = powerPointA.Id,
            RoomName = "Kitchen",
            PowerPointName = "Outlet A",
            DeviceName = "Kettle",
            IntervalStart = sharedIntervalStart,
            IntervalEnd = sharedIntervalStart.AddMinutes(15),
            KwhValue = 0.1m,
        };
        var second = new SmartPlugReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            PowerPointId = powerPointB.Id,
            RoomName = "Kitchen",
            PowerPointName = "Outlet B",
            DeviceName = "Toaster",
            IntervalStart = sharedIntervalStart,
            IntervalEnd = sharedIntervalStart.AddMinutes(15),
            KwhValue = 0.2m,
        };
        dbContext.SmartPlugReadings.AddRange(first, second);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var reader = new HouseholdExportReader(dbContext);

        var result = await reader.GetExportDataAsync(householdId, TestContext.Current.CancellationToken);
        var smartPlugReadings = await ToListAsync(result.SmartPlugReadings);

        smartPlugReadings.Select(r => r.Id).ShouldBe([first.Id, second.Id], ignoreOrder: true);
    }

    // Events pages by a three-column cursor tuple (OccurredAt, CreatedAtUtc, Id) — the most
    // structurally complex of every paged entity here (spec Design Notes: reuses the existing
    // (HouseholdId, OccurredAt, CreatedAtUtc) index rather than a new one, with Id as an extra
    // in-memory tiebreaker) — so it's the case most likely to hide an off-by-one/comparison bug.
    // Exercises both a multi-page span AND two Events sharing the exact same (OccurredAt,
    // CreatedAtUtc) pair, which only the Id tiebreaker can keep from being skipped or duplicated.
    [Fact]
    public async Task Events_page_correctly_across_multiple_pages_including_a_full_cursor_tuple_tie()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(NewHousehold(householdId));
        var expectedIds = new List<Guid>();
        var tiedOccurredAt = DateTimeOffset.UtcNow.AddDays(-1);
        var tiedCreatedAt = DateTimeOffset.UtcNow;
        for (var i = 0; i < RowCountExceedingOnePage; i++)
        {
            var @event = new Event
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                Description = $"event-{i}",
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-i),
            };
            expectedIds.Add(@event.Id);
            dbContext.Events.Add(@event);
        }

        // Two Events sharing the exact same (OccurredAt, CreatedAtUtc) pair — only the Id tiebreaker
        // (not the tuple alone) keeps the cursor unique and stable across a page boundary.
        var tiedFirst = new Event
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, Description = "tied-first",
            OccurredAt = tiedOccurredAt, CreatedAtUtc = tiedCreatedAt,
        };
        var tiedSecond = new Event
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, Description = "tied-second",
            OccurredAt = tiedOccurredAt, CreatedAtUtc = tiedCreatedAt,
        };
        dbContext.Events.AddRange(tiedFirst, tiedSecond);
        expectedIds.Add(tiedFirst.Id);
        expectedIds.Add(tiedSecond.Id);

        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var reader = new HouseholdExportReader(dbContext);

        var result = await reader.GetExportDataAsync(householdId, TestContext.Current.CancellationToken);
        var events = await ToListAsync(result.Events);

        events.Count.ShouldBe(expectedIds.Count);
        events.Select(e => e.Id).ShouldBe(expectedIds, ignoreOrder: true);
        events.Select(e => e.Id).Distinct().Count().ShouldBe(expectedIds.Count);
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

        (await ToListAsync(result.HouseholdMembers)).ShouldBeEmpty();
        (await ToListAsync(result.Rooms)).ShouldBeEmpty();
        (await ToListAsync(result.PowerPoints)).ShouldBeEmpty();
        (await ToListAsync(result.Devices)).ShouldBeEmpty();
        result.MainMeter.ShouldBeNull();
        (await ToListAsync(result.MeterReadings)).ShouldBeEmpty();
        (await ToListAsync(result.MeterRegressionPrompts)).ShouldBeEmpty();
        (await ToListAsync(result.Tariffs)).ShouldBeEmpty();
        (await ToListAsync(result.Events)).ShouldBeEmpty();
        (await ToListAsync(result.SmartPlugReadings)).ShouldBeEmpty();
        (await ToListAsync(result.StatusSnapshots)).ShouldBeEmpty();
        (await ToListAsync(result.AuditCorrections)).ShouldBeEmpty();
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
