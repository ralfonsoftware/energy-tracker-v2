---
baseline_commit: a297df5bd71580a304c7827a94826ee938afa4e8
---

# Story 11.24: Migration Timeout Headroom

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- Epic 11 "Codebase audit hardening" (planning/epics/epic-11-codebase-audit-hardening.md), tier "Triage follow-ups", promoted from deferred-work.md (Story 11.4 dev-story entry; index row line ~95, body line ~514). Origin: Story 11.4 deploy N, run 38147146530, "Apply pending EF Core migrations" took about 529 s on Azure SQL Basic (5 DTU) against `Command Timeout=600`. Architecture: AD-2 (dual-provider persistence, migrations in both projects), AD-13 (one image for Azure and self-host), Story 10.1 (expand/contract, restore point). This file cites the AD IDs, which satisfies the spec-per-fix gate (project-context.md). -->
<!-- Branch: feature/11-24-migration-timeout (current checkout, clean, at a297df5). ONE PR. No migration, no new NuGet or npm package, no frontend change, no `src/**` change expected (workflow, guard test, docs, project-context only). -->
<!-- TWO GATES BEFORE CODE, in this order: (1) Task 1 baseline (current `SmartPlugReadings` size and the measured rate from run 38147146530), recorded in this file; (2) Ask First on the option, chosen from the Decision table once the baseline is in. The table's recommendation is a hypothesis that Task 1 may overturn. -->
<!-- ASK FIRST DECIDED 2026-11-03 (Ralf): T1 + T2 rule. Command Timeout 600 -> 1800 s, step timeout-minutes 20 -> 60; project-context rule: estimate = rows / baseline rate (about 350 rows/s on Basic 5 DTU), at more than about half the ceiling (900 s) batch the change (T2) or take the manual off-peak/scale-up path (T3 documented, not automated). AC #5: stop retrying on `Execution Timeout Expired` (output captured to a file, exit 1 with ::error::). AC #6: document only (docs/self-hosting.md line), no code change. Rejected: T2 only (no headroom for the next ordinary rewrite), T3 automation (new deploy-identity rights, out of scope), attempt cap / 5-attempt sizing (a timeout is not a firewall problem). -->
<!-- No Auth0/Chrome live gate: no auth flow, claim or browser behaviour changes. The production size query (Task 1.2) and the next real deploy (Task 7) are Ralf-gated: they touch his Azure subscription. -->

## Story

As an operator,
I want deploys with a long column rewrite not to hit the migration timeout,
so that a schema change on `SmartPlugReadings` cannot fail a release (promoted from `deferred-work.md`, Story 11.4 dev-story entry).

## Acceptance Criteria

