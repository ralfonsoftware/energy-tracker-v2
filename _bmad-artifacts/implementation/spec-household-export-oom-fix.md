---
title: 'Stream & Page Household Export to Fix Production OOM'
type: 'bugfix'
created: '2026-09-25'
status: 'done'
review_loop_iteration: 0
context: ['{project-root}/_bmad-artifacts/tests/test-design/test-design-household-export-oom-fix.md']
baseline_commit: '93e71babdf6e2d3985ff45678ee58de39262fd17'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `GET /api/household-export` throws `System.OutOfMemoryException` in production (confirmed live, 2026-09-25 15:15/15:21, `energytracker-prod-app`): `HouseholdExportEndpoints.cs:45` serializes the entire export as one in-memory byte array, fed by `HouseholdExportReader.GetExportDataAsync` loading every entity collection unbounded via `.ToListAsync()`. The 0.5 vCPU/1Gi container can't hold a large household's SmartPlugReadings-driven payload at once.

**Approach:** Stream the JSON response incrementally, backed by keyset-paginated per-collection reads, with abort-on-fault semantics so a mid-export DB failure resets the connection instead of completing a truncated-but-clean-looking response. Add the one genuinely missing index; reuse existing indexes elsewhere.

## Boundaries & Constraints

**Always:**
- AD-2 (dual-provider): one shared `EnergyTrackerDbContext`, portable relational subset only, migrations via `scripts/add-migration.sh` landing in both provider projects in one commit.
- AD-3 (tenant isolation): the global `HouseholdId` query filter keeps applying automatically — never `FromSqlRaw`/`IgnoreQueryFilters`/`Find` in the new paged queries.
- AD-10: archived (soft-deleted) `Room`/`PowerPoint`/`Device` rows stay included in the export.
- Keyset (cursor) pagination only, ordered by cursor column + `Id` tiebreaker per entity.
- Peak working-set for one export request stays within ~300–400Mi headroom under the container's 1Gi limit.
- On a mid-export fault (DB read throws, cancellation), abort the HTTP response at the transport level — never let a partial export complete looking identical to a full one.
- `MeterReadings` pages/orders via `MainMeterId`, not `HouseholdId` (see Design Notes). `SmartPlugReadings` needs a new composite `(HouseholdId, IntervalStart, Id)` index. `Events`' existing `(HouseholdId, OccurredAt, CreatedAtUtc)` index needs no change.

**Ask First:**
- If the chosen page/batch size can't keep peak working-set under the ~300–400Mi target once the reproducer test runs, HALT before loosening the target or further changing pagination granularity.
- If `IHouseholdExportReader`'s contract change (list-returning → streaming) turns out to affect a consumer beyond `ExportHouseholdData`, HALT and confirm scope before extending the rewrite.

**Never:**
- No trailing checksum/record-count integrity trailer this iteration (see Design Notes) — abort-on-fault only.
- No offset-based pagination.
- No changes to `HouseholdImportEndpoints`/the restore side — out of scope, flagged as a follow-on.
- No k6/load-testing tooling — single-request memory risk, validated via .NET/Testcontainers.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Large household | SmartPlugReadings volume calibrated to the incident | Streamed 200 completes; peak working-set within ~300–400Mi | N/A |
| Archived rows | Soft-deleted Room/PowerPoint/Device rows present | Included, same as today's unbounded read | N/A |
| Mid-export DB fault | DB read fails/cancels partway through paging | Response aborts at transport level, never a clean 200 | Connection reset, logged |
| Two households | Household A and B both have data | A's export never contains B's rows at any page | N/A |
| Page-boundary duplicates | Two SmartPlugReadings rows share `IntervalStart` | `Id` tiebreaker keeps both, stable order, no skip/dupe | N/A |

</frozen-after-approval>

## Code Map

