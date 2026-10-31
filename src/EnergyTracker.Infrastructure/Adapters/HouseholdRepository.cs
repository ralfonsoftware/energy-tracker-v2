using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

public class HouseholdRepository(EnergyTrackerDbContext dbContext, TimeProvider timeProvider) : IHouseholdRepository
{
    public Task<HouseholdMember?> FindMemberAsync(string externalIssuer, string externalSubjectId, CancellationToken cancellationToken) =>
        dbContext.HouseholdMembers.SingleOrDefaultAsync(
            m => m.ExternalIssuer == externalIssuer && m.ExternalSubjectId == externalSubjectId,
            cancellationToken);

    public Task<Household?> FindByIdAsync(Guid householdId, CancellationToken cancellationToken) =>
        dbContext.Households.SingleOrDefaultAsync(h => h.Id == householdId, cancellationToken);

    public async Task AddAsync(Household household, HouseholdMember creator, CancellationToken cancellationToken)
    {
        await dbContext.Households.AddAsync(household, cancellationToken);
        await dbContext.HouseholdMembers.AddAsync(creator, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // CreateHousehold's own FindMemberAsync check and this insert aren't atomic — a
            // concurrent duplicate submission from the same principal can both pass the check
            // before either commits. The unique (ExternalIssuer, ExternalSubjectId) index still
            // guarantees no duplicate row is created; translate the resulting constraint
            // violation into the same conflict signal the pre-check path produces, instead of
            // letting a raw DbUpdateException surface as an unhandled 500.
            var existingMember = await FindMemberAsync(creator.ExternalIssuer, creator.ExternalSubjectId, cancellationToken);
            if (existingMember is not null)
            {
                throw new HouseholdAlreadyExistsException(existingMember.HouseholdId);
            }

            throw;
        }
    }

