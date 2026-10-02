using EnergyTracker.Application.Ports;
using EnergyTracker.Domain.Calculations;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Application;

/// <summary>Re-enqueues a CorrelateEvent job for every Event in an OccurredAt range of an AI-enabled Household, so an Event's correlation is re-evaluated when Meter Readings around it change (Story 10.2, AC #1-#5, #8, #9).</summary>
public class RequeueEventCorrelations(
    IHouseholdRepository householdRepository,
    IEventRepository eventRepository,
    IMeterReadingRepository readingRepository,
    IBackgroundJobQueue jobQueue,
    ILogger<RequeueEventCorrelations> logger)
{
    // Same clock-skew allowance as CreateEvent/CreateMeterReading: an Event may be stamped slightly ahead of the server clock.
    private static readonly TimeSpan MaxFutureClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Re-evaluates every Event whose ±7-day window reaches <paramref name="timestamp"/> or later —
    /// used when an open/resolved MeterRegressionPrompt changes what AD-12 excludes at and after its
    /// triggering reading, so the affected Events can lie wholly after the +7-day edge.
    /// </summary>
    public Task ExecuteFromAsync(Guid householdId, DateTimeOffset timestamp, CancellationToken cancellationToken) =>
        ExecuteAsync(
            householdId,
            timestamp - WindowedDeviationCalculator.WindowRadius,
            DateTimeOffset.UtcNow + MaxFutureClockSkew,
            cancellationToken);

    /// <summary>Same as <see cref="ExecuteFromAsync(Guid, DateTimeOffset, CancellationToken)"/>, keyed by the triggering reading's id; the lookup is guarded like the rest of the requeue (AC #9).</summary>
    public async Task ExecuteFromReadingAsync(Guid householdId, Guid triggerReadingId, CancellationToken cancellationToken)
    {
        try
        {
            var trigger = await readingRepository.FindByIdAsync(triggerReadingId, cancellationToken);
            if (trigger is null)
            {
                logger.LogWarning(
                    "Triggering Meter Reading {MeterReadingId} not found; Event correlations for Household {HouseholdId} were not requeued.",
                    triggerReadingId, householdId);
                return;
            }

            await ExecuteFromAsync(householdId, trigger.ReadingTimestamp, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Failed to requeue Event correlations after triggering Meter Reading {MeterReadingId} for Household {HouseholdId}.",
                triggerReadingId, householdId);
        }
    }

    public async Task ExecuteAsync(
        Guid householdId, DateTimeOffset occurredFrom, DateTimeOffset occurredTo, CancellationToken cancellationToken)
    {
        // The reading write that called us is already committed, so nothing here may fail that
        // request (same discipline as CreateEvent's enqueue); a lost requeue only means the
        // correlation stays as it was until the next reading in the window changes it.
        try
        {
            // Documented AD-8 exception (Story 10.2 Ask First #3, Ralf 2026-10-02): unlike
            // CreateEvent, this trigger skips AI-off Households so no pointless job rows accumulate.
            // CorrelateEvent still performs its own enablement check, and remains the only place
            // that decides what happens to a persisted correlation.
            var household = await householdRepository.FindByIdAsync(householdId, cancellationToken);
            if (household is null || !household.AiPlausibilityEnabled)
            {
                return;
            }

            var events = await eventRepository.GetByOccurredAtRangeAsync(occurredFrom, occurredTo, cancellationToken);
            foreach (var @event in events)
            {
                try
                {
                    await jobQueue.EnqueueAsync(
                        new JobEnvelope<CorrelateEventPayload>(
                            Guid.NewGuid(), householdId, JobTypes.CorrelateEvent,
                            new CorrelateEventPayload(@event.Id, householdId, @event.OccurredAt, @event.Description)),
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Failed to enqueue CorrelateEvent job for Event {EventId}.", @event.Id);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Failed to requeue Event correlations for Household {HouseholdId} in [{From}, {To}].",
                householdId, occurredFrom, occurredTo);
        }
    }
}
