# Deferred work — accepted trade-offs

Archived 2026-10-02 from `deferred-work.md` (Epic 8 retro action #6, `spec-deferred-work-triage.md`). Each entry was deliberately accepted (pre-existing convention, unreachable today, or an explicit trade-off); the `[accepted: ...]` marker holds the triage reason and, where one exists, the revisit trigger. Move an entry back to `deferred-work.md` if its trigger fires.

## Deferred from: code review of 1-2-azure-infrastructure-as-code-resource-deployment-pipeline (2026-08-12)

- [accepted: still in both bicep modules; non-VNet architecture AD-6/7] DB firewall rule allows all Azure-service traffic (`AllowAzureServices`, `0.0.0.0`-`0.0.0.0`) [infra/modules/database-postgres.bicep:54, infra/modules/database-sqlserver.bicep:51] — inherent to the non-VNet-integrated Consumption-plan architecture already committed to by AD-6/AD-7; revisit if/when VNet integration or private endpoints are ever adopted.

- [accepted: placeholder-era note; auth shipped in 1.5] Public ingress with no auth/access-control gate [infra/modules/container-app.bicep:58-62] — expected at this stage since only the public placeholder image is deployed (no real app or data yet); revisit once Story 1.5 (household/OIDC auth) lands to confirm the gate is actually wired before real data is exposed.

- [accepted: no `environment:` in workflows; matches AC design] No approval/environment-protection gate before the deploy step runs [.github/workflows/infra-deploy.yml] — matches AC #2/#3's literal push-to-main auto-deploy design; revisit if a staging environment or required-reviewer policy is ever wanted for this repo.


## Deferred from: code review of story-1-3-ci-build-test-cd-deploy-pipeline-app-to-azure (2026-08-12)

- [accepted: only from-scratch redeploy] Re-adding the ACR `registries` entry unconditionally in `container-app.bicep` would reintroduce the exact eager-validation 401 race Story 1.2 removed it to avoid, if `infra-deploy.yml` is ever run against a brand-new environment (Container App + AcrPull role assignment not yet existing) rather than redeployed against the current live Story 1.2 environment [infra/modules/container-app.bicep:79-84] — zero current impact since the live environment already exists; only relevant to a future from-scratch/disaster-recovery redeploy, out of this story's scope.


## Deferred from: code review of 1-4-pull-request-review-workflow (2026-08-13)

- [accepted: revisit if blocks code] `web/.oxlintrc.json`'s new `"no-unused-vars": "error"` has no ignore pattern for intentionally-unused vars (e.g. `_`-prefixed args) [web/.oxlintrc.json] — repo is clean today; revisit if it starts blocking legitimate code.

- [accepted: low risk] Branch-protection `required_status_checks.checks` entries omit `app_id`, so GitHub matches the required check by context string from any reporting source, not only this workflow's job [infra/README.md — branch protection `gh api` payload] — low practical risk for this repo's trust model; hardening improvement, not a defect.

- [accepted: platform setting] GitHub's "require approval to run workflows for first-time/outside contributors" setting, if enabled, could leave a fork PR's Actions run never starting — both required checks stay perpetually pending, blocking merge indefinitely, with no code-level guard possible [.github/workflows/pr-review.yml — fork-handling design] — platform-level setting outside this diff's control; worth a doc note in a future pass.

- [accepted: fine today] "Notice — infra changed but validation skipped (fork PR)" step's fork-skip condition relies on no earlier step being able to fail on that path — fine today, but fragile if a future edit inserts an unconditional failing step before it without adding `if: always()` [.github/workflows/pr-review.yml:120-122].


## Deferred from: code review of story-1.5 (2026-08-13)

- [accepted: SingleAsync dup group; unreachable] `GET /api/session`'s `SingleAsync` throws an unhandled exception if a resolved `HouseholdId` doesn't correspond to an existing `Households` row [src/EnergyTracker.Api/Endpoints/SessionEndpoints.cs:25] — pre-existing gap that only becomes reachable once a future household-deletion feature exists; no code path in this story can produce the inconsistent state today.


## Deferred from: code review of 1-6-cicd-deploy-idempotency-container-app-image-preservation (2026-08-14)

- [accepted: still in 3 workflows; one app by design] `az containerapp list --query "[0].name"` silently picks an arbitrary Container App if more than one ever exists in the resource group, rather than filtering by an identifying name/tag [.github/workflows/infra-deploy.yml:55] — pre-existing pattern copied verbatim from `app-deploy.yml:255`'s identical lookup, which is itself justified by "Exactly one exists in this resource group by design"; this diff duplicates rather than introduces the assumption. Revisit if a second Container App is ever added to the resource group.


## Deferred from: code review of 1-7-oidc-redirect-uri-scheme-correctness-behind-container-apps-ingress (2026-08-14)

- [accepted: Program.cs:476-477 cleared; self-limited] Unvalidated forwarded-header trust newly reachable in production [tests/EnergyTracker.Api.Tests/ForwardedHeadersTests.cs, src/EnergyTracker.Api/Program.cs:174-183] — clearing `KnownIPNetworks`/`KnownProxies` means `ForwardedHeadersMiddleware` now honors `X-Forwarded-Proto`'s raw value from any peer in production for the first time (previously the header was always ignored since Container Apps' peer never matched the loopback-only default). No validation restricts the value to exactly `"http"`/`"https"`, and no test covers a malformed or missing-header case. Reason for deferring: self-limited blast radius (only the requesting client's own scheme/cookie decision, no cross-user impact); keeps this story scoped to its stated four-line surgical fix. Revisit if the app ever adds logic that treats `Request.Scheme` as a trust signal beyond redirect_uri/cookie policy.

- [accepted: no multi-hop topology] `ForwardLimit` left at its ASP.NET Core default (1), untested for multi-hop proxy chains [src/EnergyTracker.Api/Program.cs:174-177] — no multi-hop topology exists today (verified: no Front Door/App Gateway/CDN in `infra/`); revisit if an additional proxy hop is ever introduced in front of Container Apps' ingress.


## Deferred from: code review of 1-9-room-power-point-device-management (2026-08-14)

- [accepted: soft-delete self-healing] Check-then-act race: `CreatePowerPoint`/`CreateDevice` check the parent's `ArchivedAt` and then save separately with no transaction, so a concurrent Archive of the parent between check and save still lets the child get created [src/EnergyTracker.Application/CreatePowerPoint.cs, CreateDevice.cs] — no transactional guards used anywhere else in this codebase either, and impact is low given the soft-delete architecture is self-healing (the orphaned child can simply be archived too).

- [accepted: entry itself re-checked 2026-09-17; remaining null! only in migration-only tests] `EnergyTrackerDbContext` constructed with a stand-in `ICurrentHouseholdAccessor` (`null!`, both migration factories, and `PostgresMigrationTests`/`SqlServerMigrationTests`) will throw a `NullReferenceException` if anything ever queries Room/PowerPoint/Device through it [src/EnergyTracker.Infrastructure.Migrations.Postgres/EnergyTrackerDbContextFactory.cs, tests/EnergyTracker.Infrastructure.Tests/PostgresMigrationTests.cs] — currently safe since migration tooling never queries the model, but the exact construction pattern is already copy-pasted in test code; revisit when the next story adds a repository-level integration test against Room/PowerPoint/Device. (Updated 2026-08-14: a same-day DI refactor replaced the original `IServiceProvider`-based construction this item was first written against with direct `ICurrentHouseholdAccessor` injection — the underlying "never queried in practice at design time" risk is unchanged, just via a different failure mode.) **Re-checked 2026-09-17 (architect review ahead of Epic 6):** the named trigger has since fired — `PostgresMigrationTests.cs`, `SqlServerMigrationTests.cs`, `SmartPlugImportRepositoryTests.cs`, `SmartPlugImportRepositoryAddAsyncDualProviderTests.cs`, and `SmartPlugImportRepositoryAddAsyncMinimalSqlServerPermissionsTests.cs` all now run real Testcontainers-backed queries against `dbContext.Rooms`/`dbContext.PowerPoints`. In every one of them, the author correctly swapped in a real `FixedHouseholdAccessor(householdId)` instead of `null!` for the DbContext instance actually used for those queries — `null!` survives only in the migration-history-only test methods that never touch domain tables (e.g. `Postgres_migrations_apply_cleanly_to_a_real_database`) and in the two design-time `IDesignTimeDbContextFactory` implementations, which `dotnet ef migrations add` never queries either. So the NRE has not occurred, not because of a structural guard, but because five independent test authors have each manually avoided the trap. No live bug and no code change proposed; downgraded from "revisit on next trigger" to low priority since the trigger has recurred multiple times without incident. Worth a lint/analyzer rule only if this pattern ever bites for real.

- [accepted: no react-router in web/package.json] Settings navigation bypasses browser back-button history (no `react-router`, local `view` state) [web/src/App.tsx] — consistent with the pre-existing pattern already used by the Invite panel, not a new regression introduced by this story.


## Deferred from: code review of spec-design-token-wiring (2026-08-15)

- [accepted: Ralf decision, Epic 8 retro: out of scope; no toHaveScreenshot] source_spec: `_bmad-artifacts/implementation/spec-design-token-wiring.md`
  summary: No visual-regression/screenshot-diff safety net exists anywhere in the project despite `test:e2e` (Playwright) already being configured, so a global theme change like this one ships with no automated check that palette/contrast didn't break elsewhere.
  evidence: Real gap, but pre-existing and disproportionate to this story's scope (a CSS-variable remap) — standing up visual regression tooling from scratch is a separately-scoped investment, worth doing once there's more themed UI (Epic 2's Status card, Trend chart, etc.) to actually protect.


## Deferred from: code review of story-2.1 (2026-08-15)

- [accepted: SingleAsync dup group] Unhandled not-found path on `Household` lookups — `GET /households/{id}` and `HouseholdRepository.UpdateYearlyBaselineAsync` both use `SingleAsync` with no not-found guard; a missing row throws an uncaught `InvalidOperationException` → 500 instead of a `ProblemDetails` 404 [src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs:90, src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs:96] — currently unreachable (no Household-deletion feature exists anywhere in the app) and mirrors the pre-existing `AcceptInviteAsync` `SingleAsync` pattern already in the codebase; not a new anti-pattern introduced by this diff.


## Deferred from: code review of story-2.2 (2026-08-15)

- [accepted: MeterReadingConfiguration.cs:50; UUID entropy] `IdempotencyKey` unique index is global, not Household-scoped; combined with the AD-3 query filter, a cross-Household key collision would surface as an unhandled 500 instead of a controlled response [src/EnergyTracker.Infrastructure/Adapters/MeterReadingRepository.cs:65] — probability is negligible with `crypto.randomUUID()` (122 bits of entropy); no realistic trigger path.


## Deferred from: code review of spec-custom-domain-managed-cert (2026-08-16)

- [accepted: rule superseded: commit c2a3a4a set customDomainName/CertificateReady in bicepparam; moot] source_spec: `_bmad-artifacts/implementation/spec-custom-domain-managed-cert.md`
  summary: The "never add customDomainName to infra/main.bicepparam" rule (docs/local-vs-azure-deltas.md#D5) has no guard at all in `infra/main.bicepparam` itself — unlike every other "leave blank for now" param there (oidcAuthority, otelAlertNotificationEmail), which carries an inline comment explaining why. No bicep-lint rule, CI check, or pre-commit guard stops a future story from adding a live value either.
  evidence: This story's own spec explicitly forbids touching `infra/main.bicepparam` at all (frozen Boundaries, approved at Checkpoint 1), so even an explanatory comment-only addition is out of scope here without a human-approved spec change. A future story revisiting `main.bicepparam` should add that comment (mirroring the oidcAuthority/otelAlertNotificationEmail style) and/or build an automated guard (lint rule or `validate-infra` check) if this class of "blank-by-convention-only" param proliferates further.


## Deferred from: code review of story-2.4 (2026-08-17)

- [accepted: in-spec per AD-7] Resolving a `MeterRegressionPrompt` doesn't trigger a Status recompute, leaving a `StatusSnapshot` audit-trail gap at classification-resolution events [src/EnergyTracker.Application/CreateMeterReading.cs — contrast with Story 2.3's resolve-prompt use case] — in-spec per AD-7's two-call-site rule; revisit once FR-8 Trend History is built.


## Deferred from: code review of story-3.2 (2026-08-20)

- [accepted: architectural; no Version in MapSmartPlugImportToPowerPoint] No optimistic-concurrency protection on SmartPlugImport mapping — concurrent/double-submit requests can race [src/EnergyTracker.Application/MapSmartPlugImportToPowerPoint.cs:16-45] — no Application-layer use case in this codebase carries a concurrency token beyond `Household`/`HouseholdInvite`; fixing this is a broader architectural decision, not specific to this diff.

- [accepted: no idempotency pattern] Mapping endpoint isn't idempotent — retrying after a lost response on a successful mapping returns 409 instead of the original success [src/EnergyTracker.Application/MapSmartPlugImportToPowerPoint.cs:16-20] — no idempotency-key pattern exists anywhere in this codebase.

- [accepted: later work addressed bulk writes (3.8/3.9); mirrors 3.1] `ListReadingsByImportIdAsync`/`UpdateMappingAsync` load and update an import's full reading set unpaged [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:415-431] — mirrors `ProcessSmartPlugImport`'s existing bulk-write pattern from Story 3.1, not introduced by this diff.


## Deferred from: code review of story-3.3 (2026-08-20)

- [accepted: SmartPlugCoverageSignal.cs:25-26; no household tz concept] `SmartPlugCoverageSignal.HasCoverageDuringAsync` derives calendar-date boundaries from UTC `DateTimeOffset`s via `.DateTime`, while `SmartPlugImportGap.StartDate`/`EndDate` are local-time dates per AD-9 [src/EnergyTracker.Infrastructure/Adapters/SmartPlugCoverageSignal.cs:25-26] — can misalign by a day near local midnight for non-UTC households, nudging the low-confidence corroboration boundary. No household-timezone concept exists anywhere else in the codebase to fix this properly against; a point fix here would be inconsistent with the rest of the app.

- [accepted: documented non-goal] Concurrently-processed imports for the same Power Point can each compute gaps against a stale view of the other's not-yet-committed readings, persisting a phantom `Missing`/`Estimated` gap for dates the other import actually fills [src/EnergyTracker.Application/CompleteSmartPlugImportProcessing.cs] — same accepted tradeoff as this story's own documented "No retroactive gap re-detection" non-goal; no locking/serialization primitive exists anywhere else in the codebase for background-job concurrency either (mirrors the already-deferred Story 2.4 concurrent-`RecomputeAsync` race).


## Deferred from: code review of story-3.4 (2026-08-23)

- [accepted: no vendor-swap case] Watermark is computed as `MAX(IntervalStart)` across all vendors for a Power Point, regardless of which import wrote it [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:301-306] — if a Power Point ever received imports from both Eve Home (~10-min granularity) and Meross (day-level granularity), e.g. after a hardware swap, a fine-grained watermark could silently suppress a genuinely-new coarser-grained row or vice versa. Deferred: narrow/unlikely scenario, no vendor-swap use case exists today (a physical Smart Plug is either Eve Home or Meross for its lifetime).

- [accepted: story Dev Notes OQ#2] No test exercises a true concurrent watermark race (two workers reading the same stale watermark before either commits) [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:12-50] — explicitly an accepted trade-off per this story's own Dev Notes Open Question #2 ("protects paths the optimization can't reach"), same class as Story 3.3's own already-deferred concurrent-import race.

- [accepted: documented trade-off] Eve Home's early-stop assumes `IntervalStart` order tracks `RowIndex` order 1:1 — a file with ascending `RowIndex` but non-monotonic timestamps would silently break early, dropping genuinely-new rows [src/EnergyTracker.Infrastructure/Adapters/EveHomeXlsxParser.cs:126-131] — already explicitly identified and accepted as a documented trade-off in this story's own Completion Notes.

- [accepted: by construction] `AnyExistingReadingAtSameKeyAsync`/`AnyMappingConflictAsync` derive the whole batch's `PowerPointId`/`HouseholdId` from `readings[0]` alone, unenforced [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:52-73,242-257] — true today by construction (every reading in a batch always shares the same values, per `ProcessSmartPlugImport`'s assignment loop), no live call site can violate it currently.


## Deferred from: code review of story-2.7 (2026-08-23)

- [accepted: shadcn Dialog pattern, dialog.tsx:66] Duplicate "Close" accessible name inside `StatusDetailDialog` [web/src/components/dashboard/status-detail-dialog.tsx:141-145] — the footer `Close` button and shadcn `DialogContent`'s built-in "X" close button share the accessible name "Close"; `status-detail-dialog.test.tsx` already works around it by DOM order. Same pattern exists in every `DialogFooter` usage in `tagging-scaffold-manager.tsx` — pre-existing codebase-wide shadcn Dialog pattern, not introduced by this diff.


## Deferred from: code review of spec-status-recompute-serialization-perf, round 4 (2026-08-24)

- [accepted: unreachable by construction] source_spec: `_bmad-artifacts/implementation/spec-status-recompute-serialization-perf.md`
  summary: If `GetRecentByMainMeterAsync`'s `mustIncludeReadingId` lookup ever fails (the repository falls back gracefully to the base window) while the open prompt's own trigger reading is itself older than that un-widened base window, `PatternDetectiveCalculator.ExcludeFromOpenPrompt` throws `InvalidOperationException` — caught/logged on the `StatusRecomputeService.RecomputeAsync` path, but uncaught on `GET /api/status`/`/api/status/detail` (`StatusEndpoints.cs`), surfacing as a 500.
  evidence: Currently unreachable by construction — `mustIncludeReadingId` is always `openPrompt.PreviousMeterReadingId`, which `CreateMeterReading.cs`'s `FindImmediatelyPrecedingAsync` always scopes to the same `MainMeterId` as the prompt itself, and no delete path exists for `MeterReading` anywhere in the repository, so the lookup cannot fail for real prompt data today. Same class of "structurally unreachable, defensive code exists anyway" gap as several prior entries in this file (e.g. Story 1.9's `ICurrentHouseholdAccessor` entry). Raised by adversarial review, round 4.


## Deferred from: code review of story-3-7-smart-plug-reading-duplicate-cleanup-on-late-mapping (2026-08-26)

- [accepted: pre-existing pattern] TOCTOU window between `UpdateMappingPerRowWithConflictToleranceAsync`'s conflict-confirmation read and the delete/skip decision it drives [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:303-330] — nothing pins the colliding "already-mapped" row in place between the read and the write, so a genuinely concurrent mutation/removal of that row in the gap could make the delete-vs-skip decision against stale data. Deferred, pre-existing: this check-then-act pattern already existed in this method (and in `AnyMappingConflictAsync`'s pre-check ahead of the `ExecuteUpdateAsync` fast path) before this story — Story 3.7 only changed what happens *after* the conflict is confirmed (delete vs. skip), not the underlying race. Raised by adversarial review (Blind Hunter).