    public async Task AddInviteAsync(HouseholdInvite invite, CancellationToken cancellationToken)
    {
        await dbContext.HouseholdInvites.AddAsync(invite, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<HouseholdInvite?> FindInviteByTokenAsync(string token, CancellationToken cancellationToken)
    {
        // Same answer as an unknown token, before the value becomes a database parameter (AC #11c).
        if (!InviteToken.IsPlausible(token))
        {
            return null;
        }

        // TokenHash has a non-unique index, hence FirstOrDefault.
        var hash = InviteToken.Hash(token);
        return await dbContext.HouseholdInvites.FirstOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);
    }

    // AD-26 / AD-3: HouseholdMember has no query filter, so this is THE by-id member lookup and it
    // carries the HouseholdId predicate itself. Guarded by HouseholdMemberLookupsAreHouseholdScopedTests.
    public Task<HouseholdMember?> FindMemberInHouseholdAsync(Guid householdId, Guid memberId, CancellationToken cancellationToken) =>
        dbContext.HouseholdMembers.SingleOrDefaultAsync(m => m.HouseholdId == householdId && m.Id == memberId, cancellationToken);

    public async Task<IReadOnlyList<HouseholdMember>> ListMembersAsync(Guid householdId, CancellationToken cancellationToken) =>
        await dbContext.HouseholdMembers
            .AsNoTracking()
            .Where(m => m.HouseholdId == householdId)
            .ToListAsync(cancellationToken);

    // HouseholdInvite is an AD-3 query-filter exception too, so the Household predicate is explicit.
    public async Task<IReadOnlyList<HouseholdInvite>> ListOpenInvitesAsync(Guid householdId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        await dbContext.HouseholdInvites
            .AsNoTracking()
            .Where(i => i.HouseholdId == householdId && i.ConsumedAtUtc == null && i.ExpiresAtUtc > nowUtc)
            .ToListAsync(cancellationToken);

    public async Task RemoveMemberAsync(Guid householdId, Guid memberId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var household = await dbContext.Households.SingleAsync(h => h.Id == householdId, cancellationToken);
        var member = await FindMemberInHouseholdAsync(householdId, memberId, cancellationToken)
            ?? throw new HouseholdMemberNotFoundException(memberId);

        // The count is the friendly error; the Household.Version token below is the actual race guard
        // (a read before a write cannot be): two concurrent removals both bump the same Household row,
        // so the second UPDATE ... WHERE Version = @old affects zero rows and throws.
        var memberCount = await dbContext.HouseholdMembers.CountAsync(m => m.HouseholdId == householdId, cancellationToken);
        if (memberCount <= 1)
        {
            throw new LastHouseholdMemberException(householdId);
        }

        dbContext.HouseholdMembers.Remove(member);

        // A removed person must not be able to come back through a link someone else still holds.
        var openInvites = await dbContext.HouseholdInvites
            .Where(i => i.HouseholdId == householdId && i.ConsumedAtUtc == null && i.ExpiresAtUtc > nowUtc)
            .ToListAsync(cancellationToken);
        foreach (var invite in openInvites)
        {
            invite.ExpiresAtUtc = nowUtc;
            invite.Version++;
        }

        household.Version++;

        try
        {
            // One SaveChanges: one implicit transaction on both providers.
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Third place allowed to know this type (see the AD-1 trap note on AcceptInviteAsync).
            throw new HouseholdConcurrencyConflictException(householdId);
        }
    }

    public async Task RevokeInviteAsync(Guid householdId, Guid inviteId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var invite = await dbContext.HouseholdInvites.SingleOrDefaultAsync(
            i => i.HouseholdId == householdId && i.Id == inviteId && i.ConsumedAtUtc == null && i.ExpiresAtUtc > nowUtc,
            cancellationToken)
            ?? throw new HouseholdInviteNotOpenException();

        invite.ExpiresAtUtc = nowUtc;
        invite.Version++;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent accept or removal got there first; the outcome the member wants (the link is
            // dead) holds either way.
            throw new HouseholdInviteNotOpenException();
        }
    }

    public async Task<Household> AcceptInviteAsync(HouseholdInvite invite, HouseholdMember newMember, CancellationToken cancellationToken)
    {
        invite.ConsumedAtUtc = timeProvider.GetUtcNow();
        // AD-4 requires the concurrency token to change on every update — EF only detects a
        // conflict when the stored value has moved since it was read, so a plain int token that
        // is never bumped never actually guards anything.
        invite.Version++;
        await dbContext.HouseholdMembers.AddAsync(newMember, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A second, concurrent accept of the same single-use invite lost the race —
            // Version's mismatch is AD-4's mechanism doing its job. Translate to the
            // Application-level exception; this is the one place in the whole story allowed to
            // know about DbUpdateConcurrencyException (see AD-1 trap note on AcceptHouseholdInvite).
            throw new HouseholdInviteExpiredOrConsumedException();
        }
        catch (DbUpdateException)
        {
            // Same non-atomic check-then-insert race AddAsync already handles: the same
            // principal concurrently accepted a *different* invite and both passed the
            // FindMemberAsync pre-check before either committed. The unique
            // (ExternalIssuer, ExternalSubjectId) index still guarantees no duplicate row, so
            // translate the constraint violation into the same conflict signal the pre-check
            // path produces instead of letting it surface as an unhandled 500.
            var existingMember = await FindMemberAsync(newMember.ExternalIssuer, newMember.ExternalSubjectId, cancellationToken);
            if (existingMember is not null)
            {
                throw new HouseholdAlreadyExistsException(existingMember.HouseholdId);
            }

            throw;
        }

        // Household has no AD-3 filter either — it's the tenant root — so this is safe
        // regardless of the calling principal's own resolved household state.
        return await dbContext.Households.SingleAsync(h => h.Id == invite.HouseholdId, cancellationToken);
    }

    public async Task<Household> UpdateYearlyBaselineAsync(Guid householdId, decimal yearlyBaselineKwh, int expectedVersion, CancellationToken cancellationToken)
    {
        var household = await dbContext.Households.SingleAsync(h => h.Id == householdId, cancellationToken);

        // Makes EF's SaveChangesAsync compare expectedVersion (the caller's known value) against
        // the DB, not whatever the freshly-loaded entity already has.
        dbContext.Entry(household).Property(h => h.Version).OriginalValue = expectedVersion;

        household.YearlyBaselineKwh = yearlyBaselineKwh;
        // AD-4 requires the concurrency token to change on every update — see AcceptInviteAsync's
        // invite.Version++ above for the identical reasoning.
        household.Version++;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Second place in the codebase allowed to know about DbUpdateConcurrencyException —
            // see the AD-1 trap-note precedent next to AcceptInviteAsync's catch block above.
            throw new HouseholdConcurrencyConflictException(householdId);
        }

        return household;
    }

    public async Task<Household> UpdateLocaleAsync(Guid householdId, string locale, CancellationToken cancellationToken)
    {
        var household = await dbContext.Households.SingleAsync(h => h.Id == householdId, cancellationToken);

        // Deliberately no expectedVersion / Version++ (Story 8.11): EF writes only the modified
        // Locale column and the loaded Version stays in the UPDATE's WHERE, so a concurrent
        // baseline edit still detects its own real conflict.
        household.Locale = locale;
        await dbContext.SaveChangesAsync(cancellationToken);

        return household;
    }

    public async Task<Household> UpdateAiPlausibilityEnabledAsync(Guid householdId, bool enabled, int expectedVersion, CancellationToken cancellationToken)
    {
        var household = await dbContext.Households.SingleAsync(h => h.Id == householdId, cancellationToken);

        // Same OriginalValue-pinning technique as UpdateYearlyBaselineAsync above — compares
        // expectedVersion (the caller's known value) against the DB, not whatever this
        // freshly-loaded entity already has.
        dbContext.Entry(household).Property(h => h.Version).OriginalValue = expectedVersion;

        household.AiPlausibilityEnabled = enabled;
        household.Version++;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new HouseholdConcurrencyConflictException(householdId);
        }

        return household;
    }
}
