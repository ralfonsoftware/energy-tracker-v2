using System.Security.Claims;
using System.Threading.RateLimiting;
using EnergyTracker.Application;
using Microsoft.AspNetCore.RateLimiting;

namespace EnergyTracker.Api;

// Names of the rate-limit policies (AD-27, audit S3). The route-to-policy mapping is guarded by
// RateLimitedRoutesTests, the numbers live in HttpRateLimiting below.
internal static class RateLimitPolicies
{
    public const string Login = "Login";
    public const string Upload = "Upload";
    public const string Export = "Export";
    public const string InviteCreate = "InviteCreate";
    public const string InviteRedeem = "InviteRedeem";

    public const string MemberRemoval = "MemberRemoval";
}

// AD-27 rate limiting: ASP.NET Core's built-in in-process limiter (correct for one instance, AD-24).
// Fixed windows, no queueing; a rejection is a 429 ProblemDetails. Identity policies partition on the
// OIDC issuer + subject (so invitees without a membership are covered), /login on the client IP that
// UseForwardedHeaders resolved. The numbers are the story 11.7 rate-limit table.
internal static class HttpRateLimiting
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    // Registers the AD-27 rate-limit policies and the 429 ProblemDetails rejection (AC #4, #7).
    public static IServiceCollection AddHttpRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = WriteRejectionAsync;

            options.AddPolicy(RateLimitPolicies.Login, context => FixedWindow(ClientIp(context), 20, OneMinute));
            options.AddPolicy(RateLimitPolicies.Upload, context => FixedWindow(IdentityKey(context), 60, TenMinutes));
            options.AddPolicy(RateLimitPolicies.Export, context => FixedWindow(IdentityKey(context), 5, OneMinute));
            options.AddPolicy(RateLimitPolicies.InviteCreate, context => FixedWindow(IdentityKey(context), 10, TenMinutes));
            options.AddPolicy(RateLimitPolicies.InviteRedeem, context => FixedWindow(IdentityKey(context), 20, TenMinutes));
            options.AddPolicy(RateLimitPolicies.MemberRemoval, context => FixedWindow(IdentityKey(context), 10, TenMinutes));
        });

    private static RateLimitPartition<string> FixedWindow(string key, int permitLimit, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    private static string ClientIp(HttpContext context) =>
        "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    // Behind RequireAuthorization both claims always exist; a missing one falls back to the client IP so
    // the limiter can never throw.
    private static string IdentityKey(HttpContext context)
    {
        var subject = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var issuer = context.User.FindFirst(HouseholdClaimTypes.ValidatedIssuer)?.Value;
        return string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(issuer)
            ? ClientIp(context)
            : $"id:{issuer}|{subject}";
    }

    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay)
            ? Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds))
            : 60;
        context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);

        await Results.Problem(
            title: "Too Many Requests",
            detail: "Too many requests. Wait a moment and try again.",
            statusCode: StatusCodes.Status429TooManyRequests).ExecuteAsync(context.HttpContext);
    }
}
