# Adversarial Divergence Review: 2026-10-10 hardening amendments

**Scope:** the `[AMENDED 2026-10-10 …]` bullets in AD-6, AD-17 and AD-20; the new AD-24, AD-25, AD-26 and AD-27 in `ARCHITECTURE-SPINE/invariants-rules.md`; and the three new end entries of `ARCHITECTURE-SPINE/deferred.md`. Context: `docs/codebase-audit-2026-10-10.md`.

**Method:** I built pairs of independently written stories, one story each, where both follow the amended text to the letter but still land incompatible. I also looked for real holes the text does not cover. Every finding below is checked against the live code, not only the prose.

**Verdict: REVISE BEFORE STORY SPLIT.** None of the amendments is wrong in direction, but they are not ready to hand to separate implementers yet. Five things need fixing first:

1. The AD-20 migration cannot be built as written. `DeviceName` is `nvarchar(max)`, and the expand/contract order is backwards.
2. The AD-6 startup sweep cannot be implemented without breaking AD-3. A literal implementation silently does nothing.
3. The AD-6 lazy sweep races the processor's terminal write. Nothing in the text says which of the two owns the terminal transition.
4. AD-26 member removal can be undone by any invite link the removed member still holds.
5. AD-27's forwarded-headers restriction, built literally, brings back the Story 1.7 production bug (`Request.Scheme == "http"` breaks OIDC).

None of these reaches CRITICAL (silent cross-household data corruption on the happy path). Several are HIGH because they break a production path, the Auth0 login gate, or the audit fix they were written to deliver.

---

## Findings ranked by severity

### 1. [HIGH] AD-20: the new index cannot be created on SQL Server, because `DeviceName` is `nvarchar(max)`

