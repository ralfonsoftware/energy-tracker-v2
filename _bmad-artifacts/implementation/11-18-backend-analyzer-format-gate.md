---
baseline_commit: d1b779264da4bfd094d4d9dfd5187ee342f9798e
---

# Story 11.18: Backend Analyzer and Format Gate

Status: in-progress

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- Epic 11 "Codebase audit hardening" (planning/epics/epic-11-codebase-audit-hardening.md), tier Later. Origin: codebase audit 2026-10-10 (docs/codebase-audit-2026-10-10.md) finding R3 ("No backend style or analyzer gate") plus the two "Other minor items" warnings (xUnit1051, exhaustive-deps). -->
<!-- Branch already exists: feature/11-18-backend-analyzer-format-gate (current checkout, clean, at d1b7792). ONE PR, but with a separate mechanical whitespace commit (see Task 3) so the reviewable diff stays readable. No migration, no new NuGet or npm package, no new AD, no runtime behaviour change. -->
<!-- No Ask First is blocking. Two recommendations below (CI-only TreatWarningsAsErrors, the suppression list) are decided here; Ralf may overrule them in review. The Auth0/Chrome live gate does not apply (no auth-flow or browser-behaviour change). -->

## Story

As a maintainer,
I want style and security analyzers enforced in CI,
So that whole classes of issues are caught at PR time instead of in audits (audit R3).

## Acceptance Criteria

