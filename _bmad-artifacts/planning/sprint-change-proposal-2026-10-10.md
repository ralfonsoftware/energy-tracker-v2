# Sprint Change Proposal — 2026-10-10

**Trigger:** codebase audit `docs/codebase-audit-2026-10-10.md` (commit `6212bdc`).
**Mode:** Batch. **Scope classification:** Moderate (new epic, backlog additions; architecture and PRD decisions already made and recorded).
**Decisions behind it:** Winston (architecture) and John (PRD) sessions with Ralf, 2026-10-10.

## 1. Issue Summary

An independent audit of `src/`, `web/`, `tests/`, `infra/` and CI rated the codebase *Good* for architecture and testing, but *Fair* for security, correctness and maintainability. It found:

- **High:** restore rewrites `HouseholdMembers` from the uploaded file, so any member can grant access to arbitrary OIDC identities or lock everyone out (S1).
- **Medium:** unencrypted Data Protection keys in Azure (S2); no security headers or rate limiting (S3); upload memory and disk amplification (S4); container hardening (S5); DB firewall scope (S6); an Azure queue consumer that can stop the host and retries poison messages forever (C7); orphaned `Processing` jobs that block restore permanently (C8); concurrent unmapped imports overwriting each other (C9); an AI config key mismatch (C1); a synchronous DB query on every request (C2); temp-file job payloads (C3); unenforced single-replica assumptions (A1); no member authorization model (A2).
- **Low:** S7–S13, C4–C6, C10–C12, A3–A4, R1–R3.

**Issue type:** technical debt and security hardening discovered by review. This is not a requirement change. The exception is C12, where the audit found an ambiguity in FR-6; it has been clarified.

**Evidence:** each finding in the audit is marked Verified or Inferred, with file:line. The architecture decisions were reviewed by four independent reviewers: `architecture-energy-tracker-2026-08-09/reviews/review-{rubric-walker,version-currency,adversarial-divergence,security}-2026-10-10.md`.

## 2. Impact Analysis

### Epic impact

| Epic | Impact |
|---|---|
| **New Epic 11** | Created: 19 stories, in the tiers Now / Soon / Later. |
| Epic 10 (Deferred-Work Hardening) | None. All three stories are done; only its optional retro remains. It is kept separate because it has a different origin. |
| Epic 9 (AI backend, backlog) | **Text ripple only.** Its stories reference `AiPlausibility:BaseUrl`. Story 11.11 (C1) settles on the `Ai:*` section, so the Epic 9 wording is updated in the same change. No scope change. |
| Epic 7 (Export/Restore, done) | Behaviour changes, delivered through Story 11.1 (AD-25). The shipped stories are not reopened. |
| Epic 1 (Household & Access, done) | Extended by Stories 11.8 and 11.9 (logout as POST, session cap, member removal, revocable invites). The shipped stories are not reopened. |
| Epic 2 (Status, done) | Behaviour change through Story 11.15 (C12, FR-6 clarified). Forward-only. |
| Epic 3 (Smart Plug, done) | Behaviour changes through Stories 11.2–11.4 (job pipeline, unmapped key). |
| Epic 5 (in progress) | None. |

### Artifact conflicts — already resolved today

- **Architecture spine** (`ARCHITECTURE-SPINE/invariants-rules.md`):
  - AD-6, AD-17 and AD-20 amended on 2026-10-10.
  - New ADs: AD-24 (single-instance invariant), AD-25 (restore and export carry no access grants), AD-26 (member trust model, removal, revocable invites), AD-27 (HTTP hardening baseline).
  - `deferred.md`: multi-instance shared state and the Owner role are deferred; C12 is resolved.
  - All of this went through the reviewer gate, and the fixes are applied.
- **PRD** `prd/4-features.md`: FR-6 now defines the trending threshold as annual and scaled to the elapsed period, and states that *Below baseline* is asymmetric on purpose. FR-30 shows both the configured and the scaled threshold.
- **UX:** no spec changes. The new UI surfaces are member list/remove/leave, the open-invites list, the "people in this backup" panel after a restore, and the "needs attention" state for the offline queue (11.16). Each follows existing Settings and card patterns. Story 11.16 has an *Ask First* for Sally if its placement isn't obvious.

### Secondary artifacts touched by stories

