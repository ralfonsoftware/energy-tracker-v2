---
workflowStatus: 'completed'
totalSteps: 5
stepsCompleted: ['step-01-detect-mode', 'step-02-load-context', 'step-03-risk-and-testability', 'step-04-coverage-plan', 'step-05-generate-output']
lastStep: 'step-05-generate-output'
nextStep: ''
lastSaved: '2026-09-25'
---

# Test Design: Household Export OutOfMemoryException Fix

**Date:** 2026-09-25
**Author:** Ralf (via Murat, Master Test Architect)
**Status:** Draft

---

## Executive Summary

**Scope:** Epic-level (single scoped fix) test design for the `GET /api/household-export` production OOM incident — streaming response, paged/smart DB access, matching indexes, and performant C# (`Span<T>`/`Utf8JsonWriter`) rewrite.

**Origin:** Live production incident, confirmed via Azure Container Apps console logs (`energytracker-prod-app`, resource group `energy-tracker-rg`) — two reproduced `System.OutOfMemoryException` crashes on 2026-09-25 at 15:15:39 and 15:21:51, root-caused to `JsonSerializer.SerializeToUtf8Bytes` at `HouseholdExportEndpoints.cs:45` over an unbounded, fully-materialized read from `HouseholdExportReader.GetExportDataAsync` (0.5 vCPU / 1Gi container, single replica).

**Risk Summary:**

- Total risks identified: 8
- High-priority risks (≥6): 4 (one at score 9 — BLOCK)
- Critical categories: DATA, PERF

**Coverage Summary:**

- P0 scenarios: 7 tests across 4 requirements (~16–24 hours)
- P1 scenarios: 13 tests across 6 requirements (~14–20 hours)
- P2 scenarios: 5 tests across 3 requirements (~4–8 hours)
- **Total effort**: ~34–52 hours (~4.5–6.5 days), excluding the spec/story doc itself

---

## Not in Scope

| Item | Reasoning | Mitigation |
|------|-----------|------------|
| Concurrent-load/k6-style testing | This incident is a single-request memory-exhaustion risk driven by per-household data volume, not concurrency under load. The project's existing Testcontainers-based integration convention is the right evidence source. | Covered by A1's memory-profiling approach instead. |
| Frontend/UI changes | `/api/household-export` has no Playwright/e2e coverage today and none is warranted — it's a pure API/file-download surface. | N/A — confirmed via repo search, no `web/e2e` references to export exist. |
| `HouseholdImportEndpoints` (the restore side) | Out of scope for this fix, but R-001 (streaming truncation integrity) has direct implications for what the import side must validate — flag as a follow-on. | Cross-reference only; no test scenarios authored here for import. |

---

## Risk Assessment

### High-Priority Risks (Score ≥6)

