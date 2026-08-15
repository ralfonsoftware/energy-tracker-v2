using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Api.Endpoints;

public static class MeterReadingEndpoints
{
    private const string NoHouseholdDetail = "The authenticated principal does not belong to a Household.";

    // Same shape as TaggingScaffoldEndpoints.TryGetHouseholdId — copied rather than referenced
    // across files (that helper is private to its own class); a MeterReading is Household-scoped
    // exactly like Room/PowerPoint/Device, not tenant-root like Household itself.
    private static bool TryGetHouseholdId(ICurrentHouseholdAccessor householdAccessor, out Guid householdId, out IResult? forbidden)
    {
        if (householdAccessor.HouseholdId is { } id)
        {
            householdId = id;
            forbidden = null;
            return true;
        }

        householdId = default;
        forbidden = Results.Problem(detail: NoHouseholdDetail, statusCode: StatusCodes.Status403Forbidden);
        return false;
    }

    public static RouteGroupBuilder MapMeterReadingEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/meter-readings", async (
            CreateMeterReadingRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            CreateMeterReading createMeterReading,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

            try
            {
                var reading = await createMeterReading.ExecuteAsync(
                    householdId, request.KwhValue, request.ReadingTimestamp, request.IdempotencyKey, cancellationToken);
                // Same response on a fresh insert and an idempotent no-op replay (AD-16) — the
                // client doesn't need to distinguish the two.
                return Results.Ok(ToResponse(reading));
            }
            catch (MeterReadingValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        return api;
    }

    private static MeterReadingResponse ToResponse(MeterReading reading) =>
        new(reading.Id, reading.KwhValue, reading.ReadingTimestamp);
}

public record CreateMeterReadingRequest(decimal KwhValue, DateTimeOffset ReadingTimestamp, Guid IdempotencyKey);

public record MeterReadingResponse(Guid Id, decimal KwhValue, DateTimeOffset ReadingTimestamp);
