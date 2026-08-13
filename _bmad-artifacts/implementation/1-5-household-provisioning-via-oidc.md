---
baseline_commit: fe2d2479fa68aa6a8f02781c140e817c2a4905d3
---

# Story 1.5: Household Provisioning via OIDC

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As the first person to access a fresh Energy Tracker deployment,
I want to authenticate via the configured OIDC provider and create my Household,
so that I can start using the product without any manual database step.

## Acceptance Criteria

1. **Given** a fresh deployment with no Household yet, **when** any visitor authenticates via the configured OIDC provider, **then** they are routed into a Household-creation step, never a broken or empty dashboard.
2. **Given** the Household-creation step, **when** completed, **then** no second party, invite code, or manual database step was required (FR-26).
3. **Given** a successful authentication, **when** the session is established, **then** it is a server-side httpOnly session cookie chained to the OIDC handler — never a token the browser-side app can read or store itself (AD-17).
4. **Given** the app has been idle and an Azure Container App instance cold-starts from scale-to-zero, **when** a previously authenticated household member returns, **then** their session is still valid — Data Protection keys are persisted externally (`PersistKeysToDbContext`), not regenerated in memory on cold start (AD-17).
5. **Given** any route in the product except the OIDC callback, **when** accessed without authentication, **then** the request is rejected (NFR3).
6. **Given** the OIDC provider is changed via configuration only, **when** the app restarts, **then** authentication works against the new provider with no code change (NFR3).
7. **Given** Household creation, **when** the household member sets its Locale and currency, **then** both are explicit choices made at creation time (from the launch Locales `de-DE`/`en-US`), never a silently-applied hardcoded default (AD-15, NFR5, NFR6).
8. **Given** the Household-creation UI, **when** rendered, **then** it contains no hardcoded locale-specific strings or formats — all copy is sourced from the Locale-driven translation mechanism (AD-18).

## Tasks / Subtasks

