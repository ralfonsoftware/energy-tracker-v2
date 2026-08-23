using System.Globalization;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Edits a Meter Reading's kWh value for the caller's own Household, recording an audit-trail correction note rather than a silent overwrite (AC #3, NFR8).</summary>
public class EditMeterReading(IMeterReadingRepository readingRepository, IAuditCorrectionRecorder auditCorrectionRecorder, IUnitOfWork unitOfWork)
{
    public async Task<MeterReading> ExecuteAsync(Guid householdId, Guid readingId, decimal kwhValue, int expectedVersion, CancellationToken cancellationToken)
    {
        MeterReadingValidation.ValidateKwhValue(kwhValue);

        // AD-3's query filter already scopes this to the caller's Household transparently — no
        // manual HouseholdId check, that would be exactly the per-handler filtering AD-3 exists
        // to prevent.
        var reading = await readingRepository.FindByIdAsync(readingId, cancellationToken);
        if (reading is null)
        {
            throw new MeterReadingNotFoundException(readingId);
        }

        var oldValue = reading.KwhValue;

        // A no-op save of the same value isn't a correction — skip the write entirely rather than
        // bumping Version for nothing, which would otherwise hand out a spurious 409 to anyone else
        // holding the pre-edit Version for this reading.
        if (oldValue == kwhValue)
        {
            return reading;
        }

        // Both writes happen inside one transaction — if recording the correction fails after the
        // value update already succeeded, the whole transaction rolls back rather than leaving a
        // silently-changed value with no correction note (the exact "silent overwrite" AC #3 exists
        // to prevent).
        return await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var updated = await readingRepository.UpdateKwhValueAsync(readingId, kwhValue, expectedVersion, ct);

            await auditCorrectionRecorder.RecordAsync(
                householdId,
                "MeterReading",
                readingId,
                "KwhValue",
                oldValue.ToString(CultureInfo.InvariantCulture),
                kwhValue.ToString(CultureInfo.InvariantCulture),
                ct);

            return updated;
        }, cancellationToken);
    }
}
