---
title: 'Normalize DateTimeOffset to UTC on write so a non-zero wire offset stops being a 500 on Postgres'
type: 'bugfix'
created: '2026-09-18'
status: 'done'
review_loop_iteration: 1
context: ['{project-root}/_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md']
baseline_commit: '7d4a8103efa5b168c8b04d17c4be060f918d0330'
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
- `src/EnergyTracker.Infrastructure/Adapters/EventRepository.cs` — **must reload the entity after `SaveChangesAsync` before returning it** (Task 3a below). An EF Core `ValueConverter` only translates the CLR↔provider boundary during the actual write/read; it never rewrites the CLR property of an entity `SaveChangesAsync` already has tracked. Without a reload, `AddAsync` hands back the exact instance the caller passed in — still holding the client's original, non-normalized offset — and `EventEndpoints.ToResponse` builds the API response straight from that instance. Found by review loop 1 (Blind Hunter): this silently violates the frozen I/O matrix row "Value round-trips through the API response."
- `src/EnergyTracker.Infrastructure/Adapters/MeterReadingRepository.cs` — same reload requirement on `AddAsync`'s fast-path `return reading;` (line ~58). The existing `catch (DbUpdateException)` race path already returns a freshly-queried `winner` (already correct — a fresh query goes through the converter's read side), so only the fast (non-conflict) path needs the fix.
- `src/EnergyTracker.Infrastructure/Adapters/TariffRepository.cs` — **(added, review loop 2)** same reload requirement, on both `AddAsync` and `UpdateAsync`. `Tariff.ContractStartDate` is a client-supplied `DateTimeOffset` created/edited by `POST /api/tariffs`/`PUT /api/tariffs/{id}` and echoed straight from the tracked instance by `TariffEndpoints.ToResponse` — the exact same shape as Event/MeterReading, missed by loop 1's audit. **Every repository with this shape is now accounted for** (audited via: every `record ... Request(...)` in `src/EnergyTracker.Api/Endpoints/*.cs` carrying a client-supplied `DateTimeOffset` that flows to a create/update-then-echo response — only `Event.OccurredAt`, `MeterReading.ReadingTimestamp`, and `Tariff.ContractStartDate` qualify; every other `DateTimeOffset`/`DateTimeOffset?` response field is server-computed via `DateTimeOffset.UtcNow`, already offset zero, so the converter is a no-op for it and no stale-echo risk exists).
- `tests/EnergyTracker.Infrastructure.Tests/` — new dual-provider test class (see Task 3); follow the `EventRepositoryTests` shape (abstract base + `Postgres*`/`SqlServer*` subclasses) so both engines run the same assertions. Must cover `Event.OccurredAt`, `MeterReading.ReadingTimestamp`, **and (review loop 2) `Tariff.ContractStartDate`** — the spec's own Problem section names the first two as "most notably" exposed, and loop 2 found the third had the identical unfixed gap; testing only some leaves the others' regression paths unverified. Must also assert (via the *same* tracked instance `AddAsync`/`UpdateAsync` returns, no fresh `DbContext`) that the returned entity already reflects the normalized `+00:00` offset — this is the regression test for the Task 3a fix, and the one Blind Hunter's loop-1 finding says was missing. **(review loop 2)** Must also cover `MeterReadingRepository.AddAsync`'s `catch (DbUpdateException)` "winner" conflict path directly (force the same `IdempotencyKey` twice), not just its fast path — the code comment claiming the winner path "already goes through the converter's read side" was previously unverified.
- `tests/EnergyTracker.Infrastructure.Tests/` — one additional fast, Docker-free unit test directly exercising `UtcDateTimeOffsetConverter`'s conversion delegates (no `DbContext`, no Testcontainer) asserting the write side (`ToUniversalTime()`) and read side (identity) preserve the instant. Closes the gap that today every assertion of this property requires Docker. **(review loop 2)** Use the delegates' own typed `Func<DateTimeOffset, DateTimeOffset>` directly (`ConvertToProviderExpression.Compile()`/`ConvertFromProviderExpression.Compile()`), not `DynamicInvoke(...)!` — the converter's `TModel`/`TProvider` are both `DateTimeOffset`, so the compiled delegate is already correctly typed and a null-forgiving cast is unnecessary risk.
- `tests/EnergyTracker.Architecture.Tests/` — guard test for the AD-9 coupling (Task 4), hardened per the amendment below and its loop-2 follow-up.
- `tests/EnergyTracker.Api.Tests/` — **(added, review loop 2)** one end-to-end HTTP-level test per affected create/update endpoint (`POST /api/events`, `POST /api/meter-readings`, `POST /api/tariffs`) asserting the actual JSON response echoes a normalized `+00:00` offset for a non-zero wire offset — AC #6 is phrased in terms of "the response's timestamp field," but every test added in loop 1 asserted only the repository's return value; a future bug in `ToResponse`'s own mapping would not have been caught by those alone.
- `_bmad-artifacts/implementation/deferred-work.md` — see Task 6's amended wording: keep the entry, do not delete it.