- `src/EnergyTracker.Application/Ports/IHouseholdExportReader.cs` -- collection fields become `IAsyncEnumerable<T>`; `Household`/`MainMeter` stay single-entity.
- `src/EnergyTracker.Infrastructure/Adapters/HouseholdExportReader.cs` -- each `.ToListAsync()` becomes a keyset-paginated `AsAsyncEnumerable()`; `MeterReadings` orders/pages via `MainMeterId`, others via `HouseholdId` + cursor column.
- `src/EnergyTracker.Infrastructure/Configurations/SmartPlugReadingConfiguration.cs` + migration via `scripts/add-migration.sh` -- composite `(HouseholdId, IntervalStart, Id)` index, both providers.
- `src/EnergyTracker.Application/ExportHouseholdData.cs` -- stream DTO mapping per-item instead of `.Select(ToDto).ToList()`.
- `src/EnergyTracker.Api/Endpoints/HouseholdExportEndpoints.cs` -- replace `SerializeToUtf8Bytes` + `Results.File` with `Utf8JsonWriter` writing directly to the response/`PipeWriter`; manual `Content-Disposition`; abort response on mid-export fault.
- `tests/EnergyTracker.Architecture.Tests/` -- new guard test: reader never uses `FromSqlRaw`/`IgnoreQueryFilters` (no existing guard today).
- `tests/EnergyTracker.Infrastructure.Tests/HouseholdExportReaderTests.cs` -- adapt existing 2 `[Fact]`s to the streamed/paged shape.
- `tests/EnergyTracker.Application.Tests/ExportHouseholdDataTests.cs` -- adapt existing 12 NSubstitute `[Fact]`s to the async-enumerable shape; call-shape assertions only, never mocked return contents for paging.
- `tests/EnergyTracker.Api.Tests/HouseholdExportEndpointsTests.cs` -- adapt existing 4 `[Fact]`s; add the incident-volume reproducer and mid-export-fault-abort tests.

## Tasks & Acceptance

**Execution:**
- [x] `IHouseholdExportReader.cs` -- streaming/async-enumerable port shape -- unblocks downstream rewrite
- [x] `HouseholdExportReader.cs` -- keyset-paginated reads per collection -- fixes the unbounded-read root cause
- [x] `SmartPlugReadingConfiguration.cs` + migration -- `(HouseholdId, IntervalStart, Id)` index, both providers -- supports pagination without a scan
- [x] `ExportHouseholdData.cs` -- stream DTO mapping per-item -- removes the Application-layer materialization step (see Spec Change Log: split into `HouseholdExportStream` for the write path, `HouseholdExportResult` untouched for import)
- [x] `HouseholdExportEndpoints.cs` -- stream via `Utf8JsonWriter`; abort on mid-export fault -- fixes the actual OOM call site
- [x] `tests/EnergyTracker.Architecture.Tests/` -- `FromSqlRaw`/`IgnoreQueryFilters` guard test -- closes the tenant-isolation regression gap
- [x] Adapt the 3 existing export test files to the new shapes -- keeps existing coverage intact
- [x] Add incident-volume reproducer + mid-export-fault-abort tests -- proves the fix holds under memory pressure and abort semantics work

**Acceptance Criteria:**
- Given a household seeded with SmartPlugReadings volume calibrated to the 2026-09-25 production incident, when `GET /api/household-export` runs, then it completes successfully with peak working-set within the ~300–400Mi target, not an `OutOfMemoryException`.
- Given the paged rewrite, when compared against today's unbounded read for the same seeded household, then output is set-equal across every collection, including archived rows.
- Given a fault injected mid-export, when the response is inspected, then it is never a well-formed, complete-looking 200 — the transfer is aborted/incomplete.
- Given the new migration, when `PostgresMigrationTests`/`SqlServerMigrationTests` run, then both pass.

## Spec Change Log

