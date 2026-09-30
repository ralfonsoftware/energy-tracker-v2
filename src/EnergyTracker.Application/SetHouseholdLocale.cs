using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Sets a Household's Locale from the closed supported list, without optimistic concurrency (AC #2, #7, #8).</summary>
public class SetHouseholdLocale(IHouseholdRepository repository)
{
    public async Task<Household> ExecuteAsync(Guid householdId, string? locale, CancellationToken cancellationToken)
    {
        if (locale is null || !CreateHousehold.SupportedLocales.Contains(locale))
        {
            throw new HouseholdValidationException(
                $"Unsupported locale '{locale}'. Supported locales: {string.Join(", ", CreateHousehold.SupportedLocales)}.");
        }

        return await repository.UpdateLocaleAsync(householdId, locale, cancellationToken);
    }
}