**Evidence.**
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/EnergyTrackerDbContextModelSnapshot.cs:576-578`: `DeviceName` is `.IsRequired().HasColumnType("nvarchar(max)")`.
- `src/EnergyTracker.Infrastructure/Configurations/SmartPlugReadingConfiguration.cs:28-29`: `.IsRequired()` with no `HasMaxLength`.

SQL Server refuses a `(max)` column as an index key column (error 1919, "is of a type that is invalid for use as a key column in an index"). The spine only says "confirm `DeviceName`'s max length fits SQL Server's 1,700-byte nonclustered key limit". Read literally, that is a check, not a change. A story that only "confirms" ships a migration that fails on Azure SQL and passes on Postgres (`text` has no such restriction). Dual-provider Testcontainers tests catch this only if the SQL Server leg runs.

**Divergence pair.**
- Story A (migration) adds `HasMaxLength(N)`. That is an `ALTER COLUMN` on a ~600k-row table on Azure SQL Basic, and it fails outright if any existing value is longer than N.
- Story B (parser, restore validator) never learns about N. An Eve Home header longer than N, or a restore file with a longer `deviceName`, then fails at insert time as an unlocalised generic job failure (`ErrorMessage = null`).

**Fix to spine text.** Replace "confirm `DeviceName`'s max length fits…" with:

> `SmartPlugReading.DeviceName` gains `HasMaxLength(200)` (≤ 400 bytes as `nvarchar`, well under the 1,700-byte key limit together with `HouseholdId`+`IntervalStart`). The migration first asserts `MAX(LEN(DeviceName)) <= 200` and aborts with a clear message otherwise; no silent truncation. Both parsers reject (as `SmartPlugImportValidationException`) a device tag longer than 200 characters, and `ValidateHouseholdImport` enforces the same limit on `smartPlugReadings[].deviceName`. The constant lives in one place (Domain).

---

### 2. [HIGH] AD-20: the expand/contract order is stated backwards, so a deploy window breaks every unmapped import on Postgres

**Evidence.**
- `SmartPlugImportRepository.cs:343`: `ON CONFLICT ("HouseholdId", "IntervalStart") WHERE "PowerPointId" IS NULL`. Postgres infers the arbiter index from this target and errors with 42P10 ("no unique or exclusion constraint matching the ON CONFLICT specification") if no matching index exists.
- `.github/workflows/app-deploy.yml:109ff`: migrations run in CI before the new revision goes live, so the old code runs against the new schema for a while.

The spine says "drop the old one in a later deploy … so the drop must follow before the upsert's `ON CONFLICT`/`MERGE` target switches". Read literally, the order is: add new index, then drop old index, then switch code. In the window between the drop and the code switch, the old code's `ON CONFLICT` target has no arbiter index, so every `AwaitingPowerPointMapping` import fails on Postgres. On SQL Server, the old `MERGE … ON HouseholdId, IntervalStart` keeps overwriting the other plug's rows, which is the C9 bug, until the code switch lands. The text also calls the old index "narrower". Its key is narrower, but it is the stricter constraint. That wording invites exactly this mistake.

**The safe order:**
1. **Deploy N:** a migration adds `(HouseholdId, DeviceName, IntervalStart) WHERE PowerPointId IS NULL`, and in the same release the upsert target and `MERGE ON` switch to the new key. While the old index is still present, a second plug at the same interval raises a unique violation on both providers. That is a loud job failure, not silent overwrite, which is strictly better than today.
2. **Deploy N+1:** a migration drops `IX_SmartPlugReadings_HouseholdId_IntervalStart_WhenPowerPointIdNull`.

**Fix to spine text.** Replace the parenthetical with the two-step order above. Add: "both indexes are raw SQL only (the EF model is unaware of them; `SmartPlugReadingConfiguration.cs` comment block), so `scripts/add-migration.sh` produces an empty diff and the `Up`/`Down` SQL is hand-written per provider."

---

### 3. [HIGH] AD-6: the in-process startup sweep is either a silent no-op or an AD-3 violation

**Evidence.**
- AD-3 says: "`DbSet<T>.Find()`, `FromSqlRaw`, and `.IgnoreQueryFilters()` are never used against a Household-scoped entity", and "`ICurrentHouseholdAccessor` has two resolution paths only".
- `BackgroundJob` is Household-scoped (`BackgroundJobConfiguration.cs:47`, global filter).
- `CurrentHouseholdAccessor.cs:62-65`: with no `HttpContext`, `HouseholdId` comes from `JobHouseholdContext`, which is null at startup.

The AD-6 rule is "at startup, every `Queued`/`Processing` row is marked `Failed`". That rule spans households and runs with neither resolution path set.

**Divergence pair.**
- Story A follows AD-3 and writes `dbContext.BackgroundJobs.Where(j => j.Status is Queued or Processing).ExecuteUpdateAsync(…)`. The global filter becomes `HouseholdId == null`, which matches nothing. The sweep is a silent no-op, its unit test with a seeded household context passes, and audit finding C8 is not fixed on self-host until the 30-minute lazy rule happens to run.
- Story B makes the sweep work with `.IgnoreQueryFilters()` and breaks AD-3's absolute ban.

**Fix to spine text.** Add a named, narrow AD-3 exception, in the same style as AD-2's raw-SQL exception: "The AD-6 in-process startup sweep is the one sanctioned cross-household write. It lives in one Infrastructure class, uses `IgnoreQueryFilters()` only on `BackgroundJobs`, writes only `Status`/`ErrorMessage`/`CompletedAtUtc`, and never reads row contents back." Alternatively, drop the startup sweep and lean on the lazy rule plus the in-process staleness rule in Finding 4. Also see Finding 7 on when the sweep runs.

---

### 4. [HIGH] AD-6: the lazy sweep and `BackgroundJobProcessor` both own the terminal transition, with no ownership token, and restore can legitimately run past 30 minutes

**Evidence.**
- `BackgroundJobProcessor.cs:155-185`: the terminal write is the tracked entity `job.Status = Completed|Failed; job.CompletedAtUtc = …; SaveChangesAsync`. That is an unconditional `UPDATE … WHERE Id = @id`. `BackgroundJobConfiguration` has no concurrency token.
- `HouseholdRestoreWriter.cs:14,20,41-99,178-191`: restore deletes 12 tables in 200-row chunks (select ids, then delete) and inserts in 200-row `SaveChangesAsync` chunks, all inside one transaction, with a 120 s timeout per command. NFR1's 15-minute budget was derived from the AD-23 bulk path (`deferred.md`, NFR1 entry), not from this chunked EF path. A household with ~600k Smart Plug rows means about 3,000 delete round trips plus about 3,000 insert round trips on 5 DTU, which can plausibly take more than 30 minutes.

**Race.** A long restore passes 30 minutes. Any member opening the Smart Plug list or polling `GET /api/jobs/{id}` triggers the sweep, which marks the restore `Failed` ("interrupted, please retry"). The restore then finishes and its unconditional save flips the row to `Completed`. `ErrorMessage` still holds the sweep's text because EF only updates modified columns, so the result is `Completed` with an error message. Two outcomes follow:
- (a) The UI told the user to retry, and restore-confirm's check (`HouseholdImportEndpoints.cs:133-134`) now lets a second restore in. It queues behind the first on the single consumer and wipes the first one's result.
- (b) A cleanup job's one-active check passes while an import is still writing.

The reverse order also exists: the processor finishes first, then a sweep evaluated on stale data marks it `Failed`, unless the sweep's own write is conditional.

**Divergence pair.** The "liveness" story writes the sweep as `ExecuteUpdate … WHERE Status = Processing AND StartedAtUtc < cutoff`. The "processor hardening" story keeps the tracked-entity save. Each is correct alone. Together they produce `Failed → Completed` flips.

**Fix to spine text.** Add a "terminal-transition ownership" rule:
- Every transition is a conditional set-based update: `Queued → Processing` (exists), sweep `→ Failed WHERE Status IN (Queued, Processing) AND <stale predicate>`, and the processor's terminal write `WHERE Status = Processing AND StartedAtUtc = @myStartedAt`. `StartedAtUtc` doubles as the run's ownership token.
- If the processor's terminal write affects 0 rows, the run lost ownership. It logs, deletes the queue message, and does not resurrect the row.
- Staleness is measured from a `HeartbeatAtUtc` that the processor bumps every ~2 minutes while a job runs (one tiny `ExecuteUpdate`), not from `StartedAtUtc`. Stale means "no heartbeat for 10 minutes". This removes the per-JobType guessing about how long restore, cleanup and import may take, and it is still lazily evaluated (AD-7-safe). If heartbeats are rejected, at minimum make the `Processing` threshold per `JobType`, with restore and cleanup at ≥ 2 h.

---

### 5. [HIGH] AD-26: a removed member rejoins with any invite link still valid. Removal does not revoke invites.

**Evidence.**
- `AcceptHouseholdInvite.cs:23-46` checks only `ConsumedAtUtc`, `ExpiresAtUtc` and whether the principal is already a member of some household. A removed principal is no longer a member, so the check passes.
- `CreateHouseholdInvite.cs:14`: invites last 7 days.
- `HouseholdInviteConfiguration` has no creator column, so invites cannot be revoked per member.

Any member, and so the soon-to-be-removed member, can create an invite at any time and keep the link. After removal they accept their own link and are back with full rights (AD-26: "full, equal rights"). AD-26 justifies the trust model with "AD-25 removes the only path by which a member could grant access … without the invite flow". But the invite flow itself is the bypass.

**Divergence pair.** The "member removal" story deletes the `HouseholdMember` row (correct per AD-26). The "invite rate-limiting" story (AD-27) never touches invite state. Both are done, and removal still does not hold.

**Fix to spine text.** Add to AD-26: "Removing a member (or a member leaving) expires every unconsumed `HouseholdInvite` of that Household in the same transaction (household-wide, because invites carry no creator). The remaining members re-issue invites if needed. Test: remove member X, then X accepts an invite link created before the removal and gets 410/expired."

---

### 6. [HIGH] AD-27: restricting `KnownIPNetworks` also drops `X-Forwarded-Proto`, which brings back the Story 1.7 OIDC bug on Azure

**Evidence.** `Program.cs:470-479`:

```csharp
ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
// KnownIPNetworks/KnownProxies default to loopback-only, which Container Apps' internal ingress
// peer never matches — without clearing these, the middleware silently ignores the headers
// above and Request.Scheme stays "http" (Story 1.7's production bug).
forwardedHeadersOptions.KnownIPNetworks.Clear();
```

AD-27 says to restrict `KnownIPNetworks` to the ingress range. If that range is wrong or incomplete (the spine admits it may not be pinnable on Consumption), the middleware ignores both headers, not just XFF. `Request.Scheme` becomes `http`, the OIDC `redirect_uri` becomes `http://…`, Auth0 rejects it, and login is down. The spine's fallback ("`/login` falls back to a global, non-IP partition") only covers the rate-limit key, not the scheme. Per project memory, this is exactly the class of bug the live Auth0/Chrome gate exists for, and it does not show up locally (no proxy; `docs/local-vs-azure-deltas.md`).

