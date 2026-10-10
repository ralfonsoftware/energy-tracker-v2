# Energy Tracker v2 — Codebase Audit

- **Date:** 2026-10-10
- **Commit audited:** `6212bdc` (main)
- **Scope:** `src/`, `web/`, `tests/`, `infra/`, `.github/`, `Dockerfile`, `docker-compose*.yml`, `scripts/`, `docs/`. Excluded as requested: `_bmad/`, `_bmad-artifacts/`, `spikes/`. Also not reviewed: `.claude/skills` (vendored tooling) and the generated EF migrations.
- **Method:** I read the code by hand: the composition root, auth and session, tenant isolation, every upload, import and restore path, the job pipeline and both queue adapters, the Smart Plug parsers and repository, the domain calculations, the AI client, the Bicep, CI/CD, the Dockerfile and the frontend's network and offline code. Tools I ran: `dotnet build`, `dotnet test`, `dotnet list package --vulnerable/--outdated`, `npm audit`, `tsc -b`, `oxlint` and `vitest`. The app was not started.
- **Evidence labels:** **Verified** means I read the code or config directly. **Inferred** means it follows from the code I read, but I did not run it.
- **Relation to the earlier draft:** this replaces an earlier draft of the same file. I re-checked every finding in that draft. Two of its claims were wrong and are corrected here: "DB-level one-active-job checks" (they are application-level only) and the npm advisory count. This version adds seven new findings: C7–C11, S12 and S13.

---

## 1. Executive summary

For a solo-built product the codebase is in good shape. The hexagonal layering is real and architecture tests enforce it. Tenant isolation is applied systematically, secrets hygiene is good, all raw SQL is parameterised, and the test suites are broad and pass. The main risks fall into four groups:

1. **Uploaded files are trusted too much.** Restore can rewrite household membership, and large uploads are buffered in memory and on disk.
2. **Security defaults fail open.** Data Protection keys are unencrypted in Azure, there are no security headers or rate limiting, and a missing connection string falls back to a default password.
3. **The background-job pipeline has gaps.** A transient queue error can stop the host, poison messages are retried forever, and jobs left in "Processing" are never recovered and block restore permanently.
4. **Correctness depends on a single replica, and nothing enforces that.**

| Area | Rating | Verdict |
|---|---|---|
| Security | **Fair** | No injection, IDOR or secret leaks found. One high-impact restore flaw, several fail-open defaults, no hardening headers. |
| Code correctness | **Fair** | One data-loss bug in unmapped imports, job-pipeline robustness gaps, one config-key mismatch. |
| Architecture & design | **Good** | Clean layers enforced by tests. Single-replica coupling is unenforced and there are a few oversized units. |
| Readability & maintainability | **Fair** | Consistent naming, but comments carry story and review history, there is deliberate copy-paste, and the backend has no analyzer gate. |
| Testing & delivery | **Good** | All suites pass. There are gaps around hostile input and job recovery. |

### Fix first

1. **S1:** Restore overwrites `HouseholdMembers` from the uploaded file. Any member can grant access to arbitrary OIDC identities or lock everyone out.
2. **C7:** In the Azure Storage Queue consumer, one transient receive error stops the whole host, and failing messages are retried forever.
3. **C8:** Jobs left in `Processing` (in-process queue plus a restart) are never recovered. Restore then returns 409 for that household forever.
4. **C9:** Two unmapped Smart Plug imports with overlapping intervals silently overwrite each other's readings.
5. **S2:** Data Protection keys are stored unencrypted in the Azure database, so anyone with DB read access can forge session cookies.
6. **S3:** There is no HSTS, CSP, frame protection or rate limiting.

---

## 2. Findings index