- **IaC:** a new `key-vault.bicep` (11.6), a Reader-scoped identity for PR what-if runs (11.5), and the `maxReplicas` description (11.13).
- **CI:** SHA-pinned actions, Dependabot, CodeQL and an image scan (11.5); an analyzer/format gate (11.18).
- **Docs:** `self-hosting.md` covers Data Protection (a breaking change), the reverse-proxy requirement, the single-instance rule and operator recovery. `data-import-restore.md` and `data-export-format.md` follow AD-25, `infra/README.md` gets the key-revocation runbook, and the release notes call out the breaking change.
- **`project-context.md`:** rules from AD-24 to AD-27 are added once the stories that implement them land (the handoff below says when).

## 3. Recommended Approach

**Direct adjustment:** add one new epic. No rollback and no MVP change.

- **Rollback was considered and rejected.** The flaws sit inside otherwise correct, shipped features. A targeted fix is smaller and safer than reverting Epic 7 or Epic 3 work.
- **MVP review is not needed.** No goal changes. C12 is a clarification of FR-6, not a scope change.

**Effort:**

| Tier | Stories | Effort |
|---|---|---|
| Now | 6 (11.1–11.6) | ≈ 2 S, 3 M, 1 M/L |
| Soon | 5 (11.7–11.11) | ≈ 1 S, 4 M |
| Later | 8 (11.12–11.19) | mostly S–M |

**Risks:**
- **11.6 fails closed.** It is a breaking change for self-hosters, mitigated by docs, an explicit opt-out and a release note.
- **11.4 needs two deploys (N, then N+1).** Between them the transitional state fails loudly; it never overwrites silently.
- **11.6, 11.7, 11.8 and 11.9 touch authentication.** The live Auth0/Chrome gate applies to each.

**Sequencing constraints:**
- 11.3 depends on 11.2.
- 11.6 depends on 11.5, because the Reader-scoped CI identity must exist before Key Vault holds anything worth protecting.
- 11.8 lands before or with any new mutation endpoints. 11.9 then uses the shared API client and the CSRF filter.
- 11.12's per-request cache must respect the AD-17 rule that membership is resolved per request.
- Everything else is independent.

## 4. Detailed Change Proposals

### 4.1 New file — `planning/epics/epic-11-codebase-audit-hardening.md`

Full content is in **Appendix A**. One story file per story is created later via `bmad-create-story`, as with Epic 10.

### 4.2 `planning/epics/epic-list.md` — append after Epic 10

```
## Epic 11: Codebase Audit Hardening
Fixes the findings of the 2026-10-10 codebase audit (`docs/codebase-audit-2026-10-10.md`), implementing the architecture decisions recorded the same day (AD-6/AD-17/AD-20 amendments, AD-24–AD-27) and the FR-6 trending-threshold clarification. Nineteen stories in three tiers — Now (restore access grants, job pipeline liveness, unmapped Smart Plug key, Data Protection key encryption and its CI-identity prerequisite), Soon (HTTP hardening, auth hardening, member removal and revocable invites, upload hardening, container and config correctness), Later (performance, documentation, maintainability). No new FR; hardens FR-17, FR-22/23, FR-26–28, FR-33 and clarifies FR-6/FR-30.
**FRs covered:** none new — FR-6/FR-30 clarified (C12); hardens FR-4, FR-22, FR-23, FR-26–FR-28, FR-33
```

### 4.3 `planning/epics/index.md`

Add the Epic 8–11 entries (the TOC currently stops at Epic 7) and the Epic 11 story links.

### 4.4 `planning/epics/epic-9-wattage-plausibility-ai-backend-decision.md`

Replace the references to `AiPlausibility:BaseUrl` with the `Ai:*` config section that Story 11.11 settles on. The text is otherwise unchanged:

```
OLD: …no `AiPlausibility:BaseUrl` has ever been configured.
NEW: …no AI endpoint (`Ai:Endpoint`, unified by Story 11.11) has ever been configured.
```

### 4.5 `implementation/sprint-status.yaml` — append after epic-10

