using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Api.Endpoints;

public static class StatusEndpoints
{
    public static RouteGroupBuilder MapStatusEndpoints(this RouteGroupBuilder api)
    {
        var household = api.MapGroup(string.Empty).RequireHousehold();
        // Singleton resource, not a collection — /api/status, not plural (consistent with
        // /api/session, /health). Per Consistency Conventions: only the current Status value and
        // its supporting figures — drill-down data (Trend History, FR-8) is always a separate
        // endpoint, never merged in here.
        household.MapGet("/status", async (
            ICurrentHouseholdAccessor householdAccessor,
            GetCurrentStatus getCurrentStatus,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

            var result = await getCurrentStatus.ExecuteAsync(householdId, cancellationToken);
            // 200 with a null body when undefined (AC #6) — same "is there one?" shape as
            // GET /api/meter-regression-prompts/open, not a 404/204. Story 2.5's onboarding empty
            // state is driven by this null, not by an error response.
            return Results.Ok(result is null ? null : ToResponse(result));
        });

        // Sub-resource drill-down of the singleton /api/status (AC #6, Story 2.7): the aggregate
        // figures behind the headline, never a Meter Reading list (AC #2). Reuses the exact same
        // GetCurrentStatus.ExecuteAsync call as /status above — AD-7's single live-computation
        // seam, not a second calculation path.
        household.MapGet("/status/detail", async (
            ICurrentHouseholdAccessor householdAccessor,
            GetCurrentStatus getCurrentStatus,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

            var result = await getCurrentStatus.ExecuteAsync(householdId, cancellationToken);
            return Results.Ok(result is null ? null : ToDetailResponse(result));
        });

        // Second drill-down of the singleton /api/status (AC #4, #5, Story 4.1): the full
        // StatusSnapshot lifetime for the Trend History chart — always persisted history, never a
        // live recomputation (AD-7). Unlike /status and /status/detail above, "no history yet" is
        // legitimately an empty array, not an undefined single value, so this always returns 200
        // with a (possibly empty) array, never a null body.
        household.MapGet("/status/history", async (
            ICurrentHouseholdAccessor householdAccessor,
            GetStatusHistory getStatusHistory,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

            var entries = await getStatusHistory.ExecuteAsync(householdId, cancellationToken);
            return Results.Ok(entries.Select(ToHistoryEntryResponse).ToList());
        });

        return api;
    }

    private static StatusResponse ToResponse(CurrentStatusResult result) => new(
        ToStatusString(result.Status),
        result.PaceToDateKwh,
        result.BaselineToDateKwh,
        result.IsLowConfidence);

    private static StatusDetailResponse ToDetailResponse(CurrentStatusResult result) => new(
        Status: ToStatusString(result.Status),
        PaceToDateKwh: result.PaceToDateKwh,
        BaselineToDateKwh: result.BaselineToDateKwh,
        ElapsedDays: result.ElapsedDays,
        TrendingThresholdKwh: result.TrendingThresholdKwh,
        TrendingThresholdToDateKwh: result.TrendingThresholdToDateKwh,
        IsLowConfidence: result.IsLowConfidence,
        DaysSinceLastReading: result.DaysSinceLastReading,
        LowConfidenceGapDaysThreshold: result.LowConfidenceGapDaysThreshold);

    private static StatusHistoryEntryResponse ToHistoryEntryResponse(StatusHistoryEntry entry) => new(
        Status: ToStatusString(entry.Status),
        PaceToDateKwh: entry.PaceToDateKwh,
        BaselineToDateKwh: entry.BaselineToDateKwh,
        IsLowConfidence: entry.IsLowConfidence,
        ComputedAtUtc: entry.ComputedAtUtc,
        GapBeforeThisEntry: entry.GapBeforeThisEntry);

    private static string ToStatusString(Status status) => status switch
    {
        Status.WithinRange => "withinRange",
        Status.BelowBaseline => "belowBaseline",
        Status.Trending => "trending",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

public record StatusResponse(string Status, decimal PaceToDateKwh, decimal BaselineToDateKwh, bool IsLowConfidence);

public record StatusDetailResponse(
    string Status,
    decimal PaceToDateKwh,
    decimal BaselineToDateKwh,
    double ElapsedDays,
    decimal TrendingThresholdKwh,
    decimal TrendingThresholdToDateKwh,
    bool IsLowConfidence,
    double DaysSinceLastReading,
    int LowConfidenceGapDaysThreshold);

public record StatusHistoryEntryResponse(
    string Status,
    decimal PaceToDateKwh,
    decimal BaselineToDateKwh,
    bool IsLowConfidence,
    DateTimeOffset ComputedAtUtc,
    bool GapBeforeThisEntry);
