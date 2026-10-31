---
baseline_commit: f213b6c8d576f035d5f73c6afd18544f072175ec
---

# Story 11.20: Drop the Plaintext Invite Token Column

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- Epic 11 "Codebase audit hardening" (planning/epics/epic-11-codebase-audit-hardening.md), tier "Triage follow-ups", promoted from deferred-work.md on 2026-10-31 (Story 11.9 dev-story entry). Origin: audit S10 follow-up. Architecture: AD-26 (invite tokens hashed), AD-2 (dual provider, migrations in both projects), AD-3 (HouseholdInvite has no query filter, on purpose). This file cites the AD IDs, which satisfies the spec-per-fix gate (project-context.md). -->
<!-- Branch: feature/11-20-drop-plaintext-invite-token-column (renamed 2026-10-31 from the "cokumn" typo; local only, nothing pushed). ONE PR, ONE DEPLOY (Ralf's decision 2026-10-31, overriding the two-deploy shape of Story 11.4). The migration that drops the column ships in the same release as the code that stops using it, a justified exception to the project-context.md expand/contract rule; safeguards in Dev Notes "Expand/contract exception". -->
<!-- No Auth0/Chrome live gate: no auth flow, claim or browser behaviour changes (invite preview/accept wire contract is identical). A cheap optional live smoke is in Task B4. -->

## Story

As a maintainer,
I want the legacy `HouseholdInvites.Token` column and its lookup branch removed,
so that no plaintext invite token is stored once every legacy invite has expired (audit S10 follow-up, promoted from `deferred-work.md`, Story 11.9 dev-story entry).

## Acceptance Criteria

1. **Given** Story 11.9 shipped on 2026-10-18 (deploy run 39120396072; Part R recorded 2026-10-21) and invites live 7 days (`CreateHouseholdInvite.InviteLifetime`), **when** this story starts, **then** Ralf runs the gate query (Dev Notes, "Gate query") against production and it returns **0 rows**: no row has a non-null `Token` with `ExpiresAtUtc` in the future. The result and date are recorded in Completion Notes **before** Part A merges. Time alone is satisfied (latest legacy invite was created before the 2026-10-18 deploy, so expired by 2026-10-25 at the latest); the query is the proof. A non-empty result stops the story (wait, or ask Ralf; never delete or null rows to pass the gate).
2. **Given** the release of this story, **when** it ships, **then** `HouseholdInvite.Token`, its `HouseholdInviteConfiguration` property and unique index, and the legacy branch of `FindInviteByTokenAsync` are gone from the code and the EF model, and one migration per provider (`DropHouseholdInviteTokenColumn`, scaffolded by `scripts/add-migration.sh`) drops the `HouseholdInvites.Token` column and its unique index `IX_HouseholdInvites_Token` on **both** providers. Invite create, preview, accept, list and revoke behave exactly as before for hash invites.
3. **Given** the expand/contract exception (one deploy), **when** the migration is written, **then** its header comment records the exception and the safeguards (gate query returned 0 rows; Story 10.1 restore point; the dropped column holds only expired plaintext tokens, so nothing of value is lost; rollback is the restore point, not `Down()`), and the story notes in Completion Notes that a health-check rollback to the previous image would fail on invite queries until the restore point is applied or the fix is rolled forward.
4. **Given** the tests named in the epic, **when** this story is done, **then** `HouseholdMembershipRepositoryTests`, `HouseholdInviteTests`, the invite unit tests that built invites with `Token`, and the migration tests (`PostgresMigrationTests`, `SqlServerMigrationTests`) are updated: the two legacy-lookup repository tests are deleted, the hash-only tests stay, and the old `AddHouseholdInviteTokenHash` migration tests no longer use the removed `HouseholdInvite.Token` property (raw SQL instead). No test is skipped or loosened to get green.
5. **Implied (nothing may move silently):** a guard proves the column cannot come back by accident: `HouseholdInvite` has no `Token` property and `HouseholdInviteConfiguration` maps no plaintext token column (extend an existing Architecture or Infrastructure test, or add one small test; known-bad sample proves it non-vacuous). `CreateHouseholdInviteTests` keeps its "stored invite carries only the hash" assertion in a form that compiles without `stored.Token` (assert the entity type has no `Token` member, or assert the persisted hash).
6. **Implied (docs and project memory):** the AD-26 line in `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md` (~line 242, "Invite tokens (S10)") gets a short `[AMENDED <date>, Story 11.20]` note that the legacy lookup and column are gone; the two `deferred-work.md` entries tagged `[promoted: 11.20]` (line ~132 index row and ~568 body) are deleted (resolved by this story, per the convention in `project-context.md`); `FindInviteByTokenAsync`'s XML doc in `IHouseholdRepository` loses "legacy plaintext column second". `docs/codebase-audit-2026-10-10.md` is not edited (dated record).

## Tasks / Subtasks

Red first for every behaviour change; record runs in Debug Log References (Story 11.1-11.19 practice). Docker must be running for Infrastructure and Api tests (Testcontainers). One PR on `feature/11-20-drop-plaintext-invite-token-column`.

- [x] Task 0: Gate and baseline (AC: #1)
  - [x] 0.1 Ralf runs the gate query on production (Dev Notes) and pastes the result. Record query, date, result in Completion Notes. **Stop if not 0 rows.** The dev agent has no production access: ask Ralf and pause.
  - [x] 0.2 `dotnet build EnergyTracker.sln -c Release --no-incremental -p:TreatWarningsAsErrors=true` (0 warnings) and `dotnet format EnergyTracker.sln --verify-no-changes --no-restore`; `dotnet test` for Application, Architecture, Infrastructure, Api. Record counts (11.19 close is the reference).
- [x] Task 1: Update tests first (AC: #4, #5). Record the pre-change green run; the edits cannot compile once `Token` goes, so test and code changes share a commit.
  - [x] 1.1 `tests/EnergyTracker.Application.Tests/AcceptHouseholdInviteTests.cs`: `MakeInvite` sets `Token = Guid.NewGuid().ToString("N")` and tests read `invite.Token!`. Rework so `MakeInvite` generates `InviteToken.Generate()`, sets `TokenHash = InviteToken.Hash(token)` and exposes the token (tuple or helper). Fix every `invite.Token!` use; the `exception.Message.ShouldNotContain(...)` check (~line 80) must keep asserting the plaintext token is absent.
  - [x] 1.2 `tests/EnergyTracker.Application.Tests/CreateHouseholdInviteTests.cs` ~line 29: replace `stored.Token.ShouldBeNull()` with `typeof(HouseholdInvite).GetProperty("Token").ShouldBeNull()` (also the AC #5 guard); keep the hash assertion and the `^[A-Za-z0-9_-]{43}$` format check.
  - [x] 1.3 `tests/EnergyTracker.Api.Tests/HouseholdInviteTests.cs` `Accepting_an_expired_invite_returns_409` (~lines 55-87): seed `TokenHash = InviteToken.Hash(token)` with `var token = InviteToken.Generate()`; assert 409 stays.
  - [x] 1.4 `tests/EnergyTracker.Infrastructure.Tests/HouseholdMembershipRepositoryTests.cs` (~lines 353-410): delete `FindInviteByTokenAsync_finds_a_legacy_plaintext_only_invite` and `..._prefers_the_hash_row_when_both_a_hash_row_and_a_plaintext_row_match`; drop `found.Token.ShouldBeNull()` from the hash-only test; keep the unknown/over-long test. Rename `Two_hash_only_invites_with_a_null_Token_can_coexist` (~line 412) to `Two_invites_with_different_hashes_can_coexist`; check the `NewInvite` helper compiles.
  - [x] 1.5 `PostgresMigrationTests.cs` (~line 662) and `SqlServerMigrationTests.cs` (~line 663), `AddHouseholdInviteTokenHash_migration_...`: they read `legacy.Token` through the entity and insert a `HouseholdInvite { Token = ... }`. Rewrite those spots with raw SQL (`SqlQueryRaw<string>` for the read, `ExecuteSqlInterpolatedAsync` for the duplicate-token insert, `Should.ThrowAsync<DbUpdateException>` as today; SQL Server bracket quoting). The legacy-row read at the pre-`TokenHash` checkpoint selects the model's `TokenHash` column, which does not exist there: read that row with raw SQL too.
  - [x] 1.6 Guard (AC #5): the reflection assertion in 1.2, plus a small Infrastructure test on `dbContext.Model.FindEntityType(typeof(HouseholdInvite))` asserting no property named `Token` and no index over it. Non-vacuous: the same test asserts `TokenHash` and its index are found.
  - [x] 1.7 New migration tests, both providers, red first (no such migration): migrate to the previous migration, assert `Token` column and `IX_HouseholdInvites_Token` exist and a legacy row is present; migrate to `DropHouseholdInviteTokenColumn`, assert column and index are gone, `TokenHash`, its index and existing rows survive, and a hash invite inserts through the EF model. Locate by name suffix `_DropHouseholdInviteTokenColumn`, like `DropOldIndexMigrationSuffix` in `PostgresMigrationTests.cs`.
- [x] Task 2: Production code (AC: #2)
  - [x] 2.1 `src/EnergyTracker.Domain/HouseholdInvite.cs`: delete `Token` and its comment; shorten the `TokenHash` comment to 1-3 lines (`NoHistoryNarratingCommentsTests`). Do not tighten `TokenHash` nullability (separate destructive-class change).
  - [x] 2.2 `HouseholdInviteConfiguration.cs`: delete the `Token` property mapping, `HasIndex(i => i.Token).IsUnique()` and their comment. Keep `TokenHash` mapping and index. The AD-3 "no query filter" reasoning stays.
  - [x] 2.3 `HouseholdRepository.cs` `FindInviteByTokenAsync` (~51-69): keep `IsPlausible`; return `await dbContext.HouseholdInvites.FirstOrDefaultAsync(i => i.TokenHash == hash, ct)`; delete the legacy query; one-line comment. `FirstOrDefault` stays (hash index is non-unique).
  - [x] 2.4 `IHouseholdRepository.cs` (~line 16) XML doc: "Finds an invite by its plaintext token via the SHA-256 hash (AD-26). Null for an unknown or over-long token."
  - [x] 2.5 `HouseholdInviteEndpoints.cs` ~line 27 `invite.Token` is `CreatedHouseholdInvite.Token` (plaintext returned once), not the entity property. Leave it and the wire field.
  - [x] 2.6 `grep -rn "\.Token\b" src --include='*.cs'` for leftovers.
- [x] Task 3: Migration (AC: #2, #3). `scripts/add-migration.sh DropHouseholdInviteTokenColumn` (both providers in one commit, AD-2). Keep the scaffolded `DropIndex`/`DropColumn`; check order and add per provider: SQL Server must drop `IX_HouseholdInvites_Token` before the column. Header comment records the expand/contract exception and safeguards (AC #3). `Down()` as scaffolded (re-adds nullable `Token` and the index; no values restored). Verify the snapshot diff removes only `Token` and its index from `HouseholdInvite`, and `dotnet ef migrations has-pending-model-changes` (or the repo's equivalent) reports none.
- [x] Task 4: Docs (AC: #6). invariants-rules.md `[AMENDED 2026-10-31, Story 11.20]` note (lookup and column gone); delete the two `[promoted: 11.20]` entries in `deferred-work.md`; `grep -n "HouseholdInvites\|plaintext" infra/README.md docs/self-hosting.md` and fix any mention.
- [ ] Task 5: Verify and PR (AC: all)
  - [x] 5.1 `dotnet format EnergyTracker.sln`, rerun the 0.2 gate, full `dotnet test` per project. Record counts and exact delta (2 legacy tests removed, new migration and guard tests added).
  - [ ] 5.2 Open one PR against `main`. After merge, Ralf's steps (the dev does not run these): watch the App Deploy run, read the "Pre-migration restore point" block in the run summary (Story 10.1), record the run id, run the post-deploy query (Dev Notes, expect `Token` and `IX_HouseholdInvites_Token` gone), create and preview an invite in the live app. Story `done` only after that.

### Review Findings

- [x] [Review][Patch] Duplicate-legacy-insert assertion accepts any `DbException`; narrow to the unique violation (Postgres `PostgresException.SqlState == "23505"`, SQL Server `SqlException.Number` 2601/2627) [tests/EnergyTracker.Infrastructure.Tests/PostgresMigrationTests.cs:695, tests/EnergyTracker.Infrastructure.Tests/SqlServerMigrationTests.cs:696]
- [x] [Review][Patch] Drop-migration test uses `migrations.IndexOf(drop) - 1` with no guard; assert `IndexOf(drop) > 0` first [tests/EnergyTracker.Infrastructure.Tests/PostgresMigrationTests.cs:714, tests/EnergyTracker.Infrastructure.Tests/SqlServerMigrationTests.cs:715]
- [x] [Review][Defer] `TokenHash` still nullable, non-unique, `FirstOrDefault` without ordering; legacy NULL-hash rows linger as dead data [src/EnergyTracker.Infrastructure/Configurations/HouseholdInviteConfiguration.cs] — deferred, spec "Out of scope" names tightening `TokenHash` as its own destructive-class migration
- [x] [Review][Defer] Migration has no in-DB guard against live legacy invites; the 0-row gate ran against production only, so another environment (self-host, Story 11.21) with a live pre-11.9 invite makes it answer 404 (invite lifetime 7 days, reissuable) [src/EnergyTracker.Infrastructure.Migrations.*/Migrations/20261031*_DropHouseholdInviteTokenColumn.cs] — deferred, spec accepts the one-deploy exception and the gate

## Dev Notes

### Expand/contract exception

`project-context.md` (Migrations → Expand/contract) says destructive changes ship in a later deploy than the code that stops using the old shape, because `app-deploy.yml` migrates before deploying and a health-check rollback redeploys the previous image against the migrated schema. The previous image selects `HouseholdInvites.Token` in every invite query, so after this migration a rollback to it would fail on invite endpoints. Ralf chose one PR (2026-10-31). Safeguards, to repeat in the migration comment: the gate query proved no live legacy invite; the dropped column holds only expired plaintext tokens; Story 10.1 writes a restore point before every migration; invites are a low-traffic feature, and all other endpoints are unaffected by a failed invite query. Do not add this exception to `project-context.md` (one-off).

### Current state of the files being changed (read before editing)

- `src/EnergyTracker.Domain/HouseholdInvite.cs`: `Token` (`string?`, init, legacy), `TokenHash` (`string?`, init), `ExpiresAtUtc` (mutable), `ConsumedAtUtc`, `Version` concurrency token. Keep all but `Token`.
- `src/EnergyTracker.Infrastructure/Configurations/HouseholdInviteConfiguration.cs`: `Token` `HasMaxLength(32)` + `HasIndex(Token).IsUnique()`; `TokenHash` `HasMaxLength(64)` + non-unique `HasIndex`; `Version` concurrency token; FK to `Household` cascade; **no** `HasQueryFilter` on purpose (AD-3 exception: the accepting principal has no household yet). `HouseholdMemberLookupsAreHouseholdScopedTests` guards the by-id methods; do not touch them.
- `src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs` `FindInviteByTokenAsync`: `IsPlausible` guard → hash `FirstOrDefaultAsync` → legacy `SingleOrDefaultAsync(i => i.Token == token)`. The last line goes.
- Migrations: Postgres `20261017203251_AddHouseholdInviteTokenHash`, SQL Server `20261017203253_AddHouseholdInviteTokenHash`. SQL Server has the **filtered** unique index `IX_HouseholdInvites_Token ... WHERE [Token] IS NOT NULL` (verified in 11.9); Postgres has the plain unique `IX_HouseholdInvites_Token` (NULLs are distinct there). Latest existing migrations are `20261025095726_AddActiveJobGuard` (SQL Server) / `20261025095723` (Postgres); new ones sort after them.
- What must be preserved: the wire contract (`POST /api/household-invites` returns the plaintext token once; `GET /api/household-invites/{token}` and `.../accept` take it), unknown/implausible/over-long token → same 404 answer (AC #11c of 11.9), rate limits, revoke/list scoping by `HouseholdId`.
- `CreatedHouseholdInvite.Token` (Application) is the plaintext returned at creation. It is **not** the entity property; do not remove it.

### Gate query (Ralf runs it; works on Azure SQL)

```sql
SELECT COUNT(*) AS LiveLegacyInvites
FROM HouseholdInvites
WHERE Token IS NOT NULL AND ExpiresAtUtc > SYSUTCDATETIME();
```

Expect `0`. Informational second query (does not gate): `SELECT COUNT(*) FROM HouseholdInvites WHERE Token IS NOT NULL;` (rows holding an expired plaintext token; they vanish with the column). Production is Azure SQL (SQL Server provider); the Postgres form for a self-host is `... "ExpiresAtUtc" > NOW()`. After deploy: `SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('HouseholdInvites') AND name = 'Token';` expect `0`, and `SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID('HouseholdInvites')` must not list `IX_HouseholdInvites_Token`.

### Legacy invites after this release

AC #1's gate guarantees no live legacy invite exists. Afterwards a leftover legacy token simply answers the same 404 as an unknown one. Do not keep a transitional fallback.

### Architecture compliance

- **AD-2:** migration added to both provider projects by `scripts/add-migration.sh`, never one alone; only portable model features; the raw SQL lives in the migration only, never `HasFilter` or provider-specific mapping in the shared model.
- **AD-26:** only the SHA-256 hash is stored; this story removes the last place the old plaintext could live. No change to hashing (`InviteToken.Hash`, `IsPlausible`, `MaxLength`).
- **AD-3:** `HouseholdInvite` stays without a query filter; do not add one "while here".
- **Comment rule (Story 11.19):** comments state why in 1-3 lines; history lives in git. New comments in migrations and config follow this (the migration header comments above are the allowed longer form for generated files, which are excluded). `NoHistoryNarratingCommentsTests` scope includes `src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository*.cs` and some folders; run the Architecture suite.
- **Analyzer gate (11.18):** build with `-p:TreatWarningsAsErrors=true` and run `dotnet format EnergyTracker.sln` before pushing. Migrations are `generated_code = true`. Test names keep `Snake_case`.

### Testing standards

- xUnit v3 + Shouldly + NSubstitute; migration and repository tests use Testcontainers against real Postgres and SQL Server (AD-2). Use `TestContext.Current.CancellationToken`.
- Migration tests locate the migration by name suffix and use `migrator.MigrateAsync(previous)` then `MigrateAsync(target)` (pattern in `PostgresMigrationTests.cs`).
- Do not reintroduce `Token` in seed data anywhere (`grep -rn "Token = " tests` for invites after the change).
- Expected test files touched: `AcceptHouseholdInviteTests`, `CreateHouseholdInviteTests`, `HouseholdInviteTests` (Api), `HouseholdMembershipRepositoryTests`, `PostgresMigrationTests`, `SqlServerMigrationTests`. `RevokeHouseholdInviteTests` and `ListOpenHouseholdInvitesTests` have no `Token` use; they should compile unchanged.

### Previous story intelligence

- **11.9** (member removal, revocable hashed invites): introduced `TokenHash`, `InviteToken` (`Generate`, `Hash`, `IsPlausible`, `MaxLength`), the legacy fallback and the `[promoted]` note that is this story. Its migration comment says the previous image answers 404 for hash-only invites; irrelevant now. Its Part R recorded the deploy (run 39120396072, 2026-10-18).
- **11.4** (unmapped readings keyed by device): template for migration tests that pin a migration by name suffix. Its two-deploy shape is deliberately not followed here.
- **11.19** (structural cleanup, latest): comment-length guard, `RequireHousehold` filter, Program.cs composition split. Not directly touched here; invite endpoints keep their own household logic on purpose.
- **11.14**: `scripts/add-migration.sh` plus hand-edited raw SQL in two migrations is an accepted pattern.

### Git intelligence

Last commits: `f213b6c` (promoted deferred items into Epic 11/12), `2f396d1` (triage), `3cf5a9b` (11.19 PR #159). Working tree clean on `feature/11-20-drop-plaintext-invite-token-column` at `f213b6c`. Commit style: `feat:`/`fix:`/`docs:` conventional, story id and audit id in the subject (e.g. `feat: ... (Story 11.9, audit A2/S10) (#131)`).

### Latest technical notes

No new package. EF Core 10.0.x and Npgsql/SqlServer providers as pinned in `Directory.Packages.props`. SQL Server: a column cannot be dropped while an index references it, drop the index first. `DROP INDEX IF EXISTS` / `DROP COLUMN IF EXISTS` need SQL Server 2016+ (Azure SQL fine).

### Out of scope

Tightening `TokenHash` to non-null or unique (own destructive-class migration); moving the token out of the URL (Story 11.22); changing invite lifetime; the self-host migration path (11.21).

### Project Structure Notes

All paths above exist at `f213b6c`. One new migration pair under `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/` and `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/`, plus updated `EnergyTrackerDbContextModelSnapshot.cs` in each.

### References

- [Source: _bmad-artifacts/planning/epics/epic-11-codebase-audit-hardening.md#Story 11.20]
- [Source: _bmad-artifacts/implementation/11-9-member-removal-revocable-invites.md] (hash column, legacy fallback, Part R deploy)
- [Source: _bmad-artifacts/implementation/11-4-unmapped-smart-plug-readings-keyed-by-device.md] (two-deploy template, empty-model migration)
- [Source: _bmad-artifacts/implementation/deferred-work.md ~lines 132, 568] (promoted entries to delete at the end)
- [Source: _bmad-artifacts/project-context.md#Migrations] (expand/contract, add-migration.sh, restore-point rollback)
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md ~line 242] (AD-26 invite token line)

## Dev Agent Record

### Agent Model Used

Sonnet 5.5 (claude-sonnet-5-5)

### Debug Log References

- Baseline (11.19 close, Release, TreatWarningsAsErrors): build 0 warnings; format clean; Application 605, Architecture 157, Infrastructure 566, Api 457, all green.
- After change: build 0 warnings; `dotnet format --verify-no-changes` clean; Application 605, Architecture 157, Infrastructure 565, Api 456, all green.
- `dotnet ef migrations has-pending-model-changes` (fresh build), both providers: no pending changes.
- Red phase was implicit: the new migration tests had no migration to find and the guard test saw `Token` mapped until the code change; test and code edits share one change set because the old tests cannot compile without `Token`.

### Completion Notes List

- Gate (AC #1): Ralf ran `SELECT COUNT(*) AS LiveLegacyInvites FROM HouseholdInvites WHERE Token IS NOT NULL AND ExpiresAtUtc > SYSUTCDATETIME();` on production on 2026-10-31. Result: 0.
- Removed `HouseholdInvite.Token`, its config mapping and unique index, and the legacy branch of `FindInviteByTokenAsync` (hash-only now). Updated the `IHouseholdRepository` XML doc. `CreatedHouseholdInvite.Token` and the wire contract are untouched.
- Migration `DropHouseholdInviteTokenColumn` on both providers (Postgres `20261031173415`, SQL Server `20261031173418`): `DropIndex` before `DropColumn`; header comment records the expand/contract exception and safeguards.
- Rollback caveat: a health-check rollback to the previous image would fail on invite queries (it selects `HouseholdInvites.Token`) until the Story 10.1 restore point is applied or the fix is rolled forward. Other endpoints are unaffected.
- Tests: reworked `AcceptHouseholdInviteTests` (`MakeInvite` returns invite and token), `CreateHouseholdInviteTests` (reflection guard), Api `HouseholdInviteTests`, `HouseholdMembershipRepositoryTests`, both `*MigrationTests` (raw SQL for legacy rows, `DbException` on duplicate legacy insert). Added `HouseholdInviteModelTests` (non-vacuous model guard) and `DropHouseholdInviteTokenColumn_migration_...` for each provider.
- Deviation: the story named six test files, but `tests/EnergyTracker.Api.Tests/HouseholdMemberEndpointsTests.cs` also used `Token`. Deleted `A_legacy_plaintext_invite_still_previews_and_accepts` (tests removed behaviour) and dropped `stored.Token.ShouldBeNull()` (covered by the guards).
- Test delta: Infrastructure 566 to 565 (the 2 legacy repository tests run on 2 providers = -4; +1 model guard, +2 migration tests). Api 457 to 456. Application and Architecture unchanged.
- Docs: AD-26 `[AMENDED 2026-10-31, Story 11.20]` note; both `[promoted: 11.20]` deferred-work entries deleted. `infra/README.md` and `docs/self-hosting.md` mentions of "plaintext" concern Data Protection keys, not invites; no change.
- Open: Task 5.2 (PR and post-deploy steps) not done. Opening the PR needs Ralf's go-ahead; the deploy checks are Ralf's per the story. Story is `done` only after those.

### File List

- src/EnergyTracker.Domain/HouseholdInvite.cs
- src/EnergyTracker.Infrastructure/Configurations/HouseholdInviteConfiguration.cs
- src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs
- src/EnergyTracker.Application/Ports/IHouseholdRepository.cs
- src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20261031173415_DropHouseholdInviteTokenColumn.cs (new)
- src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20261031173415_DropHouseholdInviteTokenColumn.Designer.cs (new)
- src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/EnergyTrackerDbContextModelSnapshot.cs
- src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20261031173418_DropHouseholdInviteTokenColumn.cs (new)
- src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20261031173418_DropHouseholdInviteTokenColumn.Designer.cs (new)
- src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/EnergyTrackerDbContextModelSnapshot.cs
- tests/EnergyTracker.Application.Tests/AcceptHouseholdInviteTests.cs
- tests/EnergyTracker.Application.Tests/CreateHouseholdInviteTests.cs
- tests/EnergyTracker.Api.Tests/HouseholdInviteTests.cs
- tests/EnergyTracker.Api.Tests/HouseholdMemberEndpointsTests.cs
- tests/EnergyTracker.Infrastructure.Tests/HouseholdMembershipRepositoryTests.cs
- tests/EnergyTracker.Infrastructure.Tests/PostgresMigrationTests.cs
- tests/EnergyTracker.Infrastructure.Tests/SqlServerMigrationTests.cs
- tests/EnergyTracker.Infrastructure.Tests/HouseholdInviteModelTests.cs (new)
- _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md
- _bmad-artifacts/implementation/deferred-work.md
- _bmad-artifacts/implementation/sprint-status.yaml

### Change Log

- 2026-10-31: Dropped plaintext invite `Token` column, model mapping and legacy lookup; added `DropHouseholdInviteTokenColumn` migration (both providers); updated tests and docs (Story 11.20).
