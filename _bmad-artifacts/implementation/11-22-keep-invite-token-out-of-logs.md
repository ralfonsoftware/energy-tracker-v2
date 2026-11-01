---
baseline_commit: fbdddac
---

# Story 11.22: Keep the Invite Token Out of Logs

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- Epic 11 "Codebase audit hardening" (planning/epics/epic-11-codebase-audit-hardening.md), tier "Triage follow-ups", promoted from deferred-work.md on 2026-10-31 (Story 11.9 dev-story entry, AC #11d; index row line ~131, body line ~560). Origin: audit S10 follow-up. Architecture: AD-26 (invite tokens, "no log line may carry the presented token"), AD-19 (structured Serilog logging to stdout, OTel extension, ADR 0001), AD-27 (HTTP hardening). This file cites the AD IDs, which satisfies the spec-per-fix gate (project-context.md). -->
<!-- Branch: feature/11-22-invite-token-logs (current checkout, clean, at fbdddac). ONE PR. No migration, no new runtime NuGet or npm package. -->
<!-- ASK FIRST DECIDED 2026-11-01: Ralf chose option A2 (Serilog enricher redacting invite segments in log properties, plus trace-span redaction), matching the create-story recommendation. Ralf also added Task 2.5 / AC #8: rewrite the `Logging` section of appsettings.json and appsettings.Development.json to the Serilog configuration schema. Options A1 and B rejected; copy into Completion Notes when the story starts. AC #1 is marked AMENDED below. -->
<!-- No Auth0/Chrome live gate for A1/A2: no auth flow, claim or browser behaviour changes. Option B changes the invite link and the OIDC round trip, so it WOULD need the live Auth0/Chrome gate. A cheap post-deploy log check by Ralf is in Task 6. -->

## Story

As a household owner,
I want invite tokens kept out of request-path logs and telemetry,
so that a log reader cannot join my household (promoted from `deferred-work.md`, Story 11.9 dev-story entry, AC #11d).

## Acceptance Criteria

1. **AMENDED 2026-11-01, Ralf: option A2 chosen (Serilog enricher + span redaction); A1 and B rejected; `appsettings` log config moves to the Serilog schema (AC #8).** Choice, date and rejected options are recorded in this file's header comment and copied to Completion Notes when the story starts. ~~**Ask First (decision before development).** **Given** the options in Dev Notes "Decision table", **when** the story starts, **then** Ralf picks one, and the choice, date and rejected options are recorded in this file's header comment and Completion Notes, and this AC is marked `AMENDED <date, decision>` with the old text struck through. No code before this.~~
2. **Given** the effective logging configuration (before AC #8: no `Serilog` section, `Information` level, hosting diagnostics `Request starting` / `Request finished` writing `Path` and `QueryString`), **when** a request hits `GET /join/<token>`, `GET /api/household-invites/<token>`, `POST /api/household-invites/<token>/accept` or `GET /login?returnUrl=%2Fjoin%2F<token>` (and the OIDC callback's redirect back to `/join/<token>`), **then** no stdout log line and no Serilog event property contains `<token>`. Request lines stay (option A2): the path shows a fixed marker instead (`/join/{token}`), and method, status and elapsed time stay in the line.
3. **Given** `Otel:Exporter` is `azuremonitor` or `otlp`, **when** the same requests run, **then** the exported server span (`url.path`, `url.query`, `url.full`, `http.target`, `http.url` where present) no longer carries the token, on **both** exporter branches of `ObservabilityRegistration`. `http.route` (the template `/api/household-invites/{token}`) is not secret and stays.
4. **Given** the chosen option, **when** it ships, **then** a test proves the redaction against the real host (captured log events and exported activities for the four requests of AC #2), and the proof is non-vacuous: the same test shows the request lines/spans exist and carry the marker, and a known-bad sample proves the redactor rewrites a raw token.
5. **Implied (the invite wire contract and request logging stay):** `POST /api/household-invites` still returns the plaintext token once, `GET/POST /api/household-invites/{token}[/accept]` and `/join/{token}` answer exactly as before (200/404/409, same rate limits), and non-invite request lines are unchanged. Only the invite segment is rewritten.
6. **Implied (a new token route cannot ship unredacted):** a guard fails when any route in the endpoint data source has a `{token}` path parameter that the redactor leaves intact (it enumerates `EndpointDataSource`, fills `{token}` with a random 43-character base64url value, runs the redactor), plus the SPA route `/join/{token}`. Known-bad sample proves it non-vacuous. Same idea as `RequestedWithRoutesTests` / `RateLimitedRoutesTests`.
7. **Implied (docs and project memory):** `project-context.md` invite paragraph (AD-26) says the redaction exists and names its guard test; a short `docs/adr/0008-invite-token-redaction-in-logs-and-traces.md` (Context / Decision / Consequences, `// ADR 0008` pointer in code) records the chosen option and the rejected ones; the two `deferred-work.md` entries tagged `[promoted: 11.22]` / `[promoted: Story 11.22]` (index row ~131, body ~560) are deleted (resolved by this story, convention in `project-context.md`); `ADR 0001`'s Consequences gain one line pointing at 0008; the AD-26 line in `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md` gets a short `[AMENDED <date>, Story 11.22]` note. `docs/codebase-audit-2026-10-10.md` is not edited (dated record).

8. **Added (AMENDED 2026-11-01, Ralf: rewrite log config to the Serilog schema).** **Given** `ReadFrom.Configuration` reads only a `Serilog` section and the `Logging:LogLevel` blocks in `src/EnergyTracker.Api/appsettings.json` and `appsettings.Development.json` are ignored, **when** this story is done, **then** both files carry no `Logging` section and instead a `Serilog:MinimumLevel` section (`Default` plus `Override`), so the levels written in the file are the levels in effect. Request lines stay at `Information` for `Microsoft.AspNetCore.Hosting.Diagnostics` (needed by AC #2/#5); see Dev Notes "Serilog configuration schema" for the exact content and the behaviour change it brings. A test proves the effective levels.

## Tasks / Subtasks

Red first for every behaviour change; record runs in Debug Log References (Story 11.1-11.21 practice). Docker must be running for Api tests (Testcontainers). One PR on `feature/11-22-invite-token-logs`.

- [x] Task 0: Ask First (AC: #1). Decided 2026-11-01: A2 (see header comment). Baseline (still to run when the story starts): `dotnet build EnergyTracker.sln -c Release --no-incremental -p:TreatWarningsAsErrors=true` (0 warnings), `dotnet format EnergyTracker.sln --verify-no-changes --no-restore`, `dotnet test` per project; record counts (11.21 close is the reference).
- [ ] Task 1: Redactor and guard tests first, red (AC: #2, #4, #6)
  - [ ] 1.1 `tests/EnergyTracker.Api.Tests/InviteTokenRedactorTests.cs` (pure, no Docker): `/join/<t>`, `/join/<t>/`, `/api/household-invites/<t>`, `/api/household-invites/<t>/accept`, `?returnUrl=%2Fjoin%2F<t>` (upper and lower case `%2f`), full URLs (`http://host/join/<t>?x=1`), double-encoded `%252F`, trailing punctuation, text with the token mid-sentence. Non-invite input (`/api/meter-readings`, `/health`, `/api/household-invites` with no segment, `/api/household-members/<guid>`) returns unchanged. Idempotent (`Redact(Redact(x)) == Redact(x)`).
  - [ ] 1.2 Real-host log test (`tests/EnergyTracker.Api.Tests/InviteTokenIsNotLoggedTests.cs`, `EnergyTrackerApiFactory`): register a capturing `ILogEventSink` via `ConfigureTestServices` (the `UseSerilog` lambda already calls `.ReadFrom.Services(services)`, which picks up every `ILogEventSink` in DI). Fire the four requests of AC #2 with a known 43-char token. Assert (a) hosting `Request starting`/`Request finished` events exist for each, (b) no event's `RenderMessage()` or any scalar property value contains the token, (c) the rendered path contains the marker (A2) or the events are absent (A1).
  - [ ] 1.3 Real-host trace test: on the `otlp` path (`Otel:Exporter=otlp`, `Otel:OtlpEndpoint=http://localhost:18889`, as `OtelConfigurationTests` does) add a capturing exporter after the app's pipeline (`ConfigureTestServices(s => s.AddOpenTelemetry().WithTracing(t => t.AddProcessor(new SimpleActivityExportProcessor(new CapturingExporter(list)))))`, no new package). Assert exported server activities for the four requests have no tag value containing the token and `url.path` shows the marker.
  - [ ] 1.4 Guard (AC #6): `InviteTokenRoutesAreRedactedTests` over `EndpointDataSource` (route patterns with `{token}`) plus the literal SPA pattern. Known-bad sample: a deliberately wrong redactor (or an unredacted route string) makes the same assertion fail, proving the test can fail.
  - [ ] 1.6 Config test (AC #8, red first): `SerilogConfigurationTests` builds the host (Production-like and Development) and asserts through captured events/`LoggerConfiguration` behaviour that (a) `Microsoft.AspNetCore.Hosting.Diagnostics` `Information` events are emitted, (b) a `Microsoft.AspNetCore.*` `Information` event from another category (e.g. `Microsoft.AspNetCore.Routing`) is not, (c) a `Microsoft.EntityFrameworkCore.Database.Command` `Information` event behaves as decided in Dev Notes. Plus a file guard: neither `appsettings*.json` contains a top-level `Logging` key, both contain `Serilog:MinimumLevel`. Known-bad sample: the old `Logging`-only JSON fails the file guard.
  - [ ] 1.5 Structural guard: `ObservabilityRegistration.cs` calls the span redaction on **both** the `azuremonitor` and the `otlp` branch (source scan like the other composition guards; the Azure branch cannot run an exporter in tests, so this is its automated proof).
- [ ] Task 2: Production code (AC: #2, #3, #5). Files in Dev Notes "File structure".
  - [ ] 2.1 `InviteTokenRedactor` (Api, one static class, compiled `Regex`, one place that knows invite path shapes). Fail closed: redact the whole segment after `/api/household-invites/` (and after `/join/`) whatever its content, including `{id:guid}` revoke ids (a GUID in a log is not secret but the cost of one more rule is not worth it; say so in the XML summary).
  - [ ] 2.2 Log side per chosen option (A2: `InviteTokenRedactingEnricher : ILogEventEnricher` added in `ObservabilityRegistration` before `.WriteTo.Console`, rewrites every `ScalarValue` string property through the redactor, not only `Path`/`QueryString`; A1: `MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", Warning)` in code, not in config). Keep `writeToProviders` and `ClearProviders` logic untouched (ADR 0001).
  - [ ] 2.3 Trace side: `AddInviteTokenRedaction(this TracerProviderBuilder)` used on both branches. Prefer the ASP.NET Core instrumentation `EnrichWithHttpRequest` hook (runs after the url tags are set, before any exporter, no ordering trap); if the Azure Monitor distro owns that options object and the hook cannot be set, use a `BaseProcessor<Activity>` and register it **before** `UseAzureMonitor`'s exporter (an `OnEnd` processor added after the batch exporter mutates the activity after it was queued: racy). Record what worked and why in the ADR.
  - [ ] 2.5 Rewrite `src/EnergyTracker.Api/appsettings.json` and `appsettings.Development.json` (AC #8): remove `Logging`, add `Serilog:MinimumLevel` per Dev Notes "Serilog configuration schema". Keep the sinks in code (`WriteTo.Console` stays in `ObservabilityRegistration`; AD-19: no environment branching, so no `Serilog:WriteTo` in config). Check `.env.example`, `docker-compose.yml`, `scripts/run-api.sh`, Bicep env blocks for `Logging__*`: none exist at `fbdddac` (verified by grep), add none.
  - [ ] 2.4 `grep -rn "Request starting\|url\.path\|UseSerilogRequestLogging" src` for any other request-path sink; add none.
- [ ] Task 3: Re-run `ServiceRegistrationSnapshotTests` (expect no change; the enricher is built inline, the processor/hook is not a `ServiceDescriptor`). If a descriptor appears, review the diff and approve with `ENERGYTRACKER_UPDATE_SNAPSHOTS=1` (AC: #5).
- [ ] Task 4: Docs (AC: #7). ADR 0008, `project-context.md`, invariants-rules.md note, delete the two `deferred-work.md` entries, ADR 0001 pointer, `Last Updated` line in `project-context.md`.
- [ ] Task 5: Verify and PR (AC: all). `dotnet format EnergyTracker.sln`, rerun the Task 0 gate, full `dotnet test` per project, `web` untouched (A1/A2) so no frontend run needed. Record counts and delta. Open one PR against `main`.
- [ ] Task 6: Post-deploy check (Ralf, the dev does not run it). After merge and the App Deploy run: create an invite, open `/join/<token>` once, preview and accept with a second account if available, then query Log Analytics (adapt table and column names to the workspace): `ContainerAppConsoleLogs_CL | where TimeGenerated > ago(30m) | where Log_s has "/join/" or Log_s has "household-invites" | project TimeGenerated, Log_s` and `AppRequests | where TimeGenerated > ago(30m) | where Url has "/join/" or Url has "household-invites" | project TimeGenerated, Url, Properties`. Expect the marker, never the token. Record run id and result. Story `done` only after that (the Azure Monitor branch has no automated proof beyond Task 1.5).

## Dev Notes

### Decision table (Ask First, Task 0)

| | A1: quiet hosting log + span redaction | **A2: redacting enricher + span redaction (create-story recommendation)** | B: token out of the path (fragment or POST body) |
|---|---|---|---|
| Logs | `Microsoft.AspNetCore.Hosting.Diagnostics` to `Warning`: all `Request starting/finished` lines gone, for every route | Serilog enricher rewrites invite segments in every string property; request lines stay, with `/join/{token}` | Token never reaches the server in a URL, nothing to redact |
| Traces | Span redaction needed | Span redaction needed | None needed for the token, but old links still in the path |
| Operational cost | Lose status/latency lines for all routes (blunt) | Small code, one regex, one place | Wire change: preview becomes a POST (a GET with a body is not an option), accept takes the token in the body, `/join#token` fragment, SPA reads `location.hash` |
| Blast radius | Config-level | `Api` only | Links already issued (valid up to 7 days) must keep working, so the old path stays supported for a window and keeps leaking; `returnUrl` cannot carry a fragment through the OIDC round trip, so the token needs another carrier (storage would meet `FrontendDoesNotStoreAuthTokensTests` territory); needs the live Auth0/Chrome gate |
| Residual | Query `returnUrl` and spans still need the redactor anyway | Redactor must know every invite shape (guard AC #6) | Largest change; mixed old/new links for a week |

Recommendation: **A2.** It keeps the one operational signal the logs give (who got which status), costs one class plus one registration, and A1 needs the span redactor anyway. B is the cleaner end state but changes the wire contract, the link format and the OIDC return path for a risk that redaction closes; revisit B only if invites move to a mail/sign-up flow. **Decided 2026-11-01: A2.**

### Where the token reaches a log today (verified at `fbdddac`)

- `src/EnergyTracker.Api/Composition/ObservabilityRegistration.cs`: `builder.Host.UseSerilog(... .ReadFrom.Configuration ... .WriteTo.Console ...)`. `appsettings.json` has only `Logging:LogLevel` (`Microsoft.AspNetCore: Warning`), which **Serilog ignores** after `ClearProviders()`; there is no `Serilog` section. So the minimum level is `Information` with no override and `Microsoft.AspNetCore.Hosting.Diagnostics` writes `Request starting ...{Path}{QueryString}` and `Request finished ...` for every request to stdout, hence Log Analytics. Story 11.9's rehearsal counted 29 such lines (9 `GET /join/<token>`, 14 `GET /api/household-invites/<token>`, 6 `POST .../accept`).
- `GET /login?returnUrl=%2Fjoin%2F<token>`: `web/src/App.tsx:239` builds it from `window.location.pathname` for the unauthenticated invite flow; the query string is logged the same way. After sign-in the OIDC callback redirects to `/join/<token>` (another logged request).
- Azure Monitor / OTLP server spans: the ASP.NET Core instrumentation sets `url.path`, `url.query` (the library redacts only a few signature-style query keys by default, not `returnUrl`), and the Azure exporter maps them to `AppRequests.Url`. In this repo the `azuremonitor` branch relies on the distro's own ASP.NET Core instrumentation; the `otlp` branch adds `.AddAspNetCoreInstrumentation()` itself.
- Not a log: `Referer` (CSP/`Referrer-Policy: same-origin` already scope it), browser history, and the invite link itself, which is the credential and is meant to be shared.
- The `Logging:LogLevel` blocks in both `appsettings` files are dead (Serilog ignores them). AC #8 / Task 2.5 replaces them with the Serilog schema.

### Serilog configuration schema (AC #8, Task 2.5)

Both files (`appsettings.Development.json` carries only this section today, with the same levels, so the two files get the same block):

```json
"Serilog": {
  "MinimumLevel": {
    "Default": "Information",
    "Override": {
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.AspNetCore.Hosting.Diagnostics": "Information"
    }
  }
}
```

Why the second override: `Microsoft.AspNetCore: Warning` written naively would also silence `Microsoft.AspNetCore.Hosting.Diagnostics` (Serilog picks the longest matching prefix, so the explicit `Information` entry wins) and kill the request lines that AC #2/#5 keep. The block therefore preserves request lines and changes only the **other** `Microsoft.AspNetCore.*` categories, which today log at `Information` and will go quiet (e.g. routing/endpoint-execution, authentication challenge/sign-in lines). That is the one behaviour change; the dev lists what disappears in Completion Notes from a before/after run, and asks Ralf if a line looks operationally useful. `Microsoft.EntityFrameworkCore` is not in the old config, so its `Information` command lines keep appearing; do not add an override for it in this story (Ralf decides separately if the SQL noise should go). `ReadFrom.Configuration` order in `ObservabilityRegistration` stays first, so code-level settings added later still win.

### Current state of the files being changed (read before editing)

- `src/EnergyTracker.Api/Composition/ObservabilityRegistration.cs`: `ClearProviders`, `otelExporter` normalized once, `UseSerilog` lambda (`ReadFrom.Configuration`, `ReadFrom.Services`, `Enrich.FromLogContext`, `WriteTo.Console(InvariantCulture)`, `writeToProviders: otelExporter == "otlp"`), then the `azuremonitor` and `otlp` switch branches. Must preserve: ADR 0001 behaviour (no double console line, degrade-to-off on blank connection string or bad endpoint), `OtelConfigurationTests`.
- `src/EnergyTracker.Api/Endpoints/HouseholdInviteEndpoints.cs`: routes `POST/GET /household-invites`, `DELETE /household-invites/{id:guid}`, `GET /household-invites/{token}` (preview, rate limit `InviteRedeem`), `POST /household-invites/{token}/accept`. No change expected. `InviteToken.IsPlausible` / `MaxLength` / hashing untouched.
- `src/EnergyTracker.Api/Endpoints/AuthEndpoints.cs`: `/login` with `returnUrl` and `IsSafeLocalReturnUrl`. Untouched; the query string is redacted at the log/span edge, not by changing the URL.
- `web/src/App.tsx` (`INVITE_PATH_PATTERN`, line ~37; unauthenticated redirect ~239): untouched under A1/A2.
- `tests/EnergyTracker.Api.Tests/Snapshots/service-registrations.*.txt` pin the ordered DI descriptors per exporter; see Task 3.

### File structure requirements

- New: `src/EnergyTracker.Api/Composition/InviteTokenRedactor.cs` (or `src/EnergyTracker.Api/InviteTokenRedactor.cs`; follow where `SecurityHeaders.cs` / `HttpRateLimiting.cs` sit, flat in `Api`; the enricher and the trace hook may live in the same folder as `ObservabilityRegistration.cs`, one type per file), `docs/adr/0008-invite-token-redaction-in-logs-and-traces.md`.
- Updated: `ObservabilityRegistration.cs`, the three test files named in Task 1, `project-context.md`, `deferred-work.md`, ADR 0001, `invariants-rules.md`.
- Tests: `Snake_case` names, one class per file named `{Subject}Tests`, `Shouldly`, `TestContext.Current.CancellationToken`, shared Postgres server via `EnergyTrackerApiFactory`/`PostgresTestDatabase` (never `new PostgreSqlBuilder`).

### Architecture compliance

- **AD-26 (and the project-context invite paragraph):** "no exception message, log line, or response may carry presented token". This story makes that true for the request path and the span, not only for exceptions. The token still arrives in the path and is hashed in the repository; do not log it, its hash or its length anywhere new.
- **AD-19:** logging stays structured Serilog to stdout, **no environment branching** in logging code: the redaction runs in every environment, Development included (so tests run it as-is).
- **ADR 0001 and config schema:** ADR 0001's Consequences gain one line: log levels live in the `Serilog` section, `Logging:LogLevel` is not read. `ClearProviders` first, `otelExporter` read once, `writeToProviders` only for `otlp`, Azure Monitor gets no forwarded logs. Do not touch these.
- **AD-3 / AD-24:** no new `ConcurrentDictionary`, `SemaphoreSlim`, `Channel`, `IMemoryCache`, `AddRateLimiter`, `Path.GetTempPath` (`SingleInstanceInvariantIsDocumentedTests`). The redactor is a static, stateless regex.
- **Time/clock (11.17):** no clock reads.
- **Comment rule (11.19):** comments state why in 1-3 lines; longer reasoning goes to the ADR with a `// ADR 0008` pointer. `NoHistoryNarratingCommentsTests` holds files touched by 11.19; run the Architecture suite.
- **Analyzer gate (11.18):** build with `-p:TreatWarningsAsErrors=true`, run `dotnet format EnergyTracker.sln` before pushing. `Regex` use needs a match timeout and `GeneratedRegex` or `RegexOptions.Compiled` per the `Recommended` rule set (CA/SYSLIB regex rules); log injection analyzers apply (`AnalysisModeSecurity=All`). No `#pragma warning disable`, no `NoWarn`.
- **No package:** `Serilog.Core`/`Serilog.Events` ship with `Serilog.AspNetCore`; `OpenTelemetry.Trace`/`System.Diagnostics.Activity` are already referenced. Do not add `OpenTelemetry.Exporter.InMemory`; write the 10-line capturing exporter in the test.

### Library / framework notes

- Serilog.AspNetCore 10.0.0, Serilog.Sinks.Console 6.1.1; OpenTelemetry.Instrumentation.AspNetCore 1.20.1; Azure.Monitor.OpenTelemetry.AspNetCore 1.6.0 (`Directory.Packages.props`). Check the instrumentation options API at the pinned version before writing the hook (`AspNetCoreTraceInstrumentationOptions.EnrichWithHttpRequest`); do not assume from memory.
- `Microsoft.AspNetCore.Hosting.Diagnostics` messages reach Serilog as structured state; verify in the Task 1.2 test which property names carry `Path`/`QueryString` at .NET 10 rather than assuming. The enricher scans all string scalars so a rename cannot reopen the leak.
- `LogEvent.AddOrUpdateProperty` with a `ScalarValue` replaces a property; the rendered message is built from properties at render time, so redacted properties redact the message too. Verify with `RenderMessage()` in the test.

### Testing requirements

- Pure redactor tests (no Docker) for every path shape and encoding in 1.1; real-host tests for log and trace; route-enumeration guard; structural guard that both exporter branches call the trace redaction.
- Known-bad samples in the guard tests (an unredacted string must fail the assertion) so none is vacuous.
- No sleeps: `SimpleActivityExportProcessor` exports synchronously on `OnEnd`; flush the Serilog sink by reading the in-memory list after `await response`.
- Do not skip or loosen any existing test. `OtelConfigurationTests`, `ServiceRegistrationSnapshotTests`, `SecurityHeadersTests`, `RateLimitedRoutesTests`, `HouseholdInviteTests` must stay green unchanged.

### Previous story intelligence

- **11.9** (revocable hashed invites): wrote the `deferred-work.md` entry and the 29-line count; its AC #11c makes unknown/implausible/over-long tokens answer the same 404, relevant to AC #5 (do not add a distinguishable response). It also fixed "no exception message, log line, or response may carry presented token".
- **11.20** (drop plaintext token column, latest invite story): hash-only lookup, `FindInviteByTokenAsync` unchanged by this story. Its own Ask First/AMENDED trail is the format to copy.
- **11.21** (latest, self-host migration): Ask First recorded as a header comment plus `AMENDED` ACs plus a Decision table; copy that shape. Create-story's recommendation was rejected there, so wait for Ralf's answer.
- **11.18** (analyzer gate): regex, string and logging analyzers fail CI; build with the CI flags locally.
- **ADR 0001**: every change to `ObservabilityRegistration` must respect `writeToProviders`/`ClearProviders`; add a Consequences line, not a rewrite.

### Git intelligence

Last commits: `fbdddac` (close 11.21, #165), `e7cc3c6` (Story 11.21, #164), `4e1c596` (close 11.20, #163). Working tree clean on `feature/11-22-invite-token-logs`. Commit style: `feat:`/`fix:`/`docs:` conventional with story and audit id in the subject (e.g. `fix: keep the invite token out of logs and traces (Story 11.22, audit S10) (#NNN)`).

### Latest technical notes

No new package. Verify the instrumentation options and the Azure Monitor distro's processor ordering against the pinned versions in the Task 2.3 spike before writing production code, and put the result in ADR 0008 (this is the one unknown in the story). If neither the enrich hook nor an early processor can be made to run before the Azure exporter, stop and ask Ralf: the fallback is dropping the distro's server-span URL via a sampler/filter for the invite routes, which loses those requests from `AppRequests`.

### Out of scope

Moving the token out of the URL (Option B, rejected); silencing EF Core SQL command lines; redacting other identifiers (member names, household ids); a general PII scrubber; changing invite lifetime or the 404/409 responses; the browser's own history and `Referer`.

### Project Structure Notes

All paths above exist at `fbdddac` except the new files. No migration, no frontend change under A1/A2. Under B the frontend (`App.tsx`, `invite-accept-form.tsx`, `invite-generate-panel.tsx`, their tests), the invite endpoints and `AuthEndpoints` change and a live Auth0/Chrome check becomes mandatory (project-context.md process gate).

### References

- [Source: _bmad-artifacts/planning/epics/epic-11-codebase-audit-hardening.md#Story 11.22]
- [Source: _bmad-artifacts/implementation/deferred-work.md ~lines 131, 560] (entries to delete at the end)
- [Source: _bmad-artifacts/implementation/11-9-member-removal-revocable-invites.md] (AC #11d, log-line count)
- [Source: _bmad-artifacts/implementation/11-21-self-host-migration-path.md] (Ask First / AMENDED format)
- [Source: docs/adr/0001-serilog-and-default-logging-providers.md]
- [Source: _bmad-artifacts/project-context.md#Member trust and invites, #Operational baseline (AD-19), #Amend ACs at decision time]
- [Source: src/EnergyTracker.Api/Composition/ObservabilityRegistration.cs, src/EnergyTracker.Api/Endpoints/HouseholdInviteEndpoints.cs, src/EnergyTracker.Api/Endpoints/AuthEndpoints.cs, web/src/App.tsx]

## Dev Agent Record

### Agent Model Used

Sonnet 5.5 (claude-sonnet-5-5)

### Debug Log References

### Completion Notes List

### File List
