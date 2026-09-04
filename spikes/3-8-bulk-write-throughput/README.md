# Story 3-8: Smart Plug Bulk-Write Throughput Spike — Harness

Throwaway, hand-run console app. Not part of `EnergyTracker.sln`, not built/tested by CI, not held
to this project's normal code-quality bar (see the story's own Dev Notes and Task 1). Its purpose
is to produce the measured numbers `_bmad-artifacts/implementation/spike-results/
3-8-bulk-write-throughput-spike-results.md` needs — that results doc, not this code, is the
durable deliverable.

## Before you run anything against the real Azure SQL Basic instance

Read the story's own Dev Notes section **"A real, load-bearing caution: this shares the live
Basic-tier (5 DTU) SQL Server with production"** and the **"Open question for Ralf"** section.
This harness will run heavy bulk writes against the same database instance your household's real
traffic uses. Decide first:

- Whether to request a temporary DTU tier bump for the duration of the Azure-SQL-side runs.
- What low-usage time window to run them in.

Nothing below is safe to run against Azure SQL until you've made that call. The Postgres-side
commands are safe to run any time (a local dev instance you already run persistently).

## Setup

```bash
cd spikes/3-8-bulk-write-throughput
dotnet restore
dotnet build
```

Set these two env vars before every command:

```bash
export SPIKE_PROVIDER=postgres        # or: sqlserver
export SPIKE_CONNECTION_STRING="..."  # see below
```

**Postgres**: point at the project's own persistent local Postgres (the real `docker-compose`
instance, not Testcontainers — see the story's Dev Notes on why). Something like:

```
Host=localhost;Port=5432;Username=energytracker;Password=<...>;Database=energytracker_spike
```

Use a **separate database** from the app's own `energytracker` database if convenient (still the
same Postgres server/instance) — not required (tables are namespaced `Spike_*` and fully torn
down), just tidier.

**Azure SQL**: per AD-21, check Story `1-11-azure-sql-entra-id-only-authentication`'s status first
— **that story is now `done`** as of this writing, so the live database should already be
AAD-only. Your connection string needs `Authentication=Active Directory Default` (or `...Managed
Identity`, matching whatever identity you run this harness as) rather than a SQL login/password.
Whatever identity you connect as needs at least the `db_ddladmin` role (Story 1-11's
`grant-entra-db-users.sql` already grants this to the CI migration identity — this harness creates
and drops real tables, same requirement).

## Commands

Run `dotnet run -- <command>` (env vars as above). `--cancel-after-ms N` is accepted by `ac8` and
`run-all` to override the cancellation timing.

