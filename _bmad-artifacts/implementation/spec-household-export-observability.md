---
title: 'Add Observability to the Household Export Success Path'
type: 'enhancement'
created: '2026-09-26'
status: 'done'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-artifacts/implementation/epic-7-retro-2026-09-26.md'
  - '{project-root}/_bmad-artifacts/implementation/spec-household-export-oom-fix.md'
  - '{project-root}/_bmad-artifacts/implementation/deferred-work.md'
baseline_commit: '5b56e80'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Epic 7 retro action item #1 (owners Winston/Amelia): the household-export success path has zero telemetry. `spec-household-export-oom-fix.md`'s own deferred-work entry named this gap explicitly — the 2026-09-25 production `OutOfMemoryException` on `GET /api/household-export` went undetected until it actually crashed; nothing today would show a future household trending toward the same danger zone before it repeats. Only the fault path (`HouseholdExportEndpoints`'s `catch` block) logs anything.

**Approach:** Add one structured success-path log line per completed export: total duration, and per-collection row counts + page round-trip counts for every keyset-paginated collection. Capture page round trips at their only source of truth — `HouseholdExportReader.PageAsync`'s loop, in Infrastructure — via a new plain accumulator type (`HouseholdExportStats`) owned by Application and threaded through the existing `HouseholdExportData`/`HouseholdExportStream` records already flowing from Infrastructure through Application to the Api endpoint. No new telemetry primitive (no OTel `Meter`/`ActivitySource`) — AD-19 already commits to structured Serilog-to-stdout only; this reuses that path exactly like the existing fault-path `logger.LogError` call it sits beside.

## Boundaries & Constraints

**Always:**
- AD-1 (ports & adapters direction): the new `HouseholdExportStats` type lives in `EnergyTracker.Application` (next to `HouseholdExportData`); `HouseholdExportReader` (Infrastructure) populates it because Infrastructure already depends on Application, never the reverse.
- AD-19 (operational baseline): structured Serilog to stdout only — no new OTel metric/trace primitive, no environment branching in the new logging code.
- `HouseholdExportStats` is never serialized onto the export wire format — `HouseholdExportStream`'s JSON shape (manually walked property-by-property in `WriteExportAsync`) stays byte-for-byte identical to today.
- Page round-trip counts are captured exactly once, inside `HouseholdExportReader.PageAsync`'s existing loop — no second counting mechanism anywhere else (row counts are a free byproduct: `sum(page.Count)` across recorded pages).
- The success log line only fires after `WriteExportAsync` completes without throwing — never on the abort-on-fault path, which keeps its own existing `logger.LogError`/`LogInformation` calls unchanged.

**Ask First:**
- If threading `HouseholdExportStats` through `HouseholdExportData`/`HouseholdExportStream` turns out to require touching any consumer beyond `HouseholdExportReader`, `ExportHouseholdData`, and `HouseholdExportEndpoints`, HALT before extending scope.

**Never:**
- No import/restore-side changes (`HouseholdExportResult`, `HouseholdImportEndpoints`, `RestoreHouseholdData`) — out of scope, untouched.
- No new alerting/dashboard/metric-export tooling this iteration — a structured log line only, consistent with the deferred-work entry's own framing ("worth a follow-up once there's an OTel metric/log convention... to fit into" — that convention doesn't exist yet, so this stays log-only, matching today's stack).
- No behavior change to the exported JSON document itself.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Single-page collection | Collection has fewer rows than `PageSize` (500) | Stats record exactly 1 page round trip, row count = actual row count | N/A |
| Multi-page collection | Collection spans >1 page (e.g. 640 rows / `PageSize` 500) | Stats record 2 page round trips, row count = 640 | N/A |
| Empty collection (household exists, no rows) | Query runs, returns 0 rows | Stats record 1 page round trip (the query still executed), row count 0 | N/A |
| Successful export | Full request completes and streams to the client | One `LogInformation` line: household id, duration, per-collection `{RowCount, PageCount}` map | N/A |
| Mid-export fault | DB fault or client disconnect during streaming | No success log line emitted; existing fault-path `LogError`/`LogInformation` behavior unchanged | Unchanged from today |
| Household with no MainMeter | `MeterReadings` never queried (existing short-circuit) | Stats record 0 rows / 0 pages for `meterReadings` — present in the map (pre-seeded at construction) despite zero queries ever running | N/A |

</frozen-after-approval>

## Code Map

