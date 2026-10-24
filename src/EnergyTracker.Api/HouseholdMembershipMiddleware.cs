using EnergyTracker.Infrastructure.Adapters;

namespace EnergyTracker.Api;

/// <summary>
/// Looks up the Household of an authenticated /api request once, asynchronously, and keeps it in
/// HttpContext.Items for that request only (AD-17), so the EF query filter and the endpoints read a cached
/// value and never block a thread on the database (audit C2). A removed member's next request runs the
/// lookup again and finds no row (AD-26).
/// </summary>
public static class HouseholdMembershipMiddleware
{
    public static IApplicationBuilder UseHouseholdMembership(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            // /api only: static assets and /health must never pay for the lookup.
            if (context.Request.Path.StartsWithSegments("/api") && context.User.Identity?.IsAuthenticated == true)
            {
                var resolver = context.RequestServices.GetRequiredService<HouseholdMembershipResolver>();
                ResolvedHouseholdMembership.Store(
                    context, await resolver.ResolveAsync(context.User, context.RequestAborted));
            }

            await next(context);
        });
}
