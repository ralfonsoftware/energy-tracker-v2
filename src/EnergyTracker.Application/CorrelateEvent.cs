using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Domain.Calculations;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Application;

// AD-6: plain JSON-serializable record — never a delegate/closure payload (must survive
// AzureStorageQueueJobQueue's serialize-to-a-cloud-queue boundary, not just InProcessChannelJobQueue's
// in-memory channel).
public record CorrelateEventPayload(Guid EventId, Guid HouseholdId, DateTimeOffset OccurredAt, string Description);

/// <summary>
/// Evaluates and persists an Event's rough AI Wattage Plausibility correlation — an idempotent
/// background job consumer (latest evaluation wins), re-run whenever Meter Readings in the Event's
/// window change (Story 10.2). Never called synchronously from the request path (Story 6.3 AC #7).
/// Calls the AI backend only when the observed deviation direction differs from the persisted one.
/// A persisted correlation is retracted only when no deviation is observed any more, never because
/// the AI backend answered null (that may be a transient failure).
/// </summary>
public class CorrelateEvent(
    IHouseholdRepository householdRepository,
    IMeterReadingRepository readingRepository,
    IMeterRegressionPromptRepository regressionPromptRepository,
    IEventRepository eventRepository,
    IAiPlausibilityClient aiPlausibilityClient,
    ILogger<CorrelateEvent> logger)
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

        var @event = await eventRepository.FindByIdAsync(payload.EventId, cancellationToken);
        if (@event is null)
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

        // AD-12: an open MeterRegressionPrompt excludes its triggering reading and everything
        // chronologically at or after it. The trigger is looked up by id because a ±7-day slice
        // usually does not contain it.
        var openPrompt = await regressionPromptRepository.GetOpenForHouseholdAsync(householdId, cancellationToken);
        MeterReading? openPromptTrigger = null;
        if (openPrompt is not null)
        {
            openPromptTrigger = await readingRepository.FindByIdAsync(openPrompt.MeterReadingId, cancellationToken);
            if (openPromptTrigger is null)
            {
                // Without the trigger the AD-12 exclusion cannot be applied, so evaluating would
                // risk a deviation computed from readings the prompt excludes. Skip rather than
                // fail (and retry) the job; the correlation stays as it was.
                logger.LogWarning(
                    "Open MeterRegressionPrompt's triggering reading {MeterReadingId} was not found; skipping correlation for Event {EventId}.",
                    openPrompt.MeterReadingId, payload.EventId);
                return;
            }
        }

        var includedReadings = WindowedDeviationCalculator.ExcludeAtOrAfter(
            readingsInWindow.OrderBy(r => r.ReadingTimestamp).ThenBy(r => r.Id).ToList(), openPromptTrigger);

        // Same resolved-prompt correction GetCurrentStatus feeds PatternDetectiveCalculator.ComputePaceToDate
        // — a rollover/reset that landed inside this window must not poison the raw delta below.
        var resolvedPrompts = await regressionPromptRepository.GetResolvedForMainMeterAsync(mainMeter.Id, cancellationToken);
        var resolvedPromptsByTriggeringReadingId = resolvedPrompts.ToDictionary(p => p.MeterReadingId);

        var deviation = WindowedDeviationCalculator.ComputeDeviation(
            includedReadings, yearlyBaselineKwh, household.TrendingThresholdKwh, resolvedPromptsByTriggeringReadingId);

        var persistedDirection = @event.CorrelationDirection;

        if (deviation is null)
        {
            // Story 6.3 AC #3: no observable deviation — shown without a correlation, never flagged
            // as wrong. Latest evaluation wins, so a previously persisted correlation is retracted.
            await ClearIfPersistedAsync(@event, cancellationToken);
            return;
        }

        if (Enum.TryParse<AiPlausibilityDirection>(persistedDirection, ignoreCase: true, out var persisted)
            && Enum.IsDefined(persisted)
            && persisted == deviation.Value)
        {
            // AI-cost guard: nothing changed since the last evaluation — zero calls, zero writes.
            return;
        }

        // IAiPlausibilityClient.ClassifyAsync's own contract guarantees it never throws (NFR14) —
        // no try/catch needed here; a flaky/misconfigured backend already resolves to null exactly
        // like "no plausible match" would.
        var direction = await aiPlausibilityClient.ClassifyAsync(payload.Description, [deviation.Value], cancellationToken);
        if (direction is null)
        {
            // The client resolves failures (timeouts, rate limits) to null exactly like "no
            // plausible match", so a null answer cannot be told apart from a transient error:
            // keep whatever is persisted. Only a vanished deviation (above) retracts a correlation.
            return;
        }

        await eventRepository.SetCorrelationAsync(payload.EventId, direction.Value.ToString(), DateTimeOffset.UtcNow, cancellationToken);
    }

    private async Task ClearIfPersistedAsync(Event @event, CancellationToken cancellationToken)
    {
        if (@event.CorrelationDirection is null && @event.CorrelationComputedAtUtc is null)
        {
            return;
        }

        await eventRepository.SetCorrelationAsync(@event.Id, null, null, cancellationToken);
    }
}