- `src/EnergyTracker.Application/Ports/IHouseholdExportReader.cs` -- new `HouseholdExportStats` accumulator type; `HouseholdExportData` gains a `Stats` field.
- `src/EnergyTracker.Infrastructure/Adapters/HouseholdExportReader.cs` -- `PageAsync` takes a collection name + the shared `HouseholdExportStats`, records one page per round trip.
- `src/EnergyTracker.Application/ExportHouseholdData.cs` -- `HouseholdExportStream` gains a `Stats` field, forwarded from `data.Stats`.
- `src/EnergyTracker.Api/Endpoints/HouseholdExportEndpoints.cs` -- `Stopwatch` around `ExecuteAsync` + `WriteExportAsync`; one `LogInformation` success line after a clean write.
- `tests/EnergyTracker.Infrastructure.Tests/HouseholdExportReaderTests.cs` -- extend the existing multi-page test to assert stats; add single-page/empty-collection stats assertions.
- `tests/EnergyTracker.Application.Tests/ExportHouseholdDataTests.cs` -- adapt existing `HouseholdExportData`/`HouseholdExportStream` construction call sites to the new field; assert `Stats` forwards through unchanged.
- `tests/EnergyTracker.Api.Tests/HouseholdExportEndpointsWriteTests.cs` -- adapt `HouseholdExportStream` construction call site to the new field.
- `_bmad-artifacts/implementation/deferred-work.md` -- remove the now-resolved "no observability on the export success path" entry.

## Tasks & Acceptance

**Execution:**
- [x] `IHouseholdExportReader.cs` -- add `HouseholdExportStats` + `HouseholdExportData.Stats` -- gives Infrastructure a place to record page/row counts without a port-signature break
- [x] `HouseholdExportReader.cs` -- `PageAsync` records one page per round trip, keyed by collection name -- single source of truth for both row and page counts
- [x] `ExportHouseholdData.cs` -- forward `Stats` onto `HouseholdExportStream` -- carries the accumulator to the one place that can log a request-scoped summary
- [x] `HouseholdExportEndpoints.cs` -- time the request, log one success line with duration + stats -- closes the actual observability gap
- [x] Update existing test call sites (`HouseholdExportReaderTests`, `ExportHouseholdDataTests`, `HouseholdExportEndpointsWriteTests`, `HouseholdExportEndpointsTests`) for the new field/signature -- keeps existing coverage green
- [x] Add stats-specific test coverage (single-page, multi-page, empty-collection row/page counts) -- proves the accumulator is correct, not just present
- [x] Remove the resolved deferred-work.md entry -- closes the loop Epic 7's retro opened

**Acceptance Criteria:**
- Given a household whose collections span multiple pages, when the export completes, then `HouseholdExportStats.Snapshot()` reports the correct row count and page-round-trip count per collection.
- Given a household whose export never runs a query for one collection (no MainMeter, so `MeterReadings` is skipped entirely), when the export completes, then that collection's stats entry is still present in the map, pre-seeded at `RowCount: 0, PageCount: 0`.
- Given a successful export, when `GET /api/household-export` completes, then exactly one `LogInformation` line is emitted carrying household id, duration, and the per-collection stats map.
- Given a mid-export fault, when the response aborts, then no success log line is emitted and the existing fault-path logging is unchanged.
- Given the exported JSON document, when compared before/after this change, then it is byte-for-byte identical (stats never serialize onto the wire).

## Spec Change Log

- 2026-09-26: Implemented as designed (Winston design consult, Amelia implementation), no scope deviations. Full backend suite (856/856) green after the change, including the mid-export-fault test confirming no success log line fires on the abort path. Verification of the "byte-for-byte identical wire format" acceptance criterion was structural (Stats is never touched by `WriteExportAsync`'s manual property walk) plus the pre-existing `HouseholdExportEndpointsTests` JSON-shape assertions passing unchanged, not a dedicated before/after diff test.

## Design Notes

- **Why the accumulator lives on `HouseholdExportData`/`HouseholdExportStream` rather than a new port method or `IProgress<T>` callback:** those records already flow end-to-end through every layer that needs to see the result (Infrastructure produces, Application forwards, Api consumes) for exactly this request's lifetime — reusing that carrier is zero new coupling. A callback-based design would need its own lifetime/threading contract for no added benefit given the port's existing single-threaded, sequential-enumeration guarantee.
- **Why row counts don't need separate instrumentation in `HouseholdExportEndpoints.WriteArrayAsync`:** `PageAsync` already sees every row that will ever reach the wire (DTO mapping in `ExportHouseholdData.MapAsync` is a 1:1, non-filtering projection), so `sum(page.Count)` recorded in the reader equals the endpoint's own eventual write count. One counting site, not two.
- **Why `HouseholdExportStats` is constructed pre-seeded with all 11 collection names (not lazily populated on first use):** every collection name appears in the log's final map even when its own query never ran (e.g. `meterReadings` when the Household has no MainMeter yet), so the success log line always has a stable, complete shape to grep/dashboard against — never a map that silently omits a key depending on that request's data shape. `RecordPage` indexes into the pre-seeded dictionary (throws on an unknown name), which also guards against a typo'd collection-name string ever going unnoticed.
- **Why not an OTel `Meter`:** no `Meter`/`ActivitySource` exists anywhere in this codebase yet (`Program.cs` only registers framework instrumentation — ASP.NET Core, EF Core, HttpClient, runtime). Introducing one for a single endpoint would be a second, competing observability path against AD-19's already-decided structured-logging convention.
