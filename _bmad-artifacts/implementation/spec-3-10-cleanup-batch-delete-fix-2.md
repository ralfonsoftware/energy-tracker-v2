---
title: 'Chunk cleanup delete by reading volume, not just import count'
type: 'bugfix'
created: '2026-09-12'
status: 'done'
review_loop_iteration: 1
baseline_commit: 'a5c1a19d8df773b4fcce17b2fa8f0864e4dd2932'
context: ['{project-root}/_bmad-artifacts/specs/spec-job-cleanup-bulk-delete-timeout/SPEC.md']
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Round-1's fix (batch `DeleteEligibleAsync` by import *count*, `DeleteBatchSize=200`) shipped and still 500s: this household has only 51-60 eligible rows (under 200), so count-based chunking never splits. 5 of 51 imports carry 57k-122k `SmartPlugReading` rows each (487,380 total) — one `DELETE FROM SmartPlugImports` cascades all of them at once and still hits `CommandTimeout`.

**Approach:** Measure each import's reading count via one `GROUP BY` query, then greedily pack `importIds` into chunks bounded by cumulative reading count (new threshold) as well as the existing import-count cap. `jobIds` loop is untouched.

## Boundaries & Constraints

**Always:**
- `jobIds` loop keeps its existing chunking unchanged — only `importIds` chunking changes.
- Packing is a pure `internal static` helper taking a precomputed `IReadOnlyDictionary<Guid,int>` and both thresholds as parameters — unit-testable without a database.
- A single import whose own count exceeds the threshold still forms its own chunk (never split further).
- Same outer transaction, same FK order as round 1 — no per-chunk transactions.

**Ask First:** none.

**Never:** eligibility-logic changes, `jobIds`-loop changes, per-chunk transactions.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Many small imports | Tiny counts, import-count > cap | Import-count cap still splits (unchanged) | N/A |
| Few large imports | Incident shape: several imports at tens of thousands of readings | Each large import in its own chunk/small group; multiple commands issued | N/A |
| Single import over threshold | One import's count alone exceeds threshold | Forms its own chunk anyway | N/A |
| Zero-reading import | Missing from `GROUP BY` result | Treated as 0, packs normally | N/A |

</frozen-after-approval>

## Code Map

- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs` -- add threshold constant, packing helper, wire a *chunked* `GROUP BY` measurement + new chunking into `DeleteEligibleAsync`
- `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryChunkingTests.cs` (new) -- pure unit tests for the packing algorithm
- `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs` -- new integration test at modest incident-shaped scale, plus a volume-triggered rollback case
- `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryDeleteJobsSqlServerTests.cs` -- same incident-shaped test against the real production provider (round-1 review's own reason this file exists — CAP-4 needs the same dual-provider proof CAP-1 got)
- `_bmad-artifacts/implementation/deferred-work.md` -- mark the round-1 entry this fix resolves as `RESOLVED by spec-3-10-cleanup-batch-delete-fix-2`; add a new entry for the measurement-staleness residual risk

## Tasks & Acceptance

**Execution:**
- [x] `SmartPlugImportRepository.cs` -- `internal const int DeleteReadingVolumeThreshold = 20_000;`, doc comment citing the incident's 487,380-row/2-minute saturation as the reasoning for a value well under that
- [x] `SmartPlugImportRepository.cs` -- `internal static IEnumerable<Guid[]> ChunkImportIdsByReadingVolume(...)`: greedily accumulate; start a new chunk when adding the next id would exceed either bound; never emit an empty chunk, never leave an id unassigned
- [x] `SmartPlugImportRepository.cs` -- **round-2 review finding (Blind Hunter + Edge Case Hunter, both independently):** the reading-count measurement query handed the *entire unchunked* `importIds` list to one `Contains(...)` predicate — the exact "one SQL parameter per id" shape `DeleteBatchSize`'s own comment says was confirmed live to approach SQL Server's ~2100 limit. Fix: measure in `DeleteBatchSize`-sized chunks too (reusing the existing safe-parameter-count primitive for a new purpose), merging results into one dictionary before packing by volume.
- [x] `SmartPlugImportRepository.cs` -- in `DeleteEligibleAsync`, when `importIds.Count > 0`: chunked `GROUP BY` query over `SmartPlugReadings`, merged dictionary, `ChunkImportIdsByReadingVolume(importIds, readingCounts, DeleteReadingVolumeThreshold, DeleteBatchSize)` replacing `importIds.Chunk(DeleteBatchSize)`
- [x] `SmartPlugImportRepositoryChunkingTests.cs` (new) -- unit tests: many-tiny respects import-count cap; incident-shaped large imports each land alone; single import alone over threshold still forms one chunk; missing-from-dictionary treated as zero
- [x] `SmartPlugImportRepositoryTests.cs` -- seed a modest case (3 imports at ~8,000 readings each plus several small ones, exceeding the threshold in combination but not individually); assert `DeleteJobsAsync` still deletes everything and detaches every reading across the resulting chunks
- [x] `SmartPlugImportRepositoryTests.cs` -- **round-2 finding (Blind Hunter):** same test, strengthen with a command-count assertion (reuse the existing `DbCommandInterceptor` pattern from the rollback test, counting rather than failing) proving the expected number of chunks/commands actually ran — final-row-count alone can't distinguish "wired correctly" from "threshold/batch-size accidentally swapped, still correct by luck"
- [x] `SmartPlugImportRepositoryTests.cs` -- **round-2 finding (Blind Hunter):** extend or add a rollback-under-failure case where the chunk boundary is forced by the *volume* dimension (not just import count, which is all the existing rollback test exercises), proving atomicity holds for this new chunking path too
- [x] `SmartPlugImportRepositoryDeleteJobsSqlServerTests.cs` -- **round-2 finding (Blind Hunter), spec-level gap:** CAP-4's own success criterion requires SqlServer verification (the actual production provider in both incidents) and this file exists specifically for that reason, but round 2's first pass never touched it. Add the same incident-shaped reading-volume test here.
- [x] `deferred-work.md` -- mark the "measure per-import reading counts before chunking" round-1 entry `RESOLVED by spec-3-10-cleanup-batch-delete-fix-2`; add a new entry: reading counts are measured once before the transaction opens, so an import still `Processing`/accumulating readings between measurement and that chunk's delete could grow past what its chunk was sized for — a TOCTOU window neither round's code, SPEC, nor prior deferred-work entries acknowledged (round-2 review finding, both reviewers independently)

**Acceptance Criteria:**
- Given imports whose individual counts are small but sum past the threshold, when chunking runs, then it splits into multiple chunks each at or under threshold.
- Given a single import whose own count exceeds the threshold, when chunking runs, then it still lands in exactly one chunk.
- Given the incident-shaped integration test, when `DeleteJobsAsync(cutoffUtc: null)` runs, then every job/import/gap is deleted, every reading survives detached, and the actual command count matches the expected chunk count — verified against both Postgres and SqlServer.
- Given a failure forced at a chunk boundary triggered by the volume dimension, when the exception propagates, then the whole operation rolls back (same atomicity guarantee as the count-triggered case).
- Given existing round-1 tests (multi-batch by count, rollback, jobs-only), when run against this change, then all pass unchanged.

## Spec Change Log

- **Round 1 (bad_spec, root cause in this spec's own Code Map/Tasks, no Ralf renegotiation needed):** this spec's Tasks list never included updating `SmartPlugImportRepositoryDeleteJobsSqlServerTests.cs`, even though the canonical spec it's built from (`spec-job-cleanup-bulk-delete-timeout/SPEC.md`, CAP-4) explicitly requires SqlServer verification and that file exists specifically because round 1's own review found the original incident hit the SqlServer-hosted production database. Caught by Blind Hunter. **Resolution:** added the SqlServer task back to Code Map/Tasks. **KEEP:** the packing algorithm and its pure/unit-testable design were correct and untouched by this loopback.
- **Round 1 patches (folded into the re-derivation above):** the reading-count measurement query ran unchunked against the full `importIds` list — the same "one SQL parameter per id" shape that risks SQL Server's parameter ceiling at scale, flagged independently by both reviewers; no test asserted the actual chunk/command count, so a wiring bug (e.g. swapped threshold/batch-size arguments) could pass silently; the atomicity/rollback test only forced a chunk boundary via import count, never via the new volume dimension; the round-1 `deferred-work.md` entry this fix resolves was never marked resolved.
- **Round 1 accepted, not actioned:** measuring reading counts once before the transaction opens creates a TOCTOU window (an import still accumulating readings mid-measurement) — real, but the same class of pre-existing, already-accepted gap as round 1's eligibility-computed-before-transaction TOCTOU; documented in `deferred-work.md` rather than structurally fixed, since a real fix (re-measuring per-chunk, or snapshot isolation) is disproportionate to this incident's evidence. `DeleteReadingVolumeThreshold=20,000` remains an unbenchmarked starting value for the same reason `DeleteBatchSize=200` was — no live load-test data available; already honestly caveated in its own comment and the canonical spec's Open Questions, not a new gap. Int-overflow on cumulative reading counts and the integration test's non-bulk insert shape are real but disproportionate to fix here (the former needs billions of rows to matter; the latter needs production code changes — an injectable threshold — to meaningfully speed up without changing what's proven).
- **Round 2 patches (no loopback — round-3 review found only patch/reject-tier issues):** the SqlServer incident-shaped test had no command-count assertion (unlike its Postgres sibling), so it could pass on the exact "wired with count/volume arguments swapped, still coincidentally correct" bug round 2 was built to catch, against the one provider both real incidents actually hit — fixed with a small counting-only interceptor duplicated into that file. None of round 2's three new/updated integration tests seeded a `SmartPlugImportGap`, so a rollback or delete bug specific to gaps under a volume-triggered boundary could go undetected — fixed by seeding one gap per import in all three. The Postgres command-count assertion used a loose `>=` lower bound instead of the exact count the spec's own acceptance criteria call for — fixed with a proven-exact count (any 2 of the 3 seeded "large" imports fit one chunk together, a 3rd never does, so it's always exactly 2 import-chunks regardless of retrieval order); the SqlServer test's new assertion uses the same proof. The measurement-query chunk-size comment reused `DeleteBatchSize` without acknowledging it was chosen for an unrelated reason (cascade volume, not parameter-count safety) — comment corrected to own that explicitly rather than implying derivation. No unit test exercised the count-cap and volume-cap being exceeded by the same addition simultaneously (an OR-vs-AND boundary a future refactor could silently break) — added. **Rejected as duplicate or out of scope:** the TOCTOU (measurement-before-transaction) and int-overflow findings were raised again by round-3 reviewers but are the same already-accepted, already-`deferred-work.md`-tracked gaps from round 1 — no new information changed that assessment; non-uniform-magnitude integration-test data (matching the incident's exact 57k-122k spread, not round 8,000s) was flagged, but the pure unit tests already use the exact real incident numbers for magnitude-sensitive coverage, and the integration tests exist to prove DB wiring at modest scale, not re-prove algorithm correctness at every magnitude combination.

## Design Notes

**Two bounds, two reasons:** `DeleteBatchSize` bounds import *count* per command (parameter-list/row sanity for `BackgroundJobs`, which has no cascade); `DeleteReadingVolumeThreshold` bounds the `SmartPlugReading` `SetNull` cascade a batch of imports triggers. A batch can be small-count-but-huge-cascade (this incident) or the reverse — both bounds apply, whichever is hit first ends a chunk.

**Why 20,000:** the incident saturated Basic-tier ~2 minutes cascading 487,380 rows in one command; 20,000 is ~24x smaller — headroom, not a benchmark. Revisit if insufficient.

**Why the measurement query is chunked too:** it uses the identical `Contains(...)` predicate shape already confirmed (via the original incident's own captured `DbCommand` log) to translate into one SQL parameter per id — unbounded, that risks the same ~2100-parameter ceiling `DeleteBatchSize` itself exists to respect, just on a read instead of a write. Chunking it by `DeleteBatchSize` reuses an already-proven-safe primitive rather than inventing a second threshold.

**Testing approach:** the packing algorithm is pure — thorough unit coverage with synthetic dictionaries, no need to seed tens of thousands of real rows. Integration tests at modest (not literal incident) scale prove the real query+delete wiring, strengthened with a command-count assertion so the test can't pass on a coincidentally-still-correct wiring bug. Like round 1, this can't reproduce a 120s Azure timeout in Testcontainers — the guarantee is the packing invariant (every chunk's cumulative count ≤ threshold by construction), not reproducing the failure.

## Verification

**Commands:**
- `dotnet test --filter-class "*SmartPlugImportRepositoryChunkingTests*"` -- unit tests pass
- `dotnet test --filter-class "*SmartPlugImportRepositoryTests*"` -- all pass
- `dotnet test --filter-class "*SmartPlugImportRepositoryDeleteJobsSqlServerTests*"` -- passes, including the new incident-shaped case
- `dotnet test` -- full suite green
- `dotnet build` (Debug + Release) -- clean

## Suggested Review Order

**The fix itself**

- Entry point: chunked reading-count measurement, then row-volume-aware chunking — start here
  [`SmartPlugImportRepository.cs:831`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L831)
- The packing algorithm — pure, greedy, bounded by both dimensions
  [`SmartPlugImportRepository.cs:778`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L778)
- The new threshold constant and its reasoning
  [`SmartPlugImportRepository.cs:770`](../../src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs#L770)

**Regression proof — the packing algorithm in isolation**

- Proves the count-cap/volume-cap OR-boundary explicitly (a review-round-3 finding)
  [`SmartPlugImportRepositoryChunkingTests.cs:123`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryChunkingTests.cs#L123)

**Regression proof — end-to-end wiring**

- Proves the real GROUP BY + packing + delete pipeline, with an exact command-count assertion, on Postgres
  [`SmartPlugImportRepositoryTests.cs:975`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs#L975)
- Same proof against the real production provider (SqlServer) — this is the coverage round-3 review flagged as missing
  [`SmartPlugImportRepositoryDeleteJobsSqlServerTests.cs:160`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryDeleteJobsSqlServerTests.cs#L160)
- Proves atomicity holds when the chunk boundary is forced by the new volume dimension, not just count
  [`SmartPlugImportRepositoryTests.cs:1229`](../../tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs#L1229)
