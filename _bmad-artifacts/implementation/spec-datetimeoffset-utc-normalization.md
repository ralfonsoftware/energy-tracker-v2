---
title: 'Normalize DateTimeOffset to UTC on write so a non-zero wire offset stops being a 500 on Postgres'
type: 'bugfix'
created: '2026-09-18'
status: 'ready-for-dev'
review_loop_iteration: 0
context: ['{project-root}/_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md']
baseline_commit: 'c3fddb121b21e8b6c6758329ba4953994731a70e'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Npgsql refuses to write a `DateTimeOffset` whose offset is not zero to a `timestamp with time zone` column — `ArgumentException: Cannot write DateTimeOffset with Offset=02:00:00 to PostgreSQL type 'timestamp with time zone', only offset 0 (UTC) is supported.` SQL Server's `datetimeoffset` accepts the same value happily. So an identical, entirely legal request either 500s or succeeds depending on which provider the deployment runs — a direct AD-2 dual-provider divergence.

This is not hypothetical, and not a niche input: `project-context.md` mandates the exact format that triggers it — *"All timestamps are `DateTimeOffset`, ISO 8601 with explicit offset on the wire."* A client sending `2026-08-01T09:15:00+02:00` to `POST /api/meter-readings` or `POST /api/events` is doing precisely what the project tells it to do, and gets an unhandled 500 on Postgres (the self-host/local provider). The exception escapes the endpoints' typed `catch` blocks, so it is not even an RFC 7807 `ProblemDetails` response.

Verified by direct probe against a real `postgres:18-alpine` Testcontainer during Story 6.1's code review. Confirmed **project-wide, not Event-specific**: `grep -rn --include="*.cs" -E "ToUniversalTime|UtcDateTime|ToOffset\(" src/` returns **zero** hits, so nothing normalizes anywhere, and all 29 `DateTimeOffset` properties across 17 entities share the exposure — `MeterReading.ReadingTimestamp` and `SmartPlugReading.IntervalStart`/`IntervalEnd` most notably.

Why it has stayed hidden: both React entry points build their payload with `new Date(...).toISOString()`, which always emits `Z`. The bug is one non-browser client away — a mobile client, an integration, a `curl`, or a future import path.

**Approach:** Normalize on write in exactly one place, at the persistence boundary, via an EF Core convention rather than at 29 call sites. In `EnergyTrackerDbContext`, override `ConfigureConventions` and apply a `ValueConverter<DateTimeOffset, DateTimeOffset>` whose write side is `v => v.ToUniversalTime()` and whose read side is the identity. `ToUniversalTime()` preserves the instant exactly — it shifts the offset, never the moment in time — so no stored value changes meaning. The converter must be registered for both `DateTimeOffset` and `DateTimeOffset?`.

This is deliberately *not* fixed at the API/JSON boundary: the divergence is a persistence-layer constraint, and a converter closes it for every current and future write path — including background jobs, importers, and seeding — rather than only for requests that happen to arrive through a controller.

## Boundaries & Constraints

**Always:** Apply the convention symmetrically for both providers — it lives in the shared `EnergyTrackerDbContext`, never in a per-provider subclass or a provider branch (AD-2: one shared context, no per-provider subclass). Register the converter for both `DateTimeOffset` and `DateTimeOffset?`; a nullable-only or non-nullable-only registration silently leaves half the columns exposed (`ArchivedAt`, `EffectiveUntil`, `CompletedAtUtc` and similar are nullable). The fix must hold for every entity, not just the ones a test happens to touch.

**Ask First:** If implementing reveals that any *read* path depends on reading back a non-zero stored offset from existing SQL Server rows, halt and confirm before proceeding. The converter's read side is identity, so previously-stored non-zero offsets continue to read back as stored — this is believed to be a non-issue because every consumer treats these values as instants, but it has not been exhaustively proven against production data and must not be assumed silently.

