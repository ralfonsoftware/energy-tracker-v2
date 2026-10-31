# 0001: Serilog, default logging providers and `writeToProviders`

Source: AD-19 (OTel extension), `ARCHITECTURE-SPINE.md`.
Code: `src/EnergyTracker.Api/Composition/ObservabilityRegistration.cs`.

## Context

`WebApplicationBuilder` registers Console, Debug, EventSource and EventLog logger providers. `UseSerilog` replaces `ILoggerFactory`, so with its default `writeToProviders: false` those providers stay registered but never receive an event.

The OTLP path needs `writeToProviders: true`, so that Serilog events also reach the OpenTelemetry logging provider (Aspire Dashboard, trace correlation). That flag forwards to every registered `ILoggerProvider`. The dormant default Console provider then wakes up and every console line would appear twice.

The Azure Monitor path must not forward. Application Insights is workspace based on the same Log Analytics workspace that Container Apps already streams stdout into, so a forward would ingest every line twice against the shared `dailyQuotaGb` cap. The same flag decides whether Azure Monitor's bundled logging provider ever receives events.

Comparing `Otel:Exporter` case insensitively in one place and case sensitively in another would let `otlp` or `OTLP` silently disable all telemetry.

## Decision

- `builder.Logging.ClearProviders()` runs first and unconditionally, so Serilog's `WriteTo.Console()` is the only console sink on every path.
- `Otel:Exporter` is read once, trimmed and lower cased at once, before `UseSerilog`. Every later comparison uses that one value.
- `writeToProviders` is true only for `otlp`.
- A blank Azure Monitor connection string or a blank, malformed or scheme-less OTLP endpoint skips the registration. `UseAzureMonitor` throws on a blank string, and `new Uri("aspire-dashboard:18889")` parses `aspire-dashboard` as a scheme instead of failing. Only an absolute http or https URI counts.
- Azure Monitor gets traces and metrics only. EF Core and runtime instrumentation are added explicitly because the distro does not cover them and the app runs on both databases.

## Consequences

- A new exporter has to decide `writeToProviders` explicitly, with the double ingest in mind.
- `OtelConfigurationTests` pin the casing and the degrade-to-off behaviour.
