---
baseline_commit: 5f31c2ef4893b005e0c81f9071327ae5c3046d61
---

# Story 1.7: OIDC Redirect URI Scheme Correctness Behind Container Apps Ingress

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As anyone authenticating against a production deployment,
I want the app's OIDC `redirect_uri` to always use `https`, matching the identity provider's whitelisted callback URL,
so that login succeeds instead of failing with a callback URL mismatch.

## Acceptance Criteria

1. **Given** the app runs behind Azure Container Apps' ingress (TLS-terminating, forwards plain HTTP internally), **when** the OIDC handler builds `redirect_uri` for the authorize request, **then** it uses `https://`, matching exactly what's registered as the allowed callback URL with the OIDC provider.
2. **Given** `ForwardedHeadersOptions` is configured for `X-Forwarded-Proto`/`X-Forwarded-For`, **when** the middleware evaluates an incoming request from Container Apps' ingress, **then** it actually trusts and applies that header — not silently ignored due to default `KnownNetworks`/`KnownProxies` restrictions.
3. **Given** a full login round trip against the real configured OIDC tenant in production, **when** a user visits `/login`, **then** they reach the identity provider's own login page without a "Callback URL mismatch" error.

## Tasks / Subtasks

- [x] Task 1: Fix `ForwardedHeadersOptions` so the middleware actually trusts Container Apps' forwarded headers (AC #1, #2)
  - [x] In `src/EnergyTracker.Api/Program.cs:174-177`, change the `app.UseForwardedHeaders(new ForwardedHeadersOptions { ... })` call so the options object also clears the known-network/proxy allowlists before being passed in. Implemented with `KnownIPNetworks.Clear()` rather than the story draft's `KnownNetworks.Clear()` — `KnownNetworks` is obsolete on .NET 10 (`ASPDEPR005`, "Please use KnownIPNetworks instead"), caught by a build warning during implementation; `KnownIPNetworks` is the direct non-deprecated replacement, same semantics.
  - [x] Left the existing comment block at lines 168-173 untouched — it already asserts exactly this trust decision; the fix makes the code match what the comment already claimed.
  - [x] Confirmed via the Task 2 regression test: red (http) before this change, green (https) after.

