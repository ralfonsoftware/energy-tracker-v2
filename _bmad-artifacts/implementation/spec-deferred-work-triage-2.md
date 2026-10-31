---
title: 'Second triage of deferred-work.md after Epic 11 (Epic 10 retro action item #4)'
type: 'chore'
created: '2026-10-31'
status: 'done'
baseline_commit: '3cf5a9b'
context:
  - '{project-root}/_bmad-artifacts/implementation/epic-10-retro-2026-10-10.md'
  - '{project-root}/_bmad-artifacts/implementation/spec-deferred-work-triage.md'
  - '{project-root}/_bmad-artifacts/implementation/deferred-work.md'
  - '{project-root}/_bmad-artifacts/implementation/deferred-work-accepted.md'
---

## Intent

**Problem:** `deferred-work.md` is 640 lines again. After the 2026-10-02 triage it held 61 entries; it now holds about 183 (the Index lists 57 old entries and none of the 120+ added since). Epic 10 retro decision: leave it until Epic 11 is done, then triage open items only, drop entries closed by Epic 11, keep the marker and index structure.

**Approach:** Same method as `spec-deferred-work-triage.md`. This document is the proposal. Nothing in `deferred-work.md` changes until Ralf approves the decisions in Section 2.

## 1. Result

Epic 11 is complete (19 of 19 `done` in `sprint-status.yaml`). Baseline `3cf5a9b`.

| Verdict | Count | Proposed action |
|---|---|---|
| RESOLVED | 7 | Remove (fix cited below) |
| DUPLICATE / NOISE | 13 | Merge into the surviving entry or remove |
| ACCEPTED | about 35 | Move to `deferred-work-accepted.md` |
| PROMOTE | 4 | Keep, mark `[promoted: needs story]` until a story exists |
| OPEN | about 120 after merges | Keep with `[open]`, fix the 4 unmarked entries, rebuild the Index |

After approval `deferred-work.md` drops from about 183 entries to about 125, all marked, with a complete Index.

**How verification was done.** I checked the RESOLVED, PROMOTE and "still open" claims for the older entries against the code at `3cf5a9b` with grep and file reads (see Section 3). Entries added since 2026-10-10 are Epic 10/11 dev-story and review entries; I judged them from their own text plus the story files. Where I did not check the code, the table says "text only".

## 2. Decisions for Ralf

