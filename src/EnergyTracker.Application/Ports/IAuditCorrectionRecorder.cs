using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface IAuditCorrectionRecorder
{
    Task RecordAsync(Guid householdId, string entityType, Guid entityId, string fieldName, string oldValue, string newValue, CancellationToken cancellationToken);

    // Latest correction per entity id (greatest CorrectedAtUtc), keyed by EntityId. A row
    // corrected more than once accumulates multiple AuditCorrection rows — full history is
    // preserved in the table — but only the most recent is surfaced as the visible "corrected
    // from X" note (NFR8); no AC in this story requires a full audit-log view.
    Task<IReadOnlyDictionary<Guid, AuditCorrection>> GetLatestForEntitiesAsync(string entityType, IReadOnlyList<Guid> entityIds, CancellationToken cancellationToken);
}