| ID | Sev. | Evidence | Title |
|---|---|---|---|
| **S1** | **High** | Verified | Restore replaces household membership with identities from an untrusted file |
| S2 | Medium | Verified | Data Protection key ring unencrypted in the Azure deployment (fail-open) |
| S3 | Medium | Verified | No security headers, no HSTS, no rate limiting |
| S4 | Medium | Verified | Memory and disk amplification on uploads; XLSX zip-bomb exposure |
| S5 | Medium | Verified | Container runs as root; Docker build skips the repo's NuGet-audit gate and SDK pin |
| S6 | Medium | Verified | DB firewall `AllowAzureServices` (0.0.0.0) admits every Azure tenant |
| S7 | Low | Verified | Hard-coded fallback DB credentials in production code path |
| S8 | Low | Verified | Forwarded headers trusted from any source; `AllowedHosts: *` |
| S9 | Low | Verified | CSRF defence relies solely on `SameSite=Lax`; logout is GET |
| S10 | Low | Verified | Invite tokens: plaintext at rest, no revocation, no throttling |
| S11 | Low | Verified | Supply chain: tag-pinned Actions, no Dependabot/CodeQL config, PR what-if gets Azure creds |
| S12 | Low | Verified | Export discloses every member's OIDC issuer and subject to any member |
| S13 | Low | Verified | Session lifetime is the 14-day sliding default, with no revocation path |
| **C7** | **Medium** | Verified | Azure queue consumer: unhandled receive error stops the host; no poison-message limit |
| **C8** | **Medium** | Verified | Orphaned `Processing` jobs are never recovered and block restore and cleanup permanently |
| **C9** | **Medium** | Verified (code), not reproduced | Concurrent unmapped imports overwrite each other's readings |
| C1 | Medium | Verified | AI config read from `AiPlausibility:*`, deployed under `Ai:*` |
| C2 | Medium | Verified | Synchronous DB query on the hot path of every API request |
| C3 | Medium | Verified | Job payloads reference container-local temp files |
| C10 | Low | Verified | Restore primary keys and idempotency keys are reused from the file, so cross-household restore fails |
| C11 | Low | Verified | `/login` returns 500 when OIDC is not configured |
| C4 | Low | Verified | Offline queue silently discards server-rejected readings |
| C5 | Low | Verified | `HouseholdRecomputeLock` never evicts and is process-local |
| C6 | Low | Verified | `DateTimeOffset.UtcNow` used directly; no `TimeProvider` |
| C12 | Low | Inferred | "Trending" threshold has different units in the two calculators |
| A1 | Medium | Verified | Single-replica assumptions are scattered and unenforced |
| A2 | Medium | Verified | No authorization model beyond "is a member" |
| A3 | Low | Verified | Oversized units (`Program.cs`, `SmartPlugImportRepository`, tagging scaffold) |
| A4 | Low | Verified | Dual DB providers permanently double migrations and raw SQL |
| R1 | Low | Verified | Comments embed story and review history |
| R2 | Low | Verified | Copy-paste by convention (household guard ×11, API client ×10) |
| R3 | Low | Verified | No `.editorconfig`, analyzers or format gate on the backend |

---

## 3. Security

### What is done well

- **Tenant isolation.** Global EF query filters cover 14 household-scoped entities. Each exemption (`Household`, `HouseholdMember`, `HouseholdInvite`, the DP keys) is documented with a reason. `IgnoreQueryFilters` is never used in `src/`, and an architecture test guards the export reader.
- **IDOR handling.** Lookups by id, jobs and import tokens return 404 or 403 without revealing whether the resource exists. Import tokens are bound to the household and can be used once.
- **Authentication.** The app uses the OIDC authorization-code flow and a server-side session cookie with `HttpOnly`, `Secure` and `SameSite=Lax`, plus `SaveTokens=false`. Tenant identity is the pair (*validated* issuer, subject), not a claim the user can influence. `/api` is protected by `RequireAuthorization()` on the route group, and unauthenticated API calls get 401 rather than a redirect.
- **Open redirect.** `IsSafeLocalReturnUrl` rejects `//`, `/\`, `://` and control characters, and has unit tests.
- **Invite accept race.** The invite has a concurrency token (`Version`) and the unique (issuer, subject) index closes double-accept and double-membership races correctly.
- **SQL.** All raw SQL (`SmartPlugImportRepository` upserts and advisory locks) uses `{n}` or interpolated parameters. No user-controlled identifiers reach SQL text.
- **Secrets.** `.env` and `certs/` are ignored by git and not tracked. The Docker build copies only `web/` and `src/`, so local secrets cannot end up in the image. Bicep uses `@secure()` and `secretRef`.
- **Dependencies.** NuGet audit is a hard restore gate (`NU1902–1904` as errors). `dotnet list package --vulnerable` reports no vulnerable packages in any project.
- **Error disclosure.** Job failures expose only messages from the app's own validation exceptions. Other exceptions are logged on the server and never sent to clients.
- **Export** is streamed through a `PipeWriter` with keyset pagination, so it is not buffered in memory.

### S1 — Restore replaces household membership from an untrusted file (High, Verified)

`RestoreHouseholdData.ToEntity(HouseholdMemberExportDto, …)` (`src/EnergyTracker.Application/RestoreHouseholdData.cs:70`) copies `ExternalIssuer` and `ExternalSubjectId` straight from the uploaded JSON. `HouseholdRestoreWriter.DeleteExistingDataAsync` (`src/EnergyTracker.Infrastructure/Adapters/HouseholdRestoreWriter.cs:66`) deletes **every** `HouseholdMember` of the household, including the uploader, and then inserts the list from the file. `ValidateHouseholdImport.ValidateHouseholdMember` (`:145`) checks only that the fields are non-empty strings.

