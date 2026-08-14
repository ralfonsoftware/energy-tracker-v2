using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

public class HouseholdRepository(EnergyTrackerDbContext dbContext) : IHouseholdRepository
{
    public Task<HouseholdMember?> FindMemberAsync(string externalIssuer, string externalSubjectId, CancellationToken cancellationToken) =>
        dbContext.HouseholdMembers.SingleOrDefaultAsync(
            m => m.ExternalIssuer == externalIssuer && m.ExternalSubjectId == externalSubjectId,
            cancellationToken);

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

    public Task<HouseholdInvite?> FindInviteByTokenAsync(string token, CancellationToken cancellationToken) =>
        dbContext.HouseholdInvites.SingleOrDefaultAsync(i => i.Token == token, cancellationToken);

    public async Task<Household> AcceptInviteAsync(HouseholdInvite invite, HouseholdMember newMember, CancellationToken cancellationToken)
    {
        invite.ConsumedAtUtc = DateTimeOffset.UtcNow;
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
            throw new HouseholdInviteExpiredOrConsumedException(invite.Token);
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
}