| Risk ID | Category | Description | Probability | Impact | Score | Mitigation | Owner | Timeline |
|---------|----------|--------------|-------------|--------|-------|------------|-------|----------|
| R-001 | DATA/BUS | **Streaming truncation is silently reported as success.** Once HTTP 200 + headers flush to start a streamed response, a downstream failure mid-stream cannot be downgraded to 500 — client receives a truncated file indistinguishable from a complete export. This is the sole full-dataset backup/restore path (FR-23); a silently-truncated "backup" is worse than today's visible 500. | 3 | 3 | **9 — BLOCK** | Design an integrity mechanism before merge: write a trailing `recordCount`/checksum object as the last streamed element that the import/restore side must validate before trusting the file, and/or buffer-then-flush per top-level collection (not per-byte) so a mid-collection failure can still abort via `context.Abort()` before enough bytes look complete. | Dev (fix author) | Before merge |
| R-002 | DATA/TECH | Paged rewrite of `HouseholdExportReader` silently drops or duplicates rows vs. today's unbounded reads — especially archived `Room`/`PowerPoint`/`Device` rows (AD-10, deliberately included) or rows at page boundaries if the cursor/order column isn't unique+stable. | 2 | 3 | **6 — MITIGATE** | Golden-master diff test (I1): same seeded household, old unbounded read vs. new paged/streamed output, set-equal comparison including archived rows. Order by a unique tiebreaker (e.g. `(HouseholdId, Id)`), not a timestamp alone — SmartPlugReading intervals can collide. | Dev | Before merge |
| R-003 | DATA/TECH | Paged rewrite accidentally reintroduces a tenant-isolation bypass (AD-3) — e.g. a hand-rolled cursor query skipping the global `HouseholdId` filter, or `FromSqlRaw`/`IgnoreQueryFilters` used to implement keyset pagination. | 2 | 3 | **6 — MITIGATE** | Extend `EnergyTracker.Architecture.Tests` guard coverage to the new reader; explicit two-household test (I2) asserting household B's rows never appear in household A's export at any page. | Dev | Before merge |
| R-004 | PERF | The fix "streams" but doesn't actually reduce peak memory enough (pages too large, reader still buffers a full collection, or writer accumulates all pages before first flush) — ships a fix that passes small-fixture tests but reproduces the OOM at real production data volume. | 2 | 3 | **6 — MITIGATE** | **The reproducer test (A1) — must not be skipped.** Seed a household with SmartPlugReadings volume calibrated to the 15:15/15:21 production incident; run through the real endpoint inside a memory-bounded harness; assert peak allocation/working-set stays under a defined ceiling. | Dev | Before merge |

### Medium-Priority Risks (Score 3-4)

| Risk ID | Category | Description | Probability | Impact | Score | Mitigation | Owner |
|---------|----------|--------------|-------------|--------|-------|------------|-------|
| R-005 | PERF/TECH | New paged queries lack a supporting composite index for the (`HouseholdId`, cursor-column) shape → keyset pagination degrades to a scan per page, multiplying total DB cost across pages, risking `CommandTimeout=30` failures replacing the OOM. *Escalates to 6 if confirmed missing after implementation.* | 2 | 2 | 4 | Define/verify composite index `(HouseholdId, <cursor column>)` for each paged entity (at minimum `SmartPlugReadings`, `MeterReadings`, `Events`) on **both** providers via `scripts/add-migration.sh`. Add an execution-plan assertion test (I3) confirming an index seek, not a scan. **Recommend keyset (cursor) pagination over offset pagination** — offset pagination is the anti-pattern that would make this risk materialize at high scores. | Dev |
| R-007 | TECH | Manual `Span<T>`/`Utf8JsonWriter`-level writing introduces a correctness bug (off-by-one, wrong length prefix, encoding error) missed by unit tests using small "nice" fixture data. | 2 | 2 | 4 | Property-based/round-trip test (U2): serialize → deserialize → deep-equal, across boundary sizes (empty, single-element, sizes straddling typical `ArrayPool` rent thresholds). | Dev |
| R-008 | OPS | No regression guard exists today for container memory pressure on this endpoint — this incident was caught by the user noticing a UI error, not by monitoring. | 2 | 2 | 4 | DoD item (not a test): add an OTel metric/log line for export peak duration and payload size, observable in `energytracker-prod-law`. Note: OTel logging deliberately doesn't forward to App Insights (traces+metrics only, per project-context.md) — this must land as a metric or stdout log. | Dev |

### Low-Priority Risks (Score 1-2)

| Risk ID | Category | Description | Probability | Impact | Score | Action |
|---------|----------|--------------|-------------|--------|-------|--------|
| R-006 | TECH | Dual-provider index drift (AD-2) — index added to one provider's migration project but not the other, or defined with non-portable syntax. | 1 | 2 | 2 | Monitor — structurally covered by the existing `scripts/add-migration.sh` convention; flag as a DoD checklist item rather than a separate test. |

### Risk Category Legend