What this lets any authenticated member do:

- **Grant access.** Add any (issuer, subject) pair. That identity then resolves to this household with full read, export, restore and invite rights.
- **Lock out.** Leave out the other members, or the uploader, and the household becomes unreachable. There is no recovery path in the UI.
- **Probe (minor).** A pair that already belongs to another household violates the global unique index, so the restore fails. This gives a weak oracle for whether an identity exists.

Even without an attacker, restoring a backup from another instance or IdP removes everyone's access.

**Recommendation:** do not restore `householdMembers` at all, and keep the current members. Ignore the array in the file, or reject files that contain unknown members. Add a test that restores a file whose member list leaves out the caller.

### S2 — Data Protection key ring unencrypted in Azure (Medium, Verified)

`Program.cs` calls `ProtectKeysWithCertificate` only when `DataProtection:CertificateBase64` is set. Otherwise the keys are written in plaintext to the `DataProtectionKeys` table, and the code comment acknowledges that DB read access is then enough to forge a session cookie. `infra/modules/container-app.bicep` sets no `DataProtection__*` variable or secret, so **production runs in the plaintext mode**. The design fails open: an optional setting that is left out silently selects the weaker mode.

**Recommendation:** provide the certificate through a Bicep secret, or use `ProtectKeysWithAzureKeyVault` with the app's managed identity. Outside Development, refuse to start when no key protector is configured.

### S3 — No security headers, HSTS or rate limiting (Medium, Verified)

A search of `src/` finds no `UseHsts`, `UseHttpsRedirection`, `AddRateLimiter`, CSP, `X-Frame-Options`, `nosniff` or `Referrer-Policy`.

- **No CSP.** XSS would have unlimited impact. Exposure today is low: React escapes output, there is no `dangerouslySetInnerHTML`, and no `eval`.
- **No `frame-ancestors` or `X-Frame-Options`.** The app can be framed, which allows clickjacking of actions like restore or invite.
- **No HSTS.** Neither the app nor Azure Container Apps ingress adds it by default.
- **No rate limiting** on `/login`, invite preview and accept, uploads, or `/api/*`.

**Recommendation:** add a small middleware that sets `Content-Security-Policy: default-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self'`, plus `X-Content-Type-Options: nosniff` and `Referrer-Policy: same-origin`. Call `UseHsts()` outside Development. Add `AddRateLimiter` with per-user partitions on the upload, invite and export routes, and per-IP partitions on `/login`.

### S4 — Memory and disk amplification on uploads (Medium, Verified)

- `POST /api/household-import` accepts up to 250 MB plus 5 MB headroom (`HouseholdImportEndpoints.cs:12`). It calls `ReadToEndAsync()` into a single `string`, which in UTF-16 takes about twice the bytes. It then parses that into a `JsonDocument` to validate, and writes the string back to disk. The restore job later reads the whole file again with `File.ReadAllTextAsync` and deserialises the full object graph. Peak memory per upload is plausibly 5–10× the file size, so one 250 MB upload can exhaust memory on a small Container App.
- **Disk exhaustion (new).** Validated uploads stay in the temp directory for 30 minutes unless confirmed. Nothing limits how many a household can have pending, so repeated validate-only uploads can fill the container's ephemeral disk. Expired entries are swept only when the next upload is registered.
- **Leaked temp files (new).** `MemoryHouseholdImportUploadRegistry.Consume` removes the entry before checking expiry and household, and returns `null` without deleting the file on either mismatch. Those files are never removed.
- **XLSX zip bomb.** The 20 MB Smart Plug cap applies to the *compressed* size. `SpreadsheetDocument.Open` is called without `OpenSettings.MaxCharactersInPart`. The whole `SharedStringTable` is loaded into memory, and `LoadCurrentElement()` builds a DOM per row. A small, highly compressed file can therefore expand by orders of magnitude.

**Recommendation:** validate imports with `Utf8JsonReader` over a `FileStream` and stream the upload straight to disk first. Lower the import cap or make it configurable. Allow one pending upload per household and delete the file in `Consume` on every path. Set `MaxCharactersInPart` and add a row-count cap in the XLSX parser.

### S5 — Container hardening and build parity (Medium, Verified)

