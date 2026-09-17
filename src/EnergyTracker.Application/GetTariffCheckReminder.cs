using EnergyTracker.Application.Ports;

namespace EnergyTracker.Application;

public record TariffCheckReminderResult(bool IsDue, DateTimeOffset GateOpensAtUtc);

/// <summary>Computes whether the caller's Household is due for a Tariff Check live, synchronously, at request time — undefined (null) with no current Tariff configured (AC #3; AD-7, FR-15).</summary>
public class GetTariffCheckReminder(ITariffRepository tariffRepository)
{
    public async Task<TariffCheckReminderResult?> ExecuteAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var currentTariff = await tariffRepository.FindCurrentForHouseholdAsync(householdId, cancellationToken);
        if (currentTariff is null)
        {
            return null;
        }

        // AC #4: ContractPeriodMonths is a MINIMUM term, not a hard end date — this is computed
        // fresh from the current Tariff's own two fields on every call, never a stored/cached
        // schedule, so it needs no concept of an actual contract end, auto-renewal, or rolling
        // continuation to "stay open." AC #5 (recompute on edit) falls out of this for free:
        // EditTariff writes new values onto this same row, and this method always re-reads them
        // live — there is no separate schedule that could go stale.
        var minimumTermEndUtc = currentTariff.ContractStartDate.AddMonths(currentTariff.ContractPeriodMonths);
        var gateOpensAtUtc = minimumTermEndUtc.AddMonths(-3);

        // AC #1/#4, resolved ambiguity #1 (Dev Notes): monotonic, not cyclic, for a given Tariff's
        // own fields — TariffCheckCadenceMonths is deliberately NOT read here. Because this is a
        // live recompute (never a stored schedule), editing ContractStartDate/ContractPeriodMonths
        // on the current Tariff can move gateOpensAtUtc forward and flip IsDue back to false —
        // that's AC #5's "recompute against the new dates going forward", not a violation of
        // monotonicity: time alone never closes the gate once it has opened for a fixed Tariff.
        var isDue = DateTimeOffset.UtcNow >= gateOpensAtUtc;

        return new TariffCheckReminderResult(IsDue: isDue, GateOpensAtUtc: gateOpensAtUtc);
    }
}