- [x] Task 1: Domain entities (AC #1, #2, #7)
  - [x] `src/EnergyTracker.Domain/Household.cs`: `Household { Guid Id; string Locale; string Currency; DateTimeOffset CreatedAtUtc; ICollection<HouseholdMember> Members }`. `Locale` is a launch-Locale string (`de-DE`/`en-US` for now — a later Locale is a resource-file addition, not a code change per AD-18, so don't hardcode an enum; validate against the currently-supported set in Application, not as a Domain-level closed type). `Currency` is an ISO 4217 code string (`decimal`/currency amounts elsewhere use `decimal`, never `double`/`float`, per Consistency Conventions — `Household.Currency` itself is just the 3-letter code).
  - [x] `src/EnergyTracker.Domain/HouseholdMember.cs`: `HouseholdMember { Guid Id; Guid HouseholdId; string ExternalIssuer; string ExternalSubjectId; DateTimeOffset CreatedAtUtc }`. **Store `ExternalIssuer` and `ExternalSubjectId` as two separate fields, not a single combined "ExternalId"** — the OIDC `sub` claim is only guaranteed unique *within* one issuer (NFR3/AD requires the provider be swappable via config with no code change); if only `sub` were stored, swapping providers could theoretically collide two different real people onto the same row. Both fields together are what `ICurrentHouseholdAccessor` (Task 4) looks up by.
  - [x] Both entities are plain C# — zero framework references, per AD-1 (`DomainHasNoExternalDependenciesTests` in `tests/EnergyTracker.Architecture.Tests/` already enforces this for the whole `EnergyTracker.Domain.csproj`; don't add a package reference to satisfy something that belongs in Infrastructure).
  - [x] Do **not** add `YearlyBaseline`, threshold, or any Pattern-Detective-related field to `Household` in this story — those are Epic 2 (Story 2.1). This story's `Household` is intentionally minimal: identity + Locale + Currency + membership.

- [x] Task 2: Application layer — port and use case (AC #1, #2, #3, #7)
  - [x] `src/EnergyTracker.Application/Ports/ICurrentHouseholdAccessor.cs`: the port named in the Structural Seed and Capability Map (`Application.ICurrentHouseholdAccessor`, AD-3). Shape: something like `Guid? HouseholdId { get; }` (nullable — an authenticated principal with no Household yet has no resolved id) plus a way to signal "authenticated but no Household". Infrastructure (Task 4) implements this from the HTTP principal; the job-processing resolution path (AD-3's other branch) belongs to a later epic (Smart Plug import) and is out of scope here — don't build it speculatively.
  - [x] `src/EnergyTracker.Application/Ports/IHouseholdRepository.cs` (or equivalent minimal repository port) — Application must not reference EF Core directly (AD-1); Infrastructure implements it against `EnergyTrackerDbContext`.
  - [x] A `CreateHousehold` use case in Application: given `(externalIssuer, externalSubjectId, locale, currency)`, creates one `Household` row and one `HouseholdMember` row (the creator) in a single transaction/unit of work, and returns the new `Household.Id`. Validate `locale` against the supported launch-Locale set here (`de-DE`, `en-US`) — reject anything else with a clear error rather than silently accepting or defaulting (AD-15/NFR5). Validate `currency` is a non-empty, plausible ISO 4217-shaped code (3 uppercase letters) — full ISO 4217 membership validation is not required for MVP, just "not blank, not obviously wrong."
  - [x] No CQRS/mediator library exists in this repo yet and the Stack table doesn't call for one — keep the use case a plain class with a constructor-injected repository port, consistent with the rest of the codebase's current simplicity. Don't introduce MediatR or similar for one use case.

- [x] Task 3: Infrastructure — persistence (AC #2, #4)
  - [x] EF Core configuration for `Household` and `HouseholdMember` via `IEntityTypeConfiguration<T>` classes in `EnergyTracker.Infrastructure` (framework/vendor code lives in Infrastructure only, per AD-1). Add `DbSet<Household> Households` and `DbSet<HouseholdMember> HouseholdMembers` to `EnergyTrackerDbContext` (currently an empty `DbContext` shell — `src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs`).
  - [x] **Tenant-isolation query filter decision — read this before writing `OnModelCreating` (AD-3):** AD-3's rule is "every Household-scoped entity gets `HasQueryFilter(e => e.HouseholdId == _currentHousehold.Id)`, sourced from `ICurrentHouseholdAccessor`." `HouseholdMember` carries `HouseholdId`, so it's Household-scoped by that definition — but `ICurrentHouseholdAccessor`'s own implementation (Task 4) must look up a `HouseholdMember` row *by `ExternalIssuer`+`ExternalSubjectId`* to determine what `HouseholdId` even is, before that value exists to filter by. Applying the standard filter to `HouseholdMember` creates a circular dependency at resolution time, and AD-3 explicitly forbids `IgnoreQueryFilters()` as the workaround (the exact bypass it exists to prevent). Recommended resolution: **do not apply an AD-3 `HasQueryFilter` to `HouseholdMember`.** It has no realistic cross-household leak vector to guard against — every real query against it is either (a) the identity-resolution lookup by `ExternalIssuer`+`ExternalSubjectId` (globally scoped by design, not household-scoped), or (b) a lookup already anchored to a known-trusted `HouseholdId` (e.g. a future "list my Household's members" query, Story 1.6, which supplies its own explicit `.Where(m => m.HouseholdId == knownId)` rather than relying on an ambient filter). Document this as a deliberate, reasoned exception to AD-3's general rule in Completion Notes — don't silently deviate without a written rationale, and don't reach for `IgnoreQueryFilters()` as an alternative fix.
  - [x] `Household` itself is the tenant root, not Household-scoped data — it never gets an AD-3 filter either (there's no `HouseholdId` on `Household` to filter by).
  - [x] Add `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` to `Directory.Packages.props` (pin to the `10.0.x` line matching this repo's other `Microsoft.AspNetCore.*`/EF Core 10.0.10 packages — verify the exact current patch version on NuGet at implementation time rather than guessing) and reference it from `EnergyTracker.Infrastructure.csproj`. Make `EnergyTrackerDbContext` implement `IDataProtectionKeyContext` (`DbSet<DataProtectionKey> DataProtectionKeys`) — this is what AC #4 (`PersistKeysToDbContext`) needs; the Api project's composition root (Task 5) wires `.PersistKeysToDbContext<EnergyTrackerDbContext>()` against it.
  - [x] Add the migration via `scripts/add-migration.sh AddHouseholdAndDataProtectionKeys` (or similar name) — **adds to both `EnergyTracker.Infrastructure.Migrations.Postgres` and `EnergyTracker.Infrastructure.Migrations.SqlServer` atomically (AD-2)**, never by hand in just one. The two existing `InitialCreate` migrations in both provider projects are currently empty placeholders (no tables) — this is effectively the first real schema. Stay inside AD-2's portable subset: plain `string`/`int`/`decimal`/`DateTimeOffset`/`bool`/`byte[]` columns only, no Postgres `jsonb`/SQL-Server-only types.
  - [x] `tests/EnergyTracker.Infrastructure.Tests/PostgresMigrationTests.cs` / `SqlServerMigrationTests.cs` currently only assert the (empty) `InitialCreate` migration name is applied — extend or add a test confirming the new migration applies cleanly against a real Testcontainers-backed database for both providers, following the exact existing pattern in those two files (don't invent a different test shape).

- [x] Task 4: Infrastructure — OIDC + cookie auth adapter (AC #1, #3, #5, #6)
  - [x] Implement `ICurrentHouseholdAccessor` in `EnergyTracker.Infrastructure` as a scoped service that reads the current `HttpContext.User` (via `IHttpContextAccessor`), extracts the OIDC `iss` and `sub` claims, and looks up the matching `HouseholdMember` (per Task 3's unfiltered-by-design query). Cache the result for the lifetime of the request (don't re-query per property access).
  - [x] **Resolution semantics — do not check "does any Household exist system-wide":** the Glossary explicitly allows one deployment to hold more than one Household ("One instance can technically hold more than one, but the product isn't designed around managing many"). The correct check for "does this visitor need to go through Household creation" is **"does the current authenticated principal (by issuer+subject) have a `HouseholdMember` row yet"** — not a global `Households.Any()` check. A naive global-count check would incorrectly block a second, unrelated authenticated principal (not sharing a Household, not invited — Story 1.6 handles invitation, out of scope here) from ever provisioning their own Household after the first one exists.
  - [x] Composition root (`Program.cs`) wiring — `Oidc:Authority`, `Oidc:ClientId`, `Oidc:ClientSecret` config values read once at the composition root (matching the existing `Database:Provider` pattern already in `Program.cs`), never re-read/branched-on elsewhere (Consistency Conventions, NFR3/AC #6): `.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(...).AddOpenIdConnect(...)`. `Microsoft.AspNetCore.Authentication.OpenIdConnect` and the cookie handler ship inside the ASP.NET Core shared framework via `Microsoft.NET.Sdk.Web` (already the Api project's SDK) — **no new NuGet package reference needed for this half**, only a `using` statement.
  - [x] Cookie options: `HttpOnly = true`, `SecurePolicy = CookieSecurePolicy.Always` (self-host is expected to sit behind TLS-terminating reverse proxy or direct HTTPS — don't weaken this for convenience), `SameSite = SameSiteMode.Lax` (needs to survive the OIDC redirect round-trip). This is what AC #3 tests structurally — the SPA must never be able to read this cookie or an equivalent token via JS.
  - [x] `builder.Services.AddDataProtection().PersistKeysToDbContext<EnergyTrackerDbContext>()` (AC #4) — this is the one line that makes sessions survive a scale-to-zero cold start; leaving Data Protection at its in-memory default silently breaks AC #4 with no visible error (exactly the failure mode AD-17/SOLUTION-OVERVIEW.md calls out).
  - [x] `Oidc:Authority`/`Oidc:ClientId` are **not secret** and should be plain config/env vars; `Oidc:ClientSecret` **is** secret (already reserved in `.env.example`/`appsettings.json`/`docker-compose.yml` from Story 1.1 — see Task 6).

- [x] Task 5: Api endpoints and route protection (AC #1, #2, #5)
  - [x] Fallback authorization policy requiring an authenticated user on `/api/**` routes (`RequireAuthenticatedUser` on a fallback/group policy) — **do not apply a single global `FallbackPolicy` across the whole app.** Two things must stay reachable unauthenticated even though AC #5 says "any route... except the OIDC callback": `/health` (AD-19, already established unauthenticated in Story 1.1 — a global fallback policy would silently break the existing liveness probe, a real regression) and static SPA asset serving (`UseStaticFiles`/`MapFallbackToFile`) — the browser has to be able to load the app shell itself before any login can happen. Scope the auth requirement to a `/api` route group (`app.MapGroup("/api").RequireAuthorization()`), plus explicit endpoints below.
  - [x] `GET /login` (or similar, outside `/api`): issues `Results.Challenge(...)` for the OIDC scheme, redirecting to the configured provider.
  - [x] `GET /logout` (or `POST`): signs out both the cookie scheme and (if the provider supports it) the OIDC scheme.
  - [x] `GET /api/session`: returns the current auth/Household state the frontend needs to decide what to render — authenticated-with-Household (`householdId`, `locale`, `currency`), authenticated-no-Household (route to creation), or unauthenticated (this endpoint itself sits behind the `/api` auth requirement, so an unauthenticated call 401s — the SPA's response to a 401 here is what triggers navigation to `/login`). Singleton resource, not a collection — `/api/session`, not plural, consistent with `/health` also not being pluralized.
  - [x] `POST /api/households`: body `{ locale, currency }`, calls Task 2's `CreateHousehold` use case using the authenticated principal's issuer+subject (never a client-supplied identity — AC #2's "no manual database step" also means no client-suppliable household-membership bypass). Returns the created Household's id/locale/currency. **A principal that already has a `HouseholdMember` row must not be able to create a second one via this endpoint** — reject with a 409/`ProblemDetails`, don't silently no-op or silently create a duplicate.
  - [x] API errors: RFC 7807 `ProblemDetails` (Consistency Conventions) — use ASP.NET Core's built-in `ProblemDetails` support, don't hand-roll a different error shape.

- [x] Task 6: Configuration surface — appsettings, docker-compose, Bicep (AC #6)
  - [x] `src/EnergyTracker.Api/appsettings.json`: add `Oidc:Authority` and `Oidc:ClientId` (empty-string placeholders, same pattern as the existing `Oidc:ClientSecret: ""`).
  - [x] `docker-compose.yml`: add `Oidc__Authority` / `Oidc__ClientId` env vars (plain values, not secret-sourced) alongside the existing `Oidc__ClientSecret: "${OIDC_CLIENT_SECRET:-}"` line.
  - [x] `.env.example`: add `OIDC_AUTHORITY=` / `OIDC_CLIENT_ID=` placeholders next to the existing `OIDC_CLIENT_SECRET=` reservation (currently commented "Reserved for Story 1.5 — leave blank until then"; this story is what fills that reservation in).
  - [x] `infra/modules/container-app.bicep`: add `param oidcAuthority string` and `param oidcClientId string` (plain, non-secret) and corresponding `Oidc__Authority` / `Oidc__ClientId` container env-var entries, next to the existing `OIDC__ClientSecret` entry (currently wired to the `oidc-client-secret` placeholder secret at line ~149 — that secret slot already exists from Story 1.2, this story supplies the two still-missing non-secret values and threads a real secret value through instead of the `'unset'` sentinel).
  - [x] `infra/main.bicep`: add `param oidcAuthority string = ''` / `param oidcClientId string = ''` and a `@secure() param oidcClientSecret string = 'unset'`, thread all three into the `containerApp` module call (`oidcAuthority`, `oidcClientId`, `oidcClientSecretValue: oidcClientSecret`).
  - [x] `infra/main.bicepparam`: needs real non-secret values (`oidcAuthority`/`oidcClientId`) and `param oidcClientSecret = readEnvironmentVariable('OIDC_CLIENT_SECRET')` mirroring the existing `databaseAdministratorPassword` pattern exactly.
  - [x] `.github/workflows/infra-deploy.yml`: add `OIDC_CLIENT_SECRET: ${{ secrets.OIDC_CLIENT_SECRET }}` to both the "What-if" and "Deploy" steps' `env:` blocks, next to the existing `DATABASE_ADMIN_PASSWORD` line — **this requires a new GitHub repository secret (`OIDC_CLIENT_SECRET`) that only a repo admin can create; this story cannot create it itself.** Flag this explicitly to the user rather than silently leaving the workflow referencing a secret that doesn't exist yet.
  - [x] **A real OIDC provider (Entra ID app registration, Auth0 tenant, etc.) is an external dependency this story cannot provision on its own** — Authority/ClientId/ClientSecret are real third-party values requiring a live account. If none is available at implementation time, wire the config surface completely and correctly (so a value drop-in is all that's needed later), and say so plainly in Completion Notes rather than claiming end-to-end live verification that didn't happen — same honesty discipline Stories 1.3/1.4 established for things that needed live infra access.

- [x] Task 7: Frontend — i18n setup (AC #8)
  - [x] Add `i18next`, `react-i18next` (and optionally `i18next-browser-languagedetector` for picking an initial *display* language before any Locale choice exists) to `web/package.json` — no i18n library is installed yet; this is the first story that needs one, per the Stack table's "i18next (or equivalent additive-catalog library) — AD-18."
  - [x] `web/src/locales/de-DE/*.json` and `web/src/locales/en-US/*.json` (or equivalent i18next resource structure) — additive catalogs, a future Locale is a new file, never a code change (AD-18).
  - [x] Initialize i18next once at the app root (`web/src/main.tsx`), matching the Locale mechanism `Household.Locale` will drive later — but note the chicken-and-egg case below.
  - [x] **Initial-language chicken-and-egg (household doesn't exist yet, so there's no `Household.Locale` to read):** use the browser-detected language purely as the *display* language default for the creation form's own chrome (e.g. `i18next-browser-languagedetector`, falling back to `en-US` if undetected/unsupported) — but the Locale value actually **submitted and stored** on the Household must still be the household member's own explicit selection in the form (AC #7/AD-15 — "never a silently-applied hardcoded default" applies to the stored value, not to what language the form happens to render in before they've chosen anything).

- [x] Task 8: Frontend — Household-creation UI and auth-aware app shell (AC #1, #7, #8)
  - [x] No mockup exists for this surface yet — `EXPERIENCE.md`'s Information Architecture table lists "Onboarding / Household Setup" but its own composition-reference note says it's "still spine-only, no rendered mock." Build a plain, on-brand form using the existing shadcn/Tailwind token set (`web/src/index.css`, `web/components.json`'s `radix-nova` style) rather than inventing new visual language — add whatever shadcn primitives are needed (`npx shadcn add form input select button`, the `Button` component already exists) rather than hand-rolling form controls.
  - [x] On app load, call `GET /api/session` (Task 5). Three outcomes: unauthenticated → navigate to `/login` (full page navigation, not an SPA client route — this is a server-initiated OIDC challenge); authenticated, no Household → render the Household-creation form; authenticated, has a Household → render the existing placeholder shell (`App.tsx`'s current "Skeleton build" content is fine as a stand-in — the real Dashboard is Epic 2, out of scope here; AC #1 only requires "never a broken or empty dashboard," not a built one).
  - [x] Household-creation form: Locale selector (exactly the two launch Locales, `de-DE`/`en-US` — not a free-text field, both must be explicitly offered, neither pre-selected) and a Currency field. **No fixed currency list is defined anywhere in the PRD/architecture** — a free-text ISO 4217 code field is acceptable, optionally pre-filled with a suggested value tied to whichever Locale is picked (`de-DE`→`EUR`, `en-US`→`USD`) using the same "suggested starting value the user must still confirm, never silently applied" pattern AD-15 already establishes for Yearly Baseline presets — the field must render as an actual editable/selectable control, not text describing a default.
  - [x] Submits `POST /api/households`; on success, re-fetch or locally update session state and render the post-creation placeholder shell.
  - [x] **No client-side router exists in this repo yet** (`web/package.json` has none installed) — this story's two-state distinction (creation form vs. placeholder shell) doesn't need one; introducing `react-router` or similar here would be scope creep ahead of actual need. Leave that decision to whichever later Epic 2+ story first needs multiple real navigable client routes (Dashboard, Trend History, Settings, etc.).
  - [x] Every user-facing string on this screen goes through the i18n mechanism from Task 7 — no inline English/German literal text (AC #8). `web/src/App.test.tsx`'s existing pattern (Vitest + Testing Library, per `web/src/test/setup.ts`) is the template for this surface's tests too.

- [x] Task 9: Verify against every AC
  - [x] AC #1/#2: automated test — an authenticated principal (via the test double below) with no `HouseholdMember` row hits a protected `/api` route or loads the app and is routed to Household creation, not a broken/empty state; completing it requires only the one `POST /api/households` call, no second party/invite/DB step.
  - [x] AC #3: structural — cookie auth handler configured with `HttpOnly = true`; no code path ever exposes an auth token to `web/` JS (grep the frontend for anything storing an auth token in `localStorage`/`sessionStorage`/a JS-readable cookie — there should be none).
  - [x] AC #4: integration test — write a Data-Protection-encrypted cookie via one `WebApplicationFactory`/host instance backed by a shared Testcontainers Postgres database, then decrypt/validate it via a **second, freshly constructed** host instance pointed at the same database (simulating a cold-started replacement instance) — confirms `PersistKeysToDbContext` actually works, not just that the line of code is present.
  - [x] AC #5: automated test — an unauthenticated request to a representative `/api/**` route returns 401/redirects to challenge; the same for `/health` returns 200 unauthenticated (regression guard — this must **not** flip to 401, Story 1.1 already established it unauthenticated); static asset/SPA fallback routes also remain reachable unauthenticated.
  - [x] AC #6: structural — `Program.cs` reads `Oidc:Authority`/`Oidc:ClientId`/`Oidc:ClientSecret` from config exactly once at the composition root with no environment/provider branching in the handler setup itself (same shape as the existing `Database:Provider` switch's *single point of decision*, though OIDC config has no provider-specific branch at all — it's generic by construction). Live verification against two different real OIDC tenants is out of scope unless real provider credentials are available (see Task 6's honesty-discipline note).
  - [x] AC #7: automated test — submitting Household creation without an explicit Locale/Currency selection is rejected client- and/or server-side; both launch Locales are selectable; the stored value always matches what was explicitly submitted, never a server-side default when omitted.
  - [x] AC #8: automated/manual check — no literal locale-specific string appears in the Household-creation component source (all copy goes through `t(...)`/equivalent); rendering the form with each of the two catalogs shows fully translated copy, not fallback keys.

- [x] Task 10: Documentation
  - [x] Update `README.md`/setup docs (NFR11 — docs are a real onboarding path) with the new required env vars: `OIDC_AUTHORITY`, `OIDC_CLIENT_ID`, `OIDC_CLIENT_SECRET` — what they are, and that a Self-Hoster needs their own OIDC provider app registration (Entra ID, Auth0, Authentik, Keycloak, or equivalent) to run this instance at all from this story forward.
  - [x] Update `infra/README.md` if the OIDC-related Bicep parameters/secret from Task 6 need a bootstrap note (distinct from that file's existing GitHub-Actions-federated-credential OIDC content, which is a different, unrelated "OIDC" — the CI/CD pipeline's own Azure login, not this story's end-user auth. Don't conflate the two when writing docs; consider explicitly noting the distinction if it's likely to confuse a future reader).

### Review Findings

- [x] [Review][Patch] Data Protection keys are persisted to the DB unencrypted at rest — add a cert-based encryptor — `src/EnergyTracker.Api/Program.cs:113-114`. `AddDataProtection().PersistKeysToDbContext<EnergyTrackerDbContext>()` has no `.ProtectKeysWithCertificate(...)`/Key Vault encryptor configured. On Linux (this app's Container Apps target), with no DPAPI available, ASP.NET Core falls back to storing key XML in plaintext in the same `DataProtectionKeys` table as `Households`/`HouseholdMembers`. Anyone with DB read access or a backup gets the master key material that protects the httpOnly session cookie. Decision: fix now via a cert-based encryptor (self-signed cert sourced via env var/volume, matching the existing secrets-via-env-var pattern).
- [x] [Review][Patch] `Claim.Issuer` reliability against a real OIDC provider is unverified and is the load-bearing value for the whole tenant-isolation design — capture the issuer explicitly — `src/EnergyTracker.Infrastructure/Adapters/CurrentHouseholdAccessor.cs:42`, `src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs:23`, `src/EnergyTracker.Api/Program.cs:104` (`GetClaimsFromUserInfoEndpoint = true`). The story's own reasoning for storing issuer+subject separately depends on `Claim.Issuer` correctly carrying the real OIDC issuer URL, which `GetClaimsFromUserInfoEndpoint`'s claim-merging path is not guaranteed to preserve. Decision: harden now by explicitly capturing the validated issuer (e.g. via `OnTokenValidated`, stashing it as a well-known custom claim) instead of relying on ambient claim provenance — removes the dependency on unverified ASP.NET Core internal behavior regardless of what live testing would eventually show.
- [x] [Review][Dismiss] `infra-deploy.yml` will fail on its next run — the `OIDC_CLIENT_SECRET` GitHub repository secret it now depends on does not exist yet — `infra/main.bicepparam:25`, `.github/workflows/infra-deploy.yml:52,63` — no code patch needed; Ralf will create the `OIDC_CLIENT_SECRET` repository secret directly (placeholder value acceptable until a real OIDC provider is registered). Already flagged transparently by the implementer in Completion Notes and `infra/README.md`.
- [x] [Review][Patch] `POST /api/households` 500s on a concurrent duplicate submission instead of the documented 409 [src/EnergyTracker.Application/CreateHousehold.cs:36-59]
- [x] [Review][Patch] `GET /logout` throws an unhandled exception when OIDC is unconfigured (blank `Oidc:ClientId`) [src/EnergyTracker.Api/Endpoints/AuthEndpoints.cs:20-23]
- [x] [Review][Patch] OIDC scheme registration guard checks only `Oidc:ClientId`, not `Oidc:Authority` — a ClientId-set/Authority-blank config 500s every route, reproducing the exact regression this story's conditional registration was written to prevent [src/EnergyTracker.Api/Program.cs:92-106]
- [x] [Review][Patch] `currency: null` in the `POST /api/households` JSON body 500s instead of returning the documented 400 [src/EnergyTracker.Application/CreateHousehold.cs:65-66]
- [x] [Review][Patch] `HouseholdEndpoints.cs` dereferences the `NameIdentifier` claim with a null-forgiving `!` instead of the null-check `CurrentHouseholdAccessor` uses for the identical lookup [src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs:18]
- [x] [Review][Patch] Frontend treats every non-401 `/api/session` failure (5xx, network error) identically to "unauthenticated" and force-navigates to `/login`, masking real backend errors and risking a redirect loop for an already-authenticated user [web/src/App.tsx:26-65]
- [x] [Review][Patch] No forwarded-headers middleware — behind Azure Container Apps' TLS-terminating ingress (this story's own deploy target), the OIDC handler will build an `http://` `redirect_uri` instead of `https://`, breaking login the moment a real provider is configured [src/EnergyTracker.Api/Program.cs — missing `UseForwardedHeaders`, insert near line 122]
- [x] [Review][Patch] `AddProblemDetails()` is registered but no `UseExceptionHandler(...)` is wired up, so every unhandled exception (including the crash bugs above) bypasses RFC 7807 and returns a bare empty 500 instead of a ProblemDetails response [src/EnergyTracker.Api/Program.cs:108-109,120-123]
- [x] [Review][Patch] `FrontendDoesNotStoreAuthTokensTests` reads each candidate file twice (once per `.Contains` check) instead of once [tests/EnergyTracker.Architecture.Tests/FrontendDoesNotStoreAuthTokensTests.cs:18-19]
- [x] [Review][Patch] A second browser tab stays stuck on the creation form after a `409 Conflict` (e.g. duplicate-tab submission) — the generic error handler never re-checks `/api/session` [web/src/components/household-creation/household-creation-form.tsx:66-75]
- [x] [Review][Patch] `HouseholdMembers`' unique `(ExternalIssuer, ExternalSubjectId)` index risks exceeding SQL Server/Postgres index key-size limits at max column length, an inconsistent-across-providers failure mode [src/EnergyTracker.Infrastructure/Configurations/HouseholdMemberConfiguration.cs:15-21]
- [x] [Review][Patch] Cookie `options.LoginPath` is never set, leaving a dangling reference to ASP.NET Core's default `/Account/Login` instead of this story's own `/login` endpoint (currently dead code, but a latent trap for the next route that requires auth outside `/api`) [src/EnergyTracker.Api/Program.cs:58-82]
- [x] [Review][Defer] `GET /api/session`'s `SingleAsync` throws an unhandled exception if a resolved `HouseholdId` doesn't correspond to an existing `Households` row [src/EnergyTracker.Api/Endpoints/SessionEndpoints.cs:25] — deferred, pre-existing gap that only becomes reachable once a future household-deletion feature exists; no code path in this story can produce the inconsistent state today.

## Dev Notes

- **This is the first story to write real application code into `Domain`/`Application`/`Infrastructure`.** Stories 1.1–1.4 built the skeleton and CI/CD pipelines only — `Household.cs`/`HouseholdMember.cs` (Task 1) are the first Domain entities in the repo, and the two `InitialCreate` migrations are currently empty placeholders with no tables (verified by reading them directly). Treat this as a from-scratch build against the architecture spine, not an extension of existing feature code — there is no existing feature-code pattern to copy from within this repo yet, only the CI/infra patterns from Stories 1.2–1.4.
- **No bundled local OIDC provider exists for dev/test** — this is an explicit architecture Deferred item ("Local OIDC provider for dev/test... not blocking, since self-hosters typically already run one"). Automated tests therefore should **not** attempt to stand up a real OIDC handshake. Recommended approach: register a test-only `AuthenticationHandler` under the same scheme name in `WebApplicationFactory.WithWebHostBuilder(...)` overrides (following `tests/EnergyTracker.Api.Tests/HealthEndpointTests.cs`'s existing `WebApplicationFactory<Program>` pattern) that issues a principal with known `iss`/`sub` claims directly, bypassing the real OIDC redirect/token-exchange entirely. This keeps AC verification meaningful without needing a live third-party IdP account, and doesn't require standing up local OIDC infrastructure the architecture deliberately deferred.
- **The tenant-isolation query-filter exception for `HouseholdMember`** (Task 3) is the single trickiest architectural judgment call in this story — re-read that task's note before implementing `OnModelCreating`. Getting it wrong either reintroduces the exact `IgnoreQueryFilters()` bypass AD-3 was written to prevent, or makes `ICurrentHouseholdAccessor` unable to resolve its own value on first login (a circular dependency). Document whichever approach is taken in Completion Notes with the reasoning, since this is a deliberate, reasoned exception rather than something the architecture spine spells out explicitly.
- **Household-resolution semantics: per-principal, not system-wide.** Re-read Task 4's note — "does *this* authenticated principal have a Household" is the correct check, not "does any Household exist yet." The Glossary explicitly allows more than one Household per deployment.
- **`/health` must stay unauthenticated.** AC #5's "any route... except the OIDC callback" cannot be read as a literal blanket `FallbackPolicy` across the whole app without breaking Story 1.1's existing liveness probe (AD-19) and, separately, making it impossible for an unauthenticated browser to even load the SPA shell that would let it reach a login trigger in the first place. Scope the auth requirement to `/api/**` plus the explicit endpoints this story adds; leave `/health` and static file/SPA-fallback serving outside it.
- **External-identity uniqueness:** store OIDC issuer and subject separately (`ExternalIssuer` + `ExternalSubjectId`), not a single combined field — `sub` alone is only unique within one issuer, and NFR3 requires the provider to be swappable via config.
- **Real OIDC provider credentials are an external dependency this story cannot self-provision** (Task 6) — same category of constraint as Stories 1.2–1.4's Azure secrets, but this time for a third-party identity provider rather than Azure itself. Wire the full config surface correctly regardless of whether real credentials are available at implementation time, and state plainly in Completion Notes which ACs were verified via the test double vs. genuinely live against a real IdP.
- **Constraints that still apply, unchanged**: AD-1 (Domain has zero external package references — `DomainHasNoExternalDependenciesTests` already enforces this and will fail the build if violated), AD-2 (any new migration goes to both provider projects atomically via `scripts/add-migration.sh`, portable-subset columns only), AD-19 (all new secrets — `OIDC_CLIENT_SECRET` — via env vars/Container Apps secrets/`.env`, never committed).

### Project Structure Notes

New/modified files this story introduces:

```text
energy-tracker-v2/
  src/
    EnergyTracker.Domain/
      Household.cs                              # new
      HouseholdMember.cs                        # new
    EnergyTracker.Application/
      Ports/
        ICurrentHouseholdAccessor.cs             # new
        IHouseholdRepository.cs                  # new
      CreateHousehold.cs (or similar use case)   # new
    EnergyTracker.Infrastructure/
      EnergyTrackerDbContext.cs                  # modified — DbSets, IDataProtectionKeyContext
      Configurations/
        HouseholdConfiguration.cs                # new
        HouseholdMemberConfiguration.cs          # new
      Adapters/
        CurrentHouseholdAccessor.cs              # new — ICurrentHouseholdAccessor impl
      EnergyTracker.Infrastructure.csproj         # modified — DataProtection.EntityFrameworkCore package
    EnergyTracker.Infrastructure.Migrations.Postgres/
      Migrations/{timestamp}_AddHouseholdAndDataProtectionKeys.cs   # new
    EnergyTracker.Infrastructure.Migrations.SqlServer/
      Migrations/{timestamp}_AddHouseholdAndDataProtectionKeys.cs   # new
    EnergyTracker.Api/
      Program.cs                                 # modified — auth services, /login, /logout, /api group
      appsettings.json                           # modified — Oidc:Authority, Oidc:ClientId added
      Endpoints/ (or inline in Program.cs, matching existing minimal-API style)
        SessionEndpoints.cs / HouseholdEndpoints.cs   # new (exact organization is a judgment call — no
                                                       # endpoint-grouping precedent exists yet in this repo)
  web/
    package.json                                 # modified — i18next, react-i18next added
    src/
      locales/de-DE/*.json, locales/en-US/*.json  # new
      main.tsx                                    # modified — i18next init
      App.tsx                                     # modified — session-aware rendering
      components/household-creation/...           # new
  Directory.Packages.props                        # modified — DataProtection.EntityFrameworkCore version
  docker-compose.yml                               # modified — Oidc__Authority/ClientId env vars
  .env.example                                     # modified — OIDC_AUTHORITY/OIDC_CLIENT_ID
  infra/
    modules/container-app.bicep                    # modified — oidcAuthority/oidcClientId params+env
    main.bicep                                     # modified — oidcAuthority/oidcClientId/oidcClientSecret params
    main.bicepparam                                # modified — real values / readEnvironmentVariable
  .github/workflows/infra-deploy.yml                # modified — OIDC_CLIENT_SECRET env in what-if/deploy steps
  tests/
    EnergyTracker.Api.Tests/...                     # new — auth/session/household-creation tests, test auth handler
    EnergyTracker.Infrastructure.Tests/...           # modified — new migration coverage, DataProtection cold-start test
```

Exact file/folder names for the Application use case and Api endpoint organization are judgment calls (no precedent exists yet in this repo for either) — pick something that reads clearly and stays consistent with the Consistency Conventions table (kebab-case routes, PascalCase types), and note the actual choice in Completion Notes/File List.

### References

- [Source: _bmad-artifacts/planning/epics/epic-1-foundation-deployment-household-access.md#Story 1.5] — story statement and acceptance criteria (verbatim origin)
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md#FR-26] — Household Provisioning FR and its testable consequences
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/cross-cutting-nfrs.md] — NFR3 (auth), NFR5 (i18n/locale), NFR6 (currency)
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE.md#AD-1] — Domain/Application must not depend on EF Core/ASP.NET Core/vendor SDKs
- [Source: ...ARCHITECTURE-SPINE.md#AD-2] — dual-provider migrations, `scripts/add-migration.sh`, portable column subset
- [Source: ...ARCHITECTURE-SPINE.md#AD-3] — tenant isolation via DbContext global query filter, the two `ICurrentHouseholdAccessor` resolution paths, the explicit `IgnoreQueryFilters()`/`Find()`/raw-SQL prohibition against Household-scoped entities
- [Source: ...ARCHITECTURE-SPINE.md#AD-15] — generic-by-default, no hardcoded household-specific values, presets-as-suggestions pattern (reused here for the Currency field)
- [Source: ...ARCHITECTURE-SPINE.md#AD-17] — server-side httpOnly cookie chained to OIDC, `PersistKeysToDbContext` and why the in-memory Data Protection default breaks on scale-to-zero cold start
- [Source: ...ARCHITECTURE-SPINE.md#AD-18] — `Household.Locale` drives formatting+translation, additive i18next-style catalogs, `.resx`/`IStringLocalizer` for any backend-rendered strings (none needed yet in this story)
- [Source: ...ARCHITECTURE-SPINE.md#AD-19] — `/health` liveness-only endpoint (already implemented, must not regress), secrets via env vars only
- [Source: ...ARCHITECTURE-SPINE.md Structural Seed / Capability Map] — `ICurrentHouseholdAccessor` named location (`Application.ICurrentHouseholdAccessor`), Household & Access capability governed by AD-3/AD-10/AD-15/AD-17
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/SOLUTION-OVERVIEW.md#Staying logged in, safely] — the Data Protection cold-start gotcha explained in narrative form; the reasoning behind the tenant-isolation `IgnoreQueryFilters()` warning
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/3-glossary.md] — "Household... one instance can technically hold more than one" (the basis for this story's per-principal, not system-wide, resolution semantics)
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/addendum.md] — "auth uses ASP.NET Core's generic OIDC handler, config-compatible with Entra ID, Auth0, or any other OIDC provider" (resolution of the original Auth0-or-Entra-ID candidate shape)
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md#Information Architecture] — Onboarding/Household Setup surface definition; explicitly noted as not yet mocked up
- [Source: src/EnergyTracker.Api/Program.cs] — existing composition-root pattern (`Database:Provider` read once, switch at startup) this story's `Oidc:*` config wiring follows the same shape for
- [Source: src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs] — currently an empty DbContext shell; this story adds its first real DbSets
- [Source: src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260809151432_InitialCreate.cs, .../SqlServer/Migrations/20260809151434_InitialCreate.cs] — both currently empty placeholder migrations (no tables) — confirmed by direct read, not assumed
- [Source: infra/modules/container-app.bicep] — the pre-existing `oidc-client-secret` reserved-placeholder secret slot (Story 1.2) this story finally threads a real value into, plus the `OIDC__ClientSecret` env-var entry already wired to it
- [Source: infra/main.bicep, infra/main.bicepparam] — `databaseAdministratorPassword`/`readEnvironmentVariable('DATABASE_ADMIN_PASSWORD')` pattern this story's `oidcClientSecret` parameter mirrors exactly
- [Source: .github/workflows/infra-deploy.yml] — existing `DATABASE_ADMIN_PASSWORD` env-var wiring in the What-if/Deploy steps, the pattern `OIDC_CLIENT_SECRET` follows
- [Source: .env.example] — `OIDC_CLIENT_SECRET=` already reserved with the comment "Reserved for Story 1.5 (household provisioning via OIDC) — leave blank until then"
- [Source: docker-compose.yml] — `Oidc__ClientSecret: "${OIDC_CLIENT_SECRET:-}"` already wired; `Oidc__Authority`/`Oidc__ClientId` are not yet present and this story adds them
- [Source: web/package.json, web/components.json] — no i18n or router library installed yet; shadcn `radix-nova` style already configured, `Button` component already scaffolded under `web/src/components/ui/`
- [Source: _bmad-artifacts/implementation/1-4-pull-request-review-workflow.md#Dev Notes, #Completion Notes] — the honesty-discipline precedent (state plainly what was verified live vs. structurally/via test double, rather than implying full verification that didn't happen) this story's Task 6/10 explicitly continues, applied here to OIDC-provider credentials instead of Azure CLI access
- [Source: tests/EnergyTracker.Api.Tests/HealthEndpointTests.cs] — existing `WebApplicationFactory<Program>` test pattern this story's auth/session tests and test-auth-handler override extend
- [Source: tests/EnergyTracker.Architecture.Tests/DomainHasNoExternalDependenciesTests.cs] — existing enforcement of AD-1 against `EnergyTracker.Domain.csproj`; this story's new Domain entities must not trip it
- [Source: https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie] — ASP.NET Core cookie authentication handler configuration, current as of .NET 10 — verify at implementation time
- [Source: https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-storage-providers#persistkeystodbcontext] — `PersistKeysToDbContext`/`IDataProtectionKeyContext` current API shape — verify package version at implementation time

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

None — no persistent debug log was needed. All verification was via `dotnet build`/`dotnet test` (including live Testcontainers Postgres/SQL Server runs), `npm run build`/`test`/`lint`/`test:e2e`, and `az bicep build`/`build-params` for the infra templates. One live `dotnet run` against a real (unreachable) Postgres connection string was used interactively to capture the actual unhandled-exception stack trace behind the AuthenticationMiddleware regression described below — see Completion Notes.

### Completion Notes List

- **A real OIDC provider (Entra ID, Auth0, Authentik, Keycloak, etc.) was not available at implementation time.** Per Task 6/Dev Notes' explicit honesty-discipline instruction, the full config surface (appsettings.json, docker-compose.yml, .env.example, all three Bicep files, infra-deploy.yml) is wired correctly end-to-end, but no live handshake against a real IdP was performed. AC #1/#2/#5/#7 are verified via the test-only `TestAuthHandler` (issues a principal with known `iss`/`sub` claims directly, per Dev Notes' recommended approach) against a real Testcontainers Postgres database — not a live OIDC redirect/token exchange. AC #6 (provider swappable via config, no code change) is verified structurally: `Program.cs` reads `Oidc:Authority`/`Oidc:ClientId`/`Oidc:ClientSecret` exactly once at the composition root with no environment/provider branching; live verification against two different real OIDC tenants was out of scope.
- **Deviation from the story's stated assumption: `Microsoft.AspNetCore.Authentication.OpenIdConnect` DOES require a NuGet package reference**, contrary to Task 4's note that it "ships inside the ASP.NET Core shared framework ... no new NuGet package reference needed." Verified directly by inspecting the local `Microsoft.AspNetCore.App.Ref` reference-assembly packs (10.0.10): only the cookie handler ships there; the OIDC handler's `Microsoft.IdentityModel.*` dependencies are heavy enough that it ships as a separate package. Added `Microsoft.AspNetCore.Authentication.OpenIdConnect` (10.0.10, matching the rest of the repo's EF Core/ASP.NET Core pin) to `Directory.Packages.props` and `EnergyTracker.Api.csproj`. Also added a `FrameworkReference Include="Microsoft.AspNetCore.App"` to `EnergyTracker.Infrastructure.csproj` (not `Sdk.Web`, so `IHttpContextAccessor`/cookie types used by `CurrentHouseholdAccessor` aren't visible without it) and a `Microsoft.EntityFrameworkCore.Relational` package reference there too (`ToTable`/`HasIndex` fluent config APIs are relational-specific, not in the core `Microsoft.EntityFrameworkCore` package).
- **A real, non-hypothetical regression was caught and fixed during implementation: registering the OIDC scheme unconditionally with a blank `Oidc:ClientId` broke every route in the app, including `/health` — violating AD-19.** Root cause (confirmed by running the API locally against a real, unreachable-DB connection string and inspecting the raw unhandled-exception response body): ASP.NET Core's `AuthenticationMiddleware` initializes every registered `IAuthenticationRequestHandler` scheme on *every* request (not just OIDC-specific ones), to check whether the request path matches that scheme's callback path — and `OpenIdConnectOptions.Validate()` throws `ArgumentException` on a blank `ClientId`. Since a blank `Oidc:ClientId` is the explicitly expected state before a self-hoster configures a real provider (Task 6), this would have 500'd every request the moment this shipped. Fixed by registering `.AddOpenIdConnect(...)` conditionally — only when `Oidc:ClientId` is non-empty — so the rest of the app (including `/health` and the SPA shell) stays fully functional with OIDC unconfigured; only `/login` fails until real provider credentials are supplied. This is a deliberate deviation beyond the story's literal text, verified via `HealthEndpointTests`/`DatabaseProviderSelectionTests` (pre-existing tests that would otherwise have regressed) and a new `AuthenticationTests.GET_health_stays_unauthenticated_even_though_api_requires_auth` regression guard.
- **AD-3 tenant-isolation query-filter exception for `HouseholdMember` — implemented exactly as the story's Task 3 reasoning prescribes.** No `HasQueryFilter` is applied to `HouseholdMember` in `HouseholdMemberConfiguration`; a unique index on `(ExternalIssuer, ExternalSubjectId)` is used instead of a query filter, since every real query against this entity is either the identity-resolution lookup (globally scoped by design) or one already anchored to a known-trusted `HouseholdId`. `Household` itself also has no filter (it's the tenant root, no `HouseholdId` column to filter by). Documented inline in `HouseholdConfiguration.cs`/`HouseholdMemberConfiguration.cs` as a deliberate, reasoned exception to AD-3's general rule, per the story's explicit instruction not to silently deviate.
- **`ICurrentHouseholdAccessor.HouseholdId` is a synchronous property (per the story's suggested shape), backed by a synchronous EF Core query, lazily resolved and cached for the request's lifetime** (a `_resolved`/`_householdId` backing-field pair) — matches "cache the result for the lifetime of the request (don't re-query per property access)." A synchronous DB call was chosen over an async-method port shape to match the story's literal `Guid? HouseholdId { get; }` suggestion; this is acceptable at this app's personal/household scale (NFR2/NFR14 cost discipline already assumes low request volume).
- **Household-resolution semantics are per-principal, not system-wide**, as required: `CurrentHouseholdAccessor` looks up a `HouseholdMember` row by the current principal's issuer+subject, never a global `Households.Any()` check. Verified by `SessionAndHouseholdCreationTests.Two_different_principals_can_each_provision_their_own_Household`.
- **A new `EnergyTracker.Application.Tests` project was added** (not explicitly listed in the story's Project Structure Notes, which only called out modifications to `Api.Tests`/`Infrastructure.Tests`) to unit-test `CreateHousehold`'s validation/duplicate-detection logic in isolation with a fake `IHouseholdRepository` (NSubstitute) — consistent with the existing one-test-project-per-src-project convention and Step 6's "unit tests for business logic" requirement. Added to `EnergyTracker.sln` under the existing `tests` solution folder.
- **Frontend form deliberately does not use the shadcn `form` component (react-hook-form + zod)** — a two-field form (Locale select, Currency input) with simple client-side "both fields must be non-empty before submit is enabled" validation doesn't need a form library; the real validation authority is the server (`CreateHousehold`'s locale/currency checks), matching the story's "client- and/or server-side" wording for AC #7. Avoids introducing two new dependencies (`react-hook-form`, `zod`) for a form this small.
- **Existing tests updated for the new auth-gated `App.tsx` behavior** (not literally required by any Task, but necessary to avoid regressions from Task 8's scope): `web/src/App.test.tsx` now mocks `fetch('/api/session')` for all three states (has-Household, needs-Household, unauthenticated → `/login` redirect); `web/e2e/app-shell.spec.ts` now mocks the same endpoint via Playwright route interception, since `vite preview`'s static server has no live backend and the SPA no longer renders anything meaningful without a successful session check.
- **AC #8 catalog completeness verified**: `web/src/locales/de-DE/translation.json` and `en-US/translation.json` have identical flattened key sets (verified via a one-off script during implementation, not a checked-in test — the two catalogs are hand-maintained and small; a dedicated parity test was judged disproportionate at this scale, revisit if the catalog grows).
- **`OIDC_CLIENT_SECRET` GitHub repository secret does not exist yet** — `infra-deploy.yml` now references it (mirroring `DATABASE_ADMIN_PASSWORD`), and `main.bicepparam`'s `readEnvironmentVariable('OIDC_CLIENT_SECRET')` will fail deploys until a repo admin creates it once a real OIDC provider exists. Flagged explicitly here and in `infra/README.md`'s new "OIDC_CLIENT_SECRET — a second, unrelated 'OIDC'" section, rather than silently deploying against a placeholder — this repo cannot create GitHub repository secrets itself.
- **All ACs verified**: AC #1/#2 (`SessionAndHouseholdCreationTests`), AC #3 (structural — `Program.cs` cookie config + `FrontendDoesNotStoreAuthTokensTests` grep guard), AC #4 (`DataProtectionColdStartTests` — two independent host instances against one shared Postgres container), AC #5 (`AuthenticationTests`), AC #6 (structural code review — see above), AC #7 (`SessionAndHouseholdCreationTests` validation cases + `CreateHouseholdTests`), AC #8 (i18n catalog parity + `FrontendDoesNotStoreAuthTokensTests`-style manual grep for inline literals — none found in `household-creation-form.tsx`/`App.tsx`).

### File List

**New:**
- `src/EnergyTracker.Domain/Household.cs`
- `src/EnergyTracker.Domain/HouseholdMember.cs`
- `src/EnergyTracker.Application/Ports/ICurrentHouseholdAccessor.cs`
- `src/EnergyTracker.Application/Ports/IHouseholdRepository.cs`
- `src/EnergyTracker.Application/CreateHousehold.cs`
- `src/EnergyTracker.Application/HouseholdValidationException.cs`
- `src/EnergyTracker.Application/HouseholdAlreadyExistsException.cs`
- `src/EnergyTracker.Infrastructure/Configurations/HouseholdConfiguration.cs`
- `src/EnergyTracker.Infrastructure/Configurations/HouseholdMemberConfiguration.cs`
- `src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs`
- `src/EnergyTracker.Infrastructure/Adapters/CurrentHouseholdAccessor.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260813173550_AddHouseholdAndDataProtectionKeys.cs` (+ `.Designer.cs`) — regenerated during code review (`ExternalIssuer` max length reduced from 2048 to 500, see Review Findings)
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260813173553_AddHouseholdAndDataProtectionKeys.cs` (+ `.Designer.cs`) — regenerated during code review, same reason
- `src/EnergyTracker.Application/HouseholdClaimTypes.cs` — added during code review (explicit OIDC issuer capture, see Review Findings)
- `src/EnergyTracker.Api/Endpoints/AuthEndpoints.cs`
- `src/EnergyTracker.Api/Endpoints/SessionEndpoints.cs`
- `src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs`
- `tests/EnergyTracker.Application.Tests/` (new project: `EnergyTracker.Application.Tests.csproj`, `CreateHouseholdTests.cs`, `xunit.runner.json`)
- `tests/EnergyTracker.Api.Tests/TestAuthHandler.cs`
- `tests/EnergyTracker.Api.Tests/EnergyTrackerApiFactory.cs`
- `tests/EnergyTracker.Api.Tests/AuthenticationTests.cs`
- `tests/EnergyTracker.Api.Tests/SessionAndHouseholdCreationTests.cs`
- `tests/EnergyTracker.Api.Tests/DataProtectionColdStartTests.cs`
- `tests/EnergyTracker.Architecture.Tests/FrontendDoesNotStoreAuthTokensTests.cs`
- `web/src/i18n/index.ts`
- `web/src/locales/de-DE/translation.json`
- `web/src/locales/en-US/translation.json`
- `web/src/components/ui/input.tsx`, `label.tsx`, `select.tsx` (shadcn-generated)
- `web/src/components/household-creation/household-creation-form.tsx`

**Modified:**
- `src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs` (DbSets, `IDataProtectionKeyContext`)
- `src/EnergyTracker.Infrastructure/EnergyTracker.Infrastructure.csproj` (DataProtection.EntityFrameworkCore, EF Relational, FrameworkReference)
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/EnergyTrackerDbContextModelSnapshot.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/EnergyTrackerDbContextModelSnapshot.cs`
- `src/EnergyTracker.Api/Program.cs` (auth wiring, `/login`, `/logout`, `/api` group, DataProtection)
- `src/EnergyTracker.Api/EnergyTracker.Api.csproj` (OIDC + DataProtection package references)
- `src/EnergyTracker.Api/appsettings.json` (`Oidc:Authority`/`Oidc:ClientId` placeholders)
- `Directory.Packages.props` (two new package version pins)
- `EnergyTracker.sln` (new `EnergyTracker.Application.Tests` project)
- `docker-compose.yml`, `.env.example` (OIDC env vars)
- `infra/modules/container-app.bicep`, `infra/main.bicep`, `infra/main.bicepparam` (OIDC params/env)
- `.github/workflows/infra-deploy.yml` (`OIDC_CLIENT_SECRET` env)
- `docs/self-hosting.md`, `docs/local-development.md`, `infra/README.md` (OIDC documentation)
- `tests/EnergyTracker.Api.Tests/EnergyTracker.Api.Tests.csproj` (Testcontainers.PostgreSql, EF Relational)
- `tests/EnergyTracker.Infrastructure.Tests/PostgresMigrationTests.cs`, `SqlServerMigrationTests.cs` (new-migration assertion)
- `web/package.json`, `web/package-lock.json` (i18next, react-i18next, i18next-browser-languagedetector)
- `web/tsconfig.app.json` (`resolveJsonModule`)
- `web/src/main.tsx` (i18n init import)
- `web/src/test/setup.ts` (i18n init for tests)
- `web/src/App.tsx` (session-aware app shell)
- `web/src/App.test.tsx` (mocked `/api/session` for all three states)
- `web/e2e/app-shell.spec.ts` (mocked `/api/session` for the smoke test)
- `_bmad-artifacts/implementation/sprint-status.yaml` (status tracking)

### Change Log

- 2026-08-13: Story 1.5 implementation complete. Domain/Application/Infrastructure/Api layers built from scratch (first story with real feature code beyond the Stories 1.1–1.4 skeleton/CI): `Household`/`HouseholdMember` entities, `CreateHousehold` use case, EF Core persistence with a deliberate AD-3 query-filter exception for `HouseholdMember`, cookie+OIDC auth wired at the composition root with `PersistKeysToDbContext` for cold-start session survival, `/login`/`/logout`/`/api/session`/`POST /api/households` endpoints, full OIDC config surface threaded through appsettings/compose/Bicep/CI, i18next-based Household-creation UI. Caught and fixed a real regression during implementation where registering the OIDC scheme unconditionally with a blank `ClientId` broke every route in the app (including `/health`) — fixed via conditional scheme registration. Corrected the story's assumption that the OIDC handler needs no NuGet package reference. No real OIDC provider was available to test against live; the full config surface is wired correctly and AC #1/#2/#5/#7 are verified via a test-only auth handler per the story's own recommendation.