- The runtime stage never sets `USER`, so the process runs as root. The `aspnet` image includes an `app` user; add `USER $APP_UID`.
- The backend build stage copies `Directory.Packages.props` and `src/` only, **not `Directory.Build.props` or `global.json`**. The NuGet-audit gate that protects CI is therefore not applied to the image that ships, and the image can be built with a different SDK than CI uses.
- Base images are pinned by tag, not by digest. There is no `HEALTHCHECK`, which matters for self-hosters only.

### S6 — Database firewall allows all Azure-hosted callers (Medium, Verified)

Both `database-sqlserver.bicep:119` and `database-postgres.bicep:61` create an `AllowAzureServices` rule (0.0.0.0–0.0.0.0). That rule admits traffic from **any** Azure customer's resources, not just this subscription. On SQL Server, protection then rests on Entra-only authentication, which is acceptable. On the Postgres path it rests on a password. This is a known trade-off for a Consumption-plan setup without a VNet.

**Recommendation:** in the long term, integrate the Container Apps environment with a VNet and use private endpoints. In the short term, prefer the SQL Server path in Azure and generate a strong Postgres password.

### S7 — Hard-coded fallback DB credentials (Low, Verified)

`Program.cs:123` falls back to `Host=localhost;…;Password=change-me` when `ConnectionStrings:Default` is missing, in every environment. **Recommendation:** use the fallback only when `IsDevelopment()`, and throw otherwise.

### S8 — Forwarded headers and Host (Low, Verified)

`KnownProxies` and `KnownIPNetworks` are cleared, so `X-Forwarded-For` and `X-Forwarded-Proto` are trusted from any caller. Nothing security-relevant reads them today, but adding rate limiting (S3) would make the client IP spoofable. `AllowedHosts` is `*`, and the OIDC `redirect_uri` is built from the request host; the IdP's exact-match check on the redirect URI is the only safeguard. **Recommendation:** set `AllowedHosts` to the real hostnames, and restrict `KnownIPNetworks` to the ingress range before introducing per-IP rate limiting.

### S9 — CSRF relies on `SameSite=Lax` alone (Low, Verified)

Antiforgery is disabled on the two multipart endpoints (documented), and no API mutation uses GET. In modern browsers `SameSite=Lax` blocks cookies on cross-site POST, PUT and DELETE, so this is acceptable today. `GET /logout` allows logout-CSRF, which is only a nuisance. As defence in depth, require a custom header such as `X-Requested-With` on `/api` mutations; a cross-site form cannot send it.

### S10 — Invite tokens (Low, Verified)

`Guid.NewGuid().ToString("N")` gives 122 bits of entropy, which is enough. However, tokens are stored in plaintext, there is no endpoint to list or revoke them, and the preview and accept endpoints are not throttled. An invite is a 7-day bearer credential for full household access. **Recommendation:** generate tokens with `RandomNumberGenerator.GetBytes(32)` encoded as base64url, store only a hash, add list and revoke, and rate-limit preview and accept.

### S11 — Supply chain and pipeline (Low, Verified)

- GitHub Actions are pinned to major tags (`actions/checkout@v7`), not commit SHAs.
- The repo has no `dependabot.yml` or CodeQL workflow. Dependabot bump commits exist in history, so it is probably enabled in repo settings; check this. CI has no container image scan.
- **`pr-review.yml` `validate-infra`** gives Azure OIDC credentials (`id-token: write`) and `DATABASE_ADMIN_PASSWORD` to the code of any non-fork PR. Anyone with push access to a branch can therefore run arbitrary `az` commands with that federated credential's role. Check that the `pull_request` federated-credential subject maps to a **Reader**-scoped identity, not the deploy identity.
- **npm audit:** `--omit=dev` reports 1 high advisory (`source-map-js`). The full tree reports 12 (1 critical `proxy-addr`, 8 high, 3 moderate), all transitive through dev tooling (`shadcn`, `vite` and its plugins). The shipped bundle is static, so runtime exposure is nil, but developer machines and CI run this code. `npm audit fix` resolves most of them.
- **Outdated packages:** ASP.NET Core and EF Core are on 10.0.10 while 10.0.12 is available, and OpenTelemetry is on 1.17 while 1.19 is available. `OpenTelemetry.Instrumentation.EntityFrameworkCore` is a **prerelease** (`1.17.0-beta.1`) in production.
- `app-deploy.yml` applies migrations before the new revision passes its health check. Rollback restores the image, not the schema, so migrations must stay backward-compatible; there is a restore-point summary as a mitigation. The temporary SQL firewall rule for the runner is removed in an `always()` step, which is good.

### S12 — Export discloses co-members' identities (Low, Verified)

`/api/household-export` includes every member's `externalIssuer`, `externalSubjectId` and `displayName`. Any member can export their co-members' IdP subject identifiers, which is personal data under GDPR. Combined with S1, this is the raw material for granting access to someone else's household. **Recommendation:** leave member identities out of the export, or export only display names.

