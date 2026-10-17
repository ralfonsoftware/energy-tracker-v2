using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace EnergyTracker.Api;

/// <summary>
/// Hands the provider's end-session URL to the /logout endpoint instead of letting the OIDC handler write a
/// 302, because a fetch cannot follow a cross-origin redirect and a form POST would hit the CSP's
/// form-action (Story 11.8, AC #5; AD-17 FR-33, AD-27).
/// </summary>
internal static class FederatedLogout
{
    /// <summary><see cref="HttpContext.Items"/> key carrying the URL (or "/") from the sign-out event to the endpoint.</summary>
    public const string RedirectUrlKey = "energy-tracker.federated-logout-url";

    /// <summary>
    /// Body of the OIDC <c>OnRedirectToIdentityProviderForSignOut</c> event, run after the ClientId assignment.
    /// Never logs the URL: it carries the protected state and the client id.
    /// </summary>
    public static void Handle(RedirectContext context)
    {
        var message = context.ProtocolMessage;

        // A provider without end_session_endpoint (AD-17's FR-33 fallback): the stock handler would throw
        // here, so degrade to a local sign-out and let the SPA land on "/".
        if (string.IsNullOrWhiteSpace(message.IssuerAddress))
        {
            context.HttpContext.Items[RedirectUrlKey] = "/";
            context.HandleResponse();
            return;
        }

        // Mandatory: the stock handler protects the state only after this event, and skips that step when
        // the event handles the response. Without a state, /signout-callback-oidc has nothing to redirect
        // to and the member lands on a blank page after the provider's confirmation.
        if (!string.IsNullOrEmpty(message.State))
        {
            context.Properties.Items[OpenIdConnectDefaults.UserstatePropertiesKey] = message.State;
        }

        message.State = context.Options.StateDataFormat.Protect(context.Properties);
        context.HttpContext.Items[RedirectUrlKey] = message.CreateLogoutRequestUrl();
        context.HandleResponse();
    }
}
