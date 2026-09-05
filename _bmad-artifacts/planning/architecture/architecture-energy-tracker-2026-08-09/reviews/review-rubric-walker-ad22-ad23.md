# Rubric Walker Review — AD-22 & AD-23

**Scope:** `ARCHITECTURE-SPINE/invariants-rules.md` (full read, focused scrutiny on AD-22/AD-23), `deferred.md`, `index.md`, cross-checked against the live codebase (`src/EnergyTracker.*`).

**Verdict:** Both ADs are directionally sound and individually well-specified, but AD-23 has a critical scope gap against an already-existing second write path in the actual codebase, and AD-22 leaves the parser-vs-orchestration split of its comparison logic (and thus the `ISmartPlugParser` port signature) unresolved — both are the kind of ambiguity that lets independently-built stories comply with the letter of the rule while landing incompatible implementations. Recommend one more revision pass before sign-off.

---

## Critical / High findings

### 1. [CRITICAL] AD-23's Rule names only `AddAsync`; it misses `UpdateMappingAsync`, an already-existing second conflict-tolerant write mechanism for the same entity

AD-23's own **Prevents** clause is: *"a second, independently-built bulk-write mechanism growing alongside this one."* But one already exists, in the same file, today:

`src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs`:
- `AddAsync` (line 13) — the mechanism AD-23's Rule explicitly names and replaces: `AnyExistingReadingAtSameKeyAsync` pre-check → `AddRangeAsync`/`SaveChangesAsync` fast path → catch-`DbUpdateException`-then-`AddWithPerRowConflictToleranceAsync` fallback.
- `UpdateMappingAsync` (line 202) — structurally the *same shape*, over the *same entity* (`SmartPlugReading`), and never mentioned by AD-23: `AnyMappingConflictAsync` pre-check → `ExecuteUpdateAsync` set-based fast path → catch-`DbUpdateException`/`DbException`-then-`UpdateMappingPerRowWithConflictToleranceAsync` fallback.

This is the Power Point mapping-assignment write path (`MapSmartPlugImportToPowerPoint.cs`, backing the recently-touched mapping dialog feature per git history) — a real, live, already-independently-built instance of exactly the divergence AD-23 says it exists to prevent, sitting untouched right next to the mechanism AD-23 does convert.