**Never:** Do **not** change `EveHomeXlsxParser`'s timestamp construction (`EveHomeXlsxParser.cs:203-208`). It builds `new DateTimeOffset(parsedLocalWallTime, TimeSpan.Zero)` and its comment states the reason explicitly: *"Local time, never UTC-converted (AC #3, AD-9) — deliberate, documented behavior: converting would corrupt data across midnight boundaries. TimeSpan.Zero here is not a UTC claim, just the 'apply no conversion' representation."* Because that offset is already zero, `ToUniversalTime()` is a **no-op** there and the deliberate behavior is preserved untouched. **The trap to avoid:** if anyone ever changes that parser to emit a real local offset, this convention would then shift its wall-clock values and reintroduce exactly the midnight-boundary corruption AD-9 exists to prevent. Encode that coupling as a test (Task 4) so the pairing cannot be broken silently.

Do not add a config knob to enable/disable normalization. Do not change any column type — a `ValueConverter` with identical CLR and provider types produces **no schema change and therefore no migration**; if `dotnet ef migrations has-pending-model-changes` reports a pending change, the converter was declared wrong. Do not "fix" this by switching Postgres columns to `timestamp without time zone`; that would discard offset information at the column level and break the portable-subset rule (AD-2).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Request with a non-zero wire offset, Postgres | `POST /api/events` with `occurredAt: "2026-08-01T09:15:00+02:00"` | **200.** Persisted as the same instant, offset `+00:00` (`2026-08-01T07:15:00Z`) | None needed — the write no longer throws |
| Same request, SQL Server | identical payload | **200.** Now also stored as `07:15:00+00:00`, where previously it stored `09:15:00+02:00` | None — deliberate convergence; both providers agree |
| Request with `Z` (every current browser path) | `occurredAt: "2026-08-01T07:15:00Z"` | Unchanged — already offset 0, conversion is a no-op | N/A |
| Negative offset | `"2026-08-01T09:15:00-05:00"` | 200, stored as `14:15:00+00:00` | N/A |
| Nullable property with a non-zero offset | e.g. `ArchivedAt` set from a non-zero-offset value | Normalized identically — this is why `DateTimeOffset?` must be registered | N/A |
| Eve Home import | Parser emits `DateTimeOffset(localWallTime, TimeSpan.Zero)` | **Byte-identical to today** — offset already zero, `ToUniversalTime()` is a no-op; wall-clock values unshifted (AD-9) | N/A |
| Existing SQL Server rows with a stored non-zero offset | Pre-existing data | Read back exactly as stored (read side is identity); no backfill, no rewrite | N/A |
| Value round-trips through the API response | Create then read back | Response echoes the normalized UTC value, not the submitted offset | N/A — a visible but correct behavior change |

</frozen-after-approval>

## Code Map

