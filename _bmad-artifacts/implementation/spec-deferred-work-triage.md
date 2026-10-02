---
title: 'Triage deferred-work.md into resolved / accepted / open / promote (Epic 8 retro action item #6)'
type: 'chore'
created: '2026-10-02'
status: 'done'
baseline_commit: '8c6a90e'
context:
  - '{project-root}/_bmad-artifacts/implementation/epic-8-retro-2026-10-02.md'
  - '{project-root}/_bmad-artifacts/implementation/deferred-work.md'
  - '{project-root}/_bmad-artifacts/project-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `deferred-work.md` is 561 lines and 174 entries with no status markers; nothing ever leaves it (Epic 8 retro, pattern 4).

**Approach:** Every entry was checked against the code at baseline `8c6a90e` and given one verdict. This doc is the proposal. Nothing in `deferred-work.md` changes until the human approves the removals in Section 2.

## Boundaries & Constraints

**Always:** Proposal first, then removals only as approved. Keep the heading convention from `project-context.md` (`## Deferred from: ...`, newest last). Mark kept entries `[open]`. Add a themed index at the top of the reduced file.

**Ask First:** Deleting ACCEPTED entries (vs. archiving them). Promoting any entry to a story/spec. Any code change.

**Never:** Delete an entry whose verdict was not confirmed by a cited fix or reason. Touch code or tests in this chore.

</frozen-after-approval>

## 1. Result

174 entries (retro estimated ~165). Merged from Charlie (lines 1–280, backend/infra) and Dana (281–561, frontend/test); duplicates counted once.

| Verdict | Count | Proposed action |
|---|---|---|
| RESOLVED | 25 | Remove (fix cited in Appendix A) |
| ACCEPTED | 88 | Remove, or move to an archive file (decision 1) |
| PROMOTE | 3 | Keep until a story/spec exists, then remove |
| OPEN | 58 | Keep, add `[open]` marker, group by theme under an index |

After approval `deferred-work.md` drops from 174 entries to about 61, and the OPEN set is indexed by theme.

How verification was done: each agent checked entries via grep/read/git log. I re-verified 12 RESOLVED claims myself (lines 38, 39, 53, 130, 147, 150, 229, 281, 348, 387, 420, 453); all held. Specs cited as done (`spec-3-10-cleanup-sweep-async`, `spec-db-command-timeout-scope`, `spec-nuget-audit-ci-gate`) have `status: done`.

## 2. Decisions for Ralf

1. **ACCEPTED entries (88):** delete outright (git history keeps them), or move to `deferred-work-accepted.md`? Recommendation: archive. 11 of the evidence notes name a "revisit if X" trigger that is worth keeping findable.
2. **PROMOTE candidates (3):**
   - Line 83: CI runs `dotnet ef database update` on every deploy with no backup or rollback story. Real data is live. Suggest a migration-safety story.
   - Line 538: NavChrome is last in the DOM at ≥660px (WCAG 2.4.3). Pinned by `tab-order.spec.ts`; the entry itself says it needs its own story.
   - Line 402 (with 399): correlation runs once, so a real-time Event has no forward-window readings. This is the common case. Suggest folding into Epic 9 or a recompute story.
3. **Other bundles worth considering** (OPEN, not promoted): infra hardening (lines 58/71 Actions pinning, 80 orphaned firewall rules, 93 timeouts, 134 cert-renewal alert, 196 `maxReplicas=1` guard); concurrency in the cleanup path (316, 317).
4. **Process:** this chore edits only `deferred-work.md` and `sprint-status.yaml`. Confirm it can go straight to `main` as doc-only, as the earlier retro doc items did.

## 3. Caveats