```yaml
  epic-11: backlog
  11-1-restore-export-never-carry-access-grants: backlog
  11-2-job-lifecycle-conditional-transitions-stale-recovery: backlog
  11-3-azure-queue-consumer-resilience: backlog
  11-4-unmapped-smart-plug-readings-keyed-by-device: backlog
  11-5-ci-identity-split-supply-chain-hygiene: backlog
  11-6-data-protection-key-encryption: backlog
  11-7-security-headers-csp-rate-limiting: backlog
  11-8-auth-hardening-csrf-logout-session-cap: backlog
  11-9-member-removal-revocable-invites: backlog
  11-10-upload-hardening: backlog
  11-11-container-config-correctness: backlog
  11-12-async-per-request-household-resolution: backlog
  11-13-single-instance-invariant-documentation: backlog
  11-14-restore-id-collisions-active-job-guard: backlog
  11-15-trending-threshold-scaled-to-elapsed: backlog
  11-16-offline-queue-rejected-readings-attention: backlog
  11-17-timeprovider-recompute-lock-eviction: backlog
  11-18-backend-analyzer-format-gate: backlog
  11-19-structural-cleanup: backlog
  epic-11-retrospective: optional
```

Also bump `last_updated` to 2026-10-10.

### 4.6 Already applied today (listed for traceability, no further edit)

- `ARCHITECTURE-SPINE/invariants-rules.md`, `index.md`, `deferred.md` and `.memlog.md`
- `prds/prd-energy-tracker-2026-08-08/prd/4-features.md` (FR-6, FR-30) and the PRD `.memlog.md`

## 5. Implementation Handoff

**Scope: Moderate.** It adds backlog and a new epic. The strategic decisions are already made, so no PM or Architect escalation is needed.

| Role | Responsibility |
|---|---|
| **SM / Dev (Amelia)** | `bmad-create-story` per story, in tier order: 11.1, 11.2, 11.3, 11.4, 11.5, 11.6, then Soon, then Later. Each story file cites its AD and audit IDs, which satisfies the spec-per-fix gate. |
| **Dev** | Implement the stories. Live Auth0/Chrome verification is mandatory for 11.6, 11.7, 11.8 and 11.9 before review → done. For 11.4, deploy N+1 is part of the story's definition of done. |
| **Ralf** | Run the out-of-band steps: verify the role of the current PR what-if identity (11.5); revoke the Data Protection keys after cutover (11.6); probe with a forged `X-Forwarded-For` on Azure (11.7); publish the self-host release note (11.6). |
| **Tech Writer (optional)** | Update `project-context.md` with the AD-24–27 rules after 11.6 and 11.7 land. Before that, the stories carry the rules themselves. |

**Success criteria:**
- Every "Fix first" audit item (S1, C7, C8, C9, S2, S3) is closed, with a regression test named in its story.
- Audit §7's test gaps are closed: hostile restore membership, stale-job recovery, poison messages, overlapping unmapped imports, AI config key binding, security headers.
- The epic-11 retrospective re-runs `dotnet list package --vulnerable` and `npm audit`, and spot-checks the audit's findings index.

---

## Appendix A — `epic-11-codebase-audit-hardening.md` (proposed content)

# Epic 11: Codebase Audit Hardening

Fixes the findings of the 2026-10-10 codebase audit (`docs/codebase-audit-2026-10-10.md`, commit `6212bdc`). The audit rated architecture and testing *Good* and security, correctness and maintainability *Fair*. Its "fix first" list was: restore can rewrite household membership (S1), the job pipeline has liveness gaps (C7, C8), unmapped Smart Plug imports overwrite each other (C9), Data Protection keys are unencrypted in Azure (S2), and there are no security headers or rate limiting (S3). Delivers no new FR.

**Origin:** codebase audit, 2026-10-10. Architecture decisions by Winston with Ralf, reviewed by four independent reviewers the same day. FR-6 clarified by John with Ralf. See `planning/sprint-change-proposal-2026-10-10.md`.

**FRs covered:** none new. FR-6/FR-30 clarified (C12). Hardens FR-4, FR-22, FR-23, FR-26–FR-28 and FR-33.
**Architecture:** AD-6, AD-17, AD-20 (amended 2026-10-10); AD-24, AD-25, AD-26, AD-27 (new); AD-3, AD-4, AD-5, AD-7 and AD-18 constrain the implementation.

**Tiers:** Now (11.1–11.6) → Soon (11.7–11.11) → Later (11.12–11.19).
**Dependencies:**
- 11.3 needs 11.2.
- 11.6 needs 11.5.
- 11.9 needs 11.8 (it uses the shared API client and the CSRF filter).
- Everything else is independent.

**Live gate:** stories touching auth flows (11.6, 11.7, 11.8, 11.9) cannot move review → done without live Auth0/Chrome verification.

### Story 11.1: Restore and Export Never Carry Access Grants

