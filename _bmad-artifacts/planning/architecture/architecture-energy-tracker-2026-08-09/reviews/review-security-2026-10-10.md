# Security Review — 2026-10-10 spine amendments (codebase-audit remediation)

**Reviewer:** Security-lens Reviewer Gate
**Scope:** `ARCHITECTURE-SPINE/invariants-rules.md` — `[AMENDED 2026-10-10 …]` bullets in AD-6 (C7/C8), AD-17 (S2), AD-20 (C9), and new AD-24, AD-25, AD-26, AD-27; the three new end entries of `ARCHITECTURE-SPINE/deferred.md`. Checked against `docs/codebase-audit-2026-10-10.md` and the code in `src/`, `web/`, `infra/` and `.github/`.
**Date:** 2026-10-10

## Verdict

**Changes requested before stories are cut. The direction is right, but AD-26 is not safe as written.**

- **AD-25 closes S1 and S12.** The restore file no longer controls access. Other fields in the file still have some effect (listed below), but none of them grant or remove access.
- **AD-17 (S2)** closes the "DB read is enough to forge a session" path. It needs two guardrails: the opt-out must be impossible in Azure, and the PR CI identity must stop holding Owner on the Key Vault's scope.
- **AD-26 (member removal) introduces new gaps:**
  - It can be bypassed through pending invites, because S10 is still undecided.
  - It acts on the one entity that has no global tenant filter.
  - It has a last-member race that can leave a household with no members.
  - It does not define what happens to destructive jobs the removed member queued before removal.
- **AD-27 (S3/S8)** is mostly sound. Four problems:
  - Its `/login` fallback lets one client block sign-in for the whole instance.
  - Its `AllowedHosts` instruction would break the deploy health gate.
  - Its rate-limit partition key is undefined for invite preview and accept, because the caller is not yet a member.
  - Its CSP leaves `style-src` open in a way the Radix dialogs will run into.

The audit's S9 (CSRF), S10 (invites) and S13 (session lifetime) are still neither decided nor deferred. S10 and part of S9 now have to be in the spine, because AD-26 and AD-27 depend on them (finding 11).

| # | Severity | Area | One-line |
|---|----------|------|----------|
| 1 | HIGH | AD-26 × S10 | A removed member can rejoin with any pending invite. Removal does not revoke invites. |
| 2 | HIGH | AD-26 × AD-3 | `HouseholdMember` has no query filter, so the new remove and list endpoints are cross-tenant IDOR targets unless the spine says otherwise. |
| 3 | MEDIUM | AD-26 | Last-member check is check-then-delete. Two concurrent removals or leaves can leave a household with zero members. |
| 4 | MEDIUM | AD-26 | "Remove everyone else" is S1's lock-out under another name. Accept it explicitly, record removals, and define what removal does to in-flight work. |
| 5 | MEDIUM | AD-26 × C2 | Removal relies on membership being resolved per request. The audit's own C2 fix could add caching and break that. |
| 6 | MEDIUM | AD-27 / S8 | The global `/login` fallback lets anyone block sign-in. Pinning the ingress range is likely impossible and probably not needed. `AllowedHosts` breaks the revision-FQDN health gate. |
| 7 | MEDIUM | AD-27 / S3 | Rate-limit partition "per authenticated member" is undefined for invite preview and accept. Limiter placement is not specified. |
| 8 | MEDIUM | AD-27 / S3 | CSP: Radix's scroll lock injects `<style>` elements, so `style-src` needs a decision now. The header middleware's position in the pipeline must be specified. `form-action` is missing. |
| 9 | MEDIUM | AD-27 / S9 | `SameSite=Lax` does not stop requests from sibling subdomains. A custom-header check on `/api` writes is cheap and belongs in AD-27. |
| 10 | MEDIUM | AD-17 × S11 | The Key Vault protection is only as strong as the Owner-scoped identity that every same-repo PR can use. |
| 11 | MEDIUM | Gaps | S10 and S13 are neither decided nor deferred. S11 has no home. |
| 12 | LOW | AD-17 | Opt-out guardrails: refuse it in Azure, keep it out of `.env.example`, define what happens when both protectors are set, purge revoked plaintext keys. |
| 13 | LOW | AD-25 | Restore-completion UI matching by display name. Members API must not expose identities. AI-consent flag is restored from the file. Restored audit rows. |
| 14 | LOW | AD-6 | Marking a job stale while it is still running allows a second concurrent destructive restore. |
| 15 | LOW | AD-26 | Client handling for a removed member: offline queue, cached data, telling two members with the same or empty display name apart. |