**Fix to spine text.** "Trust `X-Forwarded-Proto` unconditionally from the ingress, as today. Trusting `X-Forwarded-For` for the rate-limit key is a separate decision: if the ingress range cannot be pinned, keep the current `ForwardedHeadersOptions` unchanged and key `/login` on a global partition. Never narrow `KnownIPNetworks` without a test asserting `Request.Scheme == "https"` behind a simulated ingress hop (extend `tests/EnergyTracker.Api.Tests/ForwardedHeadersTests.cs`), and treat the change as gated on the live Auth0 login check."

Also, a global `/login` partition means one client can exhaust login for everyone. State a generous permit count and say this is accepted DoS exposure.

---

### 7. [MEDIUM] AD-6: the startup sweep races the first enqueues and kills fresh jobs

`InProcessChannelJobQueue.EnqueueAsync` (`InProcessChannelJobQueue.cs:18-27`) inserts the `Queued` row, then writes to the channel. If the startup sweep runs in a `BackgroundService.ExecuteAsync`, the obvious place, it runs concurrently with Kestrel accepting requests. A job enqueued in the first seconds gets swept to `Failed`. Its channel message is then dequeued, and the processor logs "already recorded as Failed; skipping" (`BackgroundJobProcessor.cs:114-118`). The upload is lost, and its temp file is never deleted because the use case's `finally` never runs.

