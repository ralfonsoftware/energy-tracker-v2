using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

// Regression test for the AD-2 divergence spec-datetimeoffset-utc-normalization.md fixes: Npgsql
// throws ArgumentException writing a non-zero-offset DateTimeOffset to "timestamp with time zone"
// ("only offset 0 (UTC) is supported"), while SQL Server's datetimeoffset accepts it. Only a real
// engine (Testcontainer) can catch this — an InMemory/SQLite provider would not reproduce it.
//
// Covers both DateTimeOffset properties the spec's Problem section names as "most notably"
// exposed: Event.OccurredAt (via EventRepository) and MeterReading.ReadingTimestamp (via
// MeterReadingRepository) — testing only one would leave the other's regression path unverified.
//
// Review loop 1 finding: an EF Core ValueConverter only translates the CLR<->provider boundary
// during an actual write/read — it never rewrites an already-tracked entity's CLR property after
// SaveChangesAsync. So the *same instance* AddAsync returns (what the API layer actually builds
// its response from) must be asserted directly, not only via a fresh DbContext reload — a fresh
// DbContext would pass even if the repository forgot to reload the returned instance itself.
public abstract class UtcDateTimeOffsetConverterTestsBase
{
    protected abstract Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken);

    protected sealed class FixedHouseholdAccessor(Guid householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;

        public Guid? HouseholdMemberId => null;
    }

    private static async Task SeedHouseholdAsync(EnergyTrackerDbContext dbContext, Guid householdId, CancellationToken cancellationToken)
    {
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Guid> SeedHouseholdAndMainMeterAsync(EnergyTrackerDbContext dbContext, Guid householdId, CancellationToken cancellationToken)
    {
        await SeedHouseholdAsync(dbContext, householdId, cancellationToken);
        var mainMeterId = Guid.NewGuid();
        dbContext.MainMeters.Add(new MainMeter { Id = mainMeterId, HouseholdId = householdId, CreatedAtUtc = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync(cancellationToken);
        return mainMeterId;
    }

    // The concrete regression this spec fixes: before the converter, this write throws
    // DbUpdateException -> ArgumentException on Postgres. After, it must persist AND the instance
    // AddAsync hands back (what EventEndpoints.ToResponse builds the API response from) must
    // already show the normalized +00:00 offset.
    [Fact]
    public async Task Event_AddAsync_returns_the_same_instance_already_normalized_to_a_non_zero_offset_write()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new EventRepository(dbContext);
        var submitted = new DateTimeOffset(2026, 8, 1, 9, 15, 0, TimeSpan.FromHours(2));

        var returned = await repository.AddAsync(new Event
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Description = "non-zero offset probe",
            OccurredAt = submitted,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }, TestContext.Current.CancellationToken);

        returned.OccurredAt.Offset.ShouldBe(TimeSpan.Zero);
        returned.OccurredAt.ShouldBe(submitted.ToUniversalTime());
    }

    [Fact]
    public async Task MeterReading_AddAsync_returns_the_same_instance_already_normalized_to_a_non_zero_offset_write()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var mainMeterId = await SeedHouseholdAndMainMeterAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new MeterReadingRepository(dbContext);
        var submitted = new DateTimeOffset(2026, 8, 1, 9, 15, 0, TimeSpan.FromHours(2));

        var returned = await repository.AddAsync(new MeterReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = mainMeterId,
            KwhValue = 123.4m,
            ReadingTimestamp = submitted,
            IdempotencyKey = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }, TestContext.Current.CancellationToken);

        returned.ReadingTimestamp.Offset.ShouldBe(TimeSpan.Zero);
        returned.ReadingTimestamp.ShouldBe(submitted.ToUniversalTime());
    }

    // Blind Hunter, review loop 2: the fast (non-conflict) path's reload was tested above, but the
    // catch(DbUpdateException) "winner" path (AD-16's idempotency-key race) was left unguarded —
    // its comment claims a fresh query "already goes through the converter's read side," but
    // nothing proved that. Forces the same race deliberately (two adds sharing one IdempotencyKey
    // against the same DbContext) to prove the winner it returns is normalized too.
    [Fact]
    public async Task MeterReading_AddAsync_conflict_winner_path_also_returns_a_normalized_offset()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var mainMeterId = await SeedHouseholdAndMainMeterAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new MeterReadingRepository(dbContext);
        var sharedIdempotencyKey = Guid.NewGuid();
        var submitted = new DateTimeOffset(2026, 8, 1, 9, 15, 0, TimeSpan.FromHours(2));

        var first = await repository.AddAsync(new MeterReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = mainMeterId,
            KwhValue = 100m,
            ReadingTimestamp = submitted,
            IdempotencyKey = sharedIdempotencyKey,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }, TestContext.Current.CancellationToken);

        // Same IdempotencyKey, different Id: AD-16's unique index rejects this at SaveChangesAsync,
        // driving AddAsync into its catch(DbUpdateException) "winner" (re-query) path.
        var winner = await repository.AddAsync(new MeterReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = mainMeterId,
            KwhValue = 200m,
            ReadingTimestamp = submitted,
            IdempotencyKey = sharedIdempotencyKey,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }, TestContext.Current.CancellationToken);

        winner.Id.ShouldBe(first.Id);
        winner.ReadingTimestamp.Offset.ShouldBe(TimeSpan.Zero);
        winner.ReadingTimestamp.ShouldBe(submitted.ToUniversalTime());
    }

    // Blind Hunter, review loop 2: TariffRepository has the identical client-supplied-DateTimeOffset
    // create/update-then-echo shape as Event/MeterReading (Tariff.ContractStartDate) but was missed
    // by loop 1's audit. AddAsync and UpdateAsync both needed the same reload fix.
    [Fact]
    public async Task Tariff_AddAsync_returns_the_same_instance_already_normalized_to_a_non_zero_offset_write()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new TariffRepository(dbContext);
        var submitted = new DateTimeOffset(2026, 8, 1, 9, 15, 0, TimeSpan.FromHours(2));

        var returned = await repository.AddAsync(new Tariff
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MonthlyBaseFee = 10m,
            PricePerKwh = 0.3m,
            Currency = "EUR",
            ContractStartDate = submitted,
            ContractPeriodMonths = 12,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }, TestContext.Current.CancellationToken);

        returned.ContractStartDate.Offset.ShouldBe(TimeSpan.Zero);
        returned.ContractStartDate.ShouldBe(submitted.ToUniversalTime());
    }

    [Fact]
    public async Task Tariff_UpdateAsync_returns_the_same_instance_already_normalized_to_a_non_zero_offset_write()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new TariffRepository(dbContext);
        var original = await repository.AddAsync(new Tariff
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MonthlyBaseFee = 10m,
            PricePerKwh = 0.3m,
            Currency = "EUR",
            ContractStartDate = DateTimeOffset.UtcNow,
            ContractPeriodMonths = 12,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }, TestContext.Current.CancellationToken);
        var submitted = new DateTimeOffset(2026, 8, 1, 9, 15, 0, TimeSpan.FromHours(2));

        var updated = await repository.UpdateAsync(
            original.Id, monthlyBaseFee: null, pricePerKwh: null, currency: null,
            contractStartDate: submitted, contractPeriodMonths: null, expectedVersion: original.Version,
            TestContext.Current.CancellationToken);

        updated.ContractStartDate.Offset.ShouldBe(TimeSpan.Zero);
        updated.ContractStartDate.ShouldBe(submitted.ToUniversalTime());
    }

    [Fact]
    public async Task A_negative_offset_DateTimeOffset_write_round_trips_to_the_same_instant()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new EventRepository(dbContext);
        var submitted = new DateTimeOffset(2026, 8, 1, 9, 15, 0, TimeSpan.FromHours(-5));

        var returned = await repository.AddAsync(new Event
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Description = "negative offset probe",
            OccurredAt = submitted,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }, TestContext.Current.CancellationToken);

        returned.OccurredAt.Offset.ShouldBe(TimeSpan.Zero);
        returned.OccurredAt.ShouldBe(submitted.ToUniversalTime());
    }

    [Fact]
    public async Task An_offset_zero_write_is_unchanged_from_today()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new EventRepository(dbContext);
        var submitted = DateTimeOffset.UtcNow;

        var returned = await repository.AddAsync(new Event
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Description = "already-UTC probe",
            OccurredAt = submitted,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }, TestContext.Current.CancellationToken);

        returned.OccurredAt.ShouldBe(submitted);
    }

    // Nullable DateTimeOffset? must be normalized identically — this is why the converter is
    // registered for both Properties<DateTimeOffset>() and Properties<DateTimeOffset?>(). Uses a
    // fresh DbContext because Room has no repository of its own in this codebase to exercise the
    // same-instance path through.
    [Fact]
    public async Task A_nullable_DateTimeOffset_with_a_non_zero_offset_is_normalized_identically()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);

        var submittedArchivedAt = new DateTimeOffset(2026, 8, 1, 9, 15, 0, TimeSpan.FromHours(2));
        var room = new Room
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Name = "Kitchen",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ArchivedAt = submittedArchivedAt,
        };

        dbContext.Rooms.Add(room);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var freshDbContext = OpenFreshDbContext(householdId);
        var reloaded = await freshDbContext.Rooms.IgnoreQueryFilters().SingleAsync(r => r.Id == room.Id, TestContext.Current.CancellationToken);
        reloaded.ArchivedAt.ShouldNotBeNull();
        reloaded.ArchivedAt!.Value.Offset.ShouldBe(TimeSpan.Zero);
        reloaded.ArchivedAt.Value.ShouldBe(submittedArchivedAt.ToUniversalTime());
    }

    protected abstract EnergyTrackerDbContext OpenFreshDbContext(Guid householdId);
}

public class PostgresUtcDateTimeOffsetConverterTests : UtcDateTimeOffsetConverterTestsBase, IAsyncLifetime
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

    protected override EnergyTrackerDbContext OpenFreshDbContext(Guid householdId)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString());
        return new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
    }
}

public class SqlServerUtcDateTimeOffsetConverterTests : UtcDateTimeOffsetConverterTestsBase, IAsyncLifetime
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

    protected override EnergyTrackerDbContext OpenFreshDbContext(Guid householdId)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString());
        return new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
    }
}
