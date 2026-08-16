using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Api.Endpoints;

public static class MeterRegressionPromptEndpoints
{
    private const string NoHouseholdDetail = "The authenticated principal does not belong to a Household.";

    // Same shape as MeterReadingEndpoints.TryGetHouseholdId — copied rather than referenced
    // across files (that helper is private to its own class); a MeterRegressionPrompt is
    // Household-scoped exactly like MeterReading.
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

    public static RouteGroupBuilder MapMeterRegressionPromptEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/meter-regression-prompts/open", async (
            ICurrentHouseholdAccessor householdAccessor,
            GetOpenMeterRegressionPrompt getOpenMeterRegressionPrompt,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

            var details = await getOpenMeterRegressionPrompt.ExecuteAsync(householdId, cancellationToken);
            // 200 with a null body when nothing's open — this is an "is there one?" poll-style
            // read, not a resource fetch that should 404 when absent.
            return Results.Ok(details is null ? null : ToResponse(details));
        });

        api.MapPost("/meter-regression-prompts/{id:guid}/resolve", async (
            Guid id,
            ResolveMeterRegressionPromptRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            ResolveMeterRegressionPrompt resolveMeterRegressionPrompt,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

            MeterRegressionClassification classification;
            if (string.Equals(request.Classification, "reset", StringComparison.OrdinalIgnoreCase))
            {
                classification = MeterRegressionClassification.Reset;
            }
            else if (string.Equals(request.Classification, "rollover", StringComparison.OrdinalIgnoreCase))
            {
                classification = MeterRegressionClassification.Rollover;
            }
            else
            {
                return Results.Problem(
                    detail: $"Classification must be 'reset' or 'rollover', got '{request.Classification}'.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var prompt = await resolveMeterRegressionPrompt.ExecuteAsync(
                    householdId, id, classification, request.DigitCapacityKwh, cancellationToken);
                return Results.Ok(ToResponse(prompt));
            }
            catch (MeterRegressionValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (MeterRegressionPromptNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (MeterRegressionPromptNotOpenException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        return api;
    }

    private static MeterRegressionPromptResponse ToResponse(OpenMeterRegressionPromptDetails details) => new(
        details.Prompt.Id,
        details.Reading.Id,
        details.Reading.KwhValue,
        details.Reading.ReadingTimestamp,
        details.PreviousReading.Id,
        details.PreviousReading.KwhValue,
        details.PreviousReading.ReadingTimestamp,
        details.MainMeterDigitCapacityKwh);

    private static ResolveMeterRegressionPromptResponse ToResponse(MeterRegressionPrompt prompt) => new(
        prompt.Id,
        prompt.Classification!.Value.ToString().ToLowerInvariant(),
        prompt.ResolvedAtUtc!.Value);
}

public record MeterRegressionPromptResponse(
    Guid Id,
    Guid MeterReadingId,
    decimal ReadingKwhValue,
    DateTimeOffset ReadingTimestamp,
    Guid PreviousMeterReadingId,
    decimal PreviousReadingKwhValue,
    DateTimeOffset PreviousReadingTimestamp,
    decimal? MainMeterDigitCapacityKwh);

public record ResolveMeterRegressionPromptRequest(string Classification, decimal? DigitCapacityKwh);

public record ResolveMeterRegressionPromptResponse(Guid Id, string Classification, DateTimeOffset ResolvedAtUtc);