## Tasks & Acceptance

**Execution:**

- [x] `src/EnergyTracker.Infrastructure/Converters/UtcDateTimeOffsetConverter.cs` — Add the `ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v)` with the explanatory comment — one place that closes the divergence for every write path.
- [x] `src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs` — Override `ConfigureConventions` and apply the converter to `Properties<DateTimeOffset>()` **and** `Properties<DateTimeOffset?>()` — covers all 29 properties across 17 entities without touching a single entity class.
- [x] Confirm no migration is produced: run `dotnet ef migrations has-pending-model-changes` against **both** provider projects and expect "No changes". A pending change means the converter altered the provider type and must be corrected, not migrated away.
- [x] **(Task 3a, new)** `src/EnergyTracker.Infrastructure/Adapters/EventRepository.cs` and `src/EnergyTracker.Infrastructure/Adapters/MeterReadingRepository.cs` — after a successful `SaveChangesAsync` on the create path, reload the just-saved entity (e.g. `await dbContext.Entry(entity).ReloadAsync(cancellationToken);`) before returning it, so the returned instance reflects the UTC-normalized value the converter actually persisted — not the client's original offset still sitting in the CLR property. `Entry(...).Reload()` re-materializes from the database (through the converter's identity read side), which EF can do to an `init`-only property even though C# itself would refuse a post-construction assignment. This is the concrete fix for the frozen I/O matrix row "Value round-trips through the API response."
- [x] `tests/EnergyTracker.Infrastructure.Tests/` — Add a dual-provider test class asserting a non-zero-offset write succeeds and round-trips to the same instant on Postgres *and* SQL Server, covering **both** `Event.OccurredAt` and `MeterReading.ReadingTimestamp`, and asserting the *same returned instance* (no fresh `DbContext`) already carries the normalized offset post-Task-3a-fix — the regression test for the actual bug and for Task 3a, which only a real-engine Testcontainer can catch.
- [x] `tests/EnergyTracker.Infrastructure.Tests/` — Add one plain unit test on `UtcDateTimeOffsetConverter` itself (no `DbContext`/Testcontainer) asserting its write-side and read-side delegates preserve the instant for a non-zero and a negative offset.
- [x] `tests/EnergyTracker.Architecture.Tests/` — Add a guard test pinning the AD-9 coupling (Task 4 detail below, hardened per the amendment).
- [x] `_bmad-artifacts/implementation/deferred-work.md` — **(amended wording)** Append a short note to the existing "Npgsql rejects a non-zero-offset `occurredAt`" entry (under *Deferred from: code review of story-6.1*) recording that it is resolved by this spec (e.g. "**Resolved by `spec-datetimeoffset-utc-normalization.md`.**"). Do **not** delete the entry — review loop 1 (Blind Hunter) flagged that deleting it loses the audit breadcrumb of why/when/by-what it was closed; "mark ... resolved" means annotate in place, not remove.
- [x] `_bmad-artifacts/implementation/deferred-work.md` — Append two new entries (`source_spec: spec-datetimeoffset-utc-normalization.md`) for issues raised by review loop 1 that are real but out of this spec's scope: (1) `ToUniversalTime()` on a `DateTimeOffset` near `DateTimeOffset.MinValue`/`MaxValue` combined with a non-zero offset can throw `ArgumentOutOfRangeException` — not reachable today (both current write paths already bound input via a floor/ceiling clock-skew check) but would surface as a new unhandled exception for any future import path that accepts unranged timestamps (Blind Hunter, loop 1). (2) EF Core's default `DateTimeOffset` value comparer compares only the UTC instant, ignoring offset — a future backfill/normalization write that reassigns an existing non-zero-offset SQL Server row to an instant-equal value could be silently skipped by change tracking; not reachable today since this spec explicitly does no backfill/rewrite of existing rows, but must be considered before any future backfill work (Edge Case Hunter, loop 1).
- [x] **(review loop 2)** `src/EnergyTracker.Infrastructure/Adapters/TariffRepository.cs` — Apply the identical Task 3a reload fix to both `AddAsync` and `UpdateAsync` (Blind Hunter's loop-2 finding: this repository has the exact same client-supplied-`DateTimeOffset`-echoed-in-response shape as Event/MeterReading, missed by loop 1's audit).
- [x] **(review loop 2)** Extend the dual-provider test class with `Tariff.ContractStartDate` coverage (both `AddAsync` and `UpdateAsync`, same-instance assertion), and a `MeterReadingRepository.AddAsync` conflict-path ("winner") test proving that path also returns a normalized offset.
- [x] **(review loop 2)** Fix `UtcDateTimeOffsetConverterUnitTests`' `.Compile().DynamicInvoke(...)!` to use the delegates' own correctly-typed `Func<DateTimeOffset, DateTimeOffset>` directly instead.
- [x] **(review loop 2)** `tests/EnergyTracker.Api.Tests/` — Add one end-to-end test per affected endpoint (`EventEndpointsTests`, `MeterReadingEndpointsTests`, `TariffEndpointsTests`) asserting the actual HTTP JSON response echoes a normalized offset, closing the gap that every loop-1 test asserted only the repository return value.
- [x] **(review loop 2)** Harden the AD-9 guard test (Task 4 detail) against a nested `new DateTimeOffset(...)` call fooling a substring check, and against a whole-line `//` comment being misread as code.