---

## Q1 — Do the rules close the findings they claim to?

| Audit | Closed? | Evidence / residual |
|---|---|---|
| **S1** restore membership | **Yes** | Today: `RestoreHouseholdData.cs:57,70-78` copies issuer and subject from the file. `HouseholdRestoreWriter.cs:66-69` deletes every member. `:143` re-inserts the members from the file. `ValidateHouseholdImport.cs:145-152` only checks shape. AD-25 removes `HouseholdMember` from both the delete and the insert phase, which closes grant, lock-out and the issuer/subject probe. Things the file does **not** touch: `HouseholdInvite` is not exported, deleted or restored, and the `Household` row is updated in place (`HouseholdRestoreWriter.cs:158-170`, `Id` never taken from the file). The fields that still matter are listed in finding 13. None of them affects access. |
| **S12** export identities | **Yes, for the export file.** | The export reader (`HouseholdExportReader.cs:113`) feeds `HouseholdExportEndpoints.cs:144`. AD-25 drops issuer and subject. Residual: the new member-list endpoint AD-26 needs is a second place that could leak them (finding 13). `HouseholdImportEndpoints.cs:179` reports a `HouseholdMembers` count in the validate preview. That count no longer means "members who will exist after the restore", so the preview wording must change. |
| **S2** DP keys | **Yes, with conditions** | `Program.cs:277-295`: today the keys are stored in plaintext unless a certificate is configured. Moving to Key Vault plus failing closed is correct. Conditions: findings 10 and 12. |
| **S3** headers / HSTS / rate limiting | **Mostly** | `Program.cs:479-538` has no headers, no `UseHsts` and no limiter. AD-27 adds all three. Gaps: findings 6–9. |
| **S8** forwarded headers | **Partly. The premise needs re-checking.** | `Program.cs:470-479` clears `KnownIPNetworks`/`KnownProxies`. `ForwardLimit` stays at its default of 1, so the middleware uses only the **right-most** `X-Forwarded-For` entry. That entry is the one the Container Apps Envoy adds, not one the client sent, as long as the container cannot be reached except through ingress. Verify this before story work starts (finding 6). The `AllowedHosts` part has a deployment trap (finding 6). |
| **A2 / S13** removal | **A2: yes, with new gaps. S13: half.** | AD-26 adds removal, but it can be bypassed (finding 1), it lacks tenant scoping (finding 2) and it has a race (finding 3). AD-26 does not decide S13's other half, session lifetime (finding 11). |
| **C7 / C8** job liveness | Not security, apart from finding 14. | — |
| **C9** | Not security. | — |

---

## Findings

### 1. [HIGH] Removal is bypassable through pending invites: the removed member rejoins

**Evidence**
- An invite is a 7-day bearer credential for full membership (`src/EnergyTracker.Domain/HouseholdInvite.cs:9-17`). There is no `CreatedByMemberId`, no revoked state and no list endpoint.
- Any member can create invites (`HouseholdInviteEndpoints.cs:11-29`).
- Accepting one only requires that the caller has no household yet (`HouseholdInviteEndpoints.cs:57-98`, `HouseholdRepository.cs:51-61`).
- After removal, the former member's cookie is still valid. They are authenticated and have no household, which is exactly the state in which accept succeeds.
- The audit's S10 asks for "list and revoke". The spine neither decides nor defers it.

**Attack:** member X sees that removal is coming, or simply mints an invite in advance. X can do this at any point, and nobody else can see the invite. After removal, X opens `/join/{token}` and is a member again within a second. X can also use a second IdP account.

