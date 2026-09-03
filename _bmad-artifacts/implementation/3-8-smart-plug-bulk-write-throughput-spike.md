---
baseline_commit: c4007b8
---

# Story 3.8: Smart Plug Bulk-Write Throughput Spike

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As the team responsible for shipping AD-23's `BulkInsertOrUpdateAsync` write path safely,
I want to measure `EFCore.BulkExtensions`' real insert/upsert throughput and cancellation/rollback behavior against the project's own real Azure SQL Basic and Postgres databases, at realistic Smart Plug data scale, using dedicated disposable spike-only tables,
so that AD-23's write path is adopted at full scale only once real numbers back the "should be faster" assumption, and the NFR1 Tier-3 time budget `deferred.md` is deliberately waiting on gets set from measured data instead of a guess.

## Context (why this is needed)

AD-23 (adopting `EFCore.BulkExtensions.Core`+`.SqlServer`+`.PostgreSql` 10.0.1's `BulkInsertOrUpdateAsync` to replace `SmartPlugImportRepository.AddAsync`'s existing pre-check/fast-path/per-row-fallback machinery) is already fully specified in `invariants-rules.md`, but its own text names one hard precondition before it may ship broadly: *"a technical spike must run against the existing Azure SQL Basic and Postgres databases, using dedicated, disposable spike-only tables — never the live `SmartPlugReading` schema — to verify write throughput, that cancellation/transactional rollback still satisfies the existing 'no partial row survives cancellation' invariant, **and** that the parent-row/reading-write transactional atomicity above actually holds under a mid-write failure. Until that spike runs, this AD's write path is not to be treated as safe to ship broadly."* This story is that spike. Story 3.9 (the actual AD-22/AD-23 implementation) is explicitly blocked on this story returning a "go" recommendation — see 3.9's own header note.

