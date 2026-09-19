using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

// AC #4: an Event's TaggedEntityName must stay whatever it was snapshotted as at write time, even
// after the tagged item is later renamed, archived, or re-parented.
//
// Note on what these tests do and do not prove: Event has no navigation property to Room/PowerPoint/
// Device and EventConfiguration deliberately configures no relationship, so TaggedEntityName is a
// plain scalar that EF has no mechanism to rewrite. These tests are therefore a guard against a
// future change introducing such a relationship (or a re-derive-on-read), not a reproduction of the
// Story 5.1 EditTariff identity-map bug — that bug needed a tracked entity whose own field was
// re-read after a write, which is structurally impossible here.
//
// Run against both providers (AD-2): the SqlServer migration would otherwise ship with no runtime
// coverage at all.
public abstract class EventRepositoryTestsBase
{
    protected abstract Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken);

    protected abstract EnergyTrackerDbContext OpenFreshDbContext(Guid householdId);

    protected sealed class FixedHouseholdAccessor(Guid householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;

        public Guid? HouseholdMemberId => null;
    }

    protected static async Task SeedHouseholdAsync(EnergyTrackerDbContext dbContext, Guid householdId, CancellationToken cancellationToken)
    {
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    [Fact]
    public async Task AddAsync_persists_an_Event()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new EventRepository(dbContext);
        var @event = new Event
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Description = "cooked 2h",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        await repository.AddAsync(@event, TestContext.Current.CancellationToken);

        var reloaded = await dbContext.Events.SingleAsync(e => e.Id == @event.Id, TestContext.Current.CancellationToken);
        reloaded.Description.ShouldBe("cooked 2h");
    }

    [Fact]
    public async Task The_TaggedEntityName_snapshot_survives_a_later_rename_and_archive_of_the_tagged_Room()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var room = new Room { Id = Guid.NewGuid(), HouseholdId = householdId, Name = "Kitchen", CreatedAtUtc = DateTimeOffset.UtcNow };
        dbContext.Rooms.Add(room);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new EventRepository(dbContext);
        var @event = new Event
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Description = "cooked 2h",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            TaggedEntityType = "Room",
            TaggedEntityId = room.Id,
            TaggedEntityName = room.Name,
        };
        await repository.AddAsync(@event, TestContext.Current.CancellationToken);

        room.Name = "Dining Room";
        room.ArchivedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var reloaded = await dbContext.Events.SingleAsync(e => e.Id == @event.Id, TestContext.Current.CancellationToken);
        reloaded.TaggedEntityName.ShouldBe("Kitchen");

        // Same assertion from a completely fresh DbContext, ruling out the identity map itself as
        // the reason the snapshot looks unchanged.
        await using var freshDbContext = OpenFreshDbContext(householdId);
        var reloadedFresh = await freshDbContext.Events.SingleAsync(e => e.Id == @event.Id, TestContext.Current.CancellationToken);
        reloadedFresh.TaggedEntityName.ShouldBe("Kitchen");
    }

    // Re-parenting is the case AD-10 is most specifically about: a Power Point moving to another
    // Room must not retroactively rewrite the Room → Power Point label an Event captured earlier.
    [Fact]
    public async Task The_TaggedEntityName_snapshot_survives_a_later_re_parent_of_the_tagged_PowerPoint()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var kitchen = new Room { Id = Guid.NewGuid(), HouseholdId = householdId, Name = "Kitchen", CreatedAtUtc = DateTimeOffset.UtcNow };
        var garage = new Room { Id = Guid.NewGuid(), HouseholdId = householdId, Name = "Garage", CreatedAtUtc = DateTimeOffset.UtcNow };
        var powerPoint = new PowerPoint
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            RoomId = kitchen.Id,
            Name = "Counter outlet",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        dbContext.Rooms.AddRange(kitchen, garage);
        dbContext.PowerPoints.Add(powerPoint);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new EventRepository(dbContext);
        var @event = new Event
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Description = "ran the dehumidifier",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            TaggedEntityType = "PowerPoint",
            TaggedEntityId = powerPoint.Id,
            TaggedEntityName = powerPoint.Name,
        };
        await repository.AddAsync(@event, TestContext.Current.CancellationToken);

        // Moved to another Room and renamed afterwards (Story 2.6's MovePowerPoint).
        powerPoint.RoomId = garage.Id;
        powerPoint.Name = "Workbench outlet";
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var freshDbContext = OpenFreshDbContext(householdId);
        var reloaded = await freshDbContext.Events.SingleAsync(e => e.Id == @event.Id, TestContext.Current.CancellationToken);
        reloaded.TaggedEntityName.ShouldBe("Counter outlet");
        reloaded.TaggedEntityId.ShouldBe(powerPoint.Id);
    }

    // AD-3: the global query filter must scope Events to the current Household, exactly as it does
    // for every other Household-scoped entity.
    [Fact]
    public async Task Events_are_scoped_to_the_current_Household_by_the_AD_3_query_filter()
    {
        var householdId = Guid.NewGuid();
        var otherHouseholdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, otherHouseholdId, TestContext.Current.CancellationToken);
        var repository = new EventRepository(dbContext);
        await repository.AddAsync(new Event
        {
            Id = Guid.NewGuid(),
            HouseholdId = otherHouseholdId,
            Description = "not yours",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }, TestContext.Current.CancellationToken);

        await using var freshDbContext = OpenFreshDbContext(householdId);
        var visible = await freshDbContext.Events.ToListAsync(TestContext.Current.CancellationToken);

        visible.ShouldBeEmpty();
    }

    // AC #1: reverse-chronological by OccurredAt (the date the Event occurred), not insertion order.
    [Fact]
    public async Task GetPageForHouseholdAsync_orders_by_OccurredAt_descending()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new EventRepository(dbContext);
        var older = new Event
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Description = "older",
            OccurredAt = DateTimeOffset.UtcNow.AddDays(-2),
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-2),
        };
        var newer = new Event
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Description = "newer",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        // Inserted in the opposite order from expected read order, to prove sort isn't insertion order.
        await repository.AddAsync(older, TestContext.Current.CancellationToken);
        await repository.AddAsync(newer, TestContext.Current.CancellationToken);

        var (items, totalCount) = await repository.GetPageForHouseholdAsync(1, 20, TestContext.Current.CancellationToken);

        totalCount.ShouldBe(2);
        items.Select(e => e.Id).ShouldBe([newer.Id, older.Id]);
    }

    // Two Events backfilled to the same OccurredAt must not reorder between pages — CreatedAtUtc DESC
    // is the stable tiebreaker.
    [Fact]
    public async Task GetPageForHouseholdAsync_breaks_ties_on_equal_OccurredAt_by_CreatedAtUtc_descending()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new EventRepository(dbContext);
        var sameOccurredAt = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var createdEarlier = new Event
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Description = "created earlier",
            OccurredAt = sameOccurredAt,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
        };
        var createdLater = new Event
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Description = "created later",
            OccurredAt = sameOccurredAt,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await repository.AddAsync(createdEarlier, TestContext.Current.CancellationToken);
        await repository.AddAsync(createdLater, TestContext.Current.CancellationToken);

        var (items, _) = await repository.GetPageForHouseholdAsync(1, 20, TestContext.Current.CancellationToken);

        items.Select(e => e.Id).ShouldBe([createdLater.Id, createdEarlier.Id]);
    }

    [Fact]
    public async Task GetPageForHouseholdAsync_paginates_correctly_at_page_boundaries()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var repository = new EventRepository(dbContext);
        var events = Enumerable.Range(0, 5)
            .Select(i => new Event
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                Description = $"event {i}",
                OccurredAt = DateTimeOffset.UtcNow.AddDays(-i),
                CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-i),
            })
            .ToList();
        foreach (var @event in events)
        {
            await repository.AddAsync(@event, TestContext.Current.CancellationToken);
        }

        var (firstPageItems, totalCount) = await repository.GetPageForHouseholdAsync(1, 2, TestContext.Current.CancellationToken);
        var (secondPageItems, _) = await repository.GetPageForHouseholdAsync(2, 2, TestContext.Current.CancellationToken);
        var (lastPageItems, _) = await repository.GetPageForHouseholdAsync(3, 2, TestContext.Current.CancellationToken);

        totalCount.ShouldBe(5);
        firstPageItems.Select(e => e.Id).ShouldBe([events[0].Id, events[1].Id]);
        secondPageItems.Select(e => e.Id).ShouldBe([events[2].Id, events[3].Id]);
        lastPageItems.Select(e => e.Id).ShouldBe([events[4].Id]);
    }

    // AD-3 for the read path — mirrors Events_are_scoped_to_the_current_Household_by_the_AD_3_query_filter
    // above, but through the paged read method rather than a bare DbSet query.
    [Fact]
    public async Task GetPageForHouseholdAsync_excludes_another_Households_Events()
    {
        var householdId = Guid.NewGuid();
        var otherHouseholdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        await SeedHouseholdAsync(dbContext, otherHouseholdId, TestContext.Current.CancellationToken);
        var repository = new EventRepository(dbContext);
        await repository.AddAsync(new Event
        {
            Id = Guid.NewGuid(),
            HouseholdId = otherHouseholdId,
            Description = "not yours",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }, TestContext.Current.CancellationToken);
        await repository.AddAsync(new Event
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Description = "yours",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }, TestContext.Current.CancellationToken);

        var (items, totalCount) = await repository.GetPageForHouseholdAsync(1, 20, TestContext.Current.CancellationToken);

        totalCount.ShouldBe(1);
        items.Single().Description.ShouldBe("yours");
    }
}

public class PostgresEventRepositoryTests : EventRepositoryTestsBase, IAsyncLifetime
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

public class SqlServerEventRepositoryTests : EventRepositoryTestsBase, IAsyncLifetime
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
