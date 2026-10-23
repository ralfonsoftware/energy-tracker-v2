# Deferred work

Triaged 2026-10-02 against baseline `8c6a90e` (Epic 8 retro action #6, `spec-deferred-work-triage.md`). This file holds **open** and **promoted** items only. Resolved entries were removed; accepted trade-offs moved to `deferred-work-accepted.md`.

Conventions: append new sections at the end, chronological (see `project-context.md`). Mark each entry `[open]`, `[resolved: <story>]` or `[accepted: <reason>]`; review open items at each epic retro.

## Index

**test-coverage (18)**

- Export stream property names vs HouseholdExportResult, no round-trip test — code review of spec-household-export-oom-fix (2026-09-25)
- No test distinguishes recompute inside/outside txn — code review of story-4-3-correcting-a-meter-reading (2026-09-10)
- No middleware-ordering regression test — code review of 1-7-oidc-redirect-uri-scheme-correctness-behind-container-apps-ingress (2026-08-14)
- Uneven length-validation tests — code review of 1-9-room-power-point-device-management (2026-08-14)
- IDOR test claim inaccurate — code review of story-2.2 (2026-08-15)
- No double-dispose test for lock — code review of spec-status-recompute-serialization-perf, round 4 (2026-08-24)
- act() warnings in full suite — code review of story-3-5-dual-entry-points-multi-file-import-queuing (2026-08-27)
- TZ not pinned in Vitest — code review of spec-trend-chart-time-axis (2026-08-30)
- Logout ACs only manually verified — code review of story-1.12 (2026-09-17)
- No chained-call test for timeout carry-over — code review of spec-db-command-timeout-scope (2026-09-17)
- Mixed-batch test only; one entry path tested — code review of spec-power-point-mapping-duplicate-timeout (2026-09-18)
- AC3 archive coverage only Room — code review of story-6.2 (2026-09-19)
- e2e locators hardcode English — code review of story-8.2 (2026-09-26)
- No test for CTA adjacency — code review of story-8.2 (2026-09-26)
- e2e does not assert centering — code review of story-8.4 (2026-09-28)
- No unit assertion of max-w class on Settings/Tariff — code review of story-8-6-wide-column-increase-across-all-surfaces (2026-09-29)
- Spinner motion and inline theme script drift — code review of 8-10-theme-toggle-profile-menu (2026-09-30)
- No guard forcing new order classes into spec — keyboard tab-order check (spec-tab-order-check, 2026-10-02)

**a11y (7)**

- No aria-live on import/export error states — code review of 7-2-full-data-import-restore-migration (2026-09-22, Pass 2/frontend+docs)
- No focus mgmt across logoff dialog steps — code review of story-1.12 (2026-09-17)
- No check shortLabel substring of entryPointLabel — code review of story-8.3 (2026-09-26)
- Trailing action TableHead unlabeled — code review of story-8.3 (2026-09-26)
- role=menu contains radiogroups — code review of 8-10-theme-toggle-profile-menu (2026-09-30)
- Live region dropped on 660px cross — code review of 8-12-settings-preferences-card-mobile (2026-09-30)
- Icon buttons 40px not 44px — locale × theme sweep (spec-locale-theme-sweep, 2026-10-02)

**infra-hardening (7)**

- GH Actions pinned to tags, not SHAs — code review of 1-2-azure-infrastructure-as-code-resource-deployment-pipeline (2026-08-12)
- Actions pinned by mutable tags — code review of 1-4-pull-request-review-workflow (2026-08-13)
- Orphaned gh-actions-migrate-* firewall rules — code review of spec-azure-sql-ci-migration-firewall (2026-08-13)
- No timeout-minutes in infra-deploy — code review of 1-6-cicd-deploy-idempotency-container-app-image-preservation (2026-08-14)
- OTel resource attrs minimal — code review of spec-otel-api-instrumentation (2026-08-15)
- No cert renewal failure alerting — code review of spec-custom-domain-managed-cert (2026-08-16)
- RecomputeLock depends on maxReplicas=1, unguarded — code review of spec-status-recompute-serialization-perf, round 4 (2026-08-24)

**validation (6)**

- InviteGeneratePanel no abort-on-unmount — code review of 8-5-settings-desktop-tablet-layout (2026-09-28)
- No KwhValue scale validation — code review of story-2.2 (2026-08-15)
- Threshold/gap-days no range validation — code review of story-2.4 (2026-08-17)
- No lower bound on estimated kWh — code review of story-3.3 (2026-08-20)
- DST gap/ambiguous local time rewritten silently — code review of story-6.1 (2026-09-18)
- Device clock >5min ahead rejects default timestamp — code review of story-6.1 (2026-09-18)

**ux-polish (6)**

- No retry after tagging-scaffold load failure — code review of 1-9-room-power-point-device-management (2026-08-14)
- Can't cancel queue item while uploading/processing — code review of story-3-5-dual-entry-points-multi-file-import-queuing (2026-08-27)
- No cap on files per drop — code review of story-3-5-dual-entry-points-multi-file-import-queuing (2026-08-27)
- Shared DialogContent lacks max-h — code review of spec-power-point-mapping-list-scroll (2026-09-02)
- No progress indication during cleanup — code review of spec-3-10-cleanup-async-job (2026-09-12)
- Tariff history scrolls 10px at 659 de-DE — locale × theme sweep (spec-locale-theme-sweep, 2026-10-02)

**perf (5)**

- Old HouseholdId index redundant on SmartPlugReadings — code review of spec-household-export-oom-fix (2026-09-25)
- 3 sequential round-trips, contract unstated — code review of spec-status-recompute-serialization-perf, round 4 (2026-08-24)
- Unbounded StatusSnapshot read — story-4-1-trend-history-view (2026-08-29)
- Cumulative row locks may escalate to table lock — code review of spec-3-10-cleanup-per-import-detach (2026-09-12)
- Eager fetch in collapsed disclosure cards — code review of story-6.2 (2026-09-19)

**concurrency (3)**

- Unsequenced concurrent refreshStatus — code review of story-2-5-dashboard-status-display (2026-08-17)
- SQL Server sweep SELECT can block on writer locks — code review of spec-3-10-cleanup-sweep-async (2026-09-17)
- DeleteEligibleAsync (manual) takes no lock — code review of spec-3-10-cleanup-sweep-async (2026-09-17)

**docs (2)**

- Epic 2 Architecture header missing AD-15/10/3 — code review of spec-epic-2-header-ux-dr11 (2026-08-22)
- Story 2.3 glass cites no UX-DR11 — code review of spec-epic-2-header-ux-dr11 (2026-08-22)

**tech-debt (2)**

- TryGetHouseholdId copy-pasted — code review of story-7.1 (2026-09-22)
- Value-based, px-only; duplication remains — Breakpoint/column drift guard (spec-breakpoint-drift-test, Epic 8 retro action #3)

**i18n (1)**

- No de-DE test for toggle strings — code review of 1-10-structure-editor-archived-item-visibility-toggle (2026-08-23)


## Deferred from: code review of 1-2-azure-infrastructure-as-code-resource-deployment-pipeline (2026-08-12)

- [open] GitHub Actions pinned to floating version tags, not commit SHAs (`azure/login@v2`, `actions/checkout@v4`) [.github/workflows/infra-deploy.yml:22,28] — supply-chain hardening opportunity for a workflow with `id-token: write`; not required by any AC.


## Deferred from: code review of 1-4-pull-request-review-workflow (2026-08-13)

- [open] All actions in `pr-review.yml` pinned by mutable major-version tags (`@v7`, `@v6`, `@v3`) rather than SHA [.github/workflows/pr-review.yml] — pre-existing convention from Story 1.2/1.3, propagated rather than introduced by this diff.


## Deferred from: code review of spec-azure-sql-ci-migration-firewall (2026-08-13)

- [open] source_spec: `_bmad-artifacts/implementation/spec-azure-sql-ci-migration-firewall.md`
  summary: No periodic sweep prunes orphaned `gh-actions-migrate-*` SQL firewall rules left behind by a killed/force-cancelled runner (the in-job `if: always()`+`continue-on-error` cleanup only covers normal step failure, not hard cancellation).
  evidence: Azure SQL server-level firewall rules have a hard cap (128); enough abandoned runs could eventually exhaust it and start blocking legitimate deploys. Building a reaper is a distinct, separately-scoped task, not part of this hotfix.


## Deferred from: code review of 1-6-cicd-deploy-idempotency-container-app-image-preservation (2026-08-14)

- [open] No `timeout-minutes` set on `infra-deploy.yml`'s job or any of its steps, including the new "Resolve current Container App image" step [.github/workflows/infra-deploy.yml] — pre-existing gap across the whole workflow file (only `app-deploy.yml`'s SQL-related steps set per-step timeouts); not unique to this diff.


## Deferred from: code review of 1-7-oidc-redirect-uri-scheme-correctness-behind-container-apps-ingress (2026-08-14)

- [open] Middleware pipeline ordering (`UseForwardedHeaders` must precede anything reading `Request.Scheme`, notably `UseAuthentication`) has no dedicated regression test [src/EnergyTracker.Api/Program.cs:183-190] — pre-existing pipeline structure, unchanged by this diff; a future reorder of `Program.cs`'s middleware chain could silently reintroduce this exact story's bug with nothing to catch it.


## Deferred from: code review of 1-9-room-power-point-device-management (2026-08-14)

- [open] No retry action in the tagging-scaffold UI after the initial load fails [web/src/components/tagging-scaffold/tagging-scaffold-manager.tsx] — nice-to-have, not blocking.

- [open] Uneven length-validation test coverage: only `CreateRoomTests` asserts the >200-char rejection; `CreatePowerPointTests`/`CreateDeviceTests`/all three `Rename*Tests` don't, despite sharing `TaggingScaffoldNameValidator` [tests/EnergyTracker.Application.Tests/] — shared-validator logic makes an actual regression unlikely.


## Deferred from: code review of spec-otel-api-instrumentation (2026-08-15)

- [open] source_spec: `_bmad-artifacts/implementation/spec-otel-api-instrumentation.md`
  summary: OTel resource attributes are minimal — `ConfigureResource(r => r.AddService("EnergyTracker.Api"))` with no `serviceVersion`, `deployment.environment`, or instance identifier, duplicated across both exporter branches with no shared factory.
  evidence: Once local, self-host, and multiple Azure environments all land in a shared backend under one bare service name, telling their telemetry apart becomes guesswork. Real future value, but out of scope for a spec whose job was getting OTel wired up at all — richer resource tagging is a natural, separately-scoped follow-up once there's more than one environment's telemetry to actually distinguish.


## Deferred from: code review of story-2.2 (2026-08-15)

- [open] No server-side rounding/scale validation on `KwhValue` — a value with more than 2 decimal places is accepted, and the response echoes the un-rounded value before the `decimal(18,2)` column silently truncates it on write, so the confirmation text can diverge from what was actually stored [src/EnergyTracker.Application/CreateMeterReading.cs:22] — mitigated in the normal UI flow by the kWh field's `step="0.01"` browser constraint validation; only reachable via direct API use.

- [open] Dev Agent Record claims a cross-Household IDOR test exists for the meter-reading endpoint; the actual test only covers a principal with no Household at all, not a genuine cross-Household access attempt [tests/EnergyTracker.Api.Tests/MeterReadingEndpointsTests.cs:124] — documentation/test-accuracy gap only, not a functional security gap; AD-3's query-filter pattern is already covered elsewhere (Room/PowerPoint/Device).


## Deferred from: code review of spec-custom-domain-managed-cert (2026-08-16)

- [open] source_spec: `_bmad-artifacts/implementation/spec-custom-domain-managed-cert.md`
  summary: No renewal-failure alerting exists for the custom-domain managed certificate — an indirect CNAME or a missing CAA record silently blocks issuance/renewal (per D5), and nothing surfaces that until TLS actually starts failing in production.
  evidence: This story only scaffolds the dormant Bicep constructs (customDomainName defaults to ''); the feature has no live consumer yet, so there's nothing to alert on today. The existing `monitorAlert`/`otelAlertNotificationEmail` pattern in `infra/main.bicep` is the natural template to extend once a real custom domain is actually bound and worth monitoring.


## Deferred from: code review of story-2-5-dashboard-status-display (2026-08-17)

- [open] Concurrent `refreshStatus()` calls (e.g. offline-sync flush racing the mount-effect fetch) aren't sequenced — a slower, earlier-triggered response can resolve after a newer one and silently overwrite Status with stale data [web/src/App.tsx:49-58,148-160] — identical unsequenced-fetch pattern already exists in `refreshOpenRegressionPrompt`, not introduced by this diff; revisit both together if this class of bug is ever prioritized.


## Deferred from: code review of story-2.4 (2026-08-17)

- [open] `TrendingThresholdKwh`/`LowConfidenceGapDays` have no range/bound validation, unlike the bound-checking discipline Story 2.3's own review called for [src/EnergyTracker.Domain/Household.cs:23,29] — currently unreachable since no endpoint in this diff writes to these columns; revisit when FR-21's settings-editing UI ships.


## Deferred from: code review of story-3.3 (2026-08-20)

- [open] No lower-bound guard on `SmartPlugGapDetector`'s computed `EstimatedTotalKwh` — a negative `precedingDaysWithData.Average()` would be silently persisted and displayed as an "estimate" [src/EnergyTracker.Domain/Calculations/SmartPlugGapDetector.cs:123] — pre-existing: neither `MerossCsvParser` nor `EveHomeXlsxParser` (Story 3.1) validates against negative `KwhValue` on the way in, so this is a symptom of an existing parser-level gap, not something newly introduced by this diff's averaging logic.


## Deferred from: code review of spec-epic-2-header-ux-dr11 (2026-08-22)

- [open] source_spec: `_bmad-artifacts/implementation/spec-epic-2-header-ux-dr11.md`
  summary: Epic 2's `**Architecture:**` rollup header (line 7: `AD-4, AD-7, AD-12, AD-14, AD-16`) is missing AD-15 (cited in the body at lines 20, 28), AD-10 (line 244), and AD-3 (line 252) — the same header/body drift class this diff fixed for `UX-DRs:`, left unfixed on the neighboring line.
  evidence: Same defect, same file, same review pass; not fixed here to keep this change a single-line, single-concern edit matching its spec trace. Worth a follow-up pass across all epic files' rollup headers rather than a second one-line patch.

- [open] source_spec: `_bmad-artifacts/implementation/spec-epic-2-header-ux-dr11.md`
  summary: Story 2.3's regression-prompt AC (epic-2 file, line 116) describes "the neutral/informational glass treatment" but cites only `(UX-DR4, UX-DR18)`, not UX-DR11 — unclear whether this is a second missing citation for the same glass-elevation system or a deliberate exclusion.
  evidence: Flagged by adversarial review of this diff; requires a judgment call on whether Story 2.3's glass treatment is actually governed by UX-DR11 (out of scope to decide as part of a trivial header-consistency fix) — a UX-designer or spec-owner call, not a mechanical correction.


## Deferred from: code review of 1-10-structure-editor-archived-item-visibility-toggle (2026-08-23)

- [open] No test exercises the German (`de-DE`) toggle strings specifically — every assertion in `tagging-scaffold-manager.test.tsx` hardcodes the English string, and this diff's new `hideArchivedToggle`/`showArchivedToggle` keys inherit that gap [web/src/components/tagging-scaffold/tagging-scaffold-manager.test.tsx] — pre-existing whole-file test convention (no test in this file has ever asserted against `de-DE` strings), not something introduced by this diff specifically; a mismatched or garbled German translation would ship undetected regardless of which story adds it.


## Deferred from: code review of spec-status-recompute-serialization-perf, round 4 (2026-08-24)

- [open] source_spec: `_bmad-artifacts/implementation/spec-status-recompute-serialization-perf.md`
  summary: `GetRecentByMainMeterAsync` issues three sequential, non-transactional DB round-trips (latest-timestamp, must-include-timestamp, final range query) rather than one query — a missed optimization given the story's own performance motivation, and its true contract ("windowDays trailing whatever was latest at the time of the *first* sub-query, plus anything inserted later, since the final query has no upper bound") isn't stated anywhere as a load-bearing property.
  evidence: Benign today only because the final query has no upper timestamp bound; a future edit adding one could reintroduce a subtle staleness bug between the three round-trips with nothing to catch it. Collapsing to a single correlated-subquery (as round 2's query shape did, before round 3's rewrite) would close both the round-trip count and the documentation gap simultaneously, but round 4 prioritized closing the correctness bug over re-optimizing the query shape. Raised by adversarial review, round 4.

- [open] source_spec: `_bmad-artifacts/implementation/spec-status-recompute-serialization-perf.md`
  summary: `HouseholdRecomputeLock`'s in-process locking correctness rests entirely on `infra/modules/container-app.bicep`'s `maxReplicas = 1`, documented only in a source comment — no test or startup assertion fails loudly if that value is ever changed to allow horizontal scaling, which would silently reintroduce the exact race this story fixes.
  evidence: Same "documented but not guarded" pattern already accepted for other infra/code couplings in this codebase (e.g. the OTel/Application-Insights dual-logging constraint in project-context.md). A guard would most naturally live as an integration/smoke check against the deployed Bicep output rather than unit-testable C#, which is why round 4 didn't add one inline. Raised by adversarial review, round 4.

- [open] source_spec: `_bmad-artifacts/implementation/spec-status-recompute-serialization-perf.md`
  summary: `HouseholdRecomputeLock`'s internal `Releaser.DisposeAsync` double-dispose guard (`Interlocked.Exchange`) has no test calling `DisposeAsync` twice to prove it actually prevents a double-release.
  evidence: Low-risk — the guard is a standard, well-understood idiom, and `await using` in every real call site only ever disposes once. Worth a quick test if this type is ever reused outside its current single call site. Raised by adversarial review, round 4.


## Deferred from: code review of story-3-5-dual-entry-points-multi-file-import-queuing (2026-08-27)

- [open] `act()` warnings appear when the new queue test file runs as part of the full suite (not in isolation), indicating unflushed async state from the polling/upload effects. Deferred, pre-existing: the pre-diff single-file panel's own test file produced the same class of warning; this story's per-item hook extraction just multiplies the exposure (N concurrent instances instead of one) rather than introducing the underlying pattern. Raised by adversarial review (Blind Hunter). [web/src/components/smart-plug-import/use-smart-plug-import-job.ts]

- [open] No affordance exists to remove/cancel a queue item while it's `uploading`/`processing` (`dismissable` only covers `completed`/`flaggedForReview`/`failed`). Deferred, pre-existing gating logic: the old single-file panel's reset button was gated identically (`state !== 'uploading'/'processing'`), so this isn't a new restriction, but batching multiple files raises the stakes — one accidental file in a 5-file drop now can't be pulled back out until it resolves on its own. Worth a follow-up UX pass, not required by any AC. Raised by adversarial review (Blind Hunter) and edge-case review. [web/src/components/smart-plug-import/smart-plug-import-page.tsx:110-111]

- [open] No upper bound on how many files one selection/drop can enqueue — a household member selecting an entire folder of exports fires that many concurrent uploads and mounts that many permanently-polling hook instances, against a backend that Dev Notes itself confirms processes jobs strictly one at a time; a large batch leaves most items sitting in "Waiting" for a long stretch with no soft cap or warning. Deferred: not required by any AC, worth tracking for a future hardening pass. Raised by adversarial review (Blind Hunter) and edge-case review. [web/src/components/smart-plug-import/smart-plug-import-page.tsx:33-38]


## Deferred from: story-4-1-trend-history-view (2026-08-29)

- [open] `GetStatusHistory`/`StatusSnapshotRepository.GetForHouseholdAsync` reads a household's entire `StatusSnapshot` lifetime with no pagination or trailing-window bound, unlike `GetCurrentStatus`'s bounded-window read (Epic 3 Retro Action Item #2, PR #21) — a latent NFR1 perf risk for a long-lived household with years of recompute history. Deliberately not addressed here: no AC requires bounding, and Trend History's whole point is showing the full trend, not a windowed one. Mirrors the "pre-existing pattern extended, not yet a measured problem at current data volumes" framing already used for the identical class of issue elsewhere in this file (Story 2.4's entry above). [src/EnergyTracker.Application/GetStatusHistory.cs, src/EnergyTracker.Infrastructure/Adapters/StatusSnapshotRepository.cs]


## Deferred from: code review of spec-trend-chart-time-axis (2026-08-30)

- [open] source_spec: `_bmad-artifacts/implementation/spec-trend-chart-time-axis.md`
  summary: `TrendChart`'s new month/week tick generation (`getMonthBoundaries`, `getWeekBoundaries`, `lastLabeledYear`) computes calendar boundaries via `Date.prototype.getFullYear()/getMonth()`, i.e. the viewer's local time zone, against `entries[].computedAtUtc` — so two viewers in different time zones can see a different *number* of ticks for identical data, not just different label text. No `TZ` is pinned in the Vitest config, so the suite's hardcoded tick-count assertions implicitly assume the CI runner's local zone stays close to UTC.
  evidence: This mirrors an already-deliberate codebase convention (the pre-existing `gapDateFormat` in the same file already formats UTC timestamps in local time, same as AD-9's Eve Home local-time parsing) — not a new pattern introduced by this change, just a new place where it affects tick *count*, not only display text. A correct fix means pinning `TZ` in the shared Vitest config, which is outside this spec's file boundary (`trend-chart.tsx`/`trend-chart.test.tsx` only). Raised by adversarial review (Blind Hunter).


## Deferred from: code review of spec-power-point-mapping-list-scroll (2026-09-02)

- [open] source_spec: `_bmad-artifacts/implementation/spec-power-point-mapping-list-scroll.md`
  summary: Shared `DialogContent` (`web/src/components/ui/dialog.tsx`) has no viewport-relative height constraint (`max-h-[...vh]`/`overflow-y-auto`) of its own, so a Dialog with enough content in total (header + body copy + inputs + this now-capped list + error text) can still overflow a short viewport even though the "many Power Points" failure mode this diff targets is fixed.
  evidence: Pre-existing gap in the shared Dialog primitive, affecting every `DialogContent` consumer in the app, not introduced by this diff — fixing it here would mean changing shared UI behavior for every dialog in the codebase, well beyond this one-shot's scope of the Power Point mapping list specifically. Raised by adversarial review (Blind Hunter).


## Deferred from: code review of story-4-3-correcting-a-meter-reading (2026-09-10)

- [open] No unit test distinguishes "`RecomputeAsync` called after the transaction commits" vs. "called inside it" [tests/EnergyTracker.Application.Tests/EditMeterReadingTests.cs] — deferred, pre-existing (identical gap already present in `CreateMeterReadingTests.cs`): the `ExecuteInTransactionAsync` NSubstitute stub is a synchronous passthrough, so it can't distinguish placement; the AD-7/AC #3 "must not be inside the transaction" requirement is enforced only by a code comment. Raised by adversarial + edge-case review.


## Deferred from: code review of spec-3-10-cleanup-async-job (2026-09-12)

- [open] source_spec: `_bmad-artifacts/implementation/spec-3-10-cleanup-async-job.md`
  summary: The cleanup dialog shows no progress indication for the entire poll duration — just a disabled Delete button, for a wait that (per this incident) can run minutes long.
  evidence: Raised by adversarial review (Blind Hunter). Not fixed here: the frozen spec's own Boundaries explicitly say "Existing `cleaningUp` dialog state extends to cover polling — no new UI states," and a progress affordance worth shipping (elapsed time, spinner copy) is a UX decision, not a mechanical fix, disproportionate to an incident hotfix's scope. Worth a follow-up UX pass. [web/src/components/smart-plug-import/job-history-list.tsx]


## Deferred from: code review of spec-3-10-cleanup-per-import-detach (2026-09-12)

- [open] source_spec: `_bmad-artifacts/implementation/spec-3-10-cleanup-per-import-detach.md`
  summary: Issuing on the order of hundreds of sequential row-level `UPDATE`s against `SmartPlugReadings` within one long-lived transaction (this round's per-import detach loop) could, on SQL Server, cross the automatic lock-escalation threshold (~5,000 locks) and upgrade to a table-level lock — blocking every household's reads/writes against that shared table (AD-3 multi-tenant architecture) for the job's full duration, not just the household being cleaned up.
  evidence: Raised by adversarial review (Blind Hunter). Plausible but not confirmed against production Azure SQL Basic-tier — SQL Server's escalation check is documented as per-statement (each of this loop's `UPDATE`s only touches ≤200 rows, well under the threshold), but locks held by earlier statements in the same uncommitted transaction are not released until commit, so the *cumulative* row-lock count against `SmartPlugReadings` grows across the whole loop; whether that alone triggers escalation (vs. only a single statement's own count) is genuinely uncertain without a load test against Azure SQL Basic-tier at incident scale. A confirmed fix (e.g. `ALTER TABLE SmartPlugReadings SET (LOCK_ESCALATION = DISABLE)`) is a schema change with its own memory-overhead tradeoff, deserving its own dedicated investigation rather than a same-day guess under incident pressure. [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs — DetachReadingsForImportAsync]


## Deferred from: code review of story-1.12 (2026-09-17)

- [open] AC #2/#5/#6 (RP-initiated logout actually terminating both sessions, and landing back on the login step) have zero automated regression coverage — proven only by a one-time manual Auth0 + Chrome verification (Task 7), never re-run in CI [src/EnergyTracker.Api/Endpoints/AuthEndpoints.cs]. Deferred, pre-existing: `/logout`'s implementation is unchanged by this diff (built in Story 1.5); this story only adds a frontend signal and control around an already-existing, unmodified backend mechanism. Raised by adversarial review (Blind Hunter). *Update (Story 11.8, 2026-10-17): `LogoutEndpointTests` and `FederatedLogoutTests` now cover the endpoint and the sign-out event logic automatically; the live provider round-trip stays manual (11.8 Task 8.2 and Part R).*

- [open] No focus/accessibility management across the logoff dialog's three steps — `confirm` → `queue-warning`/`federated-warning` swaps content inside the same mounted `Dialog` with no focus move or live-region announcement telling a screen-reader user the content changed underneath them [web/src/components/settings/settings-page.tsx:108-134]. Deferred, pre-existing pattern: no multi-step dialog anywhere in this codebase establishes focus-management conventions yet, so this isn't a regression specific to this diff. Raised by adversarial review (Blind Hunter) and edge-case review.


## Deferred from: code review of spec-3-10-cleanup-sweep-async (2026-09-17)

- [open] On SQL Server (the production provider — Azure SQL, no `READ_COMMITTED_SNAPSHOT`/RCSI configured anywhere in this repo), `SweepExpiredAsync`'s eligibility `SELECT` can itself block on row locks held by another household poll's still-in-flight, uncommitted bounded-chunk transaction — SQL Server's default (locking) Read Committed lets a reader block on an uncommitted writer's exclusive locks, unlike Postgres's MVCC (non-blocking reads), which this spec's own concurrency guard (`pg_try_advisory_xact_lock`/`sp_getapplock`) does not address since it only gates the *delete* phase, not this earlier read. Bounded impact: the block, when it happens, is bounded by the winning poll's own one-chunk duration (the same latency this spec's design already accepts for the poll that legitimately does the work) and self-heals on the next poll — not unbounded, not data-corrupting. A full fix (e.g. a scoped `READPAST` table hint on this specific query, or enabling database-wide RCSI) has its own tradeoffs and blast radius (RCSI is a database-wide isolation change) better suited to a dedicated follow-up spec than folding into this bugfix. Raised by adversarial review (Blind Hunter), loop 2. [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs — `SweepExpiredAsync`'s eligibility query, `TryAcquireHouseholdSweepLockSqlServerAsync`]

- [open] The new per-household advisory lock only guards `SweepExpiredAsync`'s bounded-chunk path; `DeleteEligibleAsync` — shared via the extracted `DeleteImportChunkAsync` helper but also called directly by `DeleteJobsAsync` (the manual "clean up everything" endpoint) — acquires no lock. A concurrent manual cleanup and an in-flight automatic sweep chunk for the same household can still contend on real DB row locks, reproducing this spec's own target latency-spike shape for that specific caller pair. Deferred rather than fixed here: this spec's own frozen "Never" boundary explicitly excludes touching `DeleteJobsAsync`'s call path, so closing this gap requires renegotiating that scope, not a patch to the current diff. Raised by edge-case review, loop 2. [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs — `DeleteEligibleAsync`, `DeleteJobsAsync`]


## Deferred from: code review of spec-db-command-timeout-scope (2026-09-17)

- [open] source_spec: `_bmad-artifacts/implementation/spec-db-command-timeout-scope.md`
  summary: No test proves `SetCommandTimeout(120)`'s "carries forward for the rest of this scoped DbContext's job" claim across an actual second method call on the same `DbContext` instance (e.g. `AddAsyncCore` followed by `CompleteSmartPlugImportProcessing`'s `AddGapsAsync`/`RecomputeAsync`) — the new test only asserts the timeout immediately after `AddAsyncCore` returns.
  evidence: Raised by edge-case review and adversarial review (Blind Hunter) independently. Same test-shape convention already accepted for `UpdateMappingAsync`'s existing 180s assertion (immediate, not chained) — not a new gap introduced by this diff's own testing standard, but a real one: the whole design leans on `SetCommandTimeout` persisting on the DbContext instance across later, unrelated method calls, which nothing in this repo's test suite directly exercises end-to-end. Worth a chained-call test if this pattern is ever reused a third time. [tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs]


## Deferred from: code review of spec-power-point-mapping-duplicate-timeout (2026-09-18)

- [open] `UpdateMappingSetBasedWithConflictToleranceAsync`'s new large-mixed-batch test exercises all three classification outcomes only combined in one batch, and only the `KwhValue` branch of the three-way divergence condition (`DeviceName`/`KwhValue`/`IntervalEnd`) — no isolated all-no-conflict, all-exact-duplicate, or all-divergent test exists, and a regression specific to the `DeviceName`-only or `IntervalEnd`-only divergence comparison would not be caught. Separately, the method is reachable via two entry points in `UpdateMappingAsync` (the `AnyMappingConflictAsync` pre-check, and the fast-path's `catch (DbUpdateException or DbException)` race-triggered fallback) but only the pre-check entry point is exercised by any test. Raised by edge-case review. [tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs, src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs]


## Deferred from: code review of story-6.1 (2026-09-18)

- [open] source_story: `_bmad-artifacts/implementation/6-1-event-logging.md`
  summary: `new Date(datetimeLocalValue).toISOString()` silently rewrites DST-transition local times — a spring-forward gap time (`2026-03-29T02:30` in Europe/Berlin) is normalized forward an hour, and a fall-back ambiguous time resolves to the first (DST) instance. The value posted is not the value the user picked, with no warning on either side.
  evidence: Raised by edge-case review. Pre-existing pattern shared with `log-reading-sheet.tsx`, which uses the same `toDateTimeLocalValue` helper and the same `new Date(...)` conversion — so this is a project-wide backfill-entry characteristic, not a story-6.1 regression. Server-side timestamp checks are only the future-skew and year-2000 floor, neither of which catches it. [web/src/components/event/log-event-sheet.tsx:194, web/src/components/meter-reading/log-reading-sheet.tsx]

- [open] source_story: `_bmad-artifacts/implementation/6-1-event-logging.md`
  summary: A device clock running more than 5 minutes ahead of the server makes the sheet's *untouched default* timestamp fail validation — the prefill comes from `new Date()` and the server rejects `occurredAt > UtcNow + 5min`, so the user gets a 400 without having edited anything. No client-side `max` on the input and no clamp before send.
  evidence: Raised by edge-case review. Pre-existing: `CreateMeterReading` uses the identical `MaxFutureClockSkew` constant against an identically prefilled `datetime-local` field, so the Log Reading sheet has the same behavior. Worth one shared fix (clamp client-side, or widen/relax the skew rule) rather than a per-feature patch. [web/src/components/event/log-event-sheet.tsx:87, src/EnergyTracker.Application/CreateEvent.cs:38]


## Deferred from: code review of story-6.2 (2026-09-19)

- [open] source_story: `_bmad-artifacts/implementation/6-2-event-history-view.md`
  summary: `EventsCard` eager-fetches `GET /api/events` on mount regardless of whether the collapsed-by-default `<details>` is ever expanded, matching `MeterReadingsCard`'s existing idiom — now the 3rd disclosure card on Trend History doing this, so the page issues 3 unconditional collection fetches on every visit.
  evidence: Raised by adversarial review (Blind Hunter). Pre-existing pattern (this story's Task 4.3 explicitly says to match `MeterReadingsCard` precisely), not introduced by this diff, but worth revisiting the eager-fetch idiom itself now that a third instance exists. [web/src/components/trend-history/trend-history-page.tsx]

- [open] source_story: `_bmad-artifacts/implementation/6-2-event-history-view.md`
  summary: AC #3's "still displays after archive" automated coverage only exercises the `Room` tag type — `PowerPoint`/`Device` are only covered for the creation-time archived-rejection path (409), not the persists-after-archive-at-display-time path.
  evidence: Raised by adversarial review (Blind Hunter). Functional risk is low since the display code path (`GetEventHistory`/`EventRepository`/`events-card.tsx`) never branches on tag type — only test coverage is missing. Worth a parametrized test across all 3 types in a follow-up. [tests/EnergyTracker.Infrastructure.Tests/EventRepositoryTests.cs, tests/EnergyTracker.Api.Tests/EventEndpointsTests.cs]


## Deferred from: code review of 7-2-full-data-import-restore-migration (2026-09-22, Pass 2/frontend+docs)

- [open] No `aria-live` region on the error/validation-failure states [web/src/components/data-import/data-import-panel.tsx:142] — pre-existing systemic gap: `DataExportPanel`'s identical error-rendering pattern has the same gap, unrelated to this story. Raised by adversarial review (Blind Hunter).


## Deferred from: code review of story-7.1 (2026-09-22)

- [open] source_story: `_bmad-artifacts/implementation/7-1-full-data-export.md`
  summary: `TryGetHouseholdId` copy-pasted verbatim into yet another endpoint file — now duplicated across at least `MeterReadingEndpoints`, `EventEndpoints`, and this new `HouseholdExportEndpoints`.
  evidence: Pre-existing pattern this diff extends rather than introduces; a future bugfix to that logic requires remembering to touch every copy. Raised by adversarial review (Blind Hunter) during code review. [src/EnergyTracker.Api/Endpoints/HouseholdExportEndpoints.cs]


## Deferred from: code review of spec-household-export-oom-fix (2026-09-25)

- [open] source_spec: `_bmad-artifacts/implementation/spec-household-export-oom-fix.md`
  summary: The new `(HouseholdId, IntervalStart, Id)` composite index on `SmartPlugReadings` makes the pre-existing standalone `HouseholdId` index largely redundant (leftmost-prefix rule), but that older index wasn't removed, adding a fourth index's write overhead to the highest-ingest-volume table in the schema — the very table whose volume caused this incident.
  evidence: Raised by adversarial review (Blind Hunter). Real but out of this bugfix's stated Code Map scope (which only asked for the new composite index); dropping an index other query paths might still rely on deserves its own explicit look, not a silent removal bundled into an incident fix.

- [open] source_spec: `_bmad-artifacts/implementation/spec-household-export-oom-fix.md`
  summary: `HouseholdExportStream`'s write side (`HouseholdExportEndpoints.WriteExportAsync`) hardcodes top-level JSON property-name string literals that must stay in sync with `HouseholdExportResult`'s property names (the untouched import/restore wire contract) by convention only — no test deserializes streamed output back into `HouseholdExportResult` to catch future drift if either side is renamed.
  evidence: Raised by adversarial review (Blind Hunter). Real but narrow: both records are currently structurally identical and were introduced together in this same diff (see the Spec Change Log's "Ask First" resolution); a round-trip parity test would close this permanently but was judged lower priority than the flush-cadence fix and the Events pagination gap given this fix's time budget.


## Deferred from: code review of story-8.2 (2026-09-26)

- [open] source_story: `_bmad-artifacts/implementation/8-2-dashboard-desktop-tablet-layout.md`
  summary: New Playwright locators hardcode English translation strings (`'Log an Event'`, `'Import Smart Plug data'`), coupling a layout/breakpoint regression test to content text — any future copy change unrelated to layout would break it.
  evidence: Same fragility pattern already present in the nav-chrome spec this test is modeled on, propagated rather than fixed. Raised by adversarial review (Blind Hunter) during code review. [web/e2e/app-shell.spec.ts]

- [open] source_story: `_bmad-artifacts/implementation/8-2-dashboard-desktop-tablet-layout.md`
  summary: No automated (unit or e2e) regression coverage for AC #2's CTA-adjacency claim ("Log reading" renders directly following the cards, not floating).
  evidence: Acknowledged gap in the story's own Debug Log/Completion Notes — a fully populated `StatusCard` state (needed to exercise the real `showPopulated` branch) wasn't reachable in the dev sandbox. Raised by the Acceptance Auditor during code review. [web/src/components/dashboard/dashboard-page.tsx]


## Deferred from: code review of story-8.3 (2026-09-26)

- [open] source_story: `_bmad-artifacts/implementation/8-3-trend-history-desktop-tablet-layout.md`
  summary: No automated check ties the translated `shortLabel` to being a case-insensitive substring of the translated `entryPointLabel` (WCAG 2.5.3, Label in Name) — holds for the current en-US/de-DE strings (manually verified in Story 8.2) but nothing would catch a future locale or copy edit that breaks the relationship.
  evidence: Pre-existing pattern from Story 8.2, not introduced fresh here. Raised by adversarial review (Blind Hunter) during code review. [web/src/locales/en-US/translation.json, web/src/locales/de-DE/translation.json]

- [open] source_story: `_bmad-artifacts/implementation/8-3-trend-history-desktop-tablet-layout.md`
  summary: `MeterReadingsCard`'s trailing action-column `<TableHead />` remains unlabeled (no accessible column name for screen readers) — this diff only added `w-px` to it.
  evidence: Missing label predates this story; pre-existing gap not caused by this change. Raised by adversarial review (Blind Hunter) during code review. [web/src/components/meter-reading/meter-readings-card.tsx:97]


## Deferred from: code review of 8-5-settings-desktop-tablet-layout (2026-09-28)

- [open] `InviteGeneratePanel`'s `handleGenerate` has no abort-on-unmount guard for its in-flight `POST /api/household-invites`, so closing the wide-mode dialog mid-request discards the server-created invite token client-side [web/src/components/household-invite/invite-generate-panel.tsx:20-44] — deferred, pre-existing behavior of a component this story intentionally left unmodified; newly reachable via the new Dialog call site but low probability/consequence. Raised by the Edge Case Hunter.


## Deferred from: code review of story-8.4 (2026-09-28)

- [open] source_story: `_bmad-artifacts/implementation/8-4-tariff-radar-desktop-tablet-layout.md`
  summary: The new e2e case only asserts `tariff-radar-content`'s width is bounded (`>600px` and `<=660px`), never that the column is actually centered (`wide:mx-auto`) — an accidental `mr-auto`/removed `mx-auto` leaving it capped-but-left-aligned would still pass.
  evidence: Pre-existing pattern — `dashboard-content`/`trend-history-content`'s own e2e assertions (Story 8.2/8.3) have the identical width-only gap. Raised by adversarial review (Blind Hunter) during code review. [web/e2e/app-shell.spec.ts]


## Deferred from: code review of story-8-6-wide-column-increase-across-all-surfaces (2026-09-29)

- [open] source_story: `_bmad-artifacts/implementation/8-6-wide-column-increase-across-all-surfaces.md`
  summary: Settings and Tariff Radar wrappers have no unit-level assertion of the `wide:max-w-[900px]` class (Tariff Radar has no page test file at all); the cap on those two pages is guarded only by e2e.
  evidence: Pre-existing coverage gap noted in the story's Dev Notes. Raised by Edge Case Hunter during code review. [web/src/components/settings/settings-page.test.tsx, web/src/components/tariff/tariff-radar-page.tsx]


## Deferred from: code review of 8-10-theme-toggle-profile-menu (2026-09-30)

- [open] source_story: `_bmad-artifacts/implementation/8-10-theme-toggle-profile-menu.md`
  summary: Radix `role="menu"` now contains non-menuitem radiogroups (wrapped in `role="none"`); screen-reader menu navigation may skip them. Popover fallback not evaluated.
  evidence: Design-level; revisit with 8.11/8.12. Still open after 8.12 (Profile-menu-specific; the Settings card is plain-page context with correct radiogroup semantics). [web/src/components/dashboard/profile-menu.tsx]

- [open] source_story: `_bmad-artifacts/implementation/8-10-theme-toggle-profile-menu.md`
  summary: Spinner ignores `prefers-reduced-motion`; nothing asserts `index.html`'s inline theme script (key, colours) stays in sync with `color-scheme.ts`.
  evidence: Low. **Spinner half resolved by Story 8.11** (`motion-safe:animate-spin`); the inline-script drift test is still open. [web/src/components/preferences/preference-strip.tsx, web/index.html]


## Deferred from: code review of 8-12-settings-preferences-card-mobile (2026-09-30)

- [open] source_story: `_bmad-artifacts/implementation/8-12-settings-preferences-card-mobile.md`
  summary: Crossing 660px while a Language save is in flight unmounts the Preferences card, dropping the screen-reader announcement and any error alert.
  evidence: Low, rare. Fix would lift the live region above the `!wide` gate in `SettingsPage`. [web/src/components/preferences/preferences-card.tsx]


## Deferred from: locale × theme sweep (spec-locale-theme-sweep, 2026-10-02)

- [open] source_spec: `_bmad-artifacts/implementation/spec-locale-theme-sweep.md`
  summary: Tariff Radar history table scrolls horizontally by ~10px at 659px in de-DE (scrollWidth 587 > clientWidth 577 in the `overflow-x-auto` wrapper) when an entry carries correction notes.
  evidence: Found in the live pass with real household data; the page itself does not overflow. The e2e matrix seed (3 entries, corrections, date ranges) only reaches 578 vs 577, within the audit's 1px tolerance, so this defect has NO automated regression guard. Candidate fixes: tighter column padding or wrapping on the correction note, or accept the scroll container as intentional. [web/src/components/tariff/tariff-history-list.tsx]

- [open] source_spec: `_bmad-artifacts/implementation/spec-locale-theme-sweep.md`
  summary: Icon-only header buttons below 660px (Import in `trend-history-page.tsx`, and the Dashboard Event/Import equivalents) are `size-10` (40px) with no `::before` hit-area extension, so the touch target is 40px, not the 44px the sweep spec and Stories 8.2/8.3 assumed.
  evidence: Hit-testing 2px outside each edge of the 659px Import button lands outside the button on all four sides; the e2e test pins the 40px box. Fix is `size-11` or a `::before` extension, a visual change deferred for a decision. [web/src/components/trend-history/trend-history-page.tsx:84]


## Deferred from: keyboard tab-order check (spec-tab-order-check, 2026-10-02)

- [open] source_spec: `_bmad-artifacts/implementation/spec-tab-order-check.md`
  summary: Nothing enforces that a new `wide:order-*` / reordering class gets a tab-order test; the rule in `project-context.md` is prose only.
  evidence: Raised by the quick-dev review. A source-scan guard test (every file using `wide:order-`/`order-first` must be named in `tab-order.spec.ts`) would close it, like the architecture guard tests. [web/e2e/tab-order.spec.ts]


## Breakpoint/column drift guard (spec-breakpoint-drift-test, Epic 8 retro action #3)

- [open] source_spec: `_bmad-artifacts/implementation/spec-breakpoint-drift-test.md`
  summary: The drift test is value-based and px-only; the underlying duplication remains. A shared exported breakpoint constant (hook `WIDE_QUERY`, `wide-viewport.ts` stub) would remove two copies outright, but needed a production change that the spec's Ask First reserved.
  evidence: Review-found. Not scanned: rem/em units, `max-width` queries, `min-[Npx]:` variants, column assertions outside `app-shell.spec.ts`. [web/src/layout-constants.drift.test.ts]

## Deferred from: story creation for Epic 10 (10-1-migration-safety-on-deploy, 2026-10-02)

- [open] source_story: `_bmad-artifacts/implementation/10-1-migration-safety-on-deploy.md`
  summary: Self-hosters may have no documented way to apply EF Core migrations: grep finds no migration step in `docker-compose.yml`, `Dockerfile` or `docs/self-hosting.md`, and the API never calls `Migrate()`/`MigrateAsync()` (only `scripts/migrate.sh` is documented, in `docs/local-development.md`).
  evidence: Found while grounding story 10.1 (originally the Story 1.1 entry, marked resolved on the strength of `docs/local-development.md:32` alone). Not verified end to end against a fresh self-host install; kept out of 10.1's scope. [docs/self-hosting.md, docker-compose.yml]

## Deferred from: code review of 10-1-migration-safety-on-deploy (2026-10-02)

- [open] Expand/contract rule in `project-context.md` is advisory prose only: no lint, review-checklist item or test, and the destructive-change list omits index/constraint drops, type changes, data-modifying `Sql()` and unique indexes on existing data.
- [open] No long-term retention (LTR) configured: a bad migration noticed after the 7-day PITR window is unrecoverable; not documented as an accepted risk.
- [open] Rollback runbook does not verify auditing/firewall/TDE/alerts on the swapped-in database, nor name the Azure role needed for `az sql db restore`/`rename`.
- [open] Pending-migrations list in the restore-point block runs once before the retry loop and can read "unavailable" while the firewall rule is still propagating.

## Deferred from: story 10-2-event-correlation-forward-window-recompute (2026-10-02)

- [open] After a regression-prompt resolve, the re-evaluated set is "Events since the trigger", uncapped; typically a handful. [src/EnergyTracker.Application/ResolveMeterRegressionPrompt.cs]
- [open] A correlation can flip or disappear as readings arrive (latest evaluation wins); by design, but not announced in the UI.
- [open] When the AI answers "None" for a persisting deviation, every later in-window reading costs one AI call: the two persisted columns cannot distinguish "evaluated, no match" from "never evaluated". Bounded by readings per ±7-day window. Epic 9 stories 9.3/9.4 live verification should include one forward-window recompute against the real backend.
- [open] The requeue trigger skips AI-off Households (documented AD-8 exception, Ask First #3, Ralf 2026-10-02), so enabling the Settings toggle does not re-evaluate existing Events until a Meter Reading in their window next changes. [src/EnergyTracker.Application/RequeueEventCorrelations.cs]

## Deferred from: code review of story-10-2 (2026-10-02)

- Same-timestamp tiebreak in `WindowedDeviationCalculator.ExcludeAtOrAfter` (and the in-memory ordering in `CorrelateEvent`) uses .NET `Guid.CompareTo`, which can differ from the database `uuid` ordering; only matters for readings with an identical `ReadingTimestamp` at the open-prompt boundary. [src/EnergyTracker.Domain/Calculations/WindowedDeviationCalculator.cs]
- Requeue is not deduplicated or capped: every reading write, edit and resolve enqueues one `CorrelateEvent` job per in-range Event. [src/EnergyTracker.Application/RequeueEventCorrelations.cs]
- Per-Event dedup of requeued `CorrelateEvent` jobs (review decision 2c) needs an Event reference on `BackgroundJob` (new column/migration); not possible inside story 10.2 (AC #10: no migration). Concurrent jobs for one Event remain last-write-wins. [src/EnergyTracker.Application/RequeueEventCorrelations.cs] Reason: Ralf accepts last-write-wins (2026-10-02).

- source_spec: `_bmad-artifacts/implementation/spec-local-dev-start-stop-scripts.md`
  summary: Set `strictPort: true` in `web/vite.config.ts` so Vite fails instead of silently moving to 5174 when 5173 is taken.
  evidence: Without it, `scripts/dev-up.sh` health-checks 5173 (possibly another process) and prints a URL Vite isn't serving; changing vite.config.ts was out of scope (Ask First).

## Deferred from: code review of story 10-3-navchrome-dom-order-tab-order (2026-10-04)

- [open] An open Account menu stays open when the window is resized below 660px, because its trigger sits in the top nav, which becomes `display:none`; the same behaviour existed before 10.3. Seen in the 10.3 live gate. [web/src/components/dashboard/profile-menu.tsx]
- [open] No automated guard that the bottom tab bar stays pinned to the viewport bottom (`mt-auto`) on a short page at <660px; `tab-order.spec.ts` only checks it sits below the content, and unit tests check the class string. [web/src/components/dashboard/nav-chrome.tsx]

## Deferred from: code review of story-11-3-azure-queue-consumer-resilience (2026-10-11)

- [open] A job failed without running (`job-retries-exhausted`, or a `Queued` row failed by the 12-hour backstop whose message is later skipped) leaves its temp upload file on the instance's disk; only a run's own `finally` deletes it. Known and accepted in the story (3b.4); a temp-file sweep or the C3 blob move would close it. [src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs]
- [open] `docs/local-vs-azure-deltas.md` has no row for the queue adapters' difference (Azure consumer survives storage errors and fails a job after 5 dequeues with `job-retries-exhausted`; the in-process adapter has no dequeue limit). Skipped in 11.3 because the file's D1–D6 structure is deployment-gotcha sections, not behaviour rows; add a D7 if that difference ever surprises an operator. [docs/local-vs-azure-deltas.md]
- [open] `AzureStorageQueueJobProcessingService` sets `queueEnsured` once and never resets it, so a queue deleted after startup produces a 5-minute error loop until the app restarts (before 11.3 it crashed the host). Resetting the flag on a `QueueNotFound` response from `ReceiveMessagesAsync` would close it; the test `The_queue_is_created_once_after_it_succeeds_not_on_every_poll` would need a companion case. [src/EnergyTracker.Infrastructure/Adapters/AzureStorageQueueJobQueue.cs]
- [open] If the process dies after a committed import/restore has deleted its temp file but before `TryComplete`, the redelivery ends `Failed` with `upload-missing` instead of recognising the work as done, and the member is told to re-upload. Narrow window; the outcome class existed before 11.3 (generic failure / "upload it again" message). A fix needs a completion marker that survives the file deletion. [src/EnergyTracker.Application/ProcessSmartPlugImport.cs, src/EnergyTracker.Application/RestoreHouseholdData.cs]

## Deferred from: code review of story-11-4-unmapped-smart-plug-readings-keyed-by-device (2026-10-11)

- [open] The Postgres pre-check in `NarrowSmartPlugReadingDeviceNameKeyUnmappedByDevice` counts code points (`length()`), while the parsers and the restore validator count UTF-16 units. A stored `DeviceName` of 101–200 astral characters (emoji) passes the migration, but the household's own export would then fail restore validation and a re-upload of that file would be rejected. SQL Server's `DATALENGTH / 2` already counts UTF-16 units. Needs 101+ emoji in a plug name, so negligible; a fix is a UTF-16-aware length in the Postgres `DO` block. [src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20261011115653_NarrowSmartPlugReadingDeviceNameKeyUnmappedByDevice.cs]
- [open] Deploy N's "Apply pending EF Core migrations" step (Story 11.4: SQL Server `ALTER COLUMN` nvarchar(max) to nvarchar(200) plus `CREATE INDEX` on `SmartPlugReadings`) took 8 min 49 s (about 529 s) on Azure SQL Basic (5 DTU) against `Command Timeout=600` (run 38147146530; the step timestamps include the whole migration run). It succeeded, but a future column rewrite on this table could exceed the timeout; raise the timeout or plan such changes differently. [.github/workflows/app-deploy.yml]

## Deferred from: dev-story of story-11-5-ci-identity-split-supply-chain-hygiene (2026-10-11)

- [open] A branch pusher can still read the repository secrets that other workflows hold (`DATABASE_ADMIN_PASSWORD`, `OIDC_CLIENT_SECRET`) by editing a workflow on their branch; 11.5 only stopped the maintained `validate-infra` job from receiving them (placeholders instead). The real fix is a GitHub Environment with a branch restriction for the secrets, which changes the federated-credential subjects to `environment:`. Also open: Dockerfile base images pinned by digest. Both are out of scope for 11.5. [.github/workflows, Dockerfile]

## Deferred from: code review of story-11-5-ci-identity-split-supply-chain-hygiene (2026-10-11)

- [open] CI (`pr-review.yml`, `app-deploy.yml`) and the Dockerfile frontend stage build on Node 25, a non-LTS line that, to the best of the reviewer's knowledge, reached end of support in mid-2026 (verify). Ralf chose "move CI to match the Dockerfile" during 11.5; the next step is moving both to an LTS line (the vitest `--no-experimental-webstorage` flag is valid there too). [.github/workflows/pr-review.yml, .github/workflows/app-deploy.yml, Dockerfile]
- [open] `Directory.Packages.props` line 15 comment says "same 10.0.10 line as the rest of EF Core"; the line is 10.0.12 since Dependabot #112. Cosmetic. [Directory.Packages.props]

## Deferred from: Story 11.5 R2.2 (2026-10-11)

- [open] `pr-review.yml` "What-if validation" sets `CURRENT_IMAGE` from the "Resolve current Container App image" step but never passes `placeholderImage="$CURRENT_IMAGE"` to `az deployment group what-if` (as `infra-deploy.yml` does), so every PR what-if shows the Container App image changing to the quickstart placeholder. Noise only; passing the parameter would make the diff show real changes only. [.github/workflows/pr-review.yml]

## Deferred from: dev-story of story-11-6-data-protection-key-encryption (2026-10-14)

- [open] The NuGet Audit gate failed `dotnet restore` on `main` itself (transitive `System.Security.Cryptography.Pkcs` 9.0.11 via `Microsoft.Data.SqlClient` 6.1.6, Pkcs/Xml 10.0.12 via the ASP.NET Core packages) before Story 11.6 touched anything. Worked around in PR #126 with `CentralPackageTransitivePinningEnabled` and pins to 10.0.13. Remove the two pins once the parent packages ask for patched versions themselves, and look at why Dependabot did not raise this before `main` went red. [Directory.Packages.props]
- [open] Optional: delete the revoked plaintext `DataProtectionKeys` rows after the Story 11.6 cutover (Ralf's call, Q2/R4). Removes live plaintext key material; backups keep it until they expire. [infra/README.md]
- [open] Key Vault diagnostic settings to Log Analytics (audit trail of wrap/unwrap) were left out on purpose: a second ingestion path against the shared daily cap (AD-19 OTel note). Revisit if an audit trail of key use is wanted. [infra/modules/key-vault.bicep]
- [open] `Azure.Identity` is now an explicit 1.21.0 pin while `Microsoft.Data.SqlClient` 6.1.6 asks for 1.17.1; a later SqlClient major may need the pin re-checked (AD-21/AD-23 Entra `Active Directory Default` migration path). [Directory.Packages.props]

## Deferred from: Story 11.6 close-out (2026-10-17)

- [open] Story 11.6 A2.4: confirm a second `infra-deploy.yml` run does not add a second `data-protection` key version (`az keyvault key list-versions --vault-name <vault> -n data-protection`; one version today, 105399eb...). Not run before close because `infra-deploy.yml` has run only once since the vault exists. A second version would be harmless (older versions stay usable for unwrap), but record it. Check after the next routine infra deploy. [infra/modules/key-vault.bicep]

## Deferred from: dev-story of story-11-7-security-headers-csp-rate-limiting (2026-10-17)

- [open] Per-IP `/login` limit keys on the full IPv6 address; a client with a whole `/64` can rotate addresses inside it. Accepted at household scale; group by prefix if it ever matters.
- [open] The limiter is in-process (AD-24). A distributed limiter store is part of the multi-instance work already listed in `ARCHITECTURE-SPINE/deferred.md`; do it together with lifting `maxReplicas`.
- [open] HSTS uses the framework defaults (30 days, no `includeSubDomains`, no preload). Raise it (a year, maybe `includeSubDomains`) once the production CSP/HSTS have run clean for a while.

## Deferred from: dev-story of story-11-8-auth-hardening-csrf-logout-session-cap (2026-10-17)

- [open] The 90-day cap is on the app's cookie only. An identity-provider session that is still alive re-authenticates silently, so "sign in again" after the cap may not show a login page. `prompt=login` or `max_age` on re-auth is a separate decision. [src/EnergyTracker.Api/SessionLifetime.cs]
- [open] No server-side revocation of a stolen cookie before the cap (AD-17 accepts this). Member removal now exists (Story 11.9) and cuts a removed identity off on its next request, because membership is resolved per request; a stolen cookie of a *current* member still lives until the 90-day cap or until that member is removed. [src/EnergyTracker.Api/SessionLifetime.cs]
- [open] A request whose body fails minimal-API binding answers the binding's own 400 before the X-Requested-With filter runs (binding precedes endpoint filters), so a header-less malformed request shows the binding error, not the filter's. No handler logic runs either way; accepted (RequestedWithFilter remarks). [src/EnergyTracker.Api/RequestedWithFilter.cs]
- [open] A tab that was open across the 11.8 deploy still runs the old bundle: its next mutation gets a 400 until the page is reloaded, and its logout (a navigation to the removed `GET /logout`) lands on the SPA instead of signing out. Reloading fixes both. Single-user app; not mitigated. [web/src/lib/api-client.ts]

## Deferred from: code review of 11-8-auth-hardening-csrf-logout-session-cap (2026-10-17)

- [open] `POST /logout` answers 500 when the identity provider's discovery document cannot be fetched (the OIDC handler throws, `UseExceptionHandler` drops the cookie deletion), so during a provider outage a member cannot sign out locally. Pre-existing (the old `GET /logout` failed the same way); consider catching it and degrading to a local sign-out like the "no `end_session_endpoint`" path. [src/EnergyTracker.Api/Endpoints/AuthEndpoints.cs]
- [open] After the yearly baseline is re-saved, the AI plausibility form still holds the household's old `Version`, so its first toggle PUT answers 409 and only the refetch-and-retry works (seen in the 11.8 live rehearsal). Pre-existing. [web/src/components/ai-plausibility/ai-plausibility-form.tsx]

## Deferred from: dev-story of story-11-9-member-removal-revocable-invites (2026-10-17)

- [open] Drop the plaintext `HouseholdInvites.Token` column in a later contract migration, **at least 7 days after Story 11.9's release** (the invite lifetime; give the earliest date as release date + 7 days once known). The exact change: drop the column and its unique index, delete the legacy branch of `FindInviteByTokenAsync`, drop `HouseholdInvite.Token`, update `HouseholdMembershipRepositoryTests`, `HouseholdInviteTests` and the migration tests. Until then legacy invites keep working through the legacy lookup (AC #5). [src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs]
- [open] A removed member's other open tab keeps its stale `ready` state until the page is reloaded; every API request from it fails (`HouseholdMemberEndpointsTests` sweeps every `/api` route as a removed member), and the next load lands in household creation (AC #13c, Q3). A global SPA reaction to a 403 or session polling was left out on purpose. [web/src/App.tsx]
- [open] Jobs a removed member queued keep running, and their history shows the generic "a household member" fallback instead of the name (`BackgroundJobs.QueuedByHouseholdMemberId` is `SET NULL`; AC #13d). [src/EnergyTracker.Application/ListSmartPlugImportJobs.cs]
- [open] The invite token appears in request paths (`/api/household-invites/<token>`, `/api/household-invites/<token>/accept`, `/join/<token>`). Log-level check (AC #11d): the effective logging configuration writes them. Serilog is set up with `ReadFrom.Configuration`, which reads a `Serilog` section; `appsettings.json` has none (only the `Logging:LogLevel` section, which Serilog ignores) and no environment variable sets one, so the minimum level is Serilog's default `Information` with no overrides, and ASP.NET Core's hosting diagnostics ("Request starting ...") write the request path at `Information` to stdout and so into Log Analytics. Azure Monitor traces also carry `url.path`. The token in the URL path is the Story 1.8 design and was not changed here. Options: a `Serilog:MinimumLevel:Override` for `Microsoft.AspNetCore.Hosting.Diagnostics` and a span processor that redacts the invite segment, or moving the token into a fragment or a POST body. [src/EnergyTracker.Api/Program.cs]
- [open] Expired and revoked invite rows are never swept (revoke only expires them in place); the table grows by one row per invite ever created. A sweep like `SmartPlugImportRepository.SweepExpiredAsync` would do it. [src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs]
- [open] Invite rows carry no creator, so the open-invite list cannot say who invited. Needs a nullable creator column and a decision about what to show once that member is removed (Q4). [src/EnergyTracker.Domain/HouseholdInvite.cs]
- [open] Removing a member bumps `Household.Version`, so a Settings form holding the old version (yearly baseline, AI plausibility) gets one 409 on its next save and recovers through its refetch. The same pre-existing pattern as the 11.8 AI plausibility entry above; expected, not a regression. [web/src/components/ai-plausibility/ai-plausibility-form.tsx]
- [open] The `MemberRemoval` budget (10 per 10 minutes per identity) is shared by every member and invite `DELETE`; the Task 11 walk-through (revoke, remove, leave, a few probes) used eight of ten, and a 429 shows only the generic failure copy ("Check your connection and try again"), which is misleading. Options: a `Retry-After`-aware message, or a higher budget for revoke. Not fixed in 11.9 (the limit is the audit's intended brake; the copy is the gap). Found in Task 11 (2026-10-18).

## Deferred from: dev-story of story-11-10-upload-hardening (2026-10-21)

- [open] The duplicate-id and reference id sets in `ValidateHouseholdImport` still grow with the file by item count, about 32 bytes per id: about 4 MB for the 121,000-reading test export, about 30 MB at the 250 MB cap (roughly 950,000 items). A lower import cap or an on-disk dedupe would remove it. [src/EnergyTracker.Application/ValidateHouseholdImport.cs]
- [open] The framework buffers the multipart file to its own temp file before the endpoint copies it, so one upload briefly uses twice its size on disk. A `MultipartReader`-based endpoint would stream straight into the one file; not done because it replaces `IFormFile` binding and its antiforgery wiring (entry 7.2b). [src/EnergyTracker.Api/Endpoints/HouseholdImportEndpoints.cs]
- [open] The restore job still materializes the whole object graph; only the string is gone. Measured peak RSS of the restore phase for the 31.75 MiB test export: 1250 MiB after, 2133 MiB before (Apple M2 Max, Server GC, no container limit), which is above the 1 GiB container on its own. A streaming restore needs `HouseholdRestoreData` to stop being one object. [src/EnergyTracker.Application/RestoreHouseholdData.cs, docs/data-import-restore.md]
- [open] A package with hundreds of thousands of tiny zip entries still costs memory in `ZipArchive`'s central directory before any limit runs (bounded by the 20 MB upload cap, roughly tens of MB). [src/EnergyTracker.Infrastructure/Adapters/EveHomeXlsxParser.cs]
- [open] Orphaned temp files from a process that died between validate and confirm (the registry is gone with it) are never swept; the existing AD-24 / C3 item. The local temp directory already held many stale `*.json` files from before this story when it was measured. [src/EnergyTracker.Infrastructure/Adapters/MemoryHouseholdImportUploadRegistry.cs]
- [open] The Meross CSV parser has no row cap (out of scope: the story covers XLSX). [src/EnergyTracker.Infrastructure/Adapters/MerossCsvParser.cs]
- [open] The API runs Server GC (`System.GC.Server: true`), whose gen0 budget scales with core count; on a 12-core host the same validation showed 109.7 MiB RSS growth against 21.6 MiB under Workstation GC and 54.6 MiB with a container-like heap limit. Decide the GC mode and `GCHeapCount`/`GCConserveMemory` for the 0.5 vCPU container with Story 11.11 (container config). [src/EnergyTracker.Api/EnergyTracker.Api.csproj, Dockerfile]
- [open] `HouseholdImportEndpoints` checks `file.Length > MaxFileSizeBytes`, but `FormOptions.MultipartBodyLengthLimit` is the same 250 MB, so the framework rejects an oversized body first and the friendly 400 can hardly fire; the check stays (it is tested through `HouseholdImportUploadSettings`) and the limits are untouched (Q1). [src/EnergyTracker.Api/Endpoints/HouseholdImportEndpoints.cs]
- [open] The real Eve exports carry directory entries and two `docProps` entries (`app.xml`, `core.xml`) that .NET's `ZipArchive` cannot open (OpenXml never reads them); the XLSX pre-scan skips both kinds. If a vendor ever ships a part the parser needs with such a quirk, the failure surfaces from OpenXml as a generic job failure, as before. [src/EnergyTracker.Infrastructure/Adapters/EveHomeXlsxParser.cs]
- [open] `ProcessSmartPlugImport` opens the temp file twice (header, then data), so the XLSX decompressed-size pre-scan runs twice per import (tens of milliseconds for the largest sample); a single open would halve it. [src/EnergyTracker.Application/ProcessSmartPlugImport.cs]

## Deferred from: code review of 11-10-upload-hardening (2026-10-21)

- [open] The XLSX shared-strings list holds every item as a string, so a hostile table just under the 64 million character limit made of tiny `<si>` items costs on the order of 150 MB (the design accepts a list, AC #10d; the pre-scan limit is the mitigation). A cap on the item count, or resolving strings lazily, would bound it. [src/EnergyTracker.Infrastructure/Adapters/EveHomeXlsxParser.cs]
- [open] `EveHomeXlsxParser.ReadDeviceTag` reads two cells but loads the whole shared-strings table (the old code loaded the whole table DOM on first access too). A lazy lookup would make the header read cheap. [src/EnergyTracker.Infrastructure/Adapters/EveHomeXlsxParser.cs]
- [open] A single worksheet cell is still loaded whole (`LoadCurrentElement` on the `Cell`), so one cell of up to the 64 million character part limit costs on the order of 250 MB (UTF-16 plus the `InnerText` copy). Real cells hold a timestamp or a number; a cell text length cap would bound it. [src/EnergyTracker.Infrastructure/Adapters/EveHomeXlsxParser.cs]

## Deferred from: dev-story of story-11-11-container-config-correctness (2026-10-22)

- [open] S5 leftovers the epic ACs left out (Q4): the base images are pinned by tag (`node:26-alpine`, `sdk:10.0`, `aspnet:10.0`), not by digest, and the image has no `HEALTHCHECK`. The digest matters for supply-chain reproducibility; the `HEALTHCHECK` only for self-hosters (Container Apps has its own probe). The aspnet image has no curl/wget, so a probe needs a tiny `dotnet`-based check or a health tool. [Dockerfile]
- [open] S8 (`AllowedHosts: *`) is in no Epic 11 story (11.7 decided the forwarded-headers half). It was looked at again here and stays as is: the app serves one host per deployment behind Container Apps ingress or the self-hoster's own proxy, and nothing reads `Host` for a security decision. Revisit when something does (absolute URLs in emails or invite links built from `Request.Host`, or a second hostname on one instance). [src/EnergyTracker.Api/appsettings.json, docs/codebase-audit-2026-10-10.md S8]
- [open] `/login` without OIDC is a JSON problem document, not a page in the SPA's voice (Q5): the operator of an unconfigured instance meets it as raw JSON in the browser. A small static page or an SPA route that reads the 503 would be friendlier. [src/EnergyTracker.Api/Endpoints/AuthEndpoints.cs]
- [open] The Azure `ai-api-key` secret is seeded with the placeholder `unset` (a non-empty value is required, D4), so once `Ai__Endpoint` is set without a real key the client sends `Authorization: Bearer unset`. Story 9.2 must supply the secret together with the endpoint, or the Bicep/`OpenAiCompatibleClient` pair must treat the sentinel as "no key". [infra/modules/container-app.bicep]
- [open] A host that fails startup (any `throw` out of `Program.cs`: the new connection-string and `Ai:Endpoint` checks, and the existing Data Protection fail-closed check) prints its `Unhandled exception` message and then does not exit: the container stays `running` with one core at 100% CPU. Found in the Task 9 rehearsal on the OrbStack/arm64 Docker host; reproduced on the unchanged pre-story image (root, `energy-tracker:before`) with the Data Protection throw, so it is not caused by this story, and `DOTNET_gcServer=0` or disabling the crash report does not change it. Effect: the clear message reaches `docker logs`, but nothing exits non-zero, so `restart:` policies and Container Apps' crash-loop detection never see a failed start (the startup/liveness probe still fails). Needs a look at what the runtime does after the unhandled exception (Serilog/OTel flush on a half-built host, or a host-specific createdump spin), and whether it also happens on Azure or a Linux amd64 host. [src/EnergyTracker.Api/Program.cs, Dockerfile]

## Deferred from: code review of story-11-11-container-config-correctness (2026-10-23)

- [open] `oidcConfigured` (Program.cs) checks only `Oidc:ClientId` and `Oidc:Authority`, but the `/login` 503 problem detail and `docs/self-hosting.md` say all three of `Oidc__Authority`, `Oidc__ClientId`, `Oidc__ClientSecret` are required. With the secret missing the OIDC scheme registers and `/login` redirects, then the code exchange fails at the provider. Pre-existing gate, not changed by Story 11.11; decide whether the gate should include the secret (a public-client/PKCE-only setup would then lose sign-in) or the wording should say "Authority and ClientId, plus ClientSecret for a confidential client". [src/EnergyTracker.Api/Program.cs:249]
- (The startup-failure-does-not-exit item found again in the same review is already `[open]` above under story-11-11's dev-story entry; not duplicated.)

## Deferred from: code review of spec-ci-pipeline-runtime-speedup (2026-10-23)

- [open] `maxParallelThreads` is now 8 (Ralf's call) for Infrastructure.Tests, all sharing one Postgres whose default `max_connections` is 100 and one SQL Server, on a 4-vCPU runner. The local run (8 threads, arm64 Mac) was clean, but a "too many clients" or timing-sensitive flake is only visible on CI. Watch the first PR and main runs; if one appears, raise Postgres `max_connections` on the shared container or lower the cap. [tests/EnergyTracker.Infrastructure.Tests/xunit.runner.json, tests/Shared/SharedDatabaseServers.cs]
- [open] Per-test cost left in Infrastructure.Tests: `CREATE DATABASE` plus replaying 30 migrations for every test. If the PR run shows it dominating, clone a pre-migrated Postgres template (`CREATE DATABASE ... TEMPLATE`) and restore a SQL Server backup instead. [tests/Shared/SharedDatabaseServers.cs]
