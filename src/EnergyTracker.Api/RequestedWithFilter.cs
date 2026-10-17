namespace EnergyTracker.Api;

/// <summary>
/// Rejects a state-changing request that lacks the X-Requested-With header with a 400 ProblemDetails
/// (Story 11.8, AC #1, #6; AD-27, audit S9). No CORS is configured, so a cross-site page cannot add a custom
/// header without a preflight this app never answers. Presence is the check; the SPA sends <c>fetch</c>.
/// </summary>
/// <remarks>
/// Pipeline order: rate limiter, authentication/authorization, parameter binding, endpoint filters, handler.
/// A header-less request is counted by the limiter, answered 401 when unauthenticated, and, because
/// minimal-API binding runs before filters, a request whose body fails to bind answers the binding's own 400
/// first. For a multipart upload the form is parsed before this rejects it, bounded by the existing size
/// caps and reachable only by an authenticated session. None of that changes the guarantee that no handler
/// logic runs. AD-27 says endpoint filter, so do not move this into a middleware.
/// </remarks>
internal sealed class RequestedWithFilter : IEndpointFilter
{
    public const string HeaderName = "X-Requested-With";
    public const string RejectionTitle = "Missing X-Requested-With header";
    public const string RejectionDetail = "State-changing requests must come from the Energy Tracker app.";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
            || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method)
            || !string.IsNullOrWhiteSpace(request.Headers[HeaderName].ToString()))
        {
            return next(context);
        }

        return ValueTask.FromResult<object?>(
            Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: RejectionTitle, detail: RejectionDetail));
    }
}

internal static class RequestedWithFilterExtensions
{
    /// <summary>
    /// Puts the X-Requested-With filter and its marker metadata on a route group or a single route (Story 11.8, AC #1, #7).
    /// Both are added together so they cannot drift apart.
    /// </summary>
    public static TBuilder RequireRequestedWith<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        // The marker says "this route enforces the header", so it goes only on routes that can mutate
        // (a route with no method metadata is treated as mutating). The filter itself ignores safe methods.
        builder.Add(endpoint =>
        {
            var methods = endpoint.Metadata.OfType<HttpMethodMetadata>().SelectMany(m => m.HttpMethods).ToList();
            if (methods.Count == 0 || methods.Any(IsMutating))
            {
                endpoint.Metadata.Add(RequestedWithRequiredMetadata.Instance);
            }
        });
        builder.AddEndpointFilter<TBuilder, RequestedWithFilter>();
        return builder;
    }

    private static bool IsMutating(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));
}
