using System.Net.Http.Headers;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Infrastructure.Adapters;

namespace EnergyTracker.Api;

// Description names the selected adapter for the startup log line and never contains the API key, nor
// anything of the endpoint beyond scheme://host[:port] (no userinfo, path or query). Warning is set when the
// endpoint is usable but probably not what the operator meant (it names the key, never a value); Program.cs
// logs it next to the Description.
internal sealed record AiPlausibilityBackendConfigurationResult(bool Configured, string Description, string? Warning = null);

// AD-8 (audit C1): the AI backend is configured through exactly one section, Ai:* (Endpoint, ApiKey, Model,
// BackendLabel), the one that appsettings.json, .env.example, docker-compose.yml and Bicep document. It is
// read exactly once, here, at the composition root (Program.cs calls Configure once) — same
// switch-on-configured-value shape as Database:Provider/JobQueue:Provider. Blank/unset selects the no-op
// adapter; CorrelateEvent is the only place downstream allowed to also check
// Household.AiPlausibilityEnabled — nothing else branches on whether AI is configured. There is no fallback
// to the old AiPlausibility:* spelling: one section, one documented spelling.
// AiConfigurationKeysAreOneSetTests fails when the code and the shipped files stop agreeing on the keys.
internal static class AiPlausibilityBackendConfiguration
{
    // Startup log lines for the selection above. Program.cs calls it once, after the app is built.
    internal static void LogSelection(ILogger logger, AiPlausibilityBackendConfigurationResult backend)
    {
        logger.LogInformation("AI plausibility backend: {Backend}", backend.Description);
        if (backend.Warning is { } warning)
        {
            logger.LogWarning("AI plausibility backend: {Warning}", warning);
        }
    }

    private const string EndpointSetting = "Ai:Endpoint";
    private const string ApiKeySetting = "Ai:ApiKey";
    private const string ModelSetting = "Ai:Model";
    private const string BackendLabelSetting = "Ai:BackendLabel";

    // "default" is a safe no-op for LMStudio's local server (it ignores the field entirely) but is
    // NOT a valid model id for real OpenAI or another cloud provider — those deployments must set
    // Ai:Model explicitly, or every classification call 400s and silently resolves to null.
    private const string DefaultModel = "default";

    public static AiPlausibilityBackendConfigurationResult Configure(IServiceCollection services, IConfiguration configuration)
    {
        var endpoint = Read(configuration, EndpointSetting);
        var apiKey = Read(configuration, ApiKeySetting);
        var model = Read(configuration, ModelSetting);
        if (model.Length == 0)
        {
            model = DefaultModel;
        }

        // A human-set label (e.g. "Local (LMStudio)" or "OpenAI"), never a heuristic guess from the URL.
        var labelValue = Read(configuration, BackendLabelSetting);
        var label = labelValue.Length == 0 ? null : labelValue;

        // AC #5's "always visible, regardless of enablement" backend info. Registered once here rather than
        // re-read per request; also carries Model through to OpenAiCompatibleClient (Infrastructure) — one
        // registration instead of a second deployment-wide options type.
        services.AddSingleton(new AiPlausibilityBackendOptions(Configured: endpoint.Length > 0, Label: label, Model: model));

        if (endpoint.Length == 0)
        {
            services.AddSingleton<IAiPlausibilityClient, NoOpAiPlausibilityClient>();
            return new AiPlausibilityBackendConfigurationResult(
                Configured: false,
                Description: $"none configured ({EndpointSetting} is blank), so the Household toggle has no effect");
        }

        // A set-but-unusable endpoint is an operator error that one edit fixes: fail startup, naming the key,
        // instead of a UriFormatException inside the first background job where it is swallowed. The value is
        // not echoed (it may carry credentials).
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            || (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{EndpointSetting} must be an absolute http or https URL of the server root, for example " +
                "http://localhost:1234 (environment variable Ai__Endpoint). See docs/self-hosting.md.");
        }

        services.AddHttpClient<IAiPlausibilityClient, OpenAiCompatibleClient>(client =>
        {
            client.BaseAddress = endpointUri;
            if (apiKey.Length > 0)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
        });

        // GetLeftPart(UriPartial.Authority) would include userinfo, so the origin is built by hand.
        var origin = $"{endpointUri.Scheme}://{endpointUri.Host}" + (endpointUri.IsDefaultPort ? "" : $":{endpointUri.Port}");
        // The client resolves the relative path v1/chat/completions against the endpoint, so anything after the
        // host is unreliable: a path segment is replaced (https://host/openai loses "openai") or doubled
        // (https://host/v1/ calls /v1/v1/...), and userinfo is never sent. No behaviour change, no new failure
        // (https://api.openai.com/v1 works today); the operator is told to use the server root.
        var hasMoreThanTheServerRoot = endpointUri.AbsolutePath != "/"
            || endpointUri.Query.Length > 0
            || endpointUri.Fragment.Length > 0
            || endpointUri.UserInfo.Length > 0;
        var warning = hasMoreThanTheServerRoot
            ? $"{EndpointSetting} has a path, query, fragment or userinfo. The client appends v1/chat/completions to it, " +
                "so a path segment may be dropped or doubled and userinfo is not sent; use the server root, " +
                "for example http://localhost:1234."
            : null;

        return new AiPlausibilityBackendConfigurationResult(
            Configured: true,
            Description: $"OpenAiCompatibleClient at {origin} (model {model}, label {label ?? "none"})",
            Warning: warning);
    }

    private static string Read(IConfiguration configuration, string key) => (configuration[key] ?? string.Empty).Trim();
}
