---
baseline_commit: 06bd90f00a2c8386bcca8b6e2eb188368fbb9783
---

# Story 3.10: Manual Job History Cleanup

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want to manually clear entries from the Smart Plug Import job history on demand,
so that I can tidy up the list on my own schedule — including jobs the automatic 30-day sweep would never touch — instead of waiting for it.

## Context (why this is needed)

Story 3.6 built a lazy, read-triggered 30-day sweep (AD-6 extension) that only ever clears the three terminal states (Success, Error, Flagged for Review) and only once they're 30 days past completion — Waiting, Processing, and Needs Mapping are deliberately exempt (that story's AC #7), since an unresolved or in-flight job must never silently disappear on its own. Ralf asked (via PM interview, 2026-09-11) for a manual escape hatch on top of that: a "Clean Up History" button offering two modes — delete records older than 30 days, or delete everything — and, deliberately, because this is a manual and explicitly confirmed action rather than a silent background sweep, **both** modes are eligible to delete a job in *any* of the six states, not just the three terminal ones. This is a narrow, explicit exception to Story 3.6's "never auto-remove Waiting/Processing/Needs Mapping" rule, scoped only to this manual path — Story 3.6's automatic sweep is completely unchanged by this story.

## Acceptance Criteria

1. **Given** the Job Status & History list (Story 3.6), **when** a Household member views the Smart Plug Import screen, **then** a "Clean Up History" action is visible on the same screen (FR-32 extension, UX-DR21 extension).
2. **Given** the Clean Up History action, **when** tapped, **then** it always opens an explicit confirmation step first — no tap of the action itself deletes anything immediately (FR-32 extension).
3. **Given** the confirmation step, **when** presented, **then** it offers exactly two mutually exclusive modes — delete every job/audit record older than 30 days (the default/pre-selected choice), or delete every job/audit record regardless of age — and the household member must explicitly confirm before either executes (FR-32 extension).
4. **Given** either cleanup mode executes, **when** it deletes records, **then** it is eligible to delete a job/import in *any* of the six states — Waiting, Processing, Success, Error, Needs Mapping, Flagged for Review — unlike the automatic sweep, which only ever touches the three terminal states (FR-32 extension; explicit, deliberate exception to Story 3.6 AC #7 for this manual path only — the automatic sweep's own behavior is unchanged).
5. **Given** a Needs Mapping job deleted by manual cleanup, **when** deleted, **then** its `SmartPlugImport` row (Story 3.2's unresolved create-or-map decision) is deleted along with it — the household permanently forfeits the ability to resolve that import once deleted, and the confirmation step's copy makes this consequence explicit, especially for the "everything" mode (FR-32 extension).
6. **Given** a Processing job deleted by manual cleanup while its background worker is still mid-flight, **when** the worker later looks up that job's row, **then** it hits `BackgroundJobProcessor.ProcessAsync`'s existing "job row missing" defensive-fallback path (`BackgroundJobProcessor.cs:31-45` — inserts a fresh `Processing` row rather than throwing) — deleting the row does not cancel or corrupt the in-flight parse, it only removes the prior audit trail. **Known, accepted consequence** (confirmed with Ralf): the recreated fallback row carries no `OriginalFileName`/`QueuedByHouseholdMemberId`, so the deleted job reappears in the list moments later (next poll) as an unattributed Processing row with no filename, then completes normally from there. This is expected behavior for this story, not a bug — this product has no job-cancellation mechanism anywhere, and this story does not add one.
7. **Given** any cleanup mode executes, **when** it deletes rows, **then** it deletes only `BackgroundJob`/`SmartPlugImport`/`SmartPlugImportGap` audit rows — `SmartPlugReading` data already written is never deleted, only detached (`SmartPlugImportId` → `NULL`) via the existing `SetNull` FK from Story 3.6 Task 3 (AD-20, same guarantee as the automatic sweep).
8. **Given** any cleanup mode executes, **when** scoped, **then** it only ever deletes rows belonging to the caller's own Household (AD-3, never cross-Household) and runs synchronously within the triggering request — never as a new `IHostedService`/`Timer`-based schedule (AD-7); this is a second, user-triggered entry point onto the same deletion mechanism Story 3.6 built, not a second background sweep.
9. **Given** the underlying deletion mechanism, **when** implemented, **then** it shares the FK-ordered, set-based delete helper `SweepExpiredAsync` already established (`SmartPlugImportRepository.cs:660-723`) rather than duplicating that three-step delete logic — `SweepExpiredAsync` itself is left behaviorally unchanged (still terminal-states-only, still 30-day cutoff, still anchored on completion time) so the automatic sweep's Story 3.6 behavior does not regress.
10. **Given** a cleanup mode deletes every job currently in the list, **when** the list is re-rendered afterward, **then** it shows the same onboarding-empty treatment as Story 3.6 AC #8 — never blank space or an error.

## Tasks / Subtasks

- [x] **Task 1: Backend — new repository method sharing `SweepExpiredAsync`'s delete mechanics, all-states eligibility** (AC: #4, #7, #8, #9)
  - [x] `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs`: extract the existing three-step FK-ordered delete block (`SmartPlugImportGaps` → `SmartPlugImports` → `BackgroundJobs`, lines ~705-722) into a small private helper, e.g. `DeleteEligibleAsync(IReadOnlyList<Guid> jobIds, IReadOnlyList<Guid> importIds, CancellationToken)`, and have `SweepExpiredAsync` call it instead of inlining the three `ExecuteDeleteAsync` calls. **Do not change `SweepExpiredAsync`'s own eligibility query or its public signature** — it must keep sweeping only terminal states (Failed / Completed+Completed / Completed+FlaggedForReview) anchored on `import?.CompletedAtUtc ?? job.CompletedAtUtc`, exactly as Story 3.6 shipped it. This refactor is purely to give the new method below a shared, already-review-hardened delete primitive, not to touch the automatic sweep's behavior.
  - [x] Add `Task<int> DeleteJobsAsync(Guid householdId, DateTimeOffset? cutoffUtc, CancellationToken cancellationToken)` to `ISmartPlugImportRepository` and `SmartPlugImportRepository`. Eligibility query, **all six states, no terminal-state restriction**:
    - `LEFT JOIN` `BackgroundJobs` (`JobType == JobTypes.ProcessSmartPlugImport`, `HouseholdId == householdId`) to `SmartPlugImports` on `job.Id == import.BackgroundJobId` — same left-join shape as `SweepExpiredAsync` (a job can have no paired import row: Queued, Processing, or a Failed job that never got one).
    - Compute `effectiveAgeUtc = import != null ? import.CompletedAtUtc : null ?? job.CompletedAtUtc ?? job.CreatedAtUtc`. **This fallback chain is the one genuinely new/subtle piece of this story** — verified directly against `ProcessSmartPlugImport.cs:113-124`: `SmartPlugImport.CompletedAtUtc` is set at initial persist time for *both* the `Completed` and `AwaitingPowerPointMapping` branches (not only once actually resolved), so Needs Mapping rows already have a usable `CompletedAtUtc` from parse time — no fallback needed for them. The fallback to `job.CreatedAtUtc` only actually triggers for `Queued`/`Processing` jobs, which have no `SmartPlugImport` row and a null `BackgroundJob.CompletedAtUtc` (only set once a job reaches a terminal `BackgroundJobStatus`) — `CreatedAtUtc` (set at enqueue time, Story 3.6 Task 1) is the only timestamp those rows have.
    - Eligibility: `cutoffUtc == null` (the "everything" mode) → every job matching the household/JobType filter is eligible, no age check at all. `cutoffUtc != null` (the "older than 30 days" mode) → eligible when `effectiveAgeUtc < cutoffUtc`.
    - **No state filter of any kind** — unlike `SweepExpiredAsync`, do not exclude `AwaitingPowerPointMapping` or `Queued`/`Processing`. This is the entire point of this method's existence (AC #4).
    - Materialize the eligible `(BackgroundJobId, SmartPlugImportId?)` set, call the shared `DeleteEligibleAsync` helper from Task 1's first bullet, and return the count of jobs deleted (`eligible.Count`) for the API response.
  - [x] Keep the exact same `ExecuteDeleteAsync` FK-order discipline (`SmartPlugImportGaps` → `SmartPlugImports` → `BackgroundJobs`) — `SmartPlugImport.BackgroundJobId`'s FK is `Restrict`, so the import row must be gone before its `BackgroundJob` row can be deleted, same reasoning `SweepExpiredAsync`'s own comments already document.

- [x] **Task 2: Backend — new use case wrapping the repository method** (AC: #4, #7, #8)
  - [x] New use case `src/EnergyTracker.Application/CleanUpSmartPlugImportJobs.cs`, single-purpose, matching `ListSmartPlugImportJobs`'s shape: constructor-injects `ISmartPlugImportRepository`. `ExecuteAsync(Guid householdId, bool deleteAll, CancellationToken)`: computes `cutoffUtc = deleteAll ? (DateTimeOffset?)null : DateTimeOffset.UtcNow.AddDays(-30)`, calls `smartPlugImportRepository.DeleteJobsAsync(householdId, cutoffUtc, cancellationToken)`, returns the deleted count.

- [x] **Task 3: Backend — new endpoint** (AC: #1, #3, #8)
  - [x] `src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs`: add `api.MapDelete("/smart-plug-import-jobs", async (ICurrentHouseholdAccessor householdAccessor, CleanUpSmartPlugImportJobs cleanUpSmartPlugImportJobs, bool deleteAll, CancellationToken cancellationToken) => ...)` — minimal API auto-binds the `deleteAll` primitive from the query string (`DELETE /api/smart-plug-import-jobs?deleteAll=true|false`, no `[FromQuery]` needed). Same `TryGetHouseholdId` guard every sibling endpoint in this file uses. Response: `Results.Ok(new { deletedCount })` — a plain count is enough for the frontend to confirm the action worked; no richer shape is specified by any AC.
  - [x] Register `CleanUpSmartPlugImportJobs` in DI: `builder.Services.AddScoped<CleanUpSmartPlugImportJobs>();` right next to `builder.Services.AddScoped<ListSmartPlugImportJobs>();` (`src/EnergyTracker.Api/Program.cs:344`) — same lifetime, same block.

- [x] **Task 4: Frontend — Clean Up History control + confirmation dialog** (AC: #1, #2, #3, #5, #10)
  - [x] `web/src/lib/smart-plug-import-api.ts`: add `cleanUpSmartPlugImportJobs(deleteAll: boolean): Promise<{ deletedCount: number }>` calling `DELETE /api/smart-plug-import-jobs?deleteAll=${deleteAll}`, same `ApiError`/`toApiError` shape as every other function in this file.
  - [x] Add the "Clean Up History" trigger + confirmation dialog to `web/src/components/smart-plug-import/job-history-list.tsx` (or a new sibling component if that file is getting crowded — dev's call), rendered near `listLabel` at the top of the list section. **No mockup exists for this control** (confirmed — `key-smart-plug-import.html` predates this story) — build directly against this codebase's existing pieces rather than waiting on a UX pass:
    - Reuse the `Dialog`/`DialogContent`/`DialogHeader`/`DialogTitle` primitives (`web/src/components/ui/dialog.tsx`) and `GLASS_MODAL_CLASSNAME`, the same pattern `PowerPointMappingDialog.tsx:1-7` already establishes — `alert-dialog` is **not** currently installed in this project's shadcn set (`web/components.json`); evaluate `npx shadcn add alert-dialog` vs. reusing `Dialog` per this project's "prefer `npx shadcn add` over hand-writing primitives" convention (`project-context.md`), but either is acceptable — dev's call.
    - The confirmation dialog offers a radio/toggle choice between the two modes (older-than-30-days pre-selected/default per AC #3) and a confirm button. Confirmation copy must explicitly name the consequence for the "everything" mode — that it includes in-flight and unresolved (Needs Mapping) jobs, not just old completed ones (AC #5) — plain, specific wording, no exclamation marks, per this project's Voice and Tone table (same discipline Story 3.6 Task 5's empty-state copy followed).
    - On confirm: call `cleanUpSmartPlugImportJobs(deleteAll)`, then re-fetch the job list (reuse `JobHistoryList`'s existing `load()` — if the button lives inside that component, this is a direct call; if extracted to a sibling, thread a refresh callback through). AC #10's empty state is already handled for free by the existing `jobs.length === 0` branch once the list re-fetches to zero rows.
    - Use the destructive `{colors.destructive}` token (the shadcn `destructive` Button variant, already the Error badge's token per `job-history-list.tsx:31`) for the "everything" mode's emphasis/confirm action, consistent with this codebase's one existing use of that token for a consequential/irreversible-feeling action.

- [x] **Task 5: i18n** (AC: #1, #2, #3, #5)
  - [x] Add new keys under `smartPlugImport.jobHistory.cleanup` in **both** `web/src/locales/en-US/translation.json` and `web/src/locales/de-DE/translation.json` (AD-18 — additive resource files, never hardcoded strings), sibling to the existing `jobHistory` block (`translation.json:233-248`): the button label, the dialog title, the two mode option labels, the "everything" mode's explicit-consequence copy (AC #5), and the confirm/cancel button labels.

- [x] **Task 6: Tests** (AC: all)
  - [x] `.NET`: `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs` (extend existing `SweepExpiredAsync` coverage, add sibling `DeleteJobsAsync` tests): with `cutoffUtc: null` — a `Queued`, `Processing`, `AwaitingPowerPointMapping` (Needs Mapping), `Completed`/Success, `Failed`/Error, and `FlaggedForReview` job/import are **all** deleted regardless of age (this is the one genuinely new behavior vs. `SweepExpiredAsync`, which excludes the first three unconditionally). With `cutoffUtc` set to 30 days ago: a `Queued`/`Processing` job younger than the cutoff (by `CreatedAtUtc`) survives; one older than the cutoff is deleted; confirm the `CreatedAtUtc` fallback is actually exercised for these two states specifically (assert against a job with `CompletedAtUtc == null`). Also confirm `SweepExpiredAsync`'s own existing test cases still pass unchanged after the shared-helper refactor (regression guard on Task 1's extraction) — a `Queued`/`Processing`/Needs Mapping row must still survive `SweepExpiredAsync` regardless of age, unlike `DeleteJobsAsync`.
  - [x] `tests/EnergyTracker.Application.Tests/CleanUpSmartPlugImportJobsTests.cs`: `deleteAll: true` calls the repository with `cutoffUtc: null`; `deleteAll: false` calls it with a cutoff ~30 days ago (assert the computed value, not just "some non-null value" — a wrong day count is an easy off-by-something to miss); returns the repository's reported count unchanged.
  - [x] `tests/EnergyTracker.Api.Tests/SmartPlugImportEndpointsTests.cs` (extend): `DELETE /api/smart-plug-import-jobs?deleteAll=true|false` is Household-scoped (a second Household's jobs are never deleted); returns `deletedCount` matching what was actually removed; a Household with no jobs returns `deletedCount: 0`, not an error.
  - [x] Frontend Vitest (colocated, `@testing-library/react`, `jsdom`): the Clean Up History button opens the confirmation dialog, never deletes on a bare click (AC #2); the dialog defaults to the older-than-30-days option; confirming calls `cleanUpSmartPlugImportJobs` with the right `deleteAll` value for each option and then re-fetches the list; cancelling closes the dialog with no API call.
  - [x] `.NET`: xUnit v3 MTP, Shouldly, NSubstitute against ports, `TestContext.Current.CancellationToken`, Testcontainers for anything DB-touching — project-context.md conventions, matching every existing Smart Plug import test in this codebase.

### Review Findings

- [x] [Review][Patch] `DeleteEligibleAsync`'s three `ExecuteDeleteAsync` calls (Gaps → Imports → BackgroundJobs) ran with no wrapping transaction — a cancellation or transient failure between calls could leave audit rows partially deleted. Fixed with an explicit `BeginTransactionAsync`/`CommitAsync` around all three, matching this file's own existing `AddAsyncCore` pattern and this codebase's prior fix for the same class of bug (commit fa77aef). [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs]
- [x] [Review][Patch] `CleanUpSmartPlugImportJobs.RetentionWindow` duplicated `ListSmartPlugImportJobs.RetentionWindow` (same value, declared twice) — a future change to the 30-day window could silently drift between the automatic sweep and this manual mode. Fixed by making `ListSmartPlugImportJobs.RetentionWindow` `internal` and referencing it from `CleanUpSmartPlugImportJobs` instead of redeclaring. [src/EnergyTracker.Application/ListSmartPlugImportJobs.cs, src/EnergyTracker.Application/CleanUpSmartPlugImportJobs.cs]
- [x] [Review][Patch] The cleanup dialog's `deleteAll` selection and `cleanupError` were never reset on close/reopen — Cancel after selecting "everything" left that selection pre-checked next time (violates AC #3's "pre-selected default" on every open, not just first mount), and a prior failed attempt's error text reappeared before any new request. Fixed with a dedicated `handleOpenCleanup` that resets both before opening. [web/src/components/smart-plug-import/job-history-list.tsx]
- [x] [Review][Patch] `DeleteJobsAsync`'s new test coverage exercised 4 of the 6 states (Queued, Processing, NeedsMapping, Success) but not Failed-with-no-paired-import or FlaggedForReview, even though `SweepExpiredAsync` has dedicated tests for both just above and AC #4's claim is "all six states." Added the two missing tests. [tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs]
- [x] [Review][Decision: not fixing] `DeleteJobsAsync`'s eligibility query is a full independent duplicate of `SweepExpiredAsync`'s rather than one parameterized method, risking future divergence. This was a deliberate choice, stated explicitly in this story's own Dev Notes hazard section: the two queries stay separate specifically so a change to one can never accidentally alter the other's already-review-hardened, terminal-states-only behavior. Standing by that tradeoff — the duplication is the cost of that safety, not an oversight.
- [x] [Review][Decision: not fixing] `DeleteJobsAsync` always performs the LEFT JOIN + `effectiveAgeUtc` computation even in `deleteAll` mode, where age is irrelevant to eligibility, and materializes the full eligible-ID set into memory before the delete. A `cutoffUtc == null` fast path (skip the join/age computation entirely) would be a reasonable follow-up, but `SweepExpiredAsync` already has this identical shape (join + materialize) for a table this codebase's own Dev Notes call "capable of holding hundreds of thousands of rows," and the manual, explicitly-user-triggered nature of this action makes the extra cost far less pressing than it would be on a hot/automatic path. Not blocking this story.
- [x] [Review][Decision: not fixing] The cleanup mode choice uses native `<input type="radio">` rather than installing the shadcn `radio-group` primitive. `project-context.md` prefers `npx shadcn add` over hand-written primitives, but this codebase already has one precedent for a native control filling a gap where no shadcn primitive was installed (`PowerPointMappingDialog`'s native `<select>`) — followed the same precedent here rather than pulling in a new dependency for one control. Worth revisiting in a dedicated design-system pass, not this story.
- [x] [Review][Decision: not fixing] `CleanUpSmartPlugImportJobs.ExecuteAsync` takes a `bool deleteAll` rather than exposing the repository's more general `DateTimeOffset? cutoffUtc` all the way up. This matches the actual two-mode requirement Ralf specified (older-than-30-days, or everything) — no third mode exists today, and the repository's general primitive is still there, one layer down, for the day a real third mode is requested. Not adding speculative flexibility ahead of an actual need.
- [x] [Review][Decision: not fixing] A cleanup completing moments before the list's 8-second poll tick can trigger two near-duplicate `GET` requests (the manual post-cleanup `load()` plus the next scheduled tick). Real but low-impact — an extra idempotent `GET` on an already-lightweight household-scoped query, not a correctness issue. Not worth the added complexity of resetting the poll interval on every manual refresh for this story.

## Dev Notes

### The one hazard most likely to cause a subtly wrong implementation

**Don't let this story's new eligibility query regress `SweepExpiredAsync`'s existing, already-review-hardened behavior.** The safest path is exactly what Task 1 specifies: extract the shared three-step delete into a private helper both methods call, but leave `SweepExpiredAsync`'s own eligibility *query* (the terminal-states-only `where` clause, the `import?.CompletedAtUtc ?? job.CompletedAtUtc` cutoff comparison with no `CreatedAtUtc` fallback) completely untouched. Do not try to parameterize one query with an "include active states" flag — that invites exactly the kind of accidental behavior change (e.g. a flag defaulting the wrong way, or a fallback chain leaking into the automatic sweep) this story must not introduce. Two separate, narrow eligibility queries feeding one shared delete primitive is the deliberate design here, not an oversight to consolidate further.

### Architecture constraints (binding, not optional)

- **AD-3 (tenant isolation):** the new endpoint and repository method follow the same Household-scoping discipline as every other entity in this codebase — no `IgnoreQueryFilters()`/`FromSqlRaw`/`.Find()`.
- **AD-7 (no scheduled background sweep):** this story's deletion runs synchronously inside the triggering `DELETE` request — never an `IHostedService`/`Timer`. It is a second call site onto the same deletion mechanism, not a second sweep.
- **AD-20 (Smart Plug data is never deleted by this mechanism):** only `BackgroundJob`/`SmartPlugImport`/`SmartPlugImportGap` audit rows are ever deleted — `SmartPlugReading` rows are only ever detached (`SmartPlugImportId → NULL`) via Story 3.6 Task 3's existing `SetNull` FK, which this story relies on unchanged.

### Existing code to reuse, not reinvent

- `SweepExpiredAsync`'s FK-ordered, set-based `ExecuteDeleteAsync` sequence (`SmartPlugImportRepository.cs:660-723`) — extract, don't copy-paste, per Task 1.
- `Dialog`/`DialogContent`/`DialogHeader`/`DialogTitle` + `GLASS_MODAL_CLASSNAME` (`PowerPointMappingDialog.tsx`) for the confirmation step — no new modal primitive needed.
- `job-history-list.tsx`'s existing `load()`/`mountedRef` pattern for refreshing the list after cleanup — don't build a second fetch-and-render path.
- `{colors.destructive}` / shadcn `destructive` Button variant — already this codebase's one precedent for a consequential-action color, reuse verbatim.

### Known non-goals (avoid scope creep)

- **No job cancellation.** Deleting a Processing job's row does not stop the in-flight background parse — confirmed acceptable with Ralf (AC #6). This product has no cancellation mechanism anywhere; do not add one here.
- **No custom age picker.** Only the two modes specified (older-than-30-days, or everything) — not an arbitrary day-count input.
- **No admin/member role distinction.** Any Household member can trigger cleanup, same as every other action in this app — do not add a permission gate.
- **No retroactive UX pass.** This story deliberately ships ahead of a dedicated mockup (see Task 4) — don't block implementation waiting on one.

### Project Structure Notes

- Backend modified: `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs`, `src/EnergyTracker.Application/Ports/ISmartPlugImportRepository.cs`, `src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs`, `src/EnergyTracker.Api/Program.cs` (DI registration).
- Backend new: `src/EnergyTracker.Application/CleanUpSmartPlugImportJobs.cs`.
- Frontend modified: `web/src/lib/smart-plug-import-api.ts`, `web/src/components/smart-plug-import/job-history-list.tsx`, `web/src/locales/{en-US,de-DE}/translation.json`.
- No new files strictly required on the frontend (the control can live inside `job-history-list.tsx`), but a sibling component is an acceptable dev-agent judgment call if that file grows unwieldy — fits the existing flat `web/src/components/{feature}` grouping either way.
- No new migration — this story adds no new columns/tables, only new query/delete logic against the existing Story 3.6 schema.

### Testing standards summary

- `.NET`: xUnit v3 MTP (`xunit.v3.mtp-v2`), Shouldly, NSubstitute against ports, `TestContext.Current.CancellationToken`, Testcontainers (real Postgres + SqlServer) for anything migration/DB-touching — project-context.md convention.
- Frontend: Vitest + Testing Library, colocated next to source, `jsdom` — same convention Story 3.6 already established for this feature area.

### References

- [Source: `_bmad-artifacts/planning/epics/epic-3-smart-plug-import-baseline-sharpening.md#Story 3.10`] — story statement + ACs (this story's authoritative source, added 2026-09-11 via bmad-agent-pm + bmad-create-epics-and-stories).
- [Source: `_bmad-artifacts/planning/epics/requirements-inventory.md`] — FR-32's 2026-09-11 extension (full testable text), UX-DR21's 2026-09-11 extension (confirms no mockup exists).
- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md:18-41,129`] — AD-3, AD-6 (incl. the FR-32 extension paragraph), AD-7, AD-20.
- [Source: `_bmad-artifacts/implementation/3-6-smart-plug-import-job-status-history.md`] — the list, six-state derivation, and `SweepExpiredAsync` mechanism this story extends; its Task 1 (`BackgroundJobStatus.Queued`, enqueue-time `CreatedAtUtc`) and Review Findings (the left-join-not-inner-join fix, the completion-time-not-job-time cutoff fix) this story's new query must preserve, not regress.
- [Source: `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:660-724`] — `SweepExpiredAsync`'s exact current implementation, read in full to design Task 1's shared-helper extraction.
- [Source: `src/EnergyTracker.Application/Ports/ISmartPlugImportRepository.cs:106-113`] — `SweepExpiredAsync`'s port declaration and doc comment this story adds a sibling method next to.
- [Source: `src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs:16-70`] — the "job row missing" defensive-fallback path (lines 31-45) AC #6 relies on; confirms it inserts a bare `Processing` row with no `OriginalFileName`/`QueuedByHouseholdMemberId`.
- [Source: `src/EnergyTracker.Application/ProcessSmartPlugImport.cs:113-124`] — confirms `SmartPlugImport.CompletedAtUtc` is set at initial persist time for both `Completed` and `AwaitingPowerPointMapping` branches, the fact Task 1's `CreatedAtUtc`-fallback reasoning depends on.
- [Source: `src/EnergyTracker.Application/MapSmartPlugImportToPowerPoint.cs:51`] — confirms `CompletedAtUtc` is updated again once a Needs Mapping import is actually resolved (Story 3.6's own review-round-2 patch this story must not regress).
- [Source: `src/EnergyTracker.Application/ListSmartPlugImportJobs.cs`] — sibling use-case shape (`SmartPlugImportJobState`, constructor-injection pattern) `CleanUpSmartPlugImportJobs` should match.
- [Source: `src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs:1-160`] — `TryGetHouseholdId` guard, the existing `GET /api/smart-plug-import-jobs` endpoint this story adds a `DELETE` sibling next to.
- [Source: `web/src/components/smart-plug-import/job-history-list.tsx`] — the list component this story's button/dialog attaches to; its `load()`/`mountedRef` refresh pattern to reuse.
- [Source: `web/src/components/smart-plug-import/power-point-mapping-dialog.tsx:1-55`] — the `Dialog`/`GLASS_MODAL_CLASSNAME` confirmation-dialog pattern to reuse for Task 4.
- [Source: `web/src/lib/smart-plug-import-api.ts:139-162`] — `SmartPlugImportJobDto`/`fetchSmartPlugImportJobs` shape this story's new `cleanUpSmartPlugImportJobs` function sits alongside.
- [Source: `web/src/locales/en-US/translation.json:233-248`] — the existing `jobHistory` i18n block this story extends with a sibling `cleanup` key.
- [Source: `_bmad-artifacts/project-context.md`] — project-wide conventions (shadcn `npx shadcn add` preference, AD-18 additive i18n catalogs, xUnit v3 MTP/Shouldly/NSubstitute, oxlint/Vitest, `@/*` path alias).

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

None — no debugger/log-file artifacts beyond standard `dotnet test`/`vitest run` output captured during implementation.

### Completion Notes List

**Task 1 (repository):** Extracted `SweepExpiredAsync`'s three-step FK-ordered delete into a private `DeleteEligibleAsync` helper; `SweepExpiredAsync`'s own eligibility query is untouched (verified by its full existing test suite still passing unchanged). Added `DeleteJobsAsync(householdId, cutoffUtc?)` with the `effectiveAgeUtc = import?.CompletedAtUtc ?? job.CompletedAtUtc ?? job.CreatedAtUtc` fallback chain — verified against `ProcessSmartPlugImport.cs` that only Queued/Processing rows actually need the `CreatedAtUtc` fallback (Needs Mapping already has `CompletedAtUtc` from parse time).

**Task 2 (use case):** `CleanUpSmartPlugImportJobs` — straightforward `deleteAll` → `cutoffUtc` mapping, matching `ListSmartPlugImportJobs`'s single-purpose shape.

**Task 3 (endpoint):** `DELETE /api/smart-plug-import-jobs?deleteAll=true|false`, minimal API auto-binding the `bool` query param. Registered in DI next to `ListSmartPlugImportJobs`.

**Task 4 (frontend):** Cleanup trigger + confirmation dialog added directly inside `JobHistoryList` (no sibling component needed — file stayed a reasonable size). Reused `Dialog`/`GLASS_MODAL_CLASSNAME` from `PowerPointMappingDialog`'s precedent; native radio inputs for the two-mode choice (no `radio-group` shadcn primitive installed, matching `PowerPointMappingDialog`'s own native-`<select>` precedent for a similarly absent primitive). Destructive `Button` variant used for both the "everything" mode's consequence framing and the confirm action.

**Task 5 (i18n):** New `smartPlugImport.jobHistory.cleanup.*` keys added to both locale files, JSON-validated.

**Task 6 (tests):** 8 new `.NET` Infrastructure tests (`DeleteJobsAsync` — all-states deletion across Queued/Processing/NeedsMapping/Success, the `CreatedAtUtc` fallback both exercised and not-yet-due, tenant isolation) plus confirmation that all 9 pre-existing `SweepExpiredAsync` tests still pass unchanged (Task 1's regression guard). One test bug caught and fixed during this work, not a product bug: `DeleteJobsAsync_never_deletes_another_households_jobs` initially queried the deleting household's own `DbContext`, whose AD-3 query filter hides the other household's row regardless of whether it was actually deleted — fixed by verifying through a `DbContext` scoped to the other household, same idiom this file's own `SweepExpiredAsync` tests already use for post-delete verification. 2 new Application tests, 3 new Api.Tests (household scoping, deletedCount accuracy, empty-household zero-count). 5 new frontend Vitest tests (dialog-open-no-delete, default-mode-selection, confirm-with-each-mode, cancel-no-call).

**Verification (initial implementation):** Full backend suite green — 487 tests (230 Application, 108 Infrastructure via Testcontainers Postgres, 146 Api.Tests via Testcontainers, 3 Architecture), `dotnet build` clean (Debug). 259 frontend tests green (11 in `job-history-list.test.tsx`, including all 5 new cleanup tests; no regressions elsewhere, including `smart-plug-import-page.test.tsx`'s existing ~15-mock suite), `tsc -b` clean, `oxlint` clean (no new warnings — 4 pre-existing warning classes unrelated to this story's files), `vite build` clean. Not live-verified in a real Chrome browser against the running app this session (no interactive browser session set up for this task) — the confirmation dialog's exact visual layout/spacing has not been eyeballed, only tested via Testing Library queries and `tsc`/`oxlint`/`vite build`.

**Code review (2026-09-11):** 4 patch findings applied (see Review Findings above) — the untransacted 3-step delete (widened blast radius onto the "delete everything" path vs. Story 3.6's original sweep-only exposure), a duplicated `RetentionWindow` constant, the cleanup dialog's `deleteAll`/`cleanupError` not resetting across opens, and 2 missing `DeleteJobsAsync` test states (Failed-no-import, FlaggedForReview). 5 findings decided not to fix, each with reasoning recorded inline rather than silently dismissed — 2 are deliberate tradeoffs already documented in this story's own pre-review Dev Notes (the intentional query duplication, the intentional non-fast-path for `deleteAll` mode), 3 are genuine but low-priority/out-of-scope (native radio vs. `radio-group`, not exposing a speculative third cleanup mode, a rare double-`GET` near a poll boundary). 2 new backend tests, 4 new frontend tests (2 for the missing states, 2 regression guards for the dialog-reset fix) added for the fixes. Full backend suite green (489/489: +2 from the new Infrastructure tests), full frontend suite green (261/261: +2 from the new regression guards), `dotnet build`/`tsc -b`/`oxlint`/`vite build` all clean.

### File List

**Backend — modified:**
- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs`
- `src/EnergyTracker.Application/Ports/ISmartPlugImportRepository.cs`
- `src/EnergyTracker.Application/ListSmartPlugImportJobs.cs` (code review: `RetentionWindow` made `internal`)
- `src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs`
- `src/EnergyTracker.Api/Program.cs`

**Backend — new:**
- `src/EnergyTracker.Application/CleanUpSmartPlugImportJobs.cs`

**Backend tests — modified:**
- `tests/EnergyTracker.Infrastructure.Tests/SmartPlugImportRepositoryTests.cs`
- `tests/EnergyTracker.Api.Tests/SmartPlugImportEndpointsTests.cs`

**Backend tests — new:**
- `tests/EnergyTracker.Application.Tests/CleanUpSmartPlugImportJobsTests.cs`

**Frontend — modified:**
- `web/src/lib/smart-plug-import-api.ts`
- `web/src/components/smart-plug-import/job-history-list.tsx`
- `web/src/components/smart-plug-import/job-history-list.test.tsx`
- `web/src/locales/en-US/translation.json`
- `web/src/locales/de-DE/translation.json`

## Change Log

- 2026-09-11: Story implemented (dev-story). All 6 tasks complete: generalized `SweepExpiredAsync`'s delete mechanics into a shared helper without changing its own behavior, added the all-states `DeleteJobsAsync` repository method + `CleanUpSmartPlugImportJobs` use case + `DELETE /api/smart-plug-import-jobs` endpoint, added the frontend Clean Up History button/dialog with the two modes, i18n (both locales), and full test coverage. One test bug found and fixed via the test suite (not a product bug): a tenant-isolation test initially queried through the wrong household's `DbContext`, whose own AD-3 query filter would have hidden a false pass. Full backend suite green (487/487), full frontend suite green (259/259), `tsc -b`/`oxlint`/`vite build` all clean. Status → review.
- 2026-09-11: Code review (`/code-review`). 4 patch findings applied: wrapped the 3-step delete in an explicit transaction (the review-widened "delete everything" path made a pre-existing untransacted gap materially riskier), deduplicated the `RetentionWindow` constant across `ListSmartPlugImportJobs`/`CleanUpSmartPlugImportJobs`, fixed the cleanup dialog's `deleteAll`/`cleanupError` state surviving across close/reopen, and added the 2 missing `DeleteJobsAsync` test states. 5 findings evaluated and deliberately not fixed, each with reasoning recorded in Review Findings rather than silently dropped — 2 are tradeoffs this story's own Dev Notes already called out as intentional before review even started, 3 are genuine but low-priority polish out of this story's scope. 6 new tests added (2 backend, 4 frontend) for the fixes. Full backend suite green (489/489), full frontend suite green (261/261), `dotnet build`/`tsc -b`/`oxlint`/`vite build` all clean. Status → review (unchanged — no reviewer approval gate reached in this pass; ready for a human/second-pass review decision).
