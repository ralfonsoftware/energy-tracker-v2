# Rubric Walker Review: 2026-10-10 spine amendments (audit remediation)

- **Date:** 2026-10-10
- **Reviewer role:** Rubric Walker (architecture-spine reviewer gate)
- **Scope:** `ARCHITECTURE-SPINE/invariants-rules.md`: the `[AMENDED 2026-10-10 …]` bullets in AD-6 (L42–47), AD-17 (L115–119) and AD-20 (L146), and the new AD-24 (L197–205), AD-25 (L207–215), AD-26 (L217–221) and AD-27 (L223–232). `ARCHITECTURE-SPINE/deferred.md` L15–17.
- **Context read:** `docs/codebase-audit-2026-10-10.md`, AD-1 to AD-23, and the brownfield code listed in each finding.
- **Verdict:** **Revise before stories are cut.** The direction is right and every audit "Fix first" item has an owning AD. However, 7 HIGH findings remain:
  - one amendment contradicts AD-3;
  - two mechanisms cannot be implemented as written on the production provider or deploy pipeline;
  - the job-liveness rule brings in new races and false failures;
  - one hardening rule would bring back the Story 1.7 login bug.

None of these is CRITICAL. Each has a contained text fix.

## Rubric summary

| Check | Result |
|---|---|
| Each AD fixes the real divergence point and misses none | Mostly. AD-6 misses the terminal-write race and the scale-to-zero interaction. AD-26 misses outstanding invites. |
| Every Rule is enforceable and prevents its divergence | Partly. AD-20's index cannot be built on SQL Server as written. AD-27's rate-limit partition key is undefined for invitees. |
| Nothing in Deferred lets two units diverge | No. The C12 entry parks a live divergence between two calculators. |
| Changes ratify the brownfield code | Mostly. AD-24's temp-file rationale and AD-17's self-host default conflict with what the code and compose file actually do. |
| No new text weakens or contradicts AD-1..AD-23 | No. AD-6's startup sweep contradicts AD-3. AD-2 and AD-23 still name the old partial-index key. |
| Operational dimensions are decided, deferred or an open question | Partly. AD-20's expand/contract order, AD-17's self-host upgrade and AD-27's Azure vs self-host proxy trust are not decided. |

---

## HIGH

### H1. The AD-6 in-process startup sweep contradicts AD-3

**Evidence:**
- `invariants-rules.md:44`: "at startup, every `Queued`/`Processing` row is marked `Failed`".
- AD-3 (`invariants-rules.md:22`): `ICurrentHouseholdAccessor` has two resolution paths only, "no code path is exempt", and `.IgnoreQueryFilters()` is never used on a Household-scoped entity.
- `BackgroundJob` is Household-scoped with a global filter (`src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs:83`).
- At startup there is no `HttpContext`, and `JobHouseholdContext.HouseholdId` is null (`CurrentHouseholdAccessor.cs`, the `HttpContext is null` branch). A cross-household `UPDATE` therefore matches zero rows unless the implementer reaches for `IgnoreQueryFilters()` or raw SQL. That is exactly the bypass AD-3 exists to prevent.

Two implementers will diverge: one uses `IgnoreQueryFilters()`, which breaks AD-3; the other writes a filtered update that silently does nothing.

**Suggested fix:** append to the AD-6 bullet:
> "The startup sweep enumerates `Household` ids (an unfiltered entity per AD-3's documented exemptions), then, per Household, sets `JobHouseholdContext.HouseholdId` in a fresh scope and runs the sweep through the normal filtered `DbContext`. It never uses `IgnoreQueryFilters()`, `FromSqlRaw` or an unscoped `ExecuteUpdate`. This is AD-3's job-processing resolution path, reused, not a third path."

### H2. Lazy staleness creates a write race, and redelivered jobs start out stale

**Evidence:**
- `invariants-rules.md:43`: `StartedAtUtc` is set only "on `Queued → Processing`". A stale `Processing` job is marked `Failed` by any reader.
- `BackgroundJobProcessor.cs:63–72` and `:109–120`: a redelivered message whose row is still `Processing` reuses the row and runs again. Nothing would reset `StartedAtUtc`. With the 60-minute visibility timeout (`AzureStorageQueueJobQueue.cs:75`), every Azure redelivery therefore starts more than 30 minutes past `StartedAtUtc`, so it is stale the moment it begins.
- `BackgroundJobProcessor.cs:155,167,184–185`: the terminal write is a tracked-entity `SaveChangesAsync`. `BackgroundJob` has no concurrency token (`BackgroundJobConfiguration.cs`).

