---
title: 'Sprint Change Proposal — CI/CD Image Reset & OIDC Callback Scheme Mismatch'
type: 'sprint-change-proposal'
created: '2026-08-14'
status: 'approved'
---

# Sprint Change Proposal: CI/CD Image Reset & OIDC Callback Scheme Mismatch

## 1. Issue Summary

Two production-blocking defects were discovered on 2026-08-13 during post-deployment verification of Story 1.5 (Household Provisioning via OIDC), while responding to a related production incident (missing EF Core migration on Azure SQL, resolved separately).

**Issue A — `infra-deploy.yml` resets the Container App image.** Whenever `infra-deploy.yml` runs `az deployment group create` against `container-app.bicep`, it deploys with `image: placeholderImage` (`mcr.microsoft.com/k8se/quickstart:latest`), unconditionally overwriting whatever real application image `app-deploy.yml` last deployed. `app-deploy.yml` already patches around two adjacent gaps ("Ensure ACR pull credential and target port" step), but does not restore the image. Confirmed live: running `infra-deploy.yml` to sync a rotated database credential silently reverted the running Container App to the placeholder image, causing a second, unrelated production outage until `app-deploy.yml` was re-run.

**Issue B — OIDC login fails with a callback URL scheme mismatch.** The identity provider (Auth0) rejects the login attempt: `redirect_uri=http://.../signin-oidc` doesn't match the whitelisted `https://.../signin-oidc`. Root cause identified: `src/EnergyTracker.Api/Program.cs:174-177` configures `UseForwardedHeaders` for `X-Forwarded-Proto` (needed because Container Apps terminates TLS at its ingress and forwards plain HTTP to the container) but never clears `ForwardedHeadersOptions.KnownNetworks`/`KnownProxies` from their ASP.NET Core defaults (loopback-only). Per documented framework behavior, `ForwardedHeadersMiddleware` only honors `X-Forwarded-*` headers when the immediate connecting proxy matches a known entry; Container Apps' internal ingress doesn't, so the header is silently ignored and `Request.Scheme` stays `http`. The adjacent code comment asserts this trust is "safe" but the configuration never actually enables it.

**Evidence:** Auth0 tenant log (`CallbackMismatchError`, `authorized: ["https://.../signin-oidc"]` vs `attempt: "http://.../signin-oidc"`); `az containerapp show` image diff before/after an `infra-deploy.yml` run; both already logged in `_bmad-artifacts/implementation/deferred-work.md`.

**Impact:** Issue B currently blocks all further app development and testing that requires an authenticated session against a real deployment. Issue A makes any infrastructure-only change (secret rotation, SKU change, etc.) risk silently taking the running app down.

## 2. Impact Analysis

**Epic Impact:** Epic 1 (Foundation, Deployment & Household Access) — both issues sit inside this epic's own domain (deploy pipeline, OIDC auth) and currently block it from completing. Story 1.6 (Household Member Invitation) and 1.7 (Room/Power Point/Device Management), both still `backlog` with no story files yet, cannot be meaningfully built or tested against a real deployment until login works and infra changes stop interfering with deploys. No other epic's scope is affected; Epics 2–7 all assume working authentication but haven't started yet.

**Story Impact:** Two new stories added to Epic 1, sequenced immediately after Story 1.5 and before the existing invitation/room-management work:
- New Story 1.6: CI/CD Deploy Idempotency — Container App Image Preservation
- New Story 1.7: OIDC Redirect URI Scheme Correctness Behind Container Apps Ingress
- Existing Story 1.6 (Household Member Invitation) renumbered to **1.8**
- Existing Story 1.7 (Room, Power Point & Device Management) renumbered to **1.9**

Renumbering was low-risk: neither existing story has a story file yet (both still `backlog`), and only `epics/index.md`'s table-of-contents links referenced the old numbers.

**Artifact Conflicts:**
- PRD: none — FR-26/NFR3 (auth) are unaffected; these are implementation defects, not requirement changes.
- Architecture: none — Issue A touches AD-13's deploy model only at the workflow-YAML level; Issue B touches AD-17's auth-persistence config only at the `Program.cs` level. Neither architectural decision needs to change.
- UX: none — no user-facing flow or screen changes.
- Other artifacts: `infra/main.bicep` / `infra/modules/container-app.bicep` / `infra-deploy.yml` (Issue A fix location); `src/EnergyTracker.Api/Program.cs` (Issue B fix location); `_bmad-artifacts/implementation/deferred-work.md` entries for both issues are superseded by these two stories.

**Technical Impact:** Both fixes are narrow and localized — no data migration, no API contract change, no cross-cutting refactor.

## 3. Recommended Approach

**Selected: Option 1 — Direct Adjustment.** Add two new stories within Epic 1's existing structure; no epic redefinition, no rollback, no MVP scope change.