### S13 — Session lifetime and revocation (Low, Verified)

The cookie handler sets no `ExpireTimeSpan` or `SlidingExpiration`, so the framework default applies: 14 days, sliding. There is no server-side session store and no way to remove a member (A2). A stolen cookie therefore stays valid as long as it keeps being used, and a household cannot cut off a former member. **Recommendation:** set an explicit shorter lifetime with an absolute cap, and add member removal. Removal already invalidates access effectively, because `CurrentHouseholdAccessor` resolves membership on every request.

### Other security observations

- The AI feature sends event descriptions to the configured endpoint only when the household opts in. Output is reduced to a three-value enum, so prompt injection has little room. The base URL is set by the operator, so this is not SSRF.
- `docker-compose.yml` binds the Aspire dashboard to `127.0.0.1` only and does not publish Postgres by default. Both are correct.
- Display names and original file names are rendered through React and escaped.

---

## 4. Code correctness and robustness

### C7 — Azure Storage Queue consumer can stop the host and retries poison messages forever (Medium, Verified) — *new*

`AzureStorageQueueJobProcessingService.ExecuteAsync` (`src/EnergyTracker.Infrastructure/Adapters/AzureStorageQueueJobQueue.cs`):

- `CreateIfNotExistsAsync` and `ReceiveMessagesAsync` sit **outside** the `try`. Any transient storage or network exception escapes `ExecuteAsync`. Since .NET 6 the default `BackgroundServiceExceptionBehavior` is `StopHost`, and `Program.cs` does not override it, so **the whole web app shuts down**. Container Apps restarts it, but in-flight requests fail and the consumer is dead until the restart.
- There is no `DequeueCount` check and no dead-letter queue. A message whose processing keeps throwing (for example `JsonException` on the envelope, or a DB failure in the processor's bookkeeping) is never deleted. It reappears after the 60-minute visibility timeout, forever.
- The service receives up to 8 messages and processes them one after another, all under a single 60-minute visibility timeout. If one message takes long, the others become visible again and are delivered twice. The processor's status checks make this safe, but it wastes work.

**Recommendation:** wrap the whole loop body in `try/catch` with backoff. After N dequeues (`queueMessage.DequeueCount`), mark the job `Failed` and delete the message, or move it to a poison queue. Receive one message at a time, or renew visibility while a job runs.

### C8 — Orphaned `Processing` jobs are never recovered (Medium, Verified) — *new*

With the default in-process `Channel` queue, a restart while a job is queued or running loses the message, and the `BackgroundJobs` row stays `Queued` or `Processing` forever. No code sweeps stale jobs at startup. The consequences:

- `POST /household-import/{token}/confirm` refuses to start a restore while any restore job is `Queued` or `Processing` (`HouseholdImportEndpoints.cs:134`), so **that household can never restore again** without manual DB surgery.
- `DELETE /smart-plug-import-jobs` returns the stuck cleanup job's id instead of starting a new one.
- The UI shows the job as running indefinitely.

Restarts are routine: scale-to-zero, deploys and the host stop in C7. Any `OperationCanceledException` that does not come from the stopping token (for example an `HttpClient` timeout) is also rethrown past the status update, which leaves the job stuck the same way.

**Recommendation:** at startup, and periodically, mark jobs `Failed` if they have been `Queued` or `Processing` longer than a threshold and have no live message (in-process queue: all of them at startup). Catch every OCE that is not `stoppingToken.IsCancellationRequested` and treat it as an ordinary failure.

### C9 — Concurrent unmapped Smart Plug imports overwrite each other (Medium, Verified in code, not reproduced) — *new*

Readings for an import whose device tag matches no Power Point are upserted with a key of `(HouseholdId, IntervalStart) WHERE PowerPointId IS NULL`. On conflict the upsert updates `SmartPlugImportId`, `DeviceName` and `KwhValue` (`SmartPlugImportRepository.cs:333–368`, with the partial unique index described in `SmartPlugReadingConfiguration.cs:68–92`).

**Scenario:** upload plug A (unknown tag), then plug B (a different unknown tag) covering the same period, and do not map A in between. B's rows overwrite A's rows for every shared `IntervalStart`. When A is later mapped, its import owns only the non-overlapping rows, and the overwritten consumption data is lost or attributed to B. No guard prevents a second unmapped import while another awaits mapping, and I found no test for this scenario.

**Recommendation:** include `SmartPlugImportId` (or the device tag) in the unmapped-row uniqueness key, so that unmapped readings are unique per import rather than per household. Alternatively, reject a new unmapped import while another one awaits mapping. Add a dual-provider test.

### C1 — AI configuration key mismatch (Medium, Verified)

`Program.cs:379–396` reads `AiPlausibility:BaseUrl`, `:ApiKey`, `:Model` and `:BackendLabel`. But `appsettings.json` (`"Ai": { "ApiKey" }`), `docker-compose.yml:15` (`Ai__ApiKey`), `.env.example` (`AI_API_KEY`) and `container-app.bicep:181–186` (`Ai__Endpoint`, `Ai__ApiKey`) all use `Ai:`. The `Ai:*` keys are never read. A self-hoster who follows `.env.example` gets the no-op client while the settings toggle is still shown. The Bicep comments call the AI slot a "placeholder… no adapter implementation exists yet", which is now stale because the adapter does exist.

**Recommendation:** pick one section name everywhere and update the Bicep comment. Log the selected AI adapter at startup, and add a test that binds the documented keys.

### C2 — Synchronous DB query per request (Medium, Verified)

`CurrentHouseholdAccessor.Resolve()` (`src/EnergyTracker.Infrastructure/Adapters/CurrentHouseholdAccessor.cs`) calls `SingleOrDefault()` synchronously, once per request scope, from both the EF query-filter path and endpoint code. This blocks a thread-pool thread for one DB round trip on every API call, which matters with Basic-tier Azure SQL latency and in bursts.

**Recommendation:** resolve membership once in an async middleware (or `IClaimsTransformation`) and cache the result in `HttpContext.Items`, so the accessor only reads cached values.

### C3 — Job payloads point at local temp files (Medium, Verified)

Both upload paths write to `Path.GetTempPath()` and enqueue only the path. With the Azure queue, a restart between enqueue and processing loses the file while the message survives, and the job fails with "upload again". A redelivery after a partial failure finds the file already deleted, because both use cases delete it in `finally`. Files are created in the shared temp directory with default permissions. This is acceptable only while there is a single replica (A1). Otherwise, store uploads in blob storage and pass a reference.

### C10 — Restore reuses primary keys and idempotency keys from the file (Low, Verified) — *new*

Every restored entity keeps its `Id` from the file, and meter readings keep their `IdempotencyKey`, which has a *global* unique index. Restoring household A's export into household B on the same instance while A still exists, for example to duplicate or migrate a household, fails with a PK or unique violation. The failure only surfaces after the delete phase has run inside the transaction, so it rolls back cleanly but gives a generic failure. A related issue: the check that only one restore runs per household is check-then-enqueue, with no DB constraint (the earlier draft wrongly called this DB-level). Two concurrent confirms with two different tokens can both pass.

**Recommendation:** re-key entities on restore (remapping FKs within the file), or validate up front and reject with a clear message. Add a filtered unique index on active jobs per household and job type if the guarantee matters.

### C11 — `/login` returns 500 without OIDC (Low, Verified) — *new*

When `Oidc:*` is unset, the OIDC scheme is never registered, but `/login` still challenges `OpenIdConnectDefaults.AuthenticationScheme`. That throws `InvalidOperationException` and the user gets a 500. Return a clear 503 or a "not configured" page instead.

### C4 — Offline queue discards rejected readings silently (Low, Verified)

`flushQueue` (`web/src/lib/meter-reading-sync.ts:108–111`) deletes a queued reading on any permanent 4xx and only calls `console.error`. The user is never told that a reading they entered offline was dropped. Keep such readings in a "needs attention" state and show them in the UI.

### C5 — `HouseholdRecomputeLock` (Low, Verified)

The `ConcurrentDictionary<Guid, SemaphoreSlim>` never evicts entries, which is a negligible leak at household scale. It serialises work only within one process, so it is correct only under A1.

### C6 — Direct `UtcNow` (Low, Verified)

There are about 47 direct `DateTimeOffset.UtcNow` calls and no `TimeProvider`, so invite expiry, upload TTLs and recompute windows can only be tested against the real clock. Inject `TimeProvider`, which is built into .NET 8 and later.

### C12 — "Trending" threshold has different units in the two calculators (Low, Inferred) — *new*

`WindowedDeviationCalculator` treats `trendingThresholdKwh` as an **annual** rate: it scales the threshold by elapsed/365 through `BonusDecayNormalizer`. `PatternDetectiveCalculator.ResolveStatus` compares the to-date difference against the **unscaled** value. One of the two probably does not match the intended meaning. `ResolveStatus` is also asymmetric: any shortfall at all is `BelowBaseline`, while `Trending` requires exceeding the threshold. Confirm the intended semantics and document them on `Household.TrendingThresholdKwh`.

### Other minor items

- `catch (Exception)` in `ProcessSmartPlugImport` calls `PersistFailedImportAsync` and rethrows. If that persist call itself throws (for example because the DB is down), its exception replaces and hides the original one. Log the original exception first.
- There are no `TODO`, `FIXME`, `async void` or `.Result` calls in `src/`.
- Frontend lint reports 4 warnings, one of them real: a `react-hooks/exhaustive-deps` warning in `use-smart-plug-import-job.ts:61` (`t` and `file` are missing).
- The build reports 6 `xUnit1051` warnings, where tests do not pass `TestContext.Current.CancellationToken`.

---

## 5. Architecture & design

### Strengths

- **The hexagonal layering is real and tested.** Domain has no dependencies (`DomainHasNoExternalDependenciesTests`). Application depends only on Domain and owns its ports. Infrastructure implements the ports, and the API is a thin composition root plus endpoints. Architecture tests also guard tenant isolation in the export reader and assert that the frontend never stores tokens.
- **One class per use case** in Application (`CreateTariff`, `CorrelateEvent`, …) makes the code easy to navigate and test.
- **One place reads configuration.** The DB provider, OIDC, OTel, the queue and AI settings are each read once in `Program.cs`, and each feature switches itself off cleanly when unset.
- **Concurrency was thought through.** Optimistic concurrency uses `Version`, meter readings have idempotency keys, Smart Plug writes take transaction-scoped advisory locks (`pg_try_advisory_xact_lock` and `sp_getapplock`), and job bookkeeping is idempotent under redelivery.
- **Operations are mature.** Deploys produce a single artifact, a health gate triggers automatic rollback, all infrastructure is in IaC, there is a runbook for the Entra-only SQL cutover, and the local-versus-Azure differences are documented.

### A1 — Single-replica assumptions are scattered and unenforced (Medium)

Correctness depends on exactly one process in at least six places:

- `MemoryHouseholdImportUploadRegistry`
- `HouseholdRecomputeLock`
- temp-file job payloads (C3)
- the in-process queue
- the lack of stale-job recovery (C8)
- the upload sweep

Only `maxReplicas = 1` in Bicep enforces this. Meanwhile `AzureStorageQueueJobQueue` and some comments suggest that multiple replicas are supported, and self-hosters control their own scaling. **Recommendation:** decide explicitly. Either document single-instance as an invariant in `docs/self-hosting.md` and link the coupled components from the Bicep `maxReplicas` comment, or move the registry, lock and uploads to shared stores.

### A2 — No authorization model beyond membership (Medium)

Every member can export, restore (replacing all data), delete Smart Plug history, invite, and change settings. There are no roles, no audit trail of who did what, and no way to remove a member. This is a reasonable choice for a household, but it amplifies S1, S10, S12 and S13. **Recommendation:** at minimum, add an Owner role for restore, export and invite, plus a way to remove members. Otherwise, state "all members are fully trusted" as an explicit non-goal.

### A3 — Oversized units (Low)

- `Program.cs` is 542 lines with about 70 registrations. Split it into `AddObservability`, `AddAuth`, `AddApplicationServices` and `AddJobQueue` extension methods.
- `SmartPlugImportRepository` is about 1,190 lines and mixes the write path, gap persistence, cleanup, mapping and two SQL dialects. Split it by responsibility.
- `tagging-scaffold-manager.tsx` (865 lines) and `TaggingScaffoldEndpoints.cs` (401 lines) can be split by entity: room, power point and device.

### A4 — Dual database providers (Low)

Supporting both Postgres and SQL Server doubles the migrations (57 files each), the raw SQL (`ON CONFLICT` versus `MERGE`) and the Testcontainers matrix. It also hides constraints from the EF model: the partial unique index from C9 exists only in hand-written migration SQL. The work is well executed, but the cost is permanent. Reconsider whether both providers need to be kept long term.

### Other notes

- Tenant isolation is enforced only at the application level, through EF filters. Raw SQL and `ExecuteDelete` paths bypass the filters and rely on code review plus a single architecture test. For defence in depth, add Postgres row-level security or an architecture test that covers every raw-SQL call site.
- The Api project references the Azure Monitor and Azure Storage Queue SDKs directly, which ties the self-host image to Azure packages.
- Import is fully buffered while export is streamed. Using the same streaming approach for import would fix most of S4.

---

## 6. Readability & maintainability

### Strengths

Naming is consistent and descriptive, and files map one-to-one to use cases. Nullable reference types are enabled everywhere. The frontend is organised by feature, with tests next to the code. `docs/` covers self-hosting, import and restore, and the export format.

### R1 — Comments embed story and review history (Low)

Many comments narrate process history, for example "Code Review, Story 7.2 Pass 1", "Story 3.10's 4th incident" and "round-3 incident fix (2026-09-12)". In `Program.cs` and the repositories the comments are often longer than the code. The *why* is valuable, but the history belongs in git, PRs or ADRs. Many `AD-n` and `Story n.n` references point into `_bmad-artifacts`, which is not part of the shipped documentation. **Recommendation:** keep one to three lines of *why* per decision, move the long reasoning into `docs/adr/`, and drop the review-pass attributions.

### R2 — Copy-paste by convention (Low)

The same `TryGetHouseholdId` helper is declared separately in 11 endpoint files. `ApiError`, `toApiError` and `fetch(..., { credentials: 'include' })` are re-declared in about 10 `*-api.ts` modules. Changing the error contract or adding a CSRF header (S9) therefore means editing more than 10 places. **Recommendation:** extract an endpoint filter for the household check and a small shared `api-client.ts`.

### R3 — No backend style or analyzer gate (Low)

There is no `.editorconfig`, no Roslyn analysis mode, no `dotnet format` check and no `TreatWarningsAsErrors`, apart from the NuGet audit codes. **Recommendation:** add `.editorconfig`, set `<AnalysisMode>Recommended</AnalysisMode>` (at least for the CA2xxx, CA3xxx and CA5xxx security rules), and run `dotnet format --verify-no-changes` in `pr-review.yml`.

---

## 7. Testing & delivery

| Check | Result |
|---|---|
| `dotnet build -c Release` | 0 errors, 6 warnings (xUnit1051) |
| `dotnet test` (Application, Api, Infrastructure + Testcontainers, Architecture) | **933 tests, all passed, 0 skipped** (7 min 18 s; Infrastructure takes 7 min 16 s) |
| `tsc -b` | clean |
| `oxlint` | 0 errors, 4 warnings |
| `vitest run` | **62 files, 546 tests, all passed** |
| `dotnet list package --vulnerable --include-transitive` | no vulnerable packages |
| `npm audit --omit=dev` | 1 high (build-time `source-map-js`) |

- **Breadth is strong:** four .NET test projects, including Testcontainers suites for both DB engines, plus Vitest and Playwright.
- **Gaps:** Playwright e2e tests are deliberately excluded from CI. There are no tests for hostile restore membership (S1), stale-job recovery (C8), poison messages (C7), overlapping unmapped imports (C9), binding of the documented AI config keys (C1), or security headers.
- **CI gates:** build, test and lint run on every PR. Infra what-if runs only for non-fork PRs that touch Bicep. Deploy is restricted to `main` and has a health gate with automatic rollback.

---

## 8. Remediation plan

| Priority | Items | Effort |
|---|---|---|
| **Now** | **S1**: stop restoring `householdMembers`; add a test | S |
| **Now** | **C7**: try/catch with backoff around the queue loop; dequeue-count limit | S |
| **Now** | **C8**: sweep stale jobs at startup; treat non-shutdown OCE as a failure | S |
| **Now** | **C9**: make unmapped readings unique per import, or block a second pending unmapped import | M |
| **Now** | **S2**: provide the DP certificate or Key Vault in Bicep; fail at startup when unprotected outside Development | S–M |
| Soon | **S3**: security-header middleware, HSTS, rate limiter (after S8's proxy config) | M |
| Soon | **S4**: stream-validate imports; one pending upload per household; delete the file in `Consume`; XLSX `MaxCharactersInPart` | M |
| Soon | **S5**: `USER $APP_UID`; copy `Directory.Build.props` and `global.json` into the image build | S |
| Soon | **C1**: unify the `Ai`/`AiPlausibility` keys; log the selected adapter | S |
| Soon | **S7, S8, C11**: dev-only connection-string fallback; real `AllowedHosts`; 503 for unconfigured `/login` | S |
| Soon | **S11**: verify the PR what-if identity's role; `npm audit fix`; patch-bump ASP.NET, EF and OTel | S |
| Later | **C2**: async, cached household-resolution middleware | M |
| Later | **A1**: decide and document the single-instance invariant | S (decision) |
| Later | **S10, S12, S13, A2**: hashed and revocable invites; drop identities from the export; explicit session lifetime; Owner role and member removal | M–L |
| Later | **C10, C12**: re-key on restore; confirm the trending-threshold semantics | S–M |
| Later | **R1–R3, A3**: trim comments, shared endpoint filter and API client, `.editorconfig` with analyzers, split large files | M |
