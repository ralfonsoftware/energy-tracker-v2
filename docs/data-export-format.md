# Data Export Format ("v2")

This document describes the file `GET /api/household-export` produces. It is
the authoritative reference for reading the export back out — the whole
point of a disaster-recovery backup is that it is never locked into this
product. There is no companion import tool published anywhere yet (Story 7.2
builds one); until then, this document plus any JSON tool is enough to
recover your data.

## Format decision

No file format (JSON, CSV, ZIP, a native database dump) is mandated anywhere
in the PRD, epic, or architecture spine — this was a genuine open decision
made during Story 7.1, disclosed here rather than silently picked.

**Chosen: a single JSON document.** Reasoning:

- The data spans many relational entity types with foreign-key relationships
  between them (a Power Point references its Room, a Smart Plug Reading
  references its Power Point, and so on). A multi-file CSV/ZIP export would
  need its own cross-file referential-integrity scheme — a second problem
  this story doesn't need to solve.
- JSON is self-describing and matches every existing API response shape in
  this codebase already.
- A native database dump (`pg_dump` / `bcp`) would violate this project's
  provider-agnostic requirement outright — Postgres and Azure SQL's native
  dump formats are not interchangeable, and this product runs on either.

## Versioning stance

The top-level `formatVersion` field is always the literal string `"v2"`.
This codebase has no "v1" export format to be compatible with — the PRD's
own requirement (FR-23) is explicit that this is a v2-to-v2 concern only.
`formatVersion` exists so a future format change has somewhere to signal
itself, not because a v1 reader needs to exist today.

## Entity scope

15 Domain types carry a `HouseholdId` (i.e. are Household-scoped data). Of
those, some are included in the export and some are deliberately excluded.
This was a genuine open decision at story-drafting time; the list below is
what Story 7.1 actually shipped.

### Included

| Entity | Why |
|---|---|
| Household (settings only) | Locale, Currency, YearlyBaselineKwh, TrendingThresholdKwh, LowConfidenceGapDays, TariffCheckCadenceMonths, AiPlausibilityEnabled — this is Household *state*, not just rows. |
| HouseholdMember | Needed to know who belongs to the Household on restore. |
| MainMeter | The Household's single meter (v2 has exactly one). |
| MeterReading | Core consumption history. |
| MeterRegressionPrompt | Rollover/reset classification history. |
| Tariff | Full contract history. |
| Event | Logged occurrences, including their AI-correlation result. |
| Room / PowerPoint / Device | Including **archived** (soft-deleted) rows — dropping them would corrupt the by-value snapshots `SmartPlugReading`/`Event` already carry. |
| SmartPlugReading | Measured per-plug interval data. |
| StatusSnapshot | Persisted Trend History — never regenerated from current settings. |
| AuditCorrection | Real historical correction records; excluding them would silently lose genuine record-keeping on a disaster-recovery restore. |

### Excluded

| Entity | Why |
|---|---|
| HouseholdInvite | Its `Token` is a live bearer credential granting full Household membership — a secret, not data. A consumed/expired invite has no restore value either. |
| BackgroundJob, SmartPlugImport, SmartPlugImportGap | Transient job-processing/queue metadata with its own existing 30-day auto-cleanup lifecycle — not household energy data. Re-importing these on a restore would resurrect job rows for uploads that no longer exist. |

### Fields deliberately left out of every included entity

- **EF concurrency tokens** (`Version` columns on Household, MeterReading,
  Tariff). These are optimistic-concurrency plumbing, not data — a restore
  starts every row's concurrency token fresh.
- **`HouseholdId` on every child entity.** The whole document is scoped to
  exactly one Household (the top-level `household.id`), so repeating it on
  every row would only add noise.
- **`SmartPlugReading.SmartPlugImportId`.** `SmartPlugImport` itself is out
  of scope (see above); an exported foreign key pointing at nothing in this
  same document would only confuse a reader. `SmartPlugReading.PowerPointId`
  is kept, since `PowerPoint` rows are themselves in scope.
- **`AiPlausibilityBackendOptions.Model`, `BaseUrl`, and any API key.**
  These are deployment-wide secrets/config, never a per-Household data
  concern. Only the non-secret `Configured`/`Label` pair — the same half
  already exposed by `GET /api/households/{id}/ai-plausibility` — is
  included, under `household.aiPlausibilityBackendConfigured` /
  `household.aiPlausibilityBackendLabel`.

## Enum encoding

Every enum field is encoded as its **lowercase string name**, matching this
codebase's existing manual per-field convention (no global
`JsonStringEnumConverter` exists or is added anywhere in this API). The two
enum fields in this export:

