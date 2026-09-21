using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Domain.Calculations;

namespace EnergyTracker.Application;

// AD-6: plain JSON-serializable record — never a delegate/closure payload (must survive
// AzureStorageQueueJobQueue's serialize-to-a-cloud-queue boundary, not just InProcessChannelJobQueue's
// in-memory channel).
public record CorrelateEventPayload(Guid EventId, Guid HouseholdId, DateTimeOffset OccurredAt, string Description);

/// <summary>
/// Computes and persists an Event's rough AI Wattage Plausibility correlation — a background job
/// consumer, never called synchronously from the request path (AC #7 is satisfied by rendering
/// whatever correlation already landed by the time the Events list is next read, not by making
/// Event creation wait on this).
/// </summary>
public class CorrelateEvent(
    IHouseholdRepository householdRepository,
    IMeterReadingRepository readingRepository,
    IEventRepository eventRepository,
    IAiPlausibilityClient aiPlausibilityClient)
{
    public async Task ExecuteAsync(Guid householdId, CorrelateEventPayload payload, CancellationToken cancellationToken)
    {
        var household = await householdRepository.FindByIdAsync(householdId, cancellationToken);

        // AD-8's one exception to the anti-hard-branch rule: this is the ONLY place in the whole
        // feature allowed to check "is AI enabled" (Dev Notes decision #5). A missing Yearly
        // Baseline also means "no meaningful expected-consumption figure exists" — same as
        // GetCurrentStatus's own undefined-Status case — so it short-circuits here too, rather than
        // silently comparing against a fabricated 0 baseline that would flag any positive
        // consumption as a "Bump".
        if (household is null || !household.AiPlausibilityEnabled || household.YearlyBaselineKwh is not { } yearlyBaselineKwh)
        {
            return;
        }

        var mainMeter = await readingRepository.FindMainMeterByHouseholdAsync(householdId, cancellationToken);
        if (mainMeter is null)
        {
            return;
        }

        var windowStart = payload.OccurredAt - WindowedDeviationCalculator.WindowRadius;
        var windowEnd = payload.OccurredAt + WindowedDeviationCalculator.WindowRadius;
        var readingsInWindow = await readingRepository.GetInWindowByMainMeterAsync(mainMeter.Id, windowStart, windowEnd, cancellationToken);

        var deviation = WindowedDeviationCalculator.ComputeDeviation(readingsInWindow, yearlyBaselineKwh, household.TrendingThresholdKwh);
        if (deviation is null)
        {
            // AC #3: no observable deviation — leave the correlation null, never flagged as wrong.
            return;
        }

        // IAiPlausibilityClient.ClassifyAsync's own contract guarantees it never throws (NFR14) —
        // no try/catch needed here; a flaky/misconfigured backend already resolves to null exactly
        // like "no plausible match" would.
        var direction = await aiPlausibilityClient.ClassifyAsync(payload.Description, [deviation.Value], cancellationToken);
        if (direction is null)
        {
            return;
        }

        await eventRepository.SetCorrelationAsync(payload.EventId, direction.Value.ToString(), DateTimeOffset.UtcNow, cancellationToken);
    }
}
