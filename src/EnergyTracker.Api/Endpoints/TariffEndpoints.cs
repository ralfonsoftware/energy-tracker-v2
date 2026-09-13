using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Api.Endpoints;

public static class TariffEndpoints
{
    private const string NoHouseholdDetail = "The authenticated principal does not belong to a Household.";

    // Same shape as MeterReadingEndpoints.TryGetHouseholdId — copied rather than referenced
    // across files (that helper is private to its own class); a Tariff is Household-scoped
    // exactly like MeterReading, not tenant-root like Household itself.
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

    public static RouteGroupBuilder MapTariffEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/tariffs", async (
            CreateTariffRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            CreateTariff createTariff,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

            try
            {
                var tariff = await createTariff.ExecuteAsync(
                    householdId, request.MonthlyBaseFee, request.PricePerKwh, request.Currency,
                    request.ContractStartDate, request.ContractPeriodMonths, cancellationToken);
                return Results.Ok(ToResponse(tariff));
            }
            catch (TariffValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        api.MapGet("/tariffs", async (
            ICurrentHouseholdAccessor householdAccessor,
            GetTariffHistory getTariffHistory,
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
                var result = await getTariffHistory.ExecuteAsync(householdId, page, pageSize, cancellationToken);
                return Results.Ok(ToHistoryPageResponse(result));
            }
            catch (TariffValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        api.MapPut("/tariffs/{id:guid}", async (
            Guid id,
            EditTariffRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            EditTariff editTariff,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

            if (request.Version is null)
            {
                return Results.Problem(detail: "Version is required.", statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var tariff = await editTariff.ExecuteAsync(
                    householdId, id, request.MonthlyBaseFee, request.PricePerKwh, request.Currency,
                    request.ContractStartDate, request.ContractPeriodMonths, request.Version.Value,
                    request.OverrideConfirmed, cancellationToken);
                return Results.Ok(ToResponse(tariff));
            }
            catch (TariffValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (TariffNotFoundException)
            {
                return Results.NotFound();
            }
            catch (TariffConcurrencyConflictException ex)
            {
                // Message only, not the full current server state — matches
                // MeterReadingEndpoints'/HouseholdEndpoints' established 409 precedent; the
                // frontend's own refetch covers getting the current value.
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        return api;
    }

    private static TariffResponse ToResponse(Tariff tariff) =>
        new(
            tariff.Id,
            tariff.MonthlyBaseFee,
            tariff.PricePerKwh,
            tariff.Currency,
            tariff.ContractStartDate,
            tariff.ContractPeriodMonths,
            tariff.Version);

    private static TariffHistoryPageResponse ToHistoryPageResponse(TariffHistoryPage page) =>
        new(
            Items: page.Items.Select(ToHistoryItemResponse).ToList(),
            TotalCount: page.TotalCount,
            Page: page.Page,
            PageSize: page.PageSize);

    private static TariffHistoryItemResponse ToHistoryItemResponse(TariffHistoryEntry entry) =>
        new(
            Id: entry.Tariff.Id,
            MonthlyBaseFee: entry.Tariff.MonthlyBaseFee,
            PricePerKwh: entry.Tariff.PricePerKwh,
            Currency: entry.Tariff.Currency,
            ContractStartDate: entry.Tariff.ContractStartDate,
            ContractPeriodMonths: entry.Tariff.ContractPeriodMonths,
            Version: entry.Tariff.Version,
            IsCurrent: entry.IsCurrent,
            EffectiveUntil: entry.EffectiveUntil,
            Corrections: entry.Corrections.Select(ToCorrectionResponse).ToList());

    private static TariffFieldCorrectionResponse ToCorrectionResponse(KeyValuePair<string, AuditCorrection> correction) =>
        new(correction.Key, correction.Value.OldValue, correction.Value.NewValue, correction.Value.CorrectedAtUtc);
}

public record CreateTariffRequest(decimal MonthlyBaseFee, decimal PricePerKwh, string Currency, DateTimeOffset ContractStartDate, int ContractPeriodMonths);

public record TariffResponse(Guid Id, decimal MonthlyBaseFee, decimal PricePerKwh, string Currency, DateTimeOffset ContractStartDate, int ContractPeriodMonths, int Version);

public record TariffHistoryPageResponse(IReadOnlyList<TariffHistoryItemResponse> Items, int TotalCount, int Page, int PageSize);

public record TariffHistoryItemResponse(
    Guid Id,
    decimal MonthlyBaseFee,
    decimal PricePerKwh,
    string Currency,
    DateTimeOffset ContractStartDate,
    int ContractPeriodMonths,
    int Version,
    bool IsCurrent,
    DateTimeOffset? EffectiveUntil,
    IReadOnlyList<TariffFieldCorrectionResponse> Corrections);

// FieldName is the raw AuditCorrection.FieldName ("MonthlyBaseFee", "PricePerKwh", "Currency",
// "ContractStartDate", "ContractPeriodMonths") — OldValue/NewValue are the same
// locale-neutral (InvariantCulture) strings AuditCorrection stores; the frontend parses/formats
// per field type and the household's own Locale (AD-18), same inversion MeterReadingEndpoints'
// CorrectedFromKwhValue already does for a single field.
public record TariffFieldCorrectionResponse(string FieldName, string OldValue, string NewValue, DateTimeOffset CorrectedAtUtc);

public record EditTariffRequest(
    decimal? MonthlyBaseFee,
    decimal? PricePerKwh,
    string? Currency,
    DateTimeOffset? ContractStartDate,
    int? ContractPeriodMonths,
    int? Version,
    bool OverrideConfirmed);
