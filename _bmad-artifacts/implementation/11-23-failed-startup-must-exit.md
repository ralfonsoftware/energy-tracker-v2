# Story 11.23: Failed Startup Must Exit

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- Epic 11 "Codebase audit hardening" (planning/epics/epic-11-codebase-audit-hardening.md), tier "Triage follow-ups", promoted from deferred-work.md on 2026-10-31 (Story 11.11 dev-story entry; index row line ~157, body line ~600). Origin: Story 11.11 Task 9.1 (f)/(g) rehearsal on OrbStack/arm64, deferred at the 11.11 code review 2026-10-23. Architecture: AD-19 (operational baseline: liveness-only `/health`, structured Serilog to stdout, Container Apps probes), AD-17 (Data Protection fail-closed throw), AD-13 (one image for Azure and self-host). This file cites the AD IDs, which satisfies the spec-per-fix gate (project-context.md). -->
<!-- Branch: feature/11-23-failed-startup-exit (current checkout, clean, at 806dfad). ONE PR. No migration, no new NuGet or npm package, no frontend change. -->
<!-- TWO GATES BEFORE CODE, in this order: (1) Task 1 reproduction and finding, recorded in this file (epic AC: "first reproduces ... and records the finding before changing code"); (2) Ask First on the fix, chosen from the Decision table once the finding is in. The table's recommendation is a hypothesis that Task 1 may overturn. -->
<!-- No Auth0/Chrome live gate: no auth flow, claim or browser behaviour changes. Live Docker rehearsal (Task 1, Task 6) and one Azure check (Task 7) are Ralf-gated where they touch his machine's other hosts or his Azure subscription; see the tasks. -->

## Story

As an operator,
I want a host that throws at startup to exit non-zero,
so that restart policies and Container Apps crash-loop detection see the failure (promoted from `deferred-work.md`, Story 11.11 dev-story entry).

## Acceptance Criteria