As a household member,
I want restoring a backup and exporting data to never change or reveal who can access my household,
So that a backup file can neither let a stranger in nor lock us out, and our login identities don't leak.

**Acceptance Criteria:**

**Given** a restore file whose `householdMembers` omits the caller, or lists identities from another household or IdP,
**When** the restore completes,
**Then** the Household's membership is exactly what it was before (dual-provider test). `HouseholdMember` is no longer in the restore's delete/insert ordering (AD-25, audit S1).

**Given** an export,
**When** it is generated,
**Then** each `householdMembers[]` entry is exactly `{ "id", "displayName" }`, with no `externalIssuer` or `externalSubjectId` (audit S12). `formatVersion` stays `"v2"`.

**Given** a pre-change export file that still contains identity fields,
**When** it is validated and restored,
**Then** it succeeds. The validator requires `id`, treats `displayName` as optional, and accepts and ignores the identity fields (fixture test).

**Given** a completed restore,
**When** the member sees the result,
**Then** a "people in this backup" panel lists the file's member display names next to the existing invite action, without comparing them to current members. The copy exists in de-DE and en-US.

**Given** the docs,
**When** this story is done,
**Then** `data-import-restore.md` ("Session continuity", now unconditional) and `data-export-format.md` (member shape, and that the restore target must run the same or a newer release) match the behaviour.

### Story 11.2: Job Lifecycle — Conditional Transitions, Heartbeat, Stale Recovery

As a household member,
I want a background job that was interrupted by a restart to end as failed with a clear message,
So that I can retry a restore or cleanup instead of being blocked forever.

**Acceptance Criteria:**

**Given** any `BackgroundJob` status change,
**When** it happens,
**Then** it goes through one Application service as a single conditional update (`WHERE Id AND Status IN expected`, plus the `StartedAtUtc` token when leaving `Processing`). A terminal status is never overwritten, and zero rows affected makes the caller stop (AD-6, 2026-10-10).

**Given** the expand-only migration adding `StartedAtUtc` and `HeartbeatAtUtc`, created for both providers via `add-migration.sh`,
**When** a job runs,
**Then** the processor refreshes `HeartbeatAtUtc` every minute from its own DbContext scope.

**Given** a `Processing` job whose heartbeat is older than 5 minutes,
**When** `GET /api/jobs/{id}`, the job list, restore-confirm or the cleanup check runs,
**Then** that Household's stale jobs are first marked failed with code `job-interrupted`. A job that is still heartbeating is never failed, and restore-confirm's 409 guard holds for its whole run. `Queued` rows are never failed for age.

**Given** the in-process queue and an app restart,
**When** the app starts,
**Then** each Household's `Queued`/`Processing` rows created before the process started are marked failed, iterating Households and setting `JobHouseholdContext` (never `IgnoreQueryFilters`). A restore-confirm that was blocked before the restart now succeeds (integration test).

**Given** a dequeued message whose job row is terminal or missing,
**When** it is processed,
**Then** nothing runs and the message is deleted. The "row missing, insert a `Processing` row" fallback is removed.

**Given** an `OperationCanceledException` that is not caused by shutdown,
**When** it escapes a job,
**Then** the job ends `Failed`. Failure codes (`job-interrupted`, `job-retries-exhausted`, `upload-missing`) are translated in both locales.

### Story 11.3: Azure Queue Consumer Resilience

As the operator of the Azure deployment,
I want transient queue errors and poison messages to never take down the app or loop forever,
So that one bad message or storage hiccup doesn't stop every request (audit C7).

**Acceptance Criteria:**

**Given** `CreateIfNotExistsAsync` or `ReceiveMessagesAsync` throws,
**When** the consumer loop runs,
**Then** the exception is caught and logged, the loop backs off and retries, and the host keeps running (test with a faulting queue client).

**Given** the consumer,
**When** it polls,
**Then** it receives one message at a time.

**Given** a message whose `DequeueCount` exceeds 5,
**When** it is received,
**Then** its job is conditionally failed with code `job-retries-exhausted` (never overwriting a terminal status) and the message is deleted.

**Given** a job whose temp payload file no longer exists (replica replaced, AD-24),
**When** it is processed,
**Then** it ends terminally `Failed` with code `upload-missing` and the message is deleted, with no retry.

**Given** `ProcessSmartPlugImport` fails and `PersistFailedImportAsync` also throws,
**When** the error propagates,
**Then** the original exception is logged first and is not masked.

*Depends on 11.2.*

