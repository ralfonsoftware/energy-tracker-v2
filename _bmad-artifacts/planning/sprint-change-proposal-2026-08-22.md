# Sprint Change Proposal — 2026-08-22

**Trigger:** Architecture planning for Story 3.4 (Incremental Smart-Plug Import), Ralf + Winston (System Architect)
**Mode:** Incremental review, all edits approved

## 1. Issue Summary

While drafting Story 3.4 as a resource-efficiency follow-up to Epic 3's already-shipped stories (3.1–3.3), inspection of the current `ProcessSmartPlugImport`/`EveHomeXlsxParser`/`SmartPlugImportRepository` implementation, combined with two new dated Eve Home sample files Ralf added to `sample-data/eve`, surfaced a requirement gap rather than just a performance opportunity:

- Eve Home's export is **cumulative, not incremental** — every download contains the device's entire history, growing on every export. Confirmed against three dated HiFi samples for the same device: 108,715 rows (2026-06-20) → 114,815 rows (2026-08-01) → 117,782 rows (2026-08-22), still reaching back to the plug's first-ever reading on 2023-05-27.
- Meross exports are manually date-ranged by the household with no surfaced record of the prior import's end date — Ralf confirmed he never checks the last export date before selecting a new range, so an overlapping re-export is his own normal workflow, not a rare mistake.
- `SmartPlugReading` (`SmartPlugReadingConfiguration.cs`) carries **no uniqueness constraint**, and `SmartPlugImportRepository.AddAsync` blind-inserts every parsed row. A re-upload with overlapping date range therefore silently **duplicates** already-stored readings rather than merely wasting time reprocessing them — inflating `SmartPlugGapDetector`'s daily kWh totals and, through it, FR-5's baseline-sharpening signal, a little more with every re-import.
- The existing PRD guarantee closest to this (`cross-cutting-nfrs.md` NFR10, "Data integrity under concurrency") only states the *lose-an-update* failure mode for *simultaneous* overlapping imports. It does not state the *opposite* failure mode this issue actually is — a silently *duplicated* write from a *sequential* (non-concurrent) overlapping re-import — so the PRD did not previously commit the product to preventing it.

**Category:** Technical limitation discovered during implementation planning, surfacing an unstated requirement gap (not a stakeholder-driven pivot, not a misunderstanding of an existing requirement).

## 2. Impact Analysis

**Epic Impact:**
- Epic 3 (status: `in-progress`, stories 3.1–3.3 done) gains a 4th story, drafted directly into `epic-3-smart-plug-import-baseline-sharpening.md`. No redefinition of Epic 3's existing scope; no rollback of 3.1–3.3's shipped behavior — the fix is additive/corrective, not a redesign.
- Epics 4–7: no scope change. Epic 7 (Data Export & Import) will need to respect the new `(PowerPointId, IntervalStart)` uniqueness constraint once it's built, which is forward-compatible, not a conflict.
- No epic resequencing needed.

