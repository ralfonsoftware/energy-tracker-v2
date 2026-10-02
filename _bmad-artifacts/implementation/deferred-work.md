# Deferred work

Triaged 2026-10-02 against baseline `8c6a90e` (Epic 8 retro action #6, `spec-deferred-work-triage.md`). This file holds **open** and **promoted** items only. Resolved entries were removed; accepted trade-offs moved to `deferred-work-accepted.md`.

Conventions: append new sections at the end, chronological (see `project-context.md`). Mark each entry `[open]`, `[resolved: <story>]` or `[accepted: <reason>]`; review open items at each epic retro.

## Index

**Promoted (need a story/spec)**

- Migrate on deploy, no backup/rollback story (code review of spec-azure-sql-ci-migration-firewall (2026-08-13))
- Real-time Event has no forward-window readings (code review of story-6.3 (2026-09-21))
- NavChrome last in DOM at wide (keyboard tab-order check (spec-tab-order-check, 2026-10-02))

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

**concurrency-correctness (1)**

- CorrelateEvent skips AD-12 prompt exclusion — code review of story-6.3 (2026-09-21)


## Deferred from: code review of 1-2-azure-infrastructure-as-code-resource-deployment-pipeline (2026-08-12)

- [open] GitHub Actions pinned to floating version tags, not commit SHAs (`azure/login@v2`, `actions/checkout@v4`) [.github/workflows/infra-deploy.yml:22,28] — supply-chain hardening opportunity for a workflow with `id-token: write`; not required by any AC.


## Deferred from: code review of 1-4-pull-request-review-workflow (2026-08-13)

- [open] All actions in `pr-review.yml` pinned by mutable major-version tags (`@v7`, `@v6`, `@v3`) rather than SHA [.github/workflows/pr-review.yml] — pre-existing convention from Story 1.2/1.3, propagated rather than introduced by this diff.


## Deferred from: code review of spec-azure-sql-ci-migration-firewall (2026-08-13)

- [open] source_spec: `_bmad-artifacts/implementation/spec-azure-sql-ci-migration-firewall.md`
  summary: No periodic sweep prunes orphaned `gh-actions-migrate-*` SQL firewall rules left behind by a killed/force-cancelled runner (the in-job `if: always()`+`continue-on-error` cleanup only covers normal step failure, not hard cancellation).
  evidence: Azure SQL server-level firewall rules have a hard cap (128); enough abandoned runs could eventually exhaust it and start blocking legitimate deploys. Building a reaper is a distinct, separately-scoped task, not part of this hotfix.

- [promoted: needs story] source_spec: `_bmad-artifacts/implementation/spec-azure-sql-ci-migration-firewall.md`
  summary: The CI migration step runs `dotnet ef database update` on every deploy with no pre-migration backup/snapshot and no expand/contract discipline documented — a bad migration commits directly against production with no rollback story.
  evidence: This is a pre-existing characteristic of the overall "migrate on deploy" strategy (not introduced by this diff, which only makes migrations apply where none were applying before); worth a dedicated migration-safety pass once the app has real user data at stake, not blocking for a schema that today only adds new tables.


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

- [open] AC #2/#5/#6 (RP-initiated logout actually terminating both sessions, and landing back on the login step) have zero automated regression coverage — proven only by a one-time manual Auth0 + Chrome verification (Task 7), never re-run in CI [src/EnergyTracker.Api/Endpoints/AuthEndpoints.cs]. Deferred, pre-existing: `/logout`'s implementation is unchanged by this diff (built in Story 1.5); this story only adds a frontend signal and control around an already-existing, unmodified backend mechanism. Raised by adversarial review (Blind Hunter).

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


## Deferred from: code review of story-6.3 (2026-09-21)

- [open] source_story: `_bmad-artifacts/implementation/6-3-wattage-plausibility-correlation.md`
  summary: `CorrelateEvent`/`WindowedDeviationCalculator` do not apply AD-12's "exclude readings at/after an open MeterRegressionPrompt" filter that `GetCurrentStatus` applies via `PatternDetectiveCalculator.ExcludeFromOpenPrompt` — a household with an open regression prompt whose triggering reading falls inside an Event's ±7-day window could have its rough correlation computed from a raw, not-yet-corrected meter delta (e.g. a rollover/reset artifact).
  evidence: Deliberate scope simplification, not caught by any test (none exists for this interaction). Reasoned low-risk given AC #1's own "rough/approximate signal" framing and UX-DR17's no-false-precision stance — a correlation that's occasionally derived from an uncorrected delta is consistent with the feature's own explicitly-approximate nature — but worth applying the same exclusion `GetCurrentStatus` uses if this proves to generate visibly wrong Bump/Dip results in practice. [src/EnergyTracker.Application/CorrelateEvent.cs, src/EnergyTracker.Domain/Calculations/WindowedDeviationCalculator.cs]

- [promoted: needs story] source_story: `_bmad-artifacts/implementation/6-3-wattage-plausibility-correlation.md`
  summary: For a real-time-logged Event (`OccurredAt` ≈ now), the ±7-day window's forward half has no `MeterReading`s yet when `CorrelateEvent` runs seconds later, since those readings haven't been taken — and because correlation is computed exactly once and never revisited, this is the common case for real-time-logged Events, not just a backfill edge case.
  evidence: Reinforces the item above (same root cause: no recompute trigger exists in this codebase). Raised by adversarial review (Blind Hunter) during code review. [src/EnergyTracker.Application/CorrelateEvent.cs:45-49]


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

- [promoted: needs story] source_spec: `_bmad-artifacts/implementation/spec-tab-order-check.md`
  summary: At ≥660px NavChrome (top nav + Profile avatar) is visually first but last in the DOM, so keyboard users Tab through the whole page before reaching it (WCAG 2.4.3 Focus Order). Kept deliberately by Story 8.1 (`wide:order-first`) so phones keep the bottom bar last.
  evidence: Pinned by `web/e2e/tab-order.spec.ts` on all four screens (it fails if the nav moves in the DOM, so the fix must update the pin). Candidate fixes: render the nav first in the DOM at ≥660px via `useWideBreakpoint` (touches `nav-chrome.tsx` and the four page mounts; watch the 660px boundary), or add a skip link plus `<nav aria-label>` landmarks (new UI and strings in both catalogs). Needs its own story. [web/src/components/dashboard/nav-chrome.tsx:48]

- [open] source_spec: `_bmad-artifacts/implementation/spec-tab-order-check.md`
  summary: Nothing enforces that a new `wide:order-*` / reordering class gets a tab-order test; the rule in `project-context.md` is prose only.
  evidence: Raised by the quick-dev review. A source-scan guard test (every file using `wide:order-`/`order-first` must be named in `tab-order.spec.ts`) would close it, like the architecture guard tests. [web/e2e/tab-order.spec.ts]


## Breakpoint/column drift guard (spec-breakpoint-drift-test, Epic 8 retro action #3)

- [open] source_spec: `_bmad-artifacts/implementation/spec-breakpoint-drift-test.md`
  summary: The drift test is value-based and px-only; the underlying duplication remains. A shared exported breakpoint constant (hook `WIDE_QUERY`, `wide-viewport.ts` stub) would remove two copies outright, but needed a production change that the spec's Ask First reserved.
  evidence: Review-found. Not scanned: rem/em units, `max-width` queries, `min-[Npx]:` variants, column assertions outside `app-shell.spec.ts`. [web/src/layout-constants.drift.test.ts]