- **TECH**: Technical/Architecture (flaws, integration, scalability)
- **SEC**: Security (access controls, auth, data exposure)
- **PERF**: Performance (SLA violations, degradation, resource limits)
- **DATA**: Data Integrity (loss, corruption, inconsistency)
- **BUS**: Business Impact (UX harm, logic errors, revenue)
- **OPS**: Operations (deployment, config, monitoring)

---

## NFR Planning

**Purpose:** Capture fix-specific NFR thresholds, planned validation, and evidence expected for a later `nfr-assess`. This is not a final evidence audit.

| NFR Category | Requirement / Threshold | Risk Link | Planned Validation | Evidence Needed |
|---------------|--------------------------|-----------|----------------------|-------------------|
| Performance | **UNKNOWN — not documented anywhere today.** Recommended default (needs confirmation, not asserted): peak working-set delta stays comfortably under the container's 1Gi limit (e.g. ~300–400Mi headroom), and time-to-first-byte does not scale with data volume the way today's 16–20s does. | R-004, R-005 | Testcontainers-backed integration test (A1) with `GC.GetAllocatedBytesForCurrentThread`/dotnet-counters memory profiling | Peak allocation measurement in test output/CI log |
| Reliability | Mid-stream failure must never present as a clean 200 with silently truncated content. | R-001 | Fault-injection integration test (A2) — cancel/throw partway through paged reads | Test result + response-integrity assertion |
| Scalability | Data volume grows monotonically per household (readings never deleted, AD-10) — fix must not regress as volume grows past today's snapshot. | R-004 | A1 parameterized at 1×/10×/50× the incident's data volume, larger tiers on nightly tier | Trend of peak-allocation-vs-volume across runs |
| Maintainability / Portability (AD-2) | Paged query shape must produce identical results on Postgres and SQL Server. | R-006 | Testcontainers dual-provider parity test (I4) | Parity test result |
| Security (AD-3 tenant isolation) | Paged reader must never cross household boundaries. | R-003 | Testcontainers two-household test (I2) | Test result; optional `EnergyTracker.Architecture.Tests` static guard extension |