- 2026-09-25: Discovered during implementation that `HouseholdExportResult` (the record `ExportHouseholdData.ExecuteAsync` returned) is also the deserialization target on the import/restore side (`ValidateHouseholdImport.cs`, `RestoreHouseholdData.cs`, `HouseholdImportEndpoints.BuildSummary`). Changing its collections to `IAsyncEnumerable<T>` broke those consumers, and `System.Text.Json` cannot deserialize a JSON array into `IAsyncEnumerable<T>` in any case — this is exactly the "Ask First" trigger ("if the port's contract change affects a consumer beyond ExportHouseholdData"). Resolved within the stated boundary (no changes to the restore side) by leaving `HouseholdExportResult` untouched (`IReadOnlyList<T>`, still the import wire contract) and adding a new `HouseholdExportStream` record — identical field names/JSON shape, `IAsyncEnumerable<T>` collections — that `ExportHouseholdData.ExecuteAsync` now returns and only `HouseholdExportEndpoints` consumes. No import/restore file was modified.
- 2026-09-25 (adversarial + edge-case review, patch round): both reviewers independently confirmed the same **CONFIRMED bug**: `HouseholdExportEndpoints.WriteArrayAsync` flushed the `Utf8JsonWriter`/`PipeWriter` only once per *entire* top-level collection, not per DB page — for one very large collection (the exact incident shape), this silently re-buffered the whole array's serialized JSON in the PipeWriter before ever reaching the wire, reintroducing the OOM shape this fix exists to close (moved from "buffered domain objects" to "buffered JSON bytes"). **KEEP:** the abort-on-fault semantics, the per-collection catch/log structure, and the manual `Utf8JsonWriter`-over-`PipeWriter` approach all worked and are preserved. **Amended (patch, no spec change needed — mechanical fix within already-approved design):** `WriteArrayAsync` now flushes every `FlushEveryNItems` (500, matching `HouseholdExportReader.PageSize`) rows in addition to the end-of-array flush, so peak buffered-but-unflushed bytes are bounded by one page's worth of JSON regardless of collection size. Also patched in this round: benign client-disconnect exceptions no longer log at Error level (only genuine mid-export faults do); the AD-3 guard test's comment-stripping no longer false-positives on inline trailing comments; `IHouseholdExportReader`'s single-enumeration/sequential-only contract is now documented on the port; a new `HouseholdExportEndpointsWriteTests.WriteExportAsync_applies_backpressure_instead_of_buffering_a_whole_large_collection_before_flushing` regression test proves the flush-cadence fix via real `System.IO.Pipelines.Pipe` backpressure (closing the AC #1 verification gap's most failure-prone mechanism, independent of the `~300-400Mi` prose ceiling still not being measured empirically — see Verification); and a new `Events_page_correctly_across_multiple_pages_including_a_full_cursor_tuple_tie` test covers Events' three-column cursor (`OccurredAt`, `CreatedAtUtc`, `Id`), the only paginated entity that had no multi-page/tie-break coverage. **Known-bad state avoided:** merging with the once-per-collection flush cadence would have shipped a fix that passes all tests (including the 5,000-row reproducer, too small to expose the gap) while still OOMing in production at real incident volume for a household with one dominant large collection.

## Design Notes

- **`MeterReadings` via `MainMeterId`:** each Household has exactly one MainMeter (1:1), and `(MainMeterId, ReadingTimestamp)` already exists (added for the status-recompute fix) — reuses it, no new migration. AD-3's global query filter still applies regardless of the ordering column.
- **Abort-on-fault only, no integrity trailer:** sufficient for the score-9 truncation risk this session's test design flagged — a server-side fault aborts the transport connection rather than completing a truncated-but-clean file. Doesn't cover post-success network truncation; accepted trade-off.
- **Keyset over offset pagination:** offset re-scans/re-sorts skipped rows every page, degrading non-linearly at this data volume even with the new index; keyset seeks directly from the last cursor value.
- **No cross-page snapshot isolation, accepted trade-off (human-confirmed 2026-09-25):** each collection's keyset pages are independent, non-transactional reads (vs. the old code's one query per collection), so a concurrent write to that household mid-export (e.g. an in-flight Smart Plug import) could leave one collection's own page boundary internally inconsistent. Raised by adversarial review; explicitly accepted rather than fixed this iteration — this is a manual, on-demand export (not a frequently-hit path), and wrapping the read in a snapshot-isolated transaction would hold a longer-lived read against the exact write-heavy table (SmartPlugReadings) this fix exists to relieve pressure on, risking trading the OOM for a lock/transaction-duration problem. See deferred-work.md.

## Verification

**Commands run (2026-09-25, initial implementation):**
- `dotnet build EnergyTracker.sln -c Debug` -- clean, 0 warnings/errors
- `dotnet build EnergyTracker.sln -c Release` -- clean, 0 warnings/errors
- `scripts/add-migration.sh AddSmartPlugReadingHouseholdIntervalStartIdIndex` -- succeeded on both providers; migration applied and verified via `PostgresMigrationTests`/`SqlServerMigrationTests` (8/8 pass)
- `dotnet test EnergyTracker.sln -c Debug` -- **848/848 pass**, 0 failed, 0 skipped (includes the new Architecture.Tests guard, the 3 adapted export test files, and the incident-volume reproducer + mid-export-fault-abort tests)