### Story 11.4: Unmapped Smart Plug Readings Keyed by Device

As a household with several smart plugs,
I want two unmapped plug files covering the same period to keep their own readings,
So that no consumption data is silently overwritten before I map the plugs (audit C9).

**Acceptance Criteria:**

**Given** `SmartPlugReading.DeviceName` (today `nvarchar(max)`/`text`),
**When** this story's first migration runs,
**Then** the column becomes `HasMaxLength(200)`. The migration first checks the stored maximum and fails loudly instead of truncating. Both parsers reject a device tag longer than 200 characters with a stable import error, and the restore validator enforces the same bound.

**Given** deploy N,
**When** it ships,
**Then** the partial unique index `(HouseholdId, DeviceName, IntervalStart) WHERE PowerPointId IS NULL` exists, and the unmapped-path upsert (`ON CONFLICT`/`MERGE ON`) and its `DeduplicateByMatchKey` use that key. The old index is still present.

**Given** deploy N+1 (a second PR in this story, shipped promptly after N),
**When** it ships,
**Then** the old `(HouseholdId, IntervalStart) WHERE PowerPointId IS NULL` index is dropped. The story is done only after N+1.

**Given** `UpdateMappingAsync`,
**When** a plug is mapped,
**Then** its collision key is still `(PowerPointId, IntervalStart)`, unchanged.

**Given** two different plugs' files covering the same period, both unmapped (dual-provider Testcontainers),
**When** both are imported and later mapped,
**Then** each keeps and is attributed its own readings, and re-uploading the same plug's file still updates in place.

### Story 11.5: CI Identity Split and Supply-Chain Hygiene

As Ralf (operator),
I want PR pipelines to run with read-only Azure rights and dependencies to be pinned and watched,
So that a branch push can't act as the deploy identity, and vulnerable dependencies surface automatically (audit S11).

**Acceptance Criteria:**

**Given** `pr-review.yml`'s `validate-infra` job,
**When** it authenticates to Azure,
**Then** it uses its own Reader-scoped federated identity (credential subject `pull_request`). The Owner-scoped deploy identity is reachable only from `main`. `infra/README.md` documents both, after Ralf first verifies the current role mapping.

**Given** every workflow,
**When** it references an action,
**Then** the action is pinned to a commit SHA with a version comment.

**Given** the repo,
**When** this story is done,
**Then** `dependabot.yml` covers nuget, npm, github-actions and docker; a CodeQL workflow covers C# and JS/TS; and CI scans the container image.

**Given** current dependencies,
**When** this story is done,
**Then** `npm audit fix` is applied, ASP.NET Core and EF Core are bumped together to the latest 10.0.x patch, and OpenTelemetry is bumped to current. `dotnet list package --vulnerable` and `npm audit --omit=dev` are clean or have documented exceptions.

### Story 11.6: Data Protection Key Encryption (Key Vault, Fail Closed)

As a household member,
I want the keys that sign my session cookie to be encrypted at rest,
So that someone with database read access can't forge my session (audit S2).

**Acceptance Criteria:**

**Given** a new `infra/modules/key-vault.bicep` (RBAC permission model, one key),
**When** it is deployed,
**Then** the Container App's system-assigned identity holds **Key Vault Crypto Service Encryption User** on that key only, and `DataProtection__KeyVaultKeyId` is set (AD-17, 2026-10-10).

**Given** the composition root,
**When** `DataProtection:KeyVaultKeyId` is set,
**Then** `ProtectKeysWithAzureKeyVault` is used alongside `PersistKeysToDbContext`. `Azure.Extensions.AspNetCore.DataProtection.Keys` and `Azure.Identity` are pinned explicitly in `Directory.Packages.props`.

**Given** a non-Development environment with neither Key Vault nor a certificate configured,
**When** the app starts,
**Then** it refuses to start unless `DataProtection:AllowUnprotectedKeys=true` is set, and the selected protector or opt-out is logged.

**Given** existing self-host installs,
**When** this ships,
**Then** `docs/self-hosting.md` has a Data Protection section (certificate, or a deliberate opt-out), `.env.example`/`docker-compose.yml` carry the setting commented out, and the release note flags the breaking change.

**Given** the cutover,
**When** the pre-cutover revision is deactivated,
**Then** Ralf runs the `infra/README.md` runbook step that revokes old keys (`RevokeAllKeys`), and a live Auth0/Chrome check confirms a fresh sign-in works and the new key in `DataProtectionKeys` is encrypted.