- **Option 2 (Rollback) rejected:** nothing to roll back to — both gaps have existed since Story 1.2/1.5 landed, not introduced by a recent regression. Rolling back Story 1.5 would re-break OIDC entirely (worse than the current state).
- **Option 3 (MVP Review) rejected:** MVP scope is entirely unaffected; these are implementation bug fixes, not scope or requirement questions.

**Effort/Risk:** Low/Low for both. Issue A: thread the currently-deployed image through `infra-deploy.yml` (or read it via `az containerapp show` before redeploying) — a few lines. Issue B: add `options.KnownNetworks.Clear(); options.KnownProxies.Clear();` to the existing `ForwardedHeadersOptions` block — already diagnosed, a two-line fix plus a live-login verification pass.

## 4. Detailed Change Proposals

### Epics — `_bmad-artifacts/planning/epics/epic-1-foundation-deployment-household-access.md`

**New Story 1.6: CI/CD Deploy Idempotency — Container App Image Preservation**

As a platform operator, I want `infra-deploy.yml` to never revert the Container App to the placeholder image, so that running an infrastructure-only change (e.g. rotating a secret) doesn't silently take production down.

- Given the Container App is currently running a real deployed image, when `infra-deploy.yml` runs, then it continues running that same image afterward — `infra-deploy.yml` never overwrites `properties.template.containers[0].image` back to `placeholderImage`.
- Given a brand-new environment with no image ever deployed, when `infra-deploy.yml` runs for the first time, then it still provisions the Container App successfully using the placeholder image (Story 1.2's original bootstrap behavior is preserved).
- Given `infra-deploy.yml` and `app-deploy.yml` are both idempotent, when either runs multiple times in any order, then the final state is always: Container App running the most recently app-deployed image, with whatever secrets/config `infra-deploy.yml` most recently applied.

**New Story 1.7: OIDC Redirect URI Scheme Correctness Behind Container Apps Ingress**

As anyone authenticating against a production deployment, I want the app's OIDC `redirect_uri` to always use `https`, matching the identity provider's whitelisted callback URL, so that login succeeds instead of failing with a callback URL mismatch.

- Given the app runs behind Azure Container Apps' ingress (TLS-terminating, forwards plain HTTP internally), when the OIDC handler builds `redirect_uri`, then it uses `https://`, matching exactly what's registered with the OIDC provider.
- Given `ForwardedHeadersOptions` is configured for `X-Forwarded-Proto`/`X-Forwarded-For`, when the middleware evaluates a request from Container Apps' ingress, then it actually trusts and applies that header — not silently ignored due to default `KnownNetworks`/`KnownProxies` restrictions.
- Given a full login round trip against the real configured OIDC tenant in production, when a user visits `/login`, then they reach the identity provider's own login page without a "Callback URL mismatch" error.

**Renumbered:** Story 1.6 "Household Member Invitation" → **Story 1.8** (content unchanged). Story 1.7 "Room, Power Point & Device Management" → **Story 1.9** (content unchanged).

**Rationale:** Both new stories fix defects that actively block further Epic 1 work (per user directive); sequencing them ahead of 1.8/1.9 ensures a working, stable deployment before building features that depend on authenticated sessions.

### Index — `_bmad-artifacts/planning/epics/index.md`

Table-of-contents links updated to add Story 1.6/1.7 entries and renumber the former 1.6/1.7 to 1.8/1.9.

### Sprint Status — `_bmad-artifacts/implementation/sprint-status.yaml`

```yaml
1-5-household-provisioning-via-oidc: done
1-6-cicd-deploy-idempotency-container-app-image-preservation: backlog
1-7-oidc-redirect-uri-scheme-correctness-behind-container-apps-ingress: backlog
1-8-household-member-invitation: backlog
1-9-room-power-point-device-management: backlog
```

## 5. Implementation Handoff

**Scope classification: Minor.** Both stories are directly implementable by the Developer agent (`bmad-dev-story` / `bmad-quick-dev`) — no PO backlog reorganization and no PM/Architect replan needed.

**Responsibilities:**
- **Developer agent:** Implement Story 1.6 and 1.7 (in that order — image-reset fix first, since it's already interfering with normal deploy operations; then the OIDC scheme fix, since it's the harder blocker for further testing). Verify Story 1.7 with a live login round trip against the real Auth0 tenant, not just a local/unit check.
- **User (Ralf):** Approve story files when generated (`bmad-create-story`); confirm live login works after Story 1.7 ships.

**Success criteria:**
- Story 1.6: Two consecutive `infra-deploy.yml` runs, with an `app-deploy.yml` run in between, leave the Container App running the app-deployed image, not the placeholder.
- Story 1.7: `/login` in production redirects to the OIDC provider with an `https://` `redirect_uri` and completes a full login round trip without a callback mismatch error.