- Line 283 (lock escalation on SQL Server) is unconfirmed and needs a load test. It stays OPEN.
- Low-confidence verdicts: 33, 148, 149, 171, 213 (Charlie); 295, 312, 375, 438, 444, 456 (Dana). I'd keep these OPEN or review them again before removal. They're flagged `low` in the tables.
- Line 137 ("never add `customDomainName` to bicepparam") is marked ACCEPTED because commit `c2a3a4a` superseded the rule. Please confirm.
- Duplicate groups: 58/71, 474/541, 399/402, 405/423/31/76/123 (`SingleAsync` without a not-found guard), 417/11, 286/317.
- Line numbers refer to `deferred-work.md` at `8c6a90e`. Apply removals in one pass.

## 4. Outcome (approved by Ralf 2026-10-02)

Decisions: (1) ACCEPTED entries archived to `deferred-work-accepted.md`; (2) all 3 PROMOTE candidates agreed, kept in `deferred-work.md` marked `[promoted: needs story]` until stories exist; (3) committed as doc-only directly on `main`; (4) line 137 accepted as superseded. Applied: 25 RESOLVED removed, 88 ACCEPTED archived, 61 kept (58 `[open]`, 3 promoted), sections reordered oldest-first, themed index added. Retro action #7 (markers on new entries) stays open: the marker format is now visible in the file header but not yet in `project-context.md`.

### Original execution plan

1. Remove approved entries (and any section left empty).
2. Add `[open]` to kept entries and a themed index at the top of the file (after the convention note).
3. Resolve decision 2 (create story stubs or leave as OPEN).
4. Set this spec to `done`; update `sprint-status.yaml` action #6. Action #7 (markers going forward) is covered by the `[open]` format.

## Appendix: per-entry verdicts

### A. RESOLVED — remove (25)

