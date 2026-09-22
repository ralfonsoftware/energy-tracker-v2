# Data Import / Restore ("v2")

This document describes `POST /api/household-import` and
`POST /api/household-import/{token}/confirm` — the companion to
[`data-export-format.md`](./data-export-format.md)'s export. Together they
let a Household restore itself after a disaster, or move to new hosting, by
re-importing a file `GET /api/household-export` produced.

## Scope decision (v2-to-v2 only)

This mechanism reads **only** the `formatVersion: "v2"` document
`data-export-format.md` describes. It does not read, sniff, or convert any
other format — there is no "v1" concept anywhere in this codebase to be
compatible with (PRD FR-23 is explicit that this is a v2-to-v2 concern
only). A file with any other `formatVersion` value (including a missing
one) is rejected outright as its own single validation failure, before any
other field is even inspected.

## The two-phase flow

Restoring is destructive — it wholesale-replaces every row of a
Household's data — so it never happens as a single request. Two phases,
two requests:

1. **`POST /api/household-import`** — synchronous upload + structural
   validation. The uploaded file is parsed and checked against the exact
   v2 shape (every required field present, every value the right JSON
   type, every enum string a recognized name). No database write of any
   kind happens in this phase — validation failure is structural by
   construction, not by convention (AC #2's "never partially applied").
   - On failure: `400 ProblemDetails` with every collected failure listed
     under the `failures` extension property (not just the first one
     found) — this codebase's `errorCode`/extension-property convention
     for structured error detail (`EventEndpoints.cs`'s own precedent),
     applied here to a list rather than a single stable code, since a
     structural failure is inherently per-field free text, not a fixed
     catalog entry.
   - On success: `200 OK` with an opaque `token` (a server-generated
     `Guid`, never the server's own temp file path — leaking a filesystem
     path to the client would be its own disclosure risk) and a `summary`
     (entity counts) the client renders in the confirmation dialog. The
     uploaded file is **not** re-uploaded for the next step — the server
     keeps it, keyed by `token`, for a short time (currently 30 minutes;
     see "Where the uploaded file lives" below).
2. **`POST /api/household-import/{token}/confirm`** — the explicit
   "replace all data" confirmation (AC #4). Verifies the token belongs to
   the *current* authenticated Household, then enqueues the actual
   wholesale delete+insert as an **async background job** (AD-6) — the
   restore itself never runs on the request thread. Returns `202 Accepted`
   + a `jobId`; the client polls the existing generic `GET /api/jobs/{id}`
   endpoint to completion, exactly like every other async job in this
   codebase (Smart Plug import, cleanup, AI correlation). No backend
   change was needed to `GET /api/jobs/{id}` itself.

**Every restore requires the confirmation step, even for a Household with
zero existing rows.** A single code path is simpler and safer than
branching on a 12-table "does this Household have any data" check for a
UX step that costs nothing when there's nothing to lose.

## Where the uploaded file lives between the two requests

The validated file is written to local temp disk (mirroring
`SmartPlugImportEndpoints`' own upload path) and the `(token → temp file
path, HouseholdId)` mapping is held in an **in-memory registry**
(`MemoryHouseholdImportUploadRegistry`), not a database row. This was a
genuine choice between the two options the story allowed:

- This app runs its API and background-job worker as **one process, one
  container** (AD-6) — Story 3.x's own upload path already makes this
  assumption for the exact same reason (a job payload only ever carries a
  temp-file path, never the bytes). Nothing else in this codebase's
  deployment story (Container Apps scale-**to-zero** when idle, not
  scale-**out** to many concurrent replicas) suggests the API tier runs as
  more than one replica at a time.
- Given that, a database row would only add migration/table ceremony for
  no correctness benefit over a process-local in-memory map.

**Disclosed trade-off:** if the container restarts (redeploy, scale-to-zero
cold start, crash) between the validate and confirm requests, the token is
lost and the temp file becomes an orphan — the member must re-upload and
re-validate. This is judged acceptable: the window between the two
requests is normally seconds (a human reading a confirmation dialog), and
losing a not-yet-confirmed upload is far less bad than a restore silently
targeting the wrong data. Entries also expire after 30 minutes regardless,
bounding how long an abandoned upload's temp file can linger. If this
deployment's operational model ever changes to multiple concurrent API
replicas, this registry would need to move to a shared store (a DB row, or
a distributed cache) — a disclosed future concern, not a silent gap.

## Upload size limit

**250 MB.** The 20 MB cap `SmartPlugImportEndpoints.MaxFileSizeBytes` uses
does not transfer here — a full household export (Story 7.1's own
live-verification export of one real, fairly small household was already
32 MB) can run much larger once Smart Plug Reading history accumulates
over years. 250 MB is generous headroom while still bounding what a single
upload can occupy on temp disk.

Two framework defaults sit below this and are raised to match, at the
composition root (`Program.cs`):

- Kestrel's `MaxRequestBodySize` (default ~28.6 MB / 30,000,000 bytes) —
  raised to `MaxFileSizeBytes + BodySizeHeadroomBytes` (5 MB headroom, for
  the raw multipart body's boundaries/headers overhead), but **only for
  `/api/household-import`**, via route-conditional middleware that sets
  `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize` before
  routing/endpoint execution runs — an in-handler override would be too
  late, since Minimal API's `IFormFile` model binding reads the whole
  multipart body as part of invoking the endpoint, before the lambda body
  itself ever runs; middleware registered ahead of that still gets there
  first. Every other route (including `SmartPlugImportEndpoints`) keeps
  Kestrel's original ~28.6 MB default (code review, Story 7.2 Pass 1: an
  earlier version of this raised the ceiling process-wide, which silently
  widened `SmartPlugImportEndpoints`' own exposure too, since Kestrel would
  then buffer up to 250 MB there before that endpoint's own lower 20 MB
  app-level check ever got a chance to run).
- `FormOptions.MultipartBodyLengthLimit` (default 128 MB) — the limit
  ASP.NET Core's multipart form reader enforces independently of Kestrel's
  own body-size ceiling. Raised globally, harmlessly: it's a secondary,
  form-parsing-stage limit only reached after Kestrel's own (now correctly
  scoped) gate has already let the body through.

The dev-only Vite proxy (`web/vite.config.ts`) forwards `/api` to the local
API process with no body-size limit of its own (Node's `http-proxy`
imposes none by default) — no change was needed there.

## Restore semantics

### Write target: always the current session's Household

Restore **always** writes into the Household the current authenticated
session already resolves to
(`ICurrentHouseholdAccessor.HouseholdId`/`JobHouseholdContext.HouseholdId`
inside the background job) — it never creates a new Household row, and it
never reuses the import file's own `household.id` as a real primary key.

This is a direct consequence of how Household resolution actually works in
this codebase: `CurrentHouseholdAccessor` resolves the current Household by
looking up the authenticated principal's `(ExternalIssuer,
ExternalSubjectId)` against `HouseholdMembers` — never by a stored
session/cookie Household id. On "move to new hosting," the very first
authenticated visitor already went through FR-26 and got a **freshly
created** Household with a new `Id` before ever reaching this Settings
screen; restore replaces that fresh Household's *contents*, it does not
resurrect the old deployment's Household row under its old id.

Because `data-export-format.md`'s own format deliberately **excludes**
`HouseholdId` from every child entity in the file (the whole document is
implicitly scoped to one Household), there is nothing to remap on the way
in: every reconstructed entity simply gets `HouseholdId = <current
Household's id>` set directly. Every entity's *internal* cross-reference
(`PowerPoint.RoomId`, `Device.PowerPointId`, `MeterReading.MainMeterId`,
`MeterRegressionPrompt.MeterReadingId`/`PreviousMeterReadingId`,
`SmartPlugReading.PowerPointId`) is reused byte-for-byte from the file,
since those references were never Household-qualified in the first place
and the file's internal graph is already self-consistent — confirmed
against this codebase's real `*Configuration.cs` Fluent API files (every
FK between these entity types is `DeleteBehavior.Restrict`, never
`Cascade`, so nothing here ever depended on an implicit cascade either).

### Session continuity across a restore

`HouseholdMember` rows are deleted and reinserted like everything else.
Because `CurrentHouseholdAccessor` resolves by issuer+subject (not by a
stable row id), the person performing the restore keeps a working session
across the operation **as long as their own `(ExternalIssuer,
ExternalSubjectId)` is present among the imported members** — true by
construction for the disaster-recovery/self-restore case (same person,
same OIDC provider) and for the move-to-new-hosting case (same identity,
new deployment).

If it is ever *not* present (e.g. deliberately restoring someone else's
export under a different identity), the current principal loses their
Household on the very next request after the job completes and is routed
back into FR-26's Household-creation flow. This is a real, disclosed edge
case — not solved by this feature — rather than a bug: the restore's own
DB work runs inside the async background job, which resolves `HouseholdId`
from the job envelope (`JobHouseholdContext`), never from a live HTTP
principal, so the destructive write itself never depends on a
`HouseholdMember` row lookup mid-flight. Continuity only matters for the
*next* request after the job finishes.

### What gets replaced, and how

Every in-scope entity **except Household itself** is fully deleted for the
current Household, then every row from the file is inserted verbatim,
reusing each row's original `Id` from the file:

```
delete order (children first):
  AuditCorrection → StatusSnapshot → SmartPlugReading → Event → Tariff →
  HouseholdMember → MeterRegressionPrompt → MeterReading → Device →
  PowerPoint → Room → MainMeter

insert order (parents first):
  Household (updated in place) → MainMeter → Room → PowerPoint → Device →
  MeterReading → MeterRegressionPrompt → HouseholdMember → Tariff →
  Event → SmartPlugReading → StatusSnapshot → AuditCorrection
```

**Household's own row is the one exception.** Its settings fields
(`Locale`, `Currency`, `YearlyBaselineKwh`, `TrendingThresholdKwh`,
`LowConfidenceGapDays`, `TariffCheckCadenceMonths`, `AiPlausibilityEnabled`)
are updated in place — the row itself is never deleted/recreated, since
it's the live tenant root the current request/session is already anchored
to. `Household.Id` and `Household.CreatedAtUtc` are never touched.
`aiPlausibilityBackendConfigured`/`aiPlausibilityBackendLabel` in the file
are deployment-level, read-only-at-export-time fields — **never** written
back; this deployment's own `AiPlausibilityBackendOptions` configuration
stays authoritative (AD-19).

`SmartPlugReading.SmartPlugImportId` is always `null` on insert — the
field isn't in the export file at all (`SmartPlugImport` stays out of
scope, unchanged from the export's own decision).

**This is a wholesale replace, not an edit.** The restore path never calls
`IAuditCorrectionRecorder` (AD-11's own explicit carve-out for exactly this
mechanism) — a restore does not produce audit-correction rows the way an
in-app Meter Reading/Tariff edit does.

### Bulk-write safety (chunking, timeouts, one transaction)

A full-household restore is structurally the same "large bulk write" shape
that caused four real production incidents in Epic 3
(`DELETE /api/smart-plug-import-jobs?deleteAll=true`, see
`spec-3-10-cleanup-*.md`) — just across all 12 entity categories instead of
one, plus an insert phase the cleanup story never had. This restore reuses
those incidents' concrete, proven fixes rather than re-deriving new ones:

- **200-row chunks** for every delete and every insert
  (`SmartPlugImportRepository.DeleteBatchSize`'s own proven-safe value
  against both providers' per-statement parameter ceilings).
- **`CommandTimeout` bumped to 120s** before the bulk work starts (the
  app-wide default is tuned for point queries, not this shape).
- **One outer transaction spans every chunk** — delete and insert alike —
  never per-chunk transactions. This is what makes "never partially
  applied" true for the destructive phase, even though AC #2's wording is
  about the validation phase.
- **The whole operation runs as an async background job**, never
  synchronously on the request thread — Story 3.10's own incident
  happened *after* every individual command was already bounded; total
  wall-clock work still exceeded Container Apps' ~240s synchronous HTTP
  ingress ceiling.
- **`SmartPlugReading` is deleted as its own explicit chunked step**,
  before `Room`/`PowerPoint` — never left to an implicit FK cascade as one
  unbounded operation (Story 3.10's 4th incident was exactly this shape).

**Explicitly accepted, not mitigated:** SQL Server lock-escalation risk
under one large transaction is the same accepted trade-off Story 3.10's own
incident response settled on — not re-litigated here.

### AD-2 compliance

Plain, portable EF Core LINQ only (`ExecuteDeleteAsync`, `AddRangeAsync`,
`SaveChangesAsync`) — no `EFCore.BulkExtensions`/AD-23 mechanism. AD-23's
provider-branching exception is scoped narrowly to
`SmartPlugImportRepository.AddAsync`; reusing it here would need its own
new AD amendment, out of this story's scope. If a plain chunked
`AddRangeAsync` loop ever proves too slow for a very large
`SmartPlugReading` volume in practice, that is a disclosed future
trade-off/spike (mirrors the export side's own Tier-2-sync disclosure),
not something to silently work around by borrowing AD-23's mechanism.

### Why the DateTimeOffset value-comparer gap does not apply here

`spec-datetimeoffset-utc-normalization.md` flags a deferred gap: EF Core's
default `DateTimeOffset` value comparer compares only the UTC instant, so
an **`UPDATE`** reassigning an already-tracked row's `DateTimeOffset`
property to an instant-equal-but-different-offset value can be silently
skipped by change tracking. This restore design never does that:

- Every entity except `Household` is deleted, then **inserted fresh** — an
  `INSERT` is unaffected by the value comparer (the gap is specifically
  about `UPDATE`s being skipped as a no-op).
- `Household` **is** updated in place, but none of its updated fields is a
  `DateTimeOffset`. `Household.CreatedAtUtc` (the one `DateTimeOffset`
  field) is never written on restore.

If a future change ever turns any part of this into an "upsert instead of
delete+insert" optimization, re-open this question before shipping it —
that is precisely the shape the deferred item warns about.

## Validation failure reporting

A `400` from `POST /api/household-import` reports **every** structural
problem found, not just the first — a `ProblemDetails` body with a
`failures` array of plain-English descriptions, e.g.:

```json
{
  "detail": "The uploaded file failed validation against the v2 export format.",
  "status": 400,
  "failures": [
    "Unsupported formatVersion 'v1'. Only 'v2' is accepted.",
    "meterReadings[2]: 'kwhValue' must be a number.",
    "meterRegressionPrompts[0]: 'classification' must be one of [reset, rollover] or null, got '\"Unknown\"'."
  ]
}
```

A `formatVersion` mismatch (including a missing one) is reported **alone**
— every other field's shape is unknowable for a document that isn't even
claiming to be this format, so piling on unrelated "missing field" noise
would bury the one failure that actually matters.

## What's out of scope (unchanged from the export side)

Same entity-scope decision as `data-export-format.md`: `HouseholdInvite`,
`BackgroundJob`, `SmartPlugImport`, and `SmartPlugImportGap` are never
read, written, or otherwise touched by a restore.