**Task 4 detail — the AD-9 guard (hardened per review loop 1, then loop 2).** Assert that `EveHomeXlsxParser` constructs its `DateTimeOffset` with `TimeSpan.Zero`, with a failure message explaining the coupling: the UTC-normalizing convention is only safe for Eve Home data *because* that offset is already zero; emitting a real local offset there would make the convention shift wall-clock values across midnight boundaries, the exact corruption AD-9 forbids. A source-scan test in the `Architecture.Tests` project matches this repo's existing precedent for encoding a spine invariant as an executable guard (`PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests`). Loop-1 Edge Case Hunter findings closed: (a) don't just assert *a* zero-offset `new DateTimeOffset(` call exists — enumerate every `new DateTimeOffset(` construction in the file and assert **each one** uses `TimeSpan.Zero`, so a second, non-zero-offset construction added elsewhere in the same file cannot slip past the guard; (b) don't rely on a single-level-nested-parens regex (`[^()]|\([^()]*\)`-style) that false-fails a compliant refactor with deeper nesting — use a small balanced-paren scan (find `new DateTimeOffset(`, then walk forward counting `(`/`)` to find the matching close) to extract each call's full argument text before checking it for `TimeSpan.Zero`. Loop-2 Edge Case Hunter/Blind Hunter findings closed: (c) a plain substring check on the whole argument-list text is fooled by a *nested* `new DateTimeOffset(x, TimeSpan.Zero)` call inside an outer, non-zero-offset call's own arguments — split each call's top-level arguments (respecting nested parens) and require `TimeSpan.Zero` among them directly, not merely present as a nested substring; (d) resume the outer scan from just after each call's opening paren (not past its whole argument list) so a nested `new DateTimeOffset(` call is still found as its own call; (e) strip whole-line `//` comments before scanning, matching `PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests`' own precedent, so a comment merely mentioning the literal text can't be misread as code.

**Acceptance Criteria:**

- Given a `DateTimeOffset` with a non-zero offset written through any repository, when it is saved on **Postgres**, then it persists successfully and reads back as the same instant with offset `+00:00` — no `ArgumentException`, no 500.
- Given the same value written on **SQL Server**, then it persists as the same instant with offset `+00:00`, so both providers store an identical value for identical input (AD-2).
- Given an offset-0 value (every current browser-originated write), when saved, then the stored value is byte-identical to today's behavior.
- Given an Eve Home import, when parsed and saved, then every `IntervalStart`/`IntervalEnd` wall-clock value is unchanged from current behavior (AD-9).
- Given the model after the change, when `dotnet ef migrations has-pending-model-changes` runs against both provider projects, then no schema change is detected.
- **(loop 1, generalized in loop 2)** Given a `POST`/`PUT` request to `/api/events`, `/api/meter-readings`, or `/api/tariffs` with a non-zero wire offset on its client-supplied `DateTimeOffset` field, when the 200 response is returned, then the response's timestamp field already shows offset `+00:00` — not the submitted offset. Verified at both the repository level (the *same instance* `AddAsync`/`UpdateAsync` returns, no fresh `DbContext`/re-fetch) and, per loop 2, at the actual HTTP/JSON layer.