Any invite link shared earlier, for example in a family chat that X can read, does the same job.

As written, AD-26's "a household can cut off a former member" (A2/S13) does not hold.

**Spine fix (AD-26 rule, add):**
> **Removal revokes every pending invite of the Household in the same transaction.** `HouseholdInvite` gains `RevokedAtUtc` (expand-only migration); the accept path treats revoked like consumed (409, same message). Pending invites are listable and individually revocable by any member (S10). Invite tokens are generated with `RandomNumberGenerator` (≥256 bits, base64url) and stored only as a SHA-256 hash; lookups hash the presented token. Test: create invite → remove creator → accept with that invite as a fresh identity returns 409.

Revoking *all* of the household's pending invites is coarse. It is still the only correct choice while invites have no creator column, and re-issuing an invite is cheap.

### 2. [HIGH] Removal and member listing act on the one household-scoped entity that has no global filter

**Evidence:** `HouseholdMemberConfiguration.cs:31-40` deliberately exempts `HouseholdMember` from the AD-3 query filter. Every existing query scopes it by hand: `HouseholdExportReader.cs:113`, `HouseholdRestoreWriter.cs:67`, `BackgroundJobRepository.cs:19`, `CurrentHouseholdAccessor.cs:84`.

AD-26 adds `DELETE /api/household-members/{id}` (or similar) and a member list for the removal UI. These are the first endpoints that take a **client-supplied** member id. If the household check is missing, a member of household A who learns a member id from household B can remove members of B. That is a cross-tenant lock-out. Member ids are GUIDs, but they appear in exports and API responses. The safety net every other entity has (the AD-3 filter plus the existing architecture test pattern) does not apply here.

**Spine fix (AD-26 rule, add):**
> `HouseholdMember` is exempt from AD-3's global filter, so every member-targeting endpoint (list, remove, leave) loads the target with an explicit `HouseholdId == currentHouseholdId` predicate and returns **404** (never 403) when it doesn't match. An architecture test — extending `HouseholdExportReaderDoesNotBypassTenantIsolationTests` — asserts every `HouseholdMembers` query outside `CurrentHouseholdAccessor`/`HouseholdRepository.FindMemberByIdentity` carries a `HouseholdId` predicate. Integration test: member of A removing a member of B → 404, B unchanged.

### 3. [MEDIUM] Last-member check races: mutual removal or simultaneous leave leaves zero members

AD-26 says "removing the last member is rejected", but not how. A two-member household where both members click Remove or Leave at once runs two transactions. Each sees `count = 2`, each deletes one row, and the result is zero members.

At zero members the household and all its data are unreachable, and there is no recovery path in the UI. The audit lists that exact lock-out outcome under S1. Neither Postgres nor SQL Server prevents this under the default READ COMMITTED isolation level.