Effects:
- A job that is still running gets marked `Failed` by a reader, then its own completion overwrites that with `Completed` (or `Failed` with a different message). The UI flips between "interrupted, please retry" and "done".
- For restore, the stale-mark clears the restore-confirm 409 guard (`HouseholdImportEndpoints.cs:133–144`), so a second restore can be enqueued while the first is still running.
- Restore is the job most likely to exceed 30 minutes. It accepts files up to 250 MB (`HouseholdImportEndpoints.cs:19`) on Azure SQL Basic, while the NFR1 15-minute figure was derived from a 120k-row Smart Plug import, not a restore.

**Suggested fix:** add to the AD-6 amendment:
> "(a) Every claim of a job (`Queued → Processing`, and reuse of a `Processing` row on redelivery) sets `StartedAtUtc = now` as part of the same conditional update.
> (b) The processor's terminal write is conditional: `ExecuteUpdate … WHERE Id = @id AND Status = Processing AND StartedAtUtc = @claimedAt`. If 0 rows are affected, the job was failed as stale or claimed by another delivery. The processor logs and discards its result and never overwrites a terminal status.
> (c) The staleness threshold is per `JobType` (restore gets its own, larger constant, derived from a measured worst-case restore), or the processor refreshes a `HeartbeatAtUtc` column at chunk boundaries and staleness keys off the heartbeat instead of `StartedAtUtc`. A write during active work is not an AD-7 timer."

Option (c) with a heartbeat is the more robust choice and removes the need to guess restore duration.

### H3. The `Queued` staleness threshold rests on wrong reasoning and misfires after scale-to-zero