1. **Baseline first (no workflow edit before this).** **Given** Story 11.4's deploy took about 529 s against `Command Timeout=600` (run 38147146530), **when** this story starts, **then** the current row count and reserved size of `SmartPlugReadings` on the production database are read (read-only query, Task 1.2) and recorded in this file and in `project-context.md` as the dated baseline for the next estimate, together with the rate derived from run 38147146530 (rows per second, labelled as an upper bound on step time, see Dev Notes "What 529 s measured").
2. **Ask First (epic AC 1).** **Given** the baseline and the options in Dev Notes "Decision table", **when** Task 1 is recorded, **then** Ralf picks one (raise the timeout, batch the change, or a documented off-peak path, alone or combined), and the choice, date and rejected options are recorded in the header comment and Completion Notes. No `app-deploy.yml` edit before that.
3. **Rule recorded (epic AC 1).** **Given** the chosen option, **when** it ships, **then** `project-context.md` "Migrations" holds, directly next to the expand/contract bullet, one bullet stating: the timeout value and why, the rule for planning a long rewrite (how to estimate from row count and the baseline rate, at what estimate the change must be batched or moved to the documented path), and where the baseline lives.
4. **Workflow and guard test reflect it (epic AC 2).** **Given** the chosen option, **when** it ships, **then** `.github/workflows/app-deploy.yml` carries the decided values and a new guard test fails when they drift. The guard pins at least: `Command Timeout=<N>;` in the connection string of "Apply pending EF Core migrations"; the step's `timeout-minutes`; and the relation between them (Dev Notes "Timeout arithmetic"). A known-bad variant (workflow text with `Command Timeout` removed, lowered below the pinned value, or the step cap below the arithmetic floor) fails the same predicate, so the guard cannot pass vacuously.
5. **Implied (the retry loop must not turn one timeout into five).** The step retries `dotnet ef database update` up to 5 times for firewall propagation. A migration that times out is not a firewall problem; with a longer timeout, five attempts can no longer fit in the step cap, and the step is cancelled mid-rollback. The decision names how a command timeout is told apart from a connection failure (or states why the current retry stays and what the step cap then is), and the guard pins it.
6. **Implied (self-host path, AD-13).** The self-host `migrate` service (EF bundle, Npgsql) has no `Command Timeout` in `ConnectionStrings__Default` (`docker-compose.yml`), so Npgsql's default of 30 s applies to each migration command. The story records whether that is acceptable for self-host sizes (one household, small tables) in Completion Notes and, if the answer is "document", adds a line to `docs/self-hosting.md` ("Database migrations") telling the operator how to raise it. No code change to the bundle unless Ralf asks.
7. **Implied (nothing else changes).** The restore-point block, preflight, firewall steps and the 5-attempt retry shape stay as `MigrationDeployWorkflowRestorePointGuardTests`, `WorkflowPreflightGuardTests` and `AppDeployWorkflowJobsGuardTests` pin them; those three stay green with no edit.
8. **Implied (project memory and deferred-work).** `project-context.md` gets the AC #3 bullet plus a `Last Updated` line; the two `deferred-work.md` entries (index row ~95, body ~514) are deleted on merge (Story 11.1-11.23 practice); the `deferred-work.md` entry carries `[open: baseline recorded <date>]` until then.

## Tasks / Subtasks