*Depends on 11.5. Live Auth0/Chrome gate applies.*

### Story 11.7: Security Headers, CSP, Theme Script, Rate Limiting

As a household member,
I want the app protected against framing, script injection, downgrade and request flooding,
So that a single bug or a hostile site can't escalate into account takeover or lockout (audit S3).

**Acceptance Criteria:**

**Given** a non-Development environment,
**When** any response is sent (including errors and static files),
**Then** a middleware registered first sets the AD-27 CSP (`script-src 'self'`, `style-src 'self' 'unsafe-inline'`, `form-action 'self'`, `frame-ancestors 'none'`, …), `nosniff` and `Referrer-Policy: same-origin`, and `UseHsts()` is active. In Development none of these headers are present (integration tests).

**Given** `web/index.html`,
**When** it is built,
**Then** it contains no inline script. The theme initialisation lives in `web/public/theme-init.js`, loaded early in `<head>`, with no flash of the wrong theme (checked live in dark and light mode).

**Given** the deployed app,
**When** every surface is visited in Chrome, including login and logout,
**Then** the console shows no CSP violations (live check; `img-src data:` is kept only if the bundle needs it).

**Given** the built-in rate limiter (`UseRateLimiter` after routing and authentication),
**When** a limit is exceeded,
**Then** the response is 429 ProblemDetails. Partitions are per OIDC identity for upload, export, invite create/preview/accept and member removal, and per client IP for `/login`.

**Given** forwarded headers,
**When** this story is done,
**Then** their configuration is unchanged (Story 1.7). A live probe on Azure with a forged `X-Forwarded-For` confirms the rate-limit IP comes from ingress, and the result is recorded. `docs/self-hosting.md` requires a reverse proxy in front of the app.

*Live Auth0/Chrome gate applies.*

### Story 11.8: Auth Hardening — CSRF Header, POST Logout, Session Cap, Shared API Client

As a household member,
I want state-changing requests to come only from the app itself, and sessions to expire eventually,
So that a hostile site can't act as me, and a stolen cookie doesn't live forever (audit S9, S13).

**Acceptance Criteria:**

**Given** any `/api` POST, PUT, PATCH or DELETE (including multipart uploads) without an `X-Requested-With` header,
**When** it arrives,
**Then** it is rejected with 400 ProblemDetails by one endpoint filter on the `/api` group (AD-27).

**Given** the frontend,
**When** this story is done,
**Then** one shared `web/src/lib/api-client.ts` (credentials, the CSRF header, `ApiError`/`toApiError`) replaces the ~10 per-feature copies (audit R2, web side).

**Given** logout,
**When** the member signs out,
**Then** it is a POST. FR-33 behaviour is preserved, including draining the offline queue first and the end-session warning.

**Given** the cookie handler,
**When** a session is issued,
**Then** the 14-day sliding expiration is explicit, and a 90-day absolute cap based on an issued-at value in the ticket rejects older sessions (AD-17).

*Live Auth0/Chrome gate applies.*

### Story 11.9: Member Removal and Revocable Invites

As a household member,
I want to remove a member, leave the household, and see and revoke open invites,
So that we can cut off someone who should no longer have access (audit A2, S10).

**Acceptance Criteria:**

**Given** Settings,
**When** a member opens the members section,
**Then** they see current members and open invites, and can remove a member, leave, or revoke an invite (de-DE and en-US).

**Given** a removal or leave,
**When** it runs,
**Then** one transaction deletes the membership, expires every open invite of the Household and increments `Household.Version`. Removing or leaving as the last member is rejected, and of two concurrent removals one gets 409 (concurrency test). A structured log event records who removed whom (AD-26).

**Given** any lookup by member id,
**When** it runs,
**Then** it goes through one repository method scoped to the current `HouseholdId`, which returns 404 for another Household's member. An architecture test guards this.

**Given** a removed member with a still-valid cookie,
**When** they make their next request,
**Then** they land in the household-creation flow (live check with two Auth0 accounts).

**Given** a new invite,
**When** it is created,
**Then** its token is 32 random bytes encoded as base64url, and only a SHA-256 hash is stored. Lookup tries the hash first and the legacy plaintext column second. A follow-up entry in `deferred-work.md` records that the plaintext column is dropped at least 7 days after release.

**Given** `docs/self-hosting.md`,
**When** this story is done,
**Then** it states that any member can remove others, and that recovering a fully removed household is an operator-level task.

