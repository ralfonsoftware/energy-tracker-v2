# Story 3.8: Smart Plug Bulk-Write Throughput Spike — Results

**Date:** 2026-09-04
**Run by:** Ralf, via `spikes/3-8-bulk-write-throughput/` (see that directory's `README.md` for
harness details, commands, and a running "Findings log" written during development)
**Databases used:** the project's real, already-provisioned Postgres (self-host/local dev) and the
real, already-provisioned Azure SQL Basic (5 DTU) instance (`energytracker-prod-qvc6vfmtp5-sql`,
database `energytracker`) — never a Testcontainers instance, never the live `SmartPlugReading`
schema. All spike tables (`Spike_SmartPlugReading`/`Spike_SmartPlugImport`) were dropped after the
run; zero `Spike_*` objects remain in either database (AC #10, confirmed).

## Throughput table

| Scenario | Provider | Rows | Elapsed | Rows/sec |
|---|---|---:|---:|---:|
| AC #4 — insert into empty table | Postgres | 120,000 | 1.94 s | 61,904 |
| AC #4 — insert into empty table | SQL Server (Basic) | 120,000 | 262.12 s (4.37 min) | 458 |
| *(preload, not itself an AC)* | Postgres | 470,000 | 6.98 s | 67,354 |
| *(preload, not itself an AC)* | SQL Server (Basic) | 470,000 | 1,149.11 s (19.15 min) | 409 |
| AC #5 — insert 120k into 470k-preloaded table | Postgres | 120,000 | 1.89 s | 63,392 |
| AC #5 — insert 120k into 470k-preloaded table | SQL Server (Basic) | 120,000 | 459.13 s (7.65 min) | 261 |
| AC #6a — resubmit full-overlap 120k (`[PowerPointId, IntervalStart]`) | Postgres | 120,000 | 2.19 s | 54,768 |
| AC #6a — resubmit full-overlap 120k (`[PowerPointId, IntervalStart]`) | SQL Server (Basic) | 120,000 | 167.18 s (2.79 min) | 718 |
| AC #6b — resubmit incremental delta (`[PowerPointId, IntervalStart]`) | Postgres | 500 | 0.29 s | 1,728 |
| AC #6b — resubmit incremental delta (`[PowerPointId, IntervalStart]`) | SQL Server (Basic) | 500 | 10.17 s | 49 |
| AC #7 — insert 5,000 `PowerPointId IS NULL` | Postgres | 5,000 | 0.09 s | 52,706 |
| AC #7 — insert 5,000 `PowerPointId IS NULL` | SQL Server (Basic) | 5,000 | 11.03 s | 453 |
| AC #7 — resubmit via `[HouseholdId, IntervalStart]` | Postgres | — | — | **THREW — see Finding #2** |
| AC #7 — resubmit via `[HouseholdId, IntervalStart]` | SQL Server (Basic) | — | — | **THREW — see Finding #2** |
| AC #8 — cancelled mid-write (120k batch, transactional) | Postgres | 120,000 attempted | cancelled at 0.39 s | n/a — cancelled, not completed |
| AC #8 — cancelled mid-write (120k batch, transactional) | SQL Server (Basic) | 120,000 attempted | cancelled at 52.42 s | n/a — cancelled, not completed |

AC #8's "rows/sec" is not a completion rate — the whole point of that scenario is that it never
completes; both figures are how far each provider's cancellation-detection got before honoring the
token, not a sustained-throughput number, and are omitted above accordingly.

**Provider gap:** SQL Server Basic tier ran the sanctioned `[PowerPointId, IntervalStart]` path at
roughly 85–135× slower than Postgres across AC #4/#5/#6a (458 vs 61,904; 261 vs 63,392; 718 vs
54,768 rows/sec) — a large, real gap, not a rounding artifact, and consistent with Story 3.7's own
prior finding that this Basic tier struggles under sustained load (multi-minute full-table joins
over ~467k rows).

