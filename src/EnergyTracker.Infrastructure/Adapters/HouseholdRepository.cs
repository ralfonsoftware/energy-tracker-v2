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
}
