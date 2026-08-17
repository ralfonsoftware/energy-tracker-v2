---
baseline_commit: 83e2f4c35c1188135dd72e47937e479d1cf1c0ca
---

# Story 2.6: Room / Power Point / Device Re-parenting

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want to move a Power Point to a different Room, or a Device to a different Power Point,
so that I can reorganize my Household's tagging structure once real day-to-day use shows it no longer matches how the house is actually laid out or used.

## Acceptance Criteria

1. **Given** an existing Power Point, **when** I move it to a different Room, **then** its Room assignment is reassigned going forward — the one deliberate exception to the init-only immutability Room/PowerPoint/Device otherwise follow (deferred in Story 1.9, reintroduced here) (FR-28).
2. **Given** an existing Device, **when** I move it to a different Power Point, **then** its Power Point assignment is reassigned going forward, following the same reassignment rule as a Power Point move (FR-28).
3. **Given** a Power Point or Device with Smart Plug readings or Events already recorded against it before the move, **when** the move completes, **then** those historical rows keep displaying the Room/Power Point/Device identity snapshotted at write time — the move is never retroactively applied to past data (AD-10).
4. **Given** a Room, Power Point, or Device that is archived (soft-deleted), **when** I attempt to move a child into it, or move it as a child of another archived parent, **then** the move is rejected — an archived node can't become a new attachment point.
5. **Given** a Power Point or Device I'm moving, **when** I select a destination, **then** only non-archived Rooms (for a Power Point) or non-archived Power Points (for a Device) within the same Household are offered — cross-Household reassignment is never possible (FR-28, AD-3).
6. **Given** a move I've just made, **when** I view the tagging scaffold immediately after, **then** the moved item appears under its new parent and is no longer listed under its old parent — never duplicated under both.
7. **Given** the Room/Power Point/Device management surface in Settings (Story 1.9), **when** a move is available, **then** it's exposed as an additive "Move to…" action on the existing management UI rather than a new standalone surface — reusing the same list/detail pattern already established.

## Tasks / Subtasks

