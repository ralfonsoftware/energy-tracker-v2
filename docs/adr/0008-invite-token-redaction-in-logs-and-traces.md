# 0008: Invite tokens are redacted in request logs and server spans

Source: AD-26 (invite tokens), AD-19 (structured Serilog logging), Story 11.22 (audit S10 follow-up).
Code: `src/EnergyTracker.Api/InviteTokenRedactor.cs`, `InviteTokenRedactingEnricher.cs`, `InviteTokenSpanRedaction.cs`, `Composition/ObservabilityRegistration.cs`, `appsettings*.json`.

## Context

The invite token is the credential and travels in the URL path (`/join/<token>`, `/api/household-invites/<token>[/accept]`) and in `/login?returnUrl=%2Fjoin%2F<token>`. ASP.NET Core's hosting diagnostics wrote `Request starting` / `Request finished` with `Path` and `QueryString` at `Information` to stdout, so into Log Analytics, and the server span carried `url.path` and `url.query` to the exporter. Anyone who could read logs could join the Household.

`ReadFrom.Configuration` reads only a `Serilog` section. The old `Logging:LogLevel` blocks in both `appsettings` files were never in effect.

## Decision

Option A2, chosen by Ralf on 2026-11-01. Rejected: A1 (silence `Microsoft.AspNetCore.Hosting.Diagnostics` for every route; loses status and latency lines and still needs the span redaction) and B (move the token out of the path; changes the invite link, the OIDC return path and old links stay valid for 7 days).

- `InviteTokenRedactor` is the one place that knows the invite path shapes, plain, percent-encoded and double-encoded. It fails closed: whatever follows `/join/` or `/api/household-invites/` (extra slashes included) up to the next `/`, `?`, `#`, `&`, whitespace or quote becomes `{token}`, a revoke id included. A regex timeout returns `{token}` for the whole value instead of throwing, because Serilog and OTel swallow the exception and would ship the raw value.
- `InviteTokenRedactingEnricher` rewrites every string property of every log event, nested values included. Request lines stay with method, status and elapsed time. It runs in every environment (AD-19: no environment branching).
- `AddInviteTokenRedaction` sets `AspNetCoreTraceInstrumentationOptions.EnrichWithHttpRequest`, which runs after the instrumentation has set the url tags and before the span ends, so no exporter ordering is involved. It is called on both the `azuremonitor` and the `otlp` branch. A processor registered after the exporter would mutate a span already queued, which is racy, so none is used. Checked for the Azure branch at the pinned `Azure.Monitor.OpenTelemetry.AspNetCore` 1.6.0: the distro calls the default `AddAspNetCoreInstrumentation()` and never references `AspNetCoreTraceInstrumentationOptions`, so the hook, which chains any earlier `EnrichWithHttpRequest`, runs there too.
- Both `appsettings` files carry `Serilog:MinimumLevel` instead of `Logging`. `Microsoft.AspNetCore` is `Warning`; `Microsoft.AspNetCore.Hosting.Diagnostics`, `Microsoft.AspNetCore.Authentication` and `Microsoft.AspNetCore.DataProtection` are `Information` (the longest prefix wins). Request lines, the sign-in/challenge trail and key-ring create/rotate lines stay. Trade-off accepted by Ralf on 2026-11-01: other `Microsoft.AspNetCore.*` categories (Authorization, Kestrel, Routing, static files) lose their `Information` lines, which they had while the old `Logging` block was dead. EF Core is not overridden.
- OpenTelemetry.Instrumentation.AspNetCore 1.20.1 already writes `url.query` as `?returnUrl=Redacted`. The hook does not depend on that default.

## Consequences

- A route with a `{token}` parameter is checked by `InviteTokenRoutesAreRedactedTests`. `/api/household-import/{token:guid}/confirm` is listed there as not an invite token.
- Both exporter branches are pinned by `InviteTokenRedactionCoversBothExportersTests`; the Azure branch has no test exporter, so Ralf checks Log Analytics once after deploy (story Task 6).
- Log levels live in the `Serilog` section. `Logging:LogLevel` is not read. Test hosts replace the static `Log.Logger`, so tests that capture log events run in the `Serial logging` collection.
