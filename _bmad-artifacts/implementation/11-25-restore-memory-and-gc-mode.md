---
baseline_commit: 475176d
---

# Story 11.25: Restore Memory and GC Mode

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- Epic 11 "Codebase audit hardening" (planning/epics/epic-11-codebase-audit-hardening.md), tier "Triage follow-ups", promoted from deferred-work.md (Story 11.10 dev-story entries, index rows ~139 and ~142, bodies ~567 and ~573). Origin: Story 11.10 (audit S4) measured the restore job at 1250 MiB peak RSS for the 31.75 MiB test export with no container limit, and the validate phase at 109.7 MiB growth under Server GC against 21.6 MiB under Workstation GC. Architecture: AD-2 (dual-provider persistence, plain portable EF Core in the restore writer), AD-6 (async jobs, stable failure codes in `BackgroundJob.ErrorMessage`), AD-13 (one image for Azure and self-host), AD-24 (one instance, temp-file payloads). This file cites the AD IDs, which satisfies the spec-per-fix gate (project-context.md). -->
<!-- Branch: feature/11-25-restore-memory-gcmode (current checkout, clean, at 475176d). ONE PR. No migration, no new NuGet or npm package. Frontend change only if a new stable failure code is chosen (catalog strings in `web/src/lib/job-error.ts` plus its locale catalogs; AC #6). -->
<!-- TWO GATES BEFORE CODE, in this order: (1) Task 1 measurement under a real 1 GiB / 0.5 vCPU container limit, recorded in this file; (2) Ask First on the restore option and the GC mode, chosen from the Decision tables once the measurement is in. The recommendations are hypotheses that Task 1 may overturn. -->
<!-- CONFIRMED 2026-11-05 (Ralf): Azure runs 0.5 vCPU / 1 GiB, the smallest Container Apps size; the next size up is 1 vCPU / 2 GiB. No issues in normal operation. Task 1 (Docker measurement) is part of dev-story, not a pre-step. -->
<!-- DECIDED 2026-11-05 (Ralf, Task 2): restore option R4 (R2 pre-flight size refusal plus a lower peak: map and release one category at a time); GC mode G2 (Server GC, GCHeapCount=1, GCConserveMemory=7, via runtimeconfig). Rejected: R1 (fails AC 3), R3 streaming (largest change in the data-destroying path), R5 bigger container (cost, only moves the ceiling), G0 keep default (about 690 MiB on the 131 MiB export), G1 Workstation (no gain: 677 MiB). Measurement: Completion Notes and docs/data-import-restore.md. -->
<!-- No Auth0/Chrome live gate: no auth flow, claim or browser behaviour change. The Docker measurement (Task 1) runs on Ralf's machine and is Ralf-gated for time only; the post-deploy check (Task 8) touches his Azure subscription and is Ralf-gated. -->

## Story

As an operator,
I want a restore of a large household to stay inside the 1 GiB container,
so that a restore cannot run the instance out of memory (promoted from `deferred-work.md`, Story 11.10 dev-story entries).

## Acceptance Criteria

1. **Measure first (epic AC 1, no code before this).** **Given** the Story 11.10 figure of 1250 MiB RSS (median of three, range 1204.5-1296.4) for the 31.75 MiB reference export with **no** container limit, **when** this story starts, **then** the restore-phase peak is measured again with the shipped image run under `--memory=1g --cpus=0.5` (the Container Apps default, see Dev Notes "Container size"), same file, same method, three runs, and the result (peak RSS, whether the kernel OOM-killed the process, exit code, last log line, time to `Failed`/`Completed`) is recorded in this file and in `docs/data-import-restore.md` as a dated row next to the 11.10 table. The same table gets a validate-phase row under the same limit.
2. **AMENDED 2026-11-05, Ralf (Task 2 decision): R4 chosen = pre-flight size refusal plus lower peak; GC mode G2.** ~~**Ask First (epic AC 1, option is Ralf's).** **Given** the measurement and the options in Dev Notes "Restore decision table", **when** Task 1 is recorded, **then** Ralf picks one (document only, pre-flight size refusal, streaming restore, or a combination), and choice, date and rejected options are recorded in the header comment and Completion Notes. No `src/**` edit before that. A streaming restore is an option, not a requirement (epic wording).~~
3. **Restore stays under the limit or fails clearly (epic AC 1).** **Given** the chosen option, **when** a restore of an export too large for the 1 GiB container is confirmed, **then** either the restore completes inside the limit (measured, AC #1 method), or the job ends `Failed` with a stable failure code before the kernel OOM-kills the process. The tenant is told through the existing catalog path (AD-6, AD-18), never an English sentence from the server, and **no data was deleted** (the refusal happens before the writer's delete phase). A kill by the kernel (exit 137, no log line, job left `Processing` until the stale check marks it `job-interrupted`) is the failure this AC exists to remove.
4. **AMENDED 2026-11-05, Ralf (Task 2 decision): G2 = `System.GC.Server` true, `System.GC.HeapCount` 1, `System.GC.ConserveMemory` 7, carried by `RuntimeHostConfigurationOption` items in `EnergyTracker.Api.csproj` (no MSBuild property exists for the last two); the guard pins all three. Task 5 re-measures the shipped image with no env overrides.** ~~**GC mode decided and set (epic AC 2).** **Given** the API runs Server GC today because `Microsoft.NET.Sdk.Web` defaults `ServerGarbageCollector` to true (no explicit `System.GC.*` setting exists in `EnergyTracker.Api.csproj`, `Dockerfile`, `appsettings*.json` or Bicep), **when** this story is done, **then** the GC mode for the 0.5 vCPU / 1 GiB container is decided (Dev Notes "GC decision table"), set in `EnergyTracker.Api.csproj` (or the `Dockerfile` as `DOTNET_*` env only if the csproj cannot carry it, with the reason recorded), and the setting is the one the measurement in AC #1/#3 ran under. An Architecture guard pins the chosen setting so the next SDK or csproj edit cannot silently return to the default; a known-bad variant (csproj text without the setting) fails the same predicate.~~
5. **GC mode recorded (epic AC 2).** **Given** the decision, **when** it ships, **then** `docs/data-import-restore.md` records: the mode, why (numbers from Task 1, not from 11.10's 12-core Mac), the memory the restore needs per MiB of export (the factor, labelled with the file and date it was measured on), and what an operator with a bigger or smaller container changes (`docs/self-hosting.md` gets one line if a self-host knob exists, e.g. the container limit the GC reads).
6. **Implied (no second path to destruction).** Whatever refuses or fails a too-large restore must run before `restoreWriter.RestoreAsync` (the delete phase) and must not alter `HouseholdRestoreWriter`'s one-outer-transaction rule or `ChunkSize`. A restore that fails mid-insert still rolls back as today; `HouseholdRestoreWriterTests` and `RestoreHouseholdDataTests` stay green with no semantic edit. The temp file is still deleted in the existing `finally`.
7. **Implied (the failure code reaches the user).** If a new stable code is added (`JobFailureCodes`, mapped from an exception type in `BackgroundJobProcessor`'s `failureMessage` switch, never from message text), the client catalogs (`web/src/lib/job-error.ts` and every locale catalog) translate it, a Vitest case pins it, and the restore panel shows it. No code path forwards `OutOfMemoryException.Message` or a byte count the tenant cannot act on. The message tells the tenant what to do (export smaller, or ask the operator to raise the container memory).
8. **Implied (validate phase unchanged).** The 4 MiB reader-buffer guard (`ValidateHouseholdImportTests`) and `HouseholdImportIsStreamedTests` stay green; the validate phase is not made slower by the GC change beyond measurement noise (AC #1 validate row is the proof).
9. **Implied (project memory and deferred-work).** `project-context.md` gets one bullet in "Performance/operational gotchas" (GC mode, restore limit rule, where the factor lives, guard test name) plus a `Last Updated` line. The two `deferred-work.md` entries tagged `[promoted: 11.25]` / `[promoted: Story 11.25]` (index rows ~139 and ~142, bodies ~567 and ~573) are deleted on merge (Story 11.1-11.24 practice); they carry `[open: measured <date>]` until then.

## Tasks / Subtasks

Red first for every behaviour change; record runs in Debug Log References (Story 11.1-11.24 practice). Docker must be running (Task 1 and the Api/Infrastructure Testcontainers tests). One PR on `feature/11-25-restore-memory-gcmode`. No `src/**` edit before Task 2 is decided.

- [x] Task 0: Baseline of the repo (AC: all). `dotnet build EnergyTracker.sln -c Release --no-incremental -p:TreatWarningsAsErrors=true` (0 warnings), `dotnet format EnergyTracker.sln --verify-no-changes --no-restore`, `dotnet test` per project; record counts (the 11.24 close is the reference, see its Debug Log). `web` is run only if Task 6 touches it.
- [x] Task 1: Measure under the real limit (AC: #1). Part of dev-story (Ralf confirmed 2026-11-05); the dev runs it. Only the signed-in Chrome upload steps need Ralf's login.
  - [x] 1.1 Generate the reference export: `scripts/gen-household-export.py --readings 121000 --out <scratchpad>/export-32mb.json` (the 11.10 file: 33,297,302 bytes; if the byte count differs, say so and record the new count; the script default is 130,000 readings, so pass the flag).
  - [x] 1.2 Build the shipped image (`docker build -t energy-tracker:mem .`) and run it plus Postgres 17 with `docker compose` overrides or `docker run`: `--memory=1g --memory-swap=1g --cpus=0.5`, `ASPNETCORE_ENVIRONMENT=Production`, same `Database__Provider=Postgres`. `--memory-swap=1g` matters: without it Docker allows swap and RSS can sit above the limit without a kill, which hides the failure. Record Docker/OrbStack version, host arch, and that the cgroup limit is what the runtime sees (`docker exec ... cat /sys/fs/cgroup/memory.max`).
  - [x] 1.3 Method of 11.10: one fresh process per run, one authenticated warm-up request, upload and confirm from the signed-in Chrome tab (Ralf's login; the dev does not hold credentials), RSS from `docker stats --no-stream` sampled every 50 ms is too coarse: use `cat /sys/fs/cgroup/memory.peak` (cgroup v2 high-water mark, reset per run by a fresh container) plus `ps -o rss=` inside the container for the sampled series. Three runs for validate and three for restore. Record median and range, `docker inspect -f '{{.State.OOMKilled}} {{.State.ExitCode}}'`, and the job's final status via `GET /api/jobs/{id}`.
  - [x] 1.4 Repeat 1.3 with the candidate GC settings from the GC decision table (at least: Server GC default, Workstation non-concurrent, Server GC with `GCHeapCount=1` + `GCConserveMemory`) so Task 2 chooses from numbers. A setting is passed as `DOTNET_gcServer=0`, `DOTNET_gcConcurrent=0`, `DOTNET_GCHeapCount=1`, `DOTNET_GCConserveMemory=<n>` env on the container for the experiment; none of them is committed before Task 2.
  - [x] 1.5 Derive the factor: peak growth over idle per MiB of export, for the winning GC mode, and the largest export (MiB and reading count) that fits under 1 GiB with a stated margin. Write all of it (date, file bytes, limits, settings, medians, ranges, OOMKilled/exit codes) into Completion Notes and `docs/data-import-restore.md`; tag the `deferred-work.md` entries `[open: measured <date>]`.
- [x] Task 2: Ask First (AC: #2, #4). Put the measurement next to the two decision tables in a message to Ralf (full sentences; he decides): restore option, GC mode, and the pre-flight margin if that option wins. Record choice, date and rejected options in the header comment and Completion Notes. No `src/**` edit before this.
- [x] Task 3: Tests first, red (AC: #3, #4, #6, #7). Per the chosen option:
  - [x] 3.1 Restore refusal/streaming behaviour in `tests/EnergyTracker.Application.Tests/RestoreHouseholdDataTests.cs`: a too-large file ends with the new exception type and `restoreWriter.RestoreAsync` is **never called** (NSubstitute `DidNotReceive`), the temp file is still deleted. Inject the memory budget (a small `IOptions`-style value or a delegate), never read `GC.GetGCMemoryInfo()` straight in the use case, so the test is deterministic and needs no 1 GiB allocation (project rule: no flaky timing or memory tests).
  - [x] 3.2 `BackgroundJobProcessor` maps the new exception type to the new `JobFailureCodes` constant (`Infrastructure.Tests`), and an `OutOfMemoryException` thrown anywhere in the job ends `Failed` with a generic null message (documented), never forwarding `.Message`.
  - [x] 3.3 GC guard: `tests/EnergyTracker.Architecture.Tests/ApiGcModeIsSetTests.cs`, a source-text scan of `src/EnergyTracker.Api/EnergyTracker.Api.csproj` (comments stripped, the convention of `DockerImageMatchesTheVerifiedBuildTests`; the rules are a function over the text so known-bad text proves they can fail). Pins the decided properties by name and value; known-bad samples: property removed, flipped value.
  - [x] 3.4 Run each new test against the current code first and record red in the Debug Log.
- [x] Task 4: Implement the restore option (AC: #3, #6, #7). Per Task 2. Keep `RestoreHouseholdData`'s `finally` temp-file delete, the `UploadMissingException` path, and the 11.10 stream deserialization. If streaming won: `HouseholdRestoreRekey` currently takes the whole `HouseholdExportResult` (id maps), so the new shape needs a two-pass design (pass 1: stream the file for ids only, which `ValidateHouseholdImport` already does for validation; pass 2: stream entities per array, re-key, hand chunks to the writer inside the **same** outer transaction). That changes `IHouseholdRestoreWriter` and is the largest scope; do it only if Ralf chose it, with its own Dev Notes addendum he approves.
- [x] Task 5: Set the GC mode (AC: #4, #5). `EnergyTracker.Api.csproj` property group (the file has none for GC today; add it beside `Nullable`/`ImplicitUsings`, one comment line of *why*, normal prose). Rebuild, then check the published `EnergyTracker.Api.runtimeconfig.json` contains the setting (`dotnet publish -c Release -o <scratchpad>/pub` and read the `configProperties`), and re-run the Task 1.3 measurement against the shipped image with **no** env overrides: the committed setting must be the one that was measured.
- [x] Task 6: Failure code through the client (AC: #7). Only if a new `JobFailureCodes` constant exists: `web/src/lib/job-error.ts` (+ test), every locale catalog the existing codes use (grep `spreadsheet-too-large` in `web/src`), the AD-18 catalog rules (no hard-coded English). `npm --prefix web run lint`, `typecheck`, the Vitest suite, build.
- [x] Task 7: Docs and project memory (AC: #5, #9). `docs/data-import-restore.md`: the dated rows next to the 11.10 table, a "Memory under a 1 GiB container" subsection (GC mode, factor, what to change for another size), and the corrected sentence "A fully streaming restore is out of scope" if streaming shipped or the new refusal otherwise. `docs/self-hosting.md` one line if a self-host knob exists. `project-context.md` bullet plus `Last Updated`. Delete the two `deferred-work.md` entries. `NoHistoryNarratingCommentsTests` applies to any `src` comment added.
- [x] Task 8: Verify and PR (AC: all). `dotnet format EnergyTracker.sln`, rerun the Task 0 gate, full `dotnet test` per project, Architecture guard green, `docker build .` succeeds (the Dockerfile is touched only if the GC setting needs `ENV`), `web` suite if Task 6 ran. Record all in the Debug Log.
- [ ] Task 9: Post-deploy check (Ralf; the dev does not run it; story `done` only after it). After merge, on Azure: `az containerapp show -g <rg> -n <app> --query "properties.template.containers[0].resources"` records the real CPU/memory (Dev Notes "Container size": the Bicep sets none, so the default is assumed until this reads it); the deploy is green and `/health` answers. A restore of a large export on Azure is **not** required; if Ralf wants one, he names the export, and the Container Apps `WorkingSetBytes` metric peak is recorded here. If the real size differs from 0.5 vCPU / 1 GiB, say so and reopen Task 5.

## Dev Notes

### Current state of the files this story touches

- `src/EnergyTracker.Application/RestoreHouseholdData.cs`: `ExecuteAsync` checks the temp file exists (`UploadMissingException` otherwise), opens a `FileStream` (81,920-byte buffer, `SequentialScan`), `JsonSerializer.DeserializeAsync<HouseholdExportResult>` into the **whole** object graph, then `ToRestoreData` re-keys every entity (`HouseholdRestoreRekey`, built from the whole `importData`) and maps each export DTO list to a **second** list of Domain entities (`.Select(...).ToList()` per category), then calls `restoreWriter.RestoreAsync(data, ct)`; `finally` deletes the temp file unless the token is cancelled. So at the peak the DTO graph and the entity graph both exist. That is the 1250 MiB. **What this story changes:** whatever the chosen option adds before `RestoreAsync` (a budget check, or a streaming shape). **Preserve:** the `finally` delete, the `UploadMissingException` path, the BOM-skipping stream overload, the id re-keying of Story 11.14 (no id comes from the file).
- `src/EnergyTracker.Application/Ports/IHouseholdRestoreWriter.cs`: `HouseholdRestoreData` is one record holding twelve `IReadOnlyList<T>` (the "one object" the deferred entry names). `RestoreAsync(HouseholdRestoreData, ct)` is the only member. Changing it is the streaming option's cost.
- `src/EnergyTracker.Infrastructure/Adapters/HouseholdRestoreWriter.cs`: `SetCommandTimeout(120 s)`, one outer transaction around chunked deletes (reverse FK order) and chunked inserts (`ChunkSize = 200`, `AddRangeAsync` + `SaveChangesAsync` + per-entity `Detached`, never a blanket `ChangeTracker.Clear()`). The writer already inserts in chunks, so its own working set is small; the peak is the input graph, not the writer. **Preserve:** one outer transaction, `ChunkSize`, delete-before-insert order, `AD-25` (no `HouseholdMember`).
- `src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs` (lines ~102-120): the generic `catch (Exception ex)` logs, sets `completed = false`, and maps `ex` to `failureMessage` with a type switch (`UploadMissingException`, `DeviceTagTooLongException`, `SpreadsheetTooLargeException` to stable codes; `SmartPlugImportValidationException or HouseholdImportValidationException` forward their user-facing `.Message`; everything else null). A new "too large for this server" exception joins that switch. **Preserve:** codes chosen from the exception type, never from message text; the null default for unknown failures. Note `OutOfMemoryException` already lands in the generic catch: it reaches `Failed` with a null message, but only if the runtime throws it (a heap hard limit) and does not instead get killed by the kernel.
- `src/EnergyTracker.Application/JobFailureCodes.cs`: stable codes, each with a one-line comment naming its story. A new code follows that shape.
- `src/EnergyTracker.Api/EnergyTracker.Api.csproj`: no `PropertyGroup` for GC; `Microsoft.NET.Sdk.Web` therefore applies its default `ServerGarbageCollector=true` (the deferred entry's "`System.GC.Server: true`" is the SDK default shown in the generated `runtimeconfig.json`, not a repo setting; Task 1.2 confirms it on the published output).
- `Dockerfile` runtime stage: `USER $APP_UID`, `ENV ASPNETCORE_HTTP_PORTS=8080`, `ENTRYPOINT ["dotnet", "EnergyTracker.Api.dll"]`; no `DOTNET_*` memory settings. `DockerImageMatchesTheVerifiedBuildTests` pins stage order, `USER`, and the audit-gate copy; keep it green.
- `docs/data-import-restore.md` "Why the cap no longer decides memory (Story 11.10, audit S4)": holds the 11.10 table and the sentences this story must correct ("A fully streaming restore is out of scope", "on a 1 GiB container it is the next memory risk").

### Container size

`infra/modules/container-app.bicep` and `main.bicep` set **no** `resources` (cpu/memory) block (grep `cpu|memory` finds nothing), so Azure Container Apps applies its default for the consumption plan: **0.5 vCPU / 1 GiB**. That is where the epic's figure comes from. Ralf confirmed 2026-11-05 that Azure runs this size (the smallest; next is 1 vCPU / 2 GiB) and that normal operation has no issues. Task 9 still records the value read from Azure. Self-host (`docker-compose.yml`) sets no `mem_limit`/`cpus` either, so a self-hosted container is bounded by the host. The memory rule therefore needs to read the **effective** limit at runtime rather than hard-code 1 GiB.

### How .NET sees a container limit (verify, do not trust this note)

- The runtime reads the cgroup limit; `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` returns it (or physical memory when unlimited). Under a cgroup limit the GC sets a heap hard limit of 75 % of the limit by default (768 MiB at 1 GiB) unless `GCHeapHardLimit*` is set. A restore needing more than that gets `OutOfMemoryException` from the GC allocator instead of a kernel kill, and the process survives: that is the "clear failure" path, but only for managed heap, not for native/arena growth, which still can push RSS over the cgroup limit.
- Server GC creates one heap per logical processor (capped by the CPU limit) and uses larger gen0 budgets; with 0.5 vCPU the runtime sees 1 processor, so the 12-core Mac figures of 11.10 overstate the Server GC cost. That is why Task 1 measures in a real limit rather than inferring. `GCHeapCount`, `GCConserveMemory` and `System.GC.Concurrent` are documented runtime config knobs; confirm names and defaults for .NET 10 on Microsoft Learn (`microsoft_docs_search` "garbage collector configuration settings") when writing the doc, not from memory.
- `System.GC.Concurrent` (background GC) adds a second thread and some memory; on 0.5 vCPU it buys little. Record its effect in Task 1.4.

### Restore decision table (Ask First; the recommendation is a hypothesis)

| Option | What changes | Cost / risk | Fits |
|---|---|---|---|
| R1 Document only | `docs/data-import-restore.md` records the measured peak under 1 GiB and the maximum export size that fits; no code. | Free. A too-large restore still dies by OOM (kernel kill or `OutOfMemoryException`), possibly mid-transaction and with the job stuck `Processing` until the stale check. Fails AC #3 unless the measurement shows the 31.75 MiB file fits with margin **and** the realistic maximum export is well below the limit. | Only if Task 1 shows large headroom |
| R2 Pre-flight size refusal | Before deserializing, compare the temp file's length times the measured factor with an injected budget (container memory limit minus idle, minus margin); too large ends the job with a new stable code before any delete. Tenant is told to export smaller or ask the operator for more memory. | Small code, deterministic test (inject the budget). The factor is an estimate; a file just under the line can still fail (the OOM path in `BackgroundJobProcessor` is the backstop). Needs one new code through the catalogs. | Single-household product, exports far below the limit in practice |
| R3 Streaming restore | Two-pass stream (ids, then entities per array in FK order) handed to the writer in chunks inside the same outer transaction; `HouseholdRestoreData` stops being one object. | Removes the proportional peak. Largest change: new port shape, `HouseholdRestoreRekey` redesign, export key order must match insert order (verify in `HouseholdExportStream`), tests for every entity category, both providers. Real regression risk in the data-destroying path. | Only if households of several hundred MiB are a requirement; none is stated |
| R4 R2 plus lower the peak cheaply | R2, plus shrink the transient copy without a new port shape: map and hand over one category at a time and release the DTO list as soon as its entities are built (null out `importData` lists, or build entities lazily per category). | Moderate; halves the peak at most (DTO and entity lists no longer coexist) and keeps the writer contract. | If the measured factor is too high for R2 alone to be useful |

| R5 Raise container size | `containerApp` resources to 1 vCPU / 2 GiB in Bicep. | Costs more per month on the consumption plan; fits the 1250 MiB peak without code, but only moves the ceiling. Infra change with its own deploy check. | Only if Task 1 shows R2 refuses realistic exports |

Recommendation to bring to Ralf: **R2**, with the budget read from the container limit and the factor from Task 1, plus the GC choice below. R3 buys unbounded exports for a product whose reference household export is 32 MiB; R1 leaves AC #3 unproven. Task 1's data may overturn this: if the factor under the real limit means even the reference file fails, R4 or R3 becomes necessary and the story's scope grows accordingly (ask Ralf before building either).

### GC decision table (Ask First; decided from Task 1.4 numbers, not from 11.10)

| Mode | Setting | Expected effect on 0.5 vCPU / 1 GiB | Risk |
|---|---|---|---|
| G0 Keep default | Server GC, concurrent on (SDK default) | Baseline; runtime sees 1 CPU so one heap; gen0 budget still larger than Workstation | None, but 11.10 measured 54.6 MiB validate growth against 21.6 MiB |
| G1 Workstation, non-concurrent | `<ServerGarbageCollector>false</ServerGarbageCollector>` + `<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>` | Lowest steady memory; fewer threads; on 1 CPU Server GC buys no parallelism anyway | Throughput under many concurrent requests (one household: low) |
| G2 Server GC, one heap, conserve | `GCHeapCount=1` plus `GCConserveMemory` (0-9) via `runtimeconfig.template.json` or `<ItemGroup><RuntimeHostConfigurationOption>` | Keeps Server GC code path with tighter memory | More knobs to explain; `GCConserveMemory` trades gen2 compaction time |

Recommendation: **G1**, pending the numbers. Where the property lives: MSBuild properties in the csproj (`ServerGarbageCollector`, `ConcurrentGarbageCollection`) flow into `runtimeconfig.json`; G2's knobs have no MSBuild property and go through `runtimeconfig.template.json` in `src/EnergyTracker.Api/` or `RuntimeHostConfigurationOption`. Prefer the csproj over `Dockerfile` `ENV` because the setting then also applies to `dotnet run`, tests and self-host builds from source (AD-13: one image, no cloud-only config). `DOTNET_*` env vars override runtimeconfig; an operator's own `-e DOTNET_gcServer=1` still wins, which the docs line should say.

### Failure code and message

- New exception (Application layer, next to `UploadMissingException`): a name like `RestoreTooLargeException`, thrown by the pre-flight check, mapped in `BackgroundJobProcessor`'s switch to a new constant such as `JobFailureCodes.RestoreTooLarge = "restore-too-large"` with a comment `// Story 11.25: ...`. Mirror `SpreadsheetTooLargeException`'s shape (file, test, client catalog entry) — grep `SpreadsheetTooLarge` for every touch point and copy the set.
- The message the tenant sees (client catalog, all locales) names the action: export a smaller date range is **not** possible (the export is whole-household), so the text says "ask the person running this server to give it more memory" and links nothing. Do not invent a "smaller export" feature.
- Do not use `HouseholdImportValidationException` for this: its `.Message` is forwarded verbatim and is for file-content problems, not server capacity.

### Measuring honestly

- `docker stats` rounds and samples slowly; use the cgroup counters (`memory.peak` on cgroup v2, `memory.max_usage_in_bytes` on v1) read after the run, plus a sampled `ps` series for the shape. A fresh container per run resets the peak; `docker restart` does not reset `memory.peak` on every runtime — start a new container.
- `--memory-swap=1g` (equal to `--memory`) disables swap; with swap available the process is slow instead of killed and the result lies. On Docker Desktop/OrbStack, check the VM itself has more than 1 GiB free or the host, not the cgroup, decides.
- Apple Silicon runs arm64 images: the Azure target is amd64. Allocation sizes are the same; say so in the record. Do not claim an amd64 number from an arm64 run.
- Generated file: `scripts/gen-household-export.py --readings 121000` (the script's default is 130,000; 11.10's file was 121,000). Record the byte count; if it is not 33,297,302 the comparison to 11.10 is approximate.

### Project Structure Notes

- New test file only under `tests/EnergyTracker.Architecture.Tests/` (`ApiGcModeIsSetTests`, `Snake_case_with_underscores` methods, xUnit v3 / Shouldly, no `Task.Delay`, no Testcontainers for a text scan). The pre-flight behaviour tests extend `RestoreHouseholdDataTests` (Application.Tests); the code-mapping test extends the `BackgroundJobProcessor` tests (Infrastructure.Tests).
- Comments and docs are normal prose, one to three lines of *why* (Story 11.19 rule). `NoHistoryNarratingCommentsTests` covers `src`: write the *why*, not the story history.
- Application layer stays free of infrastructure (AD-1, `DomainHasNoExternalDependenciesTests`): the memory budget enters `RestoreHouseholdData` as an injected value; the adapter that reads `GC.GetGCMemoryInfo()` lives in Api or Infrastructure composition. If a new constructor parameter is added, `ServiceRegistrationSnapshotTests` changes with it (project-context: snapshot of registrations) — update the snapshot in the same commit.
- One Household, one instance (AD-24): the budget is per process, so no cross-instance accounting.

### Previous story intelligence

- **11.10 (done):** the measurement method (Apple M2 Max, Release as `Production`, Postgres 17, fresh process per run, RSS sampled every 50 ms, median of three) and the table this story extends; the validator and the restore deserialization already stream. Its doc section holds the sentences to correct. Its deferred entries are the two this story promotes.
- **11.14:** restore re-keys every entity (`HouseholdRestoreRekey`), one active restore per Household by a filtered unique index; any new early-exit failure must leave the job row in a terminal `Failed` state through the normal processor path (so the active-job index frees up), not throw out of the job pipeline.
- **11.3:** the Azure queue consumer's retry/dead-letter behaviour: a restore that dies by kernel kill is redelivered; the second attempt re-runs into the same OOM. A pre-flight refusal ends the job on the first delivery (`Failed`, no redelivery loop). Confirm in `AzureStorageQueueJobQueue` that a `Failed` job is acknowledged, not retried.
- **11.23:** failed startup exits non-zero; the same operator-facing principle (fail loudly with a readable cause) applies here.
- **11.24:** shape to copy for header comments, Ask First record, guard-with-known-bad-sample, and a post-deploy Ralf check before `done`.

### Git intelligence

Last commits on `main`: `475176d` 11.24 close docs, `aa84578` 11.24 code, `a297df5` dotnet-ef bump, `ac729f5` npm bump, `c14d35c` 11.23 close. Pattern: code PR, then a separate `docs: close Story 11.x` PR after the live check. Expect the same here.

### Latest tech notes

- .NET 10 / ASP.NET Core 10 (`net10.0`, SDK pinned by `global.json`). GC knobs and their MSBuild property names (`ServerGarbageCollector`, `ConcurrentGarbageCollection`, `TieredPGO` irrelevant) are in Microsoft Learn "Runtime configuration options for garbage collection"; check the .NET 10 page for `GCConserveMemory` range and `GCHeapCount` semantics when the doc line is written. Do not copy values from older runtimes.
- `System.Text.Json` async deserialization of one root object still materializes the whole object; there is no built-in "stream each property of the root" API. A streaming restore needs `Utf8JsonReader` by hand (the validator already does this in `ValidateHouseholdImport`; reuse its reader helpers rather than writing a second parser).

### References

- [Source: _bmad-artifacts/planning/epics/epic-11-codebase-audit-hardening.md#Story 11.25: Restore Memory and GC Mode]
- [Source: _bmad-artifacts/implementation/deferred-work.md, index rows ~139 and ~142, bodies ~567 and ~573]
- [Source: _bmad-artifacts/implementation/11-10-upload-hardening.md]
- [Source: docs/data-import-restore.md#Why the cap no longer decides memory (Story 11.10, audit S4)]
- [Source: src/EnergyTracker.Application/RestoreHouseholdData.cs]
- [Source: src/EnergyTracker.Application/Ports/IHouseholdRestoreWriter.cs]
- [Source: src/EnergyTracker.Infrastructure/Adapters/HouseholdRestoreWriter.cs]
- [Source: src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs (failureMessage switch)]
- [Source: src/EnergyTracker.Application/JobFailureCodes.cs]
- [Source: src/EnergyTracker.Api/EnergyTracker.Api.csproj, Dockerfile]
- [Source: tests/EnergyTracker.Architecture.Tests/DockerImageMatchesTheVerifiedBuildTests.cs (guard shape)]
- [Source: scripts/gen-household-export.py]

### Project Context Reference

`_bmad-artifacts/project-context.md` is loaded as persistent facts: AD-24 single-instance rule, async job and stable-failure-code conventions, Architecture-guard conventions, deferred-work convention (delete promoted entries on merge), spec-per-fix gate.

## Dev Agent Record

### Agent Model Used

### Debug Log References

- Red runs before implementation (Release): `RestoreHouseholdDataTests` 2 failed (`Refuses_a_file_over_the_memory_budget...`, `Shares_one_string_instance...`), `ApiGcModeIsSetTests.The_real_Api_project_sets_the_decided_GC_mode` failed, `BackgroundJobProcessorTests.ProcessAsync_maps_RestoreTooLargeException...` failed. The OutOfMemoryException-ends-Failed test passed from the start (documents existing behaviour), as did the `RestoreMemoryBudgetTests` (pure formula, written beside the stub).
- Green after implementation: Api 508, Application 612, Architecture 223, Infrastructure 567, web Vitest 771; `dotnet build -p:TreatWarningsAsErrors=true` 0 warnings; `dotnet format --verify-no-changes` clean; `tsc -b` clean; `docker build .` succeeds. Registration snapshots: one added line per variant (`RestoreMemoryBudget -> factory`).
- Live finding, first final run: the budget came out at 106 MB and refused the 131 MiB export. `TotalAvailableMemoryBytes` is already the 768 MiB heap limit in a 1 GiB container, so the 75 % share was applied twice. Share removed; `RestoreMemoryBudgetTests` now feeds 768 MiB.
- Published `runtimeconfig.json` carries `System.GC.Server` true, `System.GC.HeapCount` 1, `System.GC.ConserveMemory` 7; the container read back the same with no `DOTNET_*` GC variable set.

### Completion Notes List

**Task 0 baseline (2026-11-05, Release, 0 warnings, `dotnet format --verify-no-changes` clean):** Api 508, Application 605, Architecture 214, Infrastructure 565, all green.

**Task 1 measurement (2026-11-05).** Shipped image, `--memory=1g --memory-swap=1g --cpus=0.5`, `memory.max` = 1073741824, `Production`, Postgres 18 (compose; story said 17), Apple M2 Max, OrbStack 29.8.2, arm64 (not amd64). Peak = `VmHWM`; idle about 170 MiB; fresh container per run; upload and confirm from signed-in Chrome (page JS fetching a bind-mounted file served same-origin, because the CSP blocks other origins and the extension upload tool caps at 10 MB). The sample series is in the scratchpad `*.samples` files. Reference file: 33,297,302 bytes (identical to 11.10). Second file: a real household export, 137,375,117 bytes, 502,881 readings (gitignored under `sample-data/`; not to be committed or published).
- Reference, Server GC default (3 runs): validate 208 (203-214), restore 333 (327-334) MiB, jobs 23-30 s. 11.10's 1250 MiB was a 12-core host without a limit.
- 131 MiB export, Server GC default (3): validate 236 (234-236), restore 677 (676-712) MiB, 46-52 s.
- 131 MiB, Workstation non-concurrent (3): validate 246 (246-264), restore 677 (670-694). No gain.
- 131 MiB, Server GC + `GCHeapCount=1` + `GCConserveMemory=7` (3): validate 233 (227-234), restore 535 (531-546). About -21 %.
- No run was OOM-killed (exit 0, `OOMKilled` false); all jobs `completed`.
- Factor: about 4 MiB of peak per MiB of export on the default setting, after 170 MiB idle. Default setting with the 768 MiB runtime heap limit leaves roughly 15 % growth over the 131 MiB export.

**Task 2 decision (2026-11-05, Ralf):** R4 + G2 (see header comment).

**Implementation (2026-11-05).** R4 as built: `RestoreHouseholdData` refuses by file size against the injected `RestoreMemoryBudget` before reading or deleting (new `RestoreTooLargeException`, `JobFailureCodes.RestoreTooLarge`, mapped by type in `BackgroundJobProcessor`, translated in en-US and de-DE); `ToRestoreData` converts each category with `ConvertAndRelease` (nulls each DTO in the deserialized `List<T>` as its entity is built) and shares repeated smart plug names. `HouseholdRestoreWriter`, `IHouseholdRestoreWriter`, `ChunkSize` and the one-transaction rule are untouched. Budget formula: (runtime-reported available memory - 170 MiB) / 3.0; 3.0 is deliberately above the measured 1.9 (real export) and 2.6 (generated).

**Final measurement, shipped image, no env overrides, 1 GiB / 0.5 vCPU (MiB):** 131 MiB export restore peak 418 (416-421, 4 runs; 677 before the story), validate 231; reference file restore 251, validate 207. No OOM kill. Live refusal check: 170 MiB export under the first budget and 235 MiB export under the final one both ended `Failed` `restore-too-large` in under a second, readings stayed at 121,200, process kept running, temp file removed.

**Open for Ralf:** Task 9 (post-deploy check on Azure: container size, `/health`) and the Auth0/Chrome-free review. A real-Azure restore of a large export is optional. The 131 MiB real export (`sample-data/`, gitignored) was used for measurement and is not part of the change.

**Notes for review.** Measurements ran on arm64/OrbStack with Postgres 18, not amd64 with Postgres 17. The restore job on the first G2 attempt (budget double-counted) is in the Debug Log. The AC 4 amendment records that the setting lives in `RuntimeHostConfigurationOption` items, not a Dockerfile `ENV`. `docs/self-hosting.md` got a "Memory and large restores" section.

### Change Log

- 2026-11-05: Story 11.25 implemented: restore memory measured under the real limit, size refusal before delete, lower restore peak, Server GC with one heap and conserve level 7, `restore-too-large` through the client catalogs, docs and project memory, deferred entries removed.

### File List

- `src/EnergyTracker.Application/RestoreHouseholdData.cs` (modified)
- `src/EnergyTracker.Application/RestoreMemoryBudget.cs` (new)
- `src/EnergyTracker.Application/RestoreTooLargeException.cs` (new)
- `src/EnergyTracker.Application/JobFailureCodes.cs` (modified)
- `src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs` (modified)
- `src/EnergyTracker.Api/EnergyTracker.Api.csproj` (modified)
- `src/EnergyTracker.Api/Composition/ApplicationServicesRegistration.cs` (modified)
- `tests/EnergyTracker.Application.Tests/RestoreHouseholdDataTests.cs` (modified)
- `tests/EnergyTracker.Application.Tests/RestoreMemoryBudgetTests.cs` (new)
- `tests/EnergyTracker.Architecture.Tests/ApiGcModeIsSetTests.cs` (new)
- `tests/EnergyTracker.Infrastructure.Tests/BackgroundJobProcessorTests.cs` (modified)
- `tests/EnergyTracker.Infrastructure.Tests/HouseholdRestoreWriterTests.cs` (modified: constructor argument only)
- `tests/EnergyTracker.Api.Tests/Snapshots/service-registrations.{default,oidc,otlp,azuremonitor,azurestoragequeue}.txt` (modified)
- `web/src/lib/job-error.ts`, `web/src/lib/job-error.test.ts`, `web/src/locales/en-US/translation.json`, `web/src/locales/de-DE/translation.json` (modified)
- `docs/data-import-restore.md`, `docs/self-hosting.md` (modified)
- `_bmad-artifacts/project-context.md`, `_bmad-artifacts/implementation/deferred-work.md`, `_bmad-artifacts/implementation/sprint-status.yaml`, this file (modified)
