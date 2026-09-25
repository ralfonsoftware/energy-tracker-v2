---
workflowStatus: 'completed'
totalSteps: 5
stepsCompleted: ['step-01-detect-mode', 'step-02-load-context', 'step-03-risk-and-testability', 'step-04-coverage-plan', 'step-05-generate-output']
lastStep: 'step-05-generate-output'
nextStep: ''
lastSaved: '2026-09-25'
inputDocuments:
  - _bmad-artifacts/project-context.md
  - _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08
  - _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE
  - _bmad-artifacts/implementation/sprint-status.yaml
  - src/EnergyTracker.Api/Endpoints/HouseholdExportEndpoints.cs
  - src/EnergyTracker.Infrastructure/Adapters/HouseholdExportReader.cs
  - src/EnergyTracker.Application/ExportHouseholdData.cs
  - tests/EnergyTracker.Infrastructure.Tests/HouseholdExportReaderTests.cs
  - tests/EnergyTracker.Application.Tests/ExportHouseholdDataTests.cs
  - tests/EnergyTracker.Api.Tests/HouseholdExportEndpointsTests.cs
  - Azure Container Apps console logs (energytracker-prod-app, resource group energy-tracker-rg) — live OOM incident evidence, 2026-09-25 15:15/15:21
  - .claude/skills/bmad-testarch-test-design/resources/knowledge/risk-governance.md
  - .claude/skills/bmad-testarch-test-design/resources/knowledge/probability-impact.md
  - .claude/skills/bmad-testarch-test-design/resources/knowledge/test-levels-framework.md
  - .claude/skills/bmad-testarch-test-design/resources/knowledge/test-priorities-matrix.md
  - .claude/skills/bmad-testarch-test-design/resources/knowledge/nfr-criteria.md
---

# Test Design Progress — Household Export OOM Fix

## Step 1: Mode Detection

**Mode: Epic-Level Mode** (single scoped test plan). Neither pure System-Level (no new PRD/ADR being authored) nor a formally-created Epic exists yet for this fix — a spec/story doc is a pending hard-gate dependency (see project-context.md "No fix to already-shipped code merges without a linked spec/story doc"). `sprint-status.yaml` exists (file-based signal → Epic-Level). Epic-Level's "single test plan for a scoped feature" is the right-sized container for a targeted endpoint/data-access rewrite.

## Step 2: Context Loaded

- **Detected stack (this scope): backend-only (.NET)**. No Playwright/e2e coverage exists or is warranted for `/api/household-export` (API-only surface, confirmed via `grep` — no `web/e2e` hits). Playwright Utils / Pact.js Utils fragments are not loaded — this project's backend test stack is xUnit v3 (MTP) + Shouldly + NSubstitute + Testcontainers, per `project-context.md`, not a JS/Node API-testing stack.
- **Existing test coverage found** (3 files, 771 lines total): `HouseholdExportReaderTests.cs` (Infrastructure), `ExportHouseholdDataTests.cs` (Application), `HouseholdExportEndpointsTests.cs` (Api) — all currently exercise the unbounded/non-streaming implementation and will need rewriting alongside the fix, not just additive tests.
- **Root cause (confirmed live in prod):** `System.OutOfMemoryException` in `HouseholdExportEndpoints.cs:45` (`JsonSerializer.SerializeToUtf8Bytes`) — the full export is materialized as one in-memory byte array. Upstream, `HouseholdExportReader.GetExportDataAsync` loads every entity collection unbounded via `.ToListAsync()` (MeterReadings, SmartPlugReadings, Events, StatusSnapshots, AuditCorrections, etc). Container: 0.5 vCPU / 1Gi memory, single replica (`energytracker-prod-app`). Two reproduced crashes 2026-09-25 15:15:39 and 15:21:51.
- **Architecture constraints this fix must respect:** AD-2 (dual-provider — Postgres + Azure SQL, shared `EnergyTrackerDbContext`, portable relational subset only, migrations via `scripts/add-migration.sh` to both providers together), AD-3 (tenant isolation — global `HouseholdId` query filter, never `IgnoreQueryFilters`/`FromSqlRaw`/`Find`), AD-10 (archived rows included deliberately — soft-deleted Room/PowerPoint/Device, by-value snapshots on SmartPlugReading/Event — must not be silently dropped by a paged rewrite). This endpoint backs FR-23's full-dataset export/restore path.
- **Knowledge fragments loaded:** `risk-governance.md` (probability × impact scoring, score≥6 mitigate, score=9 block), `probability-impact.md` (1-3 scale definitions, DOCUMENT/MONITOR/MITIGATE/BLOCK actions), `test-levels-framework.md` (unit/integration/e2e decision matrix), `test-priorities-matrix.md` (P0-P3 criteria — "data import/export" is P1 baseline, "previously broken functionality" is P0), `nfr-criteria.md` (performance NFR pattern — explicit measurable thresholds, PASS/CONCERNS/FAIL; k6 pattern noted as reference but not directly applicable — this fix's perf risk is single-request memory exhaustion under data volume, not concurrent load, so equivalent thresholds will be validated via .NET integration tests seeded with large data volumes against Testcontainers, matching this project's existing convention).

