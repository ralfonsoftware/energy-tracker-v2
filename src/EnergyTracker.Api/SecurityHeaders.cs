namespace EnergyTracker.Api;

// AD-27 (audit S3): response headers that contain the damage of a future script injection or framing
// attack. Registered first in the pipeline and outside Development only (Vite dev mode injects inline
// scripts). The headers are written in OnStarting, not before next(): UseExceptionHandler resets the
// response before it writes its ProblemDetails, which would otherwise drop them from error responses.
internal static class SecurityHeaders
{
    // style-src needs 'unsafe-inline' because Radix injects <style> elements at runtime (Select and the
    // scroll lock of Dialog, Sheet and DropdownMenu); scripts stay strict. img-src has no data: because the
    // built bundle uses no data: URI (re-checked in Story 11.7); add it back only if a build needs it.
    internal const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self'; " +
        "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    // Sets the AD-27 CSP, nosniff and Referrer-Policy on every response (AC #1, #6).
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                var headers = ((HttpContext)state).Response.Headers;
                headers.TryAdd("Content-Security-Policy", ContentSecurityPolicy);
                headers.TryAdd("X-Content-Type-Options", "nosniff");
                headers.TryAdd("Referrer-Policy", "same-origin");
                return Task.CompletedTask;
            }, context);

            await next(context);
        });
}
