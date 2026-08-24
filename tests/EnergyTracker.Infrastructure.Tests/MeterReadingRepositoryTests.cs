using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Domain.Calculations;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Infrastructure.Tests;

// MeterReadingRepository.GetRecentByMainMeterAsync's bounded/widened fetch is the exact ~10 lines
// that sank three prior review rounds (see the spec's Code Change Log) — every scenario here runs
// against BOTH real providers (AD-2), unlike most repository test files in this codebase which
// only exercise Postgres, because the round-3 bug was a query-shape bug that only a real database
// round-trip (not a mock) could ever have caught.
public abstract class MeterReadingRepositoryTestsBase
{
    private sealed class FixedHouseholdAccessor(Guid householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;
    }

    protected abstract Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(Guid householdId, CancellationToken cancellationToken);

    protected static EnergyTrackerDbContext NewDbContext(DbContextOptions<EnergyTrackerDbContext> options, Guid householdId) =>
        new(options, new FixedHouseholdAccessor(householdId));

    protected static async Task<Guid> SeedHouseholdAndMainMeterAsync(EnergyTrackerDbContext dbContext, Guid householdId, CancellationToken cancellationToken)
    {
        var mainMeterId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = now });
        dbContext.MainMeters.Add(new MainMeter { Id = mainMeterId, HouseholdId = householdId, CreatedAtUtc = now });
        await dbContext.SaveChangesAsync(cancellationToken);
        return mainMeterId;
    }

    protected static MeterReading NewReading(Guid householdId, Guid mainMeterId, decimal kwhValue, DateTimeOffset readingTimestamp) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        MainMeterId = mainMeterId,
        KwhValue = kwhValue,
        ReadingTimestamp = readingTimestamp,
        IdempotencyKey = Guid.NewGuid(),
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Returns_empty_when_the_MainMeter_has_no_readings_at_all()
    {
        var householdId = Guid.NewGuid();
        var mainMeterId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        dbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow });
        dbContext.MainMeters.Add(new MainMeter { Id = mainMeterId, HouseholdId = householdId, CreatedAtUtc = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new MeterReadingRepository(dbContext);

        var result = await repository.GetRecentByMainMeterAsync(mainMeterId, windowDays: 400, mustIncludeReadingId: null, TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_must_include_anchor_only_readings_within_windowDays_of_the_MainMeters_own_latest_reading_are_returned()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var mainMeterId = await SeedHouseholdAndMainMeterAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var latest = DateTimeOffset.UtcNow;
        var withinWindow = NewReading(householdId, mainMeterId, 100m, latest.AddDays(-29));
        var beyondWindow = NewReading(householdId, mainMeterId, 0m, latest.AddDays(-31));
        var latestReading = NewReading(householdId, mainMeterId, 200m, latest);
        dbContext.MeterReadings.AddRange(withinWindow, beyondWindow, latestReading);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new MeterReadingRepository(dbContext);

        var result = await repository.GetRecentByMainMeterAsync(mainMeterId, windowDays: 30, mustIncludeReadingId: null, TestContext.Current.CancellationToken);

        result.Select(r => r.Id).ShouldBe([withinWindow.Id, latestReading.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task The_must_include_anchor_widens_the_fetch_to_a_full_trailing_window_behind_it_not_just_the_anchor_itself()
    {
        // This is the direct regression guard for round 3's bug: it set cutoff to the anchor's
        // BARE timestamp (no margin), so `withinWindowOfAnchor` below — which sits before the
        // anchor's own timestamp but still within windowDays of it — would have been wrongly
        // excluded. The round-4 formula (Min(latest, anchor) - windowDays) must include it.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var mainMeterId = await SeedHouseholdAndMainMeterAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var latest = DateTimeOffset.UtcNow;
        // Far outside a 30-day base window from `latest` — only widening via the must-include
        // anchor can pull this (and its own trailing window) into the fetch.
        var anchor = NewReading(householdId, mainMeterId, 1000m, latest.AddDays(-200));
        var withinWindowOfAnchor = NewReading(householdId, mainMeterId, 900m, anchor.ReadingTimestamp.AddDays(-29));
        var beyondWindowOfAnchor = NewReading(householdId, mainMeterId, 0m, anchor.ReadingTimestamp.AddDays(-31));
        var latestReading = NewReading(householdId, mainMeterId, 2000m, latest);
        dbContext.MeterReadings.AddRange(anchor, withinWindowOfAnchor, beyondWindowOfAnchor, latestReading);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new MeterReadingRepository(dbContext);

        var result = await repository.GetRecentByMainMeterAsync(mainMeterId, windowDays: 30, mustIncludeReadingId: anchor.Id, TestContext.Current.CancellationToken);

        result.Select(r => r.Id).ShouldBe([withinWindowOfAnchor.Id, anchor.Id, latestReading.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task The_widen_holds_regardless_of_a_60_plus_day_gap_between_the_anchor_and_the_base_windows_own_cutoff()
    {
        // Round 2's own bug only reproduced with a >35-day gap; this proves the round-4 formula
        // holds for a materially larger (70-day) gap between the anchor and where the base
        // (non-widened) window would otherwise have started.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var mainMeterId = await SeedHouseholdAndMainMeterAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var latest = DateTimeOffset.UtcNow;
        // Base cutoff (windowDays=30) would be latest-30d; this anchor sits 100 days back — a
        // 70-day gap past that base cutoff.
        var anchor = NewReading(householdId, mainMeterId, 1000m, latest.AddDays(-100));
        var withinWindowOfAnchor = NewReading(householdId, mainMeterId, 900m, anchor.ReadingTimestamp.AddDays(-25));
        var latestReading = NewReading(householdId, mainMeterId, 2000m, latest);
        dbContext.MeterReadings.AddRange(anchor, withinWindowOfAnchor, latestReading);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new MeterReadingRepository(dbContext);

        var result = await repository.GetRecentByMainMeterAsync(mainMeterId, windowDays: 30, mustIncludeReadingId: anchor.Id, TestContext.Current.CancellationToken);

        result.Select(r => r.Id).ShouldBe([withinWindowOfAnchor.Id, anchor.Id, latestReading.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task A_reading_exactly_at_the_cutoff_boundary_is_included()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var mainMeterId = await SeedHouseholdAndMainMeterAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var latest = DateTimeOffset.UtcNow;
        var atBoundary = NewReading(householdId, mainMeterId, 100m, latest.AddDays(-30));
        var oneTickBeforeBoundary = NewReading(householdId, mainMeterId, 0m, latest.AddDays(-30).AddTicks(-1));
        var latestReading = NewReading(householdId, mainMeterId, 200m, latest);
        dbContext.MeterReadings.AddRange(atBoundary, oneTickBeforeBoundary, latestReading);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new MeterReadingRepository(dbContext);

        var result = await repository.GetRecentByMainMeterAsync(mainMeterId, windowDays: 30, mustIncludeReadingId: null, TestContext.Current.CancellationToken);

        // The cutoff test is `ReadingTimestamp >= cutoff`, not `>` — the boundary itself belongs
        // to the window, only strictly-before does not.
        result.Select(r => r.Id).ShouldBe([atBoundary.Id, latestReading.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task A_must_include_anchor_no_older_than_the_MainMeters_own_latest_reading_does_not_narrow_the_base_window()
    {
        // In production, `mustIncludeReadingId` is always the open prompt's PreviousMeterReadingId,
        // whose timestamp is provably <= the MainMeter's own latest reading (it IS one of the
        // MainMeter's readings, so it can never exceed the MAX() that defines `latestTimestamp`).
        // The repository's Min-guard is nonetheless a general-purpose comparison — this proves the
        // "anchor does not push the cutoff earlier" branch behaves correctly too, not just the
        // widen branch every other test here exercises.
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var mainMeterId = await SeedHouseholdAndMainMeterAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var latest = DateTimeOffset.UtcNow;
        var withinWindow = NewReading(householdId, mainMeterId, 100m, latest.AddDays(-10));
        var beyondWindow = NewReading(householdId, mainMeterId, 0m, latest.AddDays(-40));
        var latestReading = NewReading(householdId, mainMeterId, 200m, latest);
        dbContext.MeterReadings.AddRange(withinWindow, beyondWindow, latestReading);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new MeterReadingRepository(dbContext);

        // mustIncludeReadingId points at the MainMeter's own latest reading — the anchor and the
        // base-window reading are identical, so Min(latest, mustInclude) == latest either way.
        var result = await repository.GetRecentByMainMeterAsync(mainMeterId, windowDays: 30, mustIncludeReadingId: latestReading.Id, TestContext.Current.CancellationToken);

        result.Select(r => r.Id).ShouldBe([withinWindow.Id, latestReading.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task A_must_include_id_that_does_not_exist_falls_back_to_the_base_window_instead_of_erroring()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var mainMeterId = await SeedHouseholdAndMainMeterAsync(dbContext, householdId, TestContext.Current.CancellationToken);
        var latest = DateTimeOffset.UtcNow;
        var latestReading = NewReading(householdId, mainMeterId, 200m, latest);
        var withinWindow = NewReading(householdId, mainMeterId, 100m, latest.AddDays(-10));
        var beyondWindow = NewReading(householdId, mainMeterId, 0m, latest.AddDays(-40));
        dbContext.MeterReadings.AddRange(latestReading, withinWindow, beyondWindow);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new MeterReadingRepository(dbContext);

        var result = await repository.GetRecentByMainMeterAsync(mainMeterId, windowDays: 30, mustIncludeReadingId: Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Select(r => r.Id).ShouldBe([latestReading.Id, withinWindow.Id], ignoreOrder: true);
    }
}

public class PostgresMeterReadingRepositoryTests : MeterReadingRepositoryTestsBase, IAsyncLifetime
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

    // Real end-to-end pipeline test — repository -> ExcludeFromOpenPrompt -> ComputePaceToDate —
    // for an open prompt whose PreviousMeterReadingId reading sits outside the base 400-day
    // window, with genuine older reading history seeded within windowDays before that anchor.
    // This is the exact test that would have caught round 3's bug: round 3's own regression test
    // mocked GetRecentByMainMeterAsync's return value instead of exercising the real query, so it
    // asserted correctness the real (buggy) implementation didn't have. Postgres alone is
    // sufficient here per the spec — this test proves the calculation pipeline is wired
    // correctly, not provider portability (MeterReadingRepositoryTestsBase's other tests already
    // cover both providers for the query shape itself).
    [Fact]
    public async Task Bounded_fetch_through_ExcludeFromOpenPrompt_and_ComputePaceToDate_matches_what_an_unbounded_fetch_would_have_produced()
    {
        var householdId = Guid.NewGuid();
        await using var dbContext = await OpenMigratedDbContextAsync(householdId, TestContext.Current.CancellationToken);
        var mainMeterId = await SeedHouseholdAndMainMeterAsync(dbContext, householdId, TestContext.Current.CancellationToken);

        // Readings keep arriving for many months after the prompt opens and stays unresolved
        // (no auto-resolve/age cap — Design Notes) — `latest` below is what keeps pushing the
        // MainMeter's own most recent reading forward, while the pre-trigger history the
        // calculation actually needs (`anchor`, `tooOldEvenUnbounded`) falls further and further
        // outside a naive, non-widened 400-day window.
        var anchorTimestamp = DateTimeOffset.UtcNow.AddDays(-420);
        // Outside windowDays(400) of `anchor` too — proves the widen still correctly bounds
        // itself, and (independently) that ComputePaceToDate's own internal 365-day window would
        // have excluded this from an unbounded fetch's result anyway, so leaving it out of the
        // DB fetch changes nothing about the final Pace figure.
        var tooOldEvenUnbounded = NewReading(householdId, mainMeterId, 500m, anchorTimestamp.AddDays(-450));
        // Genuine older history within windowDays(400) of the anchor — must be fetched and
        // included by the widen.
        var olderHistory = NewReading(householdId, mainMeterId, 3000m, anchorTimestamp.AddDays(-200));
        var anchor = NewReading(householdId, mainMeterId, 4000m, anchorTimestamp);
        // The trigger: a regression relative to `anchor`, one day later.
        var trigger = NewReading(householdId, mainMeterId, 500m, anchorTimestamp.AddDays(1));
        // Readings logged after the prompt opened, still unresolved — pushes the MainMeter's own
        // "latest reading" far ahead of `anchor`/`olderHistory`.
        var afterTrigger = NewReading(householdId, mainMeterId, 600m, anchorTimestamp.AddDays(40));
        var latest = NewReading(householdId, mainMeterId, 700m, anchorTimestamp.AddDays(420));
        dbContext.MeterReadings.AddRange(tooOldEvenUnbounded, olderHistory, anchor, trigger, afterTrigger, latest);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new MeterReadingRepository(dbContext);

        var boundedFetch = await repository.GetRecentByMainMeterAsync(mainMeterId, windowDays: 400, mustIncludeReadingId: anchor.Id, TestContext.Current.CancellationToken);
        var boundedIncluded = PatternDetectiveCalculator.ExcludeFromOpenPrompt(boundedFetch, trigger.Id);
        var boundedResult = PatternDetectiveCalculator.ComputePaceToDate(boundedIncluded);

        // What GetCurrentStatus would have produced before this story's bounding was added —
        // computed here by feeding PatternDetectiveCalculator the FULL seeded history directly,
        // no repository bound at all.
        var allSeededReadings = new[] { tooOldEvenUnbounded, olderHistory, anchor, trigger, afterTrigger, latest }
            .OrderBy(r => r.ReadingTimestamp).ThenBy(r => r.Id).ToList();
        var unboundedIncluded = PatternDetectiveCalculator.ExcludeFromOpenPrompt(allSeededReadings, trigger.Id);
        var unboundedResult = PatternDetectiveCalculator.ComputePaceToDate(unboundedIncluded);

        boundedResult.ShouldNotBeNull();
        unboundedResult.ShouldNotBeNull();
        boundedResult.Value.PaceToDateKwh.ShouldBe(unboundedResult.Value.PaceToDateKwh);
        boundedResult.Value.Elapsed.ShouldBe(unboundedResult.Value.Elapsed);
        // olderHistory (3000) -> anchor (4000) is the only pair ComputePaceToDate's own 365-day
        // window keeps either way: 1000 kWh over 200 days.
        boundedResult.Value.PaceToDateKwh.ShouldBe(1000m);
        boundedResult.Value.Elapsed.ShouldBe(TimeSpan.FromDays(200));
    }
}

public class SqlServerMeterReadingRepositoryTests : MeterReadingRepositoryTestsBase, IAsyncLifetime
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