AD-23's **Binds** line ("all `SmartPlugReading` writes... both vendor adapters, both the one-time full-history first import and the routine small incremental delta") reads broad enough to imply `UpdateMappingAsync` is covered, but the **Rule** text only ever names `SmartPlugImportRepository.AddAsync`. This gap is exactly the "two units, letter-compliant, still diverge" scenario the checklist asks for:
- **Story A** reads the Binds line literally ("all SmartPlugReading writes"), converts `UpdateMappingAsync` to `BulkInsertOrUpdateAsync` too (awkward fit — it's a same-row attribute update matched by `SmartPlugImportId`, not the `[PowerPointId, IntervalStart]` key AD-23 specifies).
- **Story B** reads the Rule literally (only `AddAsync` is named), leaves `UpdateMappingAsync`'s own bespoke pre-check + `ExecuteUpdateAsync` + per-row-fallback pattern permanently in place.

Both comply with AD-23's text as written. The result: one write path migrates to `EFCore.BulkExtensions`, the other keeps EF Core's native `ExecuteUpdateAsync` conflict-tolerance pattern indefinitely — precisely the "second, independently-built... mechanism" AD-23 claims to prevent, and the AD itself is silent on which is correct. **This needs an explicit call-out: either AD-23 is scoped to insert-only import paths and `UpdateMappingAsync` is explicitly out of scope (with a one-line reason — e.g., it's a targeted attribute UPDATE by a different key, not a duplicate-tolerant bulk INSERT/UPSERT), or `UpdateMappingAsync` needs its own migration plan under this AD.**

### 2. [HIGH] AD-22 + AD-23 combine to risk silently rewriting AD-10-protected historical attribution — a direct cross-AD contradiction

AD-22's stated intent is narrow: on a watermark-boundary mismatch, *"the stored `SmartPlugReading.KwhValue` is updated... and the change is recorded."* Only `KwhValue` is named.

But AD-23's actual write mechanism for that same row is `BulkInsertOrUpdateAsync` with `UpdateByProperties = [PowerPointId, IntervalStart]` and `PropertiesToExclude = [Id]` — i.e., **every column except `Id` gets overwritten** on a match. Checking `SmartPlugReading`'s real shape (`src/EnergyTracker.Domain/SmartPlugReading.cs`, `SmartPlugReadingConfiguration.cs`) confirms this includes `HouseholdId`, `SmartPlugImportId`, `PowerPointId`, `RoomName`, `PowerPointName`, `DeviceName`, `IntervalEnd` — not just `KwhValue`.

`RoomName`/`PowerPointName`/`DeviceName` are exactly the fields AD-10 exists to protect: *"snapshot the Room/PowerPoint/Device identity by value at write time... a live FK-join would incorrectly rewrite history to reflect the new assignment."* If a Power Point is reassigned to a different Room between the original Eve Home import and a later corrective re-import that trips AD-22's boundary-row check, the correction's re-derived `RoomName`/`PowerPointName` values for that one boundary row would silently overwrite the historically-correct snapshot — reproducing, via upsert, the exact "live-join rewrites history" failure mode AD-10 was written to close off. AD-22 never carves out an exception, and AD-23's blanket `PropertiesToExclude=[Id]` doesn't protect anything but the primary key.

Concretely, this is the requested "two units, letter-compliant, still diverge" scenario for AD-23: a story implementing the boundary-row correction path that (correctly, in the spirit of AD-10) adds `RoomName`/`PowerPointName`/`DeviceName`/`SmartPlugImportId` to `PropertiesToExclude` diverges from a story that follows AD-23's literal `PropertiesToExclude=[Id]` text — and only the second one reproduces a real historical-attribution bug. **AD-22 or AD-23 needs to explicitly state which columns a boundary-row correction is allowed to touch** (arguably: `KwhValue` and nothing else, meaning the write for that one row can't go through the generic blanket-upsert AD-23 defines without a per-call exclusion list).

### 3. [HIGH] AD-22's comparison logic is unassigned to a layer, and doing it "in the parser" requires an unstated `ISmartPlugParser` port change

AD-22's Rule reads: *"`EveHomeXlsxParser`'s forward-streaming read no longer skips the exact boundary row... it reads that row and compares its parsed `KwhValue` against the stored value."* This assigns the comparison to the parser. But the actual `ISmartPlugParser.Parse` signature today is:

```
SmartPlugParseResult Parse(Stream fileContent, string fileName, DateTimeOffset? watermark, CancellationToken cancellationToken = default)
```

— `watermark` is `DateTimeOffset?` only. The port's own doc comment states the current, shared contract: *"Non-null means 'only rows with `IntervalStart` strictly greater than watermark belong in the result'"* — a contract AD-9 binds **both** adapters to (`EveHomeXlsxParser` and `MerossCsvParser`). For the parser to do the comparison AD-22 describes, it needs the watermark's *stored `KwhValue`* too, which means either:

- **(a)** `ISmartPlugParser.Parse`'s signature changes (e.g. to a `(DateTimeOffset IntervalStart, decimal KwhValue)?` pair) — a port-level change binding both adapters, forcing `MerossCsvParser` to also accept and reason about a parameter AD-22 never mentions it needing, or
- **(b)** the port signature stays untouched, the parser's early-stop threshold changes from `<=` to `<` (letting the boundary row flow out as an ordinary new `SmartPlugReading`), and the actual value-comparison + audit-trigger logic lives entirely in the repository/application layer that already holds both the new reading and the old stored value (via the extended `FindLatestReadingIntervalStartByPowerPointAsync`).

AD-22's text is compatible with either reading. Two stories built independently — one changing the shared port signature, one leaving it alone and pushing the diff to the orchestration layer — both comply with AD-22 as written, yet produce incompatible `ISmartPlugParser` contracts, and only one of them touches `MerossCsvParser` at all. **AD-22 should say explicitly whether the `ISmartPlugParser` port signature changes, and if so, what `MerossCsvParser` is required to do with the new parameter (nothing, per the AD's own "explicitly out of scope" note — but that should be stated for the port, not just for the corruption-detection scope).**

---

## Medium findings

### 4. [MEDIUM] `capability-architecture-map.md` was not updated for AD-22/AD-23

The "Smart Plug Import (FR-4, FR-24)" row still lists only `AD-6, AD-9, AD-7 (StatusSnapshot trigger), AD-3 (job-context isolation)`. AD-22 and AD-23 both explicitly bind FR-4/FR-24 but are absent from this row. Since the capability map is the discoverability index a story author is expected to consult to find "what governs this feature," this is a real (if easily fixed) gap — a story built by consulting only the map would miss both new ADs entirely.

### 5. [MEDIUM] AD-11's Binds line is now stale

AD-11 states *"Binds: Meter Reading edits, Tariff edits"* and frames itself around those two entities. AD-22 reuses `IAuditCorrectionRecorder` for a third caller (`SmartPlugReading.KwhValue` corrections) and says so explicitly ("this reuses AD-11's mechanism as-is"), which is consistent with AD-11's own forward-looking clause ("any future import path that does merge into existing data must route through this AD's mechanism"). But AD-11's Binds line itself was never updated to list this new caller — low-cost fix, but the spine's own convention (Binds line as the authoritative "who depends on this" index) is now inaccurate for AD-11.

---

## Low findings

### 6. [LOW] AD-9's `ISmartPlugParser.Parse` XML doc comment isn't updated to reflect AD-22's boundary-row change