**Spine fix (AD-26 rule, add):**
> Removal and leave run in one transaction that re-counts members **and** bumps `Household.Version` (AD-4's portable concurrency token) — a concurrent removal fails on the version check and is retried or surfaced; never a bare count-then-delete. Test: two concurrent removals in a two-member household → exactly one succeeds, one member remains, on both providers.

### 4. [MEDIUM] "Remove everyone else" is S1's lock-out under another name. Accept it on purpose, and define what happens to in-flight work.

"All members fully trusted" plus "any member removes any member" is **coherent**. The household is the trust boundary, and a member who wants to do damage can already wipe everything with a restore. But AD-26 justifies itself only against *grant* ("AD-25 removes the only path by which a member could grant access"). It says nothing about *lock-out*, which was half of S1.

The spine should state the decision instead of leaving it implied. Three more points need a decision:

- **There is no record of who removed whom.** The audit's A2 already notes there is no audit trail. Once removal can lock people out, "who removed me?" must have an answer.
- **Destructive jobs queued before removal still run.** `BackgroundJob.QueuedByHouseholdMemberId` uses SetNull when the member row is deleted (`BackgroundJob.cs:24`, `BackgroundJobConfiguration.cs:30-43`). The job processor runs the job with the household from the job envelope and no member identity (`CurrentHouseholdAccessor.cs:69-74`). A restore or cleanup that the about-to-be-removed member queued runs **after** removal. Under the Azure queue, that can be up to AD-6's two-hour Queued window.
- **Upload tokens are safe.** `MemoryHouseholdImportUploadRegistry.Consume(token, householdId)` is bound to the household, and confirming needs a request that resolves a household. A removed member cannot confirm. No change is needed. Say so, so that nobody "fixes" it later.

**Spine fix (AD-26 rule, add):**
> *Lock-out is an accepted consequence of full trust:* any member may remove all others; recovery from a hostile removal is operator-level (DB) only, documented in `docs/self-hosting.md`. Each removal/leave is recorded (`HouseholdMemberRemoval`: removed member's display name, removing member id, timestamp — never restored or exported, AD-25), shown in Settings, and logged at Information. *In-flight work:* at dequeue, a job of a destructive type (restore, Smart Plug cleanup) whose `QueuedByHouseholdMemberId` is null or no longer a member is marked `Failed` ("requester is no longer a member") instead of executed; non-destructive jobs (Smart Plug import) still run.

### 5. [MEDIUM] Removal depends on per-request resolution, and the audit's C2 fix could break that

`CurrentHouseholdAccessor.cs:80-88` queries membership once per request scope. That is what makes AD-26's "takes effect on the next request" true. The same audit's C2 recommends moving this into middleware or an `IClaimsTransformation`, with caching in `HttpContext.Items`. That is fine. But a developer could also reasonably add an `IMemoryCache` entry or a cookie claim for the household id, to save the database round trip. Either one would silently keep removed members in, until the cache expires or for the cookie's whole lifetime.

**Spine fix (AD-26 rule, add):**
> Membership is resolved from the database on every request and cached at most for that request (`HttpContext.Items`); it is never cached across requests or written into the auth cookie. (This constrains C2's fix.)

### 6. [MEDIUM] AD-27 forwarded headers: the fallback lets anyone block sign-in, the precondition is probably unachievable and probably unnecessary, and `AllowedHosts` breaks deploys

- **The global `/login` partition lets one client block sign-in for everyone.** One client looping on `/login` uses up the instance-wide budget, and nobody can sign in. That is worse than the abuse it guards against. `/login` only issues a challenge redirect and sets correlation and nonce cookies. It is cheap, and the IdP throttles credential attempts itself. The unauthenticated route that costs more is `/signin-oidc`, where the OIDC handler runs as remote authentication middleware. That route already rejects requests without a valid DP-protected `state` before making any outbound call.
- **The precondition:** in Consumption Container Apps without a VNet, Envoy's addresses are not stable, so "restrict `KnownIPNetworks` to the ingress range" probably cannot be done. It is also probably not needed. `ForwardLimit` defaults to 1 and is not overridden (`Program.cs:470-479`), so the middleware uses the right-most `X-Forwarded-For` value. Envoy appends that value, and a client cannot forge it. A client-supplied prefix is ignored, as long as the container cannot be reached directly, which `Program.cs:462` already relies on. One manual check settles this: send a forged `X-Forwarded-For` and log `RemoteIpAddress`.
- **Self-host has no ingress.** `docs/self-hosting.md:78` exposes `api` on port 8080. Forwarded headers must be opt-in through configuration there. Otherwise every self-host per-IP partition can be spoofed.
- **`AllowedHosts` breaks the health gate.** `app-deploy.yml:382-388` probes `latestRevisionFqdn` (`<app>--<rev>.<env-domain>`), which is not one of the "real hostnames". With host filtering on, `/health` returns 400 and every deploy rolls back.

**Spine fix (AD-27, replace the "Forwarded headers first" bullet):**
> **Forwarded headers:** `ForwardLimit = 1` is pinned explicitly; on Azure the right-most `X-Forwarded-For` entry (appended by Container Apps ingress, the only path to the container) is the client IP — verified at story time by a spoofed-header probe, recorded in the story. Self-host trusts forwarded headers only when `ForwardedHeaders:KnownNetworks` is configured (default: none → direct peer address). `AllowedHosts` lists the custom domain, the app FQDN **and** `*.<environment default domain>` (revision FQDNs used by the deploy health gate). **No global (non-partitioned) limiter on any unauthenticated route**; if a trustworthy client IP cannot be established, `/login` is not rate-limited at all.

### 7. [MEDIUM] Rate-limit partitions: "per authenticated member" does not exist for the invite routes

`GET /api/household-invites/{token}` and `POST …/accept` sit behind `RequireAuthorization()` (`Program.cs:519`, `HouseholdInviteEndpoints.cs:31-36`), so "unauthenticated invite preview" does not exist. The caller is authenticated, but by definition is **not a member**, so `HouseholdMemberId` is null and "per authenticated member" has no key to use.

The partition key must be the OIDC identity, (validated issuer, subject). Accounts at most public IdPs are free, so this limits rate, not the number of identities. With S10's 256-bit tokens, guessing is not a realistic risk anyway. The limiter must run **after** `UseAuthentication` (`Program.cs:507`) to see the principal. 429 responses must not count as a failed submission in the offline queue (AD-16).

**Spine fix (AD-27 rate-limiting bullet):**
> Partitions: per OIDC identity (validated issuer + subject) for upload, export and all invite routes (preview/accept callers are not yet members); `UseRateLimiter` sits after `UseAuthentication`. Meter-reading writes and `GET /api/jobs/{id}` polling are not limited.

### 8. [MEDIUM] CSP details that are better decided now than found in production

- **`style-src`:** `radix-ui` Dialog, Sheet and Select use `react-remove-scroll` → `react-style-singleton`, which runs `document.createElement('style')` at runtime (`web/node_modules/react-style-singleton/dist/es2015/singleton.js:5`). Under `default-src 'self'` with no `'unsafe-inline'` for styles, that element is blocked: scroll lock breaks and violations are reported. This only shows up when a dialog actually opens. "Verified against the production bundle" is a static check and will miss it. The spine rules out nonces, so `style-src 'self' 'unsafe-inline'` is the realistic setting. Style injection is a low-impact XSS primitive compared with script injection. Inline style attributes that React sets through CSSOM are not affected. The built CSS has no `data:` URLs (checked in `wwwroot/assets/*.css`), so `img-src`/`font-src` can stay `'self'`. `connect-src 'self'` is correct, because the AI call is made by the server.
- **Pipeline position:** `UseAuthentication` handles `/signin-oidc` and stops the pipeline there. `UseStaticFiles` and `MapFallbackToFile` serve `index.html` (`Program.cs:507-538`). A middleware registered after those would miss the SPA shell and the callback responses. Register it **first** and set headers in `Response.OnStarting`, so error and redirect responses also get them.
- **Clickjacking through the OIDC callback:** this is not exploitable. The callback is a POST (the default `response_mode=form_post`) from the IdP's page, and `frame-ancestors 'none'` on every app response covers the rest. That is only true if the previous point is implemented.
- **`form-action` does not fall back to `default-src`.** Add `form-action 'self'`. The app posts no forms, so nothing breaks.
- **The inline theme script still ships.** `src/EnergyTracker.Api/wwwroot/index.html` has two `<script>` tags, one of them inline. That confirms AD-27's move to `theme-init.js` is necessary.

**Spine fix (AD-27 headers bullet):** state the policy as `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`. Add: "registered first in the pipeline; headers applied via `OnStarting` to every response including static files, redirects and errors; verified by an E2E test that opens a dialog with the production CSP and asserts zero CSP violations."

### 9. [MEDIUM] S9: `SameSite=Lax` does not stop sibling subdomains. Add a cheap header check to AD-27.

`SameSite` is about the *site* (the registrable domain), not the origin. Two cases matter:

- In production with the custom domain (`container-app.bicep:49-53`), any other subdomain of the same registrable domain counts as same-site. That includes any static site, Vercel deployment or preview hosted there. A compromised or XSS-vulnerable sibling can then send cookie-carrying `POST`/`DELETE` requests, such as restore-confirm, member removal or invite create.
- On the default `*.azurecontainerapps.io` host, protection depends on whether that suffix is on the Public Suffix List. **Verify this.**

`DisableAntiforgery()` on the multipart routes (`HouseholdImportEndpoints.cs:97-98`) is documented and fine for cross-*site* requests, but it does not help against same-site ones. AD-26 adds a new destructive, state-changing route, which raises the stakes.

**Spine fix (AD-27, add a bullet):**
> **CSRF defence in depth:** every non-GET `/api` request must carry `X-Requested-With: XMLHttpRequest` (rejected with 400 otherwise); the SPA sets it in one shared fetch helper. A cross-origin page cannot add a custom header without a CORS preflight, and no CORS policy exists. `/logout` becomes POST (the SPA already drives logoff — AD-17's FR-33 extension).

### 10. [MEDIUM] AD-17's Key Vault protection is only as strong as the PR what-if identity (S11)

`infra/README.md:227-261` gives **one** user-assigned identity `Owner` on the resource group, plus federated credentials for both `main` and `pull_request`. All three workflows use the same `secrets.AZURE_CLIENT_ID` (`pr-review.yml:157`, `app-deploy.yml:105`, `infra-deploy.yml:74`). So any same-repo PR branch runs with Owner on the group that will hold the new Key Vault.

Owner can grant itself *Key Vault Crypto User*, unwrap the DP keys, read the database (it can also edit firewall rules) and forge sessions. That is the exact threat AD-17's amendment says it closes. The audit's S11 already flags the identity. This is the reason it matters to the spine.

**Spine fix (AD-17 S2 amendment, add):**
> The protection is only effective once no CI identity reachable from a pull request holds Owner/User Access Administrator over the Key Vault's scope: `pr-review.yml`'s what-if uses a separate identity with **Reader** (plus what-if's needed read actions) only; the `pull_request` federated credential is removed from the deploy identity. The Key Vault has purge protection enabled (an accidental key deletion would otherwise invalidate every session irrecoverably).

### 11. [MEDIUM] Security items that are neither decided nor deferred

| Audit | Status in spine | Recommendation |
|---|---|---|
| **S10** invite tokens | Missing | **Spine (AD-26).** Removal is unsafe without it (finding 1). Hashing, revocation and listing are one rule. |
| **S13** session lifetime | Half (removal only) | **Spine (AD-17 one-liner).** Cookie lifetime is a product trade-off ("stays logged in on the phone") and belongs where AD-17 makes that promise. Proposal: `ExpireTimeSpan = 14 days`, `SlidingExpiration = true`, plus an **absolute cap of 90 days** from `IssuedUtc`, enforced in `OnValidatePrincipal`. No session store, which is consistent with the FR-33 note in AD-17. |
| **S9** CSRF header | Missing | **Spine (AD-27 bullet)**: it is a pipeline-wide rule (finding 9). |
| **S11** CI identity / Actions pinning / Dependabot | Missing | **The identity split goes in the spine** (finding 10, because it is what makes AD-17 true). SHA-pinning, Dependabot, CodeQL and `npm audit` are **stories/ops only**. |
| S4 upload amplification / zip bomb | Not in scope of these amendments | Story-level. It interacts with AD-24's temp files: "one pending upload per household" fits AD-24 naturally. Consider one line there. |
| S5, S6, S7 | Not in scope | Stories. S7 (production fallback credentials) belongs in the same "fail closed outside Development" logic as AD-17. Mention it there so both are written once. |

### 12. [LOW] AD-17 opt-out guardrails

- **Azure must refuse the opt-out.** Startup throws when `DataProtection:AllowUnprotectedKeys=true` is combined with Container Apps (`CONTAINER_APP_NAME` is present) or with `DataProtection:KeyVaultKeyId` being set. Bicep never sets the flag.
- **`.env.example` and `docker-compose.yml` must not ship with the flag set.** Otherwise copy-paste turns the "explicit choice" back into the default. The self-host docs show how to generate the certificate instead.
- **Both protectors configured:** define the behaviour. Throw, do not pick one silently.
- **Revocation at cutover:** use `IKeyManager.RevokeAllKeys` once. After the longest cookie lifetime (finding 11), delete the revoked plaintext key rows, so later backups and restore points stop carrying them.

### 13. [LOW] AD-25 leftovers: the restore file still controls a few things

- **The completion UI cannot match members by display name.** New exports have no identities, so "backup members not members here" can only compare display names. Those come from the IdP, may be null (`HouseholdMember.cs:16-19`) and are not unique. The UI should **list** the backup's display names and offer to invite, never claim that someone "is already a member". Old v2 files with identities may be matched on (issuer, subject). Display names must never feed any authorization or identity decision.
- **The member-list API that AD-26 needs** returns `Id`, `DisplayName`, `CreatedAtUtc` and `IsCurrentMember` only, never issuer or subject. That keeps S12 closed for the API and not only for the file. Rule: *no API response or file exposes `ExternalIssuer`/`ExternalSubjectId`.*
- **AI consent comes from the file.** `AiPlausibilityEnabled` is restored (`HouseholdRestoreWriter.cs:167`). An old backup can switch event-description egress to the AI endpoint back on after the household turned it off. All members are trusted, so this is not an escalation, but it is a consent setting and should not change silently. Keep the current value, or show it in the restore preview.
- **Restored audit rows.** `AuditCorrection` rows come from the file, so the audit history can be forged. That is acceptable inside AD-11's restore carve-out. Any **new** accountability record, such as finding 4's removal log, must stay outside the restore and export set.
- **C10 oracle.** Global PK and `IdempotencyKey` collisions give a weak "this id exists in another household" signal, the same shape as S1's probe. Ids are random and only appear in that household's own export, so the impact is negligible. C10's re-keying fix removes it.

### 14. [LOW] AD-6: marking a running job stale allows a second concurrent destructive restore

A restore that is still running at 30 minutes, for example a large file on Basic-tier SQL, is marked `Failed`. Restore-confirm's "one active restore" check then passes, and a second restore can start in parallel with the first: two delete-all-then-insert transactions on one household.

**Spine fix (AD-6 amendment):**
> The processor's terminal write is conditional on the row still being `Processing`. If it was marked stale in the meantime, the outcome is logged and the row is not overwritten.
>
> Restore takes AD-23's per-household advisory lock (or `HouseholdRecomputeLock`, which is valid under AD-24) for its whole transaction, so a second restore waits instead of interleaving.

### 15. [LOW] Client handling for a removed member

- **Offline queue:** readings from a removed member's offline queue get a 403 "no household" response. Under C4 they are dropped silently today. The client should show a "you were removed from this household" state for that 403 and **clear the cached household data in IndexedDB**. Otherwise a removed member keeps a local copy of data they no longer have access to.
- **Telling members apart:** the removal UI must be able to tell members apart when display names are null or the same, for example by showing the join date and a "You" marker. Otherwise the wrong person gets removed.
- **Product scope:** AD-26 adds a product capability that has no FR yet. Flag it to John (PRD Household & Access) so the stories have acceptance criteria to test against.

---

## What holds up well

- AD-25's "restore never writes `HouseholdMember`" is the minimal, correct fix. It also closes S1's probe oracle, and keeping `formatVersion` at v2 is justified.
- AD-17 fails closed and logs the protector it selected. That is the right shape. Key Vault with the app's existing managed identity, scoped to the key only, follows least privilege.
- AD-24 is right to "observe, not enforce", because Container Apps briefly runs two revisions during a deploy. The in-process rate limiter's two-instance window is harmless.
- AD-27 bans inline scripts and avoids CSP hashes and nonces. That keeps `script-src 'self'` strict, which is the part of a CSP that matters most.
