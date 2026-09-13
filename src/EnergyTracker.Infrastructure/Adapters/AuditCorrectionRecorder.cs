using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

public class AuditCorrectionRecorder(EnergyTrackerDbContext dbContext) : IAuditCorrectionRecorder
{
    public async Task RecordAsync(Guid householdId, string entityType, Guid entityId, string fieldName, string oldValue, string newValue, CancellationToken cancellationToken)
    {
        await dbContext.AuditCorrections.AddAsync(
            new AuditCorrection
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                EntityType = entityType,
                EntityId = entityId,
                FieldName = fieldName,
                OldValue = oldValue,
                NewValue = newValue,
                CorrectedAtUtc = DateTimeOffset.UtcNow,
            },
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, AuditCorrection>> GetLatestForEntitiesAsync(string entityType, IReadOnlyList<Guid> entityIds, CancellationToken cancellationToken)
    {
        if (entityIds.Count == 0)
        {
            return new Dictionary<Guid, AuditCorrection>();
        }

        // One query for the whole batch (GroupBy + take the max-CorrectedAtUtc row per group,
        // translated server-side), not an N+1 loop over entityIds. Ties on CorrectedAtUtc (e.g. two
        // corrections recorded within the same DB timestamp resolution) are broken on Id, the same
        // tiebreak precedent FindImmediatelyPrecedingAsync already establishes for a timestamp tie.
        var latestPerEntity = await dbContext.AuditCorrections
            .Where(a => a.EntityType == entityType && entityIds.Contains(a.EntityId))
            .GroupBy(a => a.EntityId)
            .Select(g => g.OrderByDescending(a => a.CorrectedAtUtc).ThenByDescending(a => a.Id).First())
            .ToListAsync(cancellationToken);

        return latestPerEntity.ToDictionary(a => a.EntityId, a => a);
    }

    public async Task<IReadOnlyDictionary<(Guid EntityId, string FieldName), AuditCorrection>> GetLatestPerFieldForEntitiesAsync(
        string entityType, IReadOnlyList<Guid> entityIds, CancellationToken cancellationToken)
    {
        if (entityIds.Count == 0)
        {
            return new Dictionary<(Guid, string), AuditCorrection>();
        }

        // Same GroupBy-and-take-max-CorrectedAtUtc shape as GetLatestForEntitiesAsync, grouped by
        // (EntityId, FieldName) instead of EntityId alone — so two fields corrected in the same
        // submission each keep their own visible correction note (AC #6).
        var latestPerField = await dbContext.AuditCorrections
            .Where(a => a.EntityType == entityType && entityIds.Contains(a.EntityId))
            .GroupBy(a => new { a.EntityId, a.FieldName })
            .Select(g => g.OrderByDescending(a => a.CorrectedAtUtc).ThenByDescending(a => a.Id).First())
            .ToListAsync(cancellationToken);

        return latestPerField.ToDictionary(a => (a.EntityId, a.FieldName), a => a);
    }
}
