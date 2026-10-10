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

## Story 11.1: Restore and Export Never Carry Access Grants

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

## Story 11.2: Job Lifecycle — Conditional Transitions, Heartbeat, Stale Recovery

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

**Given** terminal `CorrelateEvent` `BackgroundJob` rows (added per Meter Reading write by Story 10.2, never swept today),
**When** the existing 30-day job-history sweep runs,
**Then** it also removes them, with the same retention and the same `JobHouseholdContext` handling as for `ProcessSmartPlugImport` rows. *(Added by Epic 10 retro, 2026-10-10; Ralf's decision.)*

*Retro 10 note:* the stale-job recovery and the startup sweep also fail interrupted `CorrelateEvent` rows (`job-interrupted`, no retry). That is acceptable: the correlation re-evaluates the next time a Meter Reading in the Event's window changes (Story 10.2). Confirm at create-story that these rows do not surface in the member-visible job list; if they do, hide or label them.

## Story 11.3: Azure Queue Consumer Resilience

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

**Given** a `Queued` job older than 12 hours, for example because its queue message was lost (AD-6, amended 2026-10-11),
**When** `GET /api/jobs/{id}`, the job list, restore-confirm or the cleanup check runs,
**Then** that Household's such jobs are first marked failed with code `job-interrupted`, with the same conditional update as stale `Processing` jobs. `Queued` jobs under 12 hours are never failed for age. *(Added at Story 11.3 create-story, 2026-10-11; Ralf's decision. This revises Story 11.2's "`Queued` rows are never failed for age".)*

*Depends on 11.2.*

## Story 11.4: Unmapped Smart Plug Readings Keyed by Device

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

*Retro 10 note:* deploy N deliberately breaks the expand/contract rule from Story 10.1 (`project-context.md`, Migrations: narrowing `AlterColumn` ships in a later deploy). It is unavoidable: SQL Server cannot use `nvarchar(max)` as an index key, and N creates the new index. The safeguards are the stored-maximum pre-check (fails loudly, never truncates), the parser and validator bound shipped in the same deploy, and the 10.1 restore point plus rollback runbook. Record this as an explicit, justified exception in the story's Dev Notes and in the migration's comment. *(Added by Epic 10 retro, 2026-10-10.)*

## Story 11.5: CI Identity Split and Supply-Chain Hygiene

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

**Given** each workflow identity (the Reader-scoped PR identity and the Owner-scoped deploy identity),
**When** this story is done,
**Then** `infra/README.md` lists the exact permissions each one needs and why. Each workflow starts with a fail-fast preflight step that checks them by behaviour, with a harmless call: for example a read of the production database with the deploy identity, and a denied write with the Reader identity. A missing or excess right fails the run with a clear message before any migration or deployment step. *(Added by Epic 10 retro, 2026-10-10; Ralf's proposal.)*

*Retro 10 note:* the Story 10.1 rollback runbook (`infra/README.md`) assumes the operator's own Azure login for `az sql db restore/rename`. It does not use the CI identity, and the drill was accepted on that basis. After the split, keep the runbook and the permission list consistent.

## Story 11.6: Data Protection Key Encryption (Key Vault, Fail Closed)

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

## Story 11.7: Security Headers, CSP, Theme Script, Rate Limiting

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

## Story 11.8: Auth Hardening — CSRF Header, POST Logout, Session Cap, Shared API Client

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

## Story 11.9: Member Removal and Revocable Invites

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

## Story 11.10: Upload Hardening

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

## Story 11.11: Container and Config Correctness

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

## Story 11.12: Async Per-Request Household Resolution

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

## Story 11.13: Document and Observe the Single-Instance Invariant

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

## Story 11.14: Restore Id Collisions and an Active-Job Guard

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

## Story 11.15: Trending Threshold Scaled to Elapsed Time

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

## Story 11.16: Offline Queue — Readings the Server Rejected Need Attention

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

## Story 11.17: TimeProvider and Recompute-Lock Eviction

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

## Story 11.18: Backend Analyzer and Format Gate

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

## Story 11.19: Structural Cleanup

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
