using System.Text.RegularExpressions;

namespace EnergyTracker.Api;

/// <summary>
/// The one place that knows the invite path shapes (<c>/join/{token}</c>, <c>/api/household-invites/{token}[/accept]</c>,
/// plain, percent-encoded or double-encoded in a query value) and rewrites the segment after them to <see cref="Marker"/>.
/// Fails closed: whatever follows the prefix is replaced, so a revoke id goes too (AD-26, ADR 0008).
/// </summary>
internal static partial class InviteTokenRedactor
{
    public const string Marker = "{token}";

    // The prefix absorbs extra slashes (`/join//<t>`); the segment is everything up to the next / ? # & whitespace or quote,
    // whatever its characters, so a value outside base64url cannot leave a readable tail.
    [GeneratedRegex(
        @"(?<prefix>(?:/|%2F|%252F)(?:join|api(?:/|%2F|%252F)household-invites)(?:/|%2F|%252F)+)[^/?#&\s""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex InviteSegment();

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        try
        {
            return InviteSegment().Replace(value, "${prefix}" + Marker);
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: Serilog and OTel swallow a thrown exception and would ship the raw value.
            return Marker;
        }
    }
}
