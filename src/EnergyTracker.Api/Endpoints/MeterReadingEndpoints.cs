using System.Globalization;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Api.Endpoints;

public static class MeterReadingEndpoints
{
    public static RouteGroupBuilder MapMeterReadingEndpoints(this RouteGroupBuilder api)
    {
        var household = api.MapGroup(string.Empty).RequireHousehold();
        household.MapPost("/meter-readings", async (
            CreateMeterReadingRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            CreateMeterReading createMeterReading,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

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

        household.MapGet("/meter-readings", async (
            ICurrentHouseholdAccessor householdAccessor,
            GetMeterReadingHistory getMeterReadingHistory,
            CancellationToken cancellationToken,
            int page = 1,
            int pageSize = 20) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

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

        household.MapPut("/meter-readings/{id:guid}", async (
            Guid id,
            EditMeterReadingRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            EditMeterReading editMeterReading,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

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