- [x] Task 2: Add a regression test proving the header is actually trusted from an untrusted-by-default peer (AC #2)
  - [x] Created `tests/EnergyTracker.Api.Tests/ForwardedHeadersTests.cs`.
  - [x] Implemented the `FakeRemoteIpStartupFilter` technique exactly as specified — overrides the connecting `RemoteIpAddress` to a non-loopback address (`10.0.0.4`) before `Program.cs`'s own pipeline runs, avoiding the TestServer-loopback-default false-positive gotcha.
  - [x] Observed the post-middleware `Request.Scheme` via a second `IStartupFilter` (`SchemeObservingStartupFilter`) that reads `ctx.Request.Scheme` after calling `next()`, confirmed to reflect the value after `UseForwardedHeaders` has run.
  - [x] **Red/green verified during implementation, not just assumed:** ran the test against the code before Task 1's fix — failed, observed scheme `http`. Applied Task 1's fix, ran again — passed, observed scheme `https`. Full solution test suite (`dotnet test`, 32 tests across all 4 test projects) also passes with no regressions.
  - [x] No separate `redirect_uri`-value test added for AC #1, per the story's own reasoning (pure downstream consequence of `Request.Scheme`, no additional code path).

- [x] Task 3: Live login verification against the real OIDC tenant (AC #3) — cannot be automated in this session
  - [x] Same honesty-discipline precedent Stories 1.2–1.6 established for infra/production-environment behavior: this cannot be verified end-to-end in this session (no live Azure Container Apps deployment or real Auth0 tenant reachable here).
  - [x] Stated plainly in Completion Notes that Task 1/2 are verified by code inspection + the automated regression test, not by an actual production login.
  - [x] Flagged that Ralf must do one real `/login` visit against the production deployment after this ships and confirm: (a) the browser reaches Auth0's own login page with no "Callback URL mismatch" error, (b) a full login round trip completes and lands back on the app authenticated. This directly matches the sprint-change-proposal's stated success criterion for this story.

- [x] Task 4: Verify against every AC
  - [x] AC #1: satisfied by Task 1's fix + the Task 2 reasoning trace (no separate redirect_uri-value test — see Task 2's last bullet for why).
  - [x] AC #2: satisfied by Task 2's automated regression test, confirmed red-before-fix / green-after-fix.
  - [x] AC #3: cannot be verified in this session — flagged for Ralf's manual post-deploy verification (Task 3).

### Review Findings

- [x] [Review][Defer] Unvalidated forwarded-header trust newly reachable in production [tests/EnergyTracker.Api.Tests/ForwardedHeadersTests.cs, src/EnergyTracker.Api/Program.cs:174-183] — deferred, reason: self-limited blast radius (attacker can only affect their own request's scheme/cookie decision, no cross-user impact); keeps Story 1.7 scoped to its stated four-line surgical fix rather than widening it mid-story

- [x] [Review][Patch] AC #2 regression test doesn't assert the `X-Forwarded-For` effect [tests/EnergyTracker.Api.Tests/ForwardedHeadersTests.cs:56-66] — fixed: test now also observes `ctx.Connection.RemoteIpAddress` post-pipeline and asserts it equals the sent `X-Forwarded-For` value
- [x] [Review][Patch] `Program.cs` safety comment overclaims for the self-host deployment target [src/EnergyTracker.Api/Program.cs:172-173] — fixed: comment now covers both deployment targets, mirroring the Dev Notes' self-host reasoning
- [x] [Review][Patch] Undocumented `Insert(0)` ordering dependency makes the regression test fragile [tests/EnergyTracker.Api.Tests/ForwardedHeadersTests.cs:73-83] — fixed: added an explanatory comment on why the insertion order is load-bearing
- [x] [Review][Patch] Test doc comment overstates fidelity of `10.0.0.4` to the real Container Apps peer [tests/EnergyTracker.Api.Tests/ForwardedHeadersTests.cs:15-19] — fixed: reworded to avoid the unsubstantiated fidelity claim

- [x] [Review][Defer] `ForwardLimit` left at default (1), untested for multi-hop proxy chains [src/EnergyTracker.Api/Program.cs:174-177] — deferred, pre-existing; no multi-hop topology exists today (verified: no Front Door/App Gateway/CDN in `infra/`)
- [x] [Review][Defer] Middleware pipeline ordering (`UseForwardedHeaders` before anything reading `Request.Scheme`) has no dedicated regression test [src/EnergyTracker.Api/Program.cs:183-190] — deferred, pre-existing pipeline structure, unchanged by this diff

## Dev Notes

- **This story exists because of a real production incident, not a hypothetical.** On 2026-08-13, login against Auth0 failed with `CallbackMismatchError`: the app generated `redirect_uri=http://.../signin-oidc` (note `http`) while Auth0 only has `https://.../signin-oidc` whitelisted. Full incident context: `_bmad-artifacts/implementation/deferred-work.md`, section "Follow-up: OIDC callback URL scheme mismatch (2026-08-13)", and `_bmad-artifacts/planning/sprint-change-proposal-2026-08-14.md`.
- **Root cause, precisely (independently reproduced during story creation, not just inferred from the incident writeup):** `src/EnergyTracker.Api/Program.cs:174-177` configures `app.UseForwardedHeaders(...)` for `XForwardedFor | XForwardedProto` — necessary because Azure Container Apps terminates TLS at its ingress and forwards plain HTTP internally (the adjacent comment at lines 168-173 already explains this). But `ForwardedHeadersOptions.KnownNetworks`/`KnownProxies` are left at ASP.NET Core's defaults (loopback-only: `127.0.0.0/8` and `::1`). Per `ForwardedHeadersMiddleware`'s documented behavior, it only honors `X-Forwarded-*` headers when the *immediate* connecting peer's IP matches a `KnownProxies` entry or falls inside a `KnownNetworks` CIDR. Container Apps' internal ingress peer IP matches neither default, so the header is silently ignored and `Request.Scheme` stays `http`. **Reproduced with a spike test during story creation:** simulating a non-loopback connecting IP (`10.0.0.4`, standing in for Container Apps' real internal ingress peer) against the current unfixed code, with `X-Forwarded-Proto: https` sent, `Request.Scheme` after the middleware runs stayed `http` — confirmed broken. Applying the two-line fix in Task 1 (`KnownNetworks.Clear(); KnownProxies.Clear();`) and re-running the identical scenario flipped it to `https` — confirmed fixed. This is the exact fix `deferred-work.md`'s incident writeup already proposed; it has now been empirically validated, not just theorized.
- **The TestServer loopback gotcha is the single most important thing to get right in this story.** `WebApplicationFactory`'s in-memory `TestServer` connects with `RemoteIpAddress = 127.0.0.1` by default — which is *already* inside ASP.NET Core's default `KnownNetworks` (`127.0.0.0/8`), even without any fix. A test that doesn't explicitly override the connecting IP to something non-loopback will pass identically whether or not Task 1's fix is applied — a false-positive "regression test" that provides zero actual coverage. This was independently discovered and confirmed during story creation (see Task 2's exact technique, which does correctly reproduce and then verify the fix).
- **AC #1's `redirect_uri` value is a pure downstream consequence, not a separate code path.** `src/EnergyTracker.Api/Endpoints/AuthEndpoints.cs`'s `/login` handler is `Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme])` — it builds no URL itself. ASP.NET Core's `OpenIdConnectHandler` derives `redirect_uri` from `Request.Scheme` + `Request.Host` internally. Fixing `Request.Scheme` (Task 1) is the entire fix; no changes to `AuthEndpoints.cs` or the `AddOpenIdConnect(...)` options block (`Program.cs:104-134`) are needed or expected.
- **Why clearing `KnownNetworks`/`KnownProxies` entirely (rather than allowlisting) is the right and safe call, for both deployment targets:**
  - **Azure:** the existing code comment (`Program.cs:172-173`) already establishes Container Apps' external ingress is the *only* path into the container — there's no untrusted network position an attacker could occupy to forge these headers directly to the app.
  - **Self-host** (`docs/self-hosting.md`): exposes the container's port directly via `docker compose` with no reverse proxy documented or required. Checked during story creation: nothing in this codebase reads `HttpContext.Connection.RemoteIpAddress` or acts on `X-Forwarded-For` for any security-relevant decision (no rate limiting, no IP-based auth logic) — `grep -rn "RemoteIpAddress\|X-Forwarded-For" src/` returns nothing. The only consumer of `Request.Scheme` is the OIDC `redirect_uri` build and the cookie's `SecurePolicy.Always` flag-marking, neither of which is exploitable by a client lying about its own scheme. `XForwardedHost` is deliberately *not* included in `ForwardedHeaders` here (only `XForwardedFor | XForwardedProto`), so this change has no bearing on host-header trust. Net effect: clearing the allowlists has no realistic security cost for this app today, on either deployment target.
- **AD-17 (Session persistence via server-side cookie)** is the nearest architectural anchor — this story's defect sits upstream of AD-17's cookie mechanism entirely (in the OIDC challenge/redirect step, before any session cookie is ever written), so AD-17 itself needs no change; this story just makes the login step that precedes it work correctly behind Container Apps.
- **No environment-specific branching is introduced** (consistent with AD-19's operational-baseline discipline) — the fix is a single, unconditional middleware config change that applies identically regardless of `Database:Provider` or any other config value.
- **Constraints that still apply, unchanged:** Consistency Conventions' "Config-driven adapter selection" (OIDC config is still read exactly once at the composition root, `Program.cs:53-55` — untouched by this fix); never log/echo secret values (not applicable here — nothing in this fix touches `Oidc:ClientSecret`).

### Project Structure Notes

Files this story touches — small, surgical change, no new production files:

```text
energy-tracker-v2/
  src/EnergyTracker.Api/
    Program.cs                         # modified — ForwardedHeadersOptions now clears
                                        # KnownNetworks/KnownProxies (lines 174-177)
  tests/EnergyTracker.Api.Tests/
    ForwardedHeadersTests.cs           # new — regression test per Task 2
```

No changes to `AuthEndpoints.cs`, the `AddOpenIdConnect(...)` options block, `infra/`, or any GitHub Actions workflow. If implementation reveals a need to touch any of those, stop and reconsider — this story's own root-cause analysis (verified, not assumed) says the fix is exactly these four lines in `Program.cs`.

### References

- [Source: _bmad-artifacts/planning/epics/epic-1-foundation-deployment-household-access.md#Story 1.7] — story statement and acceptance criteria (verbatim origin)
- [Source: _bmad-artifacts/planning/sprint-change-proposal-2026-08-14.md] — why this story exists, the two production incidents it responds to, and its explicit success criteria
- [Source: _bmad-artifacts/implementation/deferred-work.md#"Follow-up: OIDC callback URL scheme mismatch (2026-08-13)"] — original incident diagnosis and proposed fix, confirmed correct and empirically reproduced during this story's creation
- [Source: src/EnergyTracker.Api/Program.cs:166-177] — the file and exact lines this story modifies; existing comment already documents the intended (but not yet implemented) trust decision
- [Source: src/EnergyTracker.Api/Endpoints/AuthEndpoints.cs] — `/login` handler; confirms `redirect_uri` is framework-derived from `Request.Scheme`/`Request.Host`, not manually constructed
- [Source: tests/EnergyTracker.Api.Tests/EnergyTrackerApiFactory.cs] — existing `WebApplicationFactory<Program>` + Testcontainers Postgres setup pattern to reuse for the new test's factory
- [Source: tests/EnergyTracker.Api.Tests/DataProtectionColdStartTests.cs] — this repo's established pattern for a test with its own dedicated `WebApplicationFactory` setup (not the shared `EnergyTrackerApiFactory` fixture) when the scenario needs non-default host configuration — same shape Task 2's new test follows
- [Source: docs/self-hosting.md] — confirms self-host exposes the container directly with no documented reverse proxy, informing the "why clearing the allowlist is safe" Dev Notes reasoning
- [Source: ...ARCHITECTURE-SPINE.md#AD-17] — session persistence via server-side cookie; nearest architectural anchor, unaffected by this fix
- [Source: ...ARCHITECTURE-SPINE.md#AD-19] — operational baseline / no environment-specific branching discipline, satisfied by this fix's unconditional single config change
- [Source: _bmad-artifacts/implementation/1-6-cicd-deploy-idempotency-container-app-image-preservation.md] — previous story; established this sprint's precedent for honestly flagging "cannot verify end-to-end against live Azure" and requiring one real post-merge manual check (Task 3 here follows the identical pattern)

## Dev Agent Record

### Agent Model Used

claude-sonnet-5 (Claude Code)

### Debug Log References

None — no interactive debugging needed. Verification was via `dotnet test` runs (see Completion Notes for the specific red/green sequence) and `dotnet build`/`dotnet format --verify-no-changes` for static checks.

### Completion Notes List

- Fixed `src/EnergyTracker.Api/Program.cs`'s `ForwardedHeadersOptions` to clear its known-network/proxy allowlists so Container Apps' internal ingress peer is actually trusted, exactly as the story's root-cause analysis specified — with one deviation from the story's literal code snippet: used `KnownIPNetworks.Clear()` instead of `KnownNetworks.Clear()`. `KnownNetworks` is obsolete on .NET 10 (build warning `ASPDEPR005`, "Please use KnownIPNetworks instead") — this repo targets `net10.0` — so `KnownIPNetworks` is the correct non-deprecated replacement with identical semantics. `KnownProxies.Clear()` is unchanged (not deprecated).
- Added `tests/EnergyTracker.Api.Tests/ForwardedHeadersTests.cs` implementing the story's specified `FakeRemoteIpStartupFilter` technique (overrides the TestServer's connecting `RemoteIpAddress` to a non-loopback `10.0.0.4` before `Program.cs`'s pipeline runs, avoiding the TestServer-loopback-default false-positive the story's Dev Notes warned about) plus a `SchemeObservingStartupFilter` to read `Request.Scheme` after the whole inner pipeline (including `UseForwardedHeaders`) has executed.
- **Red/green sequence actually run during this session, not assumed:** ran the new test against the code before the `Program.cs` fix — failed, log showed `Request finished HTTP/1.1 GET http://localhost/health` (scheme stayed `http`). Applied the fix. Ran the test again — passed, observed scheme `https`.
- Ran the full solution test suite (`dotnet test` at repo root, all 4 test projects): 32/32 passed, 0 failures — no regressions introduced.
- Ran `dotnet build src/EnergyTracker.Api/EnergyTracker.Api.csproj`: 0 warnings, 0 errors (confirms the `ASPDEPR005` deprecation warning is fully resolved, not just worked around).
- Ran `dotnet format --verify-no-changes --no-restore`: no formatting diffs.
- AC #1 and AC #2 are fully verified (automated). **AC #3 (live login round trip against the real production Auth0 tenant) is explicitly NOT verified in this session** — no live Azure Container Apps deployment or real Auth0 tenant is reachable here, matching the same honesty-discipline precedent Stories 1.2–1.6 established. Ralf must, after this ships, do one real `/login` visit against the production deployment and confirm: (a) the browser reaches Auth0's own login page with no "Callback URL mismatch" error, (b) a full login round trip completes and lands back on the app authenticated.

### File List

- `src/EnergyTracker.Api/Program.cs` — modified: `ForwardedHeadersOptions` now clears `KnownIPNetworks` and `KnownProxies` so `X-Forwarded-For`/`X-Forwarded-Proto` from Container Apps' ingress are actually trusted.
- `tests/EnergyTracker.Api.Tests/ForwardedHeadersTests.cs` — new: regression test proving the header is trusted from a non-loopback peer (simulating Container Apps' real ingress), using a fake-remote-IP `IStartupFilter` to avoid TestServer's loopback-default false positive.

### Change Log

- 2026-08-14: Story 1.7 implementation complete. Fixed the production OIDC callback-scheme-mismatch bug by clearing `ForwardedHeadersOptions.KnownIPNetworks`/`KnownProxies` in `Program.cs`, so Container Apps' ingress-forwarded `X-Forwarded-Proto: https` is actually trusted instead of silently ignored. Added a regression test (`ForwardedHeadersTests.cs`) that reproduces the exact bug via a fake non-loopback remote IP and confirms red-before-fix/green-after-fix. Deviated from the story's literal `KnownNetworks.Clear()` snippet in favor of the non-deprecated `KnownIPNetworks.Clear()` (.NET 10 marks `KnownNetworks` obsolete). Full solution test suite passes (32/32); AC #3's live production login round trip is not verified in this session and is flagged for Ralf to confirm manually post-deploy.