**Evidence:**
- `invariants-rules.md:43` justifies 2 h as "beyond the Azure queue's 60-minute visibility timeout plus one redelivery". The visibility timeout applies only after a message has been received, and a received message's row is `Processing`, not `Queued`. Nothing about visibility bounds how long a row stays `Queued`.
- On Azure, `Queued` lasts for the backlog depth (one message per poll under the new rule, each up to 15 min) plus however long the app sits at zero replicas. `container-app.bicep:58–59,216–219` sets `minReplicas: 0` with no scale rules, so it falls back to the HTTP scaler: queued messages wait until the next HTTP request wakes the app.
- The first request after waking is typically the job list, which fails every `Queued` row older than 2 h. When the consumer later dequeues those messages it skips them as terminal (`BackgroundJobProcessor.cs:101–106`).
- Jobs whose payload lives only in the DB lose work they would otherwise complete: `CorrelateEvent` (bulk-enqueued by Story 10.2's re-evaluation) and `CleanUpSmartPlugImportJobs`.
- The actually orphaned Azure case, a message that expired at the queue's default 7-day TTL or was never sent, is not what the 2 h value is aimed at.

**Suggested fix:** replace the `Queued` clause:
> "`Queued` staleness applies only where the message cannot outlive the process. That is the in-process adapter, already covered by the startup sweep. On the Azure adapter, a `Queued` row is stale only once it is older than the queue message TTL, which is set explicitly on `SendMessageAsync` (e.g. `timeToLive: 24h`) so the two stay in lockstep. Optionally, `container-app.bicep` gains an `azure-queue` KEDA scale rule (`queueLength: 1`, still capped at `maxReplicas: 1`) so a backlog wakes the app instead of waiting for HTTP traffic."

If the KEDA rule is adopted, state it in AD-6 and drop the "Worker/API process split" Deferred entry's implication that queue-depth scaling needs a separate app.

### H4. AD-24's "temp files are acceptable under single instance" ignores that scale-to-zero replaces the instance

**Evidence:**
- `invariants-rules.md:205` and deferred `:15` treat temp-file payloads (audit C3) as a multi-instance problem only.
- With `minReplicas: 0` (`container-app.bicep:59`), every scale-in discards the replica's ephemeral disk. Upload temp files (`HouseholdImportEndpoints.cs:87–88`, Smart Plug upload) are gone. Any Azure message that survives (the queue is durable) then fails with "upload again" (`RestoreHouseholdData.cs:26–30`).
- The HTTP scaler scales in after the cooldown once the polling tab closes, even while a job is still running (ACA does not wait for background work). The same scale-in kills in-flight jobs, and their `Processing` rows then go through H2 and H3.

On Azure this is the normal path after a user uploads several files and closes the app, not an edge case of a second replica. The spine currently accepts it implicitly, under a rationale that does not hold.

**Suggested fix:** amend AD-24's last bullet:
> "'Single instance' means one replica at a time, **not** one long-lived process. On Azure, scale-to-zero replaces the replica and its disk. Temp-file payloads therefore survive only while the replica stays up. Accepted consequence: an upload whose job has not started before scale-in fails terminally with 'upload again' (AD-6). [Or, if not accepted: blob-backed payloads move out of Deferred and become a prerequisite of AD-24 on Azure.]"

Also record in AD-6 or AD-19 that in-flight jobs can be killed by scale-in, and which mechanism, if any, keeps the replica alive while a job runs.

### H5. The AD-20 index cannot be built on SQL Server: `DeviceName` is `nvarchar(max)`

**Evidence:**
- `invariants-rules.md:146` says to "confirm `DeviceName`'s max length fits SQL Server's 1,700-byte nonclustered key limit".
- `SmartPlugReadingConfiguration.cs:28–29` sets `IsRequired()` with no `HasMaxLength`. The SQL Server snapshot maps it to `nvarchar(max)` (`Migrations.SqlServer/…ModelSnapshot.cs:576–578`). An `nvarchar(max)` column cannot be an index key column at all (SQL Server error 1919), whatever the actual data length.
- Azure runs SQL Server (AD-21).

So the "confirm" step will fail, and the actual decision is not written down. The column has to be narrowed. That is an `ALTER COLUMN` on the largest table (about 600k rows per the Story 3.8 spike) on 5-DTU Basic, under the `scripts/migrate.sh` pre-health-gate apply, and the parsers then need a length guard.

**Suggested fix:** replace the "confirm" clause:
> "`SmartPlugReading.DeviceName` gets `HasMaxLength(200)` (≤ 400 bytes in `nvarchar`, comfortably under 1,700 together with `HouseholdId` (16 bytes) and `IntervalStart` (10 bytes)). Both parsers reject tags longer than that as a `SmartPlugImportValidationException`. The narrowing migration ships in its own deploy **before** the index migration. It is preceded by a data check (`MAX(LEN(DeviceName))`) and timed against a copy of production on Azure SQL Basic, the same discipline as the AD-23 spike. Postgres keeps `text` or gets `varchar(200)` for parity (AD-2: same model, both providers)."

### H6. The AD-20 expand/contract order breaks the running revision on Postgres and on rollback

**Evidence:**
- `invariants-rules.md:146`: "add the new index first, drop the old one in a later deploy … the drop must follow before the upsert's `ON CONFLICT`/`MERGE` target switches".
- That orders the old-index drop before the code switch. But `app-deploy.yml:109ff` applies migrations before the new revision is healthy, and rollback restores the image, not the schema (audit S11).
- The old code's Postgres `ON CONFLICT ("HouseholdId","IntervalStart") WHERE "PowerPointId" IS NULL` (`SmartPlugImportRepository.cs:341–348`) needs a matching arbiter index. Once the old index is dropped, every unmapped import on the old revision, and on any rollback, fails with "no unique or exclusion constraint matching the ON CONFLICT specification".

**Suggested fix:** restate the sequence so it is backward compatible at each step:
> "Deploy N: add `(HouseholdId, DeviceName, IntervalStart) WHERE PowerPointId IS NULL`, **and** switch the upsert conflict target and `UpdateMappingAsync` to the new key in the same release. While both indexes exist, a two-plug collision fails loudly through the old, stricter index as a job failure, never as a silent overwrite. Rolling back to N-1 still works because the old arbiter exists. Deploy N+1: drop the old index. Rolling back to N still works because N targets the new key."

Also name the new index (e.g. `IX_SmartPlugReadings_HouseholdId_DeviceName_IntervalStart_WhenPowerPointIdNull`); see L1.

### H7. Pinning `KnownIPNetworks` in AD-27 brings back the Story 1.7 login bug and is undecided for Azure vs self-host

**Evidence:**
- `invariants-rules.md:231`: restrict `ForwardedHeadersOptions.KnownIPNetworks` to the ingress range before any per-IP partition ships.
- `Program.cs:457–479`: the lists are cleared deliberately because the ACA ingress peer never matches the defaults. Without `X-Forwarded-Proto`, the OIDC `redirect_uri` becomes `http://`, which was Story 1.7's production bug.
- Two problems follow:
  - The same restriction would also break every self-hoster behind their own reverse proxy, whose network is unknown at build time.
  - The rule's own fallback covers only the `/login` partition, not the restriction. An implementer who "pins" a guessed ACA range breaks login in production.
- `AllowedHosts` (`appsettings.json`: `"*"`) set to real hostnames needs per-deployment values: the ACA default FQDN, the custom domain, and self-host hosts. None of that is specified.

**Suggested fix:** replace the bullet:
> "`X-Forwarded-Proto` trust stays as today, because scheme is required for OIDC. Client-IP trust is a separate, config-driven decision: `ForwardedHeaders:KnownNetworks` (CSV of CIDRs) is read once at the composition root. When it is set, the networks are applied and per-IP partitions are allowed. When it is unset (the default on ACA Consumption and on self-host), `/login` uses a global partition and no code reads `RemoteIpAddress` as a security key. `AllowedHosts` comes from a Bicep parameter (default FQDN plus custom domain) and stays `*` in `docker-compose.yml` unless the self-hoster sets `ALLOWED_HOSTS`. Verify that the deploy health gate's Host header is allowed."

---

## MEDIUM

### M1. AD-26 member removal does not cut off a removed member who holds an invite link

**Evidence:**
- `HouseholdInvite` (`src/EnergyTracker.Domain/HouseholdInvite.cs`) is a 7-day bearer token with no `CreatedBy` and no revocation path (audit S10).
- `AcceptHouseholdInvite.cs` admits any principal that has no membership.
- A removed member who created, or simply saw, an unconsumed invite link can accept it after removal and rejoin. AD-26's stated "Prevents" (a household able to cut off a former member) is therefore not achieved.

**Suggested fix:** add to AD-26:
> "Removing a member also marks every unconsumed `HouseholdInvite` of that Household as consumed or revoked in the same transaction. Invite list and revoke are added alongside removal."

The second sentence can alternatively be a Deferred entry that cross-references S10.

### M2. The AD-26 last-member guard is check-then-act, and its lock-out stance conflicts with AD-25's "Prevents"

**Evidence:**
- `invariants-rules.md:221`: "removing the last member is rejected". Two members removing each other concurrently both pass a count check and leave zero members. That is an unreachable household, the very S1 outcome.
- AD-25's "Prevents" (`:210`) names "lock out all members" as a harm. Yet under AD-26 any member can remove all others. That is acceptable under "fully trusted", but the text should say so, so a later reviewer does not "fix" one AD against the other.

**Suggested fix:**
> "Member removal increments `Household.Version` (AD-4 token) in the same `SaveChanges` as the delete, so concurrent removals conflict (409). Removing other members, including all of them, is within full trust (AD-26). AD-25 prevents an *uploaded file* from doing it, not a member."

### M3. The AD-17 fail-closed rule breaks every existing self-host install on upgrade, and the cutover is under-specified

**Evidence:**
- `docker-compose.yml:7–14` passes `DataProtection__CertificateBase64` with a default of empty and sets no `ASPNETCORE_ENVIRONMENT`, so the container runs as `Production`.
- `docs/self-hosting.md` never mentions Data Protection.
- After `:118` ships, every existing self-host install without a certificate fails to start on upgrade. The opt-out flag exists but nobody is told about it.

Cutover gaps:
- Expired DP keys remain valid for **unprotecting**; only revocation stops them. So "readable until they expire" understates the exposure. Revocation is mandatory, and who performs it (a runbook step or code) is not stated.
- During the deploy overlap (acknowledged in AD-24), the old revision cannot unwrap a new Key-Vault-wrapped key.
- The Key Vault RBAC assignment can lag the first deploy, the same race `container-app.bicep:109–116` documents for AcrPull.

**Suggested fix:** add to AD-17:
> "(a) The upgrade is a documented breaking change: `docs/self-hosting.md` and `.env.example` gain a Data Protection section, the release notes name `DataProtection__AllowUnprotectedKeys`, and `docker-compose.yml` passes it through.
> (b) The cutover runs `IKeyManager.RevokeAllKeys` once, from a one-shot runbook step in `docs/`, after the new revision is serving. The plaintext rows may then be deleted.
> (c) The Key Vault role assignment is deployed in a deploy before the one that sets `DataProtection__KeyVaultKeyId`, recorded in `docs/local-vs-azure-deltas.md`."

### M4. AD-27's CSP `style-src` is undecided while hashes and nonces are banned

**Evidence:**
- `invariants-rules.md:228–229`: the policy has no `style-src`, so it falls back to `default-src 'self'`. Styles are "widened only as the built SPA actually requires", and "No CSP hashes or nonces".
- `web/package.json` depends on `radix-ui`. Radix Dialog's scroll lock (`react-remove-scroll` / `react-style-singleton`) injects `<style>` elements at runtime, and `style-src 'self'` blocks them.
- The only ways to allow them are `'unsafe-inline'` for styles or a nonce, and nonces are banned. One implementer adds `'unsafe-inline'`, another tries a nonce, a third ships a silently broken scroll lock.

**Suggested fix:**
> "`style-src 'self' 'unsafe-inline'` is permitted. Inline-style injection is a low-risk vector compared with script, and Radix requires it. `script-src` never gets `'unsafe-inline'`. A Playwright smoke test opens a Dialog under the production CSP."

### M5. AD-27's rate-limit partition is undefined for invitees, and there are no limits or 429 contract

**Evidence:**
- `invariants-rules.md:230`: the partition is "per authenticated member" for invite preview and accept. The callers of `GET /api/household-invites/{token}` and `POST …/accept` (`HouseholdInviteEndpoints.cs:36,57`) are authenticated but are **not** members yet (`HouseholdId` is null). The partition key is undefined exactly where S10 asked for throttling.
- No limits are given, so a multi-file Smart Plug upload (FR-32) can trip a per-member upload limit.
- The SPA has no 429 contract. `meter-reading-sync.ts:108–111` deletes on any permanent 4xx, which matters if a limiter ever extends to `/api/*`.

**Suggested fix:**
> "Partition key = the authenticated principal (validated issuer + subject), not the `HouseholdMember` id. The upload limit is at least the SPA's maximum multi-file batch per minute. Rejections return 429 with `Retry-After`, every SPA client treats 429 as retryable, and the AD-16 offline queue never drops on 429."

### M6. AD-24's "second live instance" warning would fire on every deploy

**Evidence:**
- `invariants-rules.md:203` logs a warning at startup when another instance is detected, and in the same bullet acknowledges that the old and new revisions overlap on every deploy.
- A heartbeat check at startup therefore warns on every deploy, which trains the operator to ignore it.
- The mechanism is left at "e.g.", so implementations will diverge: a new table versus reusing something else.

**Suggested fix:**
> "Detection compares `CONTAINER_APP_REVISION` / `CONTAINER_APP_REPLICA_NAME` (and a random instance id on self-host) in a `RuntimeInstance` heartbeat row. It warns only when two live replicas of the **same** revision exist, or when two revisions overlap for longer than 10 minutes. The heartbeat row is not Household-scoped: it is listed as an AD-3 exemption like `DataProtectionKeys`."

### M7. AD-6's Azure retry semantics depend on whether someone happens to read

**Evidence:**
- `invariants-rules.md:43,46`: a crashed `Processing` job is redelivered after 60 minutes (visibility timeout), with up to 5 dequeues. But any read after 30 minutes marks it `Failed`, and the redelivery then skips it (`BackgroundJobProcessor.cs:114–118`).
- The same crash is therefore retried automatically if nobody looks, and failed if the user polls. The `DequeueCount` = 5 limit is effectively reachable only for failures that happen before the claim.
- A poison envelope that cannot be deserialized has no `JobId` to mark `Failed`.

**Suggested fix:** pick one model and state it:
> "On Azure, retry-on-redelivery is the recovery mechanism; lazy staleness applies only to rows whose `StartedAtUtc` (refreshed per H2) is older than `visibilityTimeout × maxDequeue`. On in-process, the startup sweep is the recovery mechanism. A message that cannot be deserialized is logged and deleted."

---

## LOW

### L1. Missing forward pointers for the new partial-index key

- AD-2's `[AMENDED 2026-09-04]` bullet (`invariants-rules.md:15`) and AD-23's index bullet (`:182`, which names `IX_SmartPlugReadings_HouseholdId_IntervalStart_WhenPowerPointIdNull`) still describe `(HouseholdId, IntervalStart)`.
- **Fix:** add "(key widened to include `DeviceName` by AD-20's 2026-10-10 amendment)" to both, and pin the new index name in AD-20.

### L2. AD-25's export-shape details are open

**Evidence (`:213–214`):**
- It does not say whether member `id` and `createdAtUtc` stay. They are listed in `docs/data-export-format.md:136–145`.
- Keeping `formatVersion: "v2"` while removing fields that the format doc lists as present is a contract change for third-party consumers. Restoring a new export into a not-yet-upgraded instance fails validation (`ValidateHouseholdImport.cs:148–149` requires both identity fields).
- The pre-confirm summary still counts `HouseholdMembers` as data to be replaced (`HouseholdImportEndpoints.cs:178–179`).
- Matching "backup members not here" uses display names, which are nullable and not unique.

**Fix:**
> "Export keeps `displayName` (and `createdAtUtc`) only. `docs/data-export-format.md` marks `id`, `externalIssuer` and `externalSubjectId` as 'present in exports before 2026-10, ignored on restore'. The confirm summary drops the member count. The post-restore list shows the backup's display names without claiming a match."

### L3. AD-26 adds product scope that is not in the PRD

- Member removal is not in FR-27, whose consequences cover invite and equal access only.
- "Binds: FR-26–FR-28" includes FR-28, which is Rooms, Power Points and Devices.
- **Fix:** bind FR-26–FR-27 plus "new FR (member removal), backfill to PRD with John". FR-32 and FR-33 were handled the same way.

### L4. The Deferred C12 entry parks a live divergence between two units

- `deferred.md:17`: the two calculators already disagree on the meaning of `TrendingThresholdKwh`.
  - `WindowedDeviationCalculator.cs:110` scales it through `BonusDecayNormalizer`.
  - `PatternDetectiveCalculator.cs:121–124` uses it unscaled.
- This is the exact divergence class AD-5 exists for, and Deferred should not hold it.
- **Fix:** move it to an Open Question with an interim rule: "no new consumer of `TrendingThresholdKwh` until resolved; the resolution lands as an AD-5 amendment that names the single owning function."

### L5. The multi-instance Deferred entry has gaps

- `deferred.md:15` omits two items:
  - the per-household one-active-restore DB guard (audit C10: check-then-enqueue, no constraint);
  - AD-24's heartbeat (M6), which would be superseded by real leases.
- **Fix:** add both to the list.

---

## What is solid (keep as is)

- **AD-6:** "Cancellation that is not shutdown is a failure", and wrapping the whole Azure receive loop matches `AzureStorageQueueJobQueue.cs:79–84`, where `CreateIfNotExistsAsync` and `ReceiveMessagesAsync` sit outside the `try`.
- **AD-25 core rule** ("restore never writes `HouseholdMember` rows"):
  - It is safe against FK fallout, because `BackgroundJob.QueuedByHouseholdMemberId` is the only FK to `HouseholdMember` and is `SetNull` (`BackgroundJobConfiguration.cs:41–44`).
  - It aligns with FR-23's "move to new hosting" through the existing invite flow.
- **AD-26** matches FR-27's "no separate admin/owner role" and `CurrentHouseholdAccessor`'s per-request membership resolution.
- **AD-27's inline-script move** is correct: `web/index.html:9–27` is the only inline script, and a classic `<script src>` in `<head>` preserves the pre-paint behavior.
- **AD-17's Azure Key Vault and managed-identity choice** fits AD-1 and AD-19, and reuses the existing system-assigned identity (`container-app.bicep:82–84`).