- [x] Task 1: Domain — make `RoomId`/`PowerPointId` mutable (AC #1, #2)
  - [x] `src/EnergyTracker.Domain/PowerPoint.cs`: change `public required Guid RoomId { get; init; }` to `public required Guid RoomId { get; set; }`. Remove the `// Immutable — this story does not support re-parenting...` comment (Story 1.9's deferral note this story exists to resolve) — replace with nothing, or a short note that this is now mutable via `MovePowerPoint` (Task 2), not a general-purpose setter to use elsewhere.
  - [x] `src/EnergyTracker.Domain/Device.cs`: identical change to `PowerPointId`.
  - [x] **No EF configuration change needed.** `RoomConfiguration.cs`/`PowerPointConfiguration.cs`/`DeviceConfiguration.cs`'s FK (`Restrict`, not `Cascade`) and unique-index (`RoomId+Name` / `PowerPointId+Name`) mappings are unaffected — EF maps the same column either way; `init` vs `set` is a C#-only compile-time distinction. **No migration is needed for this story** — do not run `scripts/add-migration.sh`; the column already exists and is already the correct type, only its C# mutability changes.
  - [x] `Room.cs` is untouched — Rooms have no parent to move under.

- [x] Task 2: Application — `MovePowerPoint` and `MoveDevice` use cases (AC #1, #2, #4, #5, #6)
  - [x] **Reuse `ITaggingScaffoldRepository` exactly as it stands today — no new port methods.** `FindPowerPointAsync`/`FindRoomAsync`/`ListPowerPointsAsync`/`UpdatePowerPointAsync` (and the Device equivalents) already cover everything a move needs; adding a `MoveXAsync` repository method would duplicate what `Update*Async`'s existing "the entity is already tracked by this `DbContext` instance from the `Find*Async` call, `SaveChangesAsync` alone persists the mutation" behavior already gives you for free (see `TaggingScaffoldRepository.cs`).
  - [x] `src/EnergyTracker.Application/MovePowerPoint.cs`:
    ```csharp
    public class MovePowerPoint(ITaggingScaffoldRepository repository)
    {
        public async Task<PowerPoint> ExecuteAsync(Guid powerPointId, Guid newRoomId, CancellationToken cancellationToken)
        {
            var powerPoint = await repository.FindPowerPointAsync(powerPointId, cancellationToken)
                ?? throw new TaggingScaffoldNotFoundException("PowerPoint", powerPointId);

            var siblings = await repository.ListPowerPointsAsync(cancellationToken);
            if (siblings.Any(p => p.Id != powerPointId && p.RoomId == newRoomId
                && string.Equals(p.Name, powerPoint.Name, StringComparison.Ordinal)))
            {
                throw new TaggingScaffoldValidationException($"A Power Point named '{powerPoint.Name}' already exists in this Room.");
            }

            var newRoom = await repository.FindRoomAsync(newRoomId, cancellationToken)
                ?? throw new TaggingScaffoldNotFoundException("Room", newRoomId);

            if (newRoom.ArchivedAt is not null)
            {
                throw new TaggingScaffoldParentArchivedException("Room", newRoomId);
            }

            powerPoint.RoomId = newRoomId;
            await repository.UpdatePowerPointAsync(powerPoint, cancellationToken);

            return powerPoint;
        }
    }
    ```
    Note the check order: source-exists (need `powerPoint.Name` for the duplicate check) → duplicate-name-at-destination → destination-exists → destination-archived. This matches `CreatePowerPoint`'s reviewed, current order (name/duplicate checks before parent-existence/archived checks — Story 1.9 Review Finding #5) as closely as this operation's shape allows; unlike Create, source-existence must come first here since there's no caller-supplied Name to validate independently of the source row.
  - [x] `src/EnergyTracker.Application/MoveDevice.cs`: identical shape, swapping `PowerPoint`→`Device`, `Room`→`PowerPoint`, `RoomId`→`PowerPointId`, `FindRoomAsync`→`FindPowerPointAsync`, `ListPowerPointsAsync`→`ListDevicesAsync`, `UpdatePowerPointAsync`→`UpdateDeviceAsync`.
  - [x] **Reuse existing exception types — do not add new ones.** `TaggingScaffoldNotFoundException("Room"/"PowerPoint", id)` for a missing source or destination; `TaggingScaffoldValidationException` for the duplicate-name-at-destination case; `TaggingScaffoldParentArchivedException("Room"/"PowerPoint", id)` for AC #4's archived-destination rejection — this is the exact same exception `CreatePowerPoint`/`CreateDevice` already throw for an archived parent, just triggered by a move instead of a create.
  - [x] **AC #5's tenant isolation needs zero new code.** `FindRoomAsync`/`FindPowerPointAsync` are already scoped by AD-3's `DbContext`-level query filter (Story 1.9's Dev Notes/AC #2 precedent) — a `newRoomId`/`newPowerPointId` belonging to another Household simply resolves to `null` and hits the same `TaggingScaffoldNotFoundException` (404) as a nonexistent id, not a distinct 403/409. **Do not add a manual `HouseholdId` equality check in the use case** — that would be exactly the per-handler filtering AD-3 forbids (the DbContext is the single enforcement point).
  - [x] **Moving an archived Power Point/Device is allowed** — no AC forbids it, and `RenameRoom`/`RenamePowerPoint`/`RenameDevice` already established the "editing an archived item is fine, only re-parenting *into* an archived item is rejected" precedent (Story 1.9 Dev Notes). Don't add a source-archived guard.
  - [x] AC #3 (historical Smart Plug/Event rows keep their write-time snapshot) needs **no code in this story** — `SmartPlugReading` and `Event` don't exist yet (Epic 3/Epic 6 respectively). It's structurally satisfied because `MovePowerPoint`/`MoveDevice` only ever mutate the `PowerPoint`/`Device` row's own `RoomId`/`PowerPointId` column — no other table is touched. AD-10's actual by-value-snapshot mechanism gets built and exercised when those future entities land; this story's only obligation is to not touch anything beyond the moved row itself, which the implementation above already guarantees by construction.

- [x] Task 3: Api — two new routes on the existing `TaggingScaffoldEndpoints.cs` (AC #1, #2, #4, #5)
  - [x] `PUT /api/power-points/{id}/room` — body `MovePowerPointRequest(Guid RoomId)` → `MovePowerPoint.ExecuteAsync(id, request.RoomId, cancellationToken)` → `200 OK PowerPointResponse` (reuse the existing private `ToResponse(PowerPoint)` — no DTO changes). Catch `TaggingScaffoldNotFoundException` → `404`, `TaggingScaffoldValidationException` → `400`, `TaggingScaffoldParentArchivedException` → `409` — the exact same three-catch shape `POST /api/power-points` already uses. Add inside `MapPowerPointEndpoints`, guarded by the existing `TryGetHouseholdId` helper like every other route in this file.
  - [x] `PUT /api/devices/{id}/power-point` — body `MoveDeviceRequest(Guid PowerPointId)` → same shape, inside `MapDeviceEndpoints`.
  - [x] **Route naming:** a sub-resource PUT (`/power-points/{id}/room`, `/devices/{id}/power-point`) rather than a verb route (`/power-points/{id}/move`) — matches the Consistency Conventions table's noun-based, kebab-case route naming; "replace this Power Point's Room" reads more consistently with the rest of this file's REST-ish shape than introducing the first verb-suffixed route in the codebase.
  - [x] Add `MovePowerPointRequest(Guid RoomId)` and `MoveDeviceRequest(Guid PowerPointId)` records at the bottom of the file, next to `CreatePowerPointRequest`/`CreateDeviceRequest`. No new response DTOs.
  - [x] `src/EnergyTracker.Api/Program.cs`: register `builder.Services.AddScoped<MovePowerPoint>();` and `builder.Services.AddScoped<MoveDevice>();` next to the other nine tagging-scaffold use-case registrations. No new repository/service registration needed.

- [x] Task 4: Frontend — i18n strings (AC #7)
  - [x] Add to both `web/src/locales/en-US/translation.json` and `de-DE/translation.json`'s `taggingScaffold` block (keep key-set parity, Story 1.9/1.5/1.8's discipline):
    - `moveTo` — the action button's label/aria-label and the dialog title, e.g. en: `"Move to…"`, de: `"Verschieben nach…"`.
    - `moveDescriptionPowerPoint` — dialog body for moving a Power Point, e.g. en: `"Choose a new Room. History already logged keeps its original Room, Power Point, and Device identity — this only changes where it lives going forward."` (mirrors the `key-room-management.html` mockup's copy, generalized to match this file's existing generic-not-name-interpolated style used by `confirmDeleteRoom`/etc.). de: analogous translation.
    - `moveDescriptionDevice` — same for moving a Device, s/Room/Power Point/ as appropriate.
    - `currentBadge` — label on the destination list's current-parent row, e.g. en: `"Current"`, de: `"Aktuell"`.
    - `noDestinations` — shown inside the dialog when no other non-archived destination exists in the Household, e.g. en: `"No other option is available to move to."`, de: `"Kein anderes Ziel verfügbar."`.

- [x] Task 5: Frontend — "Move to…" action + destination-list dialog in `TaggingScaffoldManager` (AC #5, #6, #7)
  - [x] `web/src/components/tagging-scaffold/tagging-scaffold-manager.tsx`. Import an additional lucide icon (`Move`) alongside the existing `ChevronRight, Pencil, Trash2`.
  - [x] Extend the `DialogState` union with two new variants: `{ kind: 'move-power-point'; powerPoint: PowerPointDto }` and `{ kind: 'move-device'; device: DeviceDto }`.
  - [x] Add a "Move to…" icon button (`variant="ghost" size="icon"`, `aria-label={t('taggingScaffold.moveTo')}`, `<Move aria-hidden="true" />`) next to the existing Rename/Delete buttons **on the Power Point row and the Device row only** — Rooms have no parent to move under, so no Move button on Room rows. Show it unconditionally (regardless of the item's own archived state — same reasoning as Rename, Task 2 above).
  - [x] Build a `roomsById` lookup (`new Map(rooms.map(r => [r.id, r]))`) alongside the existing `powerPointsByRoom`/`devicesByPowerPoint` maps — needed to label Device move destinations as "Room → Power Point" (matching the mockup's `"Wohnzimmer → Couch"` style), since a Device's destination list is Power Points, which only carry a `roomId`, not a Room name.
  - [x] Destination list, rendered inside the existing `Dialog`/`DialogContent` (reuse `GLASS_MODAL_CLASSNAME` — do **not** introduce a new sheet/overlay component; this file already renders every dialog through the one shared `Dialog`, unlike the UX mockup's bottom-sheet framing):
    - For `move-power-point`: `rooms.filter(r => !r.archivedAt)`, each rendered as a full-width row. The row whose `id === dialog.powerPoint.roomId` renders disabled with a `t('taggingScaffold.currentBadge')` badge (not clickable — matches the mockup's dimmed "Current" row); every other row is a clickable button that immediately calls a `handleMoveTo(destinationId)` action (see below) — **no separate "confirm" step**, tapping a destination *is* the confirmation, matching the mockup (only a Cancel link, no primary button in Frame 2).
    - For `move-device`: `powerPoints.filter(p => !p.archivedAt)`, each labeled `${roomsById.get(p.roomId)?.name} → ${p.name}`, same current-row/clickable-row treatment keyed off `dialog.device.powerPointId`.
    - If the filtered list has no row other than the current one, render `t('taggingScaffold.noDestinations')` instead of an empty list — don't leave a dead-looking empty dialog.
  - [x] `handleMoveTo(destinationId: string)`: mirrors `handleDelete`'s shape (not `handleSubmit`'s form-submit shape — there's no text input or `<form>` here, the click *is* the submit). Guards `dialog.kind === 'move-power-point' | 'move-device'`, captures `target = dialog`, `setSubmitting(true)`, issues `PUT /api/power-points/${dialog.powerPoint.id}/room` (body `{ roomId: destinationId }`) or `PUT /api/devices/${dialog.device.id}/power-point` (body `{ powerPointId: destinationId }`), on success replaces the moved item in `powerPoints`/`devices` state via the same `current.map((p) => (p.id === updated.id ? updated : p))` pattern every other mutation in this file already uses (AC #6 — the item simply gets a new `roomId`/`powerPointId`, so the existing `powerPointsByRoom`/`devicesByPowerPoint` grouping re-renders it under its new parent and off its old one automatically, with no duplication), then `closeDialogIfUnchanged(target)`. On error: reuse the exact same `ApiError` handling `handleSubmit` already has (409 → `errorParentArchived`, `err.detail` when present, else `errorGeneric`) — a 409 here means the same "someone else archived the destination in the meantime" race the existing create-under-archived-parent handling already names.
  - [x] Disable each destination row (and the whole handler) while `submitting` is true, same as every other in-flight-request guard in this file.

- [x] Task 6: Verify against every AC
  - [x] Extend `tests/EnergyTracker.Application.Tests/` with `MovePowerPointTests.cs` and `MoveDeviceTests.cs` (NSubstitute + Shouldly, following `CreatePowerPointTests.cs`'s pattern exactly). Cover: happy path (Room/PowerPoint reassigned, `Update*Async` called with the mutated entity); source not found; destination not found; destination archived (`TaggingScaffoldParentArchivedException`); duplicate Name already existing at the destination (`TaggingScaffoldValidationException`); moving an archived source item succeeds (no exception — AC #1/#2 don't require the source to be active); moving to the item's current parent is a harmless no-op (no exception, `Update*Async` still called — not required to special-case-skip).
  - [x] Extend `tests/EnergyTracker.Api.Tests/TaggingScaffoldEndpointsTests.cs` with Story 2.6 cases, following the existing `AC1_.../AC2_...` naming convention already in this file: a Power Point move reassigns `RoomId` in the returned/subsequently-`GET`-fetched response (AC #1); a Device move reassigns `PowerPointId` (AC #2); moving into an archived Room/Power Point returns `409` (AC #4); a principal from a different Household attempting to move into/attempting to reference another Household's Room/Power Point id gets `404` from the move route, mirroring the existing `AC2_...another_Households...` cross-tenant test's shape (AC #5); after a successful move, a `GET /api/power-points`/`/api/devices` reflects the new parent and the item is absent from the old parent's grouping when the frontend groups by id (AC #6 — assert on the flat `RoomId`/`PowerPointId` field the backend returns, since grouping itself is a frontend concern).
  - [x] Extend `web/src/components/tagging-scaffold/tagging-scaffold-manager.test.tsx` (mocked `fetch`, following the existing pattern): opening "Move to…" on a Power Point renders the Room destination list with the current Room tagged/disabled; clicking a non-current destination issues the `PUT .../room` request and the tree re-renders the Power Point under its new Room; same for a Device and `PUT .../power-point`; a household with only one non-archived Room/Power Point shows `noDestinations` instead of an empty list; a `409` response shows `errorParentArchived`.

- [x] Task 7: Documentation
  - [x] No `docs/*.md` changes expected — no new operator-facing configuration surface, no new migration, no new adapter/env var (same conclusion Story 1.9's Task 10 reached). Confirm this still holds once implementation is done; update only if something unexpected surfaces.

## Dev Notes

- **This story's entire backend surface is additive to Story 1.9's existing `TaggingScaffoldEndpoints.cs`/`ITaggingScaffoldRepository` — no existing route, DTO, or repository method changes shape.** The only Domain-level change is flipping two `init` accessors to `set` (Task 1); everything else is two new use-case classes and two new routes layered on infrastructure Story 1.9 already built. This directly resolves Story 1.9's own deferral note: *"No re-parenting... in this story... If this turns out to be needed later, it's an additive change (a new `MovePowerPoint` use case), not a rework of this story's shape."* — that prediction is exactly what this story implements.
- **No migration.** Unlike almost every other tagging-scaffold story, this one changes no schema — `RoomId`/`PowerPointId` are already the correct mapped column type; only their C# property's write-accessibility changes. Do not run `scripts/add-migration.sh` for this story.
- **No new repository port methods, no new exception types.** Both `MovePowerPoint`/`MoveDevice` are expressible entirely in terms of `ITaggingScaffoldRepository`'s nine existing methods and the three exception types Story 1.9 already defined (`TaggingScaffoldNotFoundException`, `TaggingScaffoldValidationException`, `TaggingScaffoldParentArchivedException`). Adding a fourth exception type or new port methods would be inventing structure this story doesn't need — reuse is the point here, not extension.
- **AD-3 tenant isolation is entirely free.** Because `FindRoomAsync`/`FindPowerPointAsync` already run through the standard, non-exempt AD-3 query filter (Story 1.9 was the first story to wire that up), a cross-Household destination id simply doesn't resolve — no manual `HouseholdId` check belongs in `MovePowerPoint`/`MoveDevice`. Adding one would violate AD-3's "DbContext is the single enforcement point, no per-handler filtering" rule.
- **Duplicate-name-at-destination reuses the exact unique-index/app-check pattern Story 1.9's code review already established** for Create/Rename (`(RoomId, Name)` / `(PowerPointId, Name)` unique indexes plus an app-layer pre-check for a clean `400` instead of surfacing a raw `DbUpdateException`). No index or config changes needed — the existing indexes already cover the moved-to scope.
- **UX reference:** `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-room-management.html`, Frame 2 ("Move to…") is the canonical visual reference — a destination list with the current parent shown, tagged "Current", and dimmed/non-interactive; tapping any other destination *is* the confirmation (no separate primary "Move" button, only Cancel). The mockup renders this as a bottom sheet; **this codebase's actual `TaggingScaffoldManager` renders every dialog through the shared shadcn `Dialog`/`GLASS_MODAL_CLASSNAME`, not a bespoke sheet** — follow the mockup's *interaction* shape (list, current-tagged, tap-to-move), not its literal sheet markup.
- **Move button shown regardless of the item's own archived state**, matching `RenameRoom`/`RenamePowerPoint`/`RenameDevice`'s established "editing an archived item is allowed" precedent (Story 1.9 Dev Notes) — no AC restricts moving an archived source item, only moving *into* an archived destination (AC #4).
- **AC #3 (historical snapshot integrity) has no code to write in this story.** `SmartPlugReading` (Epic 3) and `Event` (Epic 6) don't exist yet. The AC is satisfied structurally: `MovePowerPoint`/`MoveDevice` only ever mutate the moved row's own FK column, never any other table — when the future entities land with their AD-10 by-value snapshot fields, this story's move mechanism is already safe by construction. Don't build placeholder snapshot-preservation logic for tables that don't exist.
- **Constraints that still apply, unchanged:** AD-1 (Domain stays plain C#), AD-2 (n/a — no migration this story), AD-3 (see above), AD-10 (the core invariant this story's whole design defers to for AC #3), AD-18 (all new i18n strings go through the catalog, Task 4, no inline literals), NFR3/NFR4 (every new route stays inside the existing `/api` `RequireAuthorization()` group, guarded by the existing `TryGetHouseholdId` helper).

### Project Structure Notes

New/modified files this story introduces:

```text
energy-tracker-v2/
  src/
    EnergyTracker.Domain/
      PowerPoint.cs                                   # modified — RoomId init -> set
      Device.cs                                        # modified — PowerPointId init -> set
    EnergyTracker.Application/
      MovePowerPoint.cs                                 # new
      MoveDevice.cs                                      # new
    EnergyTracker.Api/
      Endpoints/
        TaggingScaffoldEndpoints.cs                       # modified — 2 new routes + 2 new request DTOs
      Program.cs                                          # modified — DI for MovePowerPoint/MoveDevice
  web/
    src/
      locales/de-DE/translation.json, en-US/translation.json  # modified — moveTo/moveDescription*/currentBadge/noDestinations keys
      components/
        tagging-scaffold/
          tagging-scaffold-manager.tsx                        # modified — Move to… action + destination-list dialog
  tests/
    EnergyTracker.Application.Tests/
      MovePowerPointTests.cs                             # new
      MoveDeviceTests.cs                                  # new
    EnergyTracker.Api.Tests/
      TaggingScaffoldEndpointsTests.cs                    # modified — Story 2.6 cases
    web/src/components/tagging-scaffold/tagging-scaffold-manager.test.tsx  # modified
```

No changes expected under `EnergyTracker.Infrastructure/`, `EnergyTracker.Infrastructure.Migrations.*/`, or `EnergyTracker.Domain/Room.cs` — Rooms have no parent to move under, and no EF configuration/migration change is needed (see Dev Notes).

### References

- [Source: _bmad-artifacts/planning/epics/epic-2-meter-reading-pattern-detective-status-core.md#Story 2.6] — story statement and acceptance criteria (verbatim origin)
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md#FR-28] — Room/Power Point/Device Management FR; note its listed consequences predate this story's re-parenting extension (added post-Epic-1-retro, see `epics/requirements-inventory.md#FR-28`)
- [Source: ...ARCHITECTURE-SPINE/invariants-rules.md#AD-3] — data-layer tenant isolation via DbContext global query filter; this story's destination lookups rely on it entirely, no new enforcement code
- [Source: ...ARCHITECTURE-SPINE/invariants-rules.md#AD-10] — historical tag integrity: soft-delete + by-value snapshot at write time; basis for AC #3's "no code needed yet" reasoning
- [Source: ...ARCHITECTURE-SPINE/consistency-conventions.md] — kebab-case route naming (basis for the `/room`/`/power-point` sub-resource PUT route choice over a verb route)
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-room-management.html] — Frame 2, the canonical "Move to…" interaction reference (destination list, current-tagged/dimmed row, tap-to-move, no separate confirm button)
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/review-accessibility-2026-08-16.md] — icon-only action buttons need a real `aria-label` (not just `title`) and an adequate tap target; this story's new Move button follows the same `aria-label` pattern the existing Rename/Delete buttons in this file already use
- [Source: _bmad-artifacts/implementation/1-9-room-power-point-device-management.md] — previous story: the entire tagging-scaffold foundation (entities, port, use-case shape, endpoint file, i18n catalog, `TaggingScaffoldManager` component) this story extends; its Dev Notes explicitly predicted this story's shape ("an additive change... not a rework"); its Review Findings established the duplicate-name-at-destination check pattern and the `TryGetHouseholdId`-guard-every-route convention this story reuses verbatim
- [Source: src/EnergyTracker.Domain/PowerPoint.cs, Device.cs] — current `init`-only `RoomId`/`PowerPointId` this story makes mutable
- [Source: src/EnergyTracker.Application/CreatePowerPoint.cs, CreateDevice.cs, RenameDevice.cs, ArchivePowerPoint.cs] — existing use-case shape/style `MovePowerPoint`/`MoveDevice` match; `CreatePowerPoint`'s reviewed check-order (name/duplicate before parent-existence/archived) this story's check-order follows as closely as its shape allows
- [Source: src/EnergyTracker.Application/TaggingScaffoldNotFoundException.cs, TaggingScaffoldValidationException.cs, TaggingScaffoldParentArchivedException.cs] — the three exception types this story reuses without modification
- [Source: src/EnergyTracker.Application/Ports/ITaggingScaffoldRepository.cs] — the repository port this story adds zero new methods to
- [Source: src/EnergyTracker.Infrastructure/Configurations/PowerPointConfiguration.cs, DeviceConfiguration.cs] — existing FK (`Restrict`)/unique-index (`RoomId+Name`/`PowerPointId+Name`) mappings this story relies on unchanged
- [Source: src/EnergyTracker.Api/Endpoints/TaggingScaffoldEndpoints.cs] — existing `TryGetHouseholdId` guard, three-exception-catch shape, and `ToResponse` mapping this story's two new routes reuse directly
- [Source: src/EnergyTracker.Api/Program.cs] — existing DI registration pattern for the nine Story 1.9 use cases, extended by two more here
- [Source: web/src/components/tagging-scaffold/tagging-scaffold-manager.tsx] — the component this story extends: `DialogState` union, `ApiError`/`toApiError`, `openDialog`/`closeDialog`/`closeDialogIfUnchanged`, the `Dialog`/`GLASS_MODAL_CLASSNAME` rendering pattern, and the existing Rename/Delete icon-button row shape the new Move button matches
- [Source: web/src/locales/en-US/translation.json, de-DE/translation.json] — existing `taggingScaffold.*` key set this story extends, parity discipline unchanged

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

None — implementation went green on the first pass for every task; no debugging detours needed.

### Completion Notes List

- Task 1: Flipped `PowerPoint.RoomId` and `Device.PowerPointId` from `init` to `set`. No EF configuration or migration change, as predicted by Dev Notes.
- Task 2: Added `MovePowerPoint`/`MoveDevice` use cases exactly as specified in the story — reused `ITaggingScaffoldRepository`'s existing nine methods and the three existing exception types, no new port methods or exception types added.
- Task 3: Added `PUT /api/power-points/{id}/room` and `PUT /api/devices/{id}/power-point` to `TaggingScaffoldEndpoints.cs`, reusing the existing three-catch (`404`/`400`/`409`) shape and `TryGetHouseholdId` guard. Registered `MovePowerPoint`/`MoveDevice` in `Program.cs`.
- Task 4: Added `moveTo`/`moveDescriptionPowerPoint`/`moveDescriptionDevice`/`currentBadge`/`noDestinations` keys to both `en-US` and `de-DE` catalogs, keeping key-set parity.
- Task 5: Added a "Move to…" icon button on the Power Point and Device rows in `TaggingScaffoldManager`, a `roomsById` lookup, a destination-list dialog (current parent tagged and disabled, tap-to-move with no separate confirm step), and `handleMoveTo` mirroring `handleDelete`'s shape with the same `ApiError`/409 handling as the rest of the file.
- Task 6: Added `MovePowerPointTests.cs`/`MoveDeviceTests.cs` (7 cases each: happy path, source not found, destination not found, destination archived, duplicate name at destination, archived source allowed, move-to-current-parent no-op), 5 new `TaggingScaffoldEndpointsTests.cs` cases covering AC #1/#2/#4/#5/#6 end-to-end through the real API + Postgres Testcontainer, and 4 new `tagging-scaffold-manager.test.tsx` cases (Power Point move destination list + re-render, Device move + re-render, `noDestinations` empty state, 409 → `errorParentArchived`).
- Task 7: Confirmed no `docs/*.md` changes were needed — no new operator-facing config, migration, or adapter/env var surfaced during implementation.
- Full regression: 238 backend tests green (dotnet build clean in Debug and Release), 102 frontend tests green (17 in `tagging-scaffold-manager.test.tsx`, 4 new files' worth net-new), `tsc -b`/`oxlint`/`vite build` all clean. Zero `docs/*.md` changes.
- Code review (2026-08-17): 2 findings, both fixed.
  - **Correctness (fixed):** the destination-list "no other option" check compared `destinations.length <= 1`, silently assuming the current parent is always present in the non-archived-filtered list. Since `ArchiveRoom`/`ArchivePowerPoint` don't cascade-archive their children, a Power Point/Device can end up with an archived current parent that drops out of that filter — with exactly one other valid destination, the check misfired as "no destinations" and blocked a legitimate move. Fixed by checking `items.some(item => item.id !== currentId)` instead of a length threshold. Added a regression test ("still offers a valid destination when the Power Point's current Room has since been archived").
  - **Simplification (fixed):** the Room-destination and Power-Point-destination branches duplicated near-identical IIFE logic. Extracted a shared `MoveDestinationList` generic component used by both.
- Post-fix regression: 103 frontend tests green (18 in `tagging-scaffold-manager.test.tsx`), `tsc -b`/`oxlint`/`vite build` clean. No backend files touched by the fix.

### File List

- `src/EnergyTracker.Domain/PowerPoint.cs` (modified)
- `src/EnergyTracker.Domain/Device.cs` (modified)
- `src/EnergyTracker.Application/MovePowerPoint.cs` (new)
- `src/EnergyTracker.Application/MoveDevice.cs` (new)
- `src/EnergyTracker.Api/Endpoints/TaggingScaffoldEndpoints.cs` (modified)
- `src/EnergyTracker.Api/Program.cs` (modified)
- `web/src/locales/en-US/translation.json` (modified)
- `web/src/locales/de-DE/translation.json` (modified)
- `web/src/components/tagging-scaffold/tagging-scaffold-manager.tsx` (modified)
- `tests/EnergyTracker.Application.Tests/MovePowerPointTests.cs` (new)
- `tests/EnergyTracker.Application.Tests/MoveDeviceTests.cs` (new)
- `tests/EnergyTracker.Api.Tests/TaggingScaffoldEndpointsTests.cs` (modified)
- `web/src/components/tagging-scaffold/tagging-scaffold-manager.test.tsx` (modified)