**Fix.** "The startup sweep only touches rows with `CreatedAtUtc < processStartUtc` (captured before the host starts), and runs in `IHostedService.StartAsync` (awaited before the server starts listening), not in `ExecuteAsync`."

---

### 8. [MEDIUM] AD-6 and AD-24: Azure redelivery semantics contradict the stale thresholds, and the processor comments promise a retry that no longer happens

**Evidence.**
- The visibility timeout is 60 minutes (`AzureStorageQueueJobQueue.cs:75`). The `Processing` stale threshold is 30 minutes. The processor keeps a "reuse an already-`Processing` row so a crashed job is retried" branch (`BackgroundJobProcessor.cs:60-72, 96-107, 111-119`) and a shutdown-OCE path that leaves the row `Processing` "so a redelivered message is still treated as retryable" (`:157-163`).
- With the new rule, any read between minute 30 and 60 marks the row `Failed`, so the redelivery skips. If no read happens in that window, the redelivery reuses the row with its old `StartedAtUtc`. The very next read then marks a genuinely running job `Failed`, which is Finding 4 again.

Two stories, both compliant: the "Azure consumer" story keeps reuse-on-redelivery (the spine never removes it), and the "liveness" story assumes redelivery never resumes work. Also, "`Queued` older than 2 hours (beyond the visibility timeout plus one redelivery)" is wrong reasoning. A `Queued` row has never been dequeued, so the visibility timeout does not apply to it. Its age is backlog: the single consumer runs sequentially, so a 20-file multi-upload at ~7.65 min worst case per file passes 2 h and gets swept while it is still legitimately waiting.