| line | section | entry | fix |
|---|---|---|---|
| 38 | 3-6 | GET /api/jobs/{id} no longer 404s; Waiting badge degrades | use-smart-plug-import-job.ts:92 handles `job.status === 'queued'` |
| 39 | 3-6 | Auth0 `name` claim unverified live | confirmed bug, fixed: HouseholdClaimTypes.cs:29 reads raw "name"; 3-6 story line 92 |
| 53 | 1-1 | No documented migration apply path | docs/local-development.md:32 `./scripts/migrate.sh` |
| 86 | azure-sql-fw | infra-deploy resets image to placeholder | Story 1-6; infra-deploy.yml:85-128 resolves current image |
| 130 | 2.2 | No ReadingTimestamp bounds | CreateMeterReading.cs:14-44 year-2000 floor + 5min skew |
| 147 | 2.4 | Full-history walk per save | Epic 3 retro #2: GetCurrentStatus.cs:68 / MeterReadingRepository.cs:107 bounded window |
| 150 | 2.4 | Concurrent RecomputeAsync race | HouseholdRecomputeLock.cs + StatusRecomputeService.cs (spec-status-recompute-serialization-perf) |
| 229 | orphan-fix | Global CommandTimeout(120) | spec-db-command-timeout-scope done; Program.cs:143 default 30s kept |
| 281 | 3-10 per-import-detach | Sweep runs sync in GET poll path (HIGH) | b521706 "bound sweep to one chunk per GET poll"; spec-3-10-cleanup-sweep-async status done; advisory lock in SmartPlugImportRepository.cs:873-890 |
| 300 | 5-3 | No responsive check of 3-panel layout | Story 8.4/8.9 reworked layout; locale-theme-sweep e2e at 659/660/900 (ba8cf6c) |
| 305 | 5-4 | locale prop drives only Intl | Story 8.11 (a249d71), marked in entry |
| 339 | 6-1 | Npgsql non-zero offset 500 | 5d1324c / spec-datetimeoffset-utc-normalization, marked |
| 348 | 6-1 | Events lacks time-ordered index | EventConfiguration.cs:50 HasIndex(HouseholdId, OccurredAt, CreatedAtUtc) |
| 387 | ssh-net-cve | No CI gate on NU1903 | 81f81a7 (#67), spec-nuget-audit-ci-gate, Directory.Build.props NuGetAudit settings |
| 393 | 6.3 | Live verification gap | Marked resolved in entry; real LLM round-trip residual belongs to Epic 9.3/9.4 |
| 420 | 7.1 | Export buffered in memory | 794bdc3 (#70) stream and page export, plus 2b2a698 observability |
| 435 | 8.2 | Import label asymmetry on Trend History | Story 8.3 reused shortLabel (retro: picked up) |
| 453 | 8.3 | Tests English-only | ba8cf6c locale x theme sweep (de-DE/en-US) |
| 468 | 8.4 | German label clipping at 660px | ba8cf6c sweep |
| 474 | 8.4 | Tab order after row wrappers | cee2c59 (#86), marked at 541 |
| 496 | 8.10 | PreferenceStrip pending guard | Story 8.11 (a249d71), marked |
| 504 | 8.10 | whitespace-nowrap overflow | Story 8.12 (0006b2f), marked |
| 518 | 8.12 | Breakpoint duplicated | 71992ba drift test; residual in 560 |
| 541 | tab-order | Resolves 8.4 tab-order gap | cee2c59; dup of 474 |
| 555 | breakpoint drift | Drift guard added | 71992ba |

### B. ACCEPTED — remove or archive (88)

| line | section | entry | reason / revisit trigger |
|---|---|---|---|
| 3 | 8-5 | "— 0 Power Points" copy, no zero guard | cosmetic; plural i18n `_other` still renders 0; accepted in Task 6 |
| 4 | 8-5 | Invite Dialog doesn't auto-close crossing 660px | narrow reachability; low consequence |
| 6 | 8-5 | Dual-render tests use DOM-order indexing | codebase-wide NavChrome convention |
| 11 | oom-fix | Paged export not snapshot-consistent | human-confirmed trade-off 2026-09-25; HouseholdExportReader.cs PageSize=500 |
| 17 | oom-fix | AD-3 guard test is token scan, no positive assert | matches existing guard-test convention |
| 25 | 7-2 p2 | No poll timeout on repeated 404 | mirrors use-smart-plug-import-job convention |
| 26 | 7-2 p2 | No client file-size pre-check | not in any AC; no size check in data-import-panel.tsx |
| 31 | 7-2 p1 | RestoreWriter SingleAsync throws if no Household | HouseholdRestoreWriter.cs:160; unreachable (no household delete) — see SingleAsync dup group |
| 32 | 7-2 p1 | Single long restore transaction | deliberate atomicity (:25); monitor for large households |
| 33 | 7-2 p1 | No rate limit on /household-import | no rate-limit layer in repo (grep errored; verified by absence only) |
| 34 | 7-2 p1 | OriginalFileName in error message | RestoreHouseholdData.cs:29; rendered via React state (auto-escaped) in data-import-panel.tsx:41 |
| 43 | 4-3 | Optimistic concurrency skipped on no-op path | EditMeterReading.cs:32-35 deliberate with comment |
| 45 | 4-3 | Unhandled RecomputeAsync after commit | mirrors CreateMeterReading.cs:100 |
| 49 | 3-6 rev | FindMembersByIds tenant isolation trust-based | single caller ListSmartPlugImportJobs.cs:58; revisit if 2nd caller |
| 57 | 1-2 | DB firewall AllowAzureServices | still in both bicep modules; non-VNet architecture AD-6/7 |
| 59 | 1-2 | Public ingress, no auth gate | placeholder-era note; auth shipped in 1.5 |
| 60 | 1-2 | No deploy approval gate | no `environment:` in workflows; matches AC design |
| 64 | 1-3 | Re-adding ACR registries entry race | only from-scratch redeploy |
| 68 | 1-4 | oxlint no-unused-vars no `_` ignore | revisit if blocks code |
| 69 | 1-4 | Required checks omit app_id | low risk |
| 70 | 1-4 | Fork PR first-time approval blocks checks | platform setting |
| 72 | 1-4 | Fork-skip step fragile to insertions | fine today |
| 76 | 1.5 | /api/session SingleAsync if household missing | SingleAsync dup group; unreachable |
| 92 | 1-6 | `containerapp list [0].name` arbitrary pick | still in 3 workflows; one app by design |
| 97 | 1-7 | Forwarded headers trusted from any peer, unvalidated | Program.cs:476-477 cleared; self-limited |
| 98 | 1-7 | ForwardLimit default untested multi-hop | no multi-hop topology |
| 103 | 1-9 | Check-then-act race on archived parent | soft-delete self-healing |
| 104 | 1-9 | `null!` accessor in DbContext construction | entry itself re-checked 2026-09-17; remaining null! only in migration-only tests |
| 107 | 1-9 | Settings nav bypasses browser history | no react-router in web/package.json |
| 117 | design-token | No visual-regression tooling | Ralf decision, Epic 8 retro: out of scope; no toHaveScreenshot |
| 123 | 2.1 | Household SingleAsync not-found | SingleAsync dup group |
| 127 | 2.2 | IdempotencyKey index global | MeterReadingConfiguration.cs:50; UUID entropy |
| 137 | custom-domain | "Never add customDomainName to bicepparam" unguarded | rule superseded: commit c2a3a4a set customDomainName/CertificateReady in bicepparam; moot |
| 149 | 2.4 | Resolve prompt doesn't recompute Status | in-spec per AD-7 |
| 154 | 3.2 | No optimistic concurrency on mapping | architectural; no Version in MapSmartPlugImportToPowerPoint |
| 155 | 3.2 | Mapping not idempotent | no idempotency pattern |
| 156 | 3.2 | Unpaged readings load/update | later work addressed bulk writes (3.8/3.9); mirrors 3.1 |
| 169 | 3.3 | Coverage uses UTC .DateTime vs local dates | SmartPlugCoverageSignal.cs:25-26; no household tz concept |
| 170 | 3.3 | Concurrent imports phantom gaps | documented non-goal |
| 175 | 3.4 | Watermark MAX across vendors | no vendor-swap case |
| 176 | 3.4 | No concurrent watermark race test | story Dev Notes OQ#2 |
| 177 | 3.4 | Eve Home early-stop assumes monotonic | documented trade-off |
| 178 | 3.4 | Batch ids from readings[0] | by construction |
| 186 | 2.7 | Duplicate "Close" accessible name | shadcn Dialog pattern, dialog.tsx:66 |
| 190 | recompute-perf | ExcludeFromOpenPrompt throw on lookup failure | unreachable by construction |
| 205 | 3-7 | TOCTOU in conflict-confirmation read | pre-existing pattern |
| 215 | 3-5 | awaitingMapping w/ null id stuck | copied pre-existing gate |
| 223 | scroll | Scrollbar shift, no gutter | minor cosmetic |
| 241 | 3-10 fix | No upper bound on total chunks | manual cleanup now async job (spec-3-10-cleanup-async-job) |
| 244 | 3-10 fix | Long txn blocks Postgres autovacuum | prod is Azure SQL; revisit if Postgres |
| 247 | 3-10 fix | Eligibility query before txn | pre-existing, widened only |
| 250 | 3-10 fix | DeleteBatchSize/Threshold unbenchmarked | constants SmartPlugImportRepository.cs:765,821; revisit on repeat 500 |
| 256 | 3-10 fix-2 | Chunk sizing from stale counts | no observed case |
| 262 | 3-10 async | Job > 60min visibility timeout redelivery | AzureStorageQueueJobQueue.cs:75; AD-6 uniform; revisit if long cleanup |
| 268 | 3-10 async | Always enqueues even with zero eligible | no user impact |
| 271 | 3-10 async | /api/jobs/{id} omits JobType | only internal branching (GetBackgroundJobStatus.cs:26); revisit with 3rd job type |
| 274 | 3-10 async | In-flight cleanup jobId lost on reload | mitigated by server dedup |
| 286 | 3-10 per-import-detach | Manual vs sweep delete can deadlock | Sweep now has per-household lock; manual path still unlocked (see 317). Revisit on an observed deadlock |
| 291 | 5-1 | Tariff history 2nd query page size race | Narrow, self-corrects |
| 295 | 5-2 | annualPaceKwh no negative guard | Inherited from Pattern Detective; not re-verified in code |
| 299 | 5-3 | compareTariff unchecked JSON cast | Same pattern as all of tariff-api.ts |
| 304 | 5-4 | refreshTariffCheck conflates error and none | Mirrors refreshStatus |
| 306 | 5-4 | No .Produces<T>() on /tariff-check | No Produces anywhere in TariffEndpoints.cs; convention |
| 310 | 1-12 | No test of real DI OIDC scheme lookup | Same framework constant on both sides |
| 322 | db-command-timeout | Migrations lose 120s timeout | Revisit if a migration times out on Basic tier |
| 325 | db-command-timeout | Reads before AddAsyncCore on 30s default | Deliberate scope per spec |
| 334 | pp-mapping-dup-timeout | Count==0 early-return untested | Branch hard to reach |
| 354 | datetimeoffset-utc | Converter throws near Min/MaxValue | Write paths range-limited |
| 357 | datetimeoffset-utc | Comparer ignores offset on backfill | Revisit before any backfill |
| 360 | datetimeoffset-utc | ReloadAsync adds round trip after commit | ReloadAsync present in all 3 repos; no delete paths |
| 366 | 6.2 | Non-numeric page param generic error | Mirrors MeterReadingEndpoints |
| 369 | 6.2 | Count and page two round trips | Mirrors precedent |
| 378 | 6.2 | EventsCard page not clamped | Unreachable until Event delete exists |
| 381 | 6.2 | 6.1 status flip bundled in 6.2 PR | Not a code defect |
| 399 | 6.3 | Correlation computed once, never recomputed | Deliberate AD-10. Same root cause as 402 |
| 405 | 6.3 | SingleAsync no not-found guard | Duplicate of 123/76/31 class; households never deleted |
| 408 | 6.3 | AI calls contend in single-worker queue | Opt-in; revisit in Epic 9.3 |
| 411 | 6.3 | AC5 backend deployment-wide | Ralf decision 2026-09-21 |
| 417 | 7.1 | No snapshot consistency across export queries | Duplicate of line 11 (paged export); accepted trade-off |
| 423 | 7.1 | SingleAsync multi-row unguarded | Dup of 405 |
| 429 | 7.1 | Completion note claims arch test | Nit |
| 438 | 8.2 | shrink-0 with no wrap fallback | Sweep ran de-DE; no overflow found there |
| 459 | 8.3 | No visual regression tooling | Ralf decision in Epic 8 retro |
| 471 | 8.4 | One 150-line e2e test | Inherited |
| 486 | 8.7 | Empty grid when page beyond last | Unreachable: no delete path for these entities |
| 490 | 8.7 | Intl.format RangeError on bad date | Server-supplied ISO |
| 531 | locale sweep | Sweep audit depth gaps | No false results in 48 cells |
| 550 | tab-order | Coverage gaps (Profile menu, tagging grid) | No known defect |

### C. PROMOTE — own story/spec (3)

| line | section | entry | why |
|---|---|---|---|
| 83 | azure-sql-fw | Migrate on deploy, no backup/rollback story | real data now live; needs migration-safety story |
| 402 | 6.3 | Real-time Event has no forward-window readings | Common case, not edge; duplicate root cause of 399. Suggest fold into Epic 9 or recompute story |
| 538 | tab-order | NavChrome last in DOM at wide | WCAG 2.4.3; pinned by tab-order.spec.ts; entry says needs own story |

### D. OPEN — stays, grouped by theme (58)


**test-coverage (18)**

| line | section | entry | evidence | conf |
|---|---|---|---|---|
| 20 | oom-fix | Export stream property names vs HouseholdExportResult, no round-trip test | no export stream/round-trip test found in tests/ | med |
| 44 | 4-3 | No test distinguishes recompute inside/outside txn | stub passthrough; comment-only enforcement | med |
| 99 | 1-7 | No middleware-ordering regression test | ForwardedHeadersTests covers proto only | low |
| 106 | 1-9 | Uneven length-validation tests | only CreateRoomTests asserts | med |
| 129 | 2.2 | IDOR test claim inaccurate | MeterReadingEndpointsTests has no real cross-household test | med |
| 199 | recompute-perf | No double-dispose test for lock | HouseholdRecomputeLockTests.cs only single dispose | high |
| 213 | 3-5 | act() warnings in full suite | not rechecked at runtime | low |
| 235 | trend-axis | TZ not pinned in Vitest | no TZ in web/package.json or vite.config.ts | med |
| 311 | 1-12 | Logout ACs only manually verified | No automated RP-logout coverage | med |
| 328 | db-command-timeout | No chained-call test for timeout carry-over | Only immediate assertions | med |
| 333 | pp-mapping-dup-timeout | Mixed-batch test only; one entry path tested | Tests only vary KwhValue (SmartPlugImportRepositoryTests.cs) | med |
| 375 | 6.2 | AC3 archive coverage only Room | Per entry, tests not extended | low |
| 441 | 8.2 | e2e locators hardcode English | Convention; low value | med |
| 444 | 8.2 | No test for CTA adjacency | Not addressed in later commits | low |
| 465 | 8.4 | e2e does not assert centering | Width-only assertion | low |
| 480 | 8.6 | No unit assertion of max-w class on Settings/Tariff | Per line 557 stays open; drift test checks values only | high |
| 508 | 8.10 | Spinner motion and inline theme script drift | Spinner resolved by 8.11; drift test still open | high |
| 545 | tab-order | No guard forcing new order classes into spec | Prose rule only (project-context.md:74) | high |

**a11y (7)**

| line | section | entry | evidence | conf |
|---|---|---|---|---|
| 27 | 7-2 p2 | No aria-live on import/export error states | no aria-live/role=alert in data-import-panel.tsx | high |
| 312 | 1-12 | No focus mgmt across logoff dialog steps | settings-page.tsx not re-checked; duplicate theme of 500/515 | med |
| 450 | 8.3 | No check shortLabel substring of entryPointLabel | No such test found | med |
| 456 | 8.3 | Trailing action TableHead unlabeled | Not verified fixed; triggers have aria-label, header does not appear to | low |
| 500 | 8.10 | role=menu contains radiogroups | Marked still open after 8.12 | high |
| 515 | 8.12 | Live region dropped on 660px cross | Low, rare | high |
| 527 | locale sweep | Icon buttons 40px not 44px | trend-history-page.tsx:89 still `size-10` | high |

**infra-hardening (7)**

| line | section | entry | evidence | conf |
|---|---|---|---|---|
| 58 | 1-2 | GH Actions pinned to tags, not SHAs | workflows use @v7/@v3 etc.; DUP of line 71 | high |
| 71 | 1-4 | Actions pinned by mutable tags | DUP of line 58 | high |
| 80 | azure-sql-fw | Orphaned gh-actions-migrate-* firewall rules | app-deploy.yml:148/221 cleanup warns only; no reaper; 128-rule cap | med |
| 93 | 1-6 | No timeout-minutes in infra-deploy | grep -c = 0 | high |
| 111 | otel | OTel resource attrs minimal | Program.cs:77,96 bare AddService | high |
| 134 | custom-domain | No cert renewal failure alerting | domain now live (main.bicepparam:59-60); no cert alert in main.bicep | med |
| 196 | recompute-perf | RecomputeLock depends on maxReplicas=1, unguarded | container-app.bicep:62; no test | high |

**validation (6)**

| line | section | entry | evidence | conf |
|---|---|---|---|---|
| 5 | 8-5 | InviteGeneratePanel no abort-on-unmount | no AbortController in invite-generate-panel.tsx | med |
| 128 | 2.2 | No KwhValue scale validation | no rounding in CreateMeterReading.cs; UI step=0.01 only | med |
| 148 | 2.4 | Threshold/gap-days no range validation | Household.cs no bounds; settings UI status unknown | low |
| 171 | 3.3 | No lower bound on estimated kWh | no negative guard in parser/detector | low |
| 342 | 6-1 | DST gap/ambiguous local time rewritten silently | No handling found; shared with log-reading-sheet | med |
| 345 | 6-1 | Device clock >5min ahead rejects default timestamp | MaxFutureClockSkew 5min unchanged in CreateEvent.cs:18 and CreateMeterReading.cs:14; no client clamp verified | med |

**ux-polish (6)**

| line | section | entry | evidence | conf |
|---|---|---|---|---|
| 105 | 1-9 | No retry after tagging-scaffold load failure | no retry in tagging-scaffold-manager.tsx | med |
| 214 | 3-5 | Can't cancel queue item while uploading/processing | `dismissable` gating unchanged (page.tsx:174) | med |
| 216 | 3-5 | No cap on files per drop | no limit in smart-plug-import-page.tsx | med |
| 221 | scroll | Shared DialogContent lacks max-h | no max-h/overflow in ui/dialog.tsx | high |
| 265 | 3-10 async | No progress indication during cleanup | job-history-list.tsx only `cleaningUp` flag | med |
| 523 | locale sweep | Tariff history scrolls 10px at 659 de-DE | Marked open; no regression guard | high |

**perf (5)**

| line | section | entry | evidence | conf |
|---|---|---|---|---|
| 14 | oom-fix | Old HouseholdId index redundant on SmartPlugReadings | SmartPlugReadingConfiguration.cs:58 still present beside :63 composite | high |
| 193 | recompute-perf | 3 sequential round-trips, contract unstated | MeterReadingRepository.cs:107-127 | med |
| 209 | 4-1 | Unbounded StatusSnapshot read | StatusSnapshotRepository has no Take/window | med |
| 283 | 3-10 per-import-detach | Cumulative row locks may escalate to table lock | No LOCK_ESCALATION or load test anywhere; never confirmed | med |
| 372 | 6.2 | Eager fetch in collapsed disclosure cards | 3 unconditional fetches | med |

**concurrency (3)**

| line | section | entry | evidence | conf |
|---|---|---|---|---|
| 143 | 2-5 | Unsequenced concurrent refreshStatus | no request-id guard in App.tsx | med |
| 316 | sweep-async | SQL Server sweep SELECT can block on writer locks | No READPAST/RCSI in src | high |
| 317 | sweep-async | DeleteEligibleAsync (manual) takes no lock | Lock only in sweep path; near-duplicate of 286 | high |

**docs (2)**

| line | section | entry | evidence | conf |
|---|---|---|---|---|
| 160 | epic2-header | Epic 2 Architecture header missing AD-15/10/3 | line 7 still `AD-4, AD-7, AD-12, AD-14, AD-16` | high |
| 163 | epic2-header | Story 2.3 glass cites no UX-DR11 | needs spec-owner/UX call | med |

**tech-debt (2)**

| line | section | entry | evidence | conf |
|---|---|---|---|---|
| 426 | 7.1 | TryGetHouseholdId copy-pasted | Still private copies in HouseholdImportEndpoints.cs:31, JobEndpoints.cs:13 etc. | high |
| 559 | breakpoint drift | Value-based, px-only; duplication remains | Needs production change (shared constant) | high |

**i18n (1)**

| line | section | entry | evidence | conf |
|---|---|---|---|---|
| 182 | 1-10 | No de-DE test for toggle strings | no de-DE in tagging-scaffold-manager.test.tsx; partly overlaps locale sweep | med |

**concurrency-correctness (1)**

| line | section | entry | evidence | conf |
|---|---|---|---|---|
| 396 | 6.3 | CorrelateEvent skips AD-12 prompt exclusion | ExcludeFromOpenPrompt only in status path, not CorrelateEvent | med |