1. **ACCEPTED entries (about 35):** archive to `deferred-work-accepted.md` (as in the first triage)? Recommendation: yes. Entries with a "revisit if" trigger keep it in the archive.
2. **PROMOTE candidates (4), Section 5:** which become stories? My ranking: P1 first (it is due now), then P2, P3, P4.
3. **Fix-now doc edits (2), Section 4:** apply in this chore, or leave as `[open]`? They are one-line text edits, no code.
4. **The 4 unmarked entries** under `story 10-2` and `spec-local-dev-start-stop-scripts` (Section 6): get `[open]` and a heading. The Ralf-accepted ones move to ACCEPTED.
5. **Process:** doc-only commit directly on `main` (as in the first triage)? It touches `deferred-work.md`, `deferred-work-accepted.md`, this spec and `sprint-status.yaml` (retro action #4).

## 3. RESOLVED: remove (7)

| Entry (section, date) | Fix / evidence |
|---|---|
| GitHub Actions pinned to floating tags (1-2, 2026-08-12) | Story 11.5: every `uses:` in `.github/workflows` is a commit SHA with a version comment |
| All actions in `pr-review.yml` pinned by mutable tags (1-4, 2026-08-13) | Same; duplicate of the line above |
| `TryGetHouseholdId` copy-pasted (story-7.1, 2026-09-22) | Story 11.19: only `HouseholdRequiredFilter.cs` mentions it, as the thing it replaced |
| `HouseholdRecomputeLock` depends on `maxReplicas=1`, unguarded (recompute-perf round 4, 2026-08-24) | Story 11.13: `SingleInstanceInvariantIsDocumentedTests` asserts no `maxReplicas` value moved off 1 |
| `HouseholdRecomputeLock` double-dispose has no test (`[closed]` entry, 2026-08-24) | Already marked closed by Story 11.17; only the marker line is left |
| CI and Dockerfile on Node 25, a non-LTS line (11.5 review, 2026-10-11) | CI and `Dockerfile` are on Node 26 now (`pr-review.yml:62`, `app-deploy.yml:60,116`, `Dockerfile:5`). Confirm Node 26 is your LTS target (it enters LTS in Oct 2026) |
| Logout ACs only manually verified (story-1.12, 2026-09-17) | Story 11.8: `LogoutEndpointTests` and `FederatedLogoutTests` cover it; the live provider round trip stays manual by decision |

## 4. Fix-now doc edits (2, optional)

- `Directory.Packages.props` line 15 comment says "same 10.0.10 line as the rest of EF Core"; EF Core and the Data Protection package are all `10.0.13` now.
- `_bmad-artifacts/project-context.md` lines 22, 23, 32 say EF Core, OpenIdConnect and Data Protection are `10.0.12`; `Directory.Packages.props` has `10.0.13` (EF packages are aligned now, so the 11.13 review's "patch drift" entry is half fixed: only the text is stale).

This closes two open entries (11.5 review "line 15 comment", 11.13 review "EF patch drift") at once.

## 5. PROMOTE (4)

| # | Entry | Why |
|---|---|---|
| P1 | Drop plaintext `HouseholdInvites.Token` column (contract migration) (11.9, 2026-10-17) | The entry says "at least 7 days after Story 11.9's release". 11.9 was done 2026-10-21, so the window passed on 2026-10-28. The legacy lookup and a plaintext token column still ship (`HouseholdInvite.cs:10-12`). Exact change is already written in the entry |
| P2 | Self-hosters have no documented way to apply EF migrations (10.1 create-story, 2026-10-02) | Re-checked: `docs/self-hosting.md` has no migration text. Real self-host users would hit it on first start |
| P3 | Invite token appears in request paths and so in Log Analytics (11.9, 2026-10-17) | The entry already analysed it: Serilog has no `Serilog` section, request paths log at `Information`. A live secret in logs; options are written down |
| P4 | Host that throws at startup does not exit (11.11, 2026-10-22) | Restart policies and Container Apps crash-loop detection never see a failed start. Needs a look at the runtime behaviour first |

Bundle candidates (kept `[open]`, not promoted): infra hardening (no `timeout-minutes` in `infra-deploy.yml`, orphaned `gh-actions-migrate-*` firewall rules, cert-renewal alert, no LTR, runbook gaps, migration timeout 529 s of 600 s, OTel resource attributes); supply chain 2 (secrets readable by a branch pusher, base-image digests, `HEALTHCHECK`); shared `StripComments` hardening (3 entries merge into one); a11y pass (7 entries).

## 6. DUPLICATE / NOISE: merge or remove (13)

| Entry | Action |
|---|---|
| 11.11 review: "(The startup-failure item found again ... is already `[open]` above)" | Remove (a note, not an entry) |
| 11.13: "Orphaned temp files of a dead process ... Already `[open]` under Story 11.10" | Remove (duplicate note) |
| 11.12 review: test cost of `WithWebHostBuilder` hosts ("no separate action") | Remove; covered by the 11.x ci-pipeline "per-test migration cost" entry |
| 11.12 dev-story and 11.12 review: `HouseholdMembershipResolver` `virtual` / service locator | Merge into one entry |
| 11.12 review guard-test hardening, 11.13 `StripComments`, 11.13 review scanner copy, 11.18 `CSharpSource.StripComments` gaps | Merge into one "harden the shared `StripComments`" entry |
| 11.9 "Removing a member bumps `Household.Version`" | Merge into 11.8 "AI plausibility form holds old `Version`" (same cause) |
| 11.19 review: `oidcConfigured` ignores empty `ClientSecret` | Merge with 11.11 review `oidcConfigured` entry |
| 11.19 review: endpoint filters run after binding | Merge with 11.8 "binding 400 before X-Requested-With filter" |
| 11.19 review: `use-tagging-scaffold-data` has no retry | Merge with 1-9 "No retry after tagging-scaffold load failure" |
| 11.14 "SPA state after restore" and 11.14 review "leaves Settings while restore polls" | Merge into one |
| 11.16 review: deterministic 5xx re-posted forever | Merge with 11.16 "no cap / no expiry" |
| 11.5 `Dockerfile` base images by digest and 11.11 "S5 leftovers" | Merge into one |
| 11.19 "Local DB holds smoke-test rows" | Remove: local data, not repo state. Delete the rows or ignore them |

## 7. ACCEPTED: archive (about 35)

Each has an explicit "accepted", "by design" or "narrow/negligible" statement in the entry or its story.

- **Epic 10:** 10.2 uncapped re-evaluated set after prompt resolve; correlation can flip (by design, "not announced" is cosmetic); `Guid` tiebreak on identical timestamps; requeue not deduplicated or capped, and per-Event dedup (Ralf accepted last-write-wins, 2026-10-02; terminal rows are swept by 11.2); AI-off Households skipped (Ask First #3).
- **11.3:** temp file left after a failed-without-running job (accepted in story 3b.4); no D7 row in `local-vs-azure-deltas.md` (add if it surprises an operator).
- **11.4:** Postgres pre-check counts code points, not UTF-16 units (needs 101+ emoji in a plug name).
- **11.6:** Key Vault diagnostic settings left out on purpose; `Azure.Identity` explicit pin.
- **11.7:** per-IP `/login` key on full IPv6; in-process limiter (AD-24).
- **11.8:** no server-side revocation before the 90-day cap (AD-17 accepts it); binding 400 precedes the filter (no handler logic runs); open tab across the deploy runs the old bundle.
- **11.9:** other-tab stale state after member removal; jobs of a removed member show the generic name (`SET NULL`).
- **11.10:** `ZipArchive` central directory memory; `file.Length` check vs `MultipartBodyLengthLimit`; Eve `docProps`/directory entries quirk; temp file opened twice (tens of ms).
- **11.12:** folding `HouseholdMembershipDbContext` into `EnergyTrackerDbContext` (revisit with another change to the lookup).
- **11.13:** `AppInstances` heartbeat for AD-6 staleness (revisit with lifting AD-24); first-heartbeat warning in the Api test fixture; `InstanceHeartbeatServiceTests` real timers (revisit on a flake).
- **11.17:** `ManualTimeProvider` covers only `GetUtcNow`; `NoDirectClockReadsTests` does not cover `Stopwatch`/`TickCount` (outside AC3).
- **11.18:** the five lowered analyzer rules (`CA1848`, `CA1716`, `CA1711`, `CA1873`, `CA1707`/`CA1816` in tests). The `.editorconfig` records the reason; each keeps its "revisit if" trigger in the archive.

Text only for most of these; none contradicted the code where I looked.

## 8. OPEN: keep, add index (about 120 after merges)

Unchanged from the first triage's `[open]` set, minus the entries removed above: 18 test-coverage (minus 1 resolved), 7 a11y, 5 infra-hardening (after the pins and the `maxReplicas` guard are gone), 6 validation, 6 ux-polish, 5 perf, 3 concurrency, 2 docs, 1 tech-debt (breakpoint drift), 1 i18n.

Re-checked still open today: no `timeout-minutes` in `infra-deploy.yml` (0), no `aria-live` in `data-import-panel.tsx`, icon buttons `size-10` (`dashboard-page.tsx:159,174`), `DialogContent` has no `max-h`, `StatusSnapshotRepository` has no window, `strictPort` absent in `web/vite.config.ts`, no `.gitattributes`, OTel `AddService` bare, no LTR in `infra`, no cert alert in `main.bicep`, `pr-review.yml` what-if does not pass `placeholderImage`.

Epic 10 and 11 entries that stay open, by new theme for the Index:

- **migrations / deploy (10.1, 11.4, 11.14):** expand/contract rule is prose only; no LTR; runbook gaps; pending-migrations list timing; 529 s of 600 s migration timeout; `AddActiveJobGuard` surplus-fail step never run on production data; import file uniqueness checks.
- **auth / members (11.7, 11.8, 11.9):** HSTS defaults; IdP session outlives the 90-day cap; `POST /logout` 500 on discovery failure; plaintext `Token` column (P1); expired invite rows never swept; no invite creator; `MemberRemoval` 429 copy.
- **upload / restore memory (11.10, 11.14):** id sets grow with item count; multipart double buffering; restore materializes the graph (1250 MiB peak vs 1 GiB container); Meross CSV has no row cap; shared-strings and cell memory; GC mode for the 0.5 vCPU container (no `ServerGarbageCollector` or `GCHeapCount` setting found anywhere, so the decision handed to 11.11 looks unmade; text plus grep).
- **container / config (11.11):** digest pinning and `HEALTHCHECK`; `AllowedHosts: *`; `/login` raw JSON without OIDC; `ai-api-key` sentinel `unset` (must be fixed with Story 9.2); startup failure does not exit (P4); `oidcConfigured` and empty `JobQueue:ConnectionString`.
- **frontend state (11.14, 11.16):** server reason shown in English; 403 entries stay forever after removal by someone else; `listPending` rename; `void flushQueue` unhandled rejection; `toDateTimeLocalValue` still copied in `log-event-sheet.tsx`; multi-tab discard.
- **code quality (11.17, 11.18, 11.19):** multiple clock reads per operation; ownership token uniqueness; 26 `oxlint` warnings; `NU1900`/`NU1901` interaction; `global.json` `rollForward`; comment trim for 12 named files; two endpoint files; snapshot re-approval; tagging dialog 409 mapping and in-flight close; three repeated dialog blocks.
- **docs / process (11.15):** FR-21 editor must label "per year"; PRD FR-6 and FR-30 need the floor line; Status detail "Baseline to date" formats.
- **CI (ci-pipeline-speedup, 11.5, 11.6):** `maxParallelThreads` 8 flake watch; per-test migration cost; what-if `placeholderImage`; branch-pusher secrets; NuGet transitive pins to remove; delete revoked plaintext key rows (Ralf's call); 11.6 A2.4 second key version check.
- **epic-9 bound (stay `[open]`, tag `[epic-9]`):** AI `None` costs one call per later reading; `ai-api-key` sentinel.

## 9. Caveats

- Counts are from `grep` of entry markers; the exact numbers will settle when the edits are applied.
- ACCEPTED and Epic 11 OPEN verdicts are mostly text-only. Low-confidence ones: the GC-mode entry (no setting found, could live in a place I did not grep), and "Node 26 is LTS".
- `11-18-backend-analyzer-format-gate.md` still says `Status: in-progress` while `sprint-status.yaml` and git say `done`. Not part of this chore; flagging it.
- Apply all removals in one pass, then rebuild the Index from the surviving `[open]` and `[promoted]` entries.

## 10. Execution plan (after approval)

1. Remove the 7 RESOLVED and the 13 DUPLICATE/NOISE entries (after merges).
2. Move ACCEPTED entries to `deferred-work-accepted.md`, keeping "revisit if" text.
3. Add `[open]`/`[promoted: needs story]` markers and headings to the 4 unmarked entries.
4. Rebuild the themed Index; update the header line (date, baseline `3cf5a9b`).
5. Apply the 2 fix-now doc edits if approved.
6. Set this spec to `done`; update `sprint-status.yaml` retro action #4.

## 11. Outcome (approved by Ralf 2026-10-31)

Decisions: (1) ACCEPTED entries archived; (2) all four PROMOTE candidates go into Epic 11 as stories 11.20 (plaintext invite token column), 11.21 (self-host migration path), 11.22 (invite token out of logs), 11.23 (failed startup must exit), all `backlog`; (3) both fix-now doc edits applied; (4) committed doc-only directly on `main`.

Applied: 21 entries removed (7 resolved, 14 duplicate/merged/noise incl. the 2 fix-now entries), 33 archived (fewer than the 35 estimated), 4 promoted, 129 entries left, 3 unmarked entries now `[open]`, Index rebuilt with the Epic 10 and 11 entries under new themes. Retro action #4 closed.