## Verification

**Commands:**

- `dotnet build` — expected: clean, no new warnings.
- `dotnet test tests/EnergyTracker.Infrastructure.Tests` — expected: all pass, including the new dual-provider offset round-trip on both engines (both entities) and the new Docker-free converter unit test. Requires Docker (Testcontainers starts real Postgres and SQL Server).
- `dotnet test tests/EnergyTracker.Architecture.Tests` — expected: all pass, including the hardened AD-9 guard.
- `dotnet test tests/EnergyTracker.Application.Tests` and `dotnet test tests/EnergyTracker.Api.Tests` — expected: all pass; run each project separately, the MTP runner rejects multiple project paths in one invocation.
- `dotnet ef migrations has-pending-model-changes --project src/EnergyTracker.Infrastructure.Migrations.Postgres` and the `.SqlServer` equivalent — expected: no pending model changes.

**Manual probe (the original repro):** write an `Event` with `OccurredAt = new DateTimeOffset(2026, 8, 1, 9, 15, 0, TimeSpan.FromHours(2))` through `EventRepository` against a real Postgres container. Before this change that throws `DbUpdateException` → `ArgumentException: … only offset 0 (UTC) is supported.` After, it must persist and read back as `2026-08-01T07:15:00+00:00`, **and the object `AddAsync` itself returns must already show that value** (Task 3a).

## Spec Change Log

