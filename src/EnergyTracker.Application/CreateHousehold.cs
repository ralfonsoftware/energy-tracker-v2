using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>
/// Creates a Household plus its creating HouseholdMember in one unit of work (AC #1/#2/#7).
/// Plain class with a constructor-injected repository port — no CQRS/mediator library exists
/// in this repo yet and one use case doesn't warrant introducing one.
/// </summary>
public class CreateHousehold(IHouseholdRepository repository)
{
    // Launch Locales only (AD-15/NFR5) — a later Locale is a resource-file addition, not a
    // code change, but until one ships this is the closed, currently-supported set.
    public static readonly IReadOnlyCollection<string> SupportedLocales = ["de-DE", "en-US"];

    public async Task<Household> ExecuteAsync(
        string externalIssuer,
        string externalSubjectId,
        string locale,
        string currency,
        CancellationToken cancellationToken)
    {
        if (!SupportedLocales.Contains(locale))
        {
            throw new HouseholdValidationException(
                $"Unsupported locale '{locale}'. Supported locales: {string.Join(", ", SupportedLocales)}.");
        }

        if (!IsPlausibleCurrencyCode(currency))
        {
            throw new HouseholdValidationException(
                $"Invalid currency '{currency}'. Expected a 3-letter ISO 4217-shaped code (e.g. 'EUR').");
        }

        var existingMember = await repository.FindMemberAsync(externalIssuer, externalSubjectId, cancellationToken);
        if (existingMember is not null)
        {
            throw new HouseholdAlreadyExistsException(existingMember.HouseholdId);
        }

        var now = DateTimeOffset.UtcNow;
        var household = new Household
        {
            Id = Guid.NewGuid(),
            Locale = locale,
            Currency = currency,
            CreatedAtUtc = now,
        };
        var creator = new HouseholdMember
        {
            Id = Guid.NewGuid(),
            HouseholdId = household.Id,
            ExternalIssuer = externalIssuer,
            ExternalSubjectId = externalSubjectId,
            CreatedAtUtc = now,
        };

        await repository.AddAsync(household, creator, cancellationToken);

        return household;
    }

    // Full ISO 4217 membership validation isn't required for MVP — just "not blank, not obviously
    // wrong". Null-safe: ASP.NET Core's JSON binding doesn't enforce non-nullable annotations at
    // runtime, so an explicit `"currency": null` in the request body reaches here as a real null.
    private static bool IsPlausibleCurrencyCode(string? currency) =>
        !string.IsNullOrEmpty(currency) && currency.Length == 3 && currency.All(c => c is >= 'A' and <= 'Z');
}
