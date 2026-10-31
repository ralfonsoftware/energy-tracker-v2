# Epic 12: Follow-Up Hardening (not yet defined)

**Status:** not defined. Created 2026-10-31 at the second `deferred-work.md` triage (`spec-deferred-work-triage-2.md`) as the home for the "Soon" tier: member-visible bugs, hardening and accessibility items left open after Epic 11. No stories, no ACs, no sprint plan yet.

**Source:** 35 entries in `_bmad-artifacts/implementation/deferred-work.md`, each marked `[promoted: Epic 12]`. When this epic is defined, group them into stories, copy the relevant entry text into the story's context, and mark the entries `[promoted: Story 12.N]`.

**Not in this epic:** the urgent tier went to Epic 11 (Stories 11.20-11.31); Epic 9 (AI backend) stays in the backlog.

## Candidate items by theme

### a11y

- No aria-live on import/export error states (code review of 7-2-full-data-import-restore-migration (2026-09-22, Pass 2/frontend+docs))
- No focus mgmt across logoff dialog steps (code review of story-1.12 (2026-09-17))
- role=menu contains radiogroups (code review of 8-10-theme-toggle-profile-menu (2026-09-30))
- Icon buttons 40px not 44px (locale × theme sweep (spec-locale-theme-sweep, 2026-10-02))

### infra-hardening

- Orphaned gh-actions-migrate-* firewall rules (code review of spec-azure-sql-ci-migration-firewall (2026-08-13))
- No timeout-minutes in infra-deploy (code review of 1-6-cicd-deploy-idempotency-container-app-image-preservation (2026-08-14))
- No cert renewal failure alerting (code review of spec-custom-domain-managed-cert (2026-08-16))

### validation

- Device clock >5min ahead rejects default timestamp (code review of story-6.1 (2026-09-18))

### ux-polish

- Shared DialogContent lacks max-h (code review of spec-power-point-mapping-list-scroll (2026-09-02))
- Tariff history scrolls 10px at 659 de-DE (locale × theme sweep (spec-locale-theme-sweep, 2026-10-02))

### perf

- Cumulative row locks may escalate to table lock (code review of spec-3-10-cleanup-per-import-detach (2026-09-12))

### concurrency

- SQL Server sweep SELECT can block on writer locks (code review of spec-3-10-cleanup-sweep-async (2026-09-17))
- DeleteEligibleAsync (manual) takes no lock (code review of spec-3-10-cleanup-sweep-async (2026-09-17))

### ci-supply-chain

- A branch pusher can still read the repository secrets that other workflows hold (story-11-5-ci-identity-split-supply-chain-hygiene)
- `pr-review.yml` "What-if validation" sets `CURRENT_IMAGE` from the "Resolve current Container App im (Story 11.5 R2.2 (2026-10-11))
- The NuGet Audit gate failed `dotnet restore` on `main` itself (story-11-6-data-protection-key-encryption)

### auth-members

- HSTS uses the framework defaults (story-11-7-security-headers-csp-rate-limiting)
- After the yearly baseline is re-saved, the AI plausibility form still holds the household's old `Ver` (11-8-auth-hardening-csrf-logout-session-cap)
- Expired and revoked invite rows are never swept (story-11-9-member-removal-revocable-invites)
- The `MemberRemoval` budget (story-11-9-member-removal-revocable-invites)

### upload-restore

- `ValidateHouseholdImport` still does not check a file's own uniqueness beyond duplicate ids per arra (story-11-14-restore-id-collisions-active-job-guard)
- After a completed restore the SPA reloads only what holds restored ids (story-11-14-restore-id-collisions-active-job-guard)

### container-config

- S5 leftovers the epic ACs left out (story-11-11-container-config-correctness)
- S8 (`AllowedHosts: *`) is in no Epic 11 story (story-11-11-container-config-correctness)
- `/login` without OIDC is a JSON problem document, not a page in the SPA's voice (story-11-11-container-config-correctness)
- `oidcConfigured` (Program.cs) checks only `Oidc:ClientId` and `Oidc:Authority`, but the `/login` 503 (story-11-11-container-config-correctness)

### code-quality

- `handleDelete` does not map HTTP 409 to `errorParentArchived`; `handleSubmit` and `handleMoveTo` do (11-19-structural-cleanup)
- Closing the tagging dialog while a request is in flight lets `setDialogError` and `setSubmitting(fal` (11-19-structural-cleanup)

### docs-process

- FR-21's settings editor (not built, see the Story 2.4 entry above on threshold range validation) mus (story-11-15-trending-threshold-scaled-to-elapsed)
- Story 11.15 review (decision 1c, Ralf 2026-10-25) added a floor to the scaled trending threshold (story-11-15-trending-threshold-scaled-to-elapsed)
- The Status detail view's "Baseline to date" value and its "(over N days)" use whole-number formats (Part R of story-11-15-trending-threshold-scaled-to-elapsed)

### frontend-offline

- The server's reason on the card is the meter-reading validation `detail`, which is English prose and (story-11-16-offline-queue-rejected-readings-attention)
- A member removed from a Household by someone else keeps queued entries that answer `403` forever and (story-11-16-offline-queue-rejected-readings-attention)
- No cap and no expiry on needs-attention entries, and no "discard all" (story-11-16-offline-queue-rejected-readings-attention)
- `toDateTimeLocalValue` was copied in `log-reading-sheet.tsx` and `log-event-sheet.tsx`; Story 11.16 (story-11-16-offline-queue-rejected-readings-attention)

## Next step

Run `bmad-create-epics-and-stories` (or ask for a short epic definition with John and Winston) to define Epic 12. Suggested split: offline queue and members (rejected-readings, invites), restore and Status UI polish, accessibility pass, infra and CI hardening, supply chain and config, cleanup concurrency.