**Fix.** Pick one model explicitly. Recommended: "a redelivered message whose row is `Processing` takes over only when the row is stale by the same predicate as the sweep, conditionally resetting `StartedAtUtc` and `HeartbeatAtUtc`. Otherwise it is left undeleted. On graceful shutdown, the Azure consumer sets the in-flight message's visibility to 0 so it is retried on next start, not after 60 minutes." Restate the `Queued` threshold as backlog-based, for example "`Queued` and no job of this Household has been `Processing` or completed in the last 2 h", or drop `Queued` staleness on Azure and rely on `DequeueCount`.

---

### 9. [MEDIUM] AD-6: the `DequeueCount ≥ 5 → Failed` write is unconditional and can overwrite `Completed`

The spine says "After 5 dequeues, the job is marked `Failed` and the message deleted." A job can complete and then have `DeleteMessageAsync` fail, for example on a transient storage error or a pop receipt that expired after more than 60 minutes. The message reappears, and after 5 attempts a `Completed` row is overwritten to `Failed`.

**Fix.** "The poison rule transitions `WHERE Status IN (Queued, Processing)` only. A terminal row's message is just deleted." The existing skip path (`BackgroundJobProcessor.cs:114-118`) already does the right thing if the check runs after the status lookup.

---

### 10. [MEDIUM] AD-6: "stable 'interrupted, please retry' message" and "Failed ('upload again')" contradict the round-4 i18n incident fix

**Evidence.** `BackgroundJobProcessor.cs:173-181`: generic failures deliberately store `ErrorMessage = null`, because the backend does not know the client's locale and the SPA localises via `errorMessage ?? t(...)`. A backend story following AD-6 literally writes an English sentence into `ErrorMessage`, and German users see raw English. That is the incident round 4 fixed (AD-18).

Separately, `ProcessSmartPlugImport.cs:33-35` opens the temp file with `File.OpenRead`. A missing file throws `FileNotFoundException`, which is a generic failure, so `ErrorMessage` is null and "upload again" never reaches the user. Only `RestoreHouseholdData.cs:26-30` throws the user-facing typed exception. Two stories (C3 under AD-24, C8 liveness) can each assume the other added the typed path.

**Fix.** Add `BackgroundJob.FailureReason` (nullable enum or string code: `Interrupted`, `PayloadMissing`, `PoisonMessage`) in the same expand-only migration as `StartedAtUtc`. Expose it on `GET /api/jobs/{id}` and the job list. The SPA maps codes to `t()` keys, and `ErrorMessage` stays null for these. Both use cases check `File.Exists` up front and fail with `PayloadMissing`.

---

### 11. [HIGH] AD-25: the member export shape is ambiguous, so the export story and the validator story break round-trip

**Evidence.**
- `ValidateHouseholdImport.cs:145-152` requires `id`, `externalIssuer`, `externalSubjectId` and `createdAtUtc`, and runs a duplicate-`id` check across every category (`:297-304`).
- `ExportHouseholdData.cs:62-63` emits `(Id, ExternalIssuer, ExternalSubjectId, DisplayName, CreatedAtUtc)`.
- AD-25 says "Export carries **display names only** for members" and also "`externalIssuer`/`externalSubjectId` are dropped".

**Divergence pair.**
- Story A (export) reads "display names only" and emits `{ "displayName": "…" }`.
- Story B (validator) relaxes only the two identity fields, as the text says, and still requires `id` and `createdAtUtc`.

The result: every new export fails restore validation ("'id' must be a GUID").

**Second hole: forward compatibility.** `formatVersion` stays `"v2"`, so a file exported from an upgraded Azure instance and restored into a self-host on an older image is rejected, because the old validator requires `externalIssuer`. This is exactly AD-25's own "move-to-new-hosting" scenario. The spine claims backward compatibility only.

**Fix to spine text.**
- Pin the shape: "`householdMembers[]` entries are `{ displayName: string | null }` only. `id`, `createdAtUtc`, `externalIssuer` and `externalSubjectId` are no longer emitted. The validator treats all four as optional-and-ignored (still type-checked if present), and the per-category duplicate-`id` check skips `householdMembers`."
- Either bump to `formatVersion: "v3"` with v2 still accepted, or state in `docs/data-export-format.md` the minimum app version that can restore a post-change export.