- **2026-09-18, review loop 1 (triggered by Blind Hunter finding, bad_spec):** The frozen I/O matrix row "Value round-trips through the API response ... Response echoes the normalized UTC value, not the submitted offset" was not achievable by the original Tasks list. An EF Core `ValueConverter` only transforms values at the CLR↔provider boundary on an actual DB write/read — it never rewrites an already-tracked entity's CLR property after `SaveChangesAsync` returns, so `EventRepository.AddAsync`/`MeterReadingRepository.AddAsync` handed back the exact instance the caller passed in, still holding the client's original offset, which the API endpoints then echoed verbatim. **Amended:** added Task 3a (reload the entity after save in both repositories' create paths) and a matching new Acceptance Criterion; extended the Task 3 test to assert this on the *same returned instance*, not just via a fresh `DbContext`. **Known-bad state avoided:** shipping a fix that satisfies the DB-level round-trip tests while the two actual create endpoints keep echoing the pre-normalization offset back to callers, contradicting the spec's own stated behavior. **KEEP (survives re-derivation as-is):** `UtcDateTimeOffsetConverter`'s shape and comment content (write side `ToUniversalTime()`, read side identity, AD-9 coupling note); `EnergyTrackerDbContext.ConfigureConventions` wiring for both `Properties<DateTimeOffset>()` and `Properties<DateTimeOffset?>()`; the `Converters/` folder location; the dual-provider Testcontainer test class shape (abstract base + `Postgres*`/`SqlServer*` subclasses, `FixedHouseholdAccessor`, fresh-`DbContext`-for-DB-truth pattern) — only its coverage scope and one extra same-instance assertion are added, not its structure; the AD-9 source-scan guard-test approach (per-file text scan, matching `PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests` precedent) — only its rigor is hardened (Task 4 detail), not the approach itself; `dotnet ef migrations has-pending-model-changes` returning clean on both providers (re-verify after Task 3a, expected unaffected since Task 3a only adds a reload, no model/schema change).
- **2026-09-18, review loop 1 (triggered by Blind Hunter finding, patch, bundled into this same amendment):** `deferred-work.md`'s resolved entry was deleted outright instead of annotated, losing the audit breadcrumb of why/when it closed. **Amended:** Task 6 now says annotate in place, not delete.
- **2026-09-18, review loop 1 (triggered by Edge Case Hunter findings, patch, bundled into this same amendment):** The AD-9 guard test's regex could be bypassed by a second, non-zero-offset `new DateTimeOffset(...)` call added elsewhere in the same file, and its single-level-nested-parens allowance would false-fail a compliant deeper-nested refactor. **Amended:** Task 4 detail now requires enumerating every `new DateTimeOffset(` call in the file (not just matching one) via a balanced-paren scan rather than a bounded-depth regex.
- **2026-09-18, review loop 2 (triggered by Blind Hunter finding, patch — applied directly, no further loopback):** `TariffRepository.AddAsync`/`UpdateAsync` had the identical stale-echoed-offset gap Task 3a fixed for `EventRepository`/`MeterReadingRepository` — `Tariff.ContractStartDate` is a client-supplied `DateTimeOffset` echoed straight from the tracked instance by `TariffEndpoints.ToResponse`, on both `POST /api/tariffs` and `PUT /api/tariffs/{id}`. Loop 1's audit named only the two repositories the Problem section calls out "most notably," not every repository sharing the same shape. **Amended:** applied the same `Entry(...).ReloadAsync(...)` fix to `TariffRepository`; extended the Code Map with an explicit audit statement enumerating every client-supplied-`DateTimeOffset`-echoed-in-response case in the codebase (exactly three: `Event.OccurredAt`, `MeterReading.ReadingTimestamp`, `Tariff.ContractStartDate` — every other `DateTimeOffset` response field is server-computed via `UtcNow`, already offset zero) so this class of gap cannot recur silently. Classified as a **patch** rather than triggering a third bad_spec loopback: the fix mechanism itself (reload-after-save) was already established and validated in loop 1 — applying it to a third, structurally identical repository required no new design decision.
- **2026-09-18, review loop 2 (triggered by Blind Hunter + Edge Case Hunter findings, patch, bundled into the same round):** (1) No test exercised `MeterReadingRepository.AddAsync`'s conflict/"winner" path for offset normalization — added. (2) `UtcDateTimeOffsetConverterUnitTests` used `.Compile().DynamicInvoke(...)!`, an unnecessary null-forgiving risk when the delegate is already correctly typed — fixed to call it directly. (3) AC #6 named only two endpoints and every test asserted only repository-level return values, never the actual HTTP JSON a client sees — added one end-to-end `Api.Tests` test per affected endpoint (`Event`, `MeterReading`, `Tariff`) and generalized AC #6's wording accordingly. (4) The AD-9 guard's `Contains("TimeSpan.Zero")` substring check could be fooled by a *nested* `new DateTimeOffset(x, TimeSpan.Zero)` call inside an outer non-zero-offset call, and its scan resumed past nested calls entirely — fixed to split and check top-level arguments only, and to resume scanning from just after each call's own opening paren. (5) The guard also scanned raw text with no comment-stripping, unlike its own claimed precedent — fixed to strip whole-line `//` comments first, matching `PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests` exactly. **Rejected as not requiring action:** ReloadAsync-throws-after-commit and concurrent-row-deleted-between-save-and-reload (Edge Case Hunter) — logged to `deferred-work.md` instead, since no delete path exists for any of the three affected entities today and the throw scenario is an inherent, ubiquitous two-round-trip risk this codebase already accepts elsewhere, not a materially new one; a hypothetical `ReloadAsync` throwing `DbUpdateException` and being misidentified as `MeterReadingRepository`'s idempotency race (Blind Hunter) — `ReloadAsync` issues a read query, not a `SaveChanges`-family write, so it cannot raise `DbUpdateException`, which that type is reserved for; the reload round-trip's performance cost vs. an in-memory `ToUniversalTime()` reassignment (Blind Hunter) — kept `Reload` for consistency with the established, property-count-agnostic mechanism rather than reintroducing a per-call-site transform, which is exactly the pattern this spec's write-side fix was designed to avoid; whether `ReloadAsync` respects AD-3's `HasQueryFilter` (Blind Hunter) — addressed via an explanatory code comment rather than new test infrastructure, since the written-then-reloaded row's `HouseholdId` is structurally guaranteed to equal the caller's own current household for every affected write path.

## Suggested Review Order

**Core mechanism**

- The one-line converter that closes the Npgsql divergence for every write path.
  [`UtcDateTimeOffsetConverter.cs:20`](../../src/EnergyTracker.Infrastructure/Converters/UtcDateTimeOffsetConverter.cs#L20)

- Wires the converter globally for both nullable and non-nullable `DateTimeOffset`.
  [`EnergyTrackerDbContext.cs:57`](../../src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs#L57)

**Returning the normalized value to the caller (Task 3a — the loop-1/2 fix)**

- Reload after save so the returned instance (and its API response) shows the persisted UTC offset, not the client's original one.
  [`EventRepository.cs:20`](../../src/EnergyTracker.Infrastructure/Adapters/EventRepository.cs#L20)

- Same fix on the fast path only; the existing conflict/"winner" path already re-queries fresh.
  [`MeterReadingRepository.cs:70`](../../src/EnergyTracker.Infrastructure/Adapters/MeterReadingRepository.cs#L70)

- Same fix applied to a third repository loop 1's audit missed — `Tariff.ContractStartDate` has the identical echoed-offset shape.
  [`TariffRepository.cs:21`](../../src/EnergyTracker.Infrastructure/Adapters/TariffRepository.cs#L21)

- Update path needs the identical reload after its own `SaveChangesAsync`.
  [`TariffRepository.cs:127`](../../src/EnergyTracker.Infrastructure/Adapters/TariffRepository.cs#L127)

**AD-9 guard (EveHomeXlsxParser must stay zero-offset)**

- Enumerates every `new DateTimeOffset(` call in the file rather than matching just one.
  [`EveHomeXlsxParserUsesZeroOffsetForUtcNormalizationConventionTests.cs:31`](../../tests/EnergyTracker.Architecture.Tests/EveHomeXlsxParserUsesZeroOffsetForUtcNormalizationConventionTests.cs#L31)

- Splits top-level arguments so a nested call's own `TimeSpan.Zero` can't be mistaken for the outer call's.
  [`EveHomeXlsxParserUsesZeroOffsetForUtcNormalizationConventionTests.cs:115`](../../tests/EnergyTracker.Architecture.Tests/EveHomeXlsxParserUsesZeroOffsetForUtcNormalizationConventionTests.cs#L115)

- Strips whole-line comments before scanning, matching this repo's own established guard-test precedent.
  [`EveHomeXlsxParserUsesZeroOffsetForUtcNormalizationConventionTests.cs:146`](../../tests/EnergyTracker.Architecture.Tests/EveHomeXlsxParserUsesZeroOffsetForUtcNormalizationConventionTests.cs#L146)

**Peripherals — tests and backlog bookkeeping**

- Dual-provider regression test for the original bug, asserting the *same returned instance* is already normalized.
  [`UtcDateTimeOffsetConverterTests.cs:56`](../../tests/EnergyTracker.Infrastructure.Tests/UtcDateTimeOffsetConverterTests.cs#L56)

- Same assertion for `MeterReading.ReadingTimestamp`.
  [`UtcDateTimeOffsetConverterTests.cs:78`](../../tests/EnergyTracker.Infrastructure.Tests/UtcDateTimeOffsetConverterTests.cs#L78)

- Forces the idempotency-key race deliberately to prove the conflict "winner" path is normalized too.
  [`UtcDateTimeOffsetConverterTests.cs:107`](../../tests/EnergyTracker.Infrastructure.Tests/UtcDateTimeOffsetConverterTests.cs#L107)

- Same same-instance assertion extended to `Tariff.ContractStartDate`.
  [`UtcDateTimeOffsetConverterTests.cs:149`](../../tests/EnergyTracker.Infrastructure.Tests/UtcDateTimeOffsetConverterTests.cs#L149)

- Fast, Docker-free unit test directly on the converter's own conversion delegates.
  [`UtcDateTimeOffsetConverterUnitTests.cs:10`](../../tests/EnergyTracker.Infrastructure.Tests/UtcDateTimeOffsetConverterUnitTests.cs#L10)

- End-to-end HTTP-level proof that the actual JSON response echoes a normalized offset, not just the repository return value.
  [`EventEndpointsTests.cs:42`](../../tests/EnergyTracker.Api.Tests/EventEndpointsTests.cs#L42)

- Same end-to-end proof for the Meter Reading create endpoint.
  [`MeterReadingEndpointsTests.cs:59`](../../tests/EnergyTracker.Api.Tests/MeterReadingEndpointsTests.cs#L59)

- Same end-to-end proof for the Tariff create endpoint.
  [`TariffEndpointsTests.cs:95`](../../tests/EnergyTracker.Api.Tests/TariffEndpointsTests.cs#L95)

- Backlog bookkeeping: annotates the original bug entry as resolved and logs two new, explicitly out-of-scope edge cases surfaced during review.
  [`deferred-work.md`](../../_bmad-artifacts/implementation/deferred-work.md)
