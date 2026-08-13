using System.Security.Claims;
using System.Text.Encodings.Web;
using EnergyTracker.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnergyTracker.Api.Tests;

/// <summary>
/// Test-only stand-in for the real OIDC handshake (no bundled local OIDC provider exists for
/// dev/test — an explicit architecture Deferred item). Issues a principal with the iss/sub
/// claims carried in request headers when present; otherwise reports "no result" so
/// unauthenticated-request tests (AC #5) still exercise the real authorization pipeline.
/// </summary>
public class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string IssuerHeader = "X-Test-Issuer";
    public const string SubjectHeader = "X-Test-Subject";
    public const string DefaultIssuer = "https://test-issuer.example";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(SubjectHeader, out var subjectValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var issuer = Request.Headers.TryGetValue(IssuerHeader, out var issuerValues)
            ? issuerValues.ToString()
            : DefaultIssuer;

        // Mirrors Program.cs's real OnTokenValidated behavior: the subject claim plus a
        // dedicated, explicitly-captured issuer claim (production no longer trusts ambient
        // Claim.Issuer for tenant resolution — see HouseholdClaimTypes.ValidatedIssuer).
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, subjectValues.ToString(), ClaimValueTypes.String, issuer),
            new Claim(HouseholdClaimTypes.ValidatedIssuer, issuer),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    // Mirrors Program.cs's real OnRedirectToLogin behavior for /api/** — AC #5 requires a 401,
    // not a redirect, for unauthenticated API calls.
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
