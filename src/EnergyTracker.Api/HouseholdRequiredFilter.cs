using EnergyTracker.Application.Ports;

namespace EnergyTracker.Api;

/// <summary>
/// Answers 403 ProblemDetails when the authenticated principal belongs to no Household.
/// Replaces the private <c>TryGetHouseholdId</c> copy every Household-scoped endpoint file used to carry.
/// </summary>
/// <remarks>
/// Runs after <see cref="RequestedWithFilter"/> (group filters run outer first) and after parameter binding, so
/// the order of effects is the one the copies had. Routes that must work without a Household (create a Household,
/// accept an invite, session) are not mapped behind it.
/// </remarks>
internal sealed class HouseholdRequiredFilter : IEndpointFilter
{
    public const string RejectionDetail = "The authenticated principal does not belong to a Household.";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var accessor = context.HttpContext.RequestServices.GetRequiredService<ICurrentHouseholdAccessor>();
        return accessor.HouseholdId is null
            ? ValueTask.FromResult<object?>(Results.Problem(detail: RejectionDetail, statusCode: StatusCodes.Status403Forbidden))
            : next(context);
    }
}

internal static class HouseholdRequiredFilterExtensions
{
    /// <summary>
    /// Puts the Household filter and its marker metadata on a route group or a single route.
    /// Both are added together so they cannot drift apart.
    /// </summary>
    public static TBuilder RequireHousehold<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint => endpoint.Metadata.Add(HouseholdRequiredMetadata.Instance));
        builder.AddEndpointFilter<TBuilder, HouseholdRequiredFilter>();
        return builder;
    }

    /// <summary>
    /// The caller's Household id for a handler mapped behind <see cref="RequireHousehold{TBuilder}"/>. Reaching a
    /// handler without the filter is a programming error, not a user error, so this fails loud.
    /// </summary>
    public static Guid RequireHouseholdId(this ICurrentHouseholdAccessor accessor) =>
        accessor.HouseholdId
        ?? throw new InvalidOperationException("No Household for this request: map the route behind RequireHousehold().");
}
