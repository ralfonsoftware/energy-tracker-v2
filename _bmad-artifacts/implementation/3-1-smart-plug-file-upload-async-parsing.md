---
baseline_commit: 12d5c1c1f87ac61c2652cf6603ae2aa4b0e1fbe6
---

# Story 3.1: Smart Plug File Upload & Async Parsing

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want to upload my Smart Plug export file (Eve Home `.xlsx` or Meross `.csv`) and have it parsed in the background,
so that I don't have to wait for processing to finish before continuing to use the app.

## Acceptance Criteria

1. **Given** a Smart Plug export file (Eve Home `.xlsx` or Meross `.csv`), **when** I upload it, **then** the upload confirms immediately and parsing runs asynchronously via the job queue — the UI never blocks on parsing (FR-4, AD-6).
2. **Given** an in-progress import, **when** processing completes, **then** I receive a completion notification, learned by the client polling `GET /api/jobs/{id}`, never via WebSocket/SSE (AD-6).
3. **Given** an Eve Home `.xlsx` file, **when** parsed, **then** its timestamps are interpreted as local time, never UTC-converted (FR-4, AD-9).
4. **Given** a Meross `.csv` file, **when** parsed, **then** its Device/Power Point identity is matched via the documented filename pattern (`Power Monitor Day Data - {device} - {YYYYMMDD}.csv`), not by trusting in-file metadata alone (FR-4, AD-9).
5. **Given** each vendor format, **when** parsed, **then** it goes through its own adapter (`EveHomeXlsxParser`, `MerossCsvParser`) behind the single `ISmartPlugParser` port — no vendor-specific parsing logic leaks outside the adapter (AD-9).
6. **Given** the async job processing, **when** it runs, **then** it resolves `ICurrentHouseholdAccessor` from the enqueued job's `HouseholdId` field, never bypassing tenant isolation via `IgnoreQueryFilters()` or a raw lookup (AD-3, AD-6).

## Tasks / Subtasks