1. **Finding first (epic AC, no code before this).** **Given** the cause is not yet known (Serilog/OTel flush on a half-built host, a host-specific `createdump` spin, or something PID-1 specific; see Dev Notes "Hypotheses"), **when** this story starts, **then** Task 1 reproduces the hang on the OrbStack/arm64 host **and** on a Linux amd64 host or Azure, separates "our code" from "the runtime" with a minimal repro that has none of our code in it, and records the finding (what spins, on which hosts, which hypothesis held) in this file's Completion Notes and in the Debug Log. If a host cannot be reached, say so and ask Ralf; do not infer from the other host.
2. **Ask First (decision before development).** **Given** the finding and the options in Dev Notes "Decision table", **when** Task 1 is recorded, **then** Ralf picks one, and the choice, date and rejected options are recorded in this file's header comment and Completion Notes, and this AC is marked `AMENDED <date, decision>` with the old text struck through. Source of truth for the code change is the chosen option, not the recommendation.
3. **Given** any `throw` out of `Program.cs` (the `ConnectionStrings:Default` check, the `Ai:Endpoint` check in `AiPlausibilityBackendConfiguration.Configure`, the Data Protection fail-closed checks in `DataProtectionKeyProtection`, the unsupported `Database:Provider` throw, and any later throw during `builder.Build()` or before `app.Run()`), **when** the host starts in the shipped image, **then** the process exits within seconds with a **non-zero** exit code (`docker inspect -f '{{.State.ExitCode}}'` is non-zero, the container is `exited`, not `running`) instead of staying `running` at 100% CPU, and the operator still sees the full error message in `docker logs` / Log Analytics (11.11 AC #2 and AC #9c are not weakened).
4. **Given** the chosen option, **when** it ships, **then** an automated test proves it non-vacuously: a real child process of the built API with a failing configuration (Production, no `ConnectionStrings:Default`) ends with the exit code the option promises and its stderr carries the message naming `ConnectionStrings__Default`; a known-bad variant (the same process without the fix) is shown red first, in the Debug Log. If the chosen option has no code (finding says "host-specific, document only"), this AC is replaced by AC #7's doc proof and marked `AMENDED` at the Ask First.
5. **Implied (the existing in-process startup tests keep their contract):** `ConnectionStringStartupTests`, `DataProtectionStartupTests`, `AiPlausibilityBackendConfigurationTests` and the other `WebApplicationFactory` tests still see the thrown exception through `Should.Throw<Exception>(() => factory.CreateClient())` with the same messages. The fix must not swallow, wrap or reword the exception on that path, and must not terminate the test process.
6. **Implied (the fix covers a throw nobody has written yet):** the exit path hangs on the process, not on a list of throw sites. A new `throw` in `Program.cs` or a registration it calls needs no extra step to exit non-zero. A guard (Architecture test, source scan like the other composition guards) pins that the fix is registered as the first statement of `Program.cs`, before `WebApplication.CreateBuilder`, with a known-bad sample proving the guard can fail.
7. **Implied (docs and project memory):** `docs/self-hosting.md` says what a failed start looks like (the container exits non-zero, the message is the last log line, restart policy applies) and how to read it; if the finding is host-specific, `docs/local-development.md` or `docs/local-vs-azure-deltas.md` names the host and the workaround. `project-context.md` "Performance/operational gotchas" gets one bullet (what exits, why, the guard test name). A short `docs/adr/0009-failed-startup-exits-non-zero.md` (Context / Decision / Consequences, `// ADR 0009` pointer in code) records the finding and the chosen option and the rejected ones, only when code ships. The two `deferred-work.md` entries tagged `[promoted: 11.23]` / `[promoted: Story 11.23]` (index row ~157, body ~600) are deleted (resolved by this story, convention in `project-context.md`). `docs/codebase-audit-2026-10-10.md` is not edited (dated record).

## Tasks / Subtasks

Red first for every behaviour change; record runs in Debug Log References (Story 11.1-11.22 practice). Docker must be running for Api tests (Testcontainers) and for the Task 1 rehearsal. One PR on `feature/11-23-failed-startup-exit`. No `Program.cs` edit before Task 2 is decided.

- [ ] Task 0: Baseline (AC: all). `dotnet build EnergyTracker.sln -c Release --no-incremental -p:TreatWarningsAsErrors=true` (0 warnings), `dotnet format EnergyTracker.sln --verify-no-changes --no-restore`, `dotnet test` per project; record counts (11.22 close is the reference, see its Debug Log: Api 502, Application 605, Architecture 199, Infrastructure 565 at the green stage).
- [ ] Task 1: Reproduce and record the finding (AC: #1). Throwaway env and container names, nothing committed, all removed afterwards (11.11 Task 9.1 practice; do not touch the real `.env`). Record every command and result.
  - [ ] 1.1 OrbStack/arm64, shipped image: `docker build -t et-repro:current .`, then `docker run -d --name et-repro-1 et-repro:current` (Production, no `ConnectionStrings__Default`; same shape as 11.11 (g)). After ~20 s: `docker logs et-repro-1`, `docker inspect -f '{{.State.Status}} {{.State.ExitCode}}' et-repro-1`, `docker stats --no-stream et-repro-1`, `docker top et-repro-1 -eo pid,ppid,stat,pcpu,comm,args`. **The `docker top` row with the high `pcpu` names the spinner** (the `dotnet` process itself, or a `createdump` child): that single line separates H2 from H1/H3.
  - [ ] 1.2 PID-1 hypothesis (H1): same image with `docker run -d --init --name et-repro-2 et-repro:current` (tini as PID 1). Exits non-zero with `--init` and spins without it: H1 holds, and Azure Container Apps (no `--init` switch) will likely show it too.
  - [ ] 1.3 Our code vs runtime (H3): a throwaway console/web project outside the repo (scratchpad, not `src/`), `dotnet new web`, `throw new InvalidOperationException("x");` as the first statement, published with the same `aspnet:10.0` base and `USER $APP_UID`, run with and without `--init`. Spins here too: runtime/container issue, Serilog/OTel are cleared. Exits here but not with our image: our code path (H4, Serilog/OTel/`AddObservability` before the throw).
  - [ ] 1.4 Crash handling (H2): rerun 1.1 with `-e DOTNET_DbgEnableMiniDump=0 -e DOTNET_EnableCrashReport=0 -e DOTNET_DbgMiniDumpName=` (11.11 tried disabling the crash report and saw no change; confirm which variable was set, since both `DOTNET_DbgEnableMiniDump` and `DOTNET_EnableCrashReport` exist) and note whether `createdump` appears in `docker top`.
  - [ ] 1.5 Second host (AC #1): a Linux amd64 host if Ralf has one; otherwise `--platform linux/amd64` under OrbStack (Rosetta/QEMU emulation, counts as weaker evidence and is labelled so), plus Task 7's Azure check. Do not call the finding "host-specific" on the strength of OrbStack alone.
  - [ ] 1.6 Write the finding (which of H1-H4 held, per host, the `docker top` row, exit codes) into Completion Notes **and** the `deferred-work.md` entry gets `[open: finding recorded <date>]` until the PR is merged (then it is deleted, Task 5).
- [ ] Task 2: Ask First (AC: #2). Put the finding next to the Decision table in a message to Ralf (full sentences; he decides), then record choice/date/rejected options in the header comment and Completion Notes and mark AC #2 `AMENDED`. No code before this.
- [ ] Task 3: Tests first, red (AC: #3, #4, #5, #6). Skip the code-bearing parts if Ralf chose "document only".
  - [ ] 3.1 `tests/EnergyTracker.Api.Tests/FailedStartupExitTests.cs`: start the built API as a child process (`dotnet <EnergyTracker.Api.dll>` from the test output; check that `EnergyTracker.Api.runtimeconfig.json` and `.deps.json` are in the Api test output, which they should be for a referenced web project, and fall back to `dotnet exec --runtimeconfig` only if not) with `ASPNETCORE_ENVIRONMENT=Production`, a free port, `ConnectionStrings__Default` blank, `DataProtection__AllowUnprotectedKeys=true`. Assert: exits within a bounded wait (kill and fail at 30 s, no sleeps in the happy path: `WaitForExitAsync` with a cancellation token), exit code equals what the option promises (not `134`/`-6`, the abort code), stderr contains `ConnectionStrings__Default`. Same for the Data Protection fail-closed case and the `Ai__Endpoint=localhost:1234` case (one `[Theory]`, each throw site of AC #3). Run it before the fix: red (exit code is the runtime abort, or the child hangs and the bounded wait fails it).
  - [ ] 3.2 Known-bad sample (AC #4): assert the same helper reports failure for a child that hangs (a `dotnet` child running a tiny command that sleeps, or the API with a valid configuration that is killed at the timeout), so the exit-code assertion cannot pass vacuously.
  - [ ] 3.3 Architecture guard (AC #6): `tests/EnergyTracker.Architecture.Tests/StartupFailureExitIsRegisteredFirstTests.cs`. Source scan of `src/EnergyTracker.Api/Program.cs`: the first non-using statement calls the registration, before `WebApplication.CreateBuilder`. Known-bad: a copy of `Program.cs` text with the call moved after `CreateBuilder` fails the same predicate.
  - [ ] 3.4 Re-run `ConnectionStringStartupTests`, `DataProtectionStartupTests`, `AiPlausibilityBackendConfigurationTests` unchanged (AC #5); they must stay green with no edit.
- [ ] Task 4: Production code per the chosen option (AC: #3, #5, #6). Files in Dev Notes "File structure". Recommended shape (F1) below; if Ralf chose another option, follow his.
  - [ ] 4.1 `src/EnergyTracker.Api/StartupFailureExit.cs` (one static class, one idempotent `Register()`): subscribes `AppDomain.CurrentDomain.UnhandledException` once (static flag, `Interlocked`), and in the handler writes the exception (`e.ExceptionObject.ToString()`) to `Console.Error` and calls `Environment.Exit(1)`. See Dev Notes "Handler order" for why the handler must print the message itself.
  - [ ] 4.2 `Program.cs`: `StartupFailureExit.Register();` as the first statement, with a `// ADR 0009` pointer. No other `Program.cs` change; the throw sites and their messages are untouched.
  - [ ] 4.3 `ServiceRegistrationSnapshotTests`: expect no change (nothing registers in DI). `dotnet format`, strict build.
- [ ] Task 5: Docs and project memory (AC: #7). ADR 0009 (only if code ships), `docs/self-hosting.md` ("what a failed start looks like"), host-specific note if the finding says so, `project-context.md` bullet plus `Last Updated` line, delete the two `deferred-work.md` entries, one-line `AMENDED` note in `invariants-rules.md` under AD-19 only if the decision changes the AD-19 text (it should not).
- [ ] Task 6: Verify and PR (AC: all). `dotnet format EnergyTracker.sln`, rerun the Task 0 gate, full `dotnet test` per project, `web` untouched so no frontend run. Rerun the Task 1.1 repro against the **new** image: `docker inspect` shows `exited` and the non-zero code, `docker logs` ends with the message; also run a valid configuration and confirm `/health` 200 (the happy path is untouched). Record counts and delta. Open one PR against `main` (waits for Ralf's go-ahead; opening a PR is outward-facing).
- [ ] Task 7: Post-deploy / Azure check (Ralf, the dev does not run it; story `done` only after it). Two parts. (a) If Task 1.5 had no amd64 host: before merge, prove the same symptom (or its absence) on Azure without touching production, for example a throwaway Container App from the pushed image with no `ConnectionStrings__Default`, `az containerapp revision list` / `show` for `runningState` and Log Analytics `ContainerAppSystemLogs_CL | where TimeGenerated > ago(30m) | project TimeGenerated, Reason_s, Log_s` for restarts, then delete it (outward-facing: Ralf approves each Azure write). (b) After merge and the App Deploy run: the production revision still starts and `/health` is 200 (the happy path), and the deploy's health gate behaviour is unchanged. Record run id and result in Completion Notes.

## Dev Notes

### What happens today (verified by reading `Program.cs` at `806dfad`)

- Every startup throw is in top-level statements, **before** `app.Run()`: `Program.cs:29` (`ConnectionStrings:Default`), `Program.cs:56` (`Database:Provider`, inside `ConfigureDbContext`, runs when the first context is built, so also at `builder.Build()` time or at first resolve), `DataProtectionKeyProtection.Configure` (`:63`, `:128`, `:196`), `AiPlausibilityBackendConfiguration.Configure` (`:77`). `builder.AddObservability()` has already run at line 13: Serilog is wired with `UseSerilog(...)` **without** `preserveStaticLogger`, OTel may be registered.
- There is no host yet, so no `IHostApplicationLifetime`, no host `try/catch`, no `Log.Fatal` pattern (`Log.CloseAndFlush`) anywhere in `src`. The exception leaves `Main` unhandled: the .NET runtime prints `Unhandled exception. System.InvalidOperationException: ...` to stderr and aborts. Aborting is what normally yields exit code `134` (`128 + SIGABRT`).
- 11.11 observed: the message reaches `docker logs`, then the container stays `running` at 100% CPU on one core, on OrbStack/arm64, on the **unchanged** pre-story image, and `DOTNET_gcServer=0` / disabling the crash report made no difference. The Dockerfile sets no `DOTNET_` runtime variable for the runtime stage and the compose `api` service has no `restart:`, `init:` or healthcheck; `ENTRYPOINT ["dotnet", "EnergyTracker.Api.dll"]` makes `dotnet` PID 1.
- `infra/` contains no Container Apps probe or restart-policy definition in this repo (grep for `probe|restartPolicy` finds only prose in `infra/README.md`). The platform's defaults apply; Task 7(a) is how the finding gets an Azure data point.

### Hypotheses (Task 1 decides; all unproven)

- **H1: PID 1.** In a container, `dotnet` is PID 1. Linux does not deliver signals with a default action to PID 1 from inside its own PID namespace, so the runtime's self-raised `SIGABRT` may be dropped and the abort path can fall through into a retry/spin instead of terminating. If true: not OrbStack-specific, Azure Container Apps likely behaves the same, `--init` (tini) exits non-zero, and a process-level exit (F1) is the only fix that works on Azure because ACA offers no `init` switch.
- **H2: crash dump.** The runtime launches `createdump` (or a crash-report writer) after the unhandled exception and it spins on this host (arm64 emulation layer, missing `ptrace` permission, `/tmp` quirk). Visible as a `createdump` row with the high `pcpu` in `docker top`. 11.11 says disabling the crash report did not help, so check which switch was used before ruling H2 out.
- **H3: runtime + container only.** The minimal repro from Task 1.3 spins too, so our code (Serilog, OTel, DP) is not involved.
- **H4: our code.** The minimal repro exits, ours does not: Serilog/OTel's background threads or the `UseSerilog` static logger keep the process alive after `Main` faults (non-background thread, or a flush waiting on a half-built host).

### Decision table (Ask First, Task 2; recommendation is conditional on the finding)

| | F1: process-level exit (`AppDomain.UnhandledException` → write error → `Environment.Exit(1)`) | F2: `init: true` / `docker run --init` (tini) | F3: wrap startup in `try/catch` that logs and returns exit code | F4: document only |
|---|---|---|---|---|
| Fixes | Any host, incl. Azure ACA (works whatever PID 1 does) | Only H1, only where the operator can set `init` (self-host compose); **not** Azure | Same as F1 for the exit, plus a structured `Log.Fatal` | Nothing |
| Code | One small class + one line in `Program.cs` | `docker-compose.yml` one line, doc line | Wrap the whole `Program.cs` body; the catch would swallow or rewrap the exception | None |
| Tests | Child-process exit-code test, first-statement guard | Compose guard only; no Azure proof | **Breaks the in-process contract (AC #5):** `WebApplicationFactory` tests expect the exception from `CreateClient()`; a swallow changes the message path, a rethrow re-enters the hang | Doc guard only |
| Risk | Handler registered once per process, tests included: an unrelated unhandled exception then exits 1 instead of aborting (no worse than today) | Hides the problem on Azure | Largest diff, touches every test that probes startup | Leaves crash-loop detection blind on Azure |
| Evidence needed | Works whatever the finding is, except H4 where it may also need the offending thread fixed | Task 1.2 shows H1 | Same as F1 | Task 1.5 shows amd64/Azure exit normally and only OrbStack spins |

Recommendation if H1 or H3 holds, or Azure shows the spin: **F1** (works on every host, no test contract change, smallest diff). Recommendation if only OrbStack/arm64 spins and a native amd64 host plus Azure exit non-zero (H2, host-specific): **F4** plus a note in `docs/local-development.md` naming the host; keep F1 off, because the platform that matters already works. If H4 holds, stop and ask Ralf: the fix is in `ObservabilityRegistration`/Serilog lifetime, which ADR 0001 governs, and the scope changes.

### Handler order and what the handler must print (F1)

CoreCLR raises `AppDomain.UnhandledException` **before** it prints its own `Unhandled exception.` text and aborts. If the handler calls `Environment.Exit(1)`, the runtime never reaches that print, so the handler must write `e.ExceptionObject.ToString()` to `Console.Error` itself (stderr, unbuffered, no Serilog: the logger may be half-built). Do not log through `ILogger` or `Log.*` here. 11.11 AC #2/#9c require the message naming `ConnectionStrings__Default` / `Ai__Endpoint` to stay in `docker logs`. Write the exception **as is**: the existing messages are written never to carry a configured value (`ConnectionStringStartupTests` asserts the fallback password never appears); do not add environment dumps, configuration values or the connection string to the handler's output.

### Current state of the files being changed (read before editing)

- `src/EnergyTracker.Api/Program.cs` (201 lines): builder, `AddObservability()`, connection-string check with the throw at `:29`, `ConfigureDbContext` with the `Database:Provider` throw at `:56`, `AddAuth()`, Data Protection (`DataProtectionKeyProtection.Configure`), `AiPlausibilityBackendConfiguration.Configure`, `AddJobQueue`, `Build()`, middleware, `app.Run()`, `public partial class Program;`. Must preserve: the order of the connection-string check before the Data Protection check (`The_connection_string_check_runs_before_the_Data_Protection_check`), the `public partial class Program` marker `WebApplicationFactory<Program>` needs, and every middleware position other tests pin (`HouseholdMembershipIsResolvedOncePerRequestTests`, `SecurityHeadersTests`).
- `tests/EnergyTracker.Api.Tests/ConnectionStringStartupTests.cs`: `HostIn(...)` builds a `WebApplicationFactory<Program>`; `Should.Throw<Exception>(() => factory.CreateClient())`, `MessagesOf` walks `InnerException`. This is the contract AC #5 protects. `DataProtectionStartupTests.cs` is the sibling.
- `Dockerfile` runtime stage: `USER $APP_UID`, `ENTRYPOINT ["dotnet", "EnergyTracker.Api.dll"]`. Guarded by `DockerImageMatchesTheVerifiedBuildTests` (non-root, `runtime` last stage). Under F1 the Dockerfile does not change.
- `docker-compose.yml` `api` service: no `restart:`, `init:` or healthcheck today. Do not add `restart:` in this story (a crash loop on a bad config is exactly what the restart policy would then make visible; whether self-host should restart is an operator choice, out of scope).

### File structure requirements

- New (F1): `src/EnergyTracker.Api/StartupFailureExit.cs` (flat in `Api` next to `AiPlausibilityBackendConfiguration.cs` and `DataProtectionKeyProtection.cs`, one type per file), `tests/EnergyTracker.Api.Tests/FailedStartupExitTests.cs`, `tests/EnergyTracker.Architecture.Tests/StartupFailureExitIsRegisteredFirstTests.cs`, `docs/adr/0009-failed-startup-exits-non-zero.md`.
- Updated: `src/EnergyTracker.Api/Program.cs` (one line), `docs/self-hosting.md`, `_bmad-artifacts/project-context.md`, `_bmad-artifacts/implementation/deferred-work.md`, `docs/adr/README.md` (index row if it keeps one).
- Tests: `Snake_case` names, one class per file named `{Subject}Tests`, `Shouldly`, `TestContext.Current.CancellationToken`. The child-process test needs no database (the failing paths throw before any connection opens); it must not use `new PostgreSqlBuilder`.

### Architecture compliance

- **AD-19:** `/health` stays liveness-only and untouched; structured Serilog to stdout stays; no environment branching in the exit path (it runs in every environment, Development included, so tests exercise it as-is).
- **AD-17 / 11.6 / 11.11:** the Data Protection and `Ai:Endpoint` throws keep their messages and fail-closed behaviour. This story changes only what happens to the exception afterwards.
- **AD-3 / AD-24:** no new `ConcurrentDictionary`, `SemaphoreSlim`, `Channel`, `IMemoryCache`, `AddRateLimiter`, `Path.GetTempPath` (`SingleInstanceInvariantIsDocumentedTests`). The registration flag is a plain `int` with `Interlocked.CompareExchange`.
- **ADR 0001:** `ClearProviders`, `otelExporter` read once, `writeToProviders` only for `otlp`: untouched. The handler does not use Serilog.
- **Comment rule (11.19):** comments state why in 1-3 lines; longer reasoning goes to ADR 0009 with a `// ADR 0009` pointer. `NoHistoryNarratingCommentsTests` is in the Architecture suite: run it.
- **Analyzer gate (11.18):** strict build `-p:TreatWarningsAsErrors=true`, `dotnet format EnergyTracker.sln`. `Environment.Exit` and a static event subscription are fine under `AnalysisMode=Recommended`; if an analyzer objects (for example CA1031 on a general catch, which this design does not need), fix the code, no `#pragma warning disable`, no `NoWarn`, a `[SuppressMessage(..., Justification = "...")]` only if unavoidable.
- **Time/clock (11.17):** no clock reads. A bounded wait in the test uses `CancellationTokenSource.CancelAfter`, not `Task.Delay` in the happy path.

### Library / framework notes

- .NET 10 / `mcr.microsoft.com/dotnet/aspnet:10.0` (runtime stage), SDK 10.0.402. No package change. Confirm the `AppDomain.UnhandledException` semantics at the pinned runtime in the Task 1/3 spike (handler runs first, `ExceptionObject` is `Exception`, `IsTerminating` is `true` for the main thread) rather than from memory; write the result in ADR 0009.
- Docker Compose `init: true` is a documented service key (tini), relevant only to F2.
- `Process.ExitCode` for a signal-terminated child on Linux is `128 + signal` (abort = `134`); the test must assert the exact code the option promises, not just "non-zero", so a runtime abort cannot satisfy it.

### Testing requirements

- Red first: the child-process test fails before the fix (exit `134` or a hang killed by the bounded wait). Record the red run.
- Non-vacuous (known-bad sample in 3.2 and 3.3).
- The in-process `WebApplicationFactory` tests (AC #5) stay green unchanged; do not loosen or skip any existing test.
- No sleeps: `Process.WaitForExitAsync(cts.Token)` with a 30 s cap, redirect stdout/stderr and read them with `ReadToEndAsync` after exit.
- Do not run the Task 1/7 rehearsals from the unit-test suite (they need Docker images and, for Azure, Ralf's subscription).

### Previous story intelligence

- **11.11** (container config correctness, source of this entry): Task 9.1 (f)/(g) saw the symptom on the unchanged pre-story image; the 11.11 review recorded it as deferred and said Part R2 (Azure) is the natural place for the second data point. Its throwaway-env rehearsal procedure (own compose project, throwaway env file, `down -v`) is the pattern for Task 1.
- **11.22** (latest): Ask First recorded as header comment + `AMENDED` AC + Decision table, Completion Notes copy; copy that shape. Its review found the "claim in a comment that the code does not honor" trap (false allocation comment); keep ADR 0009 and the code comment limited to what the test proves.
- **11.21** (self-host migration): the `migrate` one-shot service already makes a failed migration stop the stack with a non-zero exit; this story gives the `api` service the same property on a failed start.
- **11.18** (analyzer gate): build with the CI flags locally before pushing.

### Git intelligence

Last commits: `806dfad` (close 11.22, #167), `b291e04` (Story 11.22, #166), `fbdddac` (close 11.21, #165), `e7cc3c6` (Story 11.21, #164). Working tree clean on `feature/11-23-failed-startup-exit` at `806dfad`. Commit style: conventional with story and audit id (for example `fix: exit non-zero when the host fails to start (Story 11.23) (#NNN)`). 11.22 touched `ObservabilityRegistration.cs` and `appsettings*.json` (Serilog schema, redaction): this story does not.

### Latest technical notes

No new package. The one unknown is the cause (Task 1). Do not pick the fix from the recommendation alone: the recommendation changes with the finding. If neither the AppDomain handler nor `--init` makes the container exit (the spinner turns out to be something else, for example a non-background thread from OTel/Serilog under H4), stop and ask Ralf.

### Out of scope

A `restart:` policy or healthcheck on the compose `api` service; Container Apps probe or restart-policy changes in `infra/`; `/health` changes (stays liveness-only); the `oidcConfigured` gate and the `/login` 503 page (Epic 12 entries); making the `JobQueue:ConnectionString` check fail at startup (Story 11.19 review note, separate); structured `Log.Fatal` output of the startup failure (F3, only if Ralf picks it); a crash-dump policy for production.

### Project Structure Notes

All paths above exist at `806dfad` except the new files. No migration, no frontend change, no `infra/**` change expected (a workflow change would trigger what-if and `infra-deploy.yml`; this story has none).

### References

- [Source: _bmad-artifacts/planning/epics/epic-11-codebase-audit-hardening.md#Story 11.23]
- [Source: _bmad-artifacts/implementation/deferred-work.md ~lines 157, 600] (entries to delete at the end)
- [Source: _bmad-artifacts/implementation/11-11-container-config-correctness.md] (Task 9.1 (f)/(g), Completion Notes deviation, review Defer)
- [Source: _bmad-artifacts/implementation/11-22-keep-invite-token-out-of-logs.md] (Ask First / AMENDED format)
- [Source: docs/adr/0001-serilog-and-default-logging-providers.md]
- [Source: _bmad-artifacts/project-context.md#Performance/operational gotchas, #Process gates, #Amend ACs at decision time, #deferred-work.md convention]
- [Source: src/EnergyTracker.Api/Program.cs, src/EnergyTracker.Api/DataProtectionKeyProtection.cs, src/EnergyTracker.Api/AiPlausibilityBackendConfiguration.cs, Dockerfile, docker-compose.yml, tests/EnergyTracker.Api.Tests/ConnectionStringStartupTests.cs]

## Dev Agent Record

### Agent Model Used

Sonnet 5.5 (claude-sonnet-5-5)

### Debug Log References

### Completion Notes List

### File List