**Unknown thresholds:** Performance ceiling (peak memory, TTFB) is not documented — this is a blocker for a later `nfr-assess`, not for this test design. Recommend the spec/story doc (already required by the project's process gate) states the target explicitly before implementation starts, so A1's ceiling isn't invented ad hoc during coding.

---

## Entry Criteria

- [ ] **Spec/story doc exists and is linked** — hard process gate per project-context.md ("No fix to already-shipped code merges without a linked spec/story doc"); this fix cannot start implementation-for-merge without it.
- [ ] Performance threshold (peak memory / TTFB target) confirmed in that spec, not left as this document's placeholder default.
- [ ] Pagination strategy decided (keyset/cursor recommended — see R-005) before I3's index work starts.
- [ ] Testcontainers Postgres + SqlServer available in CI (already standard per project-context.md — no new environment provisioning needed).

## Exit Criteria

- [ ] All P0 tests passing (I1, I2, A1, A2)
- [ ] All P1 tests passing (U1, U2, I3, I4, A3, A4) or failures triaged
- [ ] R-001 (score 9) resolved, not just mitigated — BLOCK threshold per `risk-governance.md`
- [ ] R-002/R-003/R-004 (score 6) mitigations verified green
- [ ] No regression in the 3 existing export test files' intent (auth/forbidden/happy-path)

---

## Test Coverage Plan

**Note:** P0–P3 below reflect priority/risk, not execution timing — see Execution Strategy for when each scenario actually runs.

### P0 (Critical)

**Criteria**: Blocks core data-integrity/backup functionality + High risk (≥6) + No workaround

| ID | Requirement | Test Level | Risk Link | Test Count | Owner | Notes |
|---|---|---|---|---|---|---|
| I1 | Paged reader output set-equal to old unbounded output, incl. archived rows | Integration (Testcontainers, both providers) | R-002 | 2 (one per provider) | Dev | Golden-master diff, order-independent |
| I2 | Paged reader never crosses household boundary at any page | Integration (Testcontainers, both providers) | R-003 | 2 (one per provider) | Dev | Two-household seed |
| A1 | Reproducer: export succeeds under incident-calibrated data volume, peak allocation under ceiling | API (WebApplicationFactory) | R-004 | 1 (baseline tier; larger tiers nightly, see P2) | Dev | **Must not be skipped** — this is the test that proves the fix actually works |
| A2 | Mid-stream failure never presents as a clean truncated 200 | API (WebApplicationFactory) | R-001 | 2 (fault at early page, fault at late page) | Dev | The score-9 finding — new requirement, not in original ask |

**Total P0**: 7 tests, ~16–24 hours

### P1 (High)

**Criteria**: Important correctness/perf-adjacent coverage + Medium risk (3-4) + Common workflows

| ID | Requirement | Test Level | Risk Link | Test Count | Owner | Notes |
|---|---|---|---|---|---|---|
| U1 | `ExportHouseholdData` orchestrates via paged calls, never unbounded fetch | Unit | R-004 (design guard) | 2 | Dev | NSubstitute call-shape assertion |
| U2 | `Span<T>`/`Utf8JsonWriter` writer round-trips across boundary sizes | Unit | R-007 | 4 | Dev | Empty, single-element, buffer-boundary sizes |
| I3 | New paged query executes as index seek, not scan | Integration (Testcontainers, both providers) | R-005 | 2 (one per provider) | Dev | Execution-plan/`EXPLAIN` assertion |
| I4 | Paged query shape produces identical results Postgres vs SQL Server | Integration (Testcontainers) | R-006/AD-2 | 1 | Dev | Parity test |
| A3 | Exported JSON shape/field set unchanged vs. pre-fix baseline | API (WebApplicationFactory) | R-002 | 1 | Dev | Golden-file, order-independent per collection |
| A4 | Existing auth/forbidden/happy-path tests retained, adapted to streamed response | API (WebApplicationFactory) | Regression | 3 (rewrite of existing `HouseholdExportEndpointsTests.cs`) | Dev | Existing 115-line file needs adaptation, not just addition |

**Total P1**: 13 tests, ~14–20 hours

### P2 (Medium)

**Criteria**: Secondary coverage + Low risk (1-2) + Scale/exploratory

| ID | Requirement | Test Level | Risk Link | Test Count | Owner | Notes |
|---|---|---|---|---|---|---|
| U3 | Cursor/keyset ordering key is unique+stable per row | Unit | R-002 (page-boundary skew) | 2 | Dev | Pure function test |
| A5 | Response demonstrably streams (first bytes before all pages read) | API (WebApplicationFactory) | R-004 (design intent) | 1 | Dev | Proves it isn't "buffer-then-write-fast" |
| A1 (10×/50× tier) | Reproducer at 10×/50× incident volume (scalability trend) | API (WebApplicationFactory) | R-004 | 2 | Dev | Nightly tier only — expensive seeding |

**Total P2**: 5 tests, ~4–8 hours

### P3 (Low)

None identified for this scoped fix — all coverage above maps to a real risk ≥2.

---

## Execution Strategy

**Philosophy:** run everything in PRs unless a scenario is expensive/long-running; defer only that subset.

- **Every PR (~23 scenarios, target <15 min):** all P0 and P1 scenarios plus P2's cheap scenarios (unit, integration via Testcontainers, and API-level, including the baseline-tier reproducer at 1× incident volume). This matches the project's existing Testcontainers-based test convention — no new infrastructure needed.
- **Nightly/Weekly (~2 scenarios):** the 10×/50× scalability-tier reproducer runs only — seeding that much data on every PR would be wasteful; a nightly cadence still catches a scalability regression before it reaches production volume again.
- P2's remaining scenario (cursor uniqueness, streaming-shape check) is cheap enough to run in PR alongside P0/P1; only the large-volume reproducer tiers are deferred.

---

## Resource Estimates

### Test Development Effort

| Priority | Count | Notes |
|---|---|---|
| P0 | 7 | ~16–24 hours — fault-injection harness (R-001) and calibrated large-volume fixture (R-004) are the novel, non-trivial parts |
| P1 | 13 | ~14–20 hours — execution-plan assertion and rewriting the 3 existing test files are the bulk |
| P2 | 5 | ~4–8 hours |
| **Total** | **25** | **~34–52 hours (~4.5–6.5 days)** |

### Prerequisites

**Test Data:**

- A household data factory capable of seeding SmartPlugReadings at a calibrated large volume (needed for A1/reproducer and P2's 10×/50× tiers) — likely a new factory, existing ones size for normal-case fixtures only.
- Two-household fixture for tenant-isolation tests (I2).

**Tooling:**

- Testcontainers.PostgreSql/MsSql (already in use) for I1–I4.
- `dotnet-counters` or `GC.GetAllocatedBytesForCurrentThread` for memory profiling in A1 — no new external tool (k6 not applicable here, see Not in Scope).

**Environment:**

- CI must tolerate P0's larger seed volume within existing time budgets — worth benchmarking seed time early, since a slow seed could push P0 over the <10 min target and require moving baseline-tier A1 to a lighter calibrated volume than the exact production figure.

---

## Quality Gate Criteria

### Pass/Fail Thresholds

- **P0 pass rate**: 100% (no exceptions — R-001 in particular)
- **P1 pass rate**: ≥95% (waivers required for failures)
- **P2 pass rate**: ≥90% (informational)
- **High-risk mitigations**: 100% complete; R-001 (score 9) must be resolved, not waived

### Coverage Targets

- **Data-integrity scenarios (R-001–R-003)**: 100%
- **Performance regression scenario (R-004)**: 100% — this is the one the user explicitly required not to skip
- **Index/portability scenarios (R-005/R-006)**: ≥80%, escalate R-005 to MITIGATE tier if index confirmed missing

### Non-Negotiable Requirements

- [ ] All P0 tests pass
- [ ] R-001 resolved (score-9 BLOCK)
- [ ] No high-risk (≥6) items unmitigated
- [ ] Performance ceiling confirmed in spec/story doc and validated by A1
- [ ] Linked spec/story doc exists (process gate — not a test gate, but blocks the same merge)

---

## Mitigation Plans

### R-001: Streaming truncation silently reported as success (Score: 9)

**Mitigation Strategy:** Add a trailing integrity element (record count and/or checksum) as the last item in the streamed JSON; the import/restore path must validate it before trusting the file. Alternatively/additionally, buffer-and-flush at collection granularity so a mid-collection fault can still trigger `context.Abort()` before the response looks complete.
**Owner:** Dev (fix author)
**Timeline:** Before merge — this is a BLOCK-tier finding
**Status:** Planned
**Verification:** A2 (fault-injection test) — response must never be a clean 200 with silently truncated content.

### R-002: Paged rewrite drops/duplicates rows (Score: 6)

**Mitigation Strategy:** Golden-master diff against today's unbounded read; unique+stable ordering key.
**Owner:** Dev
**Timeline:** Before merge
**Status:** Planned
**Verification:** I1.

### R-003: Paged rewrite reintroduces tenant-isolation bypass (Score: 6)

**Mitigation Strategy:** Architecture-test guard extension + explicit two-household boundary test.
**Owner:** Dev
**Timeline:** Before merge
**Status:** Planned
**Verification:** I2.

### R-004: Fix doesn't actually resolve the OOM at real volume (Score: 6)

**Mitigation Strategy:** Reproducer test calibrated to the actual incident's data volume, run under memory profiling.
**Owner:** Dev
**Timeline:** Before merge
**Status:** Planned
**Verification:** A1.

---

## Assumptions and Dependencies

### Assumptions

1. Keyset (cursor-based) pagination will be chosen over offset pagination — this design assumes it; if offset pagination is chosen instead, R-005 should be re-scored upward (likely to 6) and I3 becomes mandatory rather than confirmatory.
2. The performance ceiling values proposed in NFR Planning are placeholders for the spec/story author to confirm, not committed thresholds.
3. `IHouseholdExportReader`'s public contract may need to change shape (e.g., to `IAsyncEnumerable<T>` per collection) to support true streaming — this affects Application-layer consumers and their existing NSubstitute-based tests (U1's rewrite scope).

### Dependencies

1. Linked spec/story doc — required before merge, per project's hard process gate.
2. Pagination strategy decision (keyset vs. offset) — blocks I3's index design.
3. Confirmed performance threshold — blocks a meaningful A1 ceiling assertion (currently UNKNOWN).

### Risks to Plan

- **Risk**: Calibrating A1's seed volume to "exactly what caused the production incident" may be expensive to seed in CI, pushing P0 execution time past the <10 min target.
  - **Impact**: Either P0 execution time balloons, or the team is tempted to under-seed A1 and lose its "must not skip" value.
  - **Contingency**: Profile seed time early; if too slow, seed via bulk insert (bypassing EF change tracking) rather than entity-by-entity, and/or move the exact-incident-volume tier to a fast-following nightly check while keeping a smaller-but-still-representative tier in PR.

---

## Follow-on Workflows (Manual)

- Run `*atdd` to generate failing P0 tests (separate workflow; not auto-run).
- Run `*automate` for broader coverage once implementation exists.
- Consider a follow-up spec for `HouseholdImportEndpoints` to validate whatever integrity trailer R-001's mitigation introduces (out of scope here, but the import side must consume it).

---

## Interworking & Regression

| Service/Component | Impact | Regression Scope |
|---|---|---|
| `EnergyTracker.Api.Endpoints.HouseholdExportEndpoints` | Response type changes from buffered `Results.File` to a streamed response | `HouseholdExportEndpointsTests.cs` (115 lines) — full rewrite of assertions against response shape |
| `EnergyTracker.Application.ExportHouseholdData` | Orchestration changes from eager collection-based to paged/async-enumerable | `ExportHouseholdDataTests.cs` (359 lines) — mocking shape changes |
| `EnergyTracker.Infrastructure.Adapters.HouseholdExportReader` | Core rewrite: unbounded `ToListAsync()` → paged/keyset queries | `HouseholdExportReaderTests.cs` (297 lines) — largest rewrite surface |
| `EnergyTracker.Architecture.Tests` | New guard coverage needed for the paged reader (R-003) | Extend `DomainHasNoExternalDependenciesTests.cs`-style invariant tests if a suitable guard doesn't already exist |
| `HouseholdImportEndpoints` (restore side, out of scope) | Must eventually consume R-001's integrity trailer | Not tested here — flagged as follow-on |

---

## Appendix

### Knowledge Base References

- `risk-governance.md` - Risk classification framework
- `probability-impact.md` - Risk scoring methodology
- `test-levels-framework.md` - Test level selection
- `test-priorities-matrix.md` - P0-P3 prioritization
- `nfr-criteria.md` - NFR validation patterns (performance/reliability sections referenced; k6 pattern noted but not applicable — single-request memory risk, not concurrency load)

### Related Documents

- PRD: `_bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08`
- Architecture: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE` (AD-2, AD-3, AD-10 cited above)
- Spec/story: **not yet created — required before merge per process gate**
- Incident evidence: Azure Container Apps console logs, `energytracker-prod-app` / `energy-tracker-rg`, 2026-09-25 15:15:39 and 15:21:51 UTC

---

**Generated by**: Murat, Master Test Architect (BMad TEA Agent)
**Workflow**: `bmad-testarch-test-design`
**Version**: 4.0 (BMad v6)
