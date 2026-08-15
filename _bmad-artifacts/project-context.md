---
project_name: 'energy-tracker'
user_name: 'Ralf'
date: '2026-08-15'
sections_completed: ['technology_stack', 'language_rules', 'framework_rules', 'testing_rules', 'quality_rules', 'workflow_rules', 'anti_patterns']
status: 'complete'
rule_count: 79
optimized_for_llm: true
---

# Project Context for AI Agents

_This file contains critical rules and patterns that AI agents must follow when implementing code in this project. Focus on unobvious details that agents might otherwise miss._

---

## Technology Stack & Versions

**Backend:** .NET 10 (LTS to Nov 2028), ASP.NET Core 10 Minimal APIs, EF Core 10.
**Frontend:** React 19.2, Vite 8.2, TypeScript ~6.0, Tailwind CSS v4, shadcn/ui + Radix.

**Key dependencies:**
- Persistence: Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3 (Postgres 17 self-host) / Microsoft.EntityFrameworkCore.SqlServer 10.0.10 (Azure SQL Basic DTU) — dual-provider, config-selected.
- Auth: Microsoft.AspNetCore.Authentication.OpenIdConnect 10.0.10 + cookie auth, Microsoft.AspNetCore.DataProtection.EntityFrameworkCore 10.0.10.
- Observability: Serilog.AspNetCore 10.0.0, OpenTelemetry.* 1.17.0 (`OpenTelemetry.Instrumentation.EntityFrameworkCore` is pinned to `1.17.0-beta.1` deliberately — no stable release exists upstream).
- .NET testing: xunit.v3.mtp-v2 3.2.2, Shouldly 4.3.0, NSubstitute 6.1.0, Testcontainers.PostgreSql/MsSql 4.13.0, Microsoft.AspNetCore.Mvc.Testing 10.0.10.
- Frontend: i18next 26.x + react-i18next 17.x, class-variance-authority, tailwind-merge, lucide-react.
- Frontend testing: Vitest 4.1, @testing-library/react 16.3, @playwright/test 1.62, oxlint 1.75 (not ESLint).

**Version constraints:**
- SDK pinned via `global.json` to `10.0.100`, `rollForward: latestFeature`.
- All NuGet versions are centrally managed in `Directory.Packages.props` (`ManagePackageVersionsCentrally=true`) — never add `Version=` to a `<PackageReference>` in a `.csproj`; bump the version in `Directory.Packages.props` instead.
- EF Core packages (EFCore, EFCore.Design, EFCore.Relational, EFCore.SqlServer) are pinned to the identical `10.0.10` line to avoid transitive version conflicts — bump together, never individually.
- `xunit.v3.mtp-v2` runs on Microsoft.Testing.Platform (`global.json` `test.runner`), not the classic VSTest runner — test invocation differs from xUnit v2 projects.

## Critical Implementation Rules

### Language-Specific Rules

**C# (nullable/implicit usings enabled across all projects):**
- File-scoped namespaces, primary constructors for DI (e.g. `public class CreateDevice(ITaggingScaffoldRepository repository)`), one use-case class per file — no feature-folder nesting under `Application/` (flat: `CreateDevice.cs`, `ArchiveRoom.cs`, etc.).
- Use cases are named as imperative verbs (`CreateHousehold`, `RenameDevice`, `AcceptHouseholdInvite`) with a single `ExecuteAsync(...)` method — not a generic `Handler`/CQRS mediator pattern.
- Validation throws typed domain exceptions (`TaggingScaffoldValidationException`, `HouseholdValidationException`, `*NotFoundException`, `*ArchivedException`) rather than returning result objects or throwing generic `ArgumentException`.
- `Nullable` and `ImplicitUsings` are `enable` — don't add explicit `using System;` etc., and treat nullable warnings as real (no `!`-suppressing without reason).
- Ports live in `Application/Ports` named `I{Capability}`; adapters live in `Infrastructure/Adapters` named `{Vendor}{Capability}` — never place an interface and its implementation in the same project.
- **AD-1 (Ports & Adapters direction):** Domain has zero external package refs beyond the BCL. Application defines interfaces only — never references `Infrastructure` or `Api`. All framework/vendor packages live in `Infrastructure`/`Api`. Enforced by `tests/EnergyTracker.Architecture.Tests` (`DomainHasNoExternalDependenciesTests.cs`) — a violation fails the test suite, not just a convention.

