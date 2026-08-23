using System.Globalization;
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

        api.MapGet("/meter-readings", async (
            ICurrentHouseholdAccessor householdAccessor,
            GetMeterReadingHistory getMeterReadingHistory,
            CancellationToken cancellationToken,
            int page = 1,
            int pageSize = 20) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

            try
            {
                var result = await getMeterReadingHistory.ExecuteAsync(householdId, page, pageSize, cancellationToken);
                return Results.Ok(ToHistoryPageResponse(result));
            }
            catch (MeterReadingValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        api.MapPut("/meter-readings/{id:guid}", async (
            Guid id,
            EditMeterReadingRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            EditMeterReading editMeterReading,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

            try
            {
                var reading = await editMeterReading.ExecuteAsync(householdId, id, request.KwhValue, request.Version, cancellationToken);
                return Results.Ok(ToResponse(reading));
            }
            catch (MeterReadingValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (MeterReadingNotFoundException)
            {
                return Results.NotFound();
            }
            catch (MeterReadingConcurrencyConflictException ex)
            {
                // Message only, not the full current server state — matches HouseholdEndpoints'
                // established 409 precedent; the frontend's own refetch covers getting the
                // current value.
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        return api;
    }

    private static MeterReadingResponse ToResponse(MeterReading reading) =>
        new(reading.Id, reading.KwhValue, reading.ReadingTimestamp, reading.Version);

    private static MeterReadingHistoryPageResponse ToHistoryPageResponse(MeterReadingHistoryPage page) =>
        new(
            Items: page.Items.Select(ToHistoryItemResponse).ToList(),
            TotalCount: page.TotalCount,
            Page: page.Page,
            PageSize: page.PageSize);

    private static MeterReadingHistoryItemResponse ToHistoryItemResponse(MeterReadingHistoryEntry entry) =>
        new(
            Id: entry.Reading.Id,
            KwhValue: entry.Reading.KwhValue,
            ReadingTimestamp: entry.Reading.ReadingTimestamp,
            Version: entry.Reading.Version,
            IsPendingRegression: entry.IsPendingRegression,
            // Inverse of EditMeterReading's OldValue.ToString(CultureInfo.InvariantCulture) — the
            // stored value is always locale-neutral (AD-18), never the ambient/household locale.
            CorrectedFromKwhValue: entry.LatestCorrection is null ? null : decimal.Parse(entry.LatestCorrection.OldValue, CultureInfo.InvariantCulture),
            CorrectedAtUtc: entry.LatestCorrection?.CorrectedAtUtc);
}

public record CreateMeterReadingRequest(decimal KwhValue, DateTimeOffset ReadingTimestamp, Guid IdempotencyKey);

public record MeterReadingResponse(Guid Id, decimal KwhValue, DateTimeOffset ReadingTimestamp, int Version);

public record MeterReadingHistoryPageResponse(IReadOnlyList<MeterReadingHistoryItemResponse> Items, int TotalCount, int Page, int PageSize);

public record MeterReadingHistoryItemResponse(Guid Id, decimal KwhValue, DateTimeOffset ReadingTimestamp, int Version, bool IsPendingRegression, decimal? CorrectedFromKwhValue, DateTimeOffset? CorrectedAtUtc);

public record EditMeterReadingRequest(decimal KwhValue, int Version);