**Artifact Conflicts:**
- **PRD** (`cross-cutting-nfrs.md`, NFR10): needed broadening — done.
- **Architecture spine** (`invariants-rules.md`): needed a new binding rule distinct from AD-9 (parsing) and AD-16 (a different entity's idempotency mechanism) — new AD-20 added.
- **UX**: no impact — backend-only correctness/performance fix, no new user-facing surface.
- **Other**: `requirements-inventory.md` (mirrors PRD/architecture — updated), epic-3 header's NFR/Architecture lines (updated), `sprint-status.yaml` (new backlog entry + changelog — updated).

**Technical Impact:** Confined to the Smart Plug import pipeline (`ISmartPlugParser` adapters, `SmartPlugImportRepository`, a new dual-provider EF migration). No infra/CI/CD/deployment impact.

## 3. Recommended Approach

**Selected: Option 1 — Direct Adjustment.**

- Extend NFR10's wording, add AD-20, add Story 3.4 to the backlog — all within the existing epic/PRD structure.
- Rollback (Option 2) does not apply: nothing shipped needs reverting: Story 3.1–3.3's behavior stands, this closes a gap discovered before it caused visible harm.
- MVP scope review (Option 3) does not apply: no scope reduction, no goal change — this closes a correctness gap rather than opening one.

**Effort:** Low for this correct-course pass (documentation only). Story 3.4's actual implementation is separately scoped and estimated when it's picked up for a sprint.
**Risk:** Low. The fix is additive (new constraint + one-time cleanup + parser optimization); no existing behavior is removed.

## 4. Detailed Change Proposals

### PRD — `prd/cross-cutting-nfrs.md`

```diff
- **Data integrity under concurrency:** concurrent writes to the same Household's
- data (e.g. simultaneous Meter Readings, overlapping Smart Plug imports) never
- silently lose an update. The exact conflict-resolution UX (reject, merge,
- last-write-wins) is an architecture-phase decision guided by this
- no-silent-data-loss guarantee.
+ **Data integrity under concurrent and repeated writes:** concurrent writes to
+ the same Household's data (e.g. simultaneous Meter Readings, overlapping Smart
+ Plug imports) never silently lose an update. Separately, a write path that can
+ legitimately receive the same logical data more than once (e.g. a Smart Plug
+ export re-uploaded with a date range overlapping a prior import) never
+ silently duplicates it either — repeated/overlapping submissions of
+ already-recorded data are idempotent, not additive. The exact
+ conflict-resolution UX and the exact duplicate-detection mechanism are both
+ architecture-phase decisions guided by this no-silent-loss /
+ no-silent-duplication guarantee.
```

### `epics/requirements-inventory.md`

- NFR10 line reworded to match the PRD change above.
- New AD-20 line added to the Architecture Decisions list, alongside AD-9/AD-16.

### Architecture spine — `ARCHITECTURE-SPINE/invariants-rules.md`

New section appended:

```markdown
## AD-20 — Duplicate-safe Smart-Plug reading writes

- **Binds:** FR-5, FR-24 (Cross-Cutting NFR10)
- **Prevents:** a re-imported Smart Plug export silently duplicating
  already-stored readings when its covered date range overlaps a prior
  import — the expected normal case for both vendor formats, not a rare
  mistake.
- **Rule:** `SmartPlugReading` carries a database-level uniqueness constraint
  on `(PowerPointId, IntervalStart)` — the actual correctness guarantee,
  independent of any parser-level optimization. `ISmartPlugParser` adapters
  may additionally skip/filter already-covered rows before persisting as a
  performance optimization, but that optimization is not itself the
  duplicate-safety mechanism.
```

### `epics/epic-3-smart-plug-import-baseline-sharpening.md`

```diff
  **FRs covered:** FR-4, FR-5, FR-24
- **NFRs:** NFR1 (Tier 3 async)
- **Architecture:** AD-6, AD-9, AD-10
+ **NFRs:** NFR1 (Tier 3 async), NFR10 (no-silent-duplication on repeated writes)
+ **Architecture:** AD-6, AD-9, AD-10, AD-20
```

Story 3.4 itself (full AC set, Dev Notes) was already drafted into this file earlier in this planning session.

### `implementation/sprint-status.yaml`

- Added `3-4-incremental-smart-plug-import: backlog` under the `epic-3` block.
- Added a dated changelog comment explaining the trigger and the cross-referenced PRD/architecture changes.

**All five edits applied and approved (incremental review, "Approve all").**

## 5. Implementation Handoff

**Change scope: Minor** (documentation/planning artifacts only — no code changed by this correct-course pass).

- **This pass (Winston, System Architect):** PRD NFR wording, new AD-20, requirements-inventory.md, epic-3 header, sprint-status.yaml — all complete as of this proposal.
- **Story 3.4 implementation (future, Developer agent / Amelia via `bmad-dev-story`):** build against the ACs already in `epic-3-smart-plug-import-baseline-sharpening.md` — watermark-based incremental parse (Eve: early-stop on newest-first order; Meross: filter, unordered), DB-level `(PowerPointId, IntervalStart)` uniqueness constraint via `scripts/add-migration.sh` (both providers), one-time dedup cleanup migration (tie-break: most-recent-import wins), Testcontainers verification on both providers.
- **Before scheduling Story 3.4 into a sprint:** run `bmad-check-implementation-readiness` to confirm PRD/Architecture/Epic alignment holds now that NFR10/AD-20 exist.

**Success criteria:** Story 3.4, when built, traces cleanly to NFR10 and AD-20 with no further PRD/architecture amendment needed; a re-import with overlapping data (either vendor) neither duplicates rows nor reprocesses data older than the Power Point's last stored reading.