*Needs 11.8. Live Auth0/Chrome gate applies.*

### Story 11.10: Upload Hardening

As the operator,
I want uploads to use bounded memory and disk,
So that one large or malicious file can't take down the container (audit S4).

**Acceptance Criteria:**

**Given** `POST /api/household-import`,
**When** a file is uploaded,
**Then** it is streamed to disk and validated with `Utf8JsonReader` over a `FileStream`, never read into a single string. Peak memory for a 32 MB export is measured before and after, and recorded.

**Given** a Household with a pending validated upload,
**When** it uploads again,
**Then** the previous pending upload and its file are removed, so there is at most one pending upload per Household.

**Given** `Consume` in the upload registry,
**When** an entry is expired or belongs to another Household,
**Then** its file is deleted on every path.

**Given** an XLSX Smart Plug file,
**When** it is parsed,
**Then** `OpenSettings.MaxCharactersInPart` and a row-count cap apply, and a zip-bomb fixture is rejected with a stable import error.

### Story 11.11: Container and Config Correctness

As a self-hoster or operator,
I want the image and configuration to behave the way the docs say,
So that following `.env.example` works and the image matches what CI verified (audit S5, S7, C11, C1).

**Acceptance Criteria:**

**Given** the Dockerfile,
**When** the image is built,
**Then** the runtime stage runs as `USER $APP_UID`, and the build stage copies `Directory.Build.props` and `global.json`, so the NuGet-audit gate and the SDK pin apply to the shipped image.

**Given** a missing `ConnectionStrings:Default`,
**When** the app starts outside Development,
**Then** it fails with a clear error. The `change-me` fallback is used only in Development.

**Given** OIDC is not configured,
**When** `/login` is requested,
**Then** the response is 503 with a clear "not configured" message, not a 500.

**Given** the AI configuration,
**When** this story is done,
**Then** the code reads the `Ai:*` section (`Ai:Endpoint`, `Ai:ApiKey`, `Ai:Model`, `Ai:BackendLabel`) used by `appsettings.json`, `.env.example`, `docker-compose.yml` and Bicep. The stale Bicep "placeholder" comment is fixed, the selected adapter is logged at startup, and a test binds the documented keys. Epic 9's text references are updated (proposal §4.4).

### Story 11.12: Async Per-Request Household Resolution

As a household member,
I want API requests not to block a thread on a database lookup,
So that the app stays responsive under bursts on Azure SQL Basic (audit C2).

**Acceptance Criteria:**

**Given** an authenticated API request,
**When** it is processed,
**Then** membership is resolved once, asynchronously, in middleware, and cached in `HttpContext.Items`. `CurrentHouseholdAccessor` only reads the cached value. The job path (`JobHouseholdContext`) is unchanged.

**Given** AD-17's rule that membership is resolved per request,
**When** this story is done,
**Then** nothing caches membership across requests or in the cookie. A removed member is still cut off on their next request (test).

### Story 11.13: Document and Observe the Single-Instance Invariant

As a self-hoster or future maintainer,
I want the single-instance assumption written down and observed,
So that no one raises the replica count without knowing what breaks (audit A1, AD-24).

**Acceptance Criteria:**

**Given** `docs/self-hosting.md` and the Bicep `maxReplicas` description,
**When** this story is done,
**Then** they state the invariant, list the process-local components, and point to AD-24.

**Given** two instances whose DB heartbeat rows overlap for more than 15 minutes,
**When** this is detected,
**Then** a warning is logged. Shorter overlaps during deploys produce nothing, and startup is never blocked.

**Given** each process-local mechanism,
**When** this story is done,
**Then** it carries a one-line comment pointing to AD-24.

### Story 11.14: Restore Id Collisions and an Active-Job Guard

As a household member,
I want restoring another household's export, on the same instance, to either work or fail with a clear message,
So that duplicating or migrating a household isn't a generic failure (audit C10).

**Acceptance Criteria:**

**Given** a restore file whose entity ids or meter-reading idempotency keys already exist in another Household,
**When** it is validated,
**Then** the outcome is clear: either the entities are re-keyed with foreign keys remapped inside the file, or the file is rejected up front with a specific message. *Ask First: re-key or reject.*

**Given** two concurrent restore-confirms with different tokens,
**When** both run,
**Then** at most one restore job is active per Household, enforced by a database guard (e.g. a filtered unique index on active restore and cleanup jobs), not by check-then-enqueue alone.

