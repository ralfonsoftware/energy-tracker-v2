using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Api.Endpoints;

public static class JobEndpoints
{
    public static RouteGroupBuilder MapJobEndpoints(this RouteGroupBuilder api)
    {
        var household = api.MapGroup(string.Empty).RequireHousehold();
        household.MapGet("/jobs/{id:guid}", async (
            Guid id,
            ICurrentHouseholdAccessor householdAccessor,
            GetBackgroundJobStatus getBackgroundJobStatus,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

            var result = await getBackgroundJobStatus.ExecuteAsync(householdId, id, cancellationToken);
            // A job belonging to a different Household must 404, not leak existence (AD-3, mirrors
            // the existing IDOR-guard pattern for Room/PowerPoint/Device).
            if (result is null)
            {
                return Results.Problem(detail: $"No job '{id}' found.", statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Ok(ToResponse(result));
        });

        return api;
    }

    private static JobStatusResponse ToResponse(BackgroundJobStatusResult result) => new(
        result.Job.Id,
        result.Job.Status.ToString().ToLowerInvariant(),
        result.SmartPlugImportStatus?.ToString().ToLowerInvariant(),
        result.Job.ErrorMessage,
        result.Job.CreatedAtUtc,
        result.Job.CompletedAtUtc,
        result.SmartPlugImportId,
        result.SmartPlugImportDeviceTag,
        result.SmartPlugImportGaps.Select(ToGapDto).ToList());

    private static SmartPlugImportGapDto ToGapDto(SmartPlugImportGap gap) => new(
        gap.StartDate, gap.EndDate, gap.Treatment.ToString().ToLowerInvariant(), gap.EstimatedTotalKwh);
}

public record JobStatusResponse(
    Guid Id, string Status, string? ImportStatus, string? ErrorMessage, DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc,
    Guid? SmartPlugImportId, string? SmartPlugImportDeviceTag, IReadOnlyList<SmartPlugImportGapDto> Gaps);

public record SmartPlugImportGapDto(DateOnly StartDate, DateOnly EndDate, string Treatment, decimal? EstimatedTotalKwh);
