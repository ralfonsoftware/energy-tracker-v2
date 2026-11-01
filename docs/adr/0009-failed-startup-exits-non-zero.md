# 0009: A host that fails to start exits with code 1

Source: AD-19 (operational baseline), AD-17 (Data Protection fails closed), Story 11.23 (deferred from Story 11.11, Task 9.1).
Code: `src/EnergyTracker.Api/StartupFailureExit.cs`, first statement of `src/EnergyTracker.Api/Program.cs`.

## Context

Every startup check throws out of `Program.cs` before `app.Run()`: the missing `ConnectionStrings:Default`, an unusable `Ai:Endpoint`, the Data Protection fail-closed checks, an unsupported `Database:Provider`. There is no host yet, so no host-level `try/catch`. The exception leaves `Main`, the runtime prints `Unhandled exception.` and aborts.

Reproduced on 2026-11-01 (OrbStack, aarch64, the shipped image, `ConnectionStrings__Default` unset): the container stayed `running` at 100% CPU and `docker logs` ended with the message. `docker top` showed one process, `dotnet`, state `R`, and only the main thread had CPU time. With `docker run --init` (tini as PID 1) the same image exited `134`. A project with only `throw new InvalidOperationException("x");`, published on `aspnet:10.0`, behaved the same way, so Serilog, OpenTelemetry and Data Protection are not involved. `DOTNET_DbgEnableMiniDump=0` and `DOTNET_EnableCrashReport=0` changed nothing and no `createdump` process existed. The cause is that `dotnet` runs as PID 1 in the container and the runtime's abort does not end it. Restart policies and Container Apps crash-loop detection only see a failed start when the process exits.

Not tested at the time of writing: a real Linux amd64 host and Azure Container Apps (no `--init` switch there). Story 11.23 Task 7 records that check.

## Decision

Chosen by Ralf on 2026-11-01. Rejected: `init: true` in compose (self-host only, exit code stays `134`, no effect on Azure), a `try/catch` around `Program.cs` (breaks the `WebApplicationFactory` tests, which expect the exception from `CreateClient()`), and documenting only.

`StartupFailureExit.Register()` is the first statement of `Program.cs`. It subscribes `AppDomain.UnhandledException` once per process. The handler writes `Unhandled exception. ` and the exception to `Console.Error`, then calls `Environment.Exit(1)`. It prints the exception itself because the runtime raises the event before its own text. It does not use Serilog, which may be half-built, and it adds no configuration values: the exception messages already never carry one.

The hook is on the process, not on a list of throw sites, so a new `throw` needs no extra step. `StartupFailureExitIsRegisteredFirstTests` pins the position.

## Consequences

- `FailedStartupExitTests` starts the built API as a child process for three throw sites and asserts exit code `1` and the key name on stderr. Before the change the same process exited `134`.
- The handler also applies to an unhandled exception after startup: the process exits `1` instead of aborting.
- The in-process `WebApplicationFactory` tests are unchanged: a startup throw there is caught by the factory and never reaches the handler.
