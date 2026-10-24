using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;

namespace EnergyTracker.Infrastructure.Adapters;

/// <summary>
/// The caller's own HouseholdMember id and Household id, resolved once for one HTTP request and kept in
/// HttpContext.Items (AD-17: per request only, never across requests and never in the cookie). Both are
/// null for a principal with no HouseholdMember row.
/// </summary>
public sealed record ResolvedHouseholdMembership(Guid? MemberId, Guid? HouseholdId)
{
    // The only place that touches the Items key, so the middleware (writer) and the accessor (reader) cannot drift.
    private static readonly object ItemsKey = new();

    public static ResolvedHouseholdMembership None { get; } = new(null, null);

    public static void Store(HttpContext context, ResolvedHouseholdMembership membership) =>
        context.Items[ItemsKey] = membership;

    public static bool TryGet(HttpContext context, [NotNullWhen(true)] out ResolvedHouseholdMembership? membership)
    {
        membership = context.Items.TryGetValue(ItemsKey, out var value) ? value as ResolvedHouseholdMembership : null;
        return membership is not null;
    }
}
