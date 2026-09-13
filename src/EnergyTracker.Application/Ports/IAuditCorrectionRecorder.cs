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

    // Additive — never replaces GetLatestForEntitiesAsync above, whose single-row-per-entity
    // contract GetMeterReadingHistory/MeterReadingEndpoints still depend on unchanged. This is for
    // an entity with more than one independently-correctable field (Tariff has five) where
    // GetLatestForEntitiesAsync would silently surface only one of several same-submission
    // corrections (AC #6, Story 5.1's Task 4). Latest correction per (EntityId, FieldName) pair.
    Task<IReadOnlyDictionary<(Guid EntityId, string FieldName), AuditCorrection>> GetLatestPerFieldForEntitiesAsync(
        string entityType, IReadOnlyList<Guid> entityIds, CancellationToken cancellationToken);
}