- `meterRegressionPrompts[].classification`: `"reset"` | `"rollover"` | `null`
- `statusSnapshots[].status`: `"withinrange"` | `"belowbaseline"` | `"trending"`

## Top-level shape

```json
{
  "formatVersion": "v2",
  "exportedAtUtc": "2026-09-22T10:00:00+00:00",
  "household": { "...": "see below" },
  "householdMembers": [ "..." ],
  "mainMeter": { "...": "or null if no reading was ever logged" },
  "meterReadings": [ "..." ],
  "meterRegressionPrompts": [ "..." ],
  "tariffs": [ "..." ],
  "events": [ "..." ],
  "rooms": [ "..." ],
  "powerPoints": [ "..." ],
  "devices": [ "..." ],
  "smartPlugReadings": [ "..." ],
  "statusSnapshots": [ "..." ],
  "auditCorrections": [ "..." ]
}
```

## Field-by-field reference

### `household`

| Field | Type | Notes |
|---|---|---|
| `id` | guid | The Household's own id. |
| `createdAtUtc` | datetime (ISO 8601, UTC offset) | |
| `locale` | string | e.g. `"de-DE"`. |
| `currency` | string | ISO 4217 code, e.g. `"EUR"`. |
| `yearlyBaselineKwh` | decimal or null | Unset until the Household configures one. |
| `trendingThresholdKwh` | decimal | |
| `lowConfidenceGapDays` | int | |
| `tariffCheckCadenceMonths` | int | |
| `aiPlausibilityEnabled` | bool | The Household's own on/off toggle. |
| `aiPlausibilityBackendConfigured` | bool | Whether *this deployment* has an AI backend configured at all — deployment-wide, not Household state. |
| `aiPlausibilityBackendLabel` | string or null | The deployment's human-set backend label (e.g. `"Local (LMStudio)"`), or null if unconfigured. |

### `householdMembers[]`

| Field | Type | Notes |
|---|---|---|
| `id` | guid | |
| `externalIssuer` | string | OIDC `iss` claim at membership-creation time. |
| `externalSubjectId` | string | OIDC `sub` claim at membership-creation time. |
| `displayName` | string or null | Captured from the OIDC `name` claim; null if the provider never returned one. |
| `createdAtUtc` | datetime | |

### `mainMeter` (or `null`)

| Field | Type | Notes |
|---|---|---|
| `id` | guid | |
| `createdAtUtc` | datetime | |
| `digitCapacityKwh` | decimal or null | Set once a rollover has been classified; null until then. |

### `meterReadings[]`

| Field | Type | Notes |
|---|---|---|
| `id` | guid | |
| `mainMeterId` | guid | |
| `kwhValue` | decimal | |
| `readingTimestamp` | datetime | The meter's own read time (user-editable/backfillable). |
| `idempotencyKey` | guid | The client-generated key this reading was originally created with. |
| `createdAtUtc` | datetime | Server insert time. |

### `meterRegressionPrompts[]`

| Field | Type | Notes |
|---|---|---|
| `id` | guid | |
| `mainMeterId` | guid | |
| `meterReadingId` | guid | The flagged/lower reading that triggered this prompt. |
| `previousMeterReadingId` | guid | The reading it regressed against. |
| `createdAtUtc` | datetime | |
| `resolvedAtUtc` | datetime or null | |
| `classification` | string or null | `"reset"` \| `"rollover"` \| null (unresolved). |
| `digitCapacityKwh` | decimal or null | Only ever set when `classification == "rollover"`. |

### `tariffs[]`

| Field | Type | Notes |
|---|---|---|
| `id` | guid | |
| `monthlyBaseFee` | decimal | |
| `pricePerKwh` | decimal | |
| `currency` | string | ISO 4217 code — Tariff's own currency, independent of `household.currency`. |
| `contractStartDate` | datetime | |
| `contractPeriodMonths` | int | |
| `createdAtUtc` | datetime | |

### `events[]`

| Field | Type | Notes |
|---|---|---|
| `id` | guid | |
| `description` | string | |
| `occurredAt` | datetime | User-entered/backfillable. |
| `createdAtUtc` | datetime | |
| `taggedEntityType` | string or null | `"Room"` \| `"PowerPoint"` \| `"Device"` \| null. |
| `taggedEntityId` | guid or null | |
| `taggedEntityName` | string or null | By-value snapshot at write time — never re-derived from the live tagged entity, even after a rename/archive/re-parent. |
| `correlationDirection` | string or null | `"Bump"` \| `"Dip"` \| null (no correlation). Set once by the AI correlation job, never recomputed. |
| `correlationComputedAtUtc` | datetime or null | |

### `rooms[]` / `powerPoints[]` / `devices[]`

Includes archived rows.

