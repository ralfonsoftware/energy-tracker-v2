using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

// Story 11.9 (AD-26, AD-3, AD-4): member removal, invite revocation and hashed-token lookup against
// real Postgres and SQL Server. HouseholdMember and HouseholdInvite are AD-3 query-filter exceptions,
// so every by-id access is scoped by an explicit HouseholdId predicate inside HouseholdRepository.
public abstract class HouseholdMembershipRepositoryTestsBase
{
    protected abstract Task<EnergyTrackerDbContext> OpenMigratedDbContextAsync(CancellationToken cancellationToken);

    protected abstract EnergyTrackerDbContext OpenFreshDbContext();

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static Household NewHousehold() => new()
    {
        Id = Guid.NewGuid(),
        Locale = "en-US",
        Currency = "USD",
        CreatedAtUtc = Now,
    };

    private static HouseholdMember NewMember(Guid householdId, string? displayName = null) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        ExternalIssuer = "https://issuer.example",
        ExternalSubjectId = $"sub-{Guid.NewGuid():N}",
        DisplayName = displayName,
        CreatedAtUtc = Now,
    };

    private static HouseholdInvite NewInvite(Guid householdId, DateTimeOffset? expiresAtUtc = null, DateTimeOffset? consumedAtUtc = null) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        TokenHash = InviteToken.Hash(InviteToken.Generate()),
        CreatedAtUtc = Now.AddMinutes(-5),
        ExpiresAtUtc = expiresAtUtc ?? Now.AddDays(7),
        ConsumedAtUtc = consumedAtUtc,
    };

    private async Task<(Household Household, List<HouseholdMember> Members)> SeedHouseholdAsync(int memberCount)
    {
        await using var seed = await OpenMigratedDbContextAsync(TestContext.Current.CancellationToken);
        var household = NewHousehold();
        var members = Enumerable.Range(0, memberCount).Select(_ => NewMember(household.Id)).ToList();
        seed.Households.Add(household);
        seed.HouseholdMembers.AddRange(members);
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (household, members);
    }

    private async Task AddAsync(params object[] entities)
    {
        await using var seed = await OpenMigratedDbContextAsync(TestContext.Current.CancellationToken);
        seed.AddRange(entities);
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FindMemberInHouseholdAsync_returns_the_member_of_that_Household_and_null_for_another_Households_member()
    {
        var (householdA, membersA) = await SeedHouseholdAsync(2);
        var (_, membersB) = await SeedHouseholdAsync(1);
        await using var dbContext = OpenFreshDbContext();
        var repository = new HouseholdRepository(dbContext, TimeProvider.System);

        var own = await repository.FindMemberInHouseholdAsync(householdA.Id, membersA[0].Id, TestContext.Current.CancellationToken);
        var foreign = await repository.FindMemberInHouseholdAsync(householdA.Id, membersB[0].Id, TestContext.Current.CancellationToken);

        own.ShouldNotBeNull().Id.ShouldBe(membersA[0].Id);
        foreign.ShouldBeNull();
    }

    [Fact]
    public async Task RemoveMemberAsync_deletes_the_member_expires_open_invites_and_bumps_Household_Version_by_one()
    {
        var (household, members) = await SeedHouseholdAsync(2);
        var (otherHousehold, _) = await SeedHouseholdAsync(2);
        var open1 = NewInvite(household.Id);
        var open2 = NewInvite(household.Id);
        var consumed = NewInvite(household.Id, consumedAtUtc: Now.AddDays(-1));
        var alreadyExpired = NewInvite(household.Id, expiresAtUtc: Now.AddDays(-1));
        var otherOpen = NewInvite(otherHousehold.Id);
        await AddAsync(open1, open2, consumed, alreadyExpired, otherOpen);
        var removedAt = DateTimeOffset.UtcNow;

        await using (var dbContext = OpenFreshDbContext())
        {
            await new HouseholdRepository(dbContext, TimeProvider.System).RemoveMemberAsync(household.Id, members[1].Id, removedAt, TestContext.Current.CancellationToken);
        }

        await using var verify = OpenFreshDbContext();
        (await verify.HouseholdMembers.Where(m => m.HouseholdId == household.Id).Select(m => m.Id).ToListAsync(TestContext.Current.CancellationToken))
            .ShouldBe([members[0].Id]);
        (await verify.HouseholdMembers.CountAsync(m => m.HouseholdId == otherHousehold.Id, TestContext.Current.CancellationToken)).ShouldBe(2);
        (await verify.Households.SingleAsync(h => h.Id == household.Id, TestContext.Current.CancellationToken)).Version.ShouldBe(1);
        (await verify.Households.SingleAsync(h => h.Id == otherHousehold.Id, TestContext.Current.CancellationToken)).Version.ShouldBe(0);

        var invites = await verify.HouseholdInvites.ToDictionaryAsync(i => i.Id, TestContext.Current.CancellationToken);
        foreach (var id in new[] { open1.Id, open2.Id })
        {
            invites[id].ExpiresAtUtc.ShouldBe(removedAt, TimeSpan.FromMilliseconds(5));
            invites[id].Version.ShouldBe(1);
        }

        invites[consumed.Id].ExpiresAtUtc.ShouldBe(consumed.ExpiresAtUtc, TimeSpan.FromMilliseconds(5));
        invites[consumed.Id].Version.ShouldBe(0);
        invites[alreadyExpired.Id].ExpiresAtUtc.ShouldBe(alreadyExpired.ExpiresAtUtc, TimeSpan.FromMilliseconds(5));
        invites[alreadyExpired.Id].Version.ShouldBe(0);
        invites[otherOpen.Id].ExpiresAtUtc.ShouldBe(otherOpen.ExpiresAtUtc, TimeSpan.FromMilliseconds(5));
        invites[otherOpen.Id].Version.ShouldBe(0);
    }

    [Fact]
    public async Task RemoveMemberAsync_throws_not_found_for_a_member_of_another_Household_and_changes_nothing()
    {
        var (household, _) = await SeedHouseholdAsync(2);
        var (_, otherMembers) = await SeedHouseholdAsync(2);
        await using var dbContext = OpenFreshDbContext();

        await Should.ThrowAsync<HouseholdMemberNotFoundException>(() =>
            new HouseholdRepository(dbContext, TimeProvider.System).RemoveMemberAsync(household.Id, otherMembers[0].Id, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));

        await using var verify = OpenFreshDbContext();
        (await verify.HouseholdMembers.CountAsync(m => m.Id == otherMembers[0].Id, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await verify.Households.SingleAsync(h => h.Id == household.Id, TestContext.Current.CancellationToken)).Version.ShouldBe(0);
    }

    [Fact]
    public async Task RemoveMemberAsync_rejects_the_last_member_and_changes_nothing()
    {
        var (household, members) = await SeedHouseholdAsync(1);
        var open = NewInvite(household.Id);
        await AddAsync(open);
        await using var dbContext = OpenFreshDbContext();

        await Should.ThrowAsync<LastHouseholdMemberException>(() =>
            new HouseholdRepository(dbContext, TimeProvider.System).RemoveMemberAsync(household.Id, members[0].Id, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));

        await using var verify = OpenFreshDbContext();
        (await verify.HouseholdMembers.CountAsync(m => m.HouseholdId == household.Id, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await verify.Households.SingleAsync(h => h.Id == household.Id, TestContext.Current.CancellationToken)).Version.ShouldBe(0);
        var invite = await verify.HouseholdInvites.SingleAsync(i => i.Id == open.Id, TestContext.Current.CancellationToken);
        invite.ExpiresAtUtc.ShouldBe(open.ExpiresAtUtc, TimeSpan.FromMilliseconds(5));
        invite.Version.ShouldBe(0);
    }

    [Fact]
    public async Task RemoveMemberAsync_keeps_the_job_rows_of_the_removed_member_with_a_null_attribution()
    {
        var (household, members) = await SeedHouseholdAsync(2);
        var job = new BackgroundJob
        {
            Id = Guid.NewGuid(),
            HouseholdId = household.Id,
            JobType = "ProcessSmartPlugImport",
            Status = BackgroundJobStatus.Completed,
            QueuedByHouseholdMemberId = members[1].Id,
            CreatedAtUtc = Now,
        };
        await AddAsync(job);
        await using (var dbContext = OpenFreshDbContext())
        {
            await new HouseholdRepository(dbContext, TimeProvider.System).RemoveMemberAsync(household.Id, members[1].Id, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        }

        await using var verify = OpenFreshDbContext();
        var kept = await verify.BackgroundJobs.IgnoreQueryFilters().SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        kept.QueuedByHouseholdMemberId.ShouldBeNull();
    }

    // AD-4: the Household.Version token is the race guard. Context 1 holds a stale tracked Household
    // (Version 0); context 2 removes another member and commits; context 1's removal must lose.
    // Three members, so the count is still above one and the version check is what fires.
    [Fact]
    public async Task RemoveMemberAsync_loses_to_a_concurrent_removal_with_a_concurrency_conflict()
    {
        var (household, members) = await SeedHouseholdAsync(3);
        await using var context1 = OpenFreshDbContext();
        await using var context2 = OpenFreshDbContext();
        await context1.Households.SingleAsync(h => h.Id == household.Id, TestContext.Current.CancellationToken);
        await new HouseholdRepository(context2, TimeProvider.System).RemoveMemberAsync(household.Id, members[1].Id, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<HouseholdConcurrencyConflictException>(() =>
            new HouseholdRepository(context1, TimeProvider.System).RemoveMemberAsync(household.Id, members[2].Id, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));

        await using var verify = OpenFreshDbContext();
        var remaining = await verify.HouseholdMembers.Where(m => m.HouseholdId == household.Id).Select(m => m.Id).ToListAsync(TestContext.Current.CancellationToken);
        remaining.ShouldContain(members[2].Id);
        remaining.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Two_simultaneous_removals_never_empty_the_Household()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var (household, members) = await SeedHouseholdAsync(2);
            await using var context1 = OpenFreshDbContext();
            await using var context2 = OpenFreshDbContext();
            var gate = new TaskCompletionSource();

            async Task<Exception?> RemoveAsync(EnergyTrackerDbContext context, Guid target)
            {
                await gate.Task;
                try
                {
                    await new HouseholdRepository(context, TimeProvider.System).RemoveMemberAsync(household.Id, target, DateTimeOffset.UtcNow, CancellationToken.None);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }

            var first = Task.Run(() => RemoveAsync(context1, members[1].Id));
            var second = Task.Run(() => RemoveAsync(context2, members[0].Id));
            gate.SetResult();
            var outcomes = await Task.WhenAll(first, second);

            outcomes.Count(o => o is null).ShouldBe(1, $"attempt {attempt}: exactly one removal succeeds");
            var loser = outcomes.Single(o => o is not null)!;
            (loser is HouseholdConcurrencyConflictException || loser is LastHouseholdMemberException)
                .ShouldBeTrue($"attempt {attempt}: unexpected {loser.GetType().Name}: {loser.Message}");

            await using var verify = OpenFreshDbContext();
            (await verify.HouseholdMembers.CountAsync(m => m.HouseholdId == household.Id, TestContext.Current.CancellationToken)).ShouldBe(1);
        }
    }

    [Fact]
    public async Task RevokeInviteAsync_expires_an_open_invite_and_bumps_its_Version()
    {
        var (household, _) = await SeedHouseholdAsync(1);
        var invite = NewInvite(household.Id);
        await AddAsync(invite);
        var revokedAt = DateTimeOffset.UtcNow;

        await using (var dbContext = OpenFreshDbContext())
        {
            await new HouseholdRepository(dbContext, TimeProvider.System).RevokeInviteAsync(household.Id, invite.Id, revokedAt, TestContext.Current.CancellationToken);
        }

        await using var verify = OpenFreshDbContext();
        var stored = await verify.HouseholdInvites.SingleAsync(i => i.Id == invite.Id, TestContext.Current.CancellationToken);
        stored.ExpiresAtUtc.ShouldBe(revokedAt, TimeSpan.FromMilliseconds(5));
        stored.Version.ShouldBe(1);
        stored.ConsumedAtUtc.ShouldBeNull();
    }

    [Fact]
    public async Task RevokeInviteAsync_reports_not_open_for_another_Households_consumed_expired_and_already_revoked_invites()
    {
        var (household, _) = await SeedHouseholdAsync(1);
        var (otherHousehold, _) = await SeedHouseholdAsync(1);
        var foreign = NewInvite(otherHousehold.Id);
        var consumed = NewInvite(household.Id, consumedAtUtc: Now.AddDays(-1));
        var expired = NewInvite(household.Id, expiresAtUtc: Now.AddDays(-1));
        var open = NewInvite(household.Id);
        await AddAsync(foreign, consumed, expired, open);

        foreach (var inviteId in new[] { foreign.Id, consumed.Id, expired.Id, Guid.NewGuid() })
        {
            await using var dbContext = OpenFreshDbContext();
            await Should.ThrowAsync<HouseholdInviteNotOpenException>(() =>
                new HouseholdRepository(dbContext, TimeProvider.System).RevokeInviteAsync(household.Id, inviteId, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        }

        await using (var dbContext = OpenFreshDbContext())
        {
            await new HouseholdRepository(dbContext, TimeProvider.System).RevokeInviteAsync(household.Id, open.Id, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        }

        await using var again = OpenFreshDbContext();
        await Should.ThrowAsync<HouseholdInviteNotOpenException>(() =>
            new HouseholdRepository(again, TimeProvider.System).RevokeInviteAsync(household.Id, open.Id, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));

        await using var verify = OpenFreshDbContext();
        var untouched = await verify.HouseholdInvites.SingleAsync(i => i.Id == foreign.Id, TestContext.Current.CancellationToken);
        untouched.ExpiresAtUtc.ShouldBe(foreign.ExpiresAtUtc, TimeSpan.FromMilliseconds(5));
        untouched.Version.ShouldBe(0);
    }

    // A revoke racing an accept: the accept committed first, the revoke's tracked copy is stale.
    [Fact]
    public async Task RevokeInviteAsync_loses_to_a_concurrent_accept_and_reports_not_open()
    {
        var (household, _) = await SeedHouseholdAsync(1);
        var invite = NewInvite(household.Id);
        await AddAsync(invite);
        await using var revokeContext = OpenFreshDbContext();
        await using var acceptContext = OpenFreshDbContext();
        await revokeContext.HouseholdInvites.SingleAsync(i => i.Id == invite.Id, TestContext.Current.CancellationToken);
        var toAccept = await acceptContext.HouseholdInvites.SingleAsync(i => i.Id == invite.Id, TestContext.Current.CancellationToken);
        await new HouseholdRepository(acceptContext, TimeProvider.System).AcceptInviteAsync(toAccept, NewMember(household.Id), TestContext.Current.CancellationToken);

        await Should.ThrowAsync<HouseholdInviteNotOpenException>(() =>
            new HouseholdRepository(revokeContext, TimeProvider.System).RevokeInviteAsync(household.Id, invite.Id, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
    }

    // And the other order: the revoke committed first, so the accept (holding the invite it loaded earlier) loses.
    [Fact]
    public async Task AcceptInviteAsync_loses_to_a_concurrent_revoke()
    {
        var (household, _) = await SeedHouseholdAsync(1);
        var invite = NewInvite(household.Id);
        await AddAsync(invite);
        await using var acceptContext = OpenFreshDbContext();
        await using var revokeContext = OpenFreshDbContext();
        var toAccept = await acceptContext.HouseholdInvites.SingleAsync(i => i.Id == invite.Id, TestContext.Current.CancellationToken);
        await new HouseholdRepository(revokeContext, TimeProvider.System).RevokeInviteAsync(household.Id, invite.Id, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<HouseholdInviteExpiredOrConsumedException>(() =>
            new HouseholdRepository(acceptContext, TimeProvider.System).AcceptInviteAsync(toAccept, NewMember(household.Id), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListOpenInvitesAsync_returns_only_this_Households_invites_that_are_neither_consumed_nor_expired()
    {
        var (household, _) = await SeedHouseholdAsync(1);
        var (otherHousehold, _) = await SeedHouseholdAsync(1);
        var open = NewInvite(household.Id);
        await AddAsync(open, NewInvite(household.Id, consumedAtUtc: Now), NewInvite(household.Id, expiresAtUtc: Now.AddDays(-1)), NewInvite(otherHousehold.Id));
        await using var dbContext = OpenFreshDbContext();

        var invites = await new HouseholdRepository(dbContext, TimeProvider.System).ListOpenInvitesAsync(household.Id, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        invites.Select(i => i.Id).ShouldBe([open.Id]);
    }

    [Fact]
    public async Task ListMembersAsync_returns_only_this_Households_members()
    {
        var (household, members) = await SeedHouseholdAsync(2);
        await SeedHouseholdAsync(2);
        await using var dbContext = OpenFreshDbContext();

        var listed = await new HouseholdRepository(dbContext, TimeProvider.System).ListMembersAsync(household.Id, TestContext.Current.CancellationToken);

        listed.Select(m => m.Id).OrderBy(id => id).ShouldBe(members.Select(m => m.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task FindInviteByTokenAsync_finds_a_hash_only_invite_by_its_plaintext_token()
    {
        var (household, _) = await SeedHouseholdAsync(1);
        var token = InviteToken.Generate();
        var invite = new HouseholdInvite { Id = Guid.NewGuid(), HouseholdId = household.Id, TokenHash = InviteToken.Hash(token), CreatedAtUtc = Now, ExpiresAtUtc = Now.AddDays(7) };
        await AddAsync(invite);
        await using var dbContext = OpenFreshDbContext();

        var found = await new HouseholdRepository(dbContext, TimeProvider.System).FindInviteByTokenAsync(token, TestContext.Current.CancellationToken);

        found.ShouldNotBeNull().Id.ShouldBe(invite.Id);
        found.Token.ShouldBeNull();
    }

    [Fact]
    public async Task FindInviteByTokenAsync_finds_a_legacy_plaintext_only_invite()
    {
        var (household, _) = await SeedHouseholdAsync(1);
        var legacyToken = Guid.NewGuid().ToString("N");
        var invite = new HouseholdInvite { Id = Guid.NewGuid(), HouseholdId = household.Id, Token = legacyToken, CreatedAtUtc = Now, ExpiresAtUtc = Now.AddDays(7) };
        await AddAsync(invite);
        await using var dbContext = OpenFreshDbContext();

        var found = await new HouseholdRepository(dbContext, TimeProvider.System).FindInviteByTokenAsync(legacyToken, TestContext.Current.CancellationToken);

        found.ShouldNotBeNull().Id.ShouldBe(invite.Id);
    }

    [Fact]
    public async Task FindInviteByTokenAsync_prefers_the_hash_row_when_both_a_hash_row_and_a_plaintext_row_match()
    {
        var (household, _) = await SeedHouseholdAsync(1);
        var token = Guid.NewGuid().ToString("N");
        var hashRow = new HouseholdInvite { Id = Guid.NewGuid(), HouseholdId = household.Id, TokenHash = InviteToken.Hash(token), CreatedAtUtc = Now, ExpiresAtUtc = Now.AddDays(7) };
        var legacyRow = new HouseholdInvite { Id = Guid.NewGuid(), HouseholdId = household.Id, Token = token, CreatedAtUtc = Now, ExpiresAtUtc = Now.AddDays(7) };
        await AddAsync(hashRow, legacyRow);
        await using var dbContext = OpenFreshDbContext();

        var found = await new HouseholdRepository(dbContext, TimeProvider.System).FindInviteByTokenAsync(token, TestContext.Current.CancellationToken);

        found.ShouldNotBeNull().Id.ShouldBe(hashRow.Id);
    }

    [Fact]
    public async Task FindInviteByTokenAsync_finds_nothing_for_an_unknown_or_over_long_token()
    {
        await SeedHouseholdAsync(1);
        await using var dbContext = OpenFreshDbContext();
        var repository = new HouseholdRepository(dbContext, TimeProvider.System);

        (await repository.FindInviteByTokenAsync(InviteToken.Generate(), TestContext.Current.CancellationToken)).ShouldBeNull();
        (await repository.FindInviteByTokenAsync(new string('a', InviteToken.MaxLength + 1), TestContext.Current.CancellationToken)).ShouldBeNull();
        (await repository.FindInviteByTokenAsync("", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    // AC #12: the NULL-unique-index trap. A plain unique index on SQL Server treats NULLs as equal, so a
    // second hash-only invite (Token NULL) would fail without the filtered index EF/the migration adds.
    [Fact]
    public async Task Two_hash_only_invites_with_a_null_Token_can_coexist()
    {
        var (household, _) = await SeedHouseholdAsync(1);

        await AddAsync(NewInvite(household.Id), NewInvite(household.Id));

        await using var verify = OpenFreshDbContext();
        (await verify.HouseholdInvites.CountAsync(i => i.HouseholdId == household.Id && i.Token == null, TestContext.Current.CancellationToken)).ShouldBe(2);
    }
}

public class PostgresHouseholdMembershipRepositoryTests : HouseholdMembershipRepositoryTestsBase, IAsyncLifetime
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

    protected override EnergyTrackerDbContext OpenFreshDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseNpgsql(_container.GetConnectionString());
        return new EnergyTrackerDbContext(optionsBuilder.Options, new NoOpAccessor());
    }

    private sealed class NoOpAccessor : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId => null;

        public Guid? HouseholdMemberId => null;
    }
}

public class SqlServerHouseholdMembershipRepositoryTests : HouseholdMembershipRepositoryTestsBase, IAsyncLifetime
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

    protected override EnergyTrackerDbContext OpenFreshDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString());
        return new EnergyTrackerDbContext(optionsBuilder.Options, new NoOpAccessor());
    }

    private sealed class NoOpAccessor : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId => null;

        public Guid? HouseholdMemberId => null;
    }
}