## Cancellation / transactional-rollback finding (AC #8)

**PASSED on both providers.** A spike parent-row insert plus a 120,000-row
`BulkInsertOrUpdateAsync` call, wrapped in one explicit `BeginTransactionAsync`/commit (mirroring
AD-23's required parent-row atomicity), was cancelled via `CancellationToken` partway through the
bulk write on both providers:

- **Postgres**: cancelled at 387 ms. `OperationCanceledException` observed and surfaced correctly.
  A fresh, non-transactional connection confirmed **zero** reading rows and **zero** parent rows
  survived.
- **SQL Server (Basic)**: cancelled at 52.4 s (20% of AC #4's own measured elapsed time on this
  provider — deliberately scaled per-provider by the harness, not a fixed value). Same result:
  `OperationCanceledException` observed, zero reading rows, zero parent row survived.

The "no partial row survives cancellation" invariant and the parent-row/reading-write explicit-
transaction atomicity claim both hold under a real mid-write cancellation, on both providers, even
under Azure SQL Basic tier's severe throttling.

## Findings (both confirmed against real infrastructure, not just local smoke-testing)

**Finding #1 — AD-23's specified `PropertiesToExclude = [Id]` does not work for this entity
shape; use `PropertiesToExcludeOnUpdate` instead.**
`SmartPlugReading.Id` is a client-generated `Guid` (`Guid.NewGuid()`), not a DB-generated/IDENTITY
column — no `DEFAULT`/`IDENTITY` exists in either provider's real migrations. Per
`EFCore.BulkExtensions.Core` 10.0.1's own shipped XML doc comments, the blanket
`PropertiesToExclude` omits a column from **both** insert and update; only
`PropertiesToExcludeOnUpdate` is insert-safe. Using `PropertiesToExclude=["Id"]` literally throws a
NOT NULL violation on `Id` for a genuinely-new row. **Recommend Story 3.9 use
`PropertiesToExcludeOnUpdate = ["Id"]`, not the blanket `PropertiesToExclude`, when it implements
AD-23's write path** — this harness's own `BaseConfig()` uses the corrected form throughout.

**Finding #2 — the `AwaitingPowerPointMapping` match-key configuration
(`UpdateByProperties = [HouseholdId, IntervalStart]`) does not work as AD-23 assumes, on either
provider, once the table holds realistic multi-device data.**
AC #7's resubmission (a full re-submission of the 5,000-row `PowerPointId IS NULL` batch via the
second AD-23 match-key configuration) threw on **both** real databases:

- **Postgres**: `23505: could not create unique index "tempUniqueIndex_Spike_SmartPlugReading_
  HouseholdId_IntervalStar..."` — `EFCore.BulkExtensions.PostgreSql` appears to build its own
  helper unique index scoped to exactly `(HouseholdId, IntervalStart)`, **without** the
  `WHERE PowerPointId IS NULL` predicate the real partial index carries, which then collides with
  the pre-existing non-null-`PowerPointId` rows sharing that pair (structurally expected: multiple
  devices in one household naturally share timestamps at Eve Home's ~10-minute cadence).
- **SQL Server (Basic)**: a *different* failure signature — `Cannot insert duplicate key row ...
  unique index 'IX_Spike_SmartPlugReading_HouseholdId_IntervalStart_WhenPowerPointIdNull'` — thrown
  during the resubmission of a batch whose own 5,000 rows had no internal duplicates and had
  already been inserted successfully moments earlier. This looks like the MERGE's match condition
  also not being scoped to `PowerPointId IS NULL`, misclassifying at least one row as NOT MATCHED
  against a pre-existing mapped row sharing the same `(HouseholdId, IntervalStart)`.

This is exactly the empirical question Task 4/AC #7 asked this spike to resolve, and the answer —
now confirmed on real infrastructure on both providers, not just a throwaway container — is: **this
specific match-key configuration, as directly supported by `EFCore.BulkExtensions` 10.0.1's
`UpdateByProperties`, is not safe to ship as AD-23 currently specifies it.** It never got the
chance to prove or disprove the "does it ever silently corrupt an unrelated mapped row" concern
Task 4 asked to nail down empirically — instead it fails outright before any row is touched, which
is a *better* failure mode than silent corruption, but still means this exact configuration cannot
be adopted as-is.

**A known limitation of both findings**: this spike measures the new `BulkInsertOrUpdateAsync`
path in isolation — it does not re-run the existing `AddAsync`/per-row-fallback mechanism
side-by-side for a direct throughput comparison (out of scope per AC #1 — production code and
tables were never touched). The go/no-go call below is therefore based on the new path's absolute
throughput and correctness, not a measured delta against the mechanism it would replace.

## Go/no-go recommendation

**Go, with a required amendment — split AD-23's two match-key configurations into different
outcomes:**

1. **Go, for the primary `[PowerPointId, IntervalStart]` match-key path** (AC #4/#5/#6 — a
   known/mapped Power Point, the common case for both the one-time full-history import and every
   routine incremental re-import). Correct and safe on both providers; the cancellation/rollback
   guarantee holds. Throughput is excellent on Postgres and markedly slow on Azure SQL Basic tier,
   but the project's existing fully-asynchronous background-job design (AD-6 — client polls
   `GET /api/jobs/{id}`, never blocks on the write) already absorbs exactly this kind of latency;
   nothing here demands a faster synchronous path. Apply Finding #1's
   `PropertiesToExcludeOnUpdate` correction before shipping.

2. **No-go, for the `AwaitingPowerPointMapping` match-key path
   (`[HouseholdId, IntervalStart]`) as currently specified** (AC #7). Story 3.9 needs a different
   implementation for this narrow path before it can adopt `BulkInsertOrUpdateAsync` there —
   options worth considering (not decided here, this spike's job was to surface the gap, not
   design its fix): keep the existing per-row-fallback mechanism for this one path specifically
   (AD-20's own text already frames it as a narrow, small window — Context's own sizing caps it at
   ~5,000 rows, so the throughput case for bulk-writing it is weak regardless), or a hand-written
   raw-SQL upsert whose `WHERE PowerPointId IS NULL` predicate is made explicit in the SQL itself
   rather than relying on `EFCore.BulkExtensions` to infer it from `UpdateByProperties`.

## Recommended NFR1 Tier-3 time budget

**15 minutes**, closing `deferred.md`'s "NFR1 Tier 3 concrete time budget" item.

Derivation: the slower provider's (Azure SQL Basic) worst measured single-operation time on the
**sanctioned** path is AC #5 — 459.1 s (≈7.65 min), a 120,000-row full-device-history import
landing on an already-large (~590,000-row) table, the realistic shape of a multi-device
household's later device onboarding. Rounding up and applying roughly a 2× safety margin — this
spike ran in isolation with no concurrent household traffic contending for the same 5 DTU, and a
real import will not always get that — gives 15 minutes. This comfortably covers every measured
sanctioned-path scenario in the table above, including the worse `preload` figure (19.15 min) which
itself is not a sanctioned single-operation scenario (it is only this spike's own baseline-loading
step, simulating an already-large table, not a real single import).

## Cross-references

- `deferred.md`'s "NFR1 Tier 3 concrete time budget" entry: update to point at this file and this
  section's 15-minute recommendation.
- Story 3.9 (`3-9-watermark-correction-detection-and-bulk-write-adoption`): read Finding #1 and
  Finding #2 above before implementing AD-23's write path — Finding #1 is a direct, drop-in
  correction (`PropertiesToExcludeOnUpdate`, not `PropertiesToExclude`); Finding #2 blocks adopting
  `UpdateByProperties=[HouseholdId, IntervalStart]` as specified and needs its own design decision
  before that path can be bulk-written at all.