## Deferred from: code review of story-3-5-dual-entry-points-multi-file-import-queuing (2026-08-27)

- [accepted: copied pre-existing gate] If the backend ever returns `importStatus: 'awaitingpowerpointmapping'` with a null/empty `smartPlugImportId`, the mapping dialog never renders and, unlike the old panel's global reset button (available during `awaitingMapping` too), the new per-item "Remove from queue" button also excludes `awaitingMapping`. Deferred, pre-existing: this exact null-check gate is copied byte-for-byte from the pre-diff panel, not introduced by this diff. Raised by edge-case review. [web/src/components/smart-plug-import/smart-plug-import-page.tsx:110-111,158; web/src/components/smart-plug-import/use-smart-plug-import-job.ts:91-94]


## Deferred from: code review of story-3-6-smart-plug-import-job-status-history (2026-08-28)

- [accepted: single caller ListSmartPlugImportJobs.cs:58; revisit if 2nd caller] `QueuedByHouseholdMemberId`'s tenant isolation for the new `FindMembersByIdsAsync` lookup is trust-based, not schema-enforced — `HouseholdMember` deliberately carries no AD-3 query filter (established codebase convention), and this new call site queries it with no household predicate, relying entirely on the caller (`CurrentHouseholdAccessor`) never supplying a cross-household id. Deferred, pre-existing convention: not exploitable via any current call path since the id always comes from the caller's own authenticated household context; worth hardening only if this method ever gets a second caller. Raised by adversarial review (Blind Hunter). [src/EnergyTracker.Infrastructure/Adapters/BackgroundJobRepository.cs, src/EnergyTracker.Application/ListSmartPlugImportJobs.cs:55-58]