---

### 12. [MEDIUM] AD-25: the "which backup members are not members here" UI has no data source and no comparison rule

**Evidence.**
- The restore job keeps nothing about the file: `RestoreHouseholdData.cs:38-44` deletes the temp file in `finally`, and `BackgroundJob` has no result payload.
- The validate response's `HouseholdImportSummary` (`HouseholdImportEndpoints.cs:178-210`) carries only a member count.
- With identities removed, display names are the only thing left to compare. They are nullable, not unique, and user-editable, so "not members here" cannot be computed reliably.

**Divergence pair.** The frontend story expects the names on the job-status response after completion. The backend story puts them on the validate response, or nowhere.

**Fix.** "The validate response's summary gains `backupMemberDisplayNames: (string | null)[]`. The SPA holds it across the confirm and poll cycle. After completion it lists those names as 'people in the backup' with the invite action. It does not claim which of them are already members." Also rename or retire the `HouseholdMembers` count in the confirm dialog, because it now suggests the members will be replaced.

---

### 13. [MEDIUM] AD-26: concurrent removals can leave a Household with zero members

"Removing the last member is rejected" is check-then-act. Two members removing each other at the same time, or both leaving at once, each see a count of 2, and both deletes succeed. The result is an orphaned Household nobody can reach, and invite acceptance needs a member to create the invite. The spine names no concurrency mechanism, though AD-4 (version column) exists for exactly this.

**Fix.** "Removal is a conditional delete that bumps `Household.Version` (AD-4) in the same transaction, or `DELETE … WHERE (SELECT COUNT(*) FROM HouseholdMembers WHERE HouseholdId = @h) > 1` under serializable isolation. Test: two concurrent cross-removals leave exactly one member." Also add to Deferred: a sole member can neither leave nor delete the Household, so there is no GDPR self-erasure path.

---

### 14. [MEDIUM] AD-27: "per authenticated member" partitions do not exist for invite preview and accept

The principal previewing or accepting an invite is, by definition, not a member yet. `CurrentHouseholdAccessor` returns `HouseholdMemberId == null` (`CurrentHouseholdAccessor.cs:84-89`). A partition keyed on member id becomes either one shared `null` bucket, which lets one attacker block every invite acceptance, or no limit at all.

**Fix.** "Invite preview and accept partition on the authenticated OIDC principal (`ValidatedIssuer` + `NameIdentifier`), and upload and export on `HouseholdMemberId`."

---

### 15. [MEDIUM] AD-24 and AD-6: two independently built liveness mechanisms, and "one active instance" is false during every revision swap

AD-24 asks for "a short-lived DB heartbeat row keyed by instance id". AD-6, as I recommend in Finding 4, needs per-job heartbeats. Built as separate stories, these become two tables, two clocks and two definitions of "alive". During a Container Apps revision overlap, two `AzureStorageQueueJobProcessingService` loops poll the same queue. A message whose temp file sits on the old revision's disk can be picked up by the new revision and fail as payload-missing. The same applies to `MemoryHouseholdImportUploadRegistry` tokens: validate hits the old revision, confirm hits the new one, and the user gets a 404. AD-24 accepts the missing-file outcome but does not say that every deploy may fail in-flight uploads and restore confirmations.

Also, the "Prevents" line says "six process-local mechanisms", but Binds lists seven.

**Fix.** "One `AppInstance` heartbeat table (instance id, started, last seen) serves both AD-24's observation and AD-6's staleness: a job row records `ProcessingInstanceId`, and the job is stale when that instance's heartbeat is stale." Add to AD-24: "Deploy overlap: uploads and restore confirmations in flight across a revision swap may fail with 'upload again'. This is accepted, and `docs/self-hosting.md` and the deploy runbook say so." Fix the count.

---

### 16. [MEDIUM] AD-20: the instruction to switch `UpdateMappingAsync`'s collision classification to the new key is wrong and invites a regression