1. **Given** the repo, **when** this story is done, **then** a root `.editorconfig` exists (`root = true`), `<AnalysisMode>Recommended</AnalysisMode>` is enabled for every .NET project through `Directory.Build.props`, and the CA2xxx, CA3xxx and CA5xxx security rules are on **at least as warnings** (`<AnalysisModeSecurity>All</AnalysisModeSecurity>`, because `Recommended` alone leaves most CA3xxx/CA5xxx rules off; see "Analyzer facts" in Dev Notes).
2. **Given** `pr-review.yml`'s `build-test-lint` job, **when** a PR runs, **then** it runs `dotnet format EnergyTracker.sln --verify-no-changes --no-restore` as its own named step, and that step fails the job on any whitespace, style or fixable analyzer drift.
3. **Given** current warnings, **when** this story is done, **then** the `xUnit1051` warnings are fixed (the epic says 6; at HEAD `d1b7792` there are **18**, all must go) and the real `react-hooks/exhaustive-deps` warning in `web/src/components/smart-plug-import/use-smart-plug-import-job.ts` (line 75, the upload effect's `[]`) is fixed **without** changing behaviour (no second upload on re-render or language switch).
4. **Given** the new analyzer findings, **when** this story is done, **then** each one is **fixed or suppressed with a justification** (see the triage table in Dev Notes). A suppression is either an `.editorconfig` line preceded by a `#` comment saying why, or a `[SuppressMessage(..., Justification = "...")]` on the one member. No `#pragma warning disable`, no `<NoWarn>` in a `.csproj` or `Directory.Build.props`.
5. **Implied (the gate must actually gate):** a `dotnet build` warning alone does not fail CI, so "enforced in CI" needs the PR build to fail on warnings. `pr-review.yml`'s "Build .NET solution" step builds with `-p:TreatWarningsAsErrors=true`, and `Directory.Build.props` keeps `NU1900` (audit could not run, e.g. registry outage) out of that with `<WarningsNotAsErrors>$(WarningsNotAsErrors);NU1900</WarningsNotAsErrors>`, preserving the existing "a transient registry outage shouldn't block every PR" decision in that file. Local builds and the Docker image build stay warnings-only (see "Why CI-only" in Dev Notes). The Release build of the whole solution ends with **0 warnings** at the close of this story (today: 19 without analyzers; about 1,550 with them).
6. **Implied (the gate must not rot, Story 11.x practice):** a new architecture guard `tests/EnergyTracker.Architecture.Tests/BackendAnalyzerGateIsConfiguredTests.cs` fails when: `Directory.Build.props` stops setting `AnalysisMode` to `Recommended` or `AnalysisModeSecurity` to `All`; `.editorconfig` is missing or not `root = true`; `pr-review.yml` loses the `dotnet format ... --verify-no-changes` step or the `TreatWarningsAsErrors=true` build argument; an `.editorconfig` line that lowers a rule (`severity = none|silent|suggestion`) has no `#` comment on the line directly above it; any `.cs` under `src` or `tests` contains `#pragma warning disable` or a `SuppressMessage` without `Justification =`; any `.csproj` or `Directory.Build.props` contains `<NoWarn>`. It proves itself non-vacuous the way `HouseholdImportIsStreamedTests` and `NoDirectClockReadsTests` do (known-bad text is detected, comments ignored where relevant).
7. **Implied (formatting baseline):** `dotnet format whitespace` finds 782 drifts in about 25 files today (mostly test files with mis-indented multi-line initializers). They are fixed in **one separate, mechanical commit** containing nothing else, and that commit's SHA is listed in a new root `.git-blame-ignore-revs` (with a one-line comment), so `git blame` skips it.
8. **Implied (docs and project memory):** the stale comment above "Lint frontend" in `pr-review.yml` ("No backend/.NET lint gate exists yet ...") is replaced; the comment block in `Directory.Build.props` gets a short second section explaining the analyzer settings; `_bmad-artifacts/project-context.md` gets one rule (the gate, how to suppress, where the guard lives) and its "Last Updated" bump; `docs/local-development.md` gets two lines on running `dotnet format` locally; `docs/codebase-audit-2026-10-10.md` is **not** edited (dated record). The 25 other oxlint warnings (see "Frontend lint today") are **out of scope** and are recorded as one `[open]` entry at the end of `_bmad-artifacts/implementation/deferred-work.md`.

## Tasks / Subtasks

Write each guard test red first and record the red run in Debug Log References (Story 11.1-11.17 practice). One PR on `feature/11-18-backend-analyzer-format-gate`. Docker must be running for the Infrastructure and Api test suites (Testcontainers).

- [x] Task 0: Baseline, before any change (AC: #3, #5)
  - [x] 0.1 `dotnet build EnergyTracker.sln -c Release --no-incremental`: record warning count (expected 19 distinct: 18 `xUnit1051`, 1 `CS8602` in `tests/EnergyTracker.Api.Tests/DataProtectionKeyProtectionTests.cs:98`; MSBuild prints each twice).
  - [x] 0.2 Same build with `-p:AnalysisMode=Recommended -p:AnalysisModeSecurity=All`: record the per-rule table and compare it with "Measured findings" below. If the counts differ a lot (new SDK feature band, Dependabot bump), triage the new rules with the same principles; do not silently suppress.
  - [x] 0.3 `dotnet format whitespace EnergyTracker.sln --verify-no-changes` and `dotnet format style ...` and `dotnet format analyzers ...`: record exit codes and counts (expected: whitespace exit 2 with 782 lines, style exit 0, analyzers exit 2 with 18 `xUnit1051`).
  - [x] 0.4 `dotnet test` per test project (Application, Architecture, Infrastructure, Api; there is no Domain test project) and `npm --prefix web run test`: record counts. Story 11.17's close: Application 605, Architecture 129, Infrastructure 566, Api 444.
  - [x] 0.5 `npm --prefix web run lint`: record (expected 0 errors, 26 warnings).
- [x] Task 1: Guard first (AC: #6)
  - [x] 1.1 Write `BackendAnalyzerGateIsConfiguredTests.cs` with the checks from AC #6 and self-tests (a props file without `AnalysisMode`, an `.editorconfig` lowering a rule with no comment above, a `.cs` text with `#pragma warning disable`, a `SuppressMessage` without justification, a workflow without the format step: each must be reported; the same text inside a `//` comment must not count for the `.cs` checks). Reuse the string-aware comment stripping that `NoDirectClockReadsTests` got in its 11.17 review (verbatim, raw and char literals) instead of writing a third copy: extract it into one internal helper in the Architecture test project if both guards need it, and keep `NoDirectClockReadsTests` green unedited in behaviour.
  - [x] 1.2 Run it: it must fail on the main repo (no `.editorconfig`, no `AnalysisMode`, no format step). Record in Debug Log.
- [x] Task 2: `.editorconfig` and `Directory.Build.props` (AC: #1, #4)
  - [x] 2.1 Root `.editorconfig` (shape in Dev Notes): `root = true`; `[*]` with `indent_style = space`, `end_of_line = lf`, `insert_final_newline = true`, `trim_trailing_whitespace = true`; `indent_size = 4` for `*.cs`, `*.csproj`, `*.props`; `indent_size = 2` for `*.{ts,tsx,js,mjs,json,css,html,yml,yaml}`; `[*.md] trim_trailing_whitespace = false`. **Do not** set `charset` for `*.cs`: 134 generated migration files and 6 `.csproj` files carry a UTF-8 BOM and the rest do not, so a `charset` line would make `dotnet format` rewrite them.
  - [x] 2.2 Mark the EF migrations as generated: `[src/EnergyTracker.Infrastructure.Migrations.*/Migrations/*.cs]` with `generated_code = true`. Only the `.Designer.cs` and snapshot files carry `<auto-generated>`; the migration bodies do not, which is why `CA1861` fires 22 times in them. Generated code is regenerated by `scripts/add-migration.sh`, so fixing it by hand is wrong. Verify after the change that `dotnet format` and the build no longer report anything under `Migrations/`.
  - [x] 2.3 Style options: write down the conventions the code already follows (file-scoped namespaces: all 227 `src` files already use them; `var` usage; braces) at `suggestion` or `silent` severity, never `warning`, so `dotnet format style` stays exit 0. Do **not** set `EnforceCodeStyleInBuild`. The point of this story is the CA gate plus whitespace, not a style rewrite (11.19 owns structural cleanup).
  - [x] 2.4 `Directory.Build.props`: add `<AnalysisMode>Recommended</AnalysisMode>`, `<AnalysisModeSecurity>All</AnalysisModeSecurity>`, and `<WarningsNotAsErrors>$(WarningsNotAsErrors);NU1900</WarningsNotAsErrors>` in a second `PropertyGroup` with its own comment. Keep the NuGet-audit group and its comment unchanged. No `TreatWarningsAsErrors` here (CI passes it, Task 5).
- [x] Task 3: Whitespace baseline commit (AC: #7)
  - [x] 3.1 With the `.editorconfig` in place, run `dotnet format whitespace EnergyTracker.sln`. Commit **only** that output, message like `style: apply dotnet format whitespace (Story 11.18)`. No hand edits in that commit.
  - [x] 3.2 Create `.git-blame-ignore-revs` with that commit's full SHA and a `#` comment line naming the story. Commit it separately (the SHA must exist first). Note in the PR description that GitHub's blame view honours this file automatically and local `git blame` needs `git config blame.ignoreRevsFile .git-blame-ignore-revs`.
  - [x] 3.3 Run the whole .NET test suite once after 3.1 (whitespace only, but the first pass in 11.17 showed scripted edits can surprise).
- [x] Task 4: Fix or suppress every finding (AC: #3, #4, #5)
  - [x] 4.1 `xUnit1051` (18 sites, list in Dev Notes): pass `TestContext.Current.CancellationToken`. Change only the token argument.
  - [x] 4.2 `CS8602` in `DataProtectionKeyProtectionTests.cs:98`: fix with a Shouldly null assertion (`ShouldNotBeNull()`) before the dereference, not with `!`.
  - [x] 4.3 Work through the triage table in Dev Notes, rule by rule, one commit per rule or small group, so review can follow it. Fixes in `src` must be behaviour-neutral (same public API surface, same log output text, same SQL).
  - [x] 4.4 After 4.3: `dotnet build EnergyTracker.sln -c Release --no-incremental -p:TreatWarningsAsErrors=true` succeeds with **0 warnings**, and `dotnet format EnergyTracker.sln --verify-no-changes` exits 0. Record both in Debug Log.
- [x] Task 5: CI gate (AC: #2, #5, #8)
  - [x] 5.1 `pr-review.yml`, "Build .NET solution": `dotnet build EnergyTracker.sln --no-restore --configuration Release -p:TreatWarningsAsErrors=true`. Update its comment in one or two lines (why warnings fail here, and that NU1900 is exempt in `Directory.Build.props`).
  - [x] 5.2 New step "Verify .NET formatting" right after the build step: `dotnet format EnergyTracker.sln --verify-no-changes --no-restore`. Before the frontend steps on purpose: it needs no `wwwroot` and fails fast (about 25 s locally).
  - [x] 5.3 Replace the "No backend/.NET lint gate exists yet ..." comment above "Lint frontend" with a one-line pointer to the two backend steps.
  - [x] 5.4 Do **not** touch `app-deploy.yml` (main is reached only through a PR that passed this gate; adding `TreatWarningsAsErrors` to deploy would let a new-SDK warning block a hotfix deploy) or the `Dockerfile` (analyzers do not change the published IL; `DockerImageMatchesTheVerifiedBuildTests` stays green unedited). Write that reasoning into the Completion Notes, not into the workflow.
  - [x] 5.5 The existing workflow guards (`WorkflowActionsArePinnedToCommitShaTests`, `WorkflowPreflightGuardTests`, `AppDeployWorkflowJobsGuardTests`) stay green unedited: no new action is added, only `run:` steps.
- [x] Task 6: Frontend `exhaustive-deps` fix (AC: #3)
  - [x] 6.1 Red first in `web/src/components/smart-plug-import/smart-plug-import-page.test.tsx` (the hook has no test file of its own; the page tests drive it): (a) switching the i18n language while an upload is in flight does **not** fire a second `POST` and does not abort the first; (b) a failed upload after a language switch shows the error in the **new** language. Test (b) is the one that proves the latest `t` is read. (a) passes today and must stay green; (b) should fail against a naive `[]`-only version that keeps the stale `t` only if the test switches language before the rejection arrives: make sure it does.
  - [x] 6.2 Fix: dependency array `[file]` (the `File` is stable per queue item: `smart-plug-import-page.tsx:159`, one hook instance per row, keyed per item), and read `t` through a latest-value ref updated in an effect (`const tRef = useRef(t); useEffect(() => { tRef.current = t }, [t])`), never by assigning `ref.current` during render (oxlint `react(refs)` already flags that pattern in `App.tsx`). Do **not** put `t` into the upload effect's dependencies: `t` changes identity on a language switch, which would abort and re-run the upload. Replace the "intentionally runs once per mount" comment with one line saying why `t` is read through the ref.
  - [x] 6.3 `npm --prefix web run lint`: the `exhaustive-deps` warning is gone and no new warning appears (expected 25). `npm --prefix web run test` green. The StrictMode single-upload test (`smart-plug-import-page.test.tsx:174`) stays green unedited.
- [x] Task 7: Docs and memory (AC: #8)
  - [x] 7.1 `Directory.Build.props` comment, `pr-review.yml` comments (Task 5).
  - [x] 7.2 `docs/local-development.md`: before pushing, run `dotnet format EnergyTracker.sln` (fixes whitespace and fixable analyzers) and `dotnet build -c Release -p:TreatWarningsAsErrors=true` to see what CI will see.
  - [x] 7.3 `_bmad-artifacts/project-context.md`: one rule under "Development Workflow Rules" (gate = Recommended + Security All, warnings fail only in `pr-review.yml`, `dotnet format --verify-no-changes` step, suppress only via commented `.editorconfig` line or justified `SuppressMessage`, never `#pragma`/`NoWarn`, migrations are `generated_code`, test names keep `Snake_case` because `CA1707` is off for `tests/**`, guard `BackendAnalyzerGateIsConfiguredTests`); bump "Last Updated". Write it in the file's existing style.
  - [x] 7.4 `deferred-work.md`: append at the **end** one `## Deferred from: create-story of 11-18-backend-analyzer-format-gate (2026-10-31)` section with one `[open]` entry for the 25 remaining oxlint warnings (rule names and counts from "Frontend lint today"), noting that `oxlint` does not fail on warnings today and a `--deny-warnings` gate would need them fixed first.
- [ ] Task 8: Final verification
  - [x] 8.1 Full .NET test suite and Vitest green; counts recorded (expected: Architecture grows by the new guard's tests, everything else unchanged plus Task 6's tests).
  - [ ] 8.2 Push the branch and confirm on the PR that `build-test-lint` runs the new step and passes. Then prove the gate bites: push a throwaway commit that adds one mis-indented line and one `CA1305`-violating call, see the job fail on both steps, and revert it. Record the run URLs in Debug Log.

### Review Findings

- [x] [Review][Patch] CA1873: remove the ~11 member-wide `SuppressMessage` attributes; add one commented `.editorconfig` line for CA1873 (decided D1 c) [src/**; .editorconfig]
- [x] [Review][Patch] Merge PR with a merge commit or rebase (not squash) so `4050379` survives in `.git-blame-ignore-revs`; note in PR description (decided D2 a) [.git-blame-ignore-revs:2] — recorded here; do at merge time, not code.
- [x] [Review][Patch] Add `deferred-work.md` entry for rules disabled in `.editorconfig` (CA1848 ~60 sites, CA1716, CA1707, CA1816, CA1711 suffixes) (decided D3 b) [.editorconfig; deferred-work.md]
- [ ] [Review][Patch] Task 8.2 open: push and prove the gate fails on a throwaway violation; commit story file and `sprint-status.yaml` [story Task 8] — PARTIAL: local proof done (probe file failed `dotnet build -p:TreatWarningsAsErrors=true` with CA1305, then removed). Push of a throwaway failing commit and committing story/sprint files left to Ralf (outward-facing).
- [x] [Review][Patch] `SuppressMessage` guard regex misses `[Fact, SuppressMessage(...)]`, `[module: ...]`, `]` inside Justification, and accepts whitespace-only Justification; add self-tests [tests/EnergyTracker.Architecture.Tests/BackendAnalyzerGateIsConfiguredTests.cs:1721-1728]
- [x] [Review][Patch] Guard misses other bypasses: `RunAnalyzers`/`EnableNETAnalyzers=false`, `AnalysisMode*`, per-project `TreatWarningsAsErrors`, `WarningsNotAsErrors` beyond NU1900, nested `.editorconfig`/`.globalconfig`, `severity = default`, `NoWarn` in `*.props`/`*.targets` [BackendAnalyzerGateIsConfiguredTests.cs:1633-1719]
- [x] [Review][Patch] Workflow guard accepts `continue-on-error: true`, `|| true`, trailing `# comment` and `if: false` on the format/build steps [BackendAnalyzerGateIsConfiguredTests.cs:1664-1681; .github/workflows/pr-review.yml:43-53]
- [x] [Review][Patch] `CSharpFiles` skips any `Migrations` path segment; mirror the `.editorconfig` glob `src/EnergyTracker.Infrastructure.Migrations.*/Migrations/` [BackendAnalyzerGateIsConfiguredTests.cs:1736-1741]
- [x] [Review][Patch] `FirstCellText`: `Cells.First()` to `Cells[0]` changes empty-row exception type (`InvalidOperationException` to `ArgumentOutOfRangeException`). Caller path ends in generic `catch (Exception)` at `BackgroundJobProcessor.cs:104`, so impact is low; add an empty-row test or restore behaviour [src/EnergyTracker.Infrastructure/Adapters/EveHomeXlsxParser.cs:394]
- [x] [Review][Patch] `smart-plug-import-page.test.tsx` `afterEach` forces `en-US`; capture and restore the initial language instead [web/src/components/smart-plug-import/smart-plug-import-page.test.tsx:50-57]
- [x] [Review][Patch] Docs command differs from CI (`--no-restore`, `EnergyTracker.sln`); `Directory.Build.props` comment wording garbled and duplicates `project-context.md` [Directory.Build.props; docs/local-development.md]
- [x] [Review][Defer] No `.gitattributes`; `end_of_line = lf` fails `dotnet format --verify-no-changes` on Windows autocrlf checkouts [.editorconfig:1-15] — deferred, pre-existing
- [x] [Review][Defer] `CSharpSource.StripComments` gaps (`\u0041` char escapes, `$"""`), `root = true` regex rejects trailing comment, magic `Count > N` thresholds [tests/EnergyTracker.Architecture.Tests/CSharpSource.cs:1830-1849] — deferred, pre-existing
- [x] [Review][Defer] `WarningsNotAsErrors` (NU1900) vs `WarningsAsErrors` (NU1902-NU1904) interaction under `-p:TreatWarningsAsErrors=true` untested; NU1901 unaddressed [Directory.Build.props] — deferred, pre-existing
- [x] [Review][Defer] `global.json` `rollForward: latestFeature` lets a new SDK band add Recommended rules and fail unrelated PRs [global.json] — deferred, pre-existing

## Dev Notes

### Why it exists

The 2026-10-10 audit rated readability *Fair*, partly because the backend has no analyzer gate (R3): no `.editorconfig`, no analysis mode, no format check, and `TreatWarningsAsErrors` only for the NuGet-audit codes. Story 11.17's guard work showed the cost: each invariant needed a hand-written text guard because nothing else checks code shape. This story adds the cheap, generic layer: Microsoft's own CA rules at `Recommended`, all security rules, whitespace consistency, and a CI step that fails on drift.

### Analyzer facts (SDK 10.0.403 locally, `global.json` pins `10.0.100` with `latestFeature`)

- The .NET SDK ships the analyzers (`Microsoft.CodeAnalysis.NetAnalyzers`). Enabling them needs **no package**: `AnalysisMode` in MSBuild is enough. `AnalysisMode` values: `None`, `Default`, `Minimum`, `Recommended`, `All`. Per-category overrides exist: `AnalysisModeSecurity`, `AnalysisModeReliability`, `AnalysisModePerformance`, etc.
- `Recommended` turns many CA rules into warnings but leaves most CA3xxx/CA5xxx security rules off. Measured here: `Recommended` alone produced **zero** CA2xxx/CA3xxx/CA5xxx findings; adding `AnalysisModeSecurity=All` produced 5 (all in tests, below). So `AnalysisModeSecurity=All` is what makes AC #1's "security rules at least as warnings" true.
- `dotnet format` is part of the SDK (`dotnet format --version` → `10.0.403-servicing...`). With no subcommand it runs `whitespace`, `style` and `analyzers`; `--severity` defaults to `warning`, so it fails verification on any **fixable** diagnostic at warning level, and on any whitespace drift. Non-fixable warnings (for example `CA1848`) are not caught by `dotnet format`; that is what the CI `TreatWarningsAsErrors` build is for. Both are needed.
- `--no-restore` works after the "Restore .NET solution" step. `dotnet format` reads `.editorconfig`, including `generated_code`.
- Severity in `.editorconfig`: `dotnet_diagnostic.CAxxxx.severity = none|silent|suggestion|warning|error`. Rule options: `dotnet_code_quality.CA1711.allowed_suffixes`, `dotnet_code_quality.CA1822.api_surface`. Section globs are matched against the file path relative to the `.editorconfig` (`[tests/**.cs]`, `[src/EnergyTracker.Infrastructure.Migrations.*/Migrations/*.cs]`).
- `TreatWarningsAsErrors=true` plus `WarningsNotAsErrors=NU1900`: NuGet replays restore warnings (NU190x) during build from `project.assets.json`, so without the exemption a registry outage during restore would fail the build step too. `NU1902`-`NU1904` are already errors via `WarningsAsErrors`, unchanged.

### Measured findings at HEAD `d1b7792` (Release, `Recommended` + `AnalysisModeSecurity=All`, distinct sites)

| Rule | Sites | Where | Decision |
|---|---|---|---|
| CA1707 identifiers contain underscores | 1,358 | tests only (Api 363, Application 509, Architecture 98, Infrastructure 388) | **Suppress** for `[tests/**.cs]`: the project's test-naming rule is `Snake_case_with_underscores` sentences (project-context, Testing Rules). |
| CA1848 use `LoggerMessage` delegates | 61 | src: Api 10, Application 12, Infrastructure 39 | **Suppress** globally: low-volume household app, all calls already use message templates (structured, no interpolation); source-generated logging across 61 sites is churn without user value. Revisit if a hot path appears. |
| CA1873 avoid expensive log argument evaluation | 13 | src (`DataProtectionKeyProtection`, `HouseholdExportEndpoints`, `Program.cs:485`, `SessionLifetime`, `RemoveHouseholdMember`, `BackgroundJobProcessor`, `InProcessJobStartupSweep`, `InstanceHeartbeatService`) | **Fix** where the argument is a real computation (a method call, `string.Join`, LINQ); **suppress per site** with `SuppressMessage` where it is a property read (no cost). Look at each; do not wrap logs in `IsEnabled` blocks just to silence the rule. |
| CA1816 call `GC.SuppressFinalize` | 45 | tests only (`DisposeAsync` of `IAsyncLifetime` classes) | **Suppress** for `[tests/**.cs]`: test fixtures have no finalizers and are not subclassed. |
| CA1861 constant arrays as arguments | 23 | 22 in migrations, 1 in Infrastructure.Tests | Migrations: covered by `generated_code = true` (Task 2.2). Test: **fix** (static readonly field). |
| CA1822 member can be static | 11 | src 2 (`EventTaggedEntityNotFoundException.ErrorCode`, `ValidateHouseholdImport.Execute`), tests 9 | Set `dotnet_code_quality.CA1822.api_surface = private, internal` (public use-case methods stay instance methods: DI and the one-use-case-class convention). **Fix** the remaining private/internal hits that this still reports. |
| CA1859 use concrete return type | 8 | src 4 (`EveHomeXlsxParser.ReadSharedStrings`, `SmartPlugImportRepository.DeduplicateByMatchKey`, `...UpsertAwaitingMappingReadings{Postgres,SqlServer}Async`), tests 4 | **Fix** (private methods; no API change). `SmartPlugImportRepository` gets split in 11.19; keep this change to the signatures. |
| CA1305 specify `IFormatProvider` | 8 | src 1 (`Program.cs:47` Serilog `WriteTo.Console`), tests 7 | **Fix** with `CultureInfo.InvariantCulture` (`formatProvider:` for Serilog). Log output is not locale-dependent (AD-18 is about UI, not logs). |
| CA1716 identifier is a keyword | 4 | `Domain/Event.cs` (type `Event`), `IEventRepository` (`event`, `to` parameters), `ISmartPlugCoverageSignal` (`to`) | **Suppress** globally: only VB/other-language consumers matter for this rule; nothing consumes these assemblies outside this solution, and renaming the domain `Event` type is out of all proportion. |
| CA1711 type name suffix | 4 | `IBackgroundJobQueue`, `AzureStorageQueueJobQueue`, `InProcessChannelJobQueue`, `HouseholdExportStream` | `dotnet_code_quality.CA1711.allowed_suffixes = Queue\|Stream` with a comment (AD-6 port names). No rename. |
| CA1869 cache `JsonSerializerOptions` | 4 | tests | **Fix** (static readonly). |
| CA1875 use `Regex.Count` | 3 | Architecture.Tests | **Fix**. |
| CA1852 seal type | 3 | tests | **Fix** (`sealed`). |
| CA1826 indexer instead of LINQ | 1 | `EveHomeXlsxParser.cs:394` | **Fix**; keep behaviour (check empty-collection semantics of the LINQ call you replace, e.g. `First` vs `[0]` both throw, `FirstOrDefault` does not). |
| CA1068 `CancellationToken` last | 1 | `SmartPlugImportRepository.AddAsyncCore` (private) | **Fix** (reorder parameter, update its callers). |
| CA1001 type owns disposable field | 1 | `HouseholdRecomputeLock.Entry` (`SemaphoreSlim`) | **Suppress per site** with `SuppressMessage`: the semaphore never exposes `AvailableWaitHandle`, so it holds no unmanaged resource; disposing it on eviction would add a race surface to the reference-counted lock Story 11.17 just made safe. |
| CA2100 SQL from variable | 2 | `tests/Shared/SharedDatabaseServers.cs:155,212` (`CREATE`/`DROP DATABASE` with generated names) | **Suppress per site** with `SuppressMessage`: database names are generated GUID-based identifiers, never input. |
| CA5394 insecure `Random` | 3 (one line) | `tests/EnergyTracker.Api.Tests/RateLimitingTests.cs:45` | **Fix** by generating the value with `RandomNumberGenerator.GetInt32`, or suppress per site if the test needs a seed; prefer the fix. |
| xUnit1051 | 18 | see list below | **Fix** (Task 4.1). |
| CS8602 | 1 | `DataProtectionKeyProtectionTests.cs:98` | **Fix** (Task 4.2). |

`src` is clean of every CA2xxx/CA3xxx/CA5xxx security rule. Good news worth one line in the PR description, and the reason the security gate can be `warning` + CI `TreatWarningsAsErrors` from day one.

`xUnit1051` sites: `tests/EnergyTracker.Api.Tests/HouseholdMembershipMiddlewareTests.cs` lines 64, 78, 109, 113, 121, 125, 138; `tests/EnergyTracker.Application.Tests/CreateMeterReadingTests.cs:408`; `EditMeterReadingTests.cs:201,202`; `RequeueEventCorrelationsTests.cs:211`; `ResolveMeterRegressionPromptTests.cs:245,269`; `ValidateHouseholdImportTests.cs:859,875,947`; `tests/EnergyTracker.Infrastructure.Tests/InstanceHeartbeatServiceTests.cs:164,165`. The epic's "6" was the audit's count on 2026-10-10; stories since then added the rest. Story 11.17 explicitly said not to add new ones.

### Why CI-only `TreatWarningsAsErrors`

- In `Directory.Build.props` it would also apply to the `Dockerfile`'s `dotnet publish`, to `app-deploy.yml`, and to every local build. A new SDK feature band (`rollForward: latestFeature`) can introduce new CA rules; that must fail a PR, where someone can react, not a deploy or a hotfix build.
- `pr-review.yml` is the required check on `main` (`build-test-lint`, no `name:` override), so every change to `main` passes it. That is the enforcement point the AC asks for.
- Locally, warnings show in the IDE and build output; Task 7.2 documents the one-line command to reproduce CI.
- Alternative Ralf may prefer: `TreatWarningsAsErrors` in `Directory.Build.props` under `Condition="'$(CI)' == 'true'"` (GitHub sets `CI=true`, and MSBuild sees environment variables as properties). That would also cover `app-deploy.yml`. Not chosen, for the hotfix reason above.

### `.editorconfig` shape (adapt, keep comments short)

```ini
# Editor and analyzer settings for the whole repo (Story 11.18, audit R3).
# Lowering a rule needs a comment on the line above it (BackendAnalyzerGateIsConfiguredTests).
root = true

[*]
indent_style = space
end_of_line = lf
insert_final_newline = true
trim_trailing_whitespace = true

[*.{cs,csproj,props,targets}]
indent_size = 4

[*.{ts,tsx,js,mjs,json,css,html,yml,yaml}]
indent_size = 2

[*.md]
trim_trailing_whitespace = false

[*.cs]
# Conventions the code already follows, as hints only (the gate is CA rules plus whitespace).
csharp_style_namespace_declarations = file_scoped:suggestion
# ... further existing conventions at suggestion/silent ...

# Message templates are already structured; LoggerMessage source generation across ~60 low-volume call sites is not worth it.
dotnet_diagnostic.CA1848.severity = none
# Nothing outside this solution consumes these assemblies; renaming the domain Event type is out of proportion.
dotnet_diagnostic.CA1716.severity = none
# AD-6 port and adapter names end in Queue; HouseholdExportStream is the export's streaming writer.
dotnet_code_quality.CA1711.allowed_suffixes = Queue|Stream
# Public use-case methods stay instance methods (DI, one use case per class).
dotnet_code_quality.CA1822.api_surface = private, internal

[tests/**.cs]
# Test names are Snake_case sentences by project convention.
dotnet_diagnostic.CA1707.severity = none
# Test fixtures have no finalizers and are never subclassed.
dotnet_diagnostic.CA1816.severity = none

[src/EnergyTracker.Infrastructure.Migrations.*/Migrations/*.cs]
# EF migrations are generated by scripts/add-migration.sh; never hand-edited for style.
generated_code = true
```

Check two things after writing it: `dotnet format style` still exits 0, and `.editorconfig`'s `end_of_line = lf` does not make `dotnet format` touch non-C# files (it formats only files in the solution's projects). The 6 CRLF files in the repo (`launchSettings.json`, `appsettings.Development.json`, two `xunit.runner.json`, two `.claude` CSVs) are not C#; leave them as they are, an editor may convert them later.

### Frontend: `use-smart-plug-import-job.ts`

- Current state: the upload effect (lines 43-75) uses `file` and `t` but has `[]`, with a comment saying it intentionally runs once per mount. The polling effect (77-141) already lists `[state, t]` correctly.
- The trap: adding `t` to the upload effect's dependencies. `useTranslation()` returns a new `t` when the language changes (Story 8.11's language toggle), so the effect's cleanup would abort the in-flight `fetch` and start a **second upload** of the same file: exactly the double-upload that the `AbortController` comment and the StrictMode test guard against.
- The fix: `[file]` plus a latest-value ref for `t`, updated in an effect. `file` never changes for one hook instance, so `[file]` behaves like `[]` and satisfies the rule honestly.
- `smart-plug-import-page.test.tsx` already mocks `fetch` per URL and checks upload counts (`:174` StrictMode test); copy that harness for Task 6.1. i18n in tests: look at how other component tests switch language (search for `changeLanguage` under `web/src`).

### Frontend lint today (26 warnings, 0 errors; the audit saw 4)

oxlint 1.75 added React Compiler-style rules since the audit: `react(set-state-in-effect)` 17, `react(refs)` 5 (`App.tsx` 4, `tagging-scaffold-manager.tsx` 1), `react(only-export-components)` 3 (`household-size-preset-row.tsx`, `ui/button.tsx`, `ui/badge.tsx`), `react(purity)` 1 (`edit-tariff-dialog.tsx:69`, `Date.now` in render), `react-hooks(exhaustive-deps)` 1 (this story). Only the last is in scope (AC #3). The rest go to `deferred-work.md` (Task 7.4). Do not change `.oxlintrc.json`.

### Testing standards (this repo)

- xunit.v3 (MTP), Shouldly, NSubstitute; test names are `Snake_case` sentences; `TestContext.Current.CancellationToken` for every token (the reason for this story's xUnit1051 work; keep it at zero).
- Architecture guards read files as text from the repo root, strip comments where relevant, assert they scanned something real, and prove they are non-vacuous with inline known-bad samples. Follow `NoDirectClockReadsTests` (string-aware comment stripping after its 11.17 review fix) and `DockerImageMatchesTheVerifiedBuildTests` (workflow/Dockerfile parsing and failure-message style).
- Frontend: Vitest + Testing Library, tests next to the source, `fetch` mocked per URL.

### Project Structure Notes

- New files: `.editorconfig`, `.git-blame-ignore-revs` (repo root), `tests/EnergyTracker.Architecture.Tests/BackendAnalyzerGateIsConfiguredTests.cs`, possibly one internal helper in the Architecture test project for comment stripping (Task 1.1).
- Edited: `Directory.Build.props`, `.github/workflows/pr-review.yml`, files from the triage table, the 18 `xUnit1051` test files, `use-smart-plug-import-job.ts` and `smart-plug-import-page.test.tsx`, `docs/local-development.md`, `_bmad-artifacts/project-context.md`, `deferred-work.md`, `sprint-status.yaml`, plus the mechanical whitespace commit (about 25 files, mostly tests, plus `src/EnergyTracker.Application/CreateEvent.cs`).
- Not edited: `Dockerfile`, `app-deploy.yml`, any `.csproj` (no `NoWarn`, no per-project `AnalysisMode`), `.oxlintrc.json`, migrations (except through `generated_code`), `docs/codebase-audit-2026-10-10.md`.
- Variance: the epic counts 6 `xUnit1051`; the real count is 18. The AC's intent (no `xUnit1051` left) is what Task 4.1 does; the number is not asserted anywhere. This is an implied requirement, not an AC amendment.

### References

- `_bmad-artifacts/planning/epics/epic-11-codebase-audit-hardening.md#story-1118-backend-analyzer-and-format-gate`
- `_bmad-artifacts/planning/sprint-change-proposal-2026-10-10.md` (line 47: "an analyzer/format gate (11.18)"; Story 11.18 at line ~595)
- `docs/codebase-audit-2026-10-10.md` (R3 line ~329; "Other minor items" lines ~263-266; build table line ~339)
- `_bmad-artifacts/project-context.md` (Testing Rules; Development Workflow Rules: CI, NuGet audit gate, `build-test-lint` has no `name:` override)
- `Directory.Build.props` (NuGet-audit gate and its NU1900 decision)
- `.github/workflows/pr-review.yml` (build-test-lint steps)
- `tests/EnergyTracker.Architecture.Tests/NoDirectClockReadsTests.cs`, `DockerImageMatchesTheVerifiedBuildTests.cs`, `HouseholdImportIsStreamedTests.cs` (guard patterns)
- `_bmad-artifacts/implementation/11-17-timeprovider-recompute-lock-eviction.md` (previous story; `HouseholdRecomputeLock` design; "no analyzer work: that is 11.18")
- Microsoft Learn: "Code analysis in .NET" (`AnalysisMode`, `AnalysisMode<Category>`), "Code quality rule configuration options" (`api_surface`, `allowed_suffixes`), "dotnet format" command reference.

### Previous story intelligence (11.17 and the 11.x run)

- 11.17 left the build at exactly 19 warnings (18 `xUnit1051`, 1 `CS8602`) and said so; this story takes it to 0. 11.17's Dev Notes deferred all analyzer work here and the `Program.cs` split to 11.19: do not split `Program.cs` here, even though `CA1873`/`CA1305` touch it.
- 11.17 review lessons that apply: guard tests must be non-vacuous and string-aware (raw `"""..."""`, verbatim `@"..."` and char `'"'` literals exist in `src`); do not widen scope; keep comments factual and short (11.19 will trim history-narrating comments, so do not add new ones).
- 11.17's first scripted pass over tests corrupted nested expressions and had to be reverted. For the `xUnit1051` fixes and the whitespace pass, use `dotnet format` itself (it has a fixer for `xUnit1051`: `dotnet format analyzers --diagnostics xUnit1051`) rather than a regex script, then review the diff.
- Practice since 11.1: red first, red run recorded, existing tests unedited where possible, one PR, project-context bump, a guard per invariant meant to stay true.

### Git intelligence

Recent commits: `d1b7792` (11.17, touched about 60 `src`/`tests` files: rebase risk only if 11.17 follow-ups land), `2e19e9c`/`4892ab1`/`77988d3` (Dependabot npm, NuGet, actions), `3d03add` (alert rule). Dependabot NuGet bumps can raise the SDK analyzers' behaviour only through the SDK, not packages (the analyzers ship in the SDK). A Dependabot PR opened after this story merges must pass the new gate; that is intended.

### Latest technical information

- Analyzers ship with the .NET 10 SDK; `AnalysisMode`/`AnalysisMode<Category>` are the supported switches (no `Microsoft.CodeAnalysis.NetAnalyzers` package needed, and adding one would duplicate the SDK's).
- `CA1873` is new in the .NET 10 analyzers; `CA1875` (`Regex.Count`) too. Both appear only because the SDK is 10.x.
- `dotnet format` in SDK 10: `--verify-no-changes` exits 2 on drift; `--no-restore` skips the implicit restore; `--severity` default `warning`; `--diagnostics <ids>` limits analyzers to listed ids (useful for the `xUnit1051` fix pass).
- GitHub honours `.git-blame-ignore-revs` in the blame view automatically.

### Project context reference

All rules in `_bmad-artifacts/project-context.md` apply. Most relevant: Testing Rules (naming, Shouldly, `TestContext.Current.CancellationToken`), Development Workflow Rules (CI job id `build-test-lint`, NuGet-audit gate, actions pinned by SHA, Node version pairing), "Process gates" (spec/story link for any fix; this story is that link), and "Amend ACs at decision time" (if Ralf overrules the CI-only `TreatWarningsAsErrors` or a suppression, mark the AC `AMENDED` in this file).

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5.5 (claude-sonnet-5-5)

### Debug Log References

- Baseline (HEAD d1b7792): Release build 19 warnings (18 xUnit1051, 1 CS8602); analyzers on: 1,358 CA1707, 61 CA1848, 45 CA1816, 23 CA1861, 13 CA1873, 11 CA1822, 8 CA1859, 8 CA1305, 4 CA1869/1716/1711, 3 CA5394/1875/1852, 2 CA2100, 1 CA1826/1068/1001 (matches story). `dotnet format`: whitespace exit 2 (782 lines), style exit 0, analyzers exit 2 (18).
- Guard red run (before .editorconfig/props/workflow): `Directory_Build_props_turns_the_analyzers_on`, `The_editorconfig_is_the_repo_root_and_every_lowered_rule_says_why` (FileNotFoundException), `The_PR_workflow_builds_with_warnings_as_errors_and_verifies_formatting` failed; 144 others green.
- Frontend red run: `shows an upload failure that arrives after the switch in the new language` failed against the `[]` effect (stale `t`); `does not start a second upload and does not abort the first` was green before and after.
- Final: `dotnet build -c Release --no-incremental -p:TreatWarningsAsErrors=true` 0 warnings 0 errors; `dotnet format EnergyTracker.sln --verify-no-changes --no-restore` exit 0.
- Tests: .NET 1762 passed, 0 failed (4 projects); Vitest 768 passed (70 files); oxlint 0 errors, 26 warnings (story expected 25; the story's per-rule table sums to 26 without exhaustive-deps, which is gone).
- Whitespace commit: 40503794f3f4d96983a03f5c69e55e7a559c9cf1.

### Completion Notes List

- Analyzer gate: AnalysisMode Recommended + AnalysisModeSecurity All in Directory.Build.props; root .editorconfig (CA1848/CA1716 off globally, CA1707/CA1816 off for tests, migrations generated_code, CA1711 suffixes, CA1822 api_surface), each lowering commented.
- Decisions: `CA1873` suppressed per member with justified `SuppressMessage` (boxing/property reads on always-enabled levels). Program.cs top-level logging moved to `AiPlausibilityBackendConfiguration.LogSelection` because attributes cannot sit on top-level statements. `CA1001` suppressed on `HouseholdRecomputeLock.Entry`, `CA2100` on the two `SharedDatabaseServers.ExecuteAsync`. `EveHomeXlsxParser.FirstCellText` now uses `Cells[0]`: an empty row throws ArgumentOutOfRangeException instead of InvalidOperationException (both unhandled).
- Guard scans skip `Migrations` folders for the pragma rule (EF-generated files carry `#pragma warning disable 612, 618`) and limit the NoWarn scan to src/tests plus Directory.Build.props (`spikes/` is outside the solution). Comment stripper extracted to `CSharpSource`.
- CI-only warnings-as-errors; `app-deploy.yml` and Dockerfile untouched: main is reached only through a PR that passed this gate, and a new-SDK warning must not block a hotfix deploy; analyzers do not change published IL.
- Open: Task 8.2 (push branch, confirm `build-test-lint` runs the new step, prove the gate bites with a throwaway commit) needs a push and was not done without the user's approval.

### File List

- .editorconfig (new), .git-blame-ignore-revs (new), Directory.Build.props, .github/workflows/pr-review.yml
- tests/EnergyTracker.Architecture.Tests/BackendAnalyzerGateIsConfiguredTests.cs (new), CSharpSource.cs (new), NoDirectClockReadsTests.cs
- Whitespace commit: 24 files under src/ and tests/ (mechanical)
- src: Api (AiPlausibilityBackendConfiguration, DataProtectionKeyProtection, HouseholdExportEndpoints, Program, SessionLifetime), Application/RemoveHouseholdMember, Infrastructure/Adapters (BackgroundJobProcessor, EveHomeXlsxParser, HouseholdRecomputeLock, InProcessJobStartupSweep, InstanceHeartbeatService, SmartPlugImportRepository)
- tests: xUnit1051/CA fixes across Api.Tests, Application.Tests, Architecture.Tests, Infrastructure.Tests, Shared/SharedDatabaseServers.cs
- web/src/components/smart-plug-import/use-smart-plug-import-job.ts, smart-plug-import-page.test.tsx
- docs/local-development.md, _bmad-artifacts/project-context.md, _bmad-artifacts/implementation/deferred-work.md, sprint-status.yaml, this story file

### Change Log

- 2026-10-31: Backend analyzer and format gate added; findings fixed or justified; exhaustive-deps fixed without behaviour change.