## Step 3: Risk Assessment & NFR Planning

**Mode note:** Epic-Level — system-level testability review (Section 1) skipped per workflow rules.

### Risk Register

| ID | Category | Risk | P | I | Score | Action |
|----|----------|------|---|---|-------|--------|
| R1 | DATA/BUS | **Streaming truncation is silently reported as success.** Once HTTP 200 + headers are flushed to start a streamed response, a downstream failure mid-stream (DB error, cancellation, serialization fault) cannot be downgraded to a 500 — the client receives a truncated file that looks like a complete, valid export. This is the one supported full-dataset backup/restore path (FR-23); a silently-truncated "backup" is worse than today's clean 500, because today's failure is at least visible. | 3 | 3 | **9 — BLOCK** | Must design an integrity mechanism before merge: e.g. write a `recordCount`/checksum trailer object as the last streamed element and require the import/restore side to validate it before trusting the file, and/or buffer-then-flush per top-level collection (not per-byte) so a mid-collection failure can still abort the whole response via `context.Abort()` before enough bytes are flushed to look complete. This is a structural property of streaming, not an implementation detail — it needs explicit design, not just "make it stream." |
| R2 | DATA/TECH | Paged rewrite of `HouseholdExportReader` silently drops or duplicates rows relative to today's unbounded `ToListAsync()` reads — especially archived `Room`/`PowerPoint`/`Device` rows (AD-10, deliberately included) or rows at page boundaries if the cursor/order column isn't unique+stable. | 2 | 3 | **6 — MITIGATE** | Golden-master diff test: same seeded household, compare old unbounded read's output byte-for-byte (modulo key ordering) against new paged/streamed output. Order by a unique tiebreaker (e.g. `(HouseholdId, Id)` not just a timestamp) to avoid page-boundary skew when duplicate timestamps exist (SmartPlugReadings intervals can collide). |
| R3 | DATA/TECH | Paged rewrite accidentally reintroduces a tenant-isolation bypass (AD-3) — e.g. a hand-rolled cursor query written to skip the global `HouseholdId` query filter, or use of `FromSqlRaw`/`IgnoreQueryFilters` to implement keyset pagination. | 2 | 3 | **6 — MITIGATE** | `EnergyTracker.Architecture.Tests` guard test extended (or confirmed to already catch) `FromSqlRaw`/`IgnoreQueryFilters` usage in the new reader; explicit test with two households' data seeded, asserting household B's rows never appear in household A's export at any page. |
| R4 | PERF | The fix "streams" but doesn't actually reduce peak memory enough — e.g. pages are too large, or the reader still buffers a full collection before handing it to the writer, or the writer accumulates all pages before the first flush. Ships a fix that looks right in small-fixture tests but reproduces the OOM in production at real data volume. | 2 | 3 | **6 — MITIGATE** | **The reproducer test (Section 4, must-not-skip):** seed a household with SmartPlugReadings volume calibrated to the volume that produced the original 15:15/15:21 production OOM, run the export inside a memory-bounded test harness (constrain the test process, or assert peak working-set delta via `GC.GetAllocatedBytesForCurrentThread()`/dotnet-counters around the call), assert peak allocation stays within a defined ceiling. |
| R5 | PERF/TECH | New paged queries lack a supporting index for the (`HouseholdId`, cursor-column) shape → keyset pagination degrades to a table/index scan per page, multiplying cost across pages (worse than the original single unbounded query in total DB time), risking `CommandTimeout=30` failures replacing the OOM failure. | 2 | 2 | **4 — MONITOR** (escalates to 6 if confirmed missing) | Verify/define a composite index `(HouseholdId, <cursor column>)` for each paged entity (at minimum `SmartPlugReadings`, `MeterReadings`, `Events` — the volume drivers) on **both** providers via `scripts/add-migration.sh`. Add an execution-plan assertion test (or `EXPLAIN`/`SET STATISTICS` check in a Testcontainers-backed test) confirming an index seek, not a scan, is used. |
| R6 | TECH | Dual-provider index drift (AD-2) — index added to one provider's migration project but not the other, or defined with provider-specific syntax that doesn't translate. | 1 | 2 | 2 — DOCUMENT | Covered structurally by the existing `scripts/add-migration.sh` convention (adds to both projects in one commit) — flag as a checklist item in the fix's DoD rather than a separate test, since AD-2 already has architecture-test coverage for the shared-DbContext/portable-subset invariant. |
| R7 | TECH | Manual `Span<T>`/`Utf8JsonWriter`-level writing introduces a correctness bug (off-by-one, wrong length prefix, encoding error) that unit tests miss because they use small, "nice" fixture data. | 2 | 2 | 4 — MONITOR | Property-based/round-trip test: serialize → deserialize → deep-equal against the source aggregate, across a range of sizes including boundary sizes (empty collections, single-element, buffer-boundary-straddling sizes e.g. sized to cross typical `ArrayPool` rent thresholds). |
| R8 | OPS | No regression guard exists today for container memory pressure on this endpoint — this incident was caught by the user noticing a UI error, not by monitoring/alerting. | 2 | 2 | 4 — MONITOR | Not a test per se, but flag as a DoD item: add an OTel metric/log line for export peak duration and payload size so a regression is observable in `energytracker-prod-law` before it OOMs again (note: OTel logging deliberately doesn't forward to App Insights, only traces+metrics — per project-context.md — so this must land as a metric or stdout log, not rely on App Insights logs). |

### NFR Planning

- **Performance** — threshold: **UNKNOWN**, not documented anywhere for this endpoint today. Converted to clarification item: *"What's the acceptable p95 latency and peak memory ceiling for `/api/household-export` at realistic max household data volume?"* Recommended default (for the user/spec author to confirm, not asserted as fact): peak working-set delta for the request should stay comfortably under the container's 1Gi limit (e.g. a working ceiling of ~300–400Mi, leaving headroom for concurrent requests and the ASP.NET Core baseline), and time-to-first-byte should be bounded (streaming's whole point is that TTFB shouldn't scale with data volume the way the current 16–20s does). Evidence source: the reproducer integration test (R4) plus a lightweight local load probe, not k6/browser-based — this is a single-request memory-exhaustion risk, not a concurrency-under-load risk, so the project's existing Testcontainers-based integration test convention is the right evidence source, not a new load-testing tool.
- **Reliability** — R1's mid-stream-failure design is the core reliability NFR here. Evidence source: a fault-injection test that cancels/throws partway through the DB reads and asserts the response is either fully absent (connection reset, no 200 sent) or verifiably incomplete via the trailer/checksum mechanism — never a clean-looking 200 with truncated content.
- **Scalability** — data volume grows monotonically per household (meter/smart-plug readings never deleted, AD-10). Evidence source: the same reproducer test re-run periodically (or parameterized across 1×/10×/50× today's failure volume) so a future regression is caught before it reaches production scale again, not just at today's snapshot size.
- **Maintainability/Portability (AD-2)** — dual-provider correctness for the new paged query shape. Evidence source: existing Testcontainers-based Postgres+SqlServer parity tests, extended to the new reader.
- **Security (AD-3 tenant isolation)** — covered under R3.

### Summary

Highest risks: **R1 (score 9, BLOCK)** — the streaming-truncation integrity gap — is the one finding that isn't in the user's stated requirements (streaming, paging, indexes, Span<T>) but is a direct structural consequence of doing streaming naively, and it's more dangerous than the OOM it replaces if unaddressed. **R2/R3/R4 (score 6 each, MITIGATE)** are the correctness and the "did we actually fix it" risks — R4's reproducer test is the one piece of coverage that must not be skipped, per your original ask. R5 (indexes) is currently MONITOR pending confirmation the paging shape actually needs a new index (depends on the paging strategy chosen — keyset vs offset — which isn't decided yet; recommend keyset/cursor pagination specifically, since offset pagination is the anti-pattern that would make R5 materialize).

## Step 4: Coverage Plan & Execution Strategy

### Coverage Matrix

| ID | Scenario | Level | Priority | Risk(s) covered |
|----|----------|-------|----------|------------------|
| U1 | `ExportHouseholdData` orchestrates via paged/streamed reader calls, never a single unbounded collection fetch (assert via NSubstitute call shape on `IHouseholdExportReader`, not raw data) | Unit | P1 | R4 (design-level guard) |
| U2 | Manual `Span<T>`/`Utf8JsonWriter` writer round-trips correctly across boundary sizes (empty, single-element, sizes that straddle typical buffer/ArrayPool rent thresholds) | Unit | P1 | R7 |
| U3 | Cursor/keyset ordering key is unique+stable per row (no two rows in a household share a cursor value) | Unit | P2 | R2 (page-boundary skew) |
| I1 | Paged reader's total output is set-equal to today's unbounded `ToListAsync()` output for the same seeded household, **including archived rows** | Integration (Testcontainers, both providers) | **P0** | R2 |
| I2 | Paged reader never returns another household's rows at any page, across all paged entities | Integration (Testcontainers, both providers) | **P0** | R3 |
| I3 | New paged query executes as an index seek (not a scan) on both providers | Integration (Testcontainers, both providers) | P1 | R5 |
| I4 | Paged query shape produces identical results on Postgres vs SQL Server (AD-2 parity) | Integration (Testcontainers, both providers) | P1 | R6 (structural), AD-2 |
| A1 | **Reproducer (must not skip):** household seeded with SmartPlugReadings volume calibrated to the 2026-09-25 production incident; export succeeds (no 500/OOM); peak allocation/working-set stays under a defined ceiling | API (WebApplicationFactory) | **P0** | R4 |
| A2 | **Mid-stream failure integrity:** inject a fault partway through paged reads (cancel/throw on page N); response is never a clean-looking 200 with silently truncated content — either aborted or verifiably flagged incomplete | API (WebApplicationFactory) | **P0** | R1 (score 9, BLOCK) |
| A3 | Exported JSON shape/field set is unchanged vs. the pre-fix baseline (golden-file diff, order-independent per collection) | API (WebApplicationFactory) | P1 | R2 |
| A4 | Existing auth/forbidden-without-household/happy-path tests retained and adapted to the streamed response type | API (WebApplicationFactory) | P1 | Regression (existing 3 test files) |
| A5 | Response demonstrably streams (first bytes/headers observable before all DB pages are read) — proves the fix isn't "buffer-then-write-fast" masquerading as streaming | API (WebApplicationFactory) | P2 | R4 (design intent) |

No duplicate coverage: U-level tests never touch a real DB; I-level tests never go through HTTP; A-level tests are the only ones exercising the full endpoint + real streaming behavior.

### NFR Coverage & Evidence Plan

| NFR | Validation scenario | Level/tool | Evidence artifact |
|-----|---------------------|-----------|--------------------|
| Performance (threshold **UNKNOWN** — clarification item, not guessed) | A1 | Testcontainers-backed integration test, memory profiling via `GC.GetAllocatedBytesForCurrentThread`/dotnet-counters | Peak allocation measurement in test output/CI log |
| Reliability | A2 | Fault-injection integration test | Test result + response integrity assertion |
| Scalability | A1 parameterized at 1×/10×/50× the incident's data volume | Testcontainers, nightly tier for larger multiples | Trend of peak-allocation-vs-volume across runs |
| Portability/Maintainability (AD-2) | I4 | Testcontainers dual-provider | Parity test result |
| Security (AD-3 tenant isolation) | I2 | Testcontainers | Test result; optionally extend `EnergyTracker.Architecture.Tests` static guard |

Missing threshold (performance) is a **blocker for `nfr-assess` later**, not for test design — recommend the spec/story doc (already required by the process gate) states the target explicitly before implementation starts, so A1's ceiling isn't invented ad hoc during coding.

### Execution Strategy

- **PR (target <15 min, matches existing Testcontainers-based suite convention):** U1–U3, I1–I4, A1 (baseline/1× tier only), A2, A3, A4.
- **Nightly/Weekly:** A1 at 10×/50× volume tiers (scalability trend) and A5 (exploratory streaming-shape check — useful but not a correctness gate).

### Resource Estimates

- P0 (I1, I2, A1 baseline, A2): ~16–24 hours — the fault-injection harness (A2) and calibrated large-volume fixture (A1) are the novel, non-trivial parts.
- P1 (U1, U2, I3, I4, A3, A4): ~14–20 hours — I3's execution-plan assertion and rewriting the 3 existing test files are the bulk.
- P2 (U3, A5): ~4–8 hours.
- **Total: ~34–52 hours**, plus the spec/story doc itself (process-gate prerequisite, not counted above).

### Quality Gates

- P0 pass rate: 100% (non-negotiable — A2 in particular, since it's the score-9 finding).
- P1 pass rate: ≥95%.
- R1 (score 9) and any other score-9 risk must be resolved (not just mitigated) before merge, per `risk-governance.md`'s BLOCK threshold.
- R2/R3/R4/R5 (score 6) mitigations complete (i.e., I1/I2/A1/I3 green) before merge.
- Coverage target: ≥80% of the matrix above at P0/P1 (12 of 13 scenarios are P0/P1 — target is effectively "all of them, plus A5 opportunistically").
- NFR evidence identified for all 5 categories above; full PASS/CONCERNS/FAIL deferred to a later `nfr-assess` run once implementation evidence exists.
- **Process-gate dependency (not a test gate, but blocks the same merge):** this fix cannot merge without a linked spec/story doc per project-context.md's hard gate — flagging per your original ask not to skip it.