**TypeScript/Frontend:**
- `verbatimModuleSyntax: true` — type-only imports must use `import type { Foo }`, not a plain `import { Foo }` that gets erased.
- `noUnusedLocals` + `noUnusedParameters` + `erasableSyntaxOnly` are on — unused bindings and non-erasable TS syntax (e.g. enums, parameter properties) fail the build (`tsc -b`).
- Path alias `@/*` → `web/src/*` — use `@/components/...` imports, not relative `../../` chains, for anything outside the same folder.
- Linting is **oxlint**, not ESLint — don't propose `.eslintrc` config or ESLint-specific rule names.
- `allowImportingTsExtensions` + `noEmit` — this is a bundler-mode (Vite) TS setup; don't assume CommonJS/`tsc`-emit conventions.

### Framework-Specific Rules

**ASP.NET Core / EF Core (backend) — these are architecture-spine invariants, not preferences:**
- **Config-driven adapter selection:** every swappable capability (DB provider, job queue, AI backend, OIDC) is selected by exactly **one** config value read once at the composition root (`Program.cs`). No feature code branches on environment or provider elsewhere.
- **Tenant isolation (AD-3):** every Household-scoped entity carries `HouseholdId`; `EnergyTrackerDbContext.OnModelCreating` applies a global query filter from `ICurrentHouseholdAccessor`. Never write per-handler/per-repository household filtering, and never use `DbSet<T>.Find()`, `FromSqlRaw`, or `.IgnoreQueryFilters()` against a Household-scoped entity — that's the specific bypass this rule exists to close.
- **Dual-provider persistence (AD-2):** one shared `EnergyTrackerDbContext`, never a per-provider subclass. Only the portable relational subset (`string`, `int`, `decimal`, `DateTimeOffset`, `bool`, `byte[]`, standard LINQ) — no Postgres `jsonb`/`ILike`, no SQL Server `rowversion`, no provider-specific column mapping. A migration is added to **both** provider projects in the same commit via `scripts/add-migration.sh <Name>`, never to one alone.
- **Optimistic concurrency (AD-4):** concurrency-sensitive entities (Meter Reading, Tariff, Household settings) carry a plain `int Version` EF concurrency token — never a provider-specific mechanism (`rowversion`/`xmin`). Conflicts throw `DbUpdateConcurrencyException` → HTTP 409 with current server state; never last-write-wins or silent merge.
- **Compute-at-request-time vs. persisted history (AD-7):** current Status/Reminder are pure synchronous computations on every read — never precomputed by a background schedule (`IHostedService`/`Timer` doesn't survive scale-to-zero). Every time Status is recomputed, it's also written to an immutable `StatusSnapshot` row via the **one** `IStatusRecomputeService` — don't build a second snapshot writer. History reads (Trend History) always read persisted `StatusSnapshot`, never a live recompute.
- **Async jobs (AD-6):** one `IBackgroundJobQueue` port, payload is always a plain JSON-serializable record (`JobEnvelope<TPayload>`) — never a delegate/closure-based executor. Clients learn a job finished by **polling** `GET /api/jobs/{id}`, never WebSocket/SSE.
- **Main Meter is sole authoritative total (AD-14):** no domain code, service, DTO, or frontend view may sum `SmartPlugReading`/`Event` data into a figure compared against or rendered alongside the Main Meter total. No `Residual` type/field/DTO/view anywhere.
- **No hardcoded household-specific values (AD-15):** every household-specific value (baselines, thresholds, currency, locale) is a `Household`-scoped config row, never a literal in code.
- **Idempotent Meter Reading writes (AD-16):** creation carries a client-generated idempotency key (GUID) set before any network attempt; the API upserts by that key.
- **Auth (AD-17):** cookie auth chained to OIDC — the SPA never reads or stores a token itself. Data Protection keys persist via `PersistKeysToDbContext`, never the in-memory default (doesn't survive scale-to-zero).
- **Single deployment artifact (AD-13):** the `Api` project serves the built SPA from `wwwroot/` — don't introduce a separate static-hosting deployment path.
- **Operational baseline (AD-19):** `/health` is liveness-only (no DB check). Logging is structured (Serilog) to stdout/stderr only, no environment branching in logging code. Secrets only via env vars/Container Apps secrets/`.env` — never committed or baked into the image.

**React (frontend):**
- **i18n (AD-18):** `Household.Locale` is the single field driving both number/date formatting and UI language; translations are additive i18next catalogs in `web/src/locales/{locale}` — adding a locale is a resource-file addition, never a code change.
- **Offline queue (AD-16/stack):** Meter Reading creation only (not edits) queues locally via IndexedDB and flushes on reconnect — don't extend this offline pattern to other writes without a matching architecture decision.
- Components follow the existing `web/src/components/{feature}` grouping (e.g. `household-invite`, `household-creation`, `tagging-scaffold`) plus a shared `components/ui` for shadcn primitives — new feature UI gets its own folder, not dumped into `components/ui`.

### Testing Rules

**.NET tests:**
- Test class per subject, named `{SubjectClass}Tests` (`CreateDeviceTests` for `CreateDevice`), one file per class, mirrored 1:1 into `{Layer}.Tests` (e.g. `EnergyTracker.Application` → `EnergyTracker.Application.Tests`).
- Test methods use `Snake_case_with_underscores` describing behavior (`Creates_and_persists_an_active_device_on_the_power_point`) — not `MethodName_Scenario_ExpectedResult` or camelCase.
- Assertions use **Shouldly** (`.ShouldBe(...)`, `Should.ThrowAsync<T>(...)`) — never raw xUnit `Assert.*`.
- Mocking uses **NSubstitute** (`Substitute.For<IPort>()`, `.Returns(...)`, `.Received(1)...`) against Application ports — never a hand-rolled fake/stub class.
- Async tests pass `TestContext.Current.CancellationToken` (xUnit v3 MTP convention) — not `CancellationToken.None` or `default`.
- Integration-level tests (migrations, DB-touching) use **Testcontainers** (real Postgres/SqlServer containers, not an in-memory EF provider) — AD-2's dual-provider portability can only be verified against real engines.
- `EnergyTracker.Architecture.Tests` encodes spine invariants as executable tests — when adding a new AD-style invariant, consider whether it belongs here as a guard test, not just as prose.

**Frontend tests:**
- Unit/component tests are colocated next to source (`color-scheme.test.ts` next to `color-scheme.ts`) — not a parallel `__tests__/` tree.
- Vitest + Testing Library (`@testing-library/react`), `jsdom` environment, globals on.
- E2E tests live in `web/e2e/*.spec.ts` via Playwright (`test:e2e`) — separate from the Vitest unit-test path (`test`/`test:watch`).

### Code Quality & Style Rules

**Naming:**
- Entities/DTOs: PascalCase C#, singular (`MeterReading`, never `MeterReadings`).
- API routes: kebab-case plural nouns (`/api/meter-readings`).
- Ports: `I{Capability}` in `Application/Ports`; adapters: `{Vendor}{Capability}` in `Infrastructure/Adapters`.

**Data & format rules (apply everywhere, not just at entity boundaries):**
- All timestamps are `DateTimeOffset`, ISO 8601 with explicit offset on the wire — never bare `DateTime`.
- All money is `decimal`, never `double`/`float`. Currency is an ISO 4217 code stored per Tariff entry.
- Background/scheduled work runs in UTC regardless of display Locale — locale only affects display, never storage.

**Errors:**
- API errors are RFC 7807 `ProblemDetails` — no ad hoc error shapes or bare status codes with a string body.
- Concurrency conflicts return HTTP 409 with the current server state (AD-4), not a generic 400/500.

**C# documentation:**
- Public use-case classes get a single-line `/// <summary>...</summary>` stating what they do and which acceptance criteria they satisfy (e.g. `Creates a Device on a Power Point in the caller's own Household (AC #1, #4).`) — not multi-line XML doc blocks or `<param>`/`<returns>` tags for straightforward cases.

**shadcn/ui config (`web/components.json`):**
- Style `radix-nova`, base color `neutral`, icon library `lucide`, no `tailwind.config` file (Tailwind v4 CSS-first config lives in `src/index.css`) — don't scaffold a `tailwind.config.js`/`.ts`.
- Aliases: `@/components`, `@/components/ui`, `@/lib`, `@/lib/utils`, `@/hooks` — prefer `npx shadcn add` over hand-writing primitives that already exist as shadcn components.

**oxlint (`web/.oxlintrc.json`):**
- `react/rules-of-hooks` and `no-unused-vars` are `error` (build-breaking); `react/only-export-components` is `warn` with `allowConstantExport: true`.

### Development Workflow Rules

**Git/commits:**
- Commit messages use a Conventional-Commits-style prefix (`feat:`, `fix:`, `doc:`) followed by a concise imperative summary.
- Branch naming: `feature/{short-description}` (e.g. `feature/otel-integration`).
- Commits/PRs frequently reference the AD number or story number they implement — carry that traceability forward when a change maps to a spine decision or story file.

**CI (`pr-review.yml`):**
- Runs on every PR against `main`: builds/tests/lints the .NET solution and web frontend; runs infra `what-if` only when `infra/**` changed and the PR isn't from a fork. Never deploys — deploys only happen from `app-deploy.yml`/`infra-deploy.yml` on push to `main`.
- The `build-test-lint` job has **no `name:` override** — GitHub branch protection matches required status checks by job id, not display name.
- Workflow-level `concurrency` cancels in-progress runs on new pushes to the same PR.

**Migrations:**
- Always add migrations via `scripts/add-migration.sh <Name>` — never `dotnet ef migrations add` directly against one provider project (AD-2).

**Local dev scripts (`scripts/`):** `run-api.sh`, `migrate.sh`, `add-migration.sh` — prefer these over raw `dotnet run`/`dotnet ef` invocations; they encode required env vars and provider wiring documented in `docs/local-development.md`.

### Critical Don't-Miss Rules

**Anti-patterns to avoid (each traces to a real architecture decision — see `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md`):**
- **Don't reimplement Bonus-Decay Normalization (AD-5):** the pace/savings formula shared by Pattern Detective and Tariff Savings Radar lives in exactly one place, `Domain.Calculations.BonusDecayNormalizer` — never a locally-adjusted copy in a feature.
- **Don't hard-branch on AI availability (AD-8):** `IAiPlausibilityClient` resolves to a no-op when unconfigured — features must not check "is AI enabled" and take a different code path; the correlation field is simply absent from the response.
- **Don't parse smart-plug files outside `ISmartPlugParser` (AD-9):** one port, one adapter per vendor (`EveHomeXlsxParser`, `MerossCsvParser`). Eve Home timestamps are parsed as **local time, never UTC-converted** — deliberate, documented behavior, not a bug to fix. Meross device identity comes from the filename pattern, not file-body metadata.
- **Don't hard-delete or live-FK-join Room/PowerPoint/Device (AD-10):** they're soft-deleted (`ArchivedAt`). `SmartPlugReading`/`Event` snapshot the Room/PowerPoint/Device identity **by value** at write time — a live FK join would incorrectly rewrite history when a Power Point's Room assignment changes later.
- **Don't build a bespoke "keep the old value" column per entity (AD-11):** all correction audit trails go through the single `AuditCorrection` table + `IAuditCorrectionRecorder`. Carve-out: FR-23's full-dataset restore is a wholesale replace, not an edit, and does not go through this mechanism.
- **Don't allow two open regression prompts per Main Meter (AD-12):** at most one open `MeterRegressionPrompt` at a time, ordered by reading timestamp (not entry order).

**Edge cases agents should handle:**
- An open `MeterRegressionPrompt` excludes its triggering reading (and everything chronologically after it) from baseline computation until resolved.
- Job envelopes must be plain JSON-serializable records — a delegate/closure payload works against `InProcessChannelJobQueue` but silently fails to serialize on `AzureStorageQueueJobQueue`.

**Security:**
- Every route requires authentication except the OIDC callback — don't add an unauthenticated endpoint without an explicit, reviewed reason.
- Never store an auth token client-side (`localStorage`/`sessionStorage`/JS-readable cookie) — enforced by `FrontendDoesNotStoreAuthTokensTests`. Session state lives only in the httpOnly cookie.
- Secrets (DB connection string, OIDC client secret, AI API key) come only from env vars/Container Apps secrets/`.env` — never committed, never baked into the image.

**Performance/operational gotchas:**
- `/health` must stay liveness-only (no DB/dependency check) — adding one risks a restart loop when Postgres/Azure SQL is briefly slow, since Container Apps' probe would fail and cycle the container.
- OTel logging (`Otel:Exporter=AzureMonitor`) deliberately does not forward logs to Application Insights in Azure — only traces + metrics — because Application Insights shares the same Log Analytics workspace/quota that stdout logging already streams into; a second log path would double-ingest against the shared daily cap.
- Check `docs/local-vs-azure-deltas.md` before touching auth, ingress, the database provider, or the deploy pipeline — local dev/self-host and Azure structurally diverge (TLS termination, ACR/managed-identity credential timing, region provisioning, blank-env-var handling) in ways that only surface on a live Azure deployment.

---

## Usage Guidelines

**For AI Agents:**

- Read this file before implementing any code
- Follow ALL rules exactly as documented
- When in doubt, prefer the more restrictive option
- Update this file if new patterns emerge

**For Humans:**

- Keep this file lean and focused on agent needs
- Update when technology stack changes
- Review quarterly for outdated rules
- Remove rules that become obvious over time

Last Updated: 2026-08-15