## Deferred from: code review of spec-power-point-mapping-list-scroll (2026-09-02)

- [accepted: minor cosmetic] source_spec: `_bmad-artifacts/implementation/spec-power-point-mapping-list-scroll.md`
  summary: `overflow-y-auto` on the mapping list can introduce a vertical scrollbar only once the row count crosses the `max-h-64` threshold, narrowing row content width at that exact moment (including the live "just-created Power Point appended to the list" flow) with no `scrollbar-gutter`/reserved padding to prevent the shift.
  evidence: Minor cosmetic edge case with no established scrollbar-gutter convention anywhere else in the codebase (this is the first scrollable content region inside a `DialogContent` in this repo); not worth introducing new, untested cross-browser CSS for a one-shot bug fix. Raised by adversarial review (Blind Hunter).


## Deferred from: code review of story-4-3-correcting-a-meter-reading (2026-09-10)

- [accepted: EditMeterReading.cs:32-35 deliberate with comment] Optimistic-concurrency check is bypassed entirely on the no-op path [src/EnergyTracker.Application/EditMeterReading.cs:32-35] — deferred, pre-existing (Story 2.8, untouched by this diff): `if (oldValue == kwhValue) return reading;` short-circuits before `UpdateKwhValueAsync` (the only place `expectedVersion` is checked), so a caller holding a stale `expectedVersion` whose submitted value happens to equal the *current* value gets a silent 200 instead of the 409 AC #2 promises. This story's Task 2 claims to fully regression-prove AC #2; the strengthened test only exercises a genuine value-differing race, not this edge. Raised by adversarial review (Blind Hunter).

