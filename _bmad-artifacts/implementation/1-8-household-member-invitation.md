---
baseline_commit: a7f4931e166ff780a8f8c0a9fdb0f554f62689c9
---

# Story 1.8: Household Member Invitation

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As an existing Household member,
I want to invite additional members to my Household,
so that everyone sharing this home in real life can also log readings and see the same Status.

## Acceptance Criteria

1. **Given** an existing Household member, **when** they send an invitation, **then** the invited person can join the same Household after authenticating via the configured OIDC provider (FR-27).
2. **Given** a Household with multiple members, **when** any member accesses or modifies the Household's data, **then** all members have equal, full access — there is no separate admin/owner role (FR-27).
3. **Given** a newly joined member, **when** they access any Household-scoped data, **then** they see only this Household's data, enforced at the data-access layer (AD-3, NFR4).

## Tasks / Subtasks

- [x] Task 1: Domain — `HouseholdInvite` entity (AC #1)
  - [x] `src/EnergyTracker.Domain/HouseholdInvite.cs`: `HouseholdInvite { Guid Id; Guid HouseholdId; string Token; DateTimeOffset CreatedAtUtc; DateTimeOffset ExpiresAtUtc; DateTimeOffset? ConsumedAtUtc; int Version }`.
  - [x] `Token` is the opaque bearer credential embedded in the shareable `/join/{token}` URL — generate it as `Guid.NewGuid().ToString("N")` (32 hex chars, URL-safe with no escaping needed). `Guid.NewGuid()` is cryptographically random on all supported .NET runtimes, so no custom RNG code is needed, and it matches every other identifier in this codebase already being a `Guid`. Do **not** invent a shorter/human-typeable code — this token is a bearer credential that grants full access to a Household's data (energy-consumption data is explicitly treated as sensitive per the PRD's Constraints — a proxy for occupancy patterns), so it needs real entropy, not convenience.
  - [x] `Version` is a plain `int` EF Core concurrency token, following the exact same portable pattern AD-4 already establishes elsewhere (`Meter Reading`, `Tariff`) — **not** a new mechanism. It exists to make two concurrent accepts of the same single-use invite resolve safely (Task 3).
  - [x] Plain C#, zero framework references, per AD-1 — same rule `Household.cs`/`HouseholdMember.cs` already follow.

