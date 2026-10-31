using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace EnergyTracker.Api;

/// <summary>
/// The session cookie's lifetime rules: a 14-day sliding window, plus a 90-day absolute cap measured from an
/// issued-at stamp carried in the ticket (Story 11.8, AC #4, #9; AD-17, audit S13).
/// </summary>
internal static class SessionLifetime
{
    public static readonly TimeSpan SlidingWindow = TimeSpan.FromDays(14);
    public static readonly TimeSpan AbsoluteCap = TimeSpan.FromDays(90);

    /// <summary>
    /// Key in <see cref="AuthenticationProperties.Items"/>. It is not <c>IssuedUtc</c>, which restarts on every
    /// sliding renewal and would make the cap meaningless; Items survive renewal, and the ticket is encrypted
    /// and signed by Data Protection, so a client cannot alter the stamp.
    /// </summary>
    public const string IssuedAtItemKey = "energy-tracker.session-issued-utc";

    /// <summary>Runs on every real sign-in (not on sliding renewal) and records when the session began.</summary>
    public static void StampIssuedAt(CookieSigningInContext context)
    {
        var clock = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
        context.Properties.Items[IssuedAtItemKey] = clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Rejects and signs out a session with no stamp, an unreadable stamp (fail closed) or a stamp at least
    /// <see cref="AbsoluteCap"/> old. A cookie issued before the stamp existed is rejected on purpose, so each
    /// member signs in once after the deploy (AC #9, amended). No lazy stamping, and never
    /// <c>IssuedUtc</c> as a fallback.
    /// </summary>
    [SuppressMessage("Performance", "CA1873:Avoid potentially expensive logging", Justification = "Reads of already-computed values on an always-enabled log level; the arguments cost nothing to evaluate.")]
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        string? rejection = null;
        if (!context.Properties.Items.TryGetValue(IssuedAtItemKey, out var stamp) || stamp is null)
        {
            rejection = "no issued-at stamp";
        }
        else if (!DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var issuedAt))
        {
            rejection = "unreadable issued-at stamp";
        }
        else
        {
            var clock = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
            if (clock.GetUtcNow() - issuedAt >= AbsoluteCap)
            {
                rejection = "older than the absolute cap";
            }
        }

        if (rejection is null)
        {
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        // Which of the three reasons, nothing else: no identity data.
        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(SessionLifetime))
            .LogInformation("Rejected a session cookie: {Reason}.", rejection);
    }
}