| Command | What it does |
|---|---|
| `setup` | Creates `Spike_SmartPlugImport` + `Spike_SmartPlugReading` and both AD-23/AD-20 unique indexes. |
| `teardown` | Drops both spike tables, then runs the AC #10 schema-introspection check. |
| `verify-clean` | Just the AC #10 check, no drop. |
| `truncate` | Empties `Spike_SmartPlugReading` (keeps schema). |
| `ac4` | AC #4 — insert 120k rows into an empty table. |
| `preload` | Loads the ~470k baseline (not itself an AC — a precondition for AC #5/#6). |
| `ac5` | AC #5 — insert a further 120k rows (new PowerPointId) into the preloaded table. Persists the batch's PowerPointId + a timestamp sample to `spike-state.json` for a later standalone `ac6`. |
| `ac6` | AC #6a + #6b — resubmit AC #5's batch (full overlap) and a 500-row incremental delta. Reads `spike-state.json` — run `ac5` first (same process run, or a prior one). |
| `ac7` | AC #7 — 5,000-row `PowerPointId IS NULL` batch: insert, then resubmit via the `[HouseholdId, IntervalStart]` match key, then the isolation check against a deliberately-seeded already-mapped row. **See "Findings log" below — this one already failed in local smoke-testing on both providers.** |
| `ac8` | AC #8 — parent-row + 120k bulk insert in one explicit transaction, cancelled partway through; verifies zero rows/zero parent row survive. |
| `run-all` | Runs `setup` → `ac4` → `truncate` → `preload` → `ac5` → `ac6a`/`ac6b` → `ac7` → `truncate` → `ac8` → `teardown` → `verify-clean`, in one process. Each scenario is isolated — one throwing doesn't stop the rest, and **teardown always runs, even on failure** (`finally` block), so this story never leaves `Spike_*` tables behind regardless of outcome. |

Run `run-all` once per provider (`postgres`, then `sqlserver`) for the full data set. For the
Azure SQL side, once your DTU/timing decision is made, you can instead run the individual
commands (`setup`, `ac4`, `truncate`, `preload`, `ac5`, `ac6`, `ac7`, `ac8`, `teardown`)
one at a time with pauses between them, dropping the tables (`teardown`) between sessions if you
want zero footprint between runs — Dev Notes' caution about not leaving spike tables present
longer than necessary.

All measured results are appended to `results-log.csv` (gitignored, local-only) — timestamp,
provider, scenario, row count, elapsed ms, rows/sec. Run against both providers, then hand that
file (or its contents) back so Task 6's results doc
(`_bmad-artifacts/implementation/spike-results/3-8-bulk-write-throughput-spike-results.md`) can be
written from real numbers.

## Design notes / assumptions made without a real value to mirror

- **Fixed-width string columns**: production's `SmartPlugReadingConfiguration.cs` declares
  `RoomName`/`PowerPointName`/`DeviceName` as `nvarchar(max)`/`text` — no declared max width to
  literally mirror. This harness instead uses true fixed-width `nchar`/`char` columns
  (`RoomNameLength=20`, `PowerPointNameLength=30`, `DeviceNameLength=40` in `DataGenerator.cs`) —
  representative real-world upper bounds, not a measured value. If this feels wrong, it's an easy
  constant to change before running.
- **Synthetic data determinism**: every batch uses a fixed RNG seed and a fixed anchor timestamp
  (`Scenarios.AnchorEnd`, hardcoded to 2026-09-03) rather than `DateTimeOffset.UtcNow`, so
  re-running produces byte-identical data across providers (a fair comparison) and across
  separate `ac5`/`ac6` process invocations.
- **`BaseConfig()` uses `PropertiesToExcludeOnUpdate = ["Id"]`, not the blanket
  `PropertiesToExclude` AD-23's text names** — see "Findings log" below, first entry. This is a
  deliberate correction, verified empirically, not an oversight.

## Findings log (from local smoke-testing — NOT the real spike numbers)

Everything below was found running this harness against a **throwaway local Postgres/SQL Server
Docker container** (validating the harness code itself works end-to-end), not the project's real
Postgres or Azure SQL Basic instance. Throughput numbers from that smoke test are **not** valid
inputs to Task 6's results doc — only real numbers from the actual target databases are. The two
*behavioral* findings below, however, are almost certainly real (they're about library semantics,
not about which machine is running):

1. **AD-23's literal `PropertiesToExclude = [Id]` doesn't work for this entity shape.**
   `SmartPlugReading.Id` is a client-generated `Guid` (`Guid.NewGuid()`), not a DB-generated/
   IDENTITY column — no `DEFAULT`/`IDENTITY` exists in either provider's real migrations. Per
   `EFCore.BulkExtensions.Core` 10.0.1's own shipped XML doc comments, `PropertiesToExclude`
   omits a column from **both** insert and update, unlike `PropertiesToExcludeOnUpdate` ("can
   differ from `PropertiesToExclude` that can be used for Insert config only"). Using the blanket
   `PropertiesToExclude=["Id"]` throws a NOT NULL violation on `Id` for a genuinely-new row.
   `PropertiesToExcludeOnUpdate=["Id"]` is the correct config for AD-23's actual intent (never let
   an UPDATE overwrite a matched row's own `Id`) — this harness's `Scenarios.BaseConfig()` already
   uses it. **Recommend Story 3.9 use `PropertiesToExcludeOnUpdate`, not `PropertiesToExclude`, for
   this reason** — flag this to whoever picks up 3.9, and re-verify against the real target DBs.

2. **The `UpdateByProperties = [HouseholdId, IntervalStart]` match key (AC #7's second
   configuration) failed on both providers** once the table already contained other rows sharing
   that column pair via a *different, non-null* `PowerPointId` (structurally expected: multiple
   devices in one household sampling on the same ~10-minute cadence naturally share timestamps).
   - **Postgres**: `BulkInsertOrUpdateAsync` threw `23505: could not create unique index
     "tempUniqueIndex_..._HouseholdId_IntervalStar..."` — the library appears to build its own
     helper unique index scoped to exactly `(HouseholdId, IntervalStart)`, **without** the
     `WHERE PowerPointId IS NULL` predicate the real partial index carries, which then collides
     with pre-existing non-null-`PowerPointId` rows sharing that pair.
   - **SQL Server**: a *different* failure — `Cannot insert duplicate key row ... unique index
     'IX_..._HouseholdId_IntervalStart_WhenPowerPointIdNull'` — thrown during the resubmission of
     a batch whose own 5,000 rows had no internal duplicates and had already been inserted
     successfully moments earlier. This looks like the MERGE's match condition not being scoped to
     `PowerPointId IS NULL` either, causing at least one row to be misclassified as NOT MATCHED.
   - **This is exactly the empirical question Task 4/AC #7 asks the spike to answer, and the
     answer (at least in local smoke-testing) is: this match-key configuration, as directly
     supported by `EFCore.BulkExtensions` 10.0.1's `UpdateByProperties`, does not appear to
     reliably respect a partial/filtered unique index on either provider once the table holds
     realistic multi-device data.** This is a significant, go/no-go-relevant finding for AD-23 —
     treat it as a strong candidate for "no-go" or "go-with-caveats" on the `AwaitingPowerPointMapping`
     path specifically (the fully-mapped `[PowerPointId, IntervalStart]` path in AC #4–#6 had no
     analogous failure in smoke-testing). **Re-verify against the real target databases before
     writing Task 6's recommendation** — this needs confirming on real infrastructure, not just a
     throwaway local container, before it's trusted as the spike's actual conclusion.

Re-run `ac7` (or `run-all`) against the real Postgres and Azure SQL databases and update this
section (or transcribe straight into the results doc) with what actually happens there.

## Update (2026-09-04) — real Postgres run confirms finding #2; Azure SQL run needs a retry

Ralf ran `run-all` against the real project Postgres and the real Azure SQL Basic instance.

**Postgres: full real numbers, and finding #2 above is now CONFIRMED, not just smoke-tested.**
AC #4 = 120,000 rows / 1,938.5 ms (61,904 rows/sec); AC #5 = 120,000 rows / 1,893.0 ms (63,392
rows/sec); AC #6a = 120,000 rows / 2,191.1 ms (54,768 rows/sec); AC #6b = 500 rows / 289.4 ms
(1,728 rows/sec); AC #7 insert = 5,000 rows / 94.9 ms, then the exact same `23505` unique-index
error as smoke-testing on the resubmission; AC #8 = PASSED (cancelled at 387 ms, zero rows/zero
parent survived).