Red first for every behaviour change (the guard test, AC #4); record runs in Debug Log References (Story 11.1-11.23 practice). Docker must be running for Api and Infrastructure tests (Testcontainers); the Architecture guard needs none. One PR on `feature/11-24-migration-timeout`. No `app-deploy.yml` edit before Task 2 is decided.

- [x] Task 0: Baseline of the repo (AC: all). `dotnet build EnergyTracker.sln -c Release --no-incremental -p:TreatWarningsAsErrors=true` (0 warnings), `dotnet format EnergyTracker.sln --verify-no-changes --no-restore`, `dotnet test` per project; record counts (11.23 close is the reference, see its Debug Log).
- [x] Task 1: Baseline of the data (AC: #1). Read-only, Ralf-gated (production database).
  - [x] 1.1 Read run 38147146530 (`gh run view 38147146530 --log` or the Actions UI): step start/end for "Apply pending EF Core migrations", the migration list printed in "Pre-migration restore point", and any `Done.` line from `dotnet ef`. Record the exact seconds and what the step includes (`dotnet tool restore`, `az sql server show`, `dotnet ef migrations list`, the build `dotnet ef` triggers, then `database update`). If the log has expired, say so and use the 529 s figure from the deferred entry as given.
  - [x] 1.2 Ask Ralf (full sentences) to run, or to approve running through the deploy identity, this read-only query on `energytracker`: `SELECT SUM(ps.row_count) AS [rows], SUM(ps.reserved_page_count) * 8 / 1024 AS reserved_mb FROM sys.dm_db_partition_stats ps WHERE ps.object_id = OBJECT_ID('dbo.SmartPlugReadings') AND ps.index_id IN (0, 1);` plus the same sum with no index filter for total size including indexes, and `SELECT name, SUM(used_page_count) * 8 / 1024 AS mb FROM sys.dm_db_partition_stats ps JOIN sys.indexes i ON i.object_id = ps.object_id AND i.index_id = ps.index_id WHERE ps.object_id = OBJECT_ID('dbo.SmartPlugReadings') GROUP BY name;`. Do not run it yourself without his go-ahead; do not write to the database.
  - [x] 1.3 Compute rows per second from 1.1 and 1.2 (the table at deploy time was about 180k rows per Story 11.4; use the 1.2 figure only for today's size, and say which rows the rate divides by). Record rate, today's size, and the projected time of the same `ALTER COLUMN` plus `CREATE INDEX` pair at today's size.
  - [x] 1.4 Write the baseline (date, rows, MB, rate, source run) into Completion Notes and the `deferred-work.md` entry gets `[open: baseline recorded <date>]`.
- [x] Task 2: Ask First (AC: #2, #5, #6). Put the baseline next to the Decision table in a message to Ralf (full sentences; he decides), including the retry question (AC #5) and the self-host question (AC #6). Then record choice, date and rejected options in the header comment and Completion Notes. No workflow edit before this.
- [x] Task 3: Guard test first, red (AC: #4, #5, #7). `tests/EnergyTracker.Architecture.Tests/MigrationDeployWorkflowTimeoutGuardTests.cs`, sibling of `MigrationDeployWorkflowRestorePointGuardTests` (same text-scan approach: no YAML library, whole-line `#` comments stripped, steps split on `- name:`; copy or share its helpers only if that is a pure move, otherwise duplicate the ~30 lines as the sibling guards do).
  - [x] 3.1 The migration step's connection string carries the decided `Command Timeout=<N>;`, and `Connection Timeout=30;` stays (a regression to the 30 s default is the failure this guard exists for).
  - [x] 3.2 The step's `timeout-minutes` equals the decided value and satisfies the arithmetic floor of Dev Notes "Timeout arithmetic".
  - [x] 3.3 The retry rule of AC #5 (whatever Ralf chose) is pinned.
  - [x] 3.4 Known-bad samples: the same predicates run on workflow text with `Command Timeout` removed, lowered, and with `timeout-minutes` below the floor, each reported as a failure. Run the guard against the current workflow first and record that it is red for the new value (the current file says 600 and 20).
- [x] Task 4: Workflow change per the chosen option (AC: #4, #5). `.github/workflows/app-deploy.yml` step "Apply pending EF Core migrations" only. Keep the existing comment block above `CONNECTION_STRING` in the repo's style: it already narrates 300 to 600; extend it by the decided value and the reason in one to three lines, point to `project-context.md` for the rule, and do not rewrite the earlier lines. Leave `Connection Timeout=30` and the restore-point block untouched. If Ralf chose a scale-up path (option T3), that is a separate step with its own preflight right and its own guard, written in full in the story addendum he approves, not improvised here.
- [x] Task 5: Docs and project memory (AC: #3, #6, #8). `project-context.md` "Migrations" bullet next to expand/contract, `Last Updated` line; `docs/self-hosting.md` line if AC #6 says "document"; `infra/README.md` only if option T3 touches the runbook; delete the two `deferred-work.md` entries.
- [x] Task 6: Verify and PR (AC: all). `dotnet format EnergyTracker.sln`, rerun the Task 0 gate, full `dotnet test` per project (`web` untouched, no frontend run), Architecture guard green, workflow YAML parses (`python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/app-deploy.yml'))"` if PyYAML is present, otherwise `gh workflow view app-deploy.yml` after push). Actions pinned-SHA guard stays green (no action version change in this story).
- [x] Task 7: Post-deploy check (Ralf; the dev does not run it; story `done` only after it). After merge, the deploy run's "Apply pending EF Core migrations" step shows the new `Command Timeout` in effect (the connection string is not logged: check through the step's duration and the green run, and `git show` of the merged workflow). If no schema-changing migration is pending at that deploy, the check is "step green with no pending migrations"; the new value is exercised by the next real long rewrite, which Ralf notes here.

### Review Findings

Code review 2026-11-03 (Blind Hunter, Edge Case Hunter, Acceptance Auditor). 2 decision-needed (resolved to patch by Ralf), 11 patch, 0 defer, 16 dismissed.

- [x] [Review][Patch] Job cap makes the new step cap unreachable: `deploy` job `timeout-minutes: 30` cancels the job before the 60 min step or `Command Timeout=1800` can be used (projected 24 min migration plus preflight up to 25 min). Decided 2026-11-03 (Ralf): raise the job cap to 120 and extend the guard so the job cap covers preflight cap (25) + migration step cap (60) + an allowance for the other steps [.github/workflows/app-deploy.yml:166]
- [x] [Review][Patch] Step-cap floor margin was chosen by the dev. Decided 2026-11-03 (Ralf): keep 600 s rollback margin and 300 s build allowance; record the approval in Completion Notes [_bmad-artifacts/implementation/11-24-migration-timeout-headroom.md:148]
- [x] [Review][Patch] Timeout-detection guard can pass vacuously: it checks only the position of the string `Execution Timeout Expired`, not `grep -qF ... "$EF_UPDATE_OUT"` followed by `exit 1`; the `sleep 30` check is skipped when `sleep 30` is absent; the known-bad variant only replaces the string. Pin the grep + `exit 1` block, require `sleep 30`, add a known-bad variant with `exit 1` removed [tests/EnergyTracker.Architecture.Tests/MigrationDeployWorkflowTimeoutGuardTests.cs:130]
- [x] [Review][Patch] Floor check is dead code: it uses the constant `ExpectedCommandTimeoutSeconds`, not the parsed value, and can only fire when the equality check already fired; the known-bad test asserts only `Contains("timeout-minutes")`. Use the parsed Command Timeout in the floor and assert the floor message in the known-bad test (AC #4 "relation between them") [tests/EnergyTracker.Architecture.Tests/MigrationDeployWorkflowTimeoutGuardTests.cs:123]
- [x] [Review][Patch] `dotnet ef database update` output goes only to a file and is printed after exit: up to 30 min with no live log, and a step or job cancel loses the output. Stream it with `tee` (keep the exit status, e.g. `set -o pipefail` in a subshell) [.github/workflows/app-deploy.yml:388]
- [x] [Review][Patch] `::error::` text hardcodes `Command Timeout=1800`, which drifts from the connection string. Name the setting without the number [.github/workflows/app-deploy.yml:396]
- [x] [Review][Patch] project-context.md bullet does not label the 350 rows/s rate as an upper bound (AC #1). It also reads as if 1800 s covers today's 24 min pair, while the rule says a pair over 900 s per command must already be batched; say so explicitly [_bmad-artifacts/project-context.md:160]
- [x] [Review][Patch] Workflow comment extension runs past Task 4's "one to three lines" (about five lines added). Trim it to three [.github/workflows/app-deploy.yml:316]
- [x] [Review][Patch] Self-host doc says "add `Command Timeout=300`" but the compose string has no trailing `;`. Say "append `;Command Timeout=300`" [docs/self-hosting.md:192]
- [x] [Review][Patch] Task 1 parent checkbox is unchecked while 1.1 to 1.4 are checked [_bmad-artifacts/implementation/11-24-migration-timeout-headroom.md:38]
- [x] [Review][Patch] Task 1.2 asked for a per-index size breakdown; Completion Notes record only the totals (261 MB, 441 MB). Record it, or note why it was skipped [_bmad-artifacts/implementation/11-24-migration-timeout-headroom.md:152]

## Dev Notes

### Current state of the files this story touches

- `.github/workflows/app-deploy.yml`, job `deploy`, step "Apply pending EF Core migrations" (lines ~293 to ~390): `timeout-minutes: 20`; builds `CONNECTION_STRING` with `Connection Timeout=30;Command Timeout=600;`; writes the "Pre-migration restore point" block to `$GITHUB_STEP_SUMMARY`; then `for attempt in 1 2 3 4 5` runs `dotnet ef database update` with `sleep 30` between failures. The comment above `CONNECTION_STRING` records the history: default 30 s, then 300 s, then 600 s after `CleanupOrphanedUnmappedSmartPlugReadingDuplicates` timed out at 179k rows; that migration's `Up()` runs three separate commands (temp index create, delete, temp index drop), "each getting this full budget independently". **Preserve:** that comment, the restore-point block and its order (guarded by `MigrationDeployWorkflowRestorePointGuardTests`), the firewall steps before and the always-run firewall removal after, `Connection Timeout=30`.
- `tests/EnergyTracker.Architecture.Tests/MigrationDeployWorkflowRestorePointGuardTests.cs`: today pins restore-point order and migration-before-deploy only. It does not mention `Command Timeout` or `timeout-minutes`, so nothing guards the 600 today (grep `Timeout` in the Architecture tests finds no hit). Model the new guard on it.
- `docker-compose.yml` `migrate` service and `scripts/self-host/migrate-entrypoint.sh`: Postgres bundle, connection string without a command timeout (Npgsql default 30 s per command). Story 11.21 owns that path; this story only answers AC #6.
- `infra/modules/database-sqlserver.bicep`: `skuName` `Basic` (5 DTU), `maxSizeBytes` 2 GiB. The 5 DTU log-rate cap is why a column rewrite is slow on this tier (see "What 529 s measured").

### What 529 s measured

- The deferred entry says the step "took 8 min 49 s (about 529 s)" and that "the step timestamps include the whole migration run". That is the whole step: `dotnet tool restore`, the best-effort `dotnet ef migrations list` (its own `timeout 120`), the build `dotnet ef` runs, then `database update`. So 529 s is an **upper bound** on the migration itself; do not treat it as a clean rate. Task 1.1 shortens the gap if the log still exists.
- The 11.4 migration (`20261011115656_NarrowSmartPlugReadingDeviceNameKeyUnmappedByDevice`): `ALTER COLUMN` `nvarchar(max)` to `nvarchar(200)` on `SmartPlugReadings` (a size-of-data rewrite of every row, plus index maintenance) and `CREATE INDEX`, at about 180k rows. Postgres timed the same change at 300k rows in 0.33 s plus 0.11 s (Story 11.4 review note): the slowness is SQL Server on 5 DTU, not the SQL itself.
- `Command Timeout` is per command, not per migration or per run. One migration that issues several statements gives each its own 600 s (the comment says this). The ceiling for one `ALTER COLUMN` or one `CREATE INDEX` is therefore the number to size.

### Timeout arithmetic

- Worst case of the step today: `dotnet ef database update` retried 5 times, each attempt up to (number of long commands in the pending migrations) x `Command Timeout`, plus 4 x 30 s sleeps. With 600 s and `timeout-minutes: 20` (1200 s), a single command that times out once already uses half the cap, and a second attempt at the same command cannot finish before the runner cancels the step. A cancel during the **rollback** of a large `ALTER COLUMN` is the bad case: SQL Server's rollback can take about as long as the work done, and the step is gone while the database is still busy.
- The guard's floor (Task 3.2) must be stated as a formula in the test, not a magic number: `timeout-minutes * 60` must exceed `Command Timeout` plus the time the step needs besides migrating (restore-point block with `migrations list` at 120 s, plus a build allowance), by a margin the decision names. Pick the margin with Ralf; record it in the test's comment in one line.
- Telling timeout from connection failure (AC #5): `dotnet ef` exits non-zero for both. Options: (a) accept that a timed-out migration is retried once and cap attempts so the cap fits; (b) match `Execution Timeout Expired` in the captured output and stop retrying (`exit 1` with an `::error::` line naming `Command Timeout`); (c) leave the loop and size `timeout-minutes` for 5 attempts. (b) is the smallest honest change; it needs the output captured to a file (the step already uses `EF_LIST_ERR` for the list command: same pattern). Decide with Ralf in Task 2; do not do it silently.

### Decision table (Ask First; the recommendation is a hypothesis)

| Option | What changes | Cost / risk | Fits |
|---|---|---|---|
| T1 Raise `Command Timeout` (and `timeout-minutes`) | One number in the workflow plus the guard. Candidate: 1800 s per command with `timeout-minutes: 60`. | Cheapest. A migration that is genuinely stuck now holds the deploy and a table lock for longer; the new ceiling must still be below the point where the old revision's writes (it keeps serving while migrations run, Story 10.1) pile up behind the lock. Rollback of a 30 min statement is long. | Single known-slow rewrite, small team, one household |
| T2 Batch the change | Rule only (nothing to ship now): any rewrite expected over a threshold is split into chunked expand steps (add nullable column, backfill in batches in a loop or a job, switch in a later deploy), which is already the expand/contract shape. | Real engineering per long change; keeps every command short and rollback cheap. Needs a threshold. | Recurring long rewrites; best for safety |
| T3 Documented off-peak / scale-up path | Rule plus runbook: before a known-long migration, scale the database up (Basic to a Standard tier) for the deploy and back after, or run the migration by hand off-peak. | Scale operations need Azure rights the deploy identity may not hold (Story 11.5 identity split); cost per hour small; Bicep `skuName` would drift from the live tier if done by hand. Needs its own preflight and guard if automated. | Rare, very large rewrites |

Recommendation to bring to Ralf: **T1 plus the T2 rule.** Raise the ceiling once to a stated value so the next ordinary rewrite does not fail a release, and record the rule that an estimate above a stated share of that ceiling (from rows x baseline rate) must be batched (T2) or take the T3 path by hand. T3 automation is not worth building for one table. Task 1's data may overturn this: if the table has grown several-fold since 11.4, the same pair of statements may already exceed any sensible ceiling and T2 becomes the rule, not the exception.

### Timeouts that are not this story's

- `Connection Timeout=30` (time to connect) stays.
- `timeout 120` on `dotnet ef migrations list` (best-effort restore-point listing) stays.
- `SmartPlugImportRepository` command timeouts (120 s, 180 s overrides) are runtime, not deploy; untouched.
- Preflight `timeout-minutes: 25` has its own worst-case comment; untouched.

### Project Structure Notes

- New file only under `tests/EnergyTracker.Architecture.Tests/` (one test class per file, name `{Subject}Tests`, `Snake_case_with_underscores` methods, xUnit v3 / Shouldly; no `Task.Delay`, no Testcontainers needed for a text scan).
- Workflow text is scanned, not parsed: whole-line `#` comments are stripped first, so a comment cannot satisfy or break an assertion. A guard must therefore look at the connection-string line itself, not at the explanatory comment.
- Documentation and comments are written normal prose, one to three lines of *why* (Story 11.19 rule; `NoHistoryNarratingCommentsTests` covers `src`, but keep the workflow comment in the same style).
- No `src/**` change, so `ServiceRegistrationSnapshotTests` and the analyzer gate are not touched; `dotnet format` still runs because the test file is new.

### Previous story intelligence

- **11.23 (done, `002118d`):** the same shape: an operator-facing finding promoted from `deferred-work.md`, a baseline-first gate, an Ask First recorded as `AMENDED`, a source-scan guard with a known-bad variant, one PR, docs and `project-context.md` bullet, a post-deploy Ralf check before `done`. Reuse its header-comment conventions and its Debug Log layout. Its Task 7 pattern (story `done` only after Ralf's Azure check) applies here.
- **11.21 (self-host migration path):** introduced the `migrate` service; its entrypoint prints the failure lines quoted in `docs/self-hosting.md` ("Database migrations"). Change doc and script together if the self-host answer touches either.
- **11.4 deferred note (review, line ~109):** "SQL Server `ALTER COLUMN` + `CREATE INDEX` were not timed at scale"; this story closes that loop with a recorded baseline.
- **10.1:** restore point is written once before the first attempt, never per retry. Any retry change (AC #5) must keep that.
- **11.5:** CI identity split. The deploy identity's rights are minimal; any option needing new Azure rights (T3 automation) needs `azure-preflight.sh` and `infra/README.md` ("Workflow identities and their permissions") changes, and is therefore out of this story unless Ralf asks.

### Git intelligence

Last commits on `main`: `a297df5` Dependabot dotnet-ef bump (the tool this step runs, `.config/dotnet-tools.json`; check the pinned version still matches the EF packages, "bump together" rule), `ac729f5` npm bump, `c14d35c` 11.23 close docs, `002118d` 11.23 code, `806dfad` 11.22 close. Pattern: code PR then a separate `docs: close Story 11.x` PR after the live check. Expect the same here.

### Latest tech notes

- Microsoft.Data.SqlClient `Command Timeout` connection-string keyword is the default for `SqlCommand.CommandTimeout`, in seconds, per command; `0` means no limit (do not use: a stuck migration would then hold the deploy until the step cap). EF Core's own `Database.SetCommandTimeout` is not involved: `dotnet ef database update` takes the value from the connection string.
- Azure SQL Basic (5 DTU) has the lowest log-rate governance of the DTU tiers; a size-of-data operation is log-bound. A Standard tier raises that cap, which is the only reason T3 helps.
- Timeout symptom to match for AC #5 (b): SqlClient reports `Execution Timeout Expired. The timeout period elapsed prior to completion of the operation or the server is not responding.` Verify the exact text from a real or simulated run before pinning a regex to it; do not rely on this note.

### References

- [Source: _bmad-artifacts/planning/epics/epic-11-codebase-audit-hardening.md#Story 11.24: Migration Timeout Headroom]
- [Source: _bmad-artifacts/implementation/deferred-work.md, index row ~95 and body ~514]
- [Source: .github/workflows/app-deploy.yml, step "Apply pending EF Core migrations"]
- [Source: tests/EnergyTracker.Architecture.Tests/MigrationDeployWorkflowRestorePointGuardTests.cs]
- [Source: _bmad-artifacts/project-context.md#Migrations (expand/contract, restore point, who applies migrations)]
- [Source: _bmad-artifacts/implementation/11-4-unmapped-smart-plug-readings-keyed-by-device.md, review note on SQL Server timing]
- [Source: infra/modules/database-sqlserver.bicep (Basic 5 DTU, 2 GiB)]
- [Source: docker-compose.yml `migrate`, scripts/self-host/migrate-entrypoint.sh, docs/self-hosting.md "Database migrations"]

### Project Context Reference

`_bmad-artifacts/project-context.md` is loaded as persistent facts: migrations rules (expand/contract, restore point, who applies), workflow guards (`WorkflowPreflightGuardTests`, `AppDeployWorkflowJobsGuardTests`), test naming and Architecture-guard conventions.

## Dev Agent Record

### Agent Model Used

{{agent_model_name_version}}

### Debug Log References

- Task 3 red (2026-11-03): `MigrationDeployWorkflowTimeoutGuardTests` against the unchanged workflow (600 / 20): 4 of 6 tests failed (main guard + 3 known-bad precondition mutations that need the 1800 value). Green after Task 4: Architecture 212 passed, 0 failed.
- Task 6: `dotnet build -c Release --no-incremental -p:TreatWarningsAsErrors=true` 0 warnings, 0 errors (after fixing 4 CA1305 in the new test); `dotnet format --verify-no-changes` exit 0; Api 508, Application 605, Architecture 212, Infrastructure 565, all passed; workflow YAML parses (ruby YAML.load_file).

- Task 0 (2026-11-03): Release build 0 warnings; `dotnet format --verify-no-changes` exit 0; `dotnet test` Api, Application, Architecture, Infrastructure all passed, 0 failed.
- Task 1.1 (run 38147146530, 2026-10-11): step "Apply pending EF Core migrations" starts 14:35:08Z; `Applying migration '20261011115656_NarrowSmartPlugReadingDeviceNameKeyUnmappedByDevice'.` 14:35:25Z; `Done.` 14:43:57Z. Step about 529 s; migration itself about 512 s (08:32) for about 180k rows, about 350 rows/s, with `Command Timeout=600` (headroom about 88 s). Rate covers the ALTER COLUMN + CREATE INDEX pair together (upper bound per command).

### Completion Notes List

- Ask First 2026-11-03 (AC #2, #5, #6): Ralf chose T1 + T2 rule; stop retrying on a command timeout; self-host document-only. Step cap floor formula chosen by dev (margin 600 s): `timeout-minutes*60 >= Command Timeout + 120 (migrations list) + 300 (build allowance) + 4*30 (sleeps) + 600`. Rejected: T2 only, T3 automation, attempt cap, sizing for 5 attempts. `Execution Timeout Expired` verified in Microsoft.Data.SqlClient 7.0.2 resources.
- Implemented (2026-11-03): `app-deploy.yml` step "Apply pending EF Core migrations": `timeout-minutes: 60`, `Command Timeout=1800;`, `dotnet ef database update` output captured to `$EF_UPDATE_OUT` and echoed, loop exits 1 with `::error::` on `Execution Timeout Expired` (restore point block, 5-attempt shape and firewall retry otherwise unchanged; restore-point, preflight and jobs guards green with no edit). Guard `MigrationDeployWorkflowTimeoutGuardTests`: pins Connection Timeout=30, Command Timeout=1800, timeout-minutes=60, the floor formula (2940 s) and the timeout detection between `dotnet ef database update` and `sleep 30`; 5 known-bad variants fail. `project-context.md` Migrations bullet + Last Updated; `docs/self-hosting.md` line (AC #6: 30 s acceptable for household-sized data; operator can raise via `Command Timeout=`); `deferred-work.md` index row and body entry deleted.
- Note: with two long commands in one migration (the 11.4 shape) 2 x 1800 s exceeds the 3600 s cap; the project-context rule (batch above 900 s per command) is what covers that case.
- Task 7 (2026-11-03, confirmed by Ralf): App Deploy run 41708528820 deployed `aa84578`. Attempt 1 skipped Deploy because Backend tests hit the known order-dependent `InviteTokenIsNotInTracesTests` flake (now `[open]` in `deferred-work.md`); attempt 2 (failed jobs re-run) was green. Deploy job about 3 min against the 120 min cap; "Apply pending EF Core migrations" green in 19 s with "No migrations were applied. The database is already up to date." (the `tee` output streamed live); health check passed on attempt 2. No schema-changing migration was pending, so the 1800 s ceiling is exercised by the next real long rewrite.
- Baseline 2026-11-03 (AC #1), prod `energytracker` on Azure SQL Basic 5 DTU, read-only queries run with Ralf's go-ahead via `sqlcmd` + Azure CLI login: `SmartPlugReadings` has 512,353 rows; 261 MB reserved (heap/clustered), 441 MB total with indexes. Rate from run 38147146530: about 180k rows in about 512 s, about 350 rows/s for the ALTER COLUMN + CREATE INDEX pair (upper bound; includes both commands). Projection at today's size: 512,353 / 350 is about 1,460 s (about 24 min) if linear, well above `Command Timeout=600` and above `timeout-minutes: 20`. The same pair would now fail a release. Table grew about 2.8x since 11.4. Per-index breakdown (Task 1.2, third query): not recorded at baseline time; only the two totals were captured. The 180 MB gap between them is the nonclustered indexes. Re-run that query before the next long rewrite if a per-index estimate is needed.
- Review 2026-11-03: Ralf approved the floor margins (600 s rollback margin, 300 s build allowance) and raised the `deploy` job cap 30 -> 120 min (floor: preflight 25 + migration step 60 + 35 for the other steps), now pinned by the guard.

### File List

- .github/workflows/app-deploy.yml
- tests/EnergyTracker.Architecture.Tests/MigrationDeployWorkflowTimeoutGuardTests.cs (new)
- _bmad-artifacts/project-context.md
- docs/self-hosting.md
- _bmad-artifacts/implementation/deferred-work.md
- _bmad-artifacts/implementation/sprint-status.yaml
- _bmad-artifacts/implementation/11-24-migration-timeout-headroom.md

### Change Log

- 2026-11-03: Migration Command Timeout 600 -> 1800 s, step cap 20 -> 60 min, retry stops on command timeout, guard test, project-context rule, self-hosting note (Story 11.24).