### Story 11.15: Trending Threshold Scaled to Elapsed Time

As a household member in my first year,
I want *Trending* to warn me as early in the year as it would after a full year,
So that a surprise invoice doesn't build up unnoticed (audit C12, FR-6 clarified 2026-10-10).

**Acceptance Criteria:**

**Given** `PatternDetectiveCalculator.ResolveStatus`,
**When** Status is computed,
**Then** the difference to date is compared against `TrendingThresholdKwh` scaled to the same elapsed period via `BonusDecayNormalizer.NormalizeToDate` (AD-5), which matches `WindowedDeviationCalculator`. *Below baseline* stays any shortfall, and a tie still resolves to *within range*.

**Given** existing `StatusSnapshot` rows,
**When** this ships,
**Then** they are not rewritten (forward-only; AD-7 and the PRD recomputation policy).

**Given** FR-30's detail view and the threshold setting,
**When** they are shown,
**Then** the detail view shows both the configured (per-year) and the scaled threshold, and the setting is labelled "per year" in both locales. The release note says that households in their first year will see *Trending* earlier.

### Story 11.16: Offline Queue — Readings the Server Rejected Need Attention

As a household member who logs readings offline,
I want to be told when a queued reading was rejected,
So that I don't silently lose a reading I entered (audit C4).

**Acceptance Criteria:**

**Given** `flushQueue` receives a permanent 4xx for a queued reading,
**When** it handles the response,
**Then** the reading stays in IndexedDB in a "needs attention" state instead of being deleted.

**Given** readings needing attention,
**When** the member opens the app,
**Then** they see them with the server's reason, and can correct and resend them or discard them explicitly. *Ask First (Sally, if needed): where this surfaces.*

**Given** AD-16,
**When** a corrected reading is resent,
**Then** it keeps its idempotency key, unless the correction changes its identity.

### Story 11.17: TimeProvider and Recompute-Lock Eviction

As a maintainer,
I want time-dependent logic testable without the real clock, and process-local locks that don't grow forever,
So that expiry and window logic is covered by deterministic tests (audit C6, C5).

**Acceptance Criteria:**

**Given** the ~47 direct `DateTimeOffset.UtcNow` calls,
**When** this story is done,
**Then** Application and Infrastructure code use an injected `TimeProvider`. Domain calculations receive "now" as a parameter. An architecture test bans direct `UtcNow` outside the composition root.

**Given** `HouseholdRecomputeLock`,
**When** a Household's lock is idle,
**Then** its entry is evicted safely (no eviction while held), with a comment pointing to AD-24.

### Story 11.18: Backend Analyzer and Format Gate

As a maintainer,
I want style and security analyzers enforced in CI,
So that whole classes of issues are caught at PR time instead of in audits (audit R3).

**Acceptance Criteria:**

**Given** the repo,
**When** this story is done,
**Then** a root `.editorconfig` exists, `<AnalysisMode>Recommended</AnalysisMode>` is enabled (the CA2xxx, CA3xxx and CA5xxx security rules at least as warnings), and `pr-review.yml` runs `dotnet format --verify-no-changes`.

**Given** current warnings,
**When** this story is done,
**Then** the 6 `xUnit1051` warnings and the real `react-hooks/exhaustive-deps` warning in `use-smart-plug-import-job.ts` are fixed. New analyzer findings are fixed or suppressed with a justification.

### Story 11.19: Structural Cleanup

As a maintainer,
I want the oversized units split and repeated guards extracted,
So that changes stay local and reviews stay readable (audit A3, R1, R2 api side).

**Acceptance Criteria:**

**Given** `Program.cs`,
**When** this story is done,
**Then** registrations live in `AddObservability`, `AddAuth`, `AddApplicationServices` and `AddJobQueue` extension methods, with behaviour unchanged.

**Given** `SmartPlugImportRepository` (~1,190 lines), `tagging-scaffold-manager.tsx` and `TaggingScaffoldEndpoints.cs`,
**When** this story is done,
**Then** each is split by responsibility or entity, with behaviour unchanged and existing tests green.

**Given** the 11 copies of `TryGetHouseholdId`,
**When** this story is done,
**Then** one endpoint filter replaces them.

**Given** comments that narrate story and review history,
**When** files are touched by this story,
**Then** each comment is trimmed to one to three lines of *why*, and any longer reasoning moves to `docs/adr/`.
