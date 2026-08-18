# Story 3.1: Smart Plug File Upload & Async Parsing

Status: ready-for-dev

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

- [ ] **Task 1: Async job queue infrastructure — AD-6** (AC: #1, #2, #6). This is the first story to touch job processing; nothing below exists yet.
  - [ ] `src/EnergyTracker.Application/Ports/IBackgroundJobQueue.cs`: define `JobEnvelope<TPayload> { Guid JobId; Guid HouseholdId; string JobType; TPayload Payload }` (a plain record — `TPayload` must be JSON-serializable, never a delegate/closure) and `IBackgroundJobQueue { Task EnqueueAsync<TPayload>(JobEnvelope<TPayload> envelope, CancellationToken ct); }`.
  - [ ] New `BackgroundJob` entity (`src/EnergyTracker.Domain/BackgroundJob.cs`) + `BackgroundJobConfiguration.cs`, mirroring `MeterRegressionPrompt`'s shape (`required Guid Id`, `required Guid HouseholdId`, `required string JobType`, a status field, `ErrorMessage` nullable, `CreatedAtUtc`, `CompletedAtUtc` nullable). This is what `GET /api/jobs/{id}` (Task 1.7) reads — job state must be **DB-persisted, not in-memory**, since Azure Container Apps can scale to zero/multiple replicas between enqueue and a client's next poll. Add via `scripts/add-migration.sh <Name>` (both provider projects, AD-2) and wire its `HasQueryFilter` in `EnergyTrackerDbContext.OnModelCreating` next to the other Household-scoped entities (`src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs:51-57`).
  - [ ] `InProcessChannelJobQueue` (`Infrastructure/Adapters`) — `System.Threading.Channels`-backed, paired with a hosted `BackgroundService` that reads envelopes off the channel, inserts the `BackgroundJob` row as the job starts processing, resolves the job's use case, and updates status/`ErrorMessage`/`CompletedAtUtc` on finish. Default adapter — self-host and local dev, zero extra containers.
  - [ ] `AzureStorageQueueJobQueue` (`Infrastructure/Adapters`) — cloud adapter. Add `Azure.Storage.Queues` to `Directory.Packages.props` (not present today — confirmed via grep). **Infra is already deployed and wired — do not touch `infra/`:** `infra/modules/storage-queue.bicep` provisions the Storage Account + `jobs` queue, `infra/main.bicep:142-186` wires it in, and `infra/modules/container-app.bicep:168-176` already injects `JobQueue__Provider=AzureStorageQueue` and `JobQueue__ConnectionString` (via `storage-queue-connection-string` secret) into the Container App env. This story only needs to write the C# that *reads* those two config keys.
  - [ ] `Program.cs`: read `JobQueue:Provider` once at the composition root, same `switch`-on-lowercased-config-value shape as `Database:Provider` (`Program.cs:114-141`) and `Otel:Exporter` (`Program.cs:34-75`) — unset/blank → `InProcessChannelJobQueue` (mirrors `Otel:Exporter`'s "unset stays off" default, not `Database:Provider`'s hard-required default), `"azurestoragequeue"` → `AzureStorageQueueJobQueue` reading `JobQueue:ConnectionString`. `docker-compose.yml` needs no new env var — local dev already gets the InProcess default by omission (confirmed: no `JobQueue__*` keys set there today).
  - [ ] **Extend `ICurrentHouseholdAccessor` for AD-3's second resolution path.** Today `CurrentHouseholdAccessor` (`src/EnergyTracker.Infrastructure/Adapters/CurrentHouseholdAccessor.cs`) resolves *only* from `IHttpContextAccessor.HttpContext?.User` — there is no job-context path yet. Add a small scoped mutable holder (e.g. `JobHouseholdContext { public Guid? HouseholdId { get; set; } }`, registered `AddScoped`) that `CurrentHouseholdAccessor.Resolve()` checks first when `HttpContext` is null; the job-processing `BackgroundService` creates a new `IServiceScopeFactory`-scope per dequeued envelope, sets `JobHouseholdContext.HouseholdId = envelope.HouseholdId` on that scope **before** resolving/invoking the use case, so every downstream query filter (AD-3) sees the right Household with no `IgnoreQueryFilters()`/raw-lookup workaround (AC #6).
  - [ ] `GET /api/jobs/{id}` endpoint (new `JobEndpoints.cs`, registered in `Program.cs` next to the other `api.MapXEndpoints()` calls at `Program.cs:338-345`) — Household-scoped like `MeterReadingEndpoints` (`TryGetHouseholdId` + `ICurrentHouseholdAccessor`, `MeterReadingEndpoints.cs:14-26` pattern); a `BackgroundJob` row belonging to a different Household must 404, not leak existence.

- [ ] **Task 2: Smart Plug parser port & vendor adapters — AD-9** (AC: #3, #4, #5)
  - [ ] `ISmartPlugParser` port (`Application/Ports`) + common `SmartPlugReading` output shape (`Guid PowerPointId?`, denormalized `RoomName`/`PowerPointName`/`DeviceName` display fields per AD-10, `IntervalStart`, `IntervalEnd`, `KwhValue`) — one method, e.g. `IReadOnlyList<SmartPlugReading> Parse(Stream fileContent, string fileName)`.
  - [ ] `EveHomeXlsxParser` (`Infrastructure/Adapters`). **Real sample files exist at `sample-data/eve/*.xlsx` — inspect them directly, don't rely on the PRD addendum's cell references, which are off by one row.** Actual layout (verified against both sample files):
    - Sheet name `Gesamtverbrauch` (confirmed).
    - **A1** = `"Gerät: {device name}"` (device name is prefixed, not bare — e.g. `"Gerät: Steckdose Tür"`).
    - **A2** = `"Raum: {room name}"` (e.g. `"Raum: Wohnzimmer"`).
    - **A3** = `"Zuhause: {home name}"` — a third header row **not mentioned in the PRD addendum at all**; ignore its value (home identity isn't Household-scoping-relevant here) but account for its presence when locating the header/data rows.
    - **Row 4** = column headers: `Datum`, `Gesamtverbrauch (Wh)`.
    - **Row 5+** = data, ~10-minute intervals, value in **Wh** (fractional, e.g. `0.82`) — convert to kWh (÷1000) to match `SmartPlugReading.KwhValue`'s unit. Rows are in **descending** chronological order (newest first) in both sample files — do not assume ascending order.
    - Timestamps parsed as **local time, never UTC-converted** (AC #3) — this is deliberate, documented behavior (AD-9), not a bug: converting corrupts data across midnight boundaries.
  - [ ] `MerossCsvParser` (`Infrastructure/Adapters`). **Real sample files exist at `sample-data/meross/*.csv` — verified byte-for-byte:** UTF-8 with BOM (`EF BB BF`), header line `Date\t,Power Consumption-(kWh)\t\n`, each data line `{YYYY-MM-DD}\t,{kwh value}\t\n` — the field delimiter is literally `\t,` (tab then comma) and every line has a trailing tab before the newline. One row per **day** (filename says "Day Data" — this is daily-aggregate data, coarser granularity than Eve Home's 10-minute intervals; `SmartPlugReading.IntervalStart`/`IntervalEnd` should span the full day for these rows), ascending chronological order in both samples. Device/Power Point identity comes from the **filename**, not the file body: `Power Monitor Day Data - {device} - {YYYYMMDD}.csv` (AC #4) — the file body has no device/room identifier at all.
  - [ ] Add an `.xlsx`-parsing NuGet package (e.g. ClosedXML) to `Directory.Packages.props` (none present today, confirmed via grep) — central package management, never a per-project `Version=`. Meross `.csv` needs no third-party package (plain text split).
  - [ ] `SmartPlugImport` entity (Household-scoped, tracks one uploaded file: `Id`, `HouseholdId`, `BackgroundJobId`, `VendorFormat` enum {EveHome, Meross}, `OriginalFileName`, `Status` enum {Processing, AwaitingPowerPointMapping, Completed, Failed}, `DeviceTag` (parsed device/room name), `CreatedAtUtc`, `CompletedAtUtc`) and `SmartPlugReading` entity (Household-scoped, AD-10 by-value Room/PowerPoint/Device snapshot — see `AD-10` in Dev Notes). EF configs mirroring `MeterRegressionPromptConfiguration.cs`'s pattern (`Restrict` delete behavior, `HasIndex(HouseholdId)`). One migration via `scripts/add-migration.sh` covering both new entities.

- [ ] **Task 3: Upload endpoint & import orchestration** (AC: #1, #2, #6)
  - [ ] `POST /api/smart-plug-imports` (new `SmartPlugImportEndpoints.cs`) — `IFormFile` multipart upload (no `IFormFile`/multipart precedent exists anywhere in this codebase; this is new ground). Validate extension is `.xlsx` or `.csv`, reject anything else with a 400 `ProblemDetails`. On success: write the uploaded file to a short-lived temp location (e.g. a scoped temp-file path locally, or equivalent blob/temp storage in Azure) and enqueue a job via `IBackgroundJobQueue` whose payload carries **only the file's temp-storage reference + metadata** (path/id, original filename, Household id) — **do not embed the raw or base64 file bytes in the envelope.** Azure Storage Queue caps a message at 64 KB; the real Eve Home sample files run several hundred KB uncompressed (see `sample-data/eve/`, and the mockup's own "384 KB" example file), so embedding bytes would work on `InProcessChannelJobQueue` locally and silently fail on `AzureStorageQueueJobQueue` in Azure — exactly the kind of "works on one adapter, breaks on the other" split AD-6 exists to prevent, just via message size rather than serializability. Return `202 Accepted` with the job id immediately — no parsing happens synchronously (AC #1).
  - [ ] `ProcessSmartPlugImport` use case (the job's handler, invoked by the `InProcessChannelJobQueue`'s `BackgroundService`/`AzureStorageQueueJobQueue`'s equivalent dequeue loop) — resolves the vendor parser by file extension, calls `ISmartPlugParser.Parse(...)`, attempts to match the parsed device/room tag to an existing Power Point **by exact name**, persists `SmartPlugImport` + `SmartPlugReading` rows, updates the `BackgroundJob` row to `Completed`/`Failed`.
  - [ ] **Do not call `IStatusRecomputeService` from this use case.** AD-7 names exactly two call sites (`CreateMeterReading`, and Smart-Plug-import-completion) — but Story 3.3's own AC explicitly owns wiring the import-completion → `IStatusRecomputeService` call ("Given a Smart Plug import completes, When processing finishes, Then Status recomputes immediately..."). Wiring it here would be премature: 3.3 hasn't built the gap-handling/interpolation logic yet that determines what a Smart Plug import is even allowed to contribute to the baseline. Leave this story's `SmartPlugImport.Status = Completed` as the sole completion signal.
  - [ ] **Unmatched device/tag:** if no existing Power Point's name matches, persist the `SmartPlugImport` (and its parsed `SmartPlugReading` rows, with `PowerPointId = null`) with `Status = AwaitingPowerPointMapping` rather than failing — this is a well-defined, queryable state. **Do not build the create/map prompt UI for this case** — that is entirely Story 3.2's scope ("Import-to-Power-Point Mapping"). This story's job is done once the import is durably persisted in one of its terminal/awaiting states.

- [ ] **Task 4: Frontend — upload UI & job polling** (AC: #1, #2)
  - [ ] New `web/src/components/smart-plug-import/` folder (matches the existing per-feature grouping — `household-invite`, `tagging-scaffold`, `yearly-baseline`), e.g. `smart-plug-import-panel.tsx`. Add it to `SettingsPage` (`web/src/components/settings/settings-page.tsx`) alongside `YearlyBaselineForm`/`TaggingScaffoldManager`/`InviteGeneratePanel` — per EXPERIENCE.md's IA table, Smart Plug Import is reached via Settings, not its own bottom-nav tab.
  - [ ] Reference [mockups/key-smart-plug-import.html](../planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-smart-plug-import.html) **State 1 only** ("Uploading, non-blocking", lines 314-359) for this story's UI — the dropzone, file-choose control, "Processing" pill + progress bar, and the "we're parsing this in the background" async-note copy. States 2-5 (gap summaries, create/map prompt) belong to Stories 3.2/3.3.
  - [ ] **Do not copy State 1's mockup colors literally.** The accessibility/rubric review (`review-rubric.md:37`) flagged this exact mock's `.processing-pill`/`.complete-check` as a confirmed DESIGN.md violation — it reuses `status-within-range-dark`/`status-below-baseline-dark` (the Status semantic triad) for a non-status upload-progress badge, which DESIGN.md's own Do/Don't table explicitly forbids ("Reuse a status-triad color for chrome, decoration, or a non-status badge"). Use a neutral/brand-chrome treatment instead (existing shadcn Button/Badge variants — per `DESIGN/components.md`'s "Everything else" note, Smart Plug Import uses standard shadcn components unmodified).
  - [ ] `web/src/lib/smart-plug-import-api.ts` — `uploadSmartPlugFile(file: File)` (multipart `POST /api/smart-plug-imports`, `credentials: 'include'`) and `fetchJobStatus(jobId: string)` (`GET /api/jobs/{id}`), following the exact `ApiError`/`toApiError` pattern already established in `web/src/lib/status-api.ts`/`meter-regression-api.ts` (thrown `ApiError` on non-ok, never silently swallowed).
  - [ ] Client-side polling loop for job completion — **no existing precedent in this codebase** (first polling UI). Use a `useEffect` + `setInterval` (or repeated `setTimeout`), cleared on unmount and on terminal job status (`Completed`/`Failed`), surfacing the "Import complete" state once polling resolves.

- [ ] **Task 5: Tests** (AC: all)
  - [ ] `tests/EnergyTracker.Application.Tests/EveHomeXlsxParserTests.cs`, `MerossCsvParserTests.cs` — one test class per subject (project-context.md convention), `Snake_case` method names, Shouldly assertions. Use the real fixtures at `sample-data/eve/*.xlsx` / `sample-data/meross/*.csv` (copy or reference them from the test project) rather than hand-rolled minimal files — assert local-time non-conversion (AC #3) and filename-pattern device matching (AC #4) against the real data shape documented in Task 2.
  - [ ] Job envelope test — confirm `JobEnvelope<TPayload>` round-trips through `System.Text.Json` (or whichever serializer the queue adapters use) with a plain record payload; this is the regression the `deferred-work.md` "Job envelopes must be plain JSON-serializable records" edge case (project-context.md) exists to catch.
  - [ ] `tests/EnergyTracker.Api.Tests/SmartPlugImportEndpointsTests.cs` / `JobEndpointsTests.cs` (Testcontainers, real Postgres — project-context.md convention) — upload → poll `GET /api/jobs/{id}` to `Completed`; a cross-Household job id returns 404 (AD-3, mirrors the existing IDOR-guard pattern used for Room/PowerPoint/Device).
  - [ ] Frontend Vitest tests (colocated, `@testing-library/react`) for the upload panel and `smart-plug-import-api.ts`, including a test that the polling loop clears its interval on unmount.
  - [ ] Confirm `tests/EnergyTracker.Architecture.Tests/PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests.cs` still passes untouched — this story doesn't touch any of its 9 guarded files, so it should stay green automatically; do not add `SmartPlugReading` references to any file in that guard's list (`GetCurrentStatus.cs`, `StatusRecomputeService.cs`, etc.) — that's exactly the AD-14 boundary Story 3.3 will need to respect too.

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

{{agent_model_name_version}}

### Debug Log References

### Completion Notes List

### File List
