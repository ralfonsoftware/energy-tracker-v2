---
baseline_commit: 609a7d36842d9976f66f2841a1472cb0ff587eb4
---

# Story 3.5: Dual Entry Points & Multi-File Import Queuing

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want to start a Smart Plug import from wherever I already am — Dashboard or Trend History — and queue several files at once,
so that I don't have to detour through Settings, and uploading a batch of exports doesn't mean waiting for each one before starting the next.

## Context (why this is needed)

FR-4 was amended 2026-08-26 (see `.memlog.md`, `prd/9-assumptions-index.md:10`) to move the Smart Plug Import entry point off Settings onto both Dashboard and Trend History, and to accept multiple files in one action. Today's implementation (Story 3.1, unchanged since) is Settings-only and strictly one file at a time — `SmartPlugImportPanel` holds a single `ImportState` value, a single `fileName`, and a single `jobIdRef`; there is no concept of more than one in-flight import.

**Read the "Trend History does not exist yet" note in Dev Notes before touching anything** — this is the single biggest trap in this story.

## Acceptance Criteria

1. **Given** the Dashboard screen, **when** rendered, **then** a Smart Plug Import entry point (icon button) is visible on it, and Settings no longer hosts a separate upload entry (FR-4 amendment, UX-DR20).
2. **Given** the Trend History screen does not exist yet in this codebase (Epic 4, still `backlog`), **when** this story ships, **then** the Dashboard entry point is the only one built now; the Trend History entry point is added as part of whichever Epic 4 story first builds that screen, not retrofitted here (see Dev Notes).
3. **Given** the Dashboard entry point is tapped, **when** the screen opens, **then** it is a new, dedicated Smart Plug Import screen — not the Settings-embedded panel — so that when Trend History is later built (AC #2), its entry point can open the exact same screen (FR-4, UX-DR12).
4. **Given** the file picker/dropzone on that screen, **when** a Household member selects or drops multiple files in one action, **then** each file is enqueued as its own independent `BackgroundJob`/`SmartPlugImport` job immediately — all uploads fire concurrently, never awaited one-by-one before the next starts (FR-4).
5. **Given** several files enqueued in one action, **when** one file fails to parse or needs Power Point mapping, **then** the other files' processing is unaffected — one file's outcome never blocks or cancels another's (FR-4).
6. **Given** a newly enqueued job, **when** the queue is rendered, **then** it appears immediately with a Waiting or Processing indicator for that action's files — the UI reflects queue state without waiting for any file to complete (FR-4, AD-6).
7. **Given** the existing single-file async behavior from Story 3.1, **when** multiple files are involved, **then** each file's async lifecycle (upload confirms immediately, parsing runs in background, completion polled via `GET /api/jobs/{id}`) is unchanged per file — this story only adds starting several at once, not a new processing model (AD-6).

## Tasks / Subtasks

- [x] **Task 1: New shared Smart Plug Import screen — move off Settings** (AC: #1, #3)
  - [x] Add a new `view` variant to `web/src/App.tsx`'s local view-state union (`'dashboard' | 'settings' | 'history' | 'smartPlugImport'`) — this app has no `react-router` (Story 1.5's deferred decision, still standing); follow the exact existing pattern for `'history'`/`MeterReadingHistoryPage` (`App.tsx:247-249`): a new `if (view === 'smartPlugImport') return <SmartPlugImportPage onBack={() => setView('dashboard')} />` branch.
  - [x] Create `web/src/components/smart-plug-import/smart-plug-import-page.tsx` — a new full-screen wrapper (topbar with a back affordance + page title, mirroring `MeterReadingHistoryPage`'s and `SettingsPage`'s existing `onBack` prop shape) that renders the multi-file queue UI (Task 2). This is the "same shared Smart Plug Import screen regardless of which entry point was tapped" AC #3 requires — Trend History's future entry point (AC #2) will route to this exact same page.
  - [x] Remove `<SmartPlugImportPanel />` and its import from `web/src/components/settings/settings-page.tsx:6,34` — AC #1 explicitly requires Settings no longer hosts a separate upload entry.
  - [x] Add the icon-button entry point to `DashboardPage` (`web/src/components/dashboard/dashboard-page.tsx`). **This screen currently has no topbar at all** — just `<h1>{t('app.title')}</h1>` directly above the `StatusCard` (`dashboard-page.tsx:117-118`). Add a topbar row (`flex justify-between items-center`) containing the existing `<h1>` and a new 40×40 icon-only button (`aria-label`, `title` = a new `smartPlugImport.entryPointLabel` translation key), calling a new `onSmartPlugImportClick` prop threaded the same way `onSettingsClick`/`onHistoryClick` already are (`dashboard-page.tsx:19-27` → `App.tsx` render call at `App.tsx:251-260`, wire `onSmartPlugImportClick={() => setView('smartPlugImport')}`).
  - [x] Reuse `nav-chrome-active-bg`/`nav-chrome-active-foreground` Tailwind tokens (already defined and used by `NavChrome`'s `ACTIVE_CLASSNAME`, `nav-chrome.tsx:14`) for the new icon button's resting style — UX-DR20 requires this exact token reuse ("reusing `{components.nav-chrome.active-bg}` / `-active-foreground` verbatim... chrome/interactive, never a status color"), not a new color.
  - [x] Use a `lucide-react` icon consistent with the mockup's upload-arrow glyph (`mockups/key-trend-history.html:305` uses an up-arrow-into-tray path — `Upload` from `lucide-react` is the closest existing icon in this codebase's icon set already in use elsewhere, e.g. `Plus`/`Settings`/`Home` imports in `nav-chrome.tsx`/`dashboard-page.tsx`).

- [x] **Task 2: Multi-file queue UI — rewrite `SmartPlugImportPanel`'s single-file state machine** (AC: #4, #5, #6, #7)
  - [x] **This is a full rewrite of the state shape, not an incremental patch.** Today: `useState<ImportState>` (one value), `fileName: string | null`, `jobIdRef`, `smartPlugImportIdRef`, `deviceTagRef`, `gaps` — all singular (`smart-plug-import-panel.tsx:27-38`). Replace with an array of queue-item objects, one per file selected in the current session, e.g. `{ id: string (client-generated), fileName: string, jobId: string | null, state: ImportState, error: string | null, gaps: SmartPlugImportGapDto[], smartPlugImportId: string | null, deviceTag: string, queued: boolean }[]`. Existing `ImportState` union (`'idle' | 'uploading' | 'processing' | 'completed' | 'awaitingMapping' | 'flaggedForReview' | 'failed'`) stays per-item, not global — the dropzone itself is always available to add more files (mockup Frame 6: "Add more files"), it's not a single-slot `idle` state gating the rest of the screen.
  - [x] **Fire all uploads concurrently, not sequentially.** On multi-file selection/drop, map every `File` to a call to `uploadSmartPlugFile(file)` and let them all run in parallel (`Promise.allSettled`-style — don't `await` inside a `for` loop, which would serialize them and violate AC #4's "not queued to be enqueued sequentially"). Add each file to the queue array immediately (before the upload call resolves) with `state: 'uploading'`, so it "appears immediately" per AC #6 — do not wait for the `202` response before rendering the row.
  - [x] **Reuse the existing polling loop exactly, per queue item.** The current single `useEffect` keyed on `state === 'processing'` (`smart-plug-import-panel.tsx:42-98`) — including its 404-means-not-yet-dequeued handling (`queued` flag) and `MAX_CONSECUTIVE_POLL_FAILURES` backoff — is already the correct per-job polling shape; this story just needs one such polling loop instance running per queue item with an in-flight job, not one shared loop. The cleanest fit for this codebase's existing patterns: extract the current panel's upload+poll+state-machine logic (currently inline in one component) into a per-item sub-component or hook (e.g. `useSmartPlugImportJob(file)`), and have the new multi-file container render one instance per queued file. **Do not build a second, parallel polling mechanism** — one file failing or needing mapping must not affect another's polling (AC #5), which the existing per-`jobIdRef` closure already gives you for free once each item gets its own hook/effect instance.
  - [x] **`AC #6`'s "Waiting or Processing indicator" needs no new backend state.** The existing 404-while-polling-means-not-yet-dequeued idiom (`queued` boolean, `smart-plug-import-panel.tsx:35-38,80-84`) already distinguishes "enqueued but not yet dequeued" (render as Waiting) from "dequeued, actively parsing" (render as Processing) — this is a client-side-only concept ("this action's queue"), not the household-wide durable list `BackgroundJobStatus.Queued` will back for **Story 3.6** (see Dev Notes — do not build 3.6's backend enum/persistence change here).
  - [x] Preserve everything else per-item unchanged from the current single-file implementation: `GapCard` rendering for completed/flaggedForReview items with gaps, `PowerPointMappingDialog` for `awaitingMapping` items, the "Upload another file" reset affordance (though with multiple concurrent items, "reset" now means removing that one item's card from the queue array, not resetting a single global state).
  - [x] Match the mockup's queue-row shape (`mockups/key-smart-plug-import.html` Frame 6, `.queue-file`/`.queue-progress-track` classes, lines 677-732) for visual reference — file icon, name, size/tag subtitle, a slim progress indicator, and a state badge — but keep using this codebase's actual shadcn `Badge`/`GlassCard` components (per Story 3.1's Dev Notes precedent of *not* copying the mockup's literal colors — the mockup's own file header already flags its `.processing-pill`/`.complete-check` status-triad-color reuse as a confirmed DESIGN.md violation).

- [x] **Task 3: Frontend API client — confirm no backend/endpoint changes needed** (AC: #4, #7)
  - [x] **No backend change is required for this story.** `POST /api/smart-plug-imports` already accepts exactly one `IFormFile` per call and already returns `202` with a job id immediately (`SmartPlugImportEndpoints.cs:35-96`) — "each file enqueued as its own independent job" (AC #4) is satisfied by the frontend calling this existing endpoint once per selected file, concurrently. Do not add a batch/multipart-multi-file endpoint — that would be new API surface this story's ACs don't ask for and would fight AD-6's "per-file async lifecycle unchanged" framing (AC #7).
  - [x] `web/src/lib/smart-plug-import-api.ts`'s `uploadSmartPlugFile(file: File): Promise<string>` (singular file) needs no signature change — call it once per file from Task 2's queue logic.

- [x] **Task 4: Tests** (AC: all)
  - [x] Frontend Vitest (colocated, `@testing-library/react`, `jsdom`) for the rewritten queue component: selecting/dropping 3 files in one action renders 3 queue rows immediately (before any upload promise resolves — assert this with a controllable/deferred `fetch` mock, not a real network round trip); one item resolving to `failed` (mock a 400/500 on that item's `fetchJobStatus`) does not affect the polling or rendered state of the other two (AC #5); confirm uploads fire concurrently — e.g. assert `fetch` was called 3 times before the first mocked response resolves, not interleaved one-at-a-time.
  - [x] Extend/replace the existing `smart-plug-import-panel.test.tsx` test file for the new multi-item shape — do not leave the old single-file-only test suite passing against dead code if the component is renamed/restructured (e.g. rename to `smart-plug-import-page.test.tsx` / new queue-hook test file, matching whatever file split Task 2 lands on).
  - [x] `dashboard-page.test.tsx` (if one exists — confirm) or a new colocated test: the Smart Plug Import icon button renders on the Dashboard topbar and calls `onSmartPlugImportClick` when tapped.
  - [x] `settings-page.test.tsx` (if one exists — confirm): assert the Smart Plug Import panel/section is no longer rendered inside Settings (AC #1's negative assertion — easy to silently regress back in during a later merge if nothing guards it).
  - [x] No new backend tests are needed beyond re-running the existing `SmartPlugImportEndpointsTests.cs` suite unchanged (Task 3 makes no backend changes) — confirm it still passes.
  - [x] Frontend: Vitest + Testing Library, colocated next to source, `jsdom` — project-context.md convention, matching every existing frontend test in this codebase.

### Review Findings

- [x] [Review][Patch] `useSmartPlugImportJob`'s upload fires from a mount-only `useEffect`, with no guard against React StrictMode's dev double-invocation (mount → cleanup → remount) — `web/src/main.tsx:11` wraps the app in `<StrictMode>`, and the effect's cleanup only sets a local `cancelled` flag, it never aborts the in-flight `fetch`. In local dev (`npm run dev`), selecting one file therefore fires two real `POST /api/smart-plug-imports` calls and creates two backend jobs for it; only the second job's id is kept for polling, the first is silently discarded. Story 3.1's original panel avoided this because it fired the upload from the input's `onChange`/`onDrop` handler, never from a mount effect — this story's hook extraction introduced the hazard. Production (`vite build`) is unaffected (StrictMode double-invocation is dev-only), but every manual dev/QA import currently double-uploads. [web/src/components/smart-plug-import/use-smart-plug-import-job.ts:41-69] **Fixed:** `uploadSmartPlugFile` now accepts an optional `AbortSignal`; the mount effect creates an `AbortController` and aborts it on cleanup, so StrictMode's synthetic first invocation is cancelled before its request completes, matching React's own documented fix for this exact class of bug. Also correctly cancels a genuine in-flight upload if the item is removed/unmounted before it finishes. New regression test renders under `<StrictMode>` with an abort-aware fetch mock and asserts exactly one upload succeeds.
- [x] [Review][Patch] The shared `smartPlugImport.asyncNote` paragraph ("We're parsing this in the background…") renders unconditionally whenever the queue is non-empty, with no check that any item is actually still in-flight — the old single-file panel correctly gated the equivalent text on `state === 'processing'`. Once every item in a batch has reached `completed`/`failed`/`awaitingMapping`, the UI keeps telling the user their import is still processing in the background, which is false. [web/src/components/smart-plug-import/smart-plug-import-page.tsx:94] **Fixed:** each queue item now reports its uploading/processing status up to the page via a stable `onActiveChange(id, isActive)` callback into an `activeIds` set; the shared note only renders while `activeIds.size > 0`. New regression test confirms the note disappears once the (only) item completes.
- [x] [Review][Patch] The old per-item `smartPlugImport.queuedNote` copy ("Still queued — large files can take a while to start processing") was deleted outright rather than carried over — the new UI reduces "queued behind another import" to a bare "Waiting" badge with no explanatory text, so a user watching one item sit in Waiting while siblings complete has no way to learn why. [web/src/locales/en-US/translation.json, web/src/locales/de-DE/translation.json] **Fixed:** `queuedNote` restored in both locale files and rendered per-item beneath the badge whenever `job.state === 'processing' && job.queued`. Existing Waiting-badge test extended to assert the explanatory text is present.
- [x] [Review][Patch] The new "renders 3 queue rows immediately" test resolves all three files to the identical hardcoded `jobId: 'job-x'` with no mock case for `/api/jobs/job-x` (falls through to a default 200/null response) — it only passes because the fake-timer test never advances far enough to let the 2000ms poll tick fire. It doesn't actually validate polling of concurrent jobs with distinct ids, and would silently mis-assert (`job.status` read off `null`) if a poll tick ever did land inside the test window. [web/src/components/smart-plug-import/smart-plug-import-page.test.tsx] **Fixed:** each of the 3 files now gets a distinct jobId (`job-a`/`job-b`/`job-c`) with a matching `/api/jobs/{id}` mock case returning a valid `processing` status, and an unmatched URL now throws instead of silently falling through.
- [x] [Review][Patch] No test exercises the drag-and-drop path (`handleDrop`) with multiple files — every multi-file test drives selection through `userEvent.upload` on the hidden `<input>`. AC #4/#6 both call out "select or drop multiple files," and the mockup's Frame 6 depicts drag-drop as the primary multi-file interaction, but that path has zero automated multi-file coverage. [web/src/components/smart-plug-import/smart-plug-import-page.test.tsx] **Fixed:** added a `data-testid` on the dropzone and a new test that drops 3 files via `fireEvent.drop`, asserting all 3 rows render and upload concurrently.
- [x] [Review][Patch] `SmartPlugImportPage` does no focus management on mount (no ref/`autoFocus`/effect moving focus to the heading or back button) — for a keyboard or screen-reader user activating the new Dashboard icon, focus lands in an indeterminate spot on this first-class new navigation destination, unlike surfaces that inherit focus handling from an existing, already-covered page. [web/src/components/smart-plug-import/smart-plug-import-page.tsx:25-64] **Fixed:** the `<h1>` heading now has `tabIndex={-1}` and receives focus via a mount effect. New regression test asserts the heading has focus after render.
- [x] [Review][Defer] `act()` warnings appear when the new queue test file runs as part of the full suite (not in isolation), indicating unflushed async state from the polling/upload effects — deferred, pre-existing: the pre-diff single-file panel's own test file produced the same class of warning; this story's per-item hook extraction just multiplies the exposure (N concurrent instances instead of one) rather than introducing the underlying pattern. [web/src/components/smart-plug-import/use-smart-plug-import-job.ts]
- [x] [Review][Defer] No affordance exists to remove/cancel a queue item while it's `uploading`/`processing` (`dismissable` only covers `completed`/`flaggedForReview`/`failed`) — deferred, pre-existing gating logic: the old single-file panel's reset button was gated identically (`state !== 'uploading'/'processing'`), so this isn't a new restriction, but batching multiple files raises the stakes — one accidental file in a 5-file drop now can't be pulled back out until it resolves on its own. Worth a follow-up UX pass, not required by any AC. [web/src/components/smart-plug-import/smart-plug-import-page.tsx:110-111]
- [x] [Review][Defer] If the backend ever returns `importStatus: 'awaitingpowerpointmapping'` with a null/empty `smartPlugImportId`, the mapping dialog never renders (gated on `job.smartPlugImportId` truthiness) and, unlike the old panel's global reset button (available during `awaitingMapping` too), the new per-item "Remove from queue" button also excludes `awaitingMapping` — deferred, pre-existing: this exact null-check gate is copied byte-for-byte from the pre-diff panel, not introduced by this diff. [web/src/components/smart-plug-import/smart-plug-import-page.tsx:110-111,158; web/src/components/smart-plug-import/use-smart-plug-import-job.ts:91-94]
- [x] [Review][Defer] No upper bound on how many files one selection/drop can enqueue — a household member selecting an entire folder of exports fires that many concurrent uploads and mounts that many permanently-polling hook instances, against a backend that Dev Notes itself confirms processes jobs strictly one at a time; a large batch leaves most items sitting in "Waiting" for a long stretch with no soft cap or warning. Deferred: not required by any AC, worth tracking for a future hardening pass. [web/src/components/smart-plug-import/smart-plug-import-page.tsx:33-38]

## Dev Notes

### Trend History does not exist yet — do not try to add its entry point in this story

`sprint-status.yaml` shows `epic-4: backlog` and `4-1-trend-history-view: backlog` — confirmed via a fresh check of `web/src/App.tsx`/`web/src/components`: there is no Trend History component or route anywhere in this codebase yet. **The already-existing "History" surface (`MeterReadingHistoryPage`, `view === 'history'`) is Story 2.8's Meter Reading History (FR-31) — a completely different screen from Trend History (FR-8, Epic 4).** Do not wire the Smart Plug Import icon onto `MeterReadingHistoryPage` by mistake; that would misattribute FR-8's not-yet-built surface to FR-31's already-shipped one.

This story therefore ships **Dashboard-only** for real (AC #1, #3): the dedicated `SmartPlugImportPage` this story builds is deliberately structured as its own routable `view` (not nested inside `DashboardPage`) specifically so that whichever Epic 4 story first builds Trend History can add a second icon button that opens the exact same `view === 'smartPlugImport'` destination with zero duplication. Add an entry to `_bmad-artifacts/implementation/deferred-work.md` at the end of this story noting: "Trend History's Smart Plug Import icon entry point (UX-DR20, epic-3 Story 3.5 AC #2) is deferred to whichever Epic 4 story first builds the Trend History screen — the shared `SmartPlugImportPage`/`view === 'smartPlugImport'` destination this story built is already in place and needs no changes, only a second entry-point button." — so this isn't silently lost between now and Epic 4's kickoff.

### Multi-file queue state is client-side/this-action-only — do not build Story 3.6's backend work here

The architecture spine's AD-6 entry (`invariants-rules.md:39`, "FR-32 extension, 2026-08-26") already anticipates multi-file queuing needing a `BackgroundJobStatus.Queued` value and enqueue-time `BackgroundJob` row creation — but it explicitly frames that as backing **FR-32's** (Story 3.6's) household-wide, durable-across-reloads Job Status & History list, not this story's ACs. This story's own AC #6 only needs the queue to "appear immediately" **for the files just selected in this action**, on the same screen the upload happened on, in local component state — it does not need to survive a page reload or show other members' imports (that's Story 3.6's FR-32 scope entirely).

**Recommended default (confirmed reasonable given the existing codebase, not an open question):** reuse Story 3.1's already-shipped "404-while-polling means not-yet-dequeued" idiom, per queue item, exactly as it works today for a single file — this fully satisfies "Waiting or Processing indicator" (AC #6) with zero backend changes. **Do not add `BackgroundJobStatus.Queued` or move `BackgroundJob` row creation to enqueue-time in this story** — that belongs to Story 3.6, which will build the actual household-wide persisted list on top of it. Building it here would be scope creep into a not-yet-drafted story and risks the two stories' migrations/schema work colliding.

### Backend job-processing stays genuinely sequential — this is expected, not a bug to fix

`InProcessChannelJobProcessingService` (`src/EnergyTracker.Infrastructure/Adapters/InProcessChannelJobQueue.cs:28-45`) reads one message at a time off a single `Channel` and `await`s `BackgroundJobProcessor.ProcessAsync` fully before reading the next — so even though this story enqueues all files "immediately" (AC #4 is about *enqueueing*, not *processing*), the self-host/local-dev worker still processes them one at a time, exactly like the mockup's own Frame 6 ("the third file above hasn't started yet; it's simply next in the household's queue"). **This is correct, existing AD-6 behavior — do not change job-processing concurrency in this story.**

### Architecture constraints (binding, not optional)

- **AD-6 (Async job processing):** per-file lifecycle (enqueue → `202` → background parse → poll `GET /api/jobs/{id}`) is unchanged (AC #7) — this story only changes how many uploads the *frontend* fires in one user action, never the job queue/processing mechanism itself.
- **No new endpoint, no new `IBackgroundJobQueue`/`ISmartPlugParser` port changes** — this is a frontend-only story. `POST /api/smart-plug-imports` already takes one file per call by design (multipart `IFormFile` binding) and that's the correct shape to keep calling once per file, concurrently.
- **UX-DR9 (amended 2026-08-26):** confirms Smart Plug Import stays a contextual icon entry point, never a fifth bottom-nav tab — `NavChrome` (`nav-chrome.tsx`) needs no changes.
- **UX-DR20:** the 40×40 icon button reuses `nav-chrome-active-bg`/`-active-foreground` tokens verbatim — chrome/interactive, never a status color; see Task 1.

### Existing code to reuse, not reinvent

- `web/src/lib/smart-plug-import-api.ts`'s `uploadSmartPlugFile`/`fetchJobStatus`/`mapSmartPlugImportToPowerPoint` — no changes needed, call `uploadSmartPlugFile` once per file.
- `smart-plug-import-panel.tsx`'s existing polling `useEffect` (lines 42-98), including its `MAX_CONSECUTIVE_POLL_FAILURES` backoff and 404-as-queued handling — this is the exact per-job polling shape to replicate per queue item, not redesign.
- `GapCard` and `PowerPointMappingDialog` (`web/src/components/smart-plug-import/`) — unchanged, just rendered per queue item instead of once globally.
- `MeterReadingHistoryPage`'s and `SettingsPage`'s existing `onBack` prop / full-screen-with-topbar shape — mirror this exact pattern for the new `SmartPlugImportPage`, don't invent a new page-shell convention.
- `NavChrome`'s `nav-chrome-active-bg`/`-active-foreground` Tailwind tokens (`nav-chrome.tsx:14`) — reuse verbatim for the new icon button (UX-DR20).

### Known non-goals (avoid scope creep)

- **Trend History's own entry point** — genuinely can't be built (the screen doesn't exist); tracked as a deferred-work note instead (see above).
- **`BackgroundJobStatus.Queued`, enqueue-time `BackgroundJob` row creation, the household-wide Job Status & History list, and the 30-day retention sweep** — all Story 3.6 (FR-32), not this story.
- **No batch upload endpoint** — keep calling the existing single-file `POST /api/smart-plug-imports` once per file.
- **No change to job-processing concurrency** — the single-worker sequential drain stays as-is (AD-6, unrelated to this story).

### Project Structure Notes

- Frontend modified: `web/src/App.tsx` (new `view` variant + branch), `web/src/components/dashboard/dashboard-page.tsx` (topbar + icon button + new prop), `web/src/components/settings/settings-page.tsx` (remove `SmartPlugImportPanel` usage).
- Frontend new: `web/src/components/smart-plug-import/smart-plug-import-page.tsx` (new page shell), likely a restructure of `smart-plug-import-panel.tsx` into a per-item hook/component (exact split is an implementation choice — Task 2 describes the required behavior, not a mandated file layout) plus its colocated test(s).
- No backend files touched, no migration, no new NuGet/npm packages.
- Fits the existing flat `web/src/components/{feature}` grouping (`smart-plug-import/` already exists) and the existing `web/src/lib/smart-plug-import-api.ts` client (no new client file needed).

### Testing standards summary

- Frontend: Vitest + Testing Library (`@testing-library/react`), `jsdom`, colocated next to source — project-context.md convention, matching every existing frontend test in this codebase (e.g. `smart-plug-import-panel.test.tsx`, `smart-plug-import-api.test.ts`).
- No `.NET` test changes expected — confirm the existing backend suite (`SmartPlugImportEndpointsTests.cs` et al.) still passes unmodified, since Task 3 makes no backend changes.

### References

- [Source: `_bmad-artifacts/planning/epics/epic-3-smart-plug-import-baseline-sharpening.md#Story 3.5`] — story statement + AC source (verbatim), epic framing.
- [Source: `_bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md#FR-4`] — FR-4's amended consequences (dual entry points, multi-file queuing).
- [Source: `_bmad-artifacts/planning/epics/requirements-inventory.md:119,122,130`] — UX-DR9 (amended), UX-DR12 (amended), UX-DR20 exact text (icon button spec, token reuse, shared-screen requirement).
- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-6`] — the FR-32-extension note explaining why `BackgroundJobStatus.Queued` is Story 3.6's job, not this one's.
- [Source: `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-dashboard.html:1-90`] — Dashboard `.import-btn` topbar placement, token derivation (dark/light active-bg/-fg hex values), 40×40 sizing rationale.
- [Source: `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-trend-history.html:281-305`] — the (not-yet-buildable) Trend History page-title-row placement, for whenever Epic 4 picks this up.
- [Source: `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-smart-plug-import.html:677-732`] — Frame 6, multi-file queue visual reference (`.queue-file`/`.queue-progress-track`).
- [Source: `_bmad-artifacts/implementation/3-1-smart-plug-file-upload-async-parsing.md`] — original panel implementation, the 404-as-queued polling idiom, the "don't copy mockup status-triad colors" rubric-review finding.
- [Source: `_bmad-artifacts/implementation/3-4-incremental-smart-plug-import.md`] — most recent Epic 3 story, confirms no frontend touched by 3.2-3.4 (this story's baseline for the frontend is still exactly Story 3.1's shipped code, review-patched).
- [Source: `web/src/App.tsx`, `web/src/components/dashboard/dashboard-page.tsx`, `web/src/components/dashboard/nav-chrome.tsx`, `web/src/components/settings/settings-page.tsx`, `web/src/components/smart-plug-import/smart-plug-import-panel.tsx`, `web/src/lib/smart-plug-import-api.ts`] — exact current code this story restructures.
- [Source: `src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs`, `src/EnergyTracker.Infrastructure/Adapters/InProcessChannelJobQueue.cs`] — confirms no backend change needed and why processing stays sequential.
- [Source: `_bmad-artifacts/implementation/sprint-status.yaml`] — confirms `epic-4`/`4-1-trend-history-view` are still `backlog`, the basis for AC #2's scope cut.
- [Source: `_bmad-artifacts/project-context.md`] — project-wide coding/testing conventions (oxlint, Vitest, path aliases, no-react-router-yet).

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

None — no debugger/log-file artifacts beyond standard `vitest run`/`dotnet test` output captured during implementation.

### Completion Notes List

**Frontend-only story, exactly per Dev Notes — no backend files touched.** `POST /api/smart-plug-imports` (one file per call, `202` + jobId) and `GET /api/jobs/{id}` are unchanged; confirmed by re-running `SmartPlugImportEndpointsTests.cs` unmodified (16/16 green).

**Task 2 shape:** extracted Story 3.1's inline upload+poll+state-machine logic into a new `useSmartPlugImportJob(file)` hook (`use-smart-plug-import-job.ts`) — one hook instance per queue item, each with its own `jobIdRef` closure, its own `useEffect`-driven polling loop (same `MAX_CONSECUTIVE_POLL_FAILURES`/404-as-queued idiom as Story 3.1, byte-for-byte behavior), and its own upload-on-mount effect. The new `SmartPlugImportPage` (`smart-plug-import-page.tsx`) owns only the queue array (`{ id, file }[]`) and renders one `SmartPlugImportQueueItem` per entry — mounting N items in the same render therefore fires N independent uploads and N independent polling loops with no shared state, which is what makes concurrency (AC #4) and fault isolation (AC #5) fall out for free rather than needing explicit `Promise.allSettled` orchestration.

**AC #6 "Waiting or Processing indicator":** reused the existing `queued` (404-while-polling) client-side flag exactly as Dev Notes prescribed — no `BackgroundJobStatus.Queued`/backend change. Added one new badge label (`smartPlugImport.waitingBadge` = "Waiting") shown instead of the existing `processingBadge` label while `queued` is true, matching the mockup's Frame 6 `Waiting`/`Processing` vocabulary; the old single-file panel's `queuedNote` paragraph text is retired (redundant with the new badge) in favor of one shared `asyncNote` paragraph below the whole queue, matching the mockup's layout.

**Per-item "reset" affordance repurposed as "remove from queue"** (`smartPlugImport.removeFromQueue`), since a queue item has no single-slot `idle` state to reset back to — dismissing an `awaitingMapping` item's mapping dialog now also just removes that item's card (`onCancel={onRemove}`), rather than reverting to a global idle state that no longer exists.

**Icon button:** 40×40 `rounded-xl` (12px, matches mockup) button using `bg-nav-chrome-active-bg`/`text-nav-chrome-active-foreground` verbatim (UX-DR20), `lucide-react`'s `Upload` icon (closest match to the mockup's up-arrow-into-tray glyph), added inside a new topbar row on `DashboardPage` (that screen previously had no topbar).

**Dead code removed, not just added:** deleted `smart-plug-import-panel.tsx` and its test file outright (Task 2 was an explicit full rewrite, not an incremental patch) rather than leaving it as unused dead code alongside the new page.

**Verification:** 192 frontend tests green (17 new in `smart-plug-import-page.test.tsx` replacing the 12 in the deleted `smart-plug-import-panel.test.tsx`, 1 new in `dashboard-page.test.tsx`, 1 new file `settings-page.test.tsx`), `tsc -b` clean, `oxlint` clean (pre-existing warning classes only — `react/only-export-components` on shadcn files, plus one new pre-existing-style `react-hooks/exhaustive-deps` warning on the new hook's intentionally-mount-only upload effect), `vite build` clean. `dotnet build` clean (0 errors), `SmartPlugImportEndpointsTests.cs` re-run standalone: 16/16 green, confirming zero backend regression from a frontend-only diff. **Not live-verified in a real Chrome browser against the real local stack this session** (unlike some prior stories) — no Auth0-authenticated household session was set up for this run; coverage rests on the automated suites above instead. Worth a manual pass before merge, in particular the icon button's visual placement/contrast in both themes and the multi-file drag-drop interaction, neither of which jsdom can exercise.

### File List

**Frontend — new:**
- `web/src/components/smart-plug-import/smart-plug-import-page.tsx`
- `web/src/components/smart-plug-import/smart-plug-import-page.test.tsx`
- `web/src/components/smart-plug-import/use-smart-plug-import-job.ts`
- `web/src/components/settings/settings-page.test.tsx`

**Frontend — modified:**
- `web/src/App.tsx`
- `web/src/components/dashboard/dashboard-page.tsx`
- `web/src/components/dashboard/dashboard-page.test.tsx`
- `web/src/components/settings/settings-page.tsx`
- `web/src/locales/en-US/translation.json`
- `web/src/locales/de-DE/translation.json`

**Frontend — deleted:**
- `web/src/components/smart-plug-import/smart-plug-import-panel.tsx`
- `web/src/components/smart-plug-import/smart-plug-import-panel.test.tsx`

**Docs:**
- `_bmad-artifacts/implementation/deferred-work.md` (Trend History entry-point deferral note, per Dev Notes)