- [x] **Task 1: Async job queue infrastructure — AD-6** (AC: #1, #2, #6). This is the first story to touch job processing; nothing below exists yet.
  - [x] `src/EnergyTracker.Application/Ports/IBackgroundJobQueue.cs`: define `JobEnvelope<TPayload> { Guid JobId; Guid HouseholdId; string JobType; TPayload Payload }` (a plain record — `TPayload` must be JSON-serializable, never a delegate/closure) and `IBackgroundJobQueue { Task EnqueueAsync<TPayload>(JobEnvelope<TPayload> envelope, CancellationToken ct); }`.
  - [x] New `BackgroundJob` entity (`src/EnergyTracker.Domain/BackgroundJob.cs`) + `BackgroundJobConfiguration.cs`, mirroring `MeterRegressionPrompt`'s shape (`required Guid Id`, `required Guid HouseholdId`, `required string JobType`, a status field, `ErrorMessage` nullable, `CreatedAtUtc`, `CompletedAtUtc` nullable). This is what `GET /api/jobs/{id}` (Task 1.7) reads — job state must be **DB-persisted, not in-memory**, since Azure Container Apps can scale to zero/multiple replicas between enqueue and a client's next poll. Add via `scripts/add-migration.sh <Name>` (both provider projects, AD-2) and wire its `HasQueryFilter` in `EnergyTrackerDbContext.OnModelCreating` next to the other Household-scoped entities (`src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs:51-57`).
  - [x] `InProcessChannelJobQueue` (`Infrastructure/Adapters`) — `System.Threading.Channels`-backed, paired with a hosted `BackgroundService` that reads envelopes off the channel, inserts the `BackgroundJob` row as the job starts processing, resolves the job's use case, and updates status/`ErrorMessage`/`CompletedAtUtc` on finish. Default adapter — self-host and local dev, zero extra containers.
  - [x] `AzureStorageQueueJobQueue` (`Infrastructure/Adapters`) — cloud adapter. Add `Azure.Storage.Queues` to `Directory.Packages.props` (not present today — confirmed via grep). **Infra is already deployed and wired — do not touch `infra/`:** `infra/modules/storage-queue.bicep` provisions the Storage Account + `jobs` queue, `infra/main.bicep:142-186` wires it in, and `infra/modules/container-app.bicep:168-176` already injects `JobQueue__Provider=AzureStorageQueue` and `JobQueue__ConnectionString` (via `storage-queue-connection-string` secret) into the Container App env. This story only needs to write the C# that *reads* those two config keys.
  - [x] `Program.cs`: read `JobQueue:Provider` once at the composition root, same `switch`-on-lowercased-config-value shape as `Database:Provider` (`Program.cs:114-141`) and `Otel:Exporter` (`Program.cs:34-75`) — unset/blank → `InProcessChannelJobQueue` (mirrors `Otel:Exporter`'s "unset stays off" default, not `Database:Provider`'s hard-required default), `"azurestoragequeue"` → `AzureStorageQueueJobQueue` reading `JobQueue:ConnectionString`. `docker-compose.yml` needs no new env var — local dev already gets the InProcess default by omission (confirmed: no `JobQueue__*` keys set there today).
  - [x] **Extend `ICurrentHouseholdAccessor` for AD-3's second resolution path.** Today `CurrentHouseholdAccessor` (`src/EnergyTracker.Infrastructure/Adapters/CurrentHouseholdAccessor.cs`) resolves *only* from `IHttpContextAccessor.HttpContext?.User` — there is no job-context path yet. Add a small scoped mutable holder (e.g. `JobHouseholdContext { public Guid? HouseholdId { get; set; } }`, registered `AddScoped`) that `CurrentHouseholdAccessor.Resolve()` checks first when `HttpContext` is null; the job-processing `BackgroundService` creates a new `IServiceScopeFactory`-scope per dequeued envelope, sets `JobHouseholdContext.HouseholdId = envelope.HouseholdId` on that scope **before** resolving/invoking the use case, so every downstream query filter (AD-3) sees the right Household with no `IgnoreQueryFilters()`/raw-lookup workaround (AC #6).
  - [x] `GET /api/jobs/{id}` endpoint (new `JobEndpoints.cs`, registered in `Program.cs` next to the other `api.MapXEndpoints()` calls at `Program.cs:338-345`) — Household-scoped like `MeterReadingEndpoints` (`TryGetHouseholdId` + `ICurrentHouseholdAccessor`, `MeterReadingEndpoints.cs:14-26` pattern); a `BackgroundJob` row belonging to a different Household must 404, not leak existence.

- [x] **Task 2: Smart Plug parser port & vendor adapters — AD-9** (AC: #3, #4, #5)
  - [x] `ISmartPlugParser` port (`Application/Ports`) + common `SmartPlugReading` output shape (`Guid PowerPointId?`, denormalized `RoomName`/`PowerPointName`/`DeviceName` display fields per AD-10, `IntervalStart`, `IntervalEnd`, `KwhValue`) — one method, e.g. `IReadOnlyList<SmartPlugReading> Parse(Stream fileContent, string fileName)`.
  - [x] `EveHomeXlsxParser` (`Infrastructure/Adapters`). **Real sample files exist at `sample-data/eve/*.xlsx` — inspect them directly, don't rely on the PRD addendum's cell references, which are off by one row.** Actual layout (verified against both sample files):
    - Sheet name `Gesamtverbrauch` (confirmed).
    - **A1** = `"Gerät: {device name}"` (device name is prefixed, not bare — e.g. `"Gerät: Steckdose Tür"`).
    - **A2** = `"Raum: {room name}"` (e.g. `"Raum: Wohnzimmer"`).
    - **A3** = `"Zuhause: {home name}"` — a third header row **not mentioned in the PRD addendum at all**; ignore its value (home identity isn't Household-scoping-relevant here) but account for its presence when locating the header/data rows.
    - **Row 4** = column headers: `Datum`, `Gesamtverbrauch (Wh)`.
    - **Row 5+** = data, ~10-minute intervals, value in **Wh** (fractional, e.g. `0.82`) — convert to kWh (÷1000) to match `SmartPlugReading.KwhValue`'s unit. Rows are in **descending** chronological order (newest first) in both sample files — do not assume ascending order.
    - Timestamps parsed as **local time, never UTC-converted** (AC #3) — this is deliberate, documented behavior (AD-9), not a bug: converting corrupts data across midnight boundaries.
  - [x] `MerossCsvParser` (`Infrastructure/Adapters`). **Real sample files exist at `sample-data/meross/*.csv` — verified byte-for-byte:** UTF-8 with BOM (`EF BB BF`), header line `Date\t,Power Consumption-(kWh)\t\n`, each data line `{YYYY-MM-DD}\t,{kwh value}\t\n` — the field delimiter is literally `\t,` (tab then comma) and every line has a trailing tab before the newline. One row per **day** (filename says "Day Data" — this is daily-aggregate data, coarser granularity than Eve Home's 10-minute intervals; `SmartPlugReading.IntervalStart`/`IntervalEnd` should span the full day for these rows), ascending chronological order in both samples. Device/Power Point identity comes from the **filename**, not the file body: `Power Monitor Day Data - {device} - {YYYYMMDD}.csv` (AC #4) — the file body has no device/room identifier at all.
  - [x] Add an `.xlsx`-parsing NuGet package (e.g. ClosedXML) to `Directory.Packages.props` (none present today, confirmed via grep) — central package management, never a per-project `Version=`. Meross `.csv` needs no third-party package (plain text split).
  - [x] `SmartPlugImport` entity (Household-scoped, tracks one uploaded file: `Id`, `HouseholdId`, `BackgroundJobId`, `VendorFormat` enum {EveHome, Meross}, `OriginalFileName`, `Status` enum {Processing, AwaitingPowerPointMapping, Completed, Failed}, `DeviceTag` (parsed device/room name), `CreatedAtUtc`, `CompletedAtUtc`) and `SmartPlugReading` entity (Household-scoped, AD-10 by-value Room/PowerPoint/Device snapshot — see `AD-10` in Dev Notes). EF configs mirroring `MeterRegressionPromptConfiguration.cs`'s pattern (`Restrict` delete behavior, `HasIndex(HouseholdId)`). One migration via `scripts/add-migration.sh` covering both new entities.

- [x] **Task 3: Upload endpoint & import orchestration** (AC: #1, #2, #6)
  - [x] `POST /api/smart-plug-imports` (new `SmartPlugImportEndpoints.cs`) — `IFormFile` multipart upload (no `IFormFile`/multipart precedent exists anywhere in this codebase; this is new ground). Validate extension is `.xlsx` or `.csv`, reject anything else with a 400 `ProblemDetails`. On success: write the uploaded file to a short-lived temp location (e.g. a scoped temp-file path locally, or equivalent blob/temp storage in Azure) and enqueue a job via `IBackgroundJobQueue` whose payload carries **only the file's temp-storage reference + metadata** (path/id, original filename, Household id) — **do not embed the raw or base64 file bytes in the envelope.** Azure Storage Queue caps a message at 64 KB; the real Eve Home sample files run several hundred KB uncompressed (see `sample-data/eve/`, and the mockup's own "384 KB" example file), so embedding bytes would work on `InProcessChannelJobQueue` locally and silently fail on `AzureStorageQueueJobQueue` in Azure — exactly the kind of "works on one adapter, breaks on the other" split AD-6 exists to prevent, just via message size rather than serializability. Return `202 Accepted` with the job id immediately — no parsing happens synchronously (AC #1).
  - [x] `ProcessSmartPlugImport` use case (the job's handler, invoked by the `InProcessChannelJobQueue`'s `BackgroundService`/`AzureStorageQueueJobQueue`'s equivalent dequeue loop) — resolves the vendor parser by file extension, calls `ISmartPlugParser.Parse(...)`, attempts to match the parsed device/room tag to an existing Power Point **by exact name**, persists `SmartPlugImport` + `SmartPlugReading` rows, updates the `BackgroundJob` row to `Completed`/`Failed`.
  - [x] **Do not call `IStatusRecomputeService` from this use case.** AD-7 names exactly two call sites (`CreateMeterReading`, and Smart-Plug-import-completion) — but Story 3.3's own AC explicitly owns wiring the import-completion → `IStatusRecomputeService` call ("Given a Smart Plug import completes, When processing finishes, Then Status recomputes immediately..."). Wiring it here would be премature: 3.3 hasn't built the gap-handling/interpolation logic yet that determines what a Smart Plug import is even allowed to contribute to the baseline. Leave this story's `SmartPlugImport.Status = Completed` as the sole completion signal.
  - [x] **Unmatched device/tag:** if no existing Power Point's name matches, persist the `SmartPlugImport` (and its parsed `SmartPlugReading` rows, with `PowerPointId = null`) with `Status = AwaitingPowerPointMapping` rather than failing — this is a well-defined, queryable state. **Do not build the create/map prompt UI for this case** — that is entirely Story 3.2's scope ("Import-to-Power-Point Mapping"). This story's job is done once the import is durably persisted in one of its terminal/awaiting states.

- [x] **Task 4: Frontend — upload UI & job polling** (AC: #1, #2)
  - [x] New `web/src/components/smart-plug-import/` folder (matches the existing per-feature grouping — `household-invite`, `tagging-scaffold`, `yearly-baseline`), e.g. `smart-plug-import-panel.tsx`. Add it to `SettingsPage` (`web/src/components/settings/settings-page.tsx`) alongside `YearlyBaselineForm`/`TaggingScaffoldManager`/`InviteGeneratePanel` — per EXPERIENCE.md's IA table, Smart Plug Import is reached via Settings, not its own bottom-nav tab.
  - [x] Reference [mockups/key-smart-plug-import.html](../planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-smart-plug-import.html) **State 1 only** ("Uploading, non-blocking", lines 314-359) for this story's UI — the dropzone, file-choose control, "Processing" pill + progress bar, and the "we're parsing this in the background" async-note copy. States 2-5 (gap summaries, create/map prompt) belong to Stories 3.2/3.3.
  - [x] **Do not copy State 1's mockup colors literally.** The accessibility/rubric review (`review-rubric.md:37`) flagged this exact mock's `.processing-pill`/`.complete-check` as a confirmed DESIGN.md violation — it reuses `status-within-range-dark`/`status-below-baseline-dark` (the Status semantic triad) for a non-status upload-progress badge, which DESIGN.md's own Do/Don't table explicitly forbids ("Reuse a status-triad color for chrome, decoration, or a non-status badge"). Use a neutral/brand-chrome treatment instead (existing shadcn Button/Badge variants — per `DESIGN/components.md`'s "Everything else" note, Smart Plug Import uses standard shadcn components unmodified).
  - [x] `web/src/lib/smart-plug-import-api.ts` — `uploadSmartPlugFile(file: File)` (multipart `POST /api/smart-plug-imports`, `credentials: 'include'`) and `fetchJobStatus(jobId: string)` (`GET /api/jobs/{id}`), following the exact `ApiError`/`toApiError` pattern already established in `web/src/lib/status-api.ts`/`meter-regression-api.ts` (thrown `ApiError` on non-ok, never silently swallowed).
  - [x] Client-side polling loop for job completion — **no existing precedent in this codebase** (first polling UI). Use a `useEffect` + `setInterval` (or repeated `setTimeout`), cleared on unmount and on terminal job status (`Completed`/`Failed`), surfacing the "Import complete" state once polling resolves.

- [x] **Task 5: Tests** (AC: all)
  - [x] `tests/EnergyTracker.Application.Tests/EveHomeXlsxParserTests.cs`, `MerossCsvParserTests.cs` — one test class per subject (project-context.md convention), `Snake_case` method names, Shouldly assertions. Use the real fixtures at `sample-data/eve/*.xlsx` / `sample-data/meross/*.csv` (copy or reference them from the test project) rather than hand-rolled minimal files — assert local-time non-conversion (AC #3) and filename-pattern device matching (AC #4) against the real data shape documented in Task 2.
  - [x] Job envelope test — confirm `JobEnvelope<TPayload>` round-trips through `System.Text.Json` (or whichever serializer the queue adapters use) with a plain record payload; this is the regression the `deferred-work.md` "Job envelopes must be plain JSON-serializable records" edge case (project-context.md) exists to catch.
  - [x] `tests/EnergyTracker.Api.Tests/SmartPlugImportEndpointsTests.cs` / `JobEndpointsTests.cs` (Testcontainers, real Postgres — project-context.md convention) — upload → poll `GET /api/jobs/{id}` to `Completed`; a cross-Household job id returns 404 (AD-3, mirrors the existing IDOR-guard pattern used for Room/PowerPoint/Device).
  - [x] Frontend Vitest tests (colocated, `@testing-library/react`) for the upload panel and `smart-plug-import-api.ts`, including a test that the polling loop clears its interval on unmount.
  - [x] Confirm `tests/EnergyTracker.Architecture.Tests/PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests.cs` still passes untouched — this story doesn't touch any of its 9 guarded files, so it should stay green automatically; do not add `SmartPlugReading` references to any file in that guard's list (`GetCurrentStatus.cs`, `StatusRecomputeService.cs`, etc.) — that's exactly the AD-14 boundary Story 3.3 will need to respect too.

### Review Findings

- [x] [Review][Patch] `BackgroundJobStatus.Completed` doesn't distinguish "fully imported" from "AwaitingPowerPointMapping" — resolved: fix now, expose the sub-status and give the frontend a distinct badge for it (decision: not deferred to 3.2) [src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs:51, src/EnergyTracker.Api/Endpoints/JobEndpoints.cs, web/src/components/smart-plug-import/smart-plug-import-panel.tsx:43-44]
- [x] [Review][Patch] `SmartPlugReading.KwhValue` precision truncates virtually all Eve Home readings to `0.00` [src/EnergyTracker.Infrastructure/Configurations/SmartPlugReadingConfiguration.cs:17]
- [x] [Review][Patch] Vendor mislabeled on Failed `SmartPlugImport` rows — re-derives vendor via filename extension instead of reusing the already-resolved parser's `Vendor` (AC #5) [src/EnergyTracker.Application/ProcessSmartPlugImport.cs:105-107]
- [x] [Review][Patch] Power Point match via `FirstOrDefault` doesn't handle two Power Points sharing a Name across different Rooms (schema explicitly permits this via the per-Room unique index) — silent misattribution of energy data to the wrong Room/Power Point [src/EnergyTracker.Application/ProcessSmartPlugImport.cs:43]
- [x] [Review][Patch] Matched Power Point isn't filtered to active (non-archived) ones — an import can silently attach readings to an archived Power Point [src/EnergyTracker.Infrastructure/Adapters/TaggingScaffoldRepository.cs:27-28]
- [x] [Review][Patch] `AzureStorageQueueJobQueue` has no idempotency guard on message redelivery — default 30s visibility timeout can be exceeded during real parsing/DB work, causing an uncaught PK-violation on the `BackgroundJob` insert retry that leaves the message stuck retrying forever with no dead-letter path [src/EnergyTracker.Infrastructure/Adapters/AzureStorageQueueJobQueue.cs:34-64]
- [x] [Review][Patch] `MerossCsvParser`'s filename regex is case-sensitive on `.csv` while the upload endpoint's extension check is case-insensitive — an uploaded `...CSV` (uppercase) file is accepted at 202 but always fails to parse [src/EnergyTracker.Infrastructure/Adapters/MerossCsvParser.cs:69]
- [x] [Review][Patch] Temp file on disk is never cleaned up if `IBackgroundJobQueue.EnqueueAsync` throws after the file was already written [src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs:53-64]
- [x] [Review][Patch] A cancellation mid-processing (app shutdown/redeploy) is caught by the generic `catch (Exception)` and permanently marks the import `Failed` rather than leaving it retryable [src/EnergyTracker.Application/ProcessSmartPlugImport.cs:85-92]
- [x] [Review][Patch] Raw .NET exception messages are stored verbatim in `BackgroundJob.ErrorMessage` and returned as-is via `GET /api/jobs/{id}` with no sanitization [src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs:53-58]
- [x] [Review][Patch] No `CancellationToken` plumbed into `ISmartPlugParser.Parse`, combined with no explicit app-level upload size limit beyond the ASP.NET Core/Kestrel framework default — an adversarial or oversized file can block the single shared background-processing loop with no way to cancel it [src/EnergyTracker.Application/Ports/ISmartPlugParser.cs]
- [x] [Review][Patch] Frontend never surfaces `job.errorMessage` on a failed import — only a generic "Import failed" badge is shown [web/src/components/smart-plug-import/smart-plug-import-panel.tsx:45-47]
- [x] [Review][Patch] `MerossCsvParser` has no guard for a data line missing the expected `\t,` delimiter — throws an unhandled `IndexOutOfRangeException`, aborting the whole file instead of a descriptive per-row error [src/EnergyTracker.Infrastructure/Adapters/MerossCsvParser.cs:41-43]
- [x] [Review][Patch] A single transient `fetchJobStatus` network failure during polling permanently flips the UI to `'failed'`, with no retry/backoff [web/src/components/smart-plug-import/smart-plug-import-panel.tsx:48-52]

## Dev Notes

### This is genuinely greenfield infrastructure work

Confirmed by direct codebase search (zero hits): no `IBackgroundJobQueue`, `JobEnvelope`, `ISmartPlugParser`, `SmartPlugReading`, `EveHomeXlsxParser`, or `MerossCsvParser` exists anywhere in `src/`. No `IFormFile`/multipart upload endpoint and no frontend file-upload/polling UI exists anywhere either. This story stands up AD-6's async job infrastructure *and* AD-9's parser port in one pass — treat Task 1 as the real foundation the rest of Epic 3 (3.2, 3.3) and any future Tier-3 async work will build on, not a one-off.

### Architecture constraints (binding, not optional)

- **AD-6 (Async job processing):** one `IBackgroundJobQueue` port, envelope-generic, `JobEnvelope<TPayload>` never a delegate/closure (delegates can't serialize across `AzureStorageQueueJobQueue`). Two adapters, config-selected once at the composition root exactly like every other swappable capability (Consistency Conventions table). API and job worker are **one process, one container** in both environments (no separate worker deployment — see `deferred.md`). Client learns completion by **polling**, never WebSocket/SSE.
- **AD-3 (Tenant isolation) applied to jobs:** `ICurrentHouseholdAccessor` has exactly two resolution paths per the spine — HTTP principal (exists today) and job-envelope `HouseholdId` (Task 1's job to add). `IgnoreQueryFilters()`, `FromSqlRaw`, and `DbSet<T>.Find()` are never used against a Household-scoped entity, including from job-processing code — this is the specific bypass AD-3 exists to close for exactly this kind of "no HTTP principal to resolve from" code path.
- **AD-9 (Smart-plug parser port):** one `ISmartPlugParser` port, one adapter per vendor, producing a common `SmartPlugReading` shape. Eve Home timestamps stay local time (reproduced deliberately, not a bug). Meross identity from filename, not file body. FR-20's generic column-mapping is explicitly deferred — do not build a configurable/generic parser here.
- **AD-10 (Historical tag integrity):** `SmartPlugReading` snapshots Room/PowerPoint/Device identity **by value** at write time (denormalized display fields) — a later retag must not rewrite this import's historical attribution. This binds `SmartPlugReading`'s schema (Task 2) even though the actual retag scenario isn't exercised until Story 2.6-style re-parenting meets Smart Plug data later.
- **AD-7 boundary — do not cross it:** Status recompute-on-import-completion is explicitly Story 3.3's AC, not this story's. Resist the temptation to "finish the loop" by calling `IStatusRecomputeService` here.

### Config surface already reserved in infra — read, don't provision

`infra/modules/container-app.bicep:168-176` and `infra/modules/storage-queue.bicep` already exist and are live in `infra/main.bicep`. The Azure Storage Queue resource is provisioned, and the Container App already receives `JobQueue__Provider=AzureStorageQueue` and `JobQueue__ConnectionString` (via a `storage-queue-connection-string` secret) as env vars. **This story's entire infra footprint is reading `builder.Configuration["JobQueue:Provider"]` / `["JobQueue:ConnectionString"]` in `Program.cs`** — no Bicep changes, no new Container App secrets, no `infra/` files touched at all.

### Existing patterns to mirror exactly

- **Config-driven adapter selection** (`Program.cs:34-75` Otel, `:114-141` Database): read the config value once, lowercase it, `switch` at the composition root. `JobQueue:Provider` follows the same shape.
- **Use-case shape** (`src/EnergyTracker.Application/CreateMeterReading.cs`): primary-ctor DI, single `ExecuteAsync(Guid householdId, ..., CancellationToken)`, one-line `/// <summary>` doc citing AC numbers, typed exceptions not generic ones.
- **Endpoint shape** (`src/EnergyTracker.Api/Endpoints/MeterReadingEndpoints.cs`): static class, `MapXEndpoints(this RouteGroupBuilder api)` extension registered in `Program.cs:338-345`, `TryGetHouseholdId` 403-on-no-Household guard, `record` request/response DTOs in the same file, `Results.Problem(detail:, statusCode:)` for errors.
- **Entity/config shape** (`src/EnergyTracker.Domain/MeterRegressionPrompt.cs` + `Infrastructure/Configurations/MeterRegressionPromptConfiguration.cs`): `required Guid Id`/`HouseholdId { get; init; }`, denormalized `HouseholdId` (not a join), `Restrict` delete behavior, `HasIndex(HouseholdId)`, query filter wired centrally in `EnergyTrackerDbContext.OnModelCreating` — never per-repository.
- **Frontend API client shape** (`web/src/lib/status-api.ts`): a local `ApiError`/`toApiError(response)` pair, plain `fetch(..., { credentials: 'include' })` — no shared client, no React Query, no router (manual view-state in `App.tsx`).

### Project Structure Notes

- Backend new files: `Application/Ports/IBackgroundJobQueue.cs`, `Application/Ports/ISmartPlugParser.cs`, `Application/ProcessSmartPlugImport.cs`, `Domain/BackgroundJob.cs`, `Domain/SmartPlugImport.cs`, `Domain/SmartPlugReading.cs`, `Infrastructure/Adapters/InProcessChannelJobQueue.cs`, `Infrastructure/Adapters/AzureStorageQueueJobQueue.cs`, `Infrastructure/Adapters/EveHomeXlsxParser.cs`, `Infrastructure/Adapters/MerossCsvParser.cs`, `Infrastructure/Configurations/BackgroundJobConfiguration.cs` (+2 more), `Api/Endpoints/SmartPlugImportEndpoints.cs`, `Api/Endpoints/JobEndpoints.cs`. All fit the existing flat, one-class-per-file, no-feature-folder-nesting convention (project-context.md).
- Frontend new files: `web/src/components/smart-plug-import/smart-plug-import-panel.tsx` (+ colocated test), `web/src/lib/smart-plug-import-api.ts` (+ colocated test). `SettingsPage` gets one new import + one new JSX line, mirroring how `InviteGeneratePanel` was added.
- No conflicts detected with the unified project structure — `structural-seed.md` already names `EveHomeXlsxParser`/`MerossCsvParser`/`InProcessChannelJobQueue`/`AzureStorageQueueJobQueue` as expected `Infrastructure/Adapters` members, confirming these exact file names/locations were architecturally pre-planned.
- Migration: one `scripts/add-migration.sh <Name>` call covering `BackgroundJob`, `SmartPlugImport`, and `SmartPlugReading` together (or split if that reads more cleanly) — never run `dotnet ef migrations add` directly against a single provider project (AD-2).

### Testing standards summary

- .NET: xUnit v3 MTP (`xunit.v3.mtp-v2`), Shouldly assertions, NSubstitute mocks against ports, `TestContext.Current.CancellationToken` (not `CancellationToken.None`), Testcontainers (real Postgres) for Api-level tests. One test class per subject, `Snake_case_with_underscores` method names.
- Frontend: Vitest + Testing Library, colocated next to source, `jsdom` environment.
- Use the **real** sample files at `sample-data/eve/*.xlsx` and `sample-data/meross/*.csv` as parser test fixtures — their exact byte-level layout is documented in Task 2 above (verified directly, not assumed from the PRD addendum, which is off by one row for the Eve Home cell references and doesn't mention the `Zuhause:` header row at all).

### References

- [Source: _bmad-artifacts/planning/epics/epic-3-smart-plug-import-baseline-sharpening.md#Story 3.1] — story ACs, epic framing, Story 3.2/3.3 boundaries.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-3, AD-6, AD-7, AD-9, AD-10] — binding rules.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/structural-seed.md] — expected file locations for new adapters.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/deferred.md] — worker/API split explicitly not needed yet; FR-20 generic mapping explicitly deferred.
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/addendum.md#Smart Plug export file schema] — v1 schema reference (superseded in Task 2 by direct inspection of `sample-data/`, which is more precise).
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md#FR-4, FR-5] — functional requirement consequences.
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/cross-cutting-nfrs.md] — Tier 3 async NFR.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md#Information Architecture, State Patterns] — Smart Plug Import IA placement, async-in-progress state description.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/dos-and-donts.md] — status-triad color-reuse prohibition.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/review-rubric.md:37] — confirmed color violation in the State-1 mockup, do not replicate.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-smart-plug-import.html:314-359] — State 1 visual reference for this story's scope.
- [Source: infra/modules/container-app.bicep:168-176, infra/modules/storage-queue.bicep, infra/main.bicep:142-186] — already-live infra config surface.
- [Source: src/EnergyTracker.Api/Program.cs:34-75, 114-141, 269-295, 338-345] — composition-root patterns to mirror.
- [Source: src/EnergyTracker.Application/CreateMeterReading.cs, Ports/ICurrentHouseholdAccessor.cs] — use-case and accessor patterns.
- [Source: src/EnergyTracker.Infrastructure/Adapters/CurrentHouseholdAccessor.cs] — current (HTTP-only) accessor implementation to extend.
- [Source: src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs] — query-filter wiring pattern.
- [Source: tests/EnergyTracker.Architecture.Tests/PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests.cs] — AD-14 guard this story must keep green.
- [Source: sample-data/eve/*.xlsx, sample-data/meross/*.csv] — real fixture files, exact format verified directly during story creation.
- [Source: Directory.Packages.props] — confirmed no xlsx/csv/Azure.Storage.Queues packages present yet.
- [Source: docker-compose.yml] — confirmed no `JobQueue__*` env vars set; local dev relies on the unset-default fallback.
- [Source: _bmad-artifacts/project-context.md] — project-wide coding/testing conventions.

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

- ClosedXML 0.104.2 and 0.105.1 both throw `FormatException` loading `sample-data/eve/*.xlsx` — the real files store their date column as OOXML's ISO-8601 `t="d"` cell type (dimension `A1:B57184`), which ClosedXML's date-parsing eagerly fails on at workbook-*load* time (before any cell is even read), not just on access. Verified directly by inspecting the file's raw `xl/worksheets/sheet1.xml` (`<c r="A5" s="4" t="d"><v>2026-06-20T12:00:18</v></c>`). Swapped `EveHomeXlsxParser` to read the OOXML package directly via `DocumentFormat.OpenXml` (the lower-level SDK ClosedXML itself wraps) instead of adding a workaround on top of ClosedXML. `Directory.Packages.props`/`EnergyTracker.Infrastructure.csproj` reference `DocumentFormat.OpenXml`, not `ClosedXML` (the story's "e.g. ClosedXML" was a suggestion, not a hard requirement).
- Minimal APIs attach antiforgery metadata to any `IFormFile`-binding endpoint by default; `POST /api/smart-plug-imports` 500'd with "no antiforgery middleware found" until `.DisableAntiforgery()` was added (this app has no `app.UseAntiforgery()` — auth via the `/api` group's `RequireAuthorization()` plus the session cookie's `SameSite=Lax` is this app's existing CSRF posture for every other write endpoint).

### Completion Notes List

- Task 1: `IBackgroundJobQueue`/`JobEnvelope<TPayload>` (Application/Ports), `BackgroundJob` entity + config, `InProcessChannelJobQueue` (`System.Threading.Channels`-backed, singleton, paired with `InProcessChannelJobProcessingService` hosted `BackgroundService`), `AzureStorageQueueJobQueue` (polling hosted `BackgroundService`, Base64 message encoding so JSON payloads survive the queue's XML envelope), shared `BackgroundJobProcessor` dispatch loop used by both adapters (inserts the `BackgroundJob` row, dispatches by `JobType`, records `Completed`/`Failed` + `ErrorMessage`). Extended `CurrentHouseholdAccessor` with a new `JobHouseholdContext` (scoped mutable holder) checked first when `HttpContext` is null (AD-3's job-processing resolution path). `GET /api/jobs/{id}` added via `JobEndpoints.cs`, 404 on a cross-Household job id. `Program.cs` reads `JobQueue:Provider` once at the composition root, same switch-on-lowercased-value shape as `Database:Provider`/`Otel:Exporter`.
- Task 2: `ISmartPlugParser` port + `Domain.SmartPlugReading` (parser output fields set at parse time; `HouseholdId`/`SmartPlugImportId`/`PowerPointId`/`RoomName`/`PowerPointName` are `set`, not `init`, since the parser has no Household/import/Power-Point-match context — `ProcessSmartPlugImport` fills them in after matching). `EveHomeXlsxParser` reads the real `sample-data/eve/*.xlsx` layout directly via `DocumentFormat.OpenXml` (see Debug Log — ClosedXML couldn't load these files at all). `MerossCsvParser` parses the literal `"\t,"`-delimited, BOM-prefixed format and derives device identity from the filename via a `GeneratedRegex`. `SmartPlugImport`/`SmartPlugReading` entities + EF configs (mirroring `MeterRegressionPromptConfiguration`'s `Restrict`/`HasIndex(HouseholdId)` pattern) added in the same migration as Task 1's `BackgroundJob` (`AddSmartPlugImportInfrastructure`, both providers).
- Task 3: `POST /api/smart-plug-imports` (`SmartPlugImportEndpoints.cs`) validates the extension (400 otherwise), streams the upload to a temp-disk path (API and job worker are one process/container per AD-6, so no separate blob-storage adapter was built), enqueues a job carrying only the temp path + metadata, returns `202` with the job id. `ProcessSmartPlugImport` resolves the matching parser via `ISmartPlugParser.CanParse`, matches the parsed device tag to an existing Power Point by exact name (`ITaggingScaffoldRepository`, reused rather than a new port), persists `SmartPlugImport`/`SmartPlugReading` via the new `ISmartPlugImportRepository`, and deliberately never calls `IStatusRecomputeService` (AD-7 boundary — Story 3.3's job). An unmatched tag persists as `AwaitingPowerPointMapping`, not a failure. A parse/match exception persists a `Failed` `SmartPlugImport` row before re-throwing so `BackgroundJobProcessor` also records the `BackgroundJob` as `Failed`.
- Task 4: `smart-plug-import-panel.tsx` (dropzone + native file input + drag/drop, `GlassCard` container, plain `outline`/`secondary`/`destructive` Badge variants — not the mockup's status-triad colors, per the Dev Notes' rubric-review flag) added to `SettingsPage` alongside the other Settings panels. `smart-plug-import-api.ts` follows `status-api.ts`'s exact `ApiError`/`toApiError` shape. First polling UI in the codebase: a `useEffect` + `setInterval` keyed off `state === 'processing'`, cleared on unmount and on reaching `completed`/`failed`.
- Task 5: `EveHomeXlsxParserTests.cs`/`MerossCsvParserTests.cs` placed in `tests/EnergyTracker.Infrastructure.Tests/` rather than the story's literal `Application.Tests` path — `EveHomeXlsxParser`/`MerossCsvParser` are `EnergyTracker.Infrastructure` classes, and project-context.md's own "mirrored 1:1 into `{Layer}.Tests`" convention (plus the existing `PostgresMigrationTests`/`SqlServerMigrationTests` precedent in that exact project) points at `Infrastructure.Tests`, not `Application.Tests` (which has no reference to `Infrastructure` and would need one solely to test a different layer's adapters). Real fixtures copied into both `Infrastructure.Tests` and `Api.Tests` via `<Content Include>`. `JobEnvelopeTests.cs` (Application.Tests) confirms `JobEnvelope<TPayload>` and `ProcessSmartPlugImportPayload` round-trip through `System.Text.Json`. `SmartPlugImportEndpointsTests.cs`/`JobEndpointsTests.cs` (Api.Tests, real Postgres via Testcontainers) exercise the actual upload → async-parse → poll-to-`Completed` flow against the real `InProcessChannelJobProcessingService` hosted in the test host, plus the cross-Household 404 IDOR guard. Frontend: `smart-plug-import-api.test.ts` + `smart-plug-import-panel.test.tsx` (6 cases incl. an explicit polling-interval-cleared-on-unmount assertion, using `vi.useFakeTimers`). `EnergyTracker.Architecture.Tests` (incl. `PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests`) confirmed green, untouched.
- Full regression: 260 backend tests green (dotnet build clean in Debug and Release; migration verified against real Postgres and SQL Server via Testcontainers), 113 frontend tests green (10 new), `tsc -b`/`oxlint`/`vite build` all clean. Zero `docs/*.md` changes; zero `infra/` changes (per Dev Notes — this story's Azure footprint is reading `JobQueue:Provider`/`JobQueue:ConnectionString`, already provisioned).

### File List

- `src/EnergyTracker.Domain/BackgroundJob.cs` (new)
- `src/EnergyTracker.Domain/SmartPlugImport.cs` (new)
- `src/EnergyTracker.Domain/SmartPlugReading.cs` (new)
- `src/EnergyTracker.Application/Ports/IBackgroundJobQueue.cs` (new)
- `src/EnergyTracker.Application/Ports/IBackgroundJobRepository.cs` (new)
- `src/EnergyTracker.Application/Ports/ISmartPlugParser.cs` (new)
- `src/EnergyTracker.Application/Ports/ISmartPlugImportRepository.cs` (new)
- `src/EnergyTracker.Application/JobTypes.cs` (new)
- `src/EnergyTracker.Application/GetBackgroundJobStatus.cs` (new)
- `src/EnergyTracker.Application/ProcessSmartPlugImport.cs` (new)
- `src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs` (modified)
- `src/EnergyTracker.Infrastructure/EnergyTracker.Infrastructure.csproj` (modified)
- `src/EnergyTracker.Infrastructure/Configurations/BackgroundJobConfiguration.cs` (new)
- `src/EnergyTracker.Infrastructure/Configurations/SmartPlugImportConfiguration.cs` (new)
- `src/EnergyTracker.Infrastructure/Configurations/SmartPlugReadingConfiguration.cs` (new)
- `src/EnergyTracker.Infrastructure/Adapters/JobHouseholdContext.cs` (new)
- `src/EnergyTracker.Infrastructure/Adapters/CurrentHouseholdAccessor.cs` (modified)
- `src/EnergyTracker.Infrastructure/Adapters/JobMessage.cs` (new)
- `src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs` (new)
- `src/EnergyTracker.Infrastructure/Adapters/BackgroundJobRepository.cs` (new)
- `src/EnergyTracker.Infrastructure/Adapters/InProcessChannelJobQueue.cs` (new)
- `src/EnergyTracker.Infrastructure/Adapters/AzureStorageQueueJobQueue.cs` (new)
- `src/EnergyTracker.Infrastructure/Adapters/EveHomeXlsxParser.cs` (new)
- `src/EnergyTracker.Infrastructure/Adapters/MerossCsvParser.cs` (new)
- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` (new)
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260818160546_AddSmartPlugImportInfrastructure.cs` (new)
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/EnergyTrackerDbContextModelSnapshot.cs` (modified)
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260818160548_AddSmartPlugImportInfrastructure.cs` (new)
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/EnergyTrackerDbContextModelSnapshot.cs` (modified)
- `src/EnergyTracker.Api/Program.cs` (modified)
- `src/EnergyTracker.Api/Endpoints/JobEndpoints.cs` (new)
- `src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs` (new)
- `Directory.Packages.props` (modified)
- `web/src/lib/smart-plug-import-api.ts` (new)
- `web/src/lib/smart-plug-import-api.test.ts` (new)
- `web/src/components/smart-plug-import/smart-plug-import-panel.tsx` (new)
- `web/src/components/smart-plug-import/smart-plug-import-panel.test.tsx` (new)
- `web/src/components/settings/settings-page.tsx` (modified)
- `web/src/locales/en-US/translation.json` (modified)
- `web/src/locales/de-DE/translation.json` (modified)
- `tests/EnergyTracker.Application.Tests/JobEnvelopeTests.cs` (new)
- `tests/EnergyTracker.Infrastructure.Tests/EnergyTracker.Infrastructure.Tests.csproj` (modified)
- `tests/EnergyTracker.Infrastructure.Tests/EveHomeXlsxParserTests.cs` (new)
- `tests/EnergyTracker.Infrastructure.Tests/MerossCsvParserTests.cs` (new)
- `tests/EnergyTracker.Api.Tests/EnergyTracker.Api.Tests.csproj` (modified)
- `tests/EnergyTracker.Api.Tests/SmartPlugImportEndpointsTests.cs` (new)
- `tests/EnergyTracker.Api.Tests/JobEndpointsTests.cs` (new)