The interface's own doc comment (`ISmartPlugParser.cs`) still documents the pre-AD-22 contract ("only rows with `IntervalStart` strictly greater than watermark belong in the result") with no acknowledgment that `EveHomeXlsxParser` now deviates from it for exactly one row. Not spine-breaking, but it's the kind of drift between the spine and the actual port contract that produces exactly this review's core complaint down the line.

### 7. [LOW] No AD-23 guidance on cancellation semantics vs. AD-16's idempotency-key model

AD-23's "Condition before wide rollout" flags verifying "no partial row survives cancellation" via spike — good practice, appropriately gated rather than silently assumed. Worth noting only that this doesn't yet cross-reference AD-16 (Meter Reading's idempotency-key pattern) even though both are solving adjacent "don't half-write on retry/cancel" problems for different entities; not a contradiction, just a missed opportunity to note they're deliberately different mechanisms for different NFRs (offline client retry vs. server-side spike gate).

---

## Checklist walkthrough

- **Fixes the real divergence points, misses none:** No — see Finding 1 (`UpdateMappingAsync` untouched) and Finding 3 (port-signature ambiguity). AD-22/AD-23 correctly identify and fix the primary divergence (corruption detection, bulk-write duplication) but leave secondary, already-real divergence points open.
- **Every AD's Rule is enforceable and prevents its stated divergence:** Partially. AD-23's `UpdateByProperties`/`PropertiesToExclude` values are concrete and grep-able (enforceable), but "prevents a second bulk-write mechanism" is falsified by the codebase today (Finding 1). AD-22's repository-method signature change is enforceable; the parser-side prose is not (Finding 3).
- **Nothing under Deferred is load-bearing enough to risk silent divergence:** Deferred.md is clean here — "Device swap under the same Power Point name" correctly cross-references AD-22's explicit out-of-scope carve-out, and the NFR1 Tier-3 budget is correctly gated on AD-23's own spike. No findings against Deferred itself; the gaps found are inside AD-22/AD-23's own text, not wrongly-deferred items.
- **Named tech is verified-current and internally consistent:** Yes. Verified against NuGet and the repo's own `Directory.Packages.props`: EF Core is pinned at `10.0.10` (matches `stack.md`'s "EF Core 10" and AD-23's "targeting EF Core 10"), and `EFCore.BulkExtensions` / `EFCore.BulkExtensions.Core` / `.SqlServer` / `.PostgreSql` all publish at `10.0.1` on NuGet today. The cFOSS dual-license claim (free under $1M USD gross revenue, commercial license above) is accurate per the project's own GitHub license page.
- **Ratifies rather than contradicts the brownfield codebase:** Mixed. AD-22 vs. AD-9/AD-11/AD-20 is consistent (AD-11 explicitly anticipated this kind of reuse). AD-23 vs. AD-20 is consistent in mechanism (same unique index) but changes duplicate-handling *semantics* from "reject/no-op" to "unconditional overwrite" without flagging that shift. AD-23 vs. AD-10, combined with AD-22, is where a real contradiction risk lives (Finding 2).
- **No AD-22/AD-23 text renumbers or overwrites an existing AD:** Confirmed clean. Both are strictly appended after AD-21 in `invariants-rules.md`, IDs 1–21 are untouched, and `index.md`'s table of contents/anchors match the new sections correctly.

## Constructed divergence scenarios (as requested)

- **AD-22:** Team A extends `ISmartPlugParser.Parse`'s `watermark` parameter to a `(IntervalStart, KwhValue)` pair and does the comparison inside `EveHomeXlsxParser` (also touching `MerossCsvParser`'s signature since it's a shared interface). Team B leaves the port untouched, changes the early-stop from `<=` to `<`, and does the comparison in the repository/orchestration layer using the newly-extended `FindLatestReadingIntervalStartByPowerPointAsync`. Both satisfy AD-22's text; the two produce incompatible `ISmartPlugParser` contracts and only one touches Meross at all. See Finding 3.
- **AD-23:** Team A writes the AD-22 boundary-row correction through the standard `BulkInsertOrUpdateAsync` call with the AD's literal `PropertiesToExclude=[Id]`, which silently overwrites that row's `RoomName`/`PowerPointName`/`DeviceName` snapshot with whatever the current run computes. Team B (reasoning from AD-10, not from AD-23's literal text) adds those snapshot columns to `PropertiesToExclude` for this one call path to avoid rewriting history. Both are "AD-23-compliant" in the sense that neither violates anything AD-23 explicitly forbids, but they produce different DB contents for the same corrected row. See Finding 2. A second, independent scenario: Team A treats AD-23's "all SmartPlugReading writes" Binds line as covering `UpdateMappingAsync` and migrates it to `BulkInsertOrUpdateAsync`; Team B treats the Rule's literal naming of only `AddAsync` as scope-limiting and leaves `UpdateMappingAsync`'s existing `ExecuteUpdateAsync`-based conflict-tolerance pattern in place. See Finding 1.