- [accepted: mirrors CreateMeterReading.cs:100] Unhandled `RecomputeAsync` exception after the write transaction already committed [src/EnergyTracker.Application/EditMeterReading.cs:57-61] — deferred, pre-existing (identical, deliberately-mirrored pattern in `CreateMeterReading.cs:100`, per this story's own spec instructions to mirror that placement exactly): a transient failure in the recompute step surfaces to the caller as a failed edit even though the kwh correction and audit note were already durably persisted. Raised by edge-case review.


## Deferred from: code review of spec-3-10-cleanup-batch-delete-fix (2026-09-12)

- [accepted: manual cleanup now async job (spec-3-10-cleanup-async-job)] source_spec: `_bmad-artifacts/implementation/spec-3-10-cleanup-batch-delete-fix.md`
  summary: `DeleteEligibleAsync`'s batched delete has no upper bound on total chunk count per call — an extremely large household could still fail to complete "clean up everything" within an HTTP/ingress idle timeout even though no single chunk hits `CommandTimeout`, since the fix intentionally preserves one all-or-nothing transaction across every chunk (relaxing that atomicity guarantee, e.g. via partial commits or moving to the async job queue, is an explicit non-goal of this fix).
  evidence: Raised by Edge Case Hunter and adversarial review (Blind Hunter) independently. Not exercised by the reported 2026-09-12 incident (the reported household's eligible set was resolved by batching alone); worth a follow-up if a future incident shows the *request*, not a single command, timing out. [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:749-773]

- [accepted: prod is Azure SQL; revisit if Postgres] source_spec: `_bmad-artifacts/implementation/spec-3-10-cleanup-batch-delete-fix.md`
  summary: The shared outer transaction now stays open for longer wall-clock time (one round trip per chunk instead of one big command), which would hold row locks and block Postgres `autovacuum` on every table it touches for that whole duration if this code ever runs against a Postgres-backed deployment.
  evidence: Currently inert — the deployed production database is Azure SQL (SqlServer), not Postgres (AD-2 dual-provider, config-selected) — but real if `databaseProvider` is ever switched back to Postgres. Raised by adversarial review (Blind Hunter). **Updated round 4 (2026-09-12):** the per-import batched reading-detach fix (`spec-3-10-cleanup-per-import-detach.md`) substantially increases how long this transaction stays open for a large single import (~1,200 additional sequential round trips for a 122,158-row import), further widening this same risk if Postgres is ever adopted. [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:749-773]

- [accepted: pre-existing, widened only] source_spec: `_bmad-artifacts/implementation/spec-3-10-cleanup-batch-delete-fix.md`
  summary: `DeleteJobsAsync`'s eligibility query runs once, before `DeleteEligibleAsync` opens its transaction; batching makes the delete phase take meaningfully longer in wall-clock terms than the previous single-command version, widening (not introducing) the existing gap between "what was eligible at query time" and "what actually gets deleted."
  evidence: Same class of gap that already existed pre-fix, just wider in degree now. Raised by adversarial review (Blind Hunter). [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:704-738]

- [accepted: constants SmartPlugImportRepository.cs:765,821; revisit on repeat 500] source_spec: `_bmad-artifacts/implementation/spec-3-10-cleanup-batch-delete-fix.md`
  summary: `DeleteBatchSize = 200` has no benchmarked basis (chosen from the 2026-09-12 incident's Azure Monitor DTU/Log IO evidence, not a measured per-import reading count) and nothing in this fix logs or alerts if it turns out to still be too large for a future household's data shape.
  evidence: Raised by adversarial review (Blind Hunter); the constant's own doc comment already says "revisit if a future incident shows it's still too large" but there's no mechanism to detect that other than a repeat production 500. Same open concern now also applies to `DeleteReadingVolumeThreshold = 20_000` (spec-3-10-cleanup-batch-delete-fix-2) — also unbenchmarked, also undetectable except by a repeat 500. [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:749-773]


## Deferred from: code review of spec-3-10-cleanup-batch-delete-fix-2 (2026-09-12)

- [accepted: no observed case] source_spec: `_bmad-artifacts/implementation/spec-3-10-cleanup-batch-delete-fix-2.md`
  summary: Reading counts driving `ChunkImportIdsByReadingVolume`'s packing decisions are measured once, entirely before `DeleteEligibleAsync`'s transaction opens. An import still `Processing`/`AwaitingPowerPointMapping` (both eligible per `DeleteJobsAsync`'s own deliberately-non-terminal-states design) could still be accumulating `SmartPlugReading` rows via `AddAsyncCore` between that measurement and the actual delete of its chunk — so a chunk could be sized against a stale, too-low count and still trigger the very cascade-volume problem this fix exists to bound.
  evidence: Raised independently by adversarial review (Blind Hunter) and edge-case review, round 2. Same class of gap as the pre-existing "eligibility computed once before the transaction" TOCTOU already accepted for round 1 (`DeleteJobsAsync`/`SweepExpiredAsync` at `SmartPlugImportRepository.cs:704-738`), but more consequential here since staleness affects a *safety* guarantee (chunk sizing) rather than just completeness (which rows get swept up). Not structurally fixed: a real fix (re-measuring per chunk, or snapshot isolation) is disproportionate to this incident's evidence — no observed case of an in-flight import actually growing large enough mid-cleanup to matter. [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:833-850]


## Deferred from: code review of spec-3-10-cleanup-async-job (2026-09-12)

- [accepted: AzureStorageQueueJobQueue.cs:75; AD-6 uniform; revisit if long cleanup] source_spec: `_bmad-artifacts/implementation/spec-3-10-cleanup-async-job.md`
  summary: A `CleanUpSmartPlugImportJobs` job that legitimately runs past `AzureStorageQueueJobQueue`'s 60-minute message visibility timeout gets redelivered and reprocessed concurrently with itself, duplicating deletes — the same redelivery/reprocessing design `BackgroundJobProcessor.ProcessAsync` already applies uniformly to every job type (deliberately, so a genuinely crashed worker's job still gets retried).
  evidence: Raised independently by adversarial review (Blind Hunter) and edge-case review. Pre-existing AD-6 characteristic of the shared processor/queue infrastructure, not introduced by this diff — `ProcessSmartPlugImport` has carried the identical exposure since Story 3.6. Not addressed here: this round's round-trip-guard fix (reusing an already-Queued/Processing cleanup job instead of enqueueing a second one, see `SmartPlugImportEndpoints.cs`'s DELETE handler) closes the *client-triggered* concurrency path; the redelivery path would need per-job-type visibility-timeout extension or renewal, a broader AD-6 change out of scope for an incident hotfix. This incident's actual data (487,380 rows across 51 imports) is not expected to approach a 60-minute runtime. **Updated round 4 (2026-09-12):** the per-import batched reading-detach fix (`spec-3-10-cleanup-per-import-detach.md`) adds roughly `readingCount / 200` sequential round trips per import (~1,200 for the confirmed 122,158-row import) — meaningfully more overhead than round 3's assumption accounted for, though still not expected to approach 60 minutes for any household's data seen so far. Worth re-checking if a future household's cleanup runs noticeably long. [src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs:75-120, src/EnergyTracker.Infrastructure/Adapters/AzureStorageQueueJobQueue.cs]

- [accepted: no user impact] source_spec: `_bmad-artifacts/implementation/spec-3-10-cleanup-async-job.md`
  summary: `DELETE /api/smart-plug-import-jobs` always enqueues (now: or reuses) a background job round-trip even when the household has zero eligible import jobs — a latency regression for what used to be an instant synchronous no-op.
  evidence: Raised by adversarial review (Blind Hunter). Correct behavior, not a bug; a fast-path (check eligibility count before enqueueing) would add complexity for a case with no reported user impact. Not required by the spec's Boundaries. [src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs]

- [accepted: only internal branching (GetBackgroundJobStatus.cs:26); revisit with 3rd job type] source_spec: `_bmad-artifacts/implementation/spec-3-10-cleanup-async-job.md`
  summary: `GET /api/jobs/{id}`'s response never exposes its own `JobType`, even though `BackgroundJobProcessor`/`GetBackgroundJobStatus` both branch internally on it — now two job types share this one polling endpoint instead of one.
  evidence: Raised by adversarial review (Blind Hunter). Harmless today (the frontend only ever polls a job it itself just enqueued, so it already knows what kind of job it's polling), but an increasingly implicit contract as more job types are added. Worth exposing if a third job type or a generic "in-progress jobs" view is ever built. [src/EnergyTracker.Application/GetBackgroundJobStatus.cs]

- [accepted: mitigated by server dedup] source_spec: `_bmad-artifacts/implementation/spec-3-10-cleanup-async-job.md`
  summary: An in-flight cleanup job's id lives only in a local variable inside `handleConfirmCleanup` — navigating away or reloading mid-poll loses all client-side trace of it, so the user could believe cleanup isn't running and re-trigger it.
  evidence: Raised by adversarial review (Blind Hunter) and edge-case review. Largely mitigated by this round's server-side dedup guard (a re-triggered DELETE reuses the still-active job instead of enqueueing a duplicate), so the practical risk is stale/confusing UI state, not duplicate work. Full resume-on-reload (persisting jobId client-side, e.g. localStorage, and resuming the poll on mount) is a UX enhancement out of scope for this hotfix.


## Deferred from: code review of spec-3-10-cleanup-per-import-detach (2026-09-12)

- [accepted: Sweep now has per-household lock; manual path still unlocked (see 317). Revisit on an observed deadlock] source_spec: `_bmad-artifacts/implementation/spec-3-10-cleanup-per-import-detach.md`
  summary: Concurrent `DeleteJobsAsync` (manual) and `SweepExpiredAsync` (automatic) executions targeting overlapping rows could now deadlock more easily than before — this round replaces one coarse FK cascade per import with many small sequential `UPDATE`s, each acquiring/releasing lock-manager attention in a different order than a concurrent execution's own loop, widening the interleaving window compared to one atomic operation.
  evidence: Raised by edge-case review. Same underlying class of risk as already exists (two independent calls to `DeleteEligibleAsync` could already target overlapping rows before this fix — nothing in this codebase serializes cleanup runs per household), not newly introduced by this diff — only its granularity/lock-interleaving profile changed. No confirmed occurrence. [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs]


## Deferred from: code review of story-5-1-tariff-configuration (2026-09-13)

- [accepted: Narrow, self-corrects] `GetTariffHistory`'s second "full-history" fetch reuses the first query's `totalCount` as the second query's page size — a Tariff entry created concurrently between the two queries can be silently excluded from that response's `IsCurrent`/`EffectiveUntil` computation. Narrow race window, no data loss, self-corrects on the next read; the code's own comment already documents the small-history-set assumption behind this shape, just not this race. Raised by edge-case review. [src/EnergyTracker.Application/GetTariffHistory.cs:46-48]


## Deferred from: code review of story-5-2-candidate-tariff-comparison-bonus-decay-normalized-savings (2026-09-14)

- [accepted: Inherited from Pattern Detective; not re-verified in code] `annualPaceKwh` extrapolation trusts `statusResult.PaceToDateKwh` without a non-negative guard — a negative pace (e.g. from an unusual resolved-rollover edge case) would flow unguarded into the annual cost math. Pre-existing behavior inherited from Pattern Detective's already-shipped `PatternDetectiveCalculator`/`GetCurrentStatus`, not introduced by this story. Raised by edge-case review. [src/EnergyTracker.Application/CompareTariff.cs:47]


## Deferred from: code review of story-5-3-two-way-attractiveness-signal (2026-09-15)

- [accepted: Same pattern as all of tariff-api.ts] `compareTariff`'s response is cast via a bare `JSON.parse(text) as TariffComparisonDto` with no runtime validation — deferred, pre-existing: identical unchecked-cast pattern used by every other function in this file (`createTariff`, `updateTariff`, `fetchTariffHistory`), not introduced by this story. Raised by edge-case review. [web/src/lib/tariff-api.ts:141]


## Deferred from: code review of story-5-4-tariff-check-reminder (2026-09-17)

- [accepted: Mirrors refreshStatus] `refreshTariffCheck`'s catch-all collapses network errors and "no Tariff configured" into the same `null` state, indistinguishable in the UI — deferred, pre-existing: mirrors `refreshStatus`'s identical existing pattern two functions above in the same file, not introduced by this story. Raised by adversarial review (Blind Hunter). [web/src/App.tsx:90-97]

- [accepted: No Produces anywhere in TariffEndpoints.cs; convention] The new `GET /api/tariff-check` endpoint carries no `.Produces<T>()`/OpenAPI metadata — deferred, pre-existing: matches the existing pattern for every other route in the same file. Raised by adversarial review (Blind Hunter). [src/EnergyTracker.Api/Endpoints/TariffEndpoints.cs]


## Deferred from: code review of story-1.12 (2026-09-17)

- [accepted: Same framework constant on both sides] No automated coverage of the real DI `.Get(OpenIdConnectDefaults.AuthenticationScheme)` resolution path — `SessionEndpointsTests.cs` calls `ResolveSupportsFederatedLogoutAsync` directly with a hand-built `OpenIdConnectOptions`, never exercising the actual `IOptionsMonitor<OpenIdConnectOptions>.Get(...)` scheme lookup performed in `SessionEndpoints.cs`'s handler [tests/EnergyTracker.Api.Tests/SessionEndpointsTests.cs, src/EnergyTracker.Api/Endpoints/SessionEndpoints.cs:22]. Deferred: low realistic risk (the OIDC scheme is registered via `AddOpenIdConnect`'s default-scheme overload in `Program.cs`, so both registration and lookup rely on the same framework constant rather than a duplicated string literal that could drift), and no existing test infrastructure in this repo mocks OIDC discovery through a real DI/`WebApplicationFactory` path yet. Raised by adversarial review (Blind Hunter).


## Deferred from: code review of spec-db-command-timeout-scope (2026-09-17)

- [accepted: Revisit if a migration times out on Basic tier] source_spec: `_bmad-artifacts/implementation/spec-db-command-timeout-scope.md`
  summary: `ConfigureDbContext` also drives EF Core migrations (and `HouseholdMembershipDbContext`, via the same `AddDbContextFactory<HouseholdMembershipDbContext>(ConfigureDbContext)` registration) — neither ever inherited the removed 120s app-wide `CommandTimeout` in a way this spec re-provisions, so a slow/DTU-throttled migration on a constrained tier (the same failure mode this spec's own incident narrative describes) would now fail at the 30s default instead of 120s, with no explicit `SetCommandTimeout` call anywhere in the migration path.
  evidence: Raised by edge-case review. Not confirmed as a live incident — migrations run once at deploy/startup, not on the hot request/job path this spec is scoped to (the frozen Boundaries' "Always" section enumerates only `ProcessSmartPlugImport` job call sites) — but the risk mechanism is the same DTU-throttling scenario that motivated the original global bump. Worth revisiting if a migration ever times out on Basic-tier Azure SQL. [src/EnergyTracker.Api/Program.cs:124-156]

- [accepted: Deliberate scope per spec] source_spec: `_bmad-artifacts/implementation/spec-db-command-timeout-scope.md`
  summary: `ProcessSmartPlugImport.ExecuteAsync`'s reads before `AddAsyncCore` is ever called (`ListPowerPointsAsync`, `FindRoomAsync`, `FindLatestReadingWatermarkByPowerPointAsync`) run on the same scoped `dbContext` this spec elevates to 120s, but execute *before* `AddAsyncCore`'s `SetCommandTimeout` call sets it — so they're exposed to the 30s default, unlike under the old global 120s setting.
  evidence: Raised by adversarial review (Blind Hunter). The frozen Boundaries' "Always" section explicitly enumerates only write call sites (`AddAsyncCore`, `PersistFailedImportAsync`, `CompleteSmartPlugImportProcessing`) as needing coverage, so this narrower scope appears to be a deliberate design choice rather than an oversight — but it wasn't explicitly reasoned through for these specific lightweight point-queries. Low risk (small/indexed lookups, not the bulk-insert the incident targeted), not fixed here since moving `SetCommandTimeout` any earlier than `ProcessSmartPlugImport.ExecuteAsync` itself (outside `SmartPlugImportRepository` entirely) would be a larger structural change than this spec's scope. Worth reconsidering if DTU throttling is ever observed against these specific reads. [src/EnergyTracker.Application/ProcessSmartPlugImport.cs:38,55,57]


## Deferred from: code review of spec-power-point-mapping-duplicate-timeout (2026-09-18)

- [accepted: Branch hard to reach] No test exercises `UpdateMappingSetBasedWithConflictToleranceAsync`'s `importReadings.Count == 0` early-return branch (the fallback triggered for an import with zero readings). Low realistic risk — `AnyMappingConflictAsync`'s own `hasAnyExistingForPowerPoint`/`intervalStarts` checks make this branch hard to reach with zero readings in the first place — but currently silent if it regresses. Raised by adversarial review (Blind Hunter). [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs]


## Deferred from: code review of spec-datetimeoffset-utc-normalization (2026-09-18)

- [accepted: Write paths range-limited] source_spec: `_bmad-artifacts/implementation/spec-datetimeoffset-utc-normalization.md`
  summary: `UtcDateTimeOffsetConverter`'s write side (`v.ToUniversalTime()`) can throw `ArgumentOutOfRangeException` for a `DateTimeOffset` near `DateTimeOffset.MinValue`/`MaxValue` combined with a non-zero offset, since shifting the offset can push the instant outside the representable range — trading Npgsql's own "offset 0 only" failure for a different unhandled exception on a narrower input.
  evidence: Raised by adversarial review (Blind Hunter), loop 1. Not reachable today: both current write paths that accept a client-supplied `DateTimeOffset` (`CreateEvent`, `CreateMeterReading`) already enforce a year-2000 floor and a `+5min` clock-skew ceiling, both far inside the representable range. Would matter for any future write path (e.g. an importer) that accepts an unranged timestamp. [src/EnergyTracker.Infrastructure/Converters/UtcDateTimeOffsetConverter.cs]

- [accepted: Revisit before any backfill] source_spec: `_bmad-artifacts/implementation/spec-datetimeoffset-utc-normalization.md`
  summary: EF Core's default `DateTimeOffset` value comparer compares only the UTC instant, ignoring offset (`DateTimeOffset.Equals(DateTimeOffset)` semantics) — so a future write that reassigns an existing non-zero-offset SQL Server row's property to an instant-equal value with a different offset (e.g. an attempted backfill/normalization of legacy data) would be seen as "no change" by EF's change tracking and silently skipped, never reaching the database.
  evidence: Raised by edge-case review, loop 1. Not reachable today: this spec explicitly does no backfill/rewrite of existing rows (its own I/O matrix: "Existing SQL Server rows with a stored non-zero offset ... no backfill, no rewrite"). Must be considered (e.g. via an explicit `SetValueComparer` keyed on both `UtcTicks` and `Offset`, or an `ExecuteUpdate`/raw-SQL write that bypasses change tracking) before any future story attempts to backfill or normalize existing non-zero-offset rows. [src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs]

- [accepted: ReloadAsync present in all 3 repos; no delete paths] source_spec: `_bmad-artifacts/implementation/spec-datetimeoffset-utc-normalization.md`
  summary: `EventRepository.AddAsync`/`MeterReadingRepository.AddAsync`/`TariffRepository.AddAsync`/`UpdateAsync`'s post-save `Entry(...).ReloadAsync(...)` (added to make the returned instance reflect the normalized offset) introduces a second DB round trip after the row is already committed — if that reload call itself throws (cancellation, a transient connection blip) the caller sees a failed create/update even though the row persisted; and if the row were ever deleted by a concurrent operation between the save and the reload, the reload would leave the original, non-normalized in-memory value in place with no error.
  evidence: Raised by edge-case review, loop 2. Not reachable today for the delete scenario: `grep` confirms no code path deletes an `Event`, `MeterReading`, or `Tariff` row anywhere in this codebase (all are append-only/edit-only). The reload-throws scenario is an instance of the same generic two-sequential-DB-call risk `SaveChangesAsync` itself already carries everywhere in this codebase (nothing here specially guards against a network blip between an operation completing server-side and its response reaching the caller) — not a materially new risk, but worth reconsidering if a delete path for any of these three entities is ever added. [src/EnergyTracker.Infrastructure/Adapters/EventRepository.cs, src/EnergyTracker.Infrastructure/Adapters/MeterReadingRepository.cs, src/EnergyTracker.Infrastructure/Adapters/TariffRepository.cs]


## Deferred from: code review of story-6.2 (2026-09-19)

- [accepted: Mirrors MeterReadingEndpoints] source_story: `_bmad-artifacts/implementation/6-2-event-history-view.md`
  summary: Non-numeric `page`/`pageSize` query values (e.g. `?page=abc`) fail ASP.NET's minimal-API model binding before `GetEventHistory`'s own guards run, returning a generic ProblemDetails with no `errorCode` extension — the frontend's `messageForEventError` then falls back to a generic message.
  evidence: Raised by edge-case review and adversarial review (Blind Hunter) independently. Pre-existing pattern, identical in `MeterReadingEndpoints` (this story's own "match precisely" instruction for Task 4.3 established the idiom being mirrored), not introduced by this diff. [src/EnergyTracker.Api/Endpoints/EventEndpoints.cs:61]

- [accepted: Mirrors precedent] source_story: `_bmad-artifacts/implementation/6-2-event-history-view.md`
  summary: `EventRepository.GetPageForHouseholdAsync`'s `CountAsync` and `Skip`/`Take` are two separate round trips with no transaction/snapshot isolation — a concurrent insert between them can make `TotalCount` and the returned page briefly inconsistent.
  evidence: Raised by edge-case review. Standard pattern, mirrors the existing `GetMeterReadingHistory` precedent this story was told to follow. [src/EnergyTracker.Infrastructure/Adapters/EventRepository.cs:30]

- [accepted: Unreachable until Event delete exists] source_story: `_bmad-artifacts/implementation/6-2-event-history-view.md`
  summary: `EventsCard`'s `page` state has no guard against landing out-of-range if `totalCount` shrinks below the current page (e.g. once an Event-delete feature ships, or another tab mutates data while this card sits open on page 2+).
  evidence: Raised by adversarial review (Blind Hunter). Not reachable today — no delete affordance exists for Events yet. Worth a guard (clamp `page` or re-fetch page 1) whenever a delete/edit path is added. [web/src/components/event/events-card.tsx:18]

- [accepted: Not a code defect] source_story: `_bmad-artifacts/implementation/6-2-event-history-view.md`
  summary: Story 6.1's status flip to `done` (this story's own Task 6) is bundled into the same diff/PR as 6.2's new feature code, coupling the two stories' lifecycle state.
  evidence: Raised by adversarial review (Blind Hunter). Process/documentation observation, not a code defect — if 6.2 needed a substantial revert post-review, 6.1 would revert alongside it even though none of 6.1's already-shipped code changed in this diff.


## Deferred from: code review of story-6.3 (2026-09-21)

- [accepted: Duplicate of 123/76/31 class; households never deleted] source_story: `_bmad-artifacts/implementation/6-3-wattage-plausibility-correlation.md`
  summary: `GET`/`PUT /households/{id}/ai-plausibility` call `dbContext.Households.SingleAsync(h => h.Id == id, ...)` with no not-found guard — if the Household row were deleted between authorization and this query, this throws `InvalidOperationException` (unhandled 500) instead of a clean 404.
  evidence: Pre-existing pattern copied verbatim from the existing GET endpoint (`HouseholdEndpoints.cs:97`), not introduced by this diff, and unreachable today since Households are never deleted in this app. Raised by Edge Case Hunter during code review. [src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs:145]

- [accepted: Opt-in; revisit in Epic 9.3] source_story: `_bmad-artifacts/implementation/6-3-wattage-plausibility-correlation.md`
  summary: AI classification round-trips (`OpenAiCompatibleClient`'s 10s timeout) add contention to the single-worker background job queue (AD-6) — a user backfilling several historical Events in a row could serialize multiple 10-second AI round trips ahead of unrelated queued jobs (e.g. Smart Plug import), with no rate limiting or priority.
  evidence: Pre-existing AD-6 single-worker limitation this feature exercises more than prior features did; feature is opt-in and defaults off (`Household.AiPlausibilityEnabled` defaults `false`). Raised by adversarial review (Blind Hunter) during code review. [src/EnergyTracker.Infrastructure/Adapters/OpenAiCompatibleClient.cs:20-24]

- [accepted: Ralf decision 2026-09-21] source_story: `_bmad-artifacts/implementation/6-3-wattage-plausibility-correlation.md`
  summary: AC #5 states the choice between a local model and a cloud/external API is "a Household-level setting." The implementation makes only the on/off toggle (`Household.AiPlausibilityEnabled`) Household-scoped; which backend is used is one deployment-wide config value (`AiPlausibility:BaseUrl`/`ApiKey`/`BackendLabel`) read once at the composition root, shared by every Household in the deployment.
  evidence: Deferred with reason (Ralf, code review 2026-09-21): accepted trade-off vs. AD-8 — deployment-wide backend selection is the correct read of AD-8's "one config value at composition root" rule; AC #5's wording will be corrected to match the code, not the other way around. Raised by the Acceptance Auditor during code review. [src/EnergyTracker.Api/Program.cs:346-363, src/EnergyTracker.Domain/Household.cs]


## Deferred from: code review of 7-2-full-data-import-restore-migration (2026-09-22, Pass 2/frontend+docs)

- [accepted: mirrors use-smart-plug-import-job convention] No overall poll timeout / indefinite polling on a repeated 404 in `useHouseholdImportJobPoll` [web/src/components/data-import/use-household-import-job.ts:22] — faithfully mirrors `use-smart-plug-import-job.ts`'s own established, unchanged-by-this-diff convention; not a regression introduced by this story. Raised by adversarial + edge-case review.

- [accepted: not in any AC; no size check in data-import-panel.tsx] No client-side file-size pre-check or size hint before a long upload that the server may reject at 250MB [web/src/components/data-import/data-import-panel.tsx:130] — minor UX nicety, not required by any AC. Raised by adversarial review (Blind Hunter).


## Deferred from: code review of 7-2-full-data-import-restore-migration (2026-09-22, Pass 1/backend)

- [accepted: HouseholdRestoreWriter.cs:160; unreachable (no household delete) — see SingleAsync dup group] `HouseholdRestoreWriter.UpdateHouseholdSettingsAsync`'s `SingleAsync` throws an unhandled, non-diagnostic exception if the Household row doesn't exist [src/EnergyTracker.Infrastructure/Adapters/HouseholdRestoreWriter.cs:160] — currently unreachable: no household-deletion capability exists anywhere in this codebase; revisit if one is ever added. Raised by adversarial + edge-case review.

- [accepted: deliberate atomicity (:25); monitor for large households] Single outer transaction spanning the full delete+insert doesn't bound transaction duration or lock-hold time for a household with a large history [src/EnergyTracker.Infrastructure/Adapters/HouseholdRestoreWriter.cs:25] — deliberate, disclosed trade-off (Dev Notes) required to guarantee "never partially applied" atomicity; worth monitoring on very large households (same incident shape as the Epic 6 retro's bulk-write lessons this story cites), not actionable without redesigning the atomicity guarantee itself. Raised by adversarial review (Blind Hunter).

- [accepted: no rate-limit layer in repo (grep errored; verified by absence only)] No rate limiting / concurrency bound on `/household-import`'s synchronous upload+validate path [src/EnergyTracker.Api/Endpoints/HouseholdImportEndpoints.cs:38] — no rate-limiting layer exists anywhere in this codebase; not introduced by this story beyond adding one more large-body endpoint. Raised by adversarial review (Blind Hunter).

- [accepted: RestoreHouseholdData.cs:29; rendered via React state (auto-escaped) in data-import-panel.tsx:41] `OriginalFileName` (user-supplied, unsanitized) flows into `HouseholdImportValidationException`'s message and then `BackgroundJob.ErrorMessage` [src/EnergyTracker.Application/RestoreHouseholdData.cs:29] — needs Pass 2 (frontend) context to resolve whether `ErrorMessage` rendering is safe (React JSX auto-escapes by default, but not yet confirmed against `data-import-panel.tsx`). Raised by adversarial review (Blind Hunter).


## Deferred from: code review of story-7.1 (2026-09-22)

- [accepted: Duplicate of line 11 (paged export); accepted trade-off] source_story: `_bmad-artifacts/implementation/7-1-full-data-export.md`
  summary: No transactional/snapshot consistency across `HouseholdExportReader.GetExportDataAsync`'s ~12 sequential queries — a concurrent write between, say, the `Rooms` query and the `Events` query can produce an internally inconsistent export document.
  evidence: Pre-existing pattern: no other multi-query read use case in this codebase (e.g. `GetCurrentStatus`) wraps its reads in a transaction/snapshot either. Raised by adversarial review (Blind Hunter) during code review. [src/EnergyTracker.Infrastructure/Adapters/HouseholdExportReader.cs]

- [accepted: Dup of 405] source_story: `_bmad-artifacts/implementation/7-1-full-data-export.md`
  summary: `Households.SingleAsync`/`MainMeters.SingleOrDefaultAsync` have no guard against a data-integrity multi-row violation — surfaces as an unhandled `InvalidOperationException` (500) instead of a diagnosable error.
  evidence: Identical to the existing codebase-wide convention — `HouseholdRepository.cs:99,126` and `MeterReadingRepository.cs:15,41,104` use the same unguarded `SingleAsync`/`SingleOrDefaultAsync` pattern on the same DbSets — not introduced by this diff. Raised by adversarial review (Blind Hunter) and Edge Case Hunter independently during code review. [src/EnergyTracker.Infrastructure/Adapters/HouseholdExportReader.cs:17-26]

- [accepted: Nit] source_story: `_bmad-artifacts/implementation/7-1-full-data-export.md`
  summary: Completion Notes claim "no new architecture-test assertion was needed... existing tests already enforce structurally" the `Application/Ports`/`Infrastructure/Adapters` placement convention for the new port/adapter — no `EnergyTracker.Architecture.Tests` file actually does; the four existing ones cover unrelated concerns (Domain isolation, frontend auth tokens, Pattern Detective data isolation, Eve Home parser convention).
  evidence: Documentation-accuracy nit only, not a functional gap — Task 2's phrasing was conditional ("if... needs a new architecture-test assertion... add it"), so skipping this isn't a requirement violation. Raised by the Acceptance Auditor during code review.


## Deferred from: code review of spec-household-export-oom-fix (2026-09-25)

- [accepted: human-confirmed trade-off 2026-09-25; HouseholdExportReader.cs PageSize=500] source_spec: `_bmad-artifacts/implementation/spec-household-export-oom-fix.md`
  summary: The paged rewrite reads each collection across independent, non-transactional keyset pages (one query per 500 rows) rather than the old code's single query per collection, so a concurrent write to that household mid-export (e.g. an in-flight Smart Plug import writing SmartPlugReadings) could leave that one collection internally inconsistent across its own page boundary.
  evidence: Raised by adversarial review (Blind Hunter); human-confirmed 2026-09-25 as an accepted trade-off rather than a merge blocker (see Design Notes) — wrapping the read in a snapshot-isolated transaction would hold a longer-lived read against the exact write-heavy table this fix exists to relieve pressure on. Worth a follow-up (e.g. snapshot-isolated read, or a documented "export reflects a best-effort, not point-in-time-consistent, snapshot" note in docs/data-export-format.md) if this is ever reported as a real-world discrepancy.

- [accepted: matches existing guard-test convention] source_spec: `_bmad-artifacts/implementation/spec-household-export-oom-fix.md`
  summary: `HouseholdExportReaderDoesNotBypassTenantIsolationTests` (the new AD-3 guard test) is a source-text substring scan for four forbidden identifiers; it can't catch a semantically-equivalent bypass that avoids those literal tokens, and it never positively asserts that the correct `HouseholdId` filter clause is actually present — a future paginated collection reader that simply omits the household filter (without using any of the four banned APIs) would pass it silently.
  evidence: Raised by adversarial review (Blind Hunter). Matches this codebase's pre-existing guard-test convention exactly (e.g. `PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests`), so not unique to this diff — a broader rethink of the guard-test style (positive-assertion vs. forbidden-token scan) is a separate, codebase-wide investment.


## Deferred from: code review of story-8.2 (2026-09-26)

- [accepted: Sweep ran de-DE; no overflow found there] source_story: `_bmad-artifacts/implementation/8-2-dashboard-desktop-tablet-layout.md`
  summary: `shrink-0` retained unconditionally on the header-icon buttons now that they become auto-width pills at ≥660px — no wrap/truncation fallback if header content grows (a longer future locale string, an added icon).
  evidence: Pre-existing shape convention extended rather than introduced fresh; currently unreached since both labels are short in both locales. Raised by adversarial review (Blind Hunter) during code review. [web/src/components/dashboard/dashboard-page.tsx:143,158]


## Deferred from: code review of story-8.3 (2026-09-26)

- [accepted: Ralf decision in Epic 8 retro] source_story: `_bmad-artifacts/implementation/8-3-trend-history-desktop-tablet-layout.md`
  summary: No visual-regression/screenshot testing exists anywhere in this repo to actually verify the claimed visual outcomes (Meter Readings dead-space elimination, quiet-vs-glass tier flattening) — automated coverage only checks class names and bounding-box numbers, never a rendered comparison.
  evidence: Repo-wide tooling gap, not something one story should introduce alone. Raised by adversarial review (Blind Hunter) during code review. [web/src/components/trend-history/per-plug-data-card.tsx, web/src/components/meter-reading/meter-readings-card.tsx]


## Deferred from: code review of 8-5-settings-desktop-tablet-layout (2026-09-28)

- [accepted: cosmetic; plural i18n `_other` still renders 0; accepted in Task 6] Room/Power Point item-count summary renders "— 0 Power Points"/"— 0 Devices" with no zero-guard for an empty parent [web/src/components/tagging-scaffold/tagging-scaffold-manager.tsx:580-582,656-658] — deferred, cosmetic copy preference, not a functional defect; this exact case was already observed and accepted during Task 6's live verification ("HiFi — 0 Devices"). Raised by adversarial review (Blind Hunter).

- [accepted: narrow reachability; low consequence] `InviteMemberRow`'s `Dialog` doesn't auto-close when the viewport crosses back below 660px while open, and each width variant mounts its own independent `InviteGeneratePanel` state [web/src/components/household-invite/invite-member-row.tsx] — deferred, narrow reachability (most common tablet rotations don't cross 660px either direction) and low consequence (user just regenerates the link). Raised by the Edge Case Hunter.

- [accepted: codebase-wide NavChrome convention] Dual-render narrow/wide test assertions across this story's new/extended tests are disambiguated only by DOM-order indexing (`getAllByRole(...)[0]`/`.last()`) [web/src/components/settings/settings-page.test.tsx, web/src/components/household-invite/invite-member-row.test.tsx, web/src/components/tagging-scaffold/tagging-scaffold-manager.test.tsx, web/e2e/app-shell.spec.ts] — deferred, established codebase-wide convention (the `NavChrome` dual-render precedent) predating this diff; a project-wide fix is out of scope for a single story. Raised by adversarial review (Blind Hunter).


## Deferred from: code review of story-8.4 (2026-09-28)

- [accepted: Inherited] source_story: `_bmad-artifacts/implementation/8-4-tariff-radar-desktop-tablet-layout.md`
  summary: The new Tariff Radar e2e case is one 150-line `test()` covering five unrelated concerns (narrow stacking, exact-boundary check, wide pairing, column width, post-submit card-tier/backdropFilter) with no checkpoints — an early failure prevents every later assertion, including the fully independent AC #3 card-tier check, from running in that CI pass.
  evidence: Pre-existing structure inherited verbatim from the Dashboard/Trend History cases (Stories 8.2/8.3) this one mirrors, not introduced fresh here. Raised by adversarial review (Blind Hunter) during code review. [web/e2e/app-shell.spec.ts]


## Deferred from: code review of story-8-7-meter-readings-events-tariff-history-entry-grid (2026-09-29)

- [accepted: Unreachable: no delete path for these entities] source_story: `_bmad-artifacts/implementation/8-7-meter-readings-events-tariff-history-entry-grid.md`
  summary: When `totalCount > 0` but `items` is empty (current page beyond the last after a deletion), Meter Readings, Events and Tariff History render an empty table/grid with no message.
  evidence: Pre-existing in the table branch; the new grid branch copies the same guard. Raised by Edge Case Hunter during code review. [web/src/components/meter-reading/meter-readings-card.tsx, web/src/components/event/events-card.tsx, web/src/components/tariff/tariff-history-list.tsx]

- [accepted: Server-supplied ISO] source_story: `_bmad-artifacts/implementation/8-7-meter-readings-events-tariff-history-entry-grid.md`
  summary: `Intl.DateTimeFormat.format(new Date(item.occurredAt))` throws `RangeError` on an unparseable `occurredAt`, which would blank the whole Events card.
  evidence: Pre-existing in the table row; the tile branch repeats it. Raised by Edge Case Hunter during code review. [web/src/components/event/events-card.tsx]


## Deferred from: locale × theme sweep (spec-locale-theme-sweep, 2026-10-02)

- [accepted: No false results in 48 cells] source_spec: `_bmad-artifacts/implementation/spec-locale-theme-sweep.md`
  summary: Sweep audit depth gaps: ancestor opacity, backdrop-filter and pseudo-element backgrounds are not modelled in the contrast backdrop; placeholder/input-value text, WCAG 1.4.11 non-text (UI component) contrast and vertical clipping are not audited; per-cell layout identity covers only the nav chrome (not the Settings Preferences card); locale number/date formatting is not asserted beyond `<html lang>`; translucent layers resolve via a 1×1 canvas with premultiplied-alpha precision loss.
  evidence: Raised by the quick-dev review of the sweep; none produced a false result in the 48-cell run, and elements over gradients/images are skipped and counted in the `contrast-skipped` annotation. [web/e2e/locale-theme-sweep.spec.ts]


## Deferred from: keyboard tab-order check (spec-tab-order-check, 2026-10-02)

- [accepted: No known defect] source_spec: `_bmad-artifacts/implementation/spec-tab-order-check.md`
  summary: Tab-order coverage gaps: the tagging-scaffold wide grid (expanded `col-span-full` tiles), Dashboard reordering, Events tiles (no focusable controls), Settings at 659px, the Profile menu below 900px and other close paths (outside click), and non-Chromium browsers.
  evidence: Review-found; none has a known defect (tagging grid sets no `dense` flow, Dashboard has no `wide:order-*`). The current spec covers the surfaces named in Epic 8 retro action #2. [web/e2e/tab-order.spec.ts]