| Field | Type | Notes |
|---|---|---|
| `id` | guid | |
| `roomId` (PowerPoint only) | guid | |
| `powerPointId` (Device only) | guid | |
| `name` | string | |
| `createdAtUtc` | datetime | |
| `archivedAt` | datetime or null | Null if not archived. |

### `smartPlugReadings[]`

| Field | Type | Notes |
|---|---|---|
| `id` | guid | |
| `powerPointId` | guid or null | Current Power Point attachment, if matched. |
| `roomName` | string | By-value snapshot (AD-10) — never re-derived from the live Room, even after a rename/re-parent. |
| `powerPointName` | string | Same, for Power Point. |
| `deviceName` | string | The device tag as parsed from the source file. |
| `intervalStart` | datetime | |
| `intervalEnd` | datetime | |
| `kwhValue` | decimal | |

### `statusSnapshots[]`

| Field | Type | Notes |
|---|---|---|
| `id` | guid | |
| `status` | string | `"withinrange"` \| `"belowbaseline"` \| `"trending"`. |
| `paceToDateKwh` | decimal | |
| `baselineToDateKwh` | decimal | |
| `isLowConfidence` | bool | |
| `computedAtUtc` | datetime | |

### `auditCorrections[]`

| Field | Type | Notes |
|---|---|---|
| `id` | guid | |
| `entityType` | string | e.g. `"MeterReading"`. |
| `entityId` | guid | |
| `fieldName` | string | |
| `oldValue` | string | Locale-neutral (invariant culture) string representation. |
| `newValue` | string | Same. |
| `correctedAtUtc` | datetime | |

## Worked example

A Household with one Room/Power Point, one Meter Reading, and one Tariff:

```json
{
  "formatVersion": "v2",
  "exportedAtUtc": "2026-09-22T10:00:00+00:00",
  "household": {
    "id": "b6f1f6b0-6b8b-4d3a-9b1a-000000000001",
    "createdAtUtc": "2026-01-05T08:00:00+00:00",
    "locale": "de-DE",
    "currency": "EUR",
    "yearlyBaselineKwh": 3500.0,
    "trendingThresholdKwh": 100.0,
    "lowConfidenceGapDays": 45,
    "tariffCheckCadenceMonths": 3,
    "aiPlausibilityEnabled": false,
    "aiPlausibilityBackendConfigured": true,
    "aiPlausibilityBackendLabel": "Local (LMStudio)"
  },
  "householdMembers": [
    {
      "id": "b6f1f6b0-6b8b-4d3a-9b1a-000000000002",
      "externalIssuer": "https://example-oidc.test/",
      "externalSubjectId": "auth0|abc123",
      "displayName": "Ralf",
      "createdAtUtc": "2026-01-05T08:00:00+00:00"
    }
  ],
  "mainMeter": {
    "id": "b6f1f6b0-6b8b-4d3a-9b1a-000000000003",
    "createdAtUtc": "2026-01-05T09:00:00+00:00",
    "digitCapacityKwh": null
  },
  "meterReadings": [
    {
      "id": "b6f1f6b0-6b8b-4d3a-9b1a-000000000004",
      "mainMeterId": "b6f1f6b0-6b8b-4d3a-9b1a-000000000003",
      "kwhValue": 12345.6,
      "readingTimestamp": "2026-09-01T18:00:00+00:00",
      "idempotencyKey": "b6f1f6b0-6b8b-4d3a-9b1a-000000000005",
      "createdAtUtc": "2026-09-01T18:00:05+00:00"
    }
  ],
  "meterRegressionPrompts": [],
  "tariffs": [
    {
      "id": "b6f1f6b0-6b8b-4d3a-9b1a-000000000006",
      "monthlyBaseFee": 8.50,
      "pricePerKwh": 0.32,
      "currency": "EUR",
      "contractStartDate": "2026-01-01T00:00:00+00:00",
      "contractPeriodMonths": 12,
      "createdAtUtc": "2026-01-05T08:30:00+00:00"
    }
  ],
  "events": [],
  "rooms": [
    {
      "id": "b6f1f6b0-6b8b-4d3a-9b1a-000000000007",
      "name": "Kitchen",
      "createdAtUtc": "2026-01-05T08:15:00+00:00",
      "archivedAt": null
    }
  ],
  "powerPoints": [
    {
      "id": "b6f1f6b0-6b8b-4d3a-9b1a-000000000008",
      "roomId": "b6f1f6b0-6b8b-4d3a-9b1a-000000000007",
      "name": "Counter outlet",
      "createdAtUtc": "2026-01-05T08:16:00+00:00",
      "archivedAt": null
    }
  ],
  "devices": [],
  "smartPlugReadings": [],
  "statusSnapshots": [],
  "auditCorrections": []
}
```
