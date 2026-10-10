---
baseline_commit: 794ffe1
---

# Story 11.1: Restore and Export Never Carry Access Grants

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- Epic 11 "Codebase audit hardening" (planning/epics/epic-11-codebase-audit-hardening.md). Origin: codebase audit 2026-10-10 (docs/codebase-audit-2026-10-10.md) findings S1 (High) and S12 (Low); architecture AD-25. This file cites the AD and audit IDs, which satisfies the spec-per-fix gate (project-context.md). -->
<!-- Branch already exists: feature/11-1-restore-never-carry-access-grants. -->

## Story

As a household member,
I want restoring a backup and exporting data to never change or reveal who can access my household,
so that a backup file can neither let a stranger in nor lock us out, and our login identities don't leak.

## Acceptance Criteria

1. **Given** a restore file whose `householdMembers` omits the caller, or lists identities from another household or IdP, **when** the restore completes, **then** the Household's membership is exactly what it was before (same rows: `Id`, `ExternalIssuer`, `ExternalSubjectId`, `DisplayName`, `CreatedAtUtc`). Proven by a **dual-provider** test (Postgres + SQL Server Testcontainers). `HouseholdMember` is no longer in the restore's delete/insert ordering (AD-25, audit S1).
2. **Given** an export, **when** it is generated, **then** each `householdMembers[]` entry is exactly `{ "id", "displayName" }`, with no `externalIssuer`, `externalSubjectId` or `createdAtUtc` (audit S12). `formatVersion` stays `"v2"`. `displayName` is always present as a key (`null` when the member has none).
3. **Given** a pre-change export file that still contains identity fields, **when** it is validated and restored, **then** it succeeds. The validator requires `id`, treats `displayName` as optional (absent, `null` or string), and accepts and ignores `externalIssuer`, `externalSubjectId` and `createdAtUtc` (fixture test using a verbatim pre-change member shape).
4. **Given** a completed restore, **when** the member sees the result, **then** a "People in this backup" panel lists the file's member display names next to the existing invite action, without comparing them to current members. The copy exists in de-DE and en-US.
5. **Given** the docs, **when** this story is done, **then** `docs/data-import-restore.md` ("Session continuity", now unconditional) and `docs/data-export-format.md` (member shape, and that the restore target must run the same or a newer release) match the behaviour.
6. **Given** the replace-all confirmation dialog (implied requirement, not in the epic text), **when** it lists what the file contains, **then** it no longer claims that Household Members are part of what gets replaced; it states that who can access the Household does not change. Otherwise the dialog would contradict the new behaviour.

## Tasks / Subtasks

Test-first: write Tasks 1–2's tests and see them **red** against current code before Tasks 3–7.