**Azure SQL: every scenario hit `BulkCopyTimeout`'s default (30 seconds) and threw "Execution
Timeout Expired."** This is itself a real, load-bearing finding: Basic tier's 5 DTU could not
complete even the first (empty-table) 120k-row insert inside the library's out-of-the-box timeout.
Two fixes went in as a result (both re-validated via a fresh local SQL Server container, not yet
against the real Azure SQL instance):

- `Scenarios.BaseConfig()` now sets `BulkCopyTimeout = 0` (no limit — see the code comment there).
- `SpikeDbContext.OnConfiguring` now sets `CommandTimeout(1800)` for the context's own non-bulk
  commands (schema DDL, truncate, parent-row inserts), which can also be slow on Basic tier.

A **separate harness bug** also surfaced during this: `RunAllAsync`'s old `RunStep<T>` helper
returned a bare `T?` from an unconstrained generic method and relied on `default`/pattern-matching
to detect "this step failed" — that silently does not behave as "no value" for a value-type `T`.
After AC #5's real timeout, AC #6a still ran against a zeroed-out/null batch and threw its own
unrelated `ArgumentNullException`, momentarily masking the real timeout behind a second, confusing
failure. Fixed with an explicit `StepResult<T>` class carrying a `Success` flag (`Program.cs`).

**If you're retrying the SQL Server side**: expect scenarios to now take meaningfully longer than
30 seconds (no longer capped) rather than fail fast — each one holds the shared production DTU
budget for its own duration. Given how badly Basic tier struggled here, it's worth revisiting the
DTU-tier-bump/timing question from the top of this README before running again, and worth running
scenario-by-scenario (`setup` → `ac4` → `truncate` → `preload` → `ac5` → `ac6` → `ac7` → `ac8` →
`teardown`) rather than `run-all`, so a slow scenario doesn't block visibility into the rest.