**Evidence.** `SmartPlugImportRepository.cs:501-516, 564-570`. Mapping moves rows from `PowerPointId NULL` to a concrete Power Point, so its collisions are against `(PowerPointId, IntervalStart)`, which this amendment does not change. No code path sets `PowerPointId` back to null (I grepped; only the parsers and `ProcessSmartPlugImport.cs:308` construct null-mapped rows). An implementer who obeys the spine literally rekeys `existingByIntervalStart` to `(DeviceName, IntervalStart)`. A different device's row at the same Power Point and interval is then classified as "no conflict", and the following `ExecuteUpdateAsync` throws a unique violation out of the fallback path, which surfaces as a 500 on Map.

**Fix.** Strike "and the `UpdateMappingAsync` collision classification". Replace it with: "`UpdateMappingAsync`'s collision key is unchanged (`PowerPointId, IntervalStart`, the mapped index). Only the unmapped-path upsert target and `MERGE ON` clause change. `DeduplicateByMatchKey` (`:242-270`) uses the same key as the conflict target it feeds: `(DeviceName, IntervalStart)` for the unmapped path."

---

### 17. [MEDIUM] AD-6: the processor's defensive "row missing → insert `Processing`" branch resurrects jobs that cleanup deleted

**Evidence.**
- `SmartPlugImportRepository.cs:916-933`: `DeleteJobsAsync` (manual cleanup) deliberately deletes `Queued`/`Processing` jobs.
- `BackgroundJobProcessor.cs:31-46`: if the row is missing, the processor inserts a fresh `Processing` row.
- Sequence on Azure: cleanup deletes a running import's row. The processor's final `SaveChangesAsync` updates 0 rows and throws `DbUpdateConcurrencyException`, outside the try, so the message is not deleted (`AzureStorageQueueJobQueue.cs:95-105`). Sixty minutes later the redelivery finds no row, inserts one, and reprocesses a deleted temp file. A ghost job reappears after the user cleaned up.

**Fix.** "Delete the pre-Story-3.6 defensive insert (every message now has an enqueue-time row). A message with no row is treated as cancelled: log and delete the message." Together with Finding 4's conditional terminal write, deleting a running job becomes a clean cancellation.

---

### 18. [LOW] AD-6: sweeping a `ProcessSmartPlugImport` job to `Failed` hides a persisted "Needs Mapping" import

If the process dies after `ProcessSmartPlugImport` commits its import and readings but before the processor's terminal save, the `SmartPlugImport` row exists. `ListSmartPlugImportJobs.DeriveState` (`:102-123`) maps `Failed` to `Error` regardless of the import row, so Map is hidden. The 30-day sweep later deletes the import, and its readings (`SetNull`) become unmappable orphans. They are recoverable only by re-uploading the same plug, which works only after the AD-20 fix.

**Fix.** "The sweep marks a `ProcessSmartPlugImport` job `Completed`, not `Failed`, when a `SmartPlugImport` row with that `BackgroundJobId` exists."

---

### 19. [LOW] AD-17: fail-closed startup collides with AD-27's non-Development tests and with existing self-host installs

- `WebApplicationFactory` defaults to `Development`. AD-27 requires tests that run outside Development, and those test hosts throw at startup once AD-17 lands unless they set `DataProtection:AllowUnprotectedKeys=true`. The two stories are coupled through ordering.
- `docker-compose.yml:13-14` defaults the certificate to empty and sets no `ASPNETCORE_ENVIRONMENT`, so it runs as Production. Every existing self-host refuses to start after upgrading. That is intended, but the spine should require release-note and `.env.example` changes in the same story.
- Precedence when both `KeyVaultKeyId` and `CertificateBase64` are set is unspecified.

**Fix.** Pin precedence (Key Vault wins and logs a warning). Name the test-host setting in AD-27's test bullet. Require the compose and `.env.example` update and an upgrade note in the AD-17 story.

---

### 20. [LOW] Remaining edge cases