Three items already logged in `deferred.md` are waiting specifically on this spike's findings, not on guesswork:
- **"NFR1 Tier 3 concrete time budget"** — currently only "fully asynchronous with a completion notification," no number. `deferred.md` states this is "deliberately left open until AD-23's technical spike produces real throughput numbers on Azure SQL Basic to base a budget on." This story's written deliverable must propose that number.
- **"Device swap under the same Power Point name"** — explicitly out of scope for this spike. Not a throughput or write-mechanism concern; nothing here tests or touches it.
- **"AD-23's bulk-write contract is coupled to the current one-row-per-reading storage grain."** — this spike measures throughput against today's one-row-per-reading `SmartPlugReading` shape only. If a future storage-grain rethink ever changes that shape, this spike's numbers (and AD-23's configuration) would need to be re-measured, not just re-pointed at a new table — flag this as a stated limitation of the findings, not something to solve here.

Real-scale sizing this spike's synthetic data is deliberately derived from, not invented:
- **Single-device, full-history scale.** Eve Home's own cumulative export for one Power Point grew from 108,715 rows (2026-06-20) to 114,815 (2026-08-01) to 117,782 (2026-08-22) — reaching back to the plug's first-ever reading on 2023-05-27 (Story 3.4's own verified sample data). That's ~145 rows/day (Jun20→Aug1) and ~141 rows/day (Aug1→Aug22), ~143 rows/day average. Twelve more days to this story's authoring date (2026-09-03) projects to ~119,500 rows — rounded up to **120,000 rows** as this spike's single-device/first-full-import scale.
- **Multi-device household scale.** The live production audit behind Story 3.7 (2026-08-26, household `cef40b7f-3280-4cfe-a73e-b8c349adad06`) measured **467,787** total `SmartPlugReading` rows across that one household's devices before its duplicate cleanup ran. That is the only real, measured "how big does this table actually get" data point this project has — used here as the pre-existing-table scale for insert-under-load and upsert-under-load scenarios, rounded to **470,000 rows**. (Story 3.7's own cleanup later removed 179,324 of those as duplicates, bringing that household's steady-state total to ~288,463 — this spike deliberately tests against the *larger*, pre-cleanup figure as the honest worst-case table size a bulk write must still perform well against, since a newly onboarded multi-device household with several devices' full Eve Home history could independently reach comparable scale — e.g. four devices at ~120,000 rows each ≈ 480,000.)
- **Typical incremental-delta scale.** A household re-importing periodically (Story 3.4's whole premise) writes a small watermark-filtered delta, not a full history, on every repeat import. Eve Home's ~10-minute sampling produces ~144 rows/day per device; a household re-uploading roughly every 2–3 days would submit on the order of ~300–450 new rows per re-import. **500 rows** is used here as a representative, slightly generous incremental-delta batch size.
- **AwaitingPowerPointMapping scale.** AD-20's own text frames this match-key path as protecting a narrow, small window — readings persisted before a Power Point (and thus a watermark) is known. At Eve Home's ~144 rows/day, a full month elapsing before a household gets around to mapping a new device is ~4,320 rows — **5,000 rows** is used here as a generous upper bound for this path's own scale.

## Acceptance Criteria

1. **Given** this spike's own database objects, **when** created, **then** they are dedicated, disposable, clearly-namespaced spike-only tables (e.g. a `Spike_` prefix) created in both the real, already-provisioned Azure SQL Basic database and the real, already-running Postgres database this project uses (not a Testcontainers-spun-up ephemeral instance for either — see Dev Notes) — and no production code path (`SmartPlugImportRepository`, `ISmartPlugParser`, `ProcessSmartPlugImport`, `EnergyTrackerDbContext`, `SmartPlugReadingConfiguration`, or the live `SmartPlugReading`/`SmartPlugImport` tables) is read, written, or modified by this story at all.
2. **Given** the spike tables' shape, **when** designed, **then** they reproduce the specific structural properties that determine `BulkInsertOrUpdateAsync`'s real-world write cost — the same column types/widths as `SmartPlugReading`'s `Id`, `HouseholdId`, `PowerPointId` (nullable), `IntervalStart`, `IntervalEnd`, `KwhValue` (`decimal(18,6)`), plus fixed-width `RoomName`/`PowerPointName`/`DeviceName` strings sized to match real column widths (row byte-width affects bulk-copy throughput) — and both of AD-23's real match-key unique indexes: `(PowerPointId, IntervalStart)` and a partial `(HouseholdId, IntervalStart) WHERE PowerPointId IS NULL`, plus a spike parent table mirroring `SmartPlugImport`'s FK relationship to the reading table, so the measured numbers reflect the actual constraint-checking cost production will pay, not an unconstrained table.
3. **Given** the sizing derived in Context above, **when** synthetic data is generated, **then** it includes (a) a ~120,000-row single-device/first-full-import batch, (b) a ~470,000-row pre-load used as the "already-large table" baseline for scenarios below, (c) a ~500-row typical-incremental-delta batch, and (d) a ~5,000-row `PowerPointId IS NULL` batch — every batch generated with plausible non-degenerate values (varying `IntervalStart`/`KwhValue`, not all-identical rows), never copied from or derived from real household data.
4. **Given** an empty (index-only) spike reading table, **when** `BulkInsertOrUpdateAsync` inserts the ~120,000-row single-device batch, **then** elapsed time and rows/sec are measured and recorded, on both Postgres and SQL Server (Basic tier).
5. **Given** a spike reading table pre-loaded with the ~470,000-row baseline, **when** `BulkInsertOrUpdateAsync` inserts a further ~120,000-row batch for a new, non-colliding Power Point (an index-maintenance-under-load insert scenario), **then** elapsed time and rows/sec are measured and recorded, on both providers.
6. **Given** the same ~470,000-row pre-loaded table, **when** `BulkInsertOrUpdateAsync` is called twice more — once with a batch that is a 100%-overlapping re-submission of previously-inserted rows via `UpdateByProperties = [PowerPointId, IntervalStart]` (the full-history-re-import worst case), and once with the ~500-row typical-incremental-delta batch via the same match key — **then** elaped time and rows/sec are measured and recorded for both, on both providers.
7. **Given** the ~5,000-row `PowerPointId IS NULL` batch, **when** `BulkInsertOrUpdateAsync` is run once as a pure insert and once as a full re-submission via `UpdateByProperties = [HouseholdId, IntervalStart]` (AD-23's second, partial-index match-key configuration), **then** elapsed time and rows/sec are measured and recorded on both providers, and it is explicitly confirmed (not assumed) that this match-key configuration only ever affects/matches rows where `PowerPointId IS NULL` — never a row belonging to a different, already-mapped Power Point that happens to share `(HouseholdId, IntervalStart)`.
8. **Given** a spike parent-row insert and a large (~120,000-row) `BulkInsertOrUpdateAsync` call wrapped together in one explicit `BeginTransactionAsync`/commit (mirroring AD-23's required parent-row atomicity), **when** the operation is cancelled via `CancellationToken` partway through the bulk write (before it would naturally complete), **then** it is verified, on both providers, that (a) zero rows from that batch are visible in the spike reading table afterward, and (b) the spike parent row is also absent — proving the "no partial row survives cancellation" invariant and the explicit-transaction atomicity claim both hold under a real mid-write cancellation, not just in a single-statement `SaveChangesAsync`.
9. **Given** every scenario above has run on both providers, **when** this story concludes, **then** a written result is produced at `_bmad-artifacts/implementation/spike-results/3-8-bulk-write-throughput-spike-results.md` containing: the full throughput table (rows/sec and elapsed time per scenario, per provider), the cancellation/rollback verification outcome, an explicit go/no-go recommendation for AD-23's write path at full scale, and a recommended concrete NFR1 Tier-3 time-budget number derived from the measured numbers (closing the `deferred.md` item — never a number invented ahead of running the spike).
10. **Given** the spike has concluded (whether go or no-go), **when** cleanup runs, **then** every spike-only table/object created in both databases is dropped, verified via a schema query confirming zero `Spike_*` objects remain in either database — this story leaves no trace in either schema.

## Tasks / Subtasks

- [ ] **Task 1: Provision disposable spike tables on both real databases** (AC: #1, #2)
  - [ ] Create a new, git-tracked, throwaway harness project at `spikes/3-8-bulk-write-throughput/` (a plain console app, `dotnet new console`) — **deliberately not added to `EnergyTracker.sln`**, not built/tested by CI (`pr-review.yml` only builds the main solution), and not held to this project's normal production code-quality bar (per-file XML doc comments, architecture-test coverage, etc. do not apply here). This keeps a spike whose whole purpose is exploratory, hand-run, real-network-dependent code from ever accidentally running in CI or gating a PR.
  - [ ] Reference `EFCore.BulkExtensions.Core`, `EFCore.BulkExtensions.SqlServer`, `EFCore.BulkExtensions.PostgreSql` (10.0.1) plus the matching EF Core provider packages **with explicit, self-contained `Version=` attributes in this project's own `.csproj`** — deliberately NOT wired into the main solution's centrally-managed `Directory.Packages.props` (project-context.md's central-package-management rule is for the shipped solution; this harness is throwaway and must not silently bump a shared dependency like `Microsoft.Data.SqlClient` — Story 3.9 makes that upgrade for real, deliberately and verified).
  - [ ] Define two tables per provider: a spike reading table (`Spike_SmartPlugReading` or similar — Id, HouseholdId, PowerPointId (nullable), IntervalStart, IntervalEnd, KwhValue `decimal(18,6)`, RoomName/PowerPointName/DeviceName as fixed-width strings matching `SmartPlugReadingConfiguration.cs`'s real widths) and a spike parent table (`Spike_SmartPlugImport` or similar, minimal — just enough columns for an FK relationship to exist). Create both of AD-23's real unique indexes on the reading table: `(PowerPointId, IntervalStart)` and the partial `(HouseholdId, IntervalStart) WHERE PowerPointId IS NULL` — reuse the exact index-creation SQL already proven in `SmartPlugReadingConfiguration.cs`'s comments and the `20260822165109_AddSmartPlugReadingUniqueIndex`/`20260822165112_...` migrations as the reference for correct syntax on each provider.
  - [ ] Connect to the real, already-provisioned Azure SQL Basic instance and the real, already-running Postgres instance this project uses for self-host/local dev (the actual `docker-compose` Postgres, run persistently for this exercise, not an ephemeral Testcontainers instance spun up and torn down per test run) — see Dev Notes on why Testcontainers is deliberately not used here. Respect AD-19 (secrets via env vars, never committed) and AD-21 (connect to the real Azure SQL instance using whichever auth mode — SQL auth or Entra-only — is actually live at the time this spike runs; check Story 1-11's own status before hardcoding a SQL connection string).

- [ ] **Task 2: Generate realistic synthetic data at the sizing tiers derived above** (AC: #3)
  - [ ] Generate the ~120,000-row single-device batch: one synthetic `PowerPointId`, `IntervalStart` values at ~10-minute intervals (Eve Home's real cadence) reaching back far enough to hit the row count, `KwhValue` randomized within a plausible small-appliance range (never all-identical — a constant-value bulk insert can behave differently under some engines' compression/dedup paths than realistic data).
  - [ ] Generate the ~470,000-row pre-load batch: several distinct synthetic `PowerPointId`s (simulating a multi-device household), interval timestamps spread across a multi-year span (mirroring the real 2023-05-27-to-present range Story 3.4 confirmed).
  - [ ] Generate the ~500-row typical-incremental-delta batch and the ~5,000-row `PowerPointId IS NULL` batch per Context's sizing.
  - [ ] None of this data is derived from or copied out of real household data (`sample-data/eve`/`sample-data/meross` are gitignored personal exports — do not read them here; generate purely synthetic values matching their statistical shape instead).

- [ ] **Task 3: Insert-heavy throughput measurement** (AC: #4, #5)
  - [ ] Time `BulkInsertOrUpdateAsync` inserting the ~120,000-row batch into an empty spike table, both providers.
  - [ ] Time `BulkInsertOrUpdateAsync` inserting a further ~120,000-row batch (new, non-colliding `PowerPointId`) into the table already pre-loaded with the ~470,000-row baseline, both providers — this is the scenario that actually stresses index-maintenance cost under a realistically large existing table, which the empty-table scenario above cannot show.
  - [ ] Record wall-clock elapsed time and computed rows/sec for each run.

- [ ] **Task 4: Update-heavy/upsert throughput measurement, both AD-23 match-key configurations** (AC: #6, #7)
  - [ ] Against the ~470,000-row pre-loaded table: run `BulkInsertOrUpdateAsync` with `UpdateByProperties = [PowerPointId, IntervalStart]` for (a) a 100%-conflicting resubmission of an already-inserted ~120,000-row batch, and (b) the ~500-row typical-delta batch (assume a fraction of it genuinely overlaps stored rows and a fraction is new, mirroring a real incremental re-import) — both providers, both timed.
  - [ ] Against the ~5,000-row `PowerPointId IS NULL` batch: run `BulkInsertOrUpdateAsync` with `UpdateByProperties = [HouseholdId, IntervalStart]` once as a pure insert and once as a full resubmission — both providers, both timed. **Explicitly verify** (not assume) that this match key never matches a row belonging to a different, already-mapped `PowerPointId` sharing the same `(HouseholdId, IntervalStart)` — seed one such row deliberately and confirm it survives untouched. This is the one behavior this spike must nail down empirically, since it's the exact configuration Story 3.9 will need to trust without re-deriving it.

- [ ] **Task 5: Cancellation/transactional-rollback verification** (AC: #8)
  - [ ] Wrap a spike parent-row insert and a ~120,000-row `BulkInsertOrUpdateAsync` call in one explicit `dbContext.Database.BeginTransactionAsync()`/commit, matching AD-23's required parent-row atomicity shape exactly.
  - [ ] Trigger cancellation via a `CancellationTokenSource` timed to fire well before the bulk write would naturally finish (informed by Task 3's own measured elapsed time — e.g. cancel at ~20% of the observed full-run duration). Confirm the library actually surfaces `OperationCanceledException`/`TaskCanceledException` rather than silently completing.
  - [ ] After the cancelled run, query the spike tables directly (a fresh, non-transactional connection) and confirm zero reading rows and zero parent rows exist from that attempt — on both providers. If either provider's `BulkInsertOrUpdateAsync` implementation does **not** honor cancellation cleanly (e.g. `SqlBulkCopy` completing before the token is checked), record this as a genuine, specific finding — do not paper over it.

- [ ] **Task 6: Produce the written go/no-go recommendation + NFR1 Tier-3 number** (AC: #9)
  - [ ] Write `_bmad-artifacts/implementation/spike-results/3-8-bulk-write-throughput-spike-results.md` containing: a throughput table (scenario × provider × elapsed time × rows/sec), the cancellation/rollback finding from Task 5, an explicit go/no-go recommendation for AD-23's write path at full scale (with reasoning — e.g. "go, SQL Server Basic tier sustains N rows/sec even against the 470k-row baseline, well within Tier-3 expectations" or "no-go/go-with-caveats, because X"), and a recommended concrete NFR1 Tier-3 time-budget number (e.g. "recommend NFR1 Tier 3 = the slower of the two providers' measured 120k-row full-import time, rounded up with a safety margin, e.g. N minutes") derived directly from the measured numbers above — never invented ahead of actually running the spike.
  - [ ] Cross-reference this result file from `deferred.md`'s "NFR1 Tier 3 concrete time budget" entry once the number is known (a follow-up doc edit at spike-execution time, not authored speculatively now).

- [ ] **Task 7: Teardown** (AC: #10)
  - [ ] Drop every `Spike_*` table/index/object created in both the Azure SQL Basic and Postgres databases.
  - [ ] Run a schema-introspection query against both (e.g. `information_schema.tables`/`sys.tables` filtered on the spike prefix) and confirm zero rows returned, on both providers, before considering this story done.

## Dev Notes

### Architecture constraints (binding, not optional)

- **AD-23's own spike condition (quoted in Context) is this story's literal scope** — do not expand it into building any part of AD-22/AD-23's actual production implementation (that is entirely Story 3.9's job, gated on this story's own go/no-go verdict).
- **AD-1 (Ports & Adapters) does not bind this story's own harness code** — the spike project is intentionally outside `src/`/the main solution and never touches Domain/Application/Infrastructure. It must still never modify anything under `src/` or `tests/`.
- **AD-19 (secrets)** — the spike's real Azure SQL/Postgres connection strings come from env vars only, exactly like every other environment-supplied secret in this project; never commit a real connection string or password into `spikes/3-8-bulk-write-throughput/`.
- **AD-21 (Azure SQL Entra-only auth)** — check Story `1-11-azure-sql-entra-id-only-authentication`'s own status before connecting: if it has shipped and Deploy B has flipped `azureADOnlyAuthenticationEnabled`, this spike's SQL Server connection must use `Authentication=Active Directory Default`/`Active Directory Managed Identity` exactly like production does, not a SQL login that may no longer work. If it hasn't shipped yet, SQL auth still works. Either way, this spike's own Entra/service identity (if applicable) needs at minimum the same `db_ddladmin` role Story `1-11`'s `grant-entra-db-users.sql` already grants the CI migration identity, since this story creates and drops real tables.

### Why real databases, not Testcontainers

This project's normal integration-test discipline (project-context.md) is Testcontainers everywhere — real Postgres/SQL Server containers, spun up fresh per test run. That is deliberately **wrong** for this specific spike: a locally-spun SQL Server container has none of Azure SQL Basic tier's 5-DTU throttling, and no real network latency exists between a Testcontainers container and the test process. The entire point of this spike is to measure what the *actual, DTU-capped, network-real* environment does under a bulk write — a Testcontainers run would produce numbers that look good and mean nothing. This is a deliberate, one-time, named exception to the project's Testcontainers convention, not a precedent to reuse elsewhere.

### A real, load-bearing caution: this shares the live Basic-tier (5 DTU) SQL Server with production

Story 3.7's own investigation found simple point queries fast on this tier but full-table joins over ~467k rows took several minutes — and there is (per AD-13/AD-15's personal/household scale) almost certainly only **one** Azure SQL Server instance for this project, the same one the live household's real traffic runs against. A heavy bulk-write spike run during genuine household usage could visibly degrade the live app for whoever's using it at the time. Run this spike's Azure-SQL-side scenarios at a time no real usage is expected, and drop the spike tables (Task 7) immediately after each run rather than leaving them present between scenarios longer than necessary. Whether Ralf wants a temporary DTU tier bump for the duration of this spike is a real open question — flagged below, not decided here.

### Existing code to reference (read, do not import into the spike)

- `src/EnergyTracker.Infrastructure/Configurations/SmartPlugReadingConfiguration.cs` — exact column types/widths/index syntax the spike tables mirror.
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260822165109_AddSmartPlugReadingUniqueIndex.cs` / `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260822165112_AddSmartPlugReadingUniqueIndex.cs` — exact per-provider raw-SQL idiom for the two real unique indexes this spike reproduces.
- `invariants-rules.md`'s AD-23 section — the exact `BulkConfig`/`UpdateByProperties`/`PropertiesToExclude` shape Story 3.9 will need; this spike's harness code should exercise the same configuration shape (even though the harness itself is throwaway) so its measurements are actually representative of what 3.9 will call.

### Known non-goals (avoid scope creep)

- **Does not implement any part of AD-22 or AD-23 in production code.** No `src/` file changes at all.
- **Does not address the device-swap gap** (`deferred.md`) — unrelated to write throughput.
- **Does not address the storage-grain dependency** (`deferred.md`) beyond stating, in the written results, that these numbers are only valid for today's one-row-per-reading grain.
- **Not held to this project's normal code-quality bar** — no architecture-test coverage, no XML doc comments, no requirement to keep the harness code long-term. It may be deleted after the results file is committed, at the implementer's discretion, since the results document (not the harness code) is the durable deliverable.

### Open question for Ralf (genuinely needs a human call, not a recommended default)

- Whether to request a temporary Azure SQL tier bump (above Basic/5-DTU) for the duration of this spike's Azure-SQL-side runs, to reduce the risk of degrading live household traffic while the spike executes, and whether there's a preferred low-usage window to run it in. Everything else in this story is specified precisely enough to proceed without further confirmation.

### Project Structure Notes

- New, git-tracked, non-solution files: `spikes/3-8-bulk-write-throughput/` (harness console app + its own `.csproj`), `_bmad-artifacts/implementation/spike-results/3-8-bulk-write-throughput-spike-results.md` (the durable deliverable).
- No files under `src/`, `tests/`, or `web/` are touched by this story.
- `deferred.md`'s "NFR1 Tier 3 concrete time budget" entry gets a follow-up cross-reference to the new results file once the spike has actually run (not a speculative edit now).

### Testing standards summary

- This is a spike, not a feature — there is no xUnit/Shouldly/NSubstitute test suite to write. Throughput scenarios are measured (`Stopwatch`, printed/logged output feeding the written results doc), not asserted pass/fail. The cancellation/rollback scenario (Task 5) **is** a pass/fail check (zero rows survive) and should fail loudly (a thrown exception or explicit console failure line) if it doesn't hold — this is the one place correctness, not just a number, matters.
- No CI involvement — this harness requires live network access to real cloud/self-host infrastructure and is never run automatically.

### References

- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-23`] — the exact spike condition this story satisfies, and the `BulkInsertOrUpdateAsync`/`UpdateByProperties`/`PropertiesToExclude` configuration shape this spike's harness must exercise.
- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-20`] — the two real unique indexes (`(PowerPointId, IntervalStart)` and the partial `(HouseholdId, IntervalStart) WHERE PowerPointId IS NULL`) this spike's tables reproduce.
- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-21`] — Azure SQL Entra-only auth mode this spike must connect using, whichever is live at execution time.
- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/deferred.md`] — the three deferred items (NFR1 Tier-3 budget, device-swap gap, storage-grain dependency) this spike's findings feed or explicitly do not address.
- [Source: `_bmad-artifacts/implementation/3-7-smart-plug-reading-duplicate-cleanup-on-late-mapping.md`] — the 2026-08-26 production audit (467,787 total rows, 179,324 confirmed duplicates) this spike's household-scale sizing is derived from, and the Basic-tier full-table-join performance caution it already surfaced.
- [Source: `_bmad-artifacts/implementation/3-4-incremental-smart-plug-import.md`] — the three dated Eve Home HiFi sample row counts (108,715 / 114,815 / 117,782) this spike's single-device sizing is derived from.
- [Source: `src/EnergyTracker.Infrastructure/Configurations/SmartPlugReadingConfiguration.cs`, `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs`] — exact real column shape/index syntax/existing conflict-handling machinery this spike's tables mirror and whose replacement (Story 3.9) this spike is gating.
- [Source: `_bmad-artifacts/project-context.md`] — Testcontainers-by-default testing convention this spike deliberately, explicitly departs from, and why.

## Dev Agent Record

### Agent Model Used

### Debug Log References

### Completion Notes List

### File List