- `src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs` — add a `protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)` override registering the UTC-normalizing `ValueConverter` for `DateTimeOffset` and `DateTimeOffset?`. This class already owns the other cross-cutting invariant (the AD-3 query filters in `OnModelCreating`), so it is the established home for context-wide rules.
- `src/EnergyTracker.Infrastructure/Converters/UtcDateTimeOffsetConverter.cs` *(new)* — the converter, with a comment stating why it exists (Npgsql's offset-0-only rule), that `ToUniversalTime()` preserves the instant, and the AD-9/`EveHomeXlsxParser` coupling from Boundaries.
- `src/EnergyTracker.Infrastructure/Adapters/EveHomeXlsxParser.cs:203-208` — **unchanged**; reference only. Its `TimeSpan.Zero` construction is what makes the convention a no-op for AD-9 data.
- `tests/EnergyTracker.Infrastructure.Tests/` — new dual-provider test class (see Task 3); follow the `EventRepositoryTests` shape (abstract base + `Postgres*`/`SqlServer*` subclasses) so both engines run the same assertions.
- `tests/EnergyTracker.Architecture.Tests/` — guard test for the AD-9 coupling (Task 4).

## Tasks & Acceptance

**Execution:**

- [ ] `src/EnergyTracker.Infrastructure/Converters/UtcDateTimeOffsetConverter.cs` — Add the `ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v)` with the explanatory comment — one place that closes the divergence for every write path.
- [ ] `src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs` — Override `ConfigureConventions` and apply the converter to `Properties<DateTimeOffset>()` **and** `Properties<DateTimeOffset?>()` — covers all 29 properties across 17 entities without touching a single entity class.
- [ ] Confirm no migration is produced: run `dotnet ef migrations has-pending-model-changes` against **both** provider projects and expect "No changes". A pending change means the converter altered the provider type and must be corrected, not migrated away.
- [ ] `tests/EnergyTracker.Infrastructure.Tests/` — Add a dual-provider test class asserting a non-zero-offset write succeeds and round-trips to the same instant on Postgres *and* SQL Server — the regression test for the actual bug, which only a real-engine Testcontainer can catch.
- [ ] `tests/EnergyTracker.Architecture.Tests/` — Add a guard test pinning the AD-9 coupling (Task 4 detail below).
- [ ] `_bmad-artifacts/implementation/deferred-work.md` — Mark the "Npgsql rejects a non-zero-offset `occurredAt`" entry (under *Deferred from: code review of story-6.1*) resolved by this spec, so the backlog does not carry a fixed item.

**Task 4 detail — the AD-9 guard.** Assert that `EveHomeXlsxParser` constructs its `DateTimeOffset` with `TimeSpan.Zero`, with a failure message explaining the coupling: the UTC-normalizing convention is only safe for Eve Home data *because* that offset is already zero; emitting a real local offset there would make the convention shift wall-clock values across midnight boundaries, the exact corruption AD-9 forbids. A source-scan test in the `Architecture.Tests` project matches this repo's existing precedent for encoding a spine invariant as an executable guard (`PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests`).

**Acceptance Criteria:**

- Given a `DateTimeOffset` with a non-zero offset written through any repository, when it is saved on **Postgres**, then it persists successfully and reads back as the same instant with offset `+00:00` — no `ArgumentException`, no 500.
- Given the same value written on **SQL Server**, then it persists as the same instant with offset `+00:00`, so both providers store an identical value for identical input (AD-2).
- Given an offset-0 value (every current browser-originated write), when saved, then the stored value is byte-identical to today's behavior.
- Given an Eve Home import, when parsed and saved, then every `IntervalStart`/`IntervalEnd` wall-clock value is unchanged from current behavior (AD-9).
- Given the model after the change, when `dotnet ef migrations has-pending-model-changes` runs against both provider projects, then no schema change is detected.

## Verification

**Commands:**

- `dotnet build` — expected: clean, no new warnings.
- `dotnet test tests/EnergyTracker.Infrastructure.Tests` — expected: all pass, including the new dual-provider offset round-trip on both engines. Requires Docker (Testcontainers starts real Postgres and SQL Server).
- `dotnet test tests/EnergyTracker.Architecture.Tests` — expected: all pass, including the new AD-9 guard.
- `dotnet test tests/EnergyTracker.Application.Tests` and `dotnet test tests/EnergyTracker.Api.Tests` — expected: all pass; run each project separately, the MTP runner rejects multiple project paths in one invocation.
- `dotnet ef migrations has-pending-model-changes --project src/EnergyTracker.Infrastructure.Migrations.Postgres` and the `.SqlServer` equivalent — expected: no pending model changes.

**Manual probe (the original repro):** write an `Event` with `OccurredAt = new DateTimeOffset(2026, 8, 1, 9, 15, 0, TimeSpan.FromHours(2))` through `EventRepository` against a real Postgres container. Before this change that throws `DbUpdateException` → `ArgumentException: … only offset 0 (UTC) is supported.` After, it must persist and read back as `2026-08-01T07:15:00+00:00`.
