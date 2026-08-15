---
title: 'OpenTelemetry instrumentation for the API, local Aspire Dashboard'
type: 'feature'
created: '2026-08-15'
status: 'done'
baseline_commit: '7366584496f59feb8a78a87d94d26ce9eeab3850'
review_loop_iteration: 0
context: ['{project-root}/_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE.md']
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Epic-1 retro (2026-08-15) flagged that AD-19 only ever specified stdout logging — there is no APM/tracing, no metrics, no way to see what's happening in a live deployment beyond raw log lines. Winston has already built the Azure/Bicep half of the fix on this branch (App Insights, ingestion cap, alerts); the API itself emits no OTel signals yet, and local dev has nowhere to view them.

**Approach:** Add OpenTelemetry (traces, metrics, additive logs) to `EnergyTracker.Api`, exporter selected by one config value read once at the composition root — `Otlp` locally (to a new `aspire-dashboard` compose service) or `AzureMonitor` in the cloud (to the App Insights Winston already provisioned) — exactly mirroring the existing `Database:Provider`/`Oidc:*` pattern.

## Boundaries & Constraints

**Always:**
- Read `Otel:Exporter` exactly once in `Program.cs` (composition root); nothing downstream branches on it independently (Consistency Conventions).
- Unset/unrecognized `Otel:Exporter` = OTel fully off, app still starts and serves all routes normally — same degrade shape as unconfigured OIDC. Never throw at startup over missing Otel config.
- Keep the existing Serilog console sink untouched. OTel logging is an *additive* second pipeline (`builder.Logging.AddOpenTelemetry(...)`), not a replacement — self-host's `docker logs` baseline must keep working unconditionally, independent of `Otel:Exporter`.
- `Otlp` path instruments traces + metrics + an OTLP log exporter, pointed at `Otel:OtlpEndpoint`. `UseSerilog(..., writeToProviders: true)` on this path only, so Serilog's events also reach the OTel logs pipeline (trace-correlated in the Aspire Dashboard).
- `AzureMonitor` path uses the `Azure.Monitor.OpenTelemetry.AspNetCore` GA Distro (`UseAzureMonitor(...)`) reading `Otel:AzureMonitorConnectionString` — not raw OTLP-to-Azure-Monitor (still preview) — for **traces and metrics only**. `writeToProviders` stays `false` (Serilog's default) on this path: Application Insights is workspace-based on the *same* LAW that Container Apps already streams stdout into, so forwarding Serilog through OTel too would double-ingest every log line against the shared `dailyQuotaGb` cap. Logs in Azure keep using the existing, unchanged stdout→Log Analytics stream.
- Instrument ASP.NET Core requests, `HttpClient`, EF Core, and .NET runtime metrics.
- `docker-compose.yml` gets a new `aspire-dashboard` service (`mcr.microsoft.com/dotnet/aspire-dashboard`, pinned tag) with `Dashboard__Frontend__AuthMode=Unsecured`; `api` gets `Otel__Exporter=Otlp` / `Otel__OtlpEndpoint=http://aspire-dashboard:18889` and a `depends_on`. `docker-compose.sqlserver.yml`/`docker-compose.local.yml` need no changes — both layer on the base file and inherit these via compose's own merge, per their existing header comments.
- `appsettings.json` gets a blank `Otel` section (`Exporter`/`OtlpEndpoint`/`AzureMonitorConnectionString` all `""`), matching the existing blank `Oidc`/`Ai` sections exactly — self-host/native `dotnet run` stays OTel-off until explicitly configured, same as OIDC today.

**Ask First:** none anticipated — config shape, package choice, and compose wiring were already settled with Winston/Ralf before this spec.

**Never:**
- Do not touch `infra/**` or `ARCHITECTURE-SPINE.md` — Winston already owns and has completed that half on this branch.
- Do not add a real OIDC-style secret for `Otel:OtlpEndpoint` — it is not secret, hardcode the compose hostname directly in `docker-compose.yml`, don't source it from `.env`.
- Do not remove or reconfigure the existing Serilog `WriteTo.Console()` sink.

</frozen-after-approval>

## Code Map

- `src/EnergyTracker.Api/EnergyTracker.Api.csproj` -- add OTel + Azure Monitor Distro NuGet packages
- `src/EnergyTracker.Api/Program.cs` -- composition-root wiring: read `Otel:Exporter` once, branch to `Otlp`/`AzureMonitor`/off
- `src/EnergyTracker.Api/appsettings.json` -- add blank `Otel` section (mirrors `Oidc`/`Ai`)
- `docker-compose.yml` -- new `aspire-dashboard` service; `api` gets `Otel__*` env vars + `depends_on`
- `tests/EnergyTracker.Api.Tests/` -- existing test host project; add startup test(s) for the off/Otlp/AzureMonitor branches

## Tasks & Acceptance

**Execution:**
- [x] `src/EnergyTracker.Api/EnergyTracker.Api.csproj` -- add `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.EntityFrameworkCore`, `OpenTelemetry.Instrumentation.Runtime`, `Azure.Monitor.OpenTelemetry.AspNetCore` -- current GA packages for the two exporter paths plus instrumentation libraries
- [x] `src/EnergyTracker.Api/Program.cs` -- read `Otel:Exporter` once at composition root, before the `UseSerilog` call (its `writeToProviders` argument depends on this value: `true` only when `Otel:Exporter == "Otlp"`, `false` otherwise); when `AzureMonitor`, call `builder.Services.AddOpenTelemetry().UseAzureMonitor(o => o.ConnectionString = otelAzureMonitorConnectionString)` (traces + metrics only — no OTel logs pipeline registered on this path, see Design Notes); when `Otlp`, register `AddOpenTelemetry()` with `.WithTracing(...)`/`.WithMetrics(...)` adding ASP.NET Core/HttpClient/EF Core/Runtime instrumentation and an OTLP exporter pointed at `Otel:OtlpEndpoint`, plus `builder.Logging.AddOpenTelemetry(o => o.AddOtlpExporter(...))`; anything else (including unset) registers nothing -- matches the existing `oidcConfigured`-style conditional registration so unconfigured OTel never breaks other routes
- [x] `src/EnergyTracker.Api/appsettings.json` -- add `"Otel": { "Exporter": "", "OtlpEndpoint": "", "AzureMonitorConnectionString": "" }`
- [x] `docker-compose.yml` -- add `aspire-dashboard` service (image `mcr.microsoft.com/dotnet/aspire-dashboard:9.1`, pinned; unsecured for local dev; ports `18888:18888` and `18889:18889`); add `Otel__Exporter: Otlp` and `Otel__OtlpEndpoint: http://aspire-dashboard:18889` to `api`'s environment; add `aspire-dashboard` to `api`'s `depends_on`
- [x] `tests/EnergyTracker.Api.Tests/` -- add a test verifying the app still starts and `/health` still returns 200 with `Otel:Exporter` unset (regression guard for the "must not throw" rule above)

**Acceptance Criteria:**
- Given `Otel:Exporter` is unset, when the app starts, then it starts normally and `/health` returns 200 (no exception, no OTel services registered) -- verified: `OtelConfigurationTests` (null/empty/unrecognized cases) + full `dotnet test` run, 115/115 passed
- Given `Otel:Exporter=Otlp` and `Otel:OtlpEndpoint` set, when a request hits any endpoint, then a trace/span is exported via OTLP and visible in the Aspire Dashboard (manual local check) -- verified: live `docker compose up` smoke test, `/health` traffic generated, zero OTLP export errors in `api` logs, dashboard reachable and unsecured; the dashboard's own trace view is a Blazor SPA not scriptable via `curl` -- visual confirmation left for a human glance at `http://localhost:18888`, stack left running for that
- Given `docker compose up`, when the stack starts, then the Aspire Dashboard is reachable at `http://localhost:18888` and shows live traces from `api` traffic -- verified reachable + unsecured (see above); live-trace-count assertion not automatable, same caveat

## Spec Change Log

- **Finding:** the frozen intent's illustrative env var `Dashboard__Frontend__AuthMode=Unsecured` for disabling the standalone Aspire Dashboard's auth doesn't exist on this image. Doc research first suggested `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS`, which was also empirically wrong (dashboard still redirected to `/login` in a live `docker compose up` test). **Amendment:** `docker-compose.yml` uses `DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true`, confirmed via a direct `docker run` test against `mcr.microsoft.com/dotnet/aspire-dashboard:9.1` returning `200` with no login redirect. **Avoids:** shipping a config value that silently leaves the local dashboard behind a login wall. **KEEP:** verify env-var-driven container behavior empirically, not from docs/first answers alone, when the exact image/tag isn't independently confirmed.
- **Finding:** a live `docker compose up` smoke test showed every request logged twice in `api`'s console output — once in Serilog's own format, once in the default ASP.NET Core Console-provider format — only on the `Otlp` path (`writeToProviders: true`). Root cause: `WebApplicationBuilder` registers a default Console/Debug/EventSource/EventLog provider set that stays inert (never invoked) while `writeToProviders` is `false`, but `true` forwards Serilog's events to *every* registered `ILoggerProvider`, not just the intended OTel one — waking that dormant default Console provider too. **Amendment:** added `builder.Logging.ClearProviders()` unconditionally right after `WebApplicationBuilder.CreateBuilder`, before any Otel/Serilog wiring, so Serilog's `WriteTo.Console()` stays the sole console sink regardless of `Otel:Exporter`. **Avoids:** doubled console log lines in local dev once OTel logs are wired up. **KEEP:** the composition-root ordering (`ClearProviders` → read `Otel:Exporter` → `UseSerilog` → OTel registration) — reordering any of these re-opens the same bug.
- **Review loop (Blind Hunter + Edge Case Hunter, both independently converging on the same core issues).** Findings triaged as patches (all had exactly one correct fix, no spec ambiguity) and applied in this pass:
  1. **Case-sensitivity bug:** `writeToProviders` compared `otelExporter` case-insensitively but the `switch` was ordinal — `Otel:Exporter=otlp`/`OTLP` silently disabled all telemetry with zero error. **Amendment:** `otelExporter` is now normalized once (`.Trim().ToLowerInvariant()`) at the point it's read, mirroring `databaseProvider.ToLowerInvariant()`'s existing convention elsewhere in this file; every downstream comparison (`writeToProviders`, the `switch`) now agrees by construction.
  2. **Startup crash on blank `Otel:OtlpEndpoint`:** confirmed empirically — `new Uri("")` throws `UriFormatException`, and a scheme-less value like `"aspire-dashboard:18889"` parses without error into a bogus URI (host parsed as scheme) instead of failing loudly. **Amendment:** guarded with `Uri.TryCreate(..., UriKind.Absolute, ...)` plus an explicit http/https scheme check; anything else skips Otlp registration, same graceful-degrade shape as unset/unrecognized `Otel:Exporter`.
  3. **Startup crash on blank `Otel:AzureMonitorConnectionString`:** confirmed empirically (see Design Notes below) — this contradicted this doc's own original assumption that the Distro degrades gracefully. **Amendment:** guarded with an explicit non-empty check before calling `UseAzureMonitor`; blank now skips registration instead of crashing the whole app.
  4. **`AzureMonitor` branch was missing runtime metrics** (`AddRuntimeInstrumentation()`) that the `Otlp` branch had, contradicting the frozen spec's blanket "Always" instrumentation bullet. **Amendment:** added `.WithMetrics(m => m.AddRuntimeInstrumentation())` to the `AzureMonitor` branch too.
  5. **`aspire-dashboard`'s host ports (`18888`/`18889`) bound to all interfaces** (Docker's default), directly contradicting the "compose-network-internal only" claim in both the code comment and `ARCHITECTURE-SPINE.md` — combined with anonymous auth, this meant an open, unauthenticated OTLP receiver reachable from the self-hoster's LAN (this file doubles as the self-host reference deployment, AD-13). **Amendment:** bound both ports to `127.0.0.1` explicitly, matching `docker-compose.local.yml`'s existing pattern for host-published local-only ports; corrected the comment and `ARCHITECTURE-SPINE.md`'s AD-19 extension text to match. Verified via `docker inspect` after the fix.
  6. **`ARCHITECTURE-SPINE.md` said Azure exports "traces/metrics/logs"** but the code (and this doc's own Design Notes below, from spec-drafting) deliberately keeps logs off that path. **Amendment:** corrected the architecture doc's wording to "traces + metrics only (not logs)" with the same double-ingestion rationale already captured in Design Notes.
  - Regression tests added for findings 1–3 in `OtelConfigurationTests.cs` (case/whitespace variants, invalid-endpoint variants, blank-AzureMonitor-connection-string). **Rejected** (not fixed): no healthcheck added for `aspire-dashboard`'s `depends_on` — the image ships with no shell and no curl (`docker run --entrypoint sh ... ` fails with "executable file not found"), so a practical healthcheck isn't achievable without disproportionate extra work for a telemetry-only, self-correcting (OTLP exporters retry) race. **Deferred** (see `deferred-work.md`): richer OTel resource attributes (`serviceVersion`, `deployment.environment`) — real future value, out of scope for this spec.

## Design Notes

`Otel:Exporter=AzureMonitor`'s connection-string handling *is* now exercised by an automated test (`App_starts_with_AzureMonitor_exporter_and_blank_connection_string`) — added during the review loop above after discovering empirically that a blank connection string crashes startup, contradicting this note's original assumption that the Distro degrades gracefully on its own. The full happy-path (a real, valid connection string actually exporting to App Insights) remains untested here — that requires a live App Insights instance, which only exists once Winston's Bicep deploys; trust the Distro's own GA test coverage for that part.

**Why Azure gets no OTel logs pipeline (evaluated and rejected: making OTel logging the sole log source, replacing Serilog):** the double-ingestion risk isn't caused by Serilog specifically — Container Apps forwards *any* stdout content to the same Log Analytics workspace regardless of which library wrote it, so swapping Serilog for an OTel console exporter wouldn't remove the collision, it would just relabel it. It would also couple the self-host `docker logs` baseline to `Otel:Exporter`, breaking AD-19's "unset = still logs to stdout" guarantee (self-hosters who never configure OTel would go silent). The one-line fix — gate `writeToProviders` on the exporter choice — solves the actual problem (double LAW ingestion) without touching that invariant or Serilog's already-proven console formatting.

## Verification

**Commands:**
- `dotnet restore EnergyTracker.sln && dotnet build EnergyTracker.sln --no-restore --configuration Release` -- expected: builds clean
- `dotnet test EnergyTracker.sln --no-restore --configuration Release` -- expected: all tests pass, including the new startup regression test

**Manual checks (if no CLI):**
- `docker compose up` then open `http://localhost:18888` -- Aspire Dashboard loads unsecured and shows traces after hitting `/health` or any `/api/*` route

## Suggested Review Order

**Exporter selection (composition root)**

- Entry point — one config value read once, normalized to lower-invariant so every downstream comparison agrees (fixes a case-sensitivity bug caught in review).
  [`Program.cs:41`](../../src/EnergyTracker.Api/Program.cs#L41)

- `writeToProviders` is gated on the exporter choice, not just true/false — this is *why* logs stay off the Azure path (double-ingestion against the shared cap).
  [`Program.cs:56`](../../src/EnergyTracker.Api/Program.cs#L56)

- `ClearProviders()` runs before any of the above — without it, `writeToProviders:true` wakes ASP.NET Core's dormant default Console provider and doubles every log line (found via live smoke test).
  [`Program.cs:32`](../../src/EnergyTracker.Api/Program.cs#L32)

**Azure path — traces + metrics only**

- Guarded against a blank connection string, which crashes startup outright (confirmed empirically, not assumed) rather than degrading gracefully like every other unset-config case.
  [`Program.cs:60`](../../src/EnergyTracker.Api/Program.cs#L60)

- `UseAzureMonitor` call itself — bundles ASP.NET Core/HttpClient; EF Core and runtime metrics added explicitly since the Distro doesn't cover them.
  [`Program.cs:75`](../../src/EnergyTracker.Api/Program.cs#L75)

**Local/self-host path — traces + metrics + logs via OTLP**

- Endpoint validated as an absolute http/https URI before use — an unguarded `new Uri()` here used to crash on a blank or scheme-less value.
  [`Program.cs:89`](../../src/EnergyTracker.Api/Program.cs#L89)

- Three exporter registrations (traces/metrics/logs) share the one validated `otlpEndpointUri` — trace pipeline.
  [`Program.cs:98`](../../src/EnergyTracker.Api/Program.cs#L98)

**Local dev stack — Aspire Dashboard**

- New service; host ports bound to `127.0.0.1` explicitly, not left at Docker's all-interfaces default — this file doubles as the self-host reference deployment, so an unsecured dashboard on `0.0.0.0` would be LAN-reachable.
  [`docker-compose.yml:31`](../../docker-compose.yml#L31)

- `api` service's Otel env vars pointing at the dashboard container by compose-network hostname.
  [`docker-compose.yml:17`](../../docker-compose.yml#L17)

**Peripherals**

- New NuGet packages, including the one still-beta EF Core instrumentation package (documented why it's accepted anyway).
  [`EnergyTracker.Api.csproj:14`](../../src/EnergyTracker.Api/EnergyTracker.Api.csproj#L14)

- Blank `Otel` config defaults, matching the existing `Oidc`/`Ai` blank-section pattern.
  [`appsettings.json:17`](../../src/EnergyTracker.Api/appsettings.json#L17)

- Regression tests for every bug found in review (case/whitespace variants, invalid endpoints, blank Azure connection string) plus the original unset/unrecognized-exporter guard.
  [`OtelConfigurationTests.cs:1`](../../tests/EnergyTracker.Api.Tests/OtelConfigurationTests.cs#L1)