**Commands run (2026-09-25, adversarial + edge-case review patch round):**
- Two independent review agents (Blind Hunter / `bmad-review-adversarial-general`, Edge Case Hunter / `bmad-review-edge-case-hunter`) both ran against the full diff and independently converged on the same confirmed flush-cadence bug (see Spec Change Log). Two BH findings were investigated and found to be false alarms: "8 of 11 collections lack a supporting index" (all eight already carry a pre-existing `HouseholdId` index from prior stories — verified directly against each `*Configuration.cs`) and (ECH) "`GetMeterReadingsAsync` filtering by `MainMeterId` could silently drop readings tied to a replaced MainMeter" (no MainMeter delete/replace capability exists anywhere in this codebase — verified by search; Design Notes' "exactly one MainMeter (1:1)" invariant holds).
- `dotnet build EnergyTracker.sln -c Debug` / `-c Release` -- clean, 0 warnings/errors, after the patch round
- `dotnet test EnergyTracker.sln` -- **851/851 pass**, 0 failed, 0 skipped (848 + 3 new: the flush-cadence backpressure regression test, and the Events multi-page/tie-break test on both providers)
- One genuine deadlock was found and fixed *in the new backpressure test itself* (not production code) during this round — a TOCTOU race in a `while (!writeTask.IsCompleted)` drain-loop condition; caught because the full-suite run hung past its timeout and the human operator flagged it live rather than the test being trusted on a partial/first run.

**Known verification gap (honest limitation, not silently glossed over):**
- AC #1's "~300–400Mi peak working-set" ceiling is addressed by design (keyset pagination at a 500-row page size + a JSON writer that now flushes every 500 rows too, so peak memory for any one collection is bounded regardless of household size — the flush-cadence bug that would have defeated this was caught and fixed in review, see Spec Change Log) and is now backed by a deterministic regression test (`WriteExportAsync_applies_backpressure_instead_of_buffering_a_whole_large_collection_before_flushing`) proving the write side genuinely blocks on `Pipe` backpressure rather than buffering an unbounded amount before its first flush. It is still **not asserted by an automated peak-RSS/production-scale memory measurement** — `GC.GetAllocatedBytesForCurrentThread()` around an in-process `WebApplicationFactory` call is unreliable (the handler can run on a different pooled thread) and the TestServer transport has no container-style memory ceiling to trip either way. The reproducer test (`GET_household_export_completes_and_returns_every_row_at_incident_calibrated_volume`, 5,000 SmartPlugReadings, >8 keyset pages) instead asserts successful completion + full-row-count correctness at volume. A real peak-RSS measurement would need an out-of-process load test against a memory-capped container, which the test-design's own "Not in Scope" section already excludes (no k6/load-testing tooling this iteration).
- **Cross-page consistency:** raised by adversarial review, not covered by the frozen I/O matrix — resolved 2026-09-25 (human-confirmed) as an accepted trade-off; see the new Design Notes entry above.

## Suggested Review Order

**The confirmed bug and its fix (start here)**

- Only flushed once per whole collection, not per page — for one huge collection this silently re-buffers unbounded JSON bytes in the pipe, reintroducing the exact OOM this fix exists to close. Caught by two independent review agents.
  [`HouseholdExportEndpoints.cs:159`](../../src/EnergyTracker.Api/Endpoints/HouseholdExportEndpoints.cs#L159)

- The fix: flush every `FlushEveryNItems` (500) rows, matching the reader's own page size, so peak buffered bytes stay bounded regardless of collection size.
  [`HouseholdExportEndpoints.cs:111`](../../src/EnergyTracker.Api/Endpoints/HouseholdExportEndpoints.cs#L111)

- Regression test proving the fix via real `Pipe` backpressure rather than inferring it from HTTP-level behavior, which can't reliably tell "flushed periodically" from "buffered then flushed once."
  [`HouseholdExportEndpointsWriteTests.cs:57`](../../tests/EnergyTracker.Api.Tests/HouseholdExportEndpointsWriteTests.cs#L57)

**Streaming write path (the OOM's actual call site)**

- Entry point: manual `Utf8JsonWriter`-over-`PipeWriter` write replacing the old buffered `SerializeToUtf8Bytes` + `Results.File`.
  [`HouseholdExportEndpoints.cs:36`](../../src/EnergyTracker.Api/Endpoints/HouseholdExportEndpoints.cs#L36)

- Abort-on-fault: any mid-export exception resets the connection at the transport level instead of completing a truncated-but-clean 200 (frozen spec boundary — no integrity trailer this iteration).
  [`HouseholdExportEndpoints.cs:89`](../../src/EnergyTracker.Api/Endpoints/HouseholdExportEndpoints.cs#L89)

- Fault-injection test proving the client actually observes a broken transfer, not a clean download.
  [`HouseholdExportEndpointsTests.cs:162`](../../tests/EnergyTracker.Api.Tests/HouseholdExportEndpointsTests.cs#L162)

**Keyset pagination (DB read side, root cause of the OOM)**

- Port contract: every export collection becomes `IAsyncEnumerable<T>`, single-enumeration/sequential-only by design (documented here after review flagged the missing contract note).
  [`IHouseholdExportReader.cs:27`](../../src/EnergyTracker.Application/Ports/IHouseholdExportReader.cs#L27)

- Shared keyset-pagination loop mechanics — entity-specific ordering/filtering stays in each entity's own query, this only owns the cursor/round-trip logic.
  [`HouseholdExportReader.cs:64`](../../src/EnergyTracker.Infrastructure/Adapters/HouseholdExportReader.cs#L64)

- `MeterReadings` deliberately pages via `MainMeterId`, not `HouseholdId`, reusing an existing index instead of adding a new one.
  [`HouseholdExportReader.cs:116`](../../src/EnergyTracker.Infrastructure/Adapters/HouseholdExportReader.cs#L116)

- `SmartPlugReadings` is the one collection needing a new composite index — `Id` is load-bearing here (page-boundary duplicates), not just a formality.
  [`HouseholdExportReader.cs:253`](../../src/EnergyTracker.Infrastructure/Adapters/HouseholdExportReader.cs#L253)

- New composite index backing the above, added via `scripts/add-migration.sh` to both provider projects.
  [`SmartPlugReadingConfiguration.cs:63`](../../src/EnergyTracker.Infrastructure/Configurations/SmartPlugReadingConfiguration.cs#L63)

- `Events`' three-column cursor tuple (`OccurredAt`, `CreatedAtUtc`, `Id`) reuses an existing index unchanged — the most structurally complex cursor here.
  [`HouseholdExportReader.cs:180`](../../src/EnergyTracker.Infrastructure/Adapters/HouseholdExportReader.cs#L180)

**Application-layer streaming + the "Ask First" deviation**

- `ExecuteAsync` now returns a streamed shape instead of a fully materialized result.
  [`ExportHouseholdData.cs:11`](../../src/EnergyTracker.Application/ExportHouseholdData.cs#L11)

- Hand-rolled `IAsyncEnumerable<TSource>` → `IAsyncEnumerable<TDto>` mapper, kept lazy so DTO mapping never buffers a whole collection either.
  [`ExportHouseholdData.cs:37`](../../src/EnergyTracker.Application/ExportHouseholdData.cs#L37)

- The "Ask First" trigger fired here: `HouseholdExportResult` is also the import/restore deserialization target, so it stays untouched; `HouseholdExportStream` is new and export-only. See the Spec Change Log for the full reasoning.
  [`ExportHouseholdData.cs:132`](../../src/EnergyTracker.Application/ExportHouseholdData.cs#L132)

**Tests and guards (peripherals)**

- AD-3 tenant-isolation guard test for the new paged reader — source-text scan matching this codebase's existing guard-test convention.
  [`HouseholdExportReaderDoesNotBypassTenantIsolationTests.cs:18`](../../tests/EnergyTracker.Architecture.Tests/HouseholdExportReaderDoesNotBypassTenantIsolationTests.cs#L18)

- Incident-volume reproducer (5,000 SmartPlugReadings, >8 keyset pages) — the P0 test the test-design flagged as "must not be skipped."
  [`HouseholdExportEndpointsTests.cs:142`](../../tests/EnergyTracker.Api.Tests/HouseholdExportEndpointsTests.cs#L142)

- Multi-page correctness test proving no drops/duplicates across a page boundary for `MeterReadings`.
  [`HouseholdExportReaderTests.cs:161`](../../tests/EnergyTracker.Infrastructure.Tests/HouseholdExportReaderTests.cs#L161)

- `Events`' multi-page + full-cursor-tuple-tie test, added in the review patch round to close a coverage gap the reviewers flagged.
  [`HouseholdExportReaderTests.cs:262`](../../tests/EnergyTracker.Infrastructure.Tests/HouseholdExportReaderTests.cs#L262)
- AC #2's "compared against today's unbounded read" is verified by direct assertion against seeded fixtures (single-row coverage across every entity category, a >1-page multi-page test, and a page-boundary-duplicate test for SmartPlugReadings), not a literal old-code-vs-new-code diff — the pre-fix unbounded implementation no longer exists in the tree to diff against once the rewrite landed.
