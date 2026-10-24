using System.Security.Claims;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

// Story 11.12 (audit C2, AD-3, AD-2): the one membership lookup, asynchronous, by validated issuer and
// subject, against real Postgres and SQL Server. The query shape (an anonymous-type projection, because
// Npgsql cannot read a server-side ValueTuple) is the part only a real engine can prove.
public abstract class HouseholdMembershipResolverTestsBase
{
    protected const string Issuer = "https://issuer.example";

    protected abstract Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(CancellationToken cancellationToken);

    protected abstract DbContextOptions<HouseholdMembershipDbContext> MembershipOptions();

    private static ClaimsPrincipal Principal(string? subject, string? issuer)
    {
        var claims = new List<Claim>();
        if (subject is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, subject, ClaimValueTypes.String, "ambient-issuer-that-must-be-ignored"));
        }

        if (issuer is not null)
        {
            claims.Add(new Claim(HouseholdClaimTypes.ValidatedIssuer, issuer));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private async Task<HouseholdMember> SeedMemberAsync(string issuer = Issuer)
    {
        await using var seed = await OpenMigratedDbContextAsync(TestContext.Current.CancellationToken);
        var household = new Household { Id = Guid.NewGuid(), Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow };
        var member = new HouseholdMember
        {
            Id = Guid.NewGuid(),
            HouseholdId = household.Id,
            ExternalIssuer = issuer,
            ExternalSubjectId = $"sub-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        seed.Households.Add(household);
        seed.HouseholdMembers.Add(member);
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        return member;
    }

    private (HouseholdMembershipResolver Resolver, IDbContextFactory<HouseholdMembershipDbContext> Factory) NewResolver(
        Func<HouseholdMembershipDbContext>? create = null)
    {
        var factory = Substitute.For<IDbContextFactory<HouseholdMembershipDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(create is null ? new HouseholdMembershipDbContext(MembershipOptions()) : create()));
        return (new HouseholdMembershipResolver(factory), factory);
    }

    [Fact]
    public async Task A_seeded_member_resolves_to_its_member_id_and_household_id()
    {
        var member = await SeedMemberAsync();
        var (resolver, _) = NewResolver();

        var resolved = await resolver.ResolveAsync(Principal(member.ExternalSubjectId, Issuer), TestContext.Current.CancellationToken);

        resolved.ShouldBe(new ResolvedHouseholdMembership(member.Id, member.HouseholdId));
    }

    [Fact]
    public async Task An_unknown_subject_the_same_subject_under_another_issuer_and_the_same_issuer_with_another_subject_resolve_to_none()
    {
        var member = await SeedMemberAsync();
        var (resolver, _) = NewResolver();

        var unknown = await resolver.ResolveAsync(Principal("nobody", Issuer), TestContext.Current.CancellationToken);
        var otherIssuer = await resolver.ResolveAsync(Principal(member.ExternalSubjectId, "https://another-issuer.example"), TestContext.Current.CancellationToken);
        var otherSubject = await resolver.ResolveAsync(Principal(member.ExternalSubjectId + "-x", Issuer), TestContext.Current.CancellationToken);

        unknown.ShouldBe(ResolvedHouseholdMembership.None);
        otherIssuer.ShouldBe(ResolvedHouseholdMembership.None);
        otherSubject.ShouldBe(ResolvedHouseholdMembership.None);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task A_principal_without_the_subject_or_the_validated_issuer_resolves_to_none_without_opening_a_context(bool hasSubject, bool hasIssuer)
    {
        var (resolver, factory) = NewResolver();

        var resolved = await resolver.ResolveAsync(
            Principal(hasSubject ? "someone" : null, hasIssuer ? Issuer : null), TestContext.Current.CancellationToken);

        resolved.ShouldBe(ResolvedHouseholdMembership.None);
        await factory.DidNotReceive().CreateDbContextAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_cancelled_token_cancels_the_query_instead_of_returning_a_value()
    {
        var member = await SeedMemberAsync();
        var (resolver, _) = NewResolver();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var thrown = await Record.ExceptionAsync(() => resolver.ResolveAsync(Principal(member.ExternalSubjectId, Issuer), cancelled.Token));

        thrown.ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task The_context_handed_out_by_the_factory_is_disposed_asynchronously_after_the_lookup()
    {
        var member = await SeedMemberAsync();
        var created = new List<DisposalRecordingContext>();
        var (resolver, _) = NewResolver(() =>
        {
            var context = new DisposalRecordingContext(MembershipOptions());
            created.Add(context);
            return context;
        });

        await resolver.ResolveAsync(Principal(member.ExternalSubjectId, Issuer), TestContext.Current.CancellationToken);

        created.Count.ShouldBe(1);
        created[0].DisposedAsync.ShouldBeTrue("the resolver must `await using` the context it asks the factory for");
    }

    private sealed class DisposalRecordingContext(DbContextOptions<HouseholdMembershipDbContext> options) : HouseholdMembershipDbContext(options)
    {
        public bool DisposedAsync { get; private set; }

        public override ValueTask DisposeAsync()
        {
            DisposedAsync = true;
            return base.DisposeAsync();
        }
    }
}

public class PostgresHouseholdMembershipResolverTests : HouseholdMembershipResolverTestsBase, IAsyncLifetime
{
    private readonly PostgresTestDatabase _container = new();
    private bool _migrated;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    protected override async Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));
        var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new NoOpAccessor());
        if (!_migrated)
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
            _migrated = true;
        }

        return dbContext;
    }

    protected override DbContextOptions<HouseholdMembershipDbContext> MembershipOptions() =>
        new DbContextOptionsBuilder<HouseholdMembershipDbContext>().UseNpgsql(_container.GetConnectionString()).Options;

    private sealed class NoOpAccessor : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId => null;

        public Guid? HouseholdMemberId => null;
    }
}

public class SqlServerHouseholdMembershipResolverTests : HouseholdMembershipResolverTestsBase, IAsyncLifetime
{
    private readonly SqlServerTestDatabase _container = new();
    private bool _migrated;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    protected override async Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer"));
        var dbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new NoOpAccessor());
        if (!_migrated)
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
            _migrated = true;
        }

        return dbContext;
    }

    protected override DbContextOptions<HouseholdMembershipDbContext> MembershipOptions() =>
        new DbContextOptionsBuilder<HouseholdMembershipDbContext>().UseSqlServer(_container.GetConnectionString()).Options;

    private sealed class NoOpAccessor : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId => null;

        public Guid? HouseholdMemberId => null;
    }
}