- [x] Task 1: Backend tests, red first (AC: #1, #2, #3)
  - [x] 1.1 `ExportHouseholdDataTests`: a `HouseholdMember` with issuer/subject/displayName maps to a DTO whose public properties are exactly `Id`, `DisplayName` (reflection) and whose serialized JSON (Web defaults) has exactly the keys `id`, `displayName`. Also a member with `DisplayName = null` serializes `"displayName": null`.
  - [x] 1.2 `HouseholdExportEndpointsTests`: the raw response body for a household with one member contains `householdMembers[0]` keys exactly `["id","displayName"]`, and the body text contains neither `externalIssuer`, `externalSubjectId`, nor the caller's subject string (use the subject passed to `factory.CreateAuthenticatedClient(...)`).
  - [x] 1.3 `ValidateHouseholdImportTests` (member section; helper at line ~39 builds `householdMembers`): (a) new shape `{id, displayName}` passes; (b) `{id}` only passes; (c) `displayName: null` passes; (d) legacy shape with `externalIssuer`, `externalSubjectId`, `createdAtUtc` passes and those fields are ignored (even if `externalIssuer` is a number: ignored means not validated); (e) missing/non-GUID `id` fails; (f) `displayName: 5` fails with `householdMembers[0]: 'displayName' must be a string or null.`. Delete or invert any existing test that asserts `externalIssuer`/`externalSubjectId`/`createdAtUtc` are required.
  - [x] 1.4 `RestoreHouseholdDataTests`: the captured `HouseholdRestoreData` has no member property at all (update the assertion at line ~138); keep the legacy-shape JSON at line ~86 as the "pre-change export" fixture and add a second test with the new `{id, displayName}` shape.
  - [x] 1.5 `HouseholdRestoreWriterTests` (`HouseholdRestoreWriterTestsBase`, runs on both providers via `PostgresHouseholdRestoreWriterTests` / `SqlServerHouseholdRestoreWriterTests`): remove `member` from `BuildRestoreData`; replace `Restoring_onto_..._leaves_no_pre_existing_rows_behind`'s member assertions (lines ~171–188: the pre-existing member must now **survive**); replace `A_HouseholdMember_present_in_the_imported_data_still_resolves_...` (line ~221). **New headline test** (AC #1): seed a Household with two members; run `new RestoreHouseholdData(new HouseholdRestoreWriter(dbContext)).ExecuteAsync(householdId, payload, ct)` over a temp v2 JSON file whose `householdMembers` omits both seeded members and lists a foreign issuer/subject; assert the member rows are field-for-field identical afterward and no row with the foreign identity exists (query with `IgnoreQueryFilters()` since `HouseholdMember` carries no filter, or scope by `HouseholdId`). Needs the temp-file + `RestoreHouseholdDataPayload` pattern from `RestoreHouseholdDataTests`.
  - [x] 1.6 `HouseholdImportEndpointsTests` (Api, real DB): (a) upload a legacy-shape file whose members list a foreign identity and omit the caller → validate 200, confirm 202, job `completed`, then `GET /api/household-export` still 200 (caller still resolves) and `householdMembers` has exactly the caller's `id`; (b) the validate response `summary.memberDisplayNames` contains the file's names.
  - [x] 1.7 `EnergyTracker.Architecture.Tests`: new guard `HouseholdRestoreNeverCarriesAccessGrantsTests` (AD-25 as an executable invariant, per project-context "consider whether it belongs here"): `HouseholdRestoreData` has no property whose type mentions `HouseholdMember`; `HouseholdMemberExportDto`'s public properties are exactly `{Id, DisplayName}`; source-text scan (same style as `HouseholdExportReaderDoesNotBypassTenantIsolationTests`) that `HouseholdRestoreWriter.cs` does not contain `HouseholdMembers` in code (strip `//` comments the same way that test does).
- [x] Task 2: Frontend tests, red first (AC: #4, #6)
  - [x] 2.1 `data-import-panel.test.tsx`: extend `validationResponseBody.summary` with `memberDisplayNames`. Tests: (a) after a completed restore, "People in this backup" heading and each name are shown, together with an invite button (`householdInvite.generateButton` copy); (b) names are rendered as text only (a name like `<img src=x onerror=...>` appears literally); (c) `householdMembers: 3` with 1 name shows the "2 people without a name" line; (d) `householdMembers: 0` shows no panel; (e) the confirm dialog no longer renders a Household Members line and shows the access-unchanged sentence; (f) `resetToIdle` ("Import another file") clears the panel.
- [x] Task 3: Export side (AC: #2)
  - [x] 3.1 `ExportHouseholdData.cs`: change `HouseholdMemberExportDto` to `(Guid Id, string? DisplayName)`; `ToDto(HouseholdMember)` becomes `new(member.Id, member.DisplayName)`. Update the record's comment to say why (AD-25/S12). Do **not** touch `IHouseholdExportReader`/`HouseholdExportReader` (still reads entities, keyset-paged by `CreatedAtUtc`; identity columns never reach the wire).
  - [x] 3.2 Fix compile fallout in tests that construct `HouseholdMemberExportDto` (`HouseholdExportEndpointsWriteTests` uses `Empty<...>()` only).
- [x] Task 4: Validator (AC: #3)
  - [x] 4.1 `ValidateHouseholdImport.ValidateHouseholdMember`: keep `RequireGuid(id)` and `RequireOptionalString(displayName)`; remove the `externalIssuer`, `externalSubjectId` and `createdAtUtc` requirements. Leave `ValidateArray`'s generic duplicate-`id` check alone. Comment: legacy fields are deliberately not validated (AD-25).
  - [x] 4.2 Confirm `JsonSerializer.Deserialize<HouseholdExportResult>` (Web defaults) ignores the unmapped legacy properties and maps a missing `displayName` to `null`. Do **not** switch on `UnmappedMemberHandling.Disallow` anywhere.
- [x] Task 5: Restore side (AC: #1)
  - [x] 5.1 `HouseholdRestoreData` (`IHouseholdRestoreWriter.cs`): remove `IReadOnlyList<HouseholdMember> HouseholdMembers`.
  - [x] 5.2 `RestoreHouseholdData.ToRestoreData`: drop the members line; delete `ToEntity(HouseholdMemberExportDto, Guid)`. Update the class `<summary>` to say restore never writes `HouseholdMember` (AD-25).
  - [x] 5.3 `HouseholdRestoreWriter`: delete the `HouseholdMembers` `ChunkedDeleteAsync` block and the `ChunkedInsertAsync(dbContext.HouseholdMembers, …)` line; fix the two ordering comments (insert-order comment at ~line 108 lists `HouseholdMember`; the class header comment). Add one line: membership is deliberately untouched (AD-25).
  - [x] 5.4 `BackgroundJobConfiguration.cs` (~line 30): the comment still says restore deletes/reinserts members. Update it: restore no longer touches members; **keep `DeleteBehavior.SetNull` unchanged** (no migration; AD-26/Story 11.9 member removal will delete members while job rows reference them). Comment-only change.
- [x] Task 6: Summary payload carries the names (AC: #4)
  - [x] 6.1 `HouseholdImportEndpoints.BuildSummary`: add `IReadOnlyList<string> MemberDisplayNames` to `HouseholdImportSummary` (appended last; keep `HouseholdMembers` count). Build it from `data.HouseholdMembers`: `DisplayName` trimmed, non-blank only, file order. JSON name is `memberDisplayNames` (Web defaults).
- [x] Task 7: Frontend (AC: #4, #6)
  - [x] 7.1 `web/src/lib/household-import-api.ts`: add `memberDisplayNames: string[]` to `HouseholdImportSummary`.
  - [x] 7.2 `data-import-panel.tsx`: remove the `confirmSummaryHouseholdMembers` `<li>`; add the access-unchanged sentence to the confirm dialog (see Dev Notes, i18n). In the `step === 'success'` block, after the success message, when `summary && summary.householdMembers > 0` render the people panel: heading, hint, a plain `<ul>` of `summary.memberDisplayNames` (key `${index}-${name}`), the "N people without a name" line when `householdMembers > memberDisplayNames.length`, and `<InviteGeneratePanel bare />` beneath it (the opt-in `bare` prop was added by Task 7.4 to avoid a card-in-card). No comparison with current members, no fetch of current members.
  - [x] 7.3 `web/src/locales/en-US/translation.json` and `de-DE/translation.json`: add the new keys, remove `confirmSummaryHouseholdMembers_one/_other` from both (catalogs stay in parity; see `translation-consistency.test.ts`).
  - [x] 7.4 Look at the success state in both themes and at 400px and ≥660px. `InviteGeneratePanel` is itself a `GlassCard`; nested inside `DataImportPanel`'s card it may look doubled. If it does, stop and Ask First (see below) instead of inventing a layout.
- [x] Task 8: Docs (AC: #5) — see Dev Notes "Docs to rewrite" for the exact sections.
- [x] Task 9: Full verification
  - [x] 9.1 `dotnet test` (all projects; Testcontainers need Docker), `dotnet format --verify-no-changes` is not yet a gate (11.18) but do not add warnings. Frontend: `npm test`, `npm run lint` (oxlint), `npx tsc -b`.
  - [x] 9.2 **Live Chrome check (required before review → done).** Epic 11's live gate lists 11.6–11.9 only, but `project-context.md` makes any browser-dependent behaviour a live gate and this story adds restore-result UI and an unconditional session-continuity claim. With Claude-in-Chrome: (a) export your own household (new shape; confirm no identity fields in the downloaded file); (b) restore that file: still signed in afterwards, people panel shows, invite button produces a link; (c) restore a hand-edited copy whose `householdMembers` lists a foreign `externalIssuer`/`externalSubjectId` and omits you: still signed in, Settings → Household unchanged; (d) repeat the panel in en-US and de-DE (switch via Settings preferences). If Chrome is not connected, raise it immediately and pause; do not defer or check the task off on test evidence alone.
  - [x] 9.3 Add `[open]` entries to `deferred-work.md` only for real leftovers found, appended at the end in the documented format.

### Review Findings

Code review 2026-10-10 (Blind Hunter, Edge Case Hunter, Acceptance Auditor; all three layers completed). All six ACs verified satisfied by the Acceptance Auditor. 0 decision-needed, 5 patch, 0 defer, 18 dismissed.

- [x] [Review][Patch] Success panel throws if the validate response lacks `memberDisplayNames` (newer web against an older API during a rolling deploy): `summary.memberDisplayNames.length`/`.map` have no fallback, so the render fails after the destructive restore already completed. Use `summary.memberDisplayNames ?? []` [web/src/components/data-import/data-import-panel.tsx:~196-206]
- [x] [Review][Patch] Long untrusted display names can overflow the card (e.g. a 500-char name without spaces, worst at 400px): `<li>{name}</li>` has no wrapping class. Add `break-words` [web/src/components/data-import/data-import-panel.tsx:~198]
- [x] [Review][Patch] Architecture guard only matches the literal `HouseholdMembers`, so `dbContext.Set<HouseholdMember>()` in `HouseholdRestoreWriter.cs` would pass. Match `HouseholdMember` (comments are already stripped) [tests/EnergyTracker.Architecture.Tests/HouseholdRestoreNeverCarriesAccessGrantsTests.cs:~52-56]
- [x] [Review][Patch] `Restores_a_file_whose_members_use_the_new_id_and_displayName_shape` can pass vacuously: `string.Replace` is a silent no-op if the fixture text changes and the assertion (`HouseholdId == currentHouseholdId`) holds either way. Assert the replaced JSON contains `"displayName": "Ralf"` before restoring [tests/EnergyTracker.Application.Tests/RestoreHouseholdDataTests.cs:~164-174]
- [x] [Review][Patch] Story file drift: the Dev Notes table row and Task 7.2 still say "reuse `InviteGeneratePanel`; do not change either" although Task 7.4 added the opt-in `bare` prop; and the Debug Log says 551 Vitest tests while the Change Log says 554. Amend the row/task text and reconcile the count [_bmad-artifacts/implementation/11-1-restore-export-never-carry-access-grants.md]

Dismissed (18), grouped: accepted by the spec or by Ralf's recorded decisions (formatVersion stays v2, residual file-controlled data, panel state lost on navigation, hint copy, panel hidden for zero members, export reader unchanged, iframe evidence at 400px, new `ProjectReference`); verified not an issue (no FK to `HouseholdMember` other than `BackgroundJob`'s `SetNull`, the `rgba` hairline and `ehlert.haus` hostname already have precedent in the repo, docs are consistent on the move-to-new-hosting case, comment in `BackgroundJobConfiguration` reads correctly); self-inflicted only (unbounded `memberDisplayNames` from the member's own file, already bounded by the 250 MB cap; duplicate member ids can only come from a hand-edited file); cosmetic (long comment line, `data-slot` test selector, mockup file size, sprint-status bundling).

## Dev Notes

### What this fixes and why it is safe to change the format

- **S1 (High):** `HouseholdRestoreWriter` deletes and re-inserts `HouseholdMembers` from the uploaded file, and `CurrentHouseholdAccessor` resolves the Household by `(ExternalIssuer, ExternalSubjectId)`. Any member could upload a file granting arbitrary OIDC identities access, or omitting everyone and locking all members out.
- **S12 (Low):** the export writes every member's `externalIssuer` and `externalSubjectId` to any member, which is GDPR personal data and the raw material for S1.
- **AD-25 (rule, verbatim intent):** restore never writes `HouseholdMember` rows; export member entry is exactly `{id, displayName}`; the validator requires `id`, accepts optional `displayName`, accepts and ignores the legacy identity fields; `formatVersion` stays `"v2"` (Ralf's decision 2026-10-10); the restore target must run the same or a newer release (an older release rejects a newer export through its own validator, an explicit validation failure and never a partial restore); after restore the UI lists member display names ("people in this backup") next to the invite flow **without** comparing them to current members (display names are nullable, editable, not identities); re-invitation is the only path to access. Residual file-controlled data (AI-consent flag, audit rows, entity ids) is data, not access.
- Old release + new export: the old validator requires `externalIssuer`, so it rejects the new file with `householdMembers[0]: 'externalIssuer' must be a string.`. That is the intended explicit failure. Document it, do not "fix" it.

### Current state of each file you will touch (read before editing)

| File | Today | Change |
|---|---|---|
| `src/EnergyTracker.Application/ExportHouseholdData.cs` | `HouseholdMemberExportDto(Guid Id, string ExternalIssuer, string ExternalSubjectId, string? DisplayName, DateTimeOffset CreatedAtUtc)`. The **same** record is the export mapping target and the deserialization contract inside `HouseholdExportResult` (restore side). `FormatVersion = "v2"` const. | DTO becomes `(Guid Id, string? DisplayName)`; `ToDto` maps two fields. One DTO change fixes both sides. |
| `src/EnergyTracker.Api/Endpoints/HouseholdExportEndpoints.cs` | Streams with `Utf8JsonWriter` and `JsonSerializer.Serialize(writer, item, SerializerOptions)` where `SerializerOptions = new(JsonSerializerDefaults.Web)` (camelCase, nulls kept). `householdMembers` written via generic `WriteArrayAsync`. | **No change needed.** The DTO shape drives the output. Don't hand-write member JSON. |
| `src/EnergyTracker.Application/ValidateHouseholdImport.cs` | `ValidateHouseholdMember` requires `id`, `externalIssuer`, `externalSubjectId`, optional `displayName`, `createdAtUtc`. Pre-validates raw `JsonElement`s, then `JsonSerializer.Deserialize<HouseholdExportResult>`. Collects every failure. `ValidateArray` also rejects duplicate `id`s per array. | Relax per Task 4. |
| `src/EnergyTracker.Application/RestoreHouseholdData.cs` | Re-reads the temp file, deserializes, maps all DTOs to entities with `HouseholdId = current`, calls `restoreWriter.RestoreAsync`. Maps members via `ToEntity(HouseholdMemberExportDto, …)`. Deletes the temp file in `finally`. | Drop member mapping. |
| `src/EnergyTracker.Application/Ports/IHouseholdRestoreWriter.cs` | `HouseholdRestoreData` record includes `HouseholdMembers`. | Remove the member list. |
| `src/EnergyTracker.Infrastructure/Adapters/HouseholdRestoreWriter.cs` | One outer transaction; chunked (200) delete in FK-safe order (AuditCorrection → StatusSnapshot → SmartPlugReading → Event → Tariff → **HouseholdMember** → MeterRegressionPrompt → MeterReading → …), then chunked insert (Household update → MainMeter → Room → PowerPoint → Device → MeterReading → MeterRegressionPrompt → **HouseholdMember** → Tariff → …). `UpdateHouseholdSettingsAsync` updates the Household row in place and `Version++`. | Remove the two member steps and fix the comments. Everything else (chunking, 120 s timeout, single transaction, `Household.Version++`) stays. |
| `src/EnergyTracker.Api/Endpoints/HouseholdImportEndpoints.cs` | `POST /household-import` reads the whole file into a string, validates, writes temp file, registers a token; `BuildSummary` returns counts. `POST …/{token}/confirm` consumes the token, 409s if a restore job is Queued/Processing, enqueues. | Only `BuildSummary` + `HouseholdImportSummary` change (Task 6). Do not touch upload streaming (Story 11.10), the active-job guard (11.14), or `TryGetHouseholdId` (11.19). |
| `web/src/components/data-import/data-import-panel.tsx` | Wizard state machine `idle → validating → validationFailed | confirmDialog → confirming → restoring → success | error`. `summary` state survives until `resetToIdle`. Confirm dialog lists all 12 categories including Household Members. Success shows one sentence and an "Import another file" button. | Per Task 7. State machine unchanged. |
| `web/src/components/household-invite/` | `InviteGeneratePanel` (a `GlassCard`, `w-full max-w-sm`, fetches nothing on mount), `InviteMemberRow` (dual-render wrapper used by Settings). | Reuse `InviteGeneratePanel`; do not change its default rendering (Task 7.4 later added an opt-in `bare` prop, default unchanged); do not change `InviteMemberRow`. Mounting it a third time is safe (comment in `invite-member-row.tsx`). |

### What must be preserved (regression guard)

- Session continuity is now unconditional: restore must never alter a `HouseholdMember` row, so the caller's next request resolves exactly as before. The existing end-to-end `Full_validate_then_confirm_flow_restores_the_Households_own_exported_data` must stay green (self-restore of a new-shape export).
- Everything else in restore stays wholesale delete+insert, chunk size 200, 120 s command timeout, one outer transaction, `Household` updated in place with `Version++`, AI backend fields never written back, `SmartPlugImportId = null`, no `IAuditCorrectionRecorder` call (AD-11 carve-out).
- `BackgroundJob.QueuedByHouseholdMemberId` stays `DeleteBehavior.SetNull`. Story 7.2 changed it from `Restrict` because restore deleted members; restore no longer does, but Story 11.9's member removal will, so do **not** revert it and add no migration.
- `HouseholdInvite` is out of export/restore scope and stays so. Restore never touches invites.
- The export's other collections and `formatVersion: "v2"` are unchanged. Don't bump the version, don't add a `v3` path, don't read any other format.
- The restore-validation `400` shape (`failures` array) is unchanged.
- AD-3: the export reader still reads members with an explicit `HouseholdId` filter (the documented exception because `HouseholdMember` has no query filter) and `HouseholdExportReaderDoesNotBypassTenantIsolationTests` must stay green.

### Frontend specifics

- **Where the names come from:** the panel appears after the job completes, but the temp file is deleted by then and `GET /api/jobs/{id}` carries no result payload. So the names ride along in the validate response (`summary.memberDisplayNames`), which `DataImportPanel` already holds in `summary` state through `restoring` and `success`. No new endpoint, no job-result plumbing. Trade-off (accepted): if the member navigates away from Settings during the restore the panel state is lost; the restore itself still completes. Say so in a code comment.
- **Untrusted text:** display names come from an uploaded file. Render only as React text children, never `dangerouslySetInnerHTML`, never into a `title`/URL.
- **Names list:** non-blank, trimmed, file order, duplicates kept (two people can share a first name; the list is informational). If `summary.householdMembers > summary.memberDisplayNames.length`, add the "without a name" line with `count = householdMembers - memberDisplayNames.length`.
- **i18n keys (add to both catalogs under `settings.dataImport`, keep parity):**
  - `confirmAccessUnchanged`: en "Who can access your Household does not change." / de "Wer Zugriff auf deinen Haushalt hat, ändert sich dadurch nicht."
  - `peopleHeading`: en "People in this backup" / de "Personen in diesem Backup"
  - `peopleHint`: en "These people had access when the backup was made. Restoring does not give anyone access. Invite each person who should have it." / de "Diese Personen hatten Zugriff, als das Backup erstellt wurde. Die Wiederherstellung gibt niemandem Zugriff. Lade jede Person ein, die Zugriff haben soll."
  - `peopleUnnamed_one` / `_other`: en "{{count}} person without a name" / "{{count}} people without a name"; de "{{count}} Person ohne Namen" / "{{count}} Personen ohne Namen"
  - Remove `confirmSummaryHouseholdMembers_one/_other` (en and de). The success sentence stays as is. Use the existing de-DE "du" register and the existing term "Haushalt".
- Where `confirmAccessUnchanged` goes: a `<p>` directly under the "The file contains:" list inside the confirm dialog.
- **No UX mockup exists** for this panel (same precedent as `DataExportPanel`/`DataImportPanel`; the sprint-change proposal says "follow existing Settings and card patterns" and makes no UX spec change). Compact, text-only list; no icons needed.
- **Ask First (Sally, if needed):** if `InviteGeneratePanel`'s own glass card nested inside `DataImportPanel`'s card looks wrong, do not restyle `InviteGeneratePanel` globally. Either pass nothing and accept the nesting, or stop and ask for a rendered mock (Ralf prefers rendered HTML mocks over prose for UX, `feedback_ux_mockups_not_prose`).

### Docs to rewrite (AC #5)

`docs/data-import-restore.md`:
- **"Session continuity across a restore"** (lines ~163–181): rewrite. Restore never writes `HouseholdMember`; continuity is unconditional; remove the "as long as … is present among the imported members" caveat and the "disclosed edge case". Explain why (AD-25, S1) and that the destructive write still resolves `HouseholdId` from the job envelope.
- **"Write target"** (~line 131–160): drop the sentence that restore resolves/rebuilds identity through imported members; the "move to new hosting" paragraph now says the first visitor creates a fresh Household via FR-26 and then re-invites others.
- **"What gets replaced, and how"** (~line 189 ff.): remove `HouseholdMember` from both the delete-order and insert-order blocks, and say members are never touched.
- Add a short **"Backups and access"** section: what the file's member list is used for (display names shown as "People in this backup" after restore, no comparison with current members), that re-invitation is the only way to grant access, and that legacy files with identity fields still restore (identity fields ignored).
- Add the **compatibility rule**: the restore target must run the same or a newer release than the one that produced the export; an older release rejects a newer export at validation (explicit failure, nothing changed).
- **"What's out of scope"**: add `HouseholdMember` to the never-read/never-written list. Update the line in the "Validation failure reporting" area only if it shows member examples (it does not today).

`docs/data-export-format.md`:
- Entity-scope table row (line ~49) "HouseholdMember | Needed to know who belongs to the Household on restore." is now wrong: reword to "Display names only, for the 'people in this backup' list shown after a restore. Identities (OIDC issuer/subject) are deliberately not exported (AD-25)."
- `householdMembers[]` table (lines ~136–144): two rows, `id` guid and `displayName` string or null. State explicitly there is no `externalIssuer`, `externalSubjectId` or `createdAtUtc`, and that older files carrying them are still accepted (ignored).
- JSON example (line ~274): replace the member object.
- "Versioning stance" (line ~29): add that `formatVersion` stays `"v2"` across this change and the restore target must run the same or a newer release.

### Architecture compliance

- **AD-25** (this story), **AD-3** (tenant isolation unchanged), **AD-2** (portable EF only; this story *removes* EF calls, adds none, so no provider-specific code and no migration), **AD-11** (restore stays an AD-11 carve-out), **AD-1** (Application has no Infrastructure reference; the new architecture test lives in `EnergyTracker.Architecture.Tests`), **AD-26** (member trust model; removal arrives in 11.9, not here), **AD-17** (membership resolved per request; nothing is cached or stored in the cookie).
- API errors stay RFC 7807 ProblemDetails. No new endpoint, no new route, no new config value.
- Hard process gate: this change to already-shipped code is covered by this story file, which is the linked spec.

### Library / framework notes

No new packages and no version bumps. Stack as in `project-context.md`: .NET 10 / EF Core 10.0.10, xunit.v3.mtp-v2 3.2.2 with Shouldly 4.3.0 and NSubstitute 6.1.0 (NSubstitute only against Application ports), Testcontainers 4.13.0, React 19.2, Vitest 4.1, i18next 26 / react-i18next 17, oxlint. System.Text.Json default (`JsonSerializerDefaults.Web`) ignores unknown properties on deserialize and keeps `null` on serialize; that default is load-bearing for AC #2 and #3, so don't add `DefaultIgnoreCondition` or `UnmappedMemberHandling` options. Web research was not needed: no external API or library behavior changes.

### Testing standards (from project-context.md)

- One test class per subject, `{Subject}Tests`, snake_case behavior names, Shouldly only, `TestContext.Current.CancellationToken` on async tests, real Testcontainers DBs for anything DB-touching (an NSubstitute-mocked writer cannot prove AD-25; the AC #1 proof must be the real-DB dual-provider test in 1.5).
- Do the "capture old values before write" caution in reverse: the AC #1 test must compare member rows read **before** and **after** with a fresh query (`AsNoTracking`), not tracked instances.
- Frontend: colocated `*.test.tsx`; fake timers with `shouldAdvanceTime` as in the existing panel test; mock `fetch` per call. Keep locale catalogs in parity.
- Architecture tests are source-text/reflection guards; follow the structure of `HouseholdExportReaderDoesNotBypassTenantIsolationTests` (repo root found by walking up to `EnergyTracker.sln`).

### Project structure notes

- All paths above are existing files; the only new file is `tests/EnergyTracker.Architecture.Tests/HouseholdRestoreNeverCarriesAccessGrantsTests.cs` (no new production files, no migration, no config). Use `scripts/` only if you need the dev stack.
- Branch `feature/11-1-restore-never-carry-access-grants` already exists; commit prefix `feat:`/`fix:`/`doc:` and reference "Story 11.1, AD-25, audit S1/S12". Don't merge without this story file linked.

### Previous work intelligence

- **Story 7.2 (restore) code review and live pass:** the hard-won lessons are already in the code; don't undo them: validator negatives/enum `IsDefined`/`TryGetDateTimeOffset` parity with STJ (a value the validator accepts must never fail `Deserialize`); 250 MB cap plus Kestrel headroom; 409 guard for concurrent restores; `BackgroundJob`/`SmartPlugImportGap` FK `SetNull` fixes. Live restore against real data found two FK bugs that synthetic fixtures missed, so the live check in 9.2 is not optional ceremony.
- **Story 7.1 / `spec-household-export-oom-fix`:** export is streamed (`IAsyncEnumerable` → `Utf8JsonWriter`, flush every 500 rows). A `deferred-work.md` entry notes there is no round-trip test between the stream property names and `HouseholdExportResult`. Task 1.6(a) (export → import round trip with the new shape) incidentally covers the member collection; don't widen scope to the other collections.
- **Epic 10 retro / Epic 11 kickoff:** 11.1 has no preconditions from the retro action items (those gate 11.2/11.5/11.6 and the first migration-bearing deploy). 11.1 ships **no migration**, so the 10.1 restore-point/expand-contract concerns don't apply.
- **Git (last 5):** `794ffe1` epic 10 retro doc, `f3089ee` Epic 11 planning (AD-24–27), `6212bdc` dev-up/dev-down scripts (#95), then Dependabot bumps. Nothing in recent history touches restore/export; the baseline is `794ffe1`.

### References

- [Source: _bmad-artifacts/planning/epics/epic-11-codebase-audit-hardening.md#Story 11.1]
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-25]
- [Source: docs/codebase-audit-2026-10-10.md#S1, #S12]
- [Source: _bmad-artifacts/planning/sprint-change-proposal-2026-10-10.md]
- [Source: _bmad-artifacts/implementation/7-2-full-data-import-restore-migration.md], [7-1-full-data-export.md], [epic-7-retro-2026-09-26.md]
- [Source: _bmad-artifacts/project-context.md] (Testing Rules, Process gates, Security, Migrations)
- [Source: docs/data-import-restore.md], [docs/data-export-format.md]

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5.5 (claude-sonnet-5-5)

### Debug Log References

- Red phase confirmed before implementation: Application (6 failing), Architecture (3), Infrastructure writer on Postgres and SQL Server (3 each, including the headline membership test), Api (3), Vitest data-import-panel (5).
- Final: `dotnet test` 950 passed / 0 failed; `npm test` 554 passed; `npx tsc -b` clean; `npm run lint` no new warnings (4 pre-existing, none in touched files).

### Completion Notes List

- **Export (AC #2):** `HouseholdMemberExportDto` is now `(Guid Id, string? DisplayName)`; one DTO change fixes both the export mapping and the restore deserialization contract. Reader, endpoint and `formatVersion` untouched.
- **Validator (AC #3):** `ValidateHouseholdMember` requires `id` and optional `displayName` only; legacy `externalIssuer`/`externalSubjectId`/`createdAtUtc` are not validated at all (a number or garbage there is ignored).
- **Restore (AC #1):** `HouseholdRestoreData` no longer has a member list; `RestoreHouseholdData` and `HouseholdRestoreWriter` never touch `HouseholdMember`. Headline test (`A_restore_file_listing_foreign_or_no_members_leaves_the_Households_membership_untouched`) runs the real use case + writer on Postgres and SQL Server Testcontainers and compares member rows field-for-field before/after. `BackgroundJobConfiguration` keeps `SetNull` (comment-only change, no migration).
- **Summary payload (AC #4):** `HouseholdImportSummary.MemberDisplayNames` (trimmed, non-blank, file order).
- **UI (AC #4, #6):** confirm dialog drops the Household Members line and states access does not change; success state shows "People in this backup" (plain-text list, "N people without a name" line, `InviteGeneratePanel` beneath), cleared by "Import another file". en-US/de-DE keys added, old member count keys removed.
- **Architecture guard:** `HouseholdRestoreNeverCarriesAccessGrantsTests` (3 tests). This required adding a `ProjectReference` from `EnergyTracker.Architecture.Tests` to `EnergyTracker.Application` (no new package) so the DTO/restore-data types can be inspected by reflection.
- **Docs (AC #5):** `data-import-restore.md` (session continuity now unconditional, order blocks, "Backups and access", "Compatibility rule", out-of-scope) and `data-export-format.md` (versioning stance, scope table, member table, JSON example).
- **Live Chrome check (9.2) done 2026-10-10** against the local stack (Vite :5173, Postgres) with the test user, via Claude-in-Chrome: (a) the export's `householdMembers` is exactly `{id, displayName}` and the body contains no `externalIssuer`/`externalSubjectId`; (b) self-restore of that file through the real UI: still signed in (export 200), "Personen in diesem Backup" panel shows the name, "Mitglied einladen" produced an invite link; (c) restore of a hand-edited file listing a foreign identity (`attacker.test`/`evil|mallory`) and omitting the user: still signed in, member list byte-identical before/after, "Mallory" appears only as a name in the panel; (d) en-US: dialog has no members line and shows the access-unchanged sentence, panel shows the name plus "2 people without a name"; de-DE confirmed in (b)/(c). The household language was switched to EN for (d) and reset to de-DE afterwards.
- **Task 7.4 (Ask First, resolved 2026-10-10):** the first build nested `InviteGeneratePanel`'s glass card inside the import card (card-in-card; the darker rectangle was `GlassCard`'s offset rear panel `glass-card-panel-back`). Sally rendered three options (`_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-restore-people-panel-2026-10-10.html`); Ralf chose **B**: the invite action sits flat inside the people panel under a hairline. Implemented as an opt-in `bare` prop on `InviteGeneratePanel` (default unchanged, so Settings and `InviteMemberRow` are untouched); `DataImportPanel` passes `bare`. New tests: `invite-generate-panel.test.tsx` (default keeps its glass card, `bare` renders none and still generates a link) and a `data-import-panel.test.tsx` case (exactly one glass card in the success state), both seen red first.
- **Live check of B (2026-10-10), de-DE:** dark and light at 1728px (real window): flat block, button then Einladungslink field with "Link kopieren", expiry note, no nested card, no stray rectangle. At 400px the Chrome sandbox could not resize the real window (`innerWidth` stayed 1728, the known limitation in project-context), so the 400px look was checked in a same-origin iframe at a true 400px width (`innerWidth` 400), dark and light: no overflow, link field fits. **Per project-context an iframe is not an accepted substitute unless Ralf explicitly accepts it: Ralf explicitly accepted the iframe evidence for this story on 2026-10-10 (in chat, after being offered a real-window check instead).** The device theme was reset to System and the household language is de-DE again.
- No leftovers found for `deferred-work.md` (9.3).

### File List

- src/EnergyTracker.Application/ExportHouseholdData.cs
- src/EnergyTracker.Application/ValidateHouseholdImport.cs
- src/EnergyTracker.Application/RestoreHouseholdData.cs
- src/EnergyTracker.Application/Ports/IHouseholdRestoreWriter.cs
- src/EnergyTracker.Infrastructure/Adapters/HouseholdRestoreWriter.cs
- src/EnergyTracker.Infrastructure/Configurations/BackgroundJobConfiguration.cs
- src/EnergyTracker.Api/Endpoints/HouseholdImportEndpoints.cs
- tests/EnergyTracker.Application.Tests/ExportHouseholdDataTests.cs
- tests/EnergyTracker.Application.Tests/ValidateHouseholdImportTests.cs
- tests/EnergyTracker.Application.Tests/RestoreHouseholdDataTests.cs
- tests/EnergyTracker.Infrastructure.Tests/HouseholdRestoreWriterTests.cs
- tests/EnergyTracker.Api.Tests/HouseholdExportEndpointsTests.cs
- tests/EnergyTracker.Api.Tests/HouseholdImportEndpointsTests.cs
- tests/EnergyTracker.Architecture.Tests/HouseholdRestoreNeverCarriesAccessGrantsTests.cs (new)
- tests/EnergyTracker.Architecture.Tests/EnergyTracker.Architecture.Tests.csproj
- web/src/lib/household-import-api.ts
- web/src/components/data-import/data-import-panel.tsx
- web/src/components/data-import/data-import-panel.test.tsx
- web/src/components/household-invite/invite-generate-panel.tsx
- web/src/components/household-invite/invite-generate-panel.test.tsx (new)
- _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-restore-people-panel-2026-10-10.html (new, Sally)
- web/src/locales/en-US/translation.json
- web/src/locales/de-DE/translation.json
- docs/data-import-restore.md
- docs/data-export-format.md
- _bmad-artifacts/implementation/11-1-restore-export-never-carry-access-grants.md
- _bmad-artifacts/implementation/sprint-status.yaml

### Change Log

- 2026-10-10: Task 7.4 resolved with UX option B (flat invite block, opt-in `bare` prop on `InviteGeneratePanel`; Sally mock; Ralf chose B and accepted iframe evidence for the 400px check). Final regression: 950 .NET tests, 554 Vitest tests, tsc clean, no new lint warnings.
- 2026-10-10: Story 11.1 implemented (AD-25, audit S1/S12): restore never writes members, export members are `{id, displayName}`, legacy files accepted, "People in this backup" panel, docs. Live Chrome check 9.2 done. Task 7.4: UX option B (flat invite block via opt-in `bare` prop on `InviteGeneratePanel`) implemented and checked live; 400px iframe evidence accepted by Ralf.