- [x] Task 2: Application — ports, use cases, exceptions (AC #1, #2, #3)
  - [x] Extend `src/EnergyTracker.Application/Ports/IHouseholdRepository.cs` with three new members: `Task AddInviteAsync(HouseholdInvite invite, CancellationToken cancellationToken)`; `Task<HouseholdInvite?> FindInviteByTokenAsync(string token, CancellationToken cancellationToken)`; `Task<Household> AcceptInviteAsync(HouseholdInvite invite, HouseholdMember newMember, CancellationToken cancellationToken)` (marks the invite consumed and adds the new member as one atomic unit of work, returning the joined `Household`). Do not create a second, parallel repository port for invites — `HouseholdInvite` is part of the same Household & Access capability/aggregate as `Household`/`HouseholdMember`, and this repo already keeps that as one port (see Story 1.5's precedent of not introducing a mediator/second abstraction for one closely-related concern).
  - [x] `src/EnergyTracker.Application/HouseholdInviteNotFoundException.cs` and `src/EnergyTracker.Application/HouseholdInviteExpiredOrConsumedException.cs` — two small, purpose-specific exception types (constructor takes the token string, sets a plain `Message`), matching the existing flat-namespace style of `HouseholdValidationException`/`HouseholdAlreadyExistsException`. Keep them separate types (not one exception with an internal reason enum) for consistency with the existing pattern.
  - [x] `src/EnergyTracker.Application/CreateHouseholdInvite.cs`: plain class, constructor-injected `IHouseholdRepository` (same shape as `CreateHousehold`). `public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7)` — a fixed, undocumented-elsewhere operational default (not a per-Household AD-15 config value; AD-15's rule is about household-specific *product* values like Yearly Baseline presets and currency, not an invite-link security/operational policy constant — don't build a settings surface for this). `ExecuteAsync(Guid householdId, CancellationToken cancellationToken)` builds a new `HouseholdInvite` (`Token` = `Guid.NewGuid().ToString("N")`, `ExpiresAtUtc` = `DateTimeOffset.UtcNow + InviteLifetime`) and persists it via `AddInviteAsync`.
  - [x] `src/EnergyTracker.Application/AcceptHouseholdInvite.cs`: constructor-injected `IHouseholdRepository`. `ExecuteAsync(string token, string externalIssuer, string externalSubjectId, CancellationToken cancellationToken)`:
    1. Look up the invite by token; throw `HouseholdInviteNotFoundException` if `null`.
    2. If `invite.ConsumedAtUtc is not null` or `invite.ExpiresAtUtc <= DateTimeOffset.UtcNow`, throw `HouseholdInviteExpiredOrConsumedException`.
    3. Look up an existing `HouseholdMember` row for `(externalIssuer, externalSubjectId)` via the repository's existing `FindMemberAsync` — if found, throw `HouseholdAlreadyExistsException` (**reuse** the exact exception `CreateHousehold` already uses for "this principal already belongs to a Household" — same invariant, same type, don't invent a second one).
    4. Build a new `HouseholdMember` (`HouseholdId = invite.HouseholdId`, fresh `Id`/`CreatedAtUtc`) and call `AcceptInviteAsync(invite, newMember, cancellationToken)`, returning its result.
  - [x] **AD-1 trap — do not catch `DbUpdateConcurrencyException` (or any EF Core type) in this file.** `EnergyTracker.Application.csproj` currently has zero `PackageReference`s (verified by reading it directly) — no test enforces this the way `DomainHasNoExternalDependenciesTests` enforces it for Domain, so adding an EF Core reference here to catch a concurrency exception would be a silent AD-1 violation that nothing in CI would catch. The concurrency race (Task 3) must be caught and translated **inside `HouseholdRepository.AcceptInviteAsync`** (Infrastructure), which then throws the Application-level `HouseholdInviteExpiredOrConsumedException` — by the time it reaches this file, it's already a plain Application exception.

- [x] Task 3: Infrastructure — persistence (AC #1, #3)
  - [x] `src/EnergyTracker.Infrastructure/Configurations/HouseholdInviteConfiguration.cs`: `ToTable("HouseholdInvites")`, `HasKey(i => i.Id)`, `Property(i => i.Token).HasMaxLength(32).IsRequired()`, `HasIndex(i => i.Token).IsUnique()`, `Property(i => i.Version).IsConcurrencyToken()`.
  - [x] **No AD-3 `HasQueryFilter` on `HouseholdInvite` — the exact same reasoned exception `HouseholdMemberConfiguration.cs` already documents for `HouseholdMember`, for the identical reason.** The accept-by-token lookup (`FindInviteByTokenAsync`) is performed by a principal who, by definition, does not have a resolved `HouseholdId` yet (that's the entire premise of accepting an invite — `ICurrentHouseholdAccessor.HouseholdId` is `null` for them at this point). If the standard `HasQueryFilter(i => i.HouseholdId == _currentHousehold.Id)` were applied, comparing `HouseholdId` (non-nullable `Guid`) against a `null` current-household id would filter out every row, and the join flow could never find any invite — the same circular-dependency trap Story 1.5 documented for `HouseholdMember`. Read `HouseholdMemberConfiguration.cs`'s comment before writing this file; follow the identical reasoning and document it inline the same way. `HouseholdInvite` creation (`AddInviteAsync`) is always called with an already-known-trusted `HouseholdId` from the authenticated creator, so the absence of a filter costs nothing on that path either.
  - [x] Configure the FK without a navigation collection on `Household` (don't add `Household.Invites` — nothing in this story's ACs needs to list a household's invites, and `HouseholdMember`/`HouseholdConfiguration`'s existing pattern already shows a unidirectional FK is fine): `builder.HasOne<Household>().WithMany().HasForeignKey(i => i.HouseholdId).IsRequired().OnDelete(DeleteBehavior.Cascade)` in `HouseholdInviteConfiguration`.
  - [x] Add `DbSet<HouseholdInvite> HouseholdInvites` to `EnergyTrackerDbContext`.
  - [x] `HouseholdRepository.cs` — add the three new `IHouseholdRepository` members:
    - `AddInviteAsync`: straightforward add + `SaveChangesAsync`.
    - `FindInviteByTokenAsync`: `dbContext.HouseholdInvites.SingleOrDefaultAsync(i => i.Token == token, cancellationToken)`.
    - `AcceptInviteAsync(invite, newMember, cancellationToken)`: set `invite.ConsumedAtUtc = DateTimeOffset.UtcNow`, add `newMember`, call `SaveChangesAsync` inside a `try`/`catch (DbUpdateConcurrencyException)` — **this is the one place in the whole story allowed to know about that EF Core type** (see Task 2's AD-1 trap note). On catch, throw `HouseholdInviteExpiredOrConsumedException` (a second, concurrent accept lost the race — `Version`'s mismatch is exactly AD-4's mechanism doing its job). On success, load and return the `Household` (`dbContext.Households.SingleAsync(h => h.Id == invite.HouseholdId, ...)` — `Household` has no AD-3 filter either, it's the tenant root, so this is safe regardless of the calling principal's own resolved household state).
  - [x] Add the migration via `scripts/add-migration.sh AddHouseholdInvites` — both provider projects atomically (AD-2). Portable subset only: `string`, `int`, `DateTimeOffset` columns — nothing provider-specific.

- [x] Task 4: Api — household-invite endpoints (AC #1, #2)
  - [x] `src/EnergyTracker.Api/Endpoints/HouseholdInviteEndpoints.cs`, registered in `Program.cs` next to the existing `api.MapSessionEndpoints(); api.MapHouseholdEndpoints();` line as `api.MapHouseholdInviteEndpoints();` (stays inside the same `/api` `RequireAuthorization()` group — every route requires authentication per the Consistency Conventions/NFR3, no exception here).
  - [x] `POST /api/household-invites` — reads `ICurrentHouseholdAccessor.HouseholdId`; if `null` (authenticated but no Household yet — can't invite people into a Household you're not in), return `403 Forbidden` via `Results.Problem`. Otherwise call `CreateHouseholdInvite.ExecuteAsync(householdId.Value, ...)` and return `200 OK` with `HouseholdInviteResponse(string Token, DateTimeOffset ExpiresAtUtc)`. **No admin/owner check beyond "is a member of this Household" (AC #2)** — any member, including one who joined five minutes ago via someone else's invite, can call this identically; don't add a first-member/creator-only gate anywhere.
  - [x] `GET /api/household-invites/{token}` — side-effect-free preview/validity check via `IHouseholdRepository.FindInviteByTokenAsync` directly (no Application use case needed for a plain read with no business rule beyond "does it exist and is it still usable"). Not found → `404`. Found but `ConsumedAtUtc is not null` or expired → `409 Conflict`. Otherwise `200 OK` with `HouseholdInvitePreviewResponse(DateTimeOffset ExpiresAtUtc)`. **This endpoint must never consume the invite** — some messaging apps auto-fetch link previews via a bot the instant a link is shared/pasted, before the intended recipient ever opens it; if a `GET` had any side effect, a preview-bot could silently burn a single-use invite before the real person clicks anything. (This link never reaches a preview bot without a valid session cookie anyway, since every `/api` route requires auth — but keep the `GET` side-effect-free regardless, as the correct REST semantics and as defense in depth.)
  - [x] `POST /api/household-invites/{token}/accept` — extract `externalIssuer`/`externalSubjectId` from the `ClaimsPrincipal` exactly like `HouseholdEndpoints.cs`'s `POST /households` already does (missing claims → `400`, same pattern, don't diverge). Call `AcceptHouseholdInvite.ExecuteAsync(token, issuer, subject, ...)`; catch `HouseholdInviteNotFoundException` → `404`, `HouseholdInviteExpiredOrConsumedException` → `409`, `HouseholdAlreadyExistsException` → `409` (reusing the exact status code `POST /households` already uses for the same underlying "this principal already has a Household" condition). On success, return `200 OK` with the **existing** `HouseholdResponse(Guid Id, string Locale, string Currency)` record from `HouseholdEndpoints.cs` — don't declare a second, duplicate DTO with the same three fields; import/reuse the one that already exists so the frontend can treat "created a Household" and "joined a Household" identically once either succeeds.
  - [x] Register `CreateHouseholdInvite`/`AcceptHouseholdInvite` in `Program.cs`'s DI (`builder.Services.AddScoped<CreateHouseholdInvite>(); builder.Services.AddScoped<AcceptHouseholdInvite>();`), next to the existing `AddScoped<CreateHousehold>()` line.

- [x] Task 5: Api — `/login` return-URL support (AC #1)
  - [x] `AuthEndpoints.cs`'s `GET /login` currently hardcodes `RedirectUri = "/"`. It needs to support returning the browser to `/join/{token}` after the OIDC round trip, since the invited person's very first click is on that URL, not `/`. Change the handler to accept an optional `string? returnUrl` query parameter (minimal-API model binding does this automatically for a query-string parameter of a nullable primitive type — no extra attribute needed) and use it as `RedirectUri` **only if it passes a strict same-origin-relative-path check**; otherwise fall back to `"/"`.
  - [x] **This is a real open-redirect vulnerability surface if done carelessly — `redirect_uri`-adjacent code is exactly where that class of bug hides.** Add a small private helper, e.g. `IsSafeLocalReturnUrl(string? returnUrl)`, that requires ALL of: non-empty; starts with a single `/`; does not start with `//` or `/\` (both are browser-recognized protocol-relative-URL tricks that can redirect off-origin despite starting with a slash); does not contain `://` anywhere in the string. Reject anything that fails any check and use `"/"` instead — never trust the query value directly as `RedirectUri`.
  - [x] No changes needed to the cookie/OIDC configuration itself — `AuthenticationProperties.RedirectUri` already flows through the existing challenge/sign-in round trip unchanged; this is purely about what value gets passed in.

- [x] Task 6: Frontend — i18n strings (AC #1)
  - [x] Add a `householdInvite` block to both `web/src/locales/en-US/translation.json` and `de-DE/translation.json`, matching the existing flat, component-namespaced key structure (`householdCreation.*` is the direct precedent). Suggested keys (exact wording is a judgment call — follow EXPERIENCE.md's Voice and Tone table: plain-language, specific, human, no exclamation marks/gamification): `generateButton`, `generating`, `linkLabel`, `copyButton`, `copied`, `expiresNote`, `errorGeneric` (generate-side); `acceptHeading`, `acceptDescription`, `acceptButton`, `accepting`, `invalid`, `alreadyInHousehold` (accept-side). Keep both catalogs' key sets identical (Story 1.5's parity discipline — verify by eye or a one-off script, a dedicated parity test remains disproportionate at this catalog size per Story 1.5's own note).

- [x] Task 7: Frontend — invite-generation panel on the placeholder shell (AC #1, #2)
  - [x] `web/src/components/household-invite/invite-generate-panel.tsx` (new folder, mirroring `household-creation/`'s naming convention): a button (`t('householdInvite.generateButton')`) that, on click, `POST /api/household-invites`, then reveals the shareable link built **client-side** (`${window.location.origin}/join/${token}`) — never hardcode a host/origin, this must work identically on self-host and Azure per AD-13/Consistency Conventions. Show the link in a read-only `Input` plus a "Copy link" button (`navigator.clipboard.writeText(...)` — no new dependency needed for this) and a short expiry note. Add this panel into `App.tsx`'s existing `'ready'` state (the placeholder dashboard shell) below the current heading/button — this is the same kind of placeholder-location call Story 1.5 made putting the Household-creation form on a bare page ahead of the real Dashboard (Epic 2) or a real Settings surface (EXPERIENCE.md lists "member invitation (FR-27)" as a future Settings entry — Settings itself doesn't exist as a built surface yet, and Story 1.9's own AC is what first says "reached via Settings"). Don't build a Settings page in this story to house it — that's out of scope and premature; a future story relocates this trigger, it doesn't rebuild it.
  - [x] No new shadcn primitive is required — `Input`/`Button`/`Label` (already scaffolded, Story 1.5) are enough for a link-plus-copy-button panel. Don't add a `Dialog` component for this unless it turns out genuinely necessary; a simple inline reveal keeps this consistent with the "no premature abstraction" discipline the rest of this codebase follows.

- [x] Task 8: Frontend — `/join/{token}` accept flow (AC #1)
  - [x] **This is the first URL in this repo besides `/` that the SPA shell must render distinctly** — Story 1.5's Dev Notes explicitly deferred introducing `react-router` until "whichever later Epic 2+ story first needs multiple real navigable client routes." This story is that trigger, but the need is narrow (exactly one extra path pattern) — **do not add `react-router` or any router library for this.** `web/package.json` has none installed; adding one now for a single path is the over-engineering Story 1.5 deliberately avoided. Instead, parse `window.location.pathname` directly in `App.tsx` with a small regex, e.g. `const inviteToken = window.location.pathname.match(/^\/join\/([^/]+)$/)?.[1] ?? null`.
  - [x] Update the unauthenticated branch: when `inviteToken` is set and `/api/session` returns `401`, navigate to `/login?returnUrl=${encodeURIComponent(window.location.pathname)}` instead of the plain `/login` used elsewhere — otherwise the invited person gets bounced to `/` after login and loses their invite link entirely, defeating AC #1. When `inviteToken` is `null` (the existing, unchanged case), keep navigating to plain `/login`.
  - [x] Update the `'needs-household'` branch: if `inviteToken` is set, render a new `web/src/components/household-invite/invite-accept-form.tsx` instead of `HouseholdCreationForm`. That component: on mount, `GET /api/household-invites/{token}`; `200` → show `acceptHeading`/`acceptDescription` copy plus an explicit `acceptButton` (per FR-1's established "single confirmation tap" pattern — **never auto-accept on page load**, both because a silent side effect on load would be surprising UX and because it removes the last line of defense against a link-preview crawler consuming a single-use invite before the real person acts, even though the `/api` auth gate already mostly prevents that — see Task 4's `GET` note); `404`/`409` → show the `invalid` copy instead of the form, no retry button needed (an expired/consumed/nonexistent invite doesn't become valid by retrying). On accept-button tap, `POST /api/household-invites/{token}/accept`; success → call the same `onCreated`-shaped callback prop (name it `onJoined` for clarity) with the returned `{ id, locale, currency }`, which the caller uses exactly like `HouseholdCreationForm`'s `onCreated` to transition to `'ready'`; `404`/`409` on accept → same `invalid` copy (covers the lost-the-race case too).
  - [x] Update the `'ready'` branch: if `inviteToken` is set (the principal already has a Household and is visiting a stale/foreign invite link), show a brief `alreadyInHousehold` message instead of silently ignoring the link or re-triggering the normal dashboard — matches the product's general "never a broken or silently-ignored state" discipline (FR-26/FR-7's onboarding-empty-state precedent, applied to this new edge case). Keep it to one line; this is not a feature, just graceful handling.

- [x] Task 9: Verify against every AC
  - [x] AC #1: integration test — mirroring `SessionAndHouseholdCreationTests.cs`'s exact pattern (`EnergyTrackerApiFactory.CreateAuthenticatedClient(subject, issuer)` for two distinct principals): principal A creates a Household, then `POST /api/household-invites` as A; principal B (a different subject) has no Household yet, calls `GET /api/household-invites/{token}` (expect `200`), then `POST .../accept` (expect `200` with A's `HouseholdId`/`Locale`/`Currency` echoed back); B's subsequent `GET /api/session` reflects the same Household. Also test: an unauthenticated/unknown token → `404`; an expired invite (construct one directly against the test `DbContext` with `ExpiresAtUtc` in the past, or via a short-lived invite if a constructor param is exposed) → `409`; accepting twice with the same token (second accept, either sequentially or via two near-simultaneous requests) → second call `409`; a principal who already has their own Household attempting to accept → `409` (reusing `HouseholdAlreadyExistsException`'s existing status code).
  - [x] AC #2: integration test — after B joins via A's invite, B (not just A) can successfully `POST /api/household-invites` and produce a second invite that a third principal C can accept into the same Household — proves no creator-only/admin gate exists on invite creation. Also assert B's own `GET /api/session` immediately reflects full access (same `HouseholdId`/`Locale`/`Currency` as A) — no reduced/pending-member state exists anywhere in this design.
  - [x] AC #3: integration test — after B joins A's Household, a fourth, wholly unrelated principal D (never invited, no Household) still gets `HasHousehold: false` from `/api/session` and cannot be confused with A/B's Household — re-affirms the existing AD-3/`ICurrentHouseholdAccessor` tenant-isolation mechanism (built in Story 1.5) continues to hold once a Household has more than one member, which is the first time this codebase actually exercises that multi-member case.
  - [x] Frontend: extend `web/src/App.test.tsx`'s existing mocked-`fetch` pattern with cases for `window.location.pathname = '/join/sometoken'` — mock `/api/session` (401 → asserts navigation to `/login?returnUrl=%2Fjoin%2Fsometoken`, not plain `/login`) and mock `/api/household-invites/sometoken` (200/404/409) to assert `InviteAcceptForm` renders the expected heading/error copy. A component-level test for `InviteAcceptForm`/`invite-generate-panel` (mocking `fetch` and, for the copy button, `navigator.clipboard.writeText`) follows the same Vitest + Testing Library pattern `household-creation-form`'s coverage (inside `App.test.tsx` today) already established.
  - [x] Backend: `AuthEndpoints`' new `returnUrl` handling — a small unit/integration test asserting a malicious `returnUrl` (`//evil.example`, `https://evil.example`, `/\evil.example`) does **not** get echoed into the challenge's `RedirectUri`, while a legitimate `/join/{token}` value does. This is the one piece of this story with real security stakes if it regresses silently.

- [x] Task 10: Documentation
  - [x] If `docs/local-development.md`/`docs/self-hosting.md` document the OIDC login flow at all (check both — Story 1.5 added OIDC setup content to them), add one short note that `/login` now accepts an optional `returnUrl` used internally by the invite-accept flow — informational only, not a new operator-facing configuration surface, so keep it brief.

## Dev Notes

- **No email/notification delivery exists or is architected anywhere in this codebase, and building one is explicitly out of scope for this story.** The PRD's own Open Question 2 ("what delivery channel(s) will ambient/push notification use") is unresolved and the architecture spine has no email/SMTP port or adapter (`AD-6`'s job queue and `AD-8`'s AI client are the only external-facing adapters that exist). "Send an invitation" (AC #1's wording) is therefore implemented as **a shareable link the existing member copies and sends through whatever channel they already use outside the app** (text message, chat, in person) — not an in-app email send. Don't build an SMTP/email adapter for this story; that would be a new architectural capability nothing in Epic 1 calls for.
- **No admin/owner role exists anywhere in this design, by construction, not by omission (AC #2).** `POST /api/household-invites` has exactly one gate: "does the caller have a resolved `HouseholdId`" — the same check every other Household-scoped write in this codebase already relies on via `ICurrentHouseholdAccessor`. There is no `IsOwner`/`Role` field on `HouseholdMember` anywhere, and this story must not add one — the PRD's Non-Goals explicitly rule out "a cross-user admin/management platform."
- **The `HouseholdInvite` no-query-filter exception (Task 3) is this story's version of Story 1.5's single trickiest judgment call** — re-read `HouseholdMemberConfiguration.cs`'s existing inline comment before writing `HouseholdInviteConfiguration.cs`; the reasoning transfers directly (a principal accepting an invite has no resolved `HouseholdId` to filter by yet, so the standard AD-3 filter would make the entire feature return zero rows for the one caller who actually needs it — and `IgnoreQueryFilters()` is explicitly forbidden as the workaround by AD-3 itself).
- **The AD-1 layering trap (Task 2): only `HouseholdRepository.cs` (Infrastructure) may catch `DbUpdateConcurrencyException`.** `EnergyTracker.Application.csproj` has zero package references today (confirmed by reading it directly) — nothing currently stops a developer from adding one, unlike Domain, which `DomainHasNoExternalDependenciesTests` actively enforces. Catching an EF Core exception type inside `AcceptHouseholdInvite.cs` would require adding an EF Core package reference to Application, silently violating AD-1 with no test to catch it. Translate the race in Infrastructure and let only the resulting Application-level `HouseholdInviteExpiredOrConsumedException` cross the boundary — see `HouseholdRepository.AddAsync`'s existing `catch (DbUpdateException)` block for the established shape of this exact pattern (there it's a unique-index collision on concurrent Household creation; here it's a `Version`-token collision on concurrent invite acceptance, but the "catch in Infrastructure, translate to an Application exception" principle is identical).
- **Single-use, expiring, high-entropy tokens are a deliberate security posture, not incidental.** Energy-consumption data is called out explicitly in the PRD's Constraints as sensitive ("a fairly direct proxy for occupancy patterns"), and an invite link is a bearer credential granting **full, permanent, equal-access** membership (AC #2 — there's no lesser "guest" tier to fall back to). A 7-day expiry and single-use consumption (Task 3's `Version`-guarded `AcceptInviteAsync`) bound how long a leaked/forwarded link stays dangerous; there's no revoke-an-outstanding-invite endpoint in this story (not required by any AC) — flag that as a reasonable, deliberately deferred gap in Completion Notes if it comes up, don't silently build it or silently skip mentioning it.
- **This story is the first to genuinely need more than one client-visible URL**, and Task 8 makes a deliberate, bounded choice not to introduce `react-router` for it — re-read Story 1.5's own Dev Notes on this (`web/src/App.tsx`'s Dev Notes/File List) before reaching for a router library. One `window.location.pathname` regex check is proportionate to one new path pattern; a full router is the kind of premature abstraction this codebase has been explicitly avoiding story over story.
- **Open-redirect risk in Task 5 is real, not theoretical** — `returnUrl`-style query parameters feeding into a redirect target are one of the most common web-app vulnerability classes precisely because the naive implementation ("just redirect to whatever was passed") is also the shortest one to write. The validation rule in Task 5 must reject protocol-relative URLs (`//evil.example`) and absolute URLs (`https://evil.example`) in addition to the obvious non-`/`-prefixed case — both are real bypass techniques for a check that only tests `StartsWith("/")`.
- **Constraints that still apply, unchanged:** AD-1 (see above), AD-2 (migration to both provider projects atomically via `scripts/add-migration.sh`), AD-3 (see above), AD-4 (this story is the second entity ever to use the `Version`-concurrency-token pattern — reuse it exactly as `Meter Reading`/`Tariff` are specified to, don't invent a different concurrency mechanism), AD-13 (no hardcoded host/origin anywhere — the shareable invite URL is built from `window.location.origin` at the moment it's generated, so it's correct on both self-host and Azure with zero configuration), AD-18 (every new user-facing string goes through the i18n mechanism, Task 6, no inline literals), NFR3 (every new route stays inside the existing `/api` `RequireAuthorization()` group — nothing in this story is a second unauthenticated endpoint beyond the OIDC callback that already exists).

### Project Structure Notes

New/modified files this story introduces:

```text
energy-tracker-v2/
  src/
    EnergyTracker.Domain/
      HouseholdInvite.cs                          # new
    EnergyTracker.Application/
      Ports/
        IHouseholdRepository.cs                   # modified — 3 new invite methods
      CreateHouseholdInvite.cs                    # new
      AcceptHouseholdInvite.cs                     # new
      HouseholdInviteNotFoundException.cs          # new
      HouseholdInviteExpiredOrConsumedException.cs # new
    EnergyTracker.Infrastructure/
      EnergyTrackerDbContext.cs                    # modified — DbSet<HouseholdInvite>
      Configurations/
        HouseholdInviteConfiguration.cs             # new
      Adapters/
        HouseholdRepository.cs                      # modified — 3 new methods, DbUpdateConcurrencyException handling
    EnergyTracker.Infrastructure.Migrations.Postgres/
      Migrations/{timestamp}_AddHouseholdInvites.cs   # new
    EnergyTracker.Infrastructure.Migrations.SqlServer/
      Migrations/{timestamp}_AddHouseholdInvites.cs   # new
    EnergyTracker.Api/
      Program.cs                                    # modified — DI for the 2 new use cases, api.MapHouseholdInviteEndpoints()
      Endpoints/
        HouseholdInviteEndpoints.cs                  # new
        AuthEndpoints.cs                             # modified — /login returnUrl support
  web/
    src/
      locales/de-DE/translation.json, en-US/translation.json  # modified — householdInvite.* keys
      App.tsx                                        # modified — /join/{token} path handling, returnUrl-aware login redirect
      components/household-invite/
        invite-generate-panel.tsx                     # new
        invite-accept-form.tsx                        # new
  tests/
    EnergyTracker.Api.Tests/
      SessionAndHouseholdCreationTests.cs (or a new HouseholdInviteTests.cs — judgment call, follow whichever keeps the file focused)   # new/modified
      AuthEndpointsTests.cs (or extend an existing auth test file)   # new/modified — returnUrl safety
    web/src/App.test.tsx                              # modified — /join/{token} coverage
  docs/local-development.md, docs/self-hosting.md      # modified if they already document /login — see Task 10
```

Exact file/test organization for the new backend tests is a judgment call (no strict one-test-file-per-feature precedent exists yet — `SessionAndHouseholdCreationTests.cs` already mixes session + household-creation concerns) — pick whatever keeps files focused and note the actual choice in Completion Notes/File List, matching Story 1.5's own precedent for this kind of naming judgment call.

### References

- [Source: _bmad-artifacts/planning/epics/epic-1-foundation-deployment-household-access.md#Story 1.8] — story statement and acceptance criteria (verbatim origin)
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md#FR-27] — Household Member Invitation FR and its testable consequence ("all members have equal, full access — no separate admin/owner role")
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/cross-cutting-nfrs.md] — NFR3 (auth — every route behind authentication except the OIDC callback), tenant isolation NFR (NFR4, referenced in AC #3)
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/constraints-and-guardrails.md] — energy data treated as sensitive/occupancy-proxy; basis for this story's single-use/expiring/high-entropy token posture
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/5-non-goals-explicit.md] — "Not a cross-user admin/management platform" — basis for AC #2's no-admin-role rule
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/8-open-questions.md#2] — notification delivery channel is an open, unresolved question — basis for this story using a shareable link instead of building email delivery
- [Source: ...ARCHITECTURE-SPINE.md#AD-1] — Domain/Application must not depend on EF Core; the specific trap this story's `DbUpdateConcurrencyException` handling must avoid
- [Source: ...ARCHITECTURE-SPINE.md#AD-2] — dual-provider migrations, `scripts/add-migration.sh`, portable column subset
- [Source: ...ARCHITECTURE-SPINE.md#AD-3] — tenant isolation via DbContext global query filter; the `HouseholdMember`-style exemption this story's `HouseholdInvite` needs too
- [Source: ...ARCHITECTURE-SPINE.md#AD-4] — portable `int Version` optimistic concurrency column; reused here for single-use invite consumption instead of a provider-specific mechanism
- [Source: ...ARCHITECTURE-SPINE.md#AD-13] — single-artifact deployment; basis for building the shareable invite URL from `window.location.origin` rather than any hardcoded host
- [Source: ...ARCHITECTURE-SPINE.md#AD-15] — generic-by-default / no hardcoded household-specific values; why the invite lifetime constant is *not* treated as an AD-15 household-scoped config value (it's an operational/security default, not a product value like Yearly Baseline)
- [Source: ...ARCHITECTURE-SPINE.md#AD-18] — i18n additive-catalog requirement for all new UI strings
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md#Information Architecture] — "member invitation (FR-27)" listed as a future Settings surface; Settings itself not yet built (Story 1.9 is what first says "reached via Settings") — basis for this story's placeholder-shell UI location instead of building Settings prematurely
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md#Voice and Tone] — plain-language, specific, human copy; no exclamation marks/gamification — applies to the new i18n strings (Task 6)
- [Source: _bmad-artifacts/implementation/1-5-household-provisioning-via-oidc.md] — previous story with real feature code in this area: `Household`/`HouseholdMember` entity shape, `ICurrentHouseholdAccessor`/`IHouseholdRepository` port shapes, the `HouseholdMember` no-query-filter precedent (Task 3 of that story), `CreateHousehold`'s validation/exception patterns reused directly here, `HouseholdRepository.AddAsync`'s `DbUpdateException`-catch-and-translate pattern (the template for this story's `DbUpdateConcurrencyException` handling), the "no client-side router yet, don't add one prematurely" Dev Notes precedent, the i18n-catalog-parity discipline
- [Source: src/EnergyTracker.Domain/Household.cs, HouseholdMember.cs] — existing entity shape/style this story's `HouseholdInvite.cs` matches
- [Source: src/EnergyTracker.Application/CreateHousehold.cs, HouseholdAlreadyExistsException.cs, HouseholdValidationException.cs] — existing use-case/exception style `CreateHouseholdInvite.cs`/`AcceptHouseholdInvite.cs` match; `HouseholdAlreadyExistsException` is reused directly, not reimplemented
- [Source: src/EnergyTracker.Application/Ports/IHouseholdRepository.cs] — the port this story extends rather than replaces
- [Source: src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs] — existing `AddAsync`'s `catch (DbUpdateException)` block, the direct template for `AcceptInviteAsync`'s `catch (DbUpdateConcurrencyException)`
- [Source: src/EnergyTracker.Infrastructure/Configurations/HouseholdMemberConfiguration.cs] — the exact AD-3 no-filter reasoning this story's `HouseholdInviteConfiguration.cs` must replicate
- [Source: src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs, SessionEndpoints.cs, AuthEndpoints.cs] — existing endpoint file organization/DTO style (`HouseholdResponse` is reused directly, not duplicated); `AuthEndpoints.cs`'s `/login` is the file Task 5 modifies
- [Source: src/EnergyTracker.Api/Program.cs] — existing composition-root DI registration pattern (`AddScoped<CreateHousehold>()`) this story's two new use cases follow; the `/api` `RequireAuthorization()` group every new endpoint stays inside
- [Source: web/src/App.tsx, components/household-creation/household-creation-form.tsx] — existing session-state-machine and form component this story extends with a second path branch and a second form component, matching structure/style
- [Source: web/src/App.test.tsx] — existing mocked-`fetch` Vitest/Testing-Library pattern this story's new frontend tests (Task 9) extend
- [Source: tests/EnergyTracker.Api.Tests/EnergyTrackerApiFactory.cs, TestAuthHandler.cs, SessionAndHouseholdCreationTests.cs] — existing multi-principal test infrastructure (`CreateAuthenticatedClient(subject, issuer)`) this story's AC tests reuse directly, no new test infrastructure needed
- [Source: src/EnergyTracker.Application/EnergyTracker.Application.csproj] — confirmed zero `PackageReference`s today (verified by direct read), the basis for Task 2's AD-1 trap warning
- [Source: https://learn.microsoft.com/en-us/ef/core/saving/concurrency] — EF Core optimistic concurrency / `DbUpdateConcurrencyException` current API shape (AD-4's mechanism) — verify at implementation time

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

- `dotnet build EnergyTracker.sln` — clean build after each layer (Domain/Application/Infrastructure/Api), 0 errors.
- `./scripts/add-migration.sh AddHouseholdInvites` — added `AddHouseholdInvites` migration to both `EnergyTracker.Infrastructure.Migrations.Postgres` and `...Migrations.SqlServer` atomically (AD-2); portable column types only (`Guid`, `string`, `DateTimeOffset`, `int`).
- `dotnet test EnergyTracker.sln` — 59/59 passed (Architecture, Application, Infrastructure, Api.Tests projects), including the 8 new `HouseholdInviteTests` (AC #1/#2/#3) and the `AuthEndpointsTests` returnUrl-safety suite.
- `npx tsc --noEmit`, `npm run lint` (oxlint) — clean (one pre-existing, unrelated warning in `ui/button.tsx`).
- `npm run test -- --run` (Vitest) — 10/10 passed, including the new `/join/{token}` and invite-generation-panel coverage in `App.test.tsx`.

### Completion Notes List

- Implemented all 10 tasks: `HouseholdInvite` domain entity; `CreateHouseholdInvite`/`AcceptHouseholdInvite` use cases plus the two invite exceptions; `IHouseholdRepository` extension and `HouseholdRepository` persistence (including the `DbUpdateConcurrencyException` → `HouseholdInviteExpiredOrConsumedException` translation, the one place outside Domain/Application allowed to know about that EF Core type); `HouseholdInviteConfiguration` with the same AD-3 no-query-filter exception `HouseholdMemberConfiguration` documents; the dual-provider `AddHouseholdInvites` migration; the three `/api/household-invites*` endpoints; `/login`'s `returnUrl` support with an open-redirect allowlist check (`AuthEndpoints.IsSafeLocalReturnUrl`); the `householdInvite.*` i18n catalog (en-US/de-DE, verified key-set parity); `InviteGeneratePanel` and `InviteAcceptForm` React components; and the `/join/{token}` path handling in `App.tsx` (no router library added, matching Story 1.5's precedent).
- Test organization: backend AC coverage lives in a new `HouseholdInviteTests.cs` (kept separate from `SessionAndHouseholdCreationTests.cs` to keep that file focused on its original scope) plus a new `AuthEndpointsTests.cs` for the `returnUrl` safety checks; unit tests for the two new use cases follow `CreateHouseholdTests.cs`'s existing pattern in `EnergyTracker.Application.Tests`. Frontend coverage extends the existing `App.test.tsx` (no dedicated component-test files, matching `household-creation-form.tsx`'s existing precedent of being covered only through `App.test.tsx`).
- `AuthEndpoints.IsSafeLocalReturnUrl` is `internal` with a scoped `InternalsVisibleTo` (added to `EnergyTracker.Api.csproj`, no prior precedent in this repo) so it can be unit-tested directly — a full HTTP round-trip through `GET /login` isn't feasible in the existing test host, since `EnergyTrackerApiFactory` doesn't configure a real OIDC scheme (the `Results.Challenge` call would throw for an unregistered scheme before the `RedirectUri` value it received could be observed).
- Found and fixed a real bug during frontend testing: after `InviteAcceptForm.onJoined` fires, `window.location.pathname` was still `/join/{token}` (no client-side router to navigate away), so the `'ready'` branch's stale-invite check would immediately show "already in household" instead of the dashboard to the person who *just* joined. Fixed by calling `window.history.replaceState({}, '', '/')` in the `onJoined` callback before transitioning state, since `inviteToken` is re-derived from `window.location.pathname` on every render rather than stored in state.
- Deliberately deferred, per Dev Notes: no revoke-an-outstanding-invite endpoint exists (not required by any AC) — a leaked/forwarded invite link is still bounded by the 7-day expiry and single-use consumption, but there is no way for a member to invalidate a specific outstanding invite early.
- `docs/local-development.md` and `docs/self-hosting.md` both already documented the OIDC sign-in flow (Story 1.5) — added one short, informational note to each about `/login`'s new optional `returnUrl` parameter; no new operator-facing configuration surface.

### File List

- `src/EnergyTracker.Domain/HouseholdInvite.cs` (new)
- `src/EnergyTracker.Application/Ports/IHouseholdRepository.cs` (modified)
- `src/EnergyTracker.Application/CreateHouseholdInvite.cs` (new)
- `src/EnergyTracker.Application/AcceptHouseholdInvite.cs` (new)
- `src/EnergyTracker.Application/HouseholdInviteNotFoundException.cs` (new)
- `src/EnergyTracker.Application/HouseholdInviteExpiredOrConsumedException.cs` (new)
- `src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs` (modified)
- `src/EnergyTracker.Infrastructure/Configurations/HouseholdInviteConfiguration.cs` (new)
- `src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs` (modified)
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260814145126_AddHouseholdInvites.cs` (new)
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260814145126_AddHouseholdInvites.Designer.cs` (new)
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/*_AddHouseholdInvites.cs` (new)
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/*_AddHouseholdInvites.Designer.cs` (new)
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/EnergyTrackerDbContextModelSnapshot.cs` (modified)
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/EnergyTrackerDbContextModelSnapshot.cs` (modified)
- `src/EnergyTracker.Api/Endpoints/HouseholdInviteEndpoints.cs` (new)
- `src/EnergyTracker.Api/Endpoints/AuthEndpoints.cs` (modified)
- `src/EnergyTracker.Api/EnergyTracker.Api.csproj` (modified — `InternalsVisibleTo` for `EnergyTracker.Api.Tests`)
- `src/EnergyTracker.Api/Program.cs` (modified)
- `web/src/locales/en-US/translation.json` (modified)
- `web/src/locales/de-DE/translation.json` (modified)
- `web/src/App.tsx` (modified)
- `web/src/components/household-invite/invite-generate-panel.tsx` (new)
- `web/src/components/household-invite/invite-accept-form.tsx` (new)
- `tests/EnergyTracker.Application.Tests/CreateHouseholdInviteTests.cs` (new)
- `tests/EnergyTracker.Application.Tests/AcceptHouseholdInviteTests.cs` (new)
- `tests/EnergyTracker.Api.Tests/HouseholdInviteTests.cs` (new)
- `tests/EnergyTracker.Api.Tests/AuthEndpointsTests.cs` (new)
- `web/src/App.test.tsx` (modified)
- `docs/local-development.md` (modified)
- `docs/self-hosting.md` (modified)

### Change Log

- 2026-08-14: Story 1.8 implementation complete. Added the full Household member-invitation feature end to end: `HouseholdInvite` domain entity (single-use, 7-day-expiring, high-entropy token, `Version`-guarded optimistic concurrency per AD-4); `CreateHouseholdInvite`/`AcceptHouseholdInvite` Application use cases; `HouseholdRepository`'s new invite methods including the `DbUpdateConcurrencyException` → `HouseholdInviteExpiredOrConsumedException` translation (kept out of Application per AD-1); the dual-provider `AddHouseholdInvites` EF Core migration; three new `/api/household-invites*` endpoints; `/login`'s new `returnUrl` query parameter with an open-redirect-safe allowlist check; the `householdInvite.*` i18n catalog; and the frontend's `InviteGeneratePanel`/`InviteAcceptForm` components plus `/join/{token}` path handling in `App.tsx` (no router library added). Found and fixed a real UX bug during test-writing: the URL wasn't cleared after a successful accept, so a freshly joined member briefly saw the "this invite doesn't apply to you" copy instead of the dashboard — fixed via `window.history.replaceState` in the accept-success handler. Full backend suite passes (59/59 across Architecture/Application/Infrastructure/Api.Tests, including 8 new AC-covering integration tests); full frontend suite passes (10/10 Vitest, plus clean `tsc`/`oxlint`). No revoke-invite endpoint exists — deliberately deferred, not required by any AC.

### Review Findings

- [x] [Review][Patch] AD-4 `Version` concurrency token is never incremented, so a single-use invite is not actually race-safe under real concurrent accepts [src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs:56] — fixed: `invite.Version++` before `SaveChangesAsync`; added an integration test asserting the persisted `Version` is non-zero after an accept.
- [x] [Review][Patch] Open-redirect check in `IsSafeLocalReturnUrl` can be bypassed with an embedded tab/CR/LF character (browsers strip these before parsing, turning `/\t/evil.example` into `//evil.example`) [src/EnergyTracker.Api/Endpoints/AuthEndpoints.cs:43] — fixed: reject any `char.IsControl` character; added tab/CR/LF test cases.
- [x] [Review][Patch] `AcceptInviteAsync` only catches `DbUpdateConcurrencyException`, not the general `DbUpdateException` a concurrent accept of two *different* invites by the same principal raises (unique-index race) — surfaces as an unhandled 500 instead of the 409 the sibling `AddAsync` produces for the identical race [src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs:52] — fixed: added a `catch (DbUpdateException)` mirroring `AddAsync`'s existing translation to `HouseholdAlreadyExistsException`.
- [x] [Review][Patch] `InviteAcceptForm` collapses every non-2xx/network failure (including transient 5xx) into the same "invite invalid" state, masking real backend errors — contradicts the 401-vs-other-failure distinction `App.tsx` itself already establishes [web/src/components/household-invite/invite-accept-form.tsx:20] — fixed: added a distinct `error` state (only 404/409 map to `invalid`); new `householdInvite.error` copy in both locales; added tests for both the preview and accept paths.
- [x] [Review][Patch] "Already in Household" and "invalid invite" screens are dead ends with no link/button back to the app [web/src/App.tsx:136] — fixed: added a `householdInvite.backToApp` link to `/` on both screens (and the new `error` screen).
- [x] [Review][Patch] `InviteGeneratePanel.handleCopy` has no error handling around `navigator.clipboard.writeText`, unlike `handleGenerate` in the same file [web/src/components/household-invite/invite-generate-panel.tsx:45] — fixed: wrapped in try/catch, reusing `householdInvite.errorGeneric`; added a rejected-clipboard test.
- [x] [Review][Patch] `INVITE_PATH_PATTERN` doesn't match a trailing-slash variant (`/join/token/`), silently falling through to the normal (non-invite) flow [web/src/App.tsx:25] — fixed: regex now accepts an optional trailing slash; added a test.
- [x] [Review][Patch] `GET /household-invites/{token}` comment's stated threat (anonymous preview-bot) can't reach the endpoint, since the whole route is already behind `RequireAuthorization()` — misleading rationale for a future maintainer [src/EnergyTracker.Api/Endpoints/HouseholdInviteEndpoints.cs:29] — fixed: corrected the comment to state the real rationale (REST semantics + defense in depth against a future auth-policy change).