- **Restore validator lacks key-uniqueness checks.** `ValidateHouseholdImport` checks only duplicate `id`. A file with two unmapped rows sharing `(deviceName, intervalStart)`, or two mapped rows sharing `(powerPointId, intervalStart)`, passes validation and fails inside the restore transaction as a generic `Failed`. Add both checks so the user gets a reportable failure. This matters more after C9, because new exports legitimately contain multi-plug unmapped data that an older deployment's index rejects.
- **Cross-vendor tag collision.** The new key has no vendor. An Eve Home device and a Meross device with the same tag share `(DeviceName, IntervalStart)` at midnight, and Meross daily rows overwrite Eve hourly rows. Rare. Either name it as accepted or include `Vendor` in the key.
- **Temp-file leak on skipped jobs.** A swept job's late dequeue skips without running the use case's `finally`, so its temp file is never deleted. That is bounded by container restarts under AD-24. Have the skip path delete the payload file.
- **Restored unmapped readings are permanently unmappable.** `RestoreHouseholdData.cs:174-186` sets `SmartPlugImportId = null`, and mapping is per import. This predates the amendment, but the AD-20 text should note that re-uploading the same plug's file is the recovery path, because the upsert adopts orphans by setting `SmartPlugImportId`.
- **The restore-confirm one-active check is check-then-act** (`HouseholdImportEndpoints.cs:133-150`). Under AD-24 the single consumer serialises execution, so the effect is a second queued restore, not concurrent ones. Say so in AD-24's Binds instead of leaving the endpoint comment's multi-replica claim (`:124-132`) standing. The comment contradicts AD-24 and should point to it.
- **`deferred.md`** "Multi-instance shared state" should also list the Azure consumer's sequential-processing assumption, which the restore and cleanup one-active checks rely on, and the instance heartbeat from Finding 15. "Owner role" should mention that invite revocation (Finding 5) is the minimum needed even without roles.

---

## Summary table

| # | Sev | Area | One-line fix |
|---|---|---|---|
| 1 | HIGH | AD-20 | Make `DeviceName` `HasMaxLength(200)` with a guarded migration; parsers and validator enforce it |
| 2 | HIGH | AD-20 | Order: add new index and switch target together, then drop the old index |
| 3 | HIGH | AD-6/AD-3 | Name the startup sweep as the one sanctioned `IgnoreQueryFilters` write, or drop it |
| 4 | HIGH | AD-6 | All transitions conditional; `StartedAtUtc` as ownership token; heartbeat-based staleness |
| 5 | HIGH | AD-26 | Removal expires all unconsumed invites of the Household |
| 6 | HIGH | AD-27 | Keep `X-Forwarded-Proto` trust; narrow only the XFF/rate-limit key; Auth0 gate |
| 7 | MED | AD-6 | Startup sweep in `StartAsync`, only rows created before process start |
| 8 | MED | AD-6/24 | One explicit redelivery model; `Queued` threshold is backlog, not visibility |
| 9 | MED | AD-6 | Poison rule only transitions non-terminal rows |
| 10 | MED | AD-6/18 | `FailureReason` code instead of English `ErrorMessage`; typed missing-file failure |
| 11 | HIGH | AD-25 | Pin member DTO `{displayName}`; validator ignores the rest; forward-compat version note |
| 12 | MED | AD-25 | Display names on the validate response; no "not a member here" claim |
| 13 | MED | AD-26 | Atomic last-member guard (AD-4) |
| 14 | MED | AD-27 | Invite routes partition on OIDC principal, not member id |
| 15 | MED | AD-24/6 | One instance-heartbeat table; document deploy-overlap loss |
| 16 | MED | AD-20 | Strike the `UpdateMappingAsync` rekey; align the dedupe key with the conflict target |
| 17 | MED | AD-6 | Remove the processor's missing-row insert fallback |
| 18 | LOW | AD-6 | Sweep marks import jobs with a persisted import `Completed` |
| 19 | LOW | AD-17 | Precedence, test-host flag, self-host upgrade note |
| 20 | LOW | various | Validator key checks, vendor collision, temp leak, deferred additions |
