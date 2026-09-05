---
source: brainstorm-smart-plug-import-optimization-2026-09-02 (.memlog.md)
status: complete
---

# Brainstorm Intent: Smart Plug Import Optimization

## 1. Goal / Problem

Eve Home (10-min granularity) full-history xlsx imports are slow and require babysitting; Meross
(daily granularity) imports already complete in seconds. Target: a 14-30 day data delta import
completes in a few minutes on Azure SQL Basic DTU tier. Improve via algorithmic changes (delta
detection, mapping timing, bulk writes), not micro-optimization.

## 2. Chosen Direction — Configuration A

- **Delta detection:** reverse-parse-with-early-stop — parse file newest-to-oldest, stop at first
  already-known entry.
- **Power Point mapping:** in-file device name matching (read header/name before processing rows).
- **DB writes:** bulk-library writes (e.g. EFCore.BulkExtensions-style), staying EF-adjacent/portable.
- **Toggle:** user-facing "smart import" (early-stop, default) vs "full import" (parse everything,
  e.g. to catch corrections). This toggle doubles as the audit mechanism for device-reset/silent-
  corruption risk — no separate detection machinery needed.

Rejected alternative (Configuration B — gap-inference delta, filename-pattern-history mapping,
provider-native SQL bulk writes) was the "aggressive/higher-risk" option; not chosen for v1.

## 3. Key Mechanisms Decided

- **Watermark = (timestamp, value) pair**, not timestamp alone. Storing the value too makes silent
  corrections to already-imported rows detectable (value differs at a known timestamp).
- **Mapping-first upload UX:** drop file -> system reads in-file device name -> auto-prefills
  target Power Point -> user does a final confirm to trigger import. If confidence is low, fall
  back to manual pick-or-create before import proceeds. Mapping confirmation is the guard against
  name collisions/renames — no separate collision-detection logic needed.

## 4. Explicitly Deferred / Separate Tracks

- **Provider-native SQL bulk writes** (e.g. Postgres COPY, SQL Server native bulk) — higher
  throughput but in tension with AD-2 ("one shared DbContext, portable subset only"). If pursued
  later, must be isolated behind the existing config-driven adapter-selection pattern (like DB
  provider selection itself), not branched inline.
- **Storage-grain rethink** — question whether one-row-per-reading is right at all, vs.
  batching/packing higher-granularity data (e.g. JSON payload per period), given Meross is daily
  and Eve is 10-minute granularity and both are forced into the same row shape today. Treated as a
  separate, riskier exploratory track (high design impact, potentially high gain) — not bundled
  into core bulk-write work, allowed to be dropped if it doesn't prove out.
- **Pattern-plausibility warning** — compare newly imported usage pattern against existing history
  for a Power Point; mismatch flags possible wrong-mapping/device-swap. Decided as a dismissible,
  low-priority nudge, not a hard block. Conceptually the same signal as filename-pattern-history
  matching (see Open Questions) — candidate to merge later rather than build twice.

## 5. Constraints / Requirements Surfaced

- Two distinct, separately-tracked performance requirements — do not conflate in NFR/architecture
  docs:
  - **"Few minutes"** — DTU/cost-driven processing budget on Azure SQL Basic tier.
  - **"Check back within the hour"** — UX patience threshold for fire-and-forget use; no
    requirement for real-time/immediate confirmation.
- **Idempotent re-import must stay cheap.** Users currently cope with import distrust by
  re-running/redoing imports repeatedly and manually double-checking values — a real, current
  habit, not hypothetical. Reverse-parse-early-stop + watermark makes this near-free, turning the
  habit from slow reassurance into fast reassurance (should still be preserved, not designed away).
- **Device-reset / wrong-mapping must not silently corrupt existing Power Point data.** Failure
  mode of concern is silent (small, quiet, undetected drift), not a loud crash — only caught later
  by comparing totals or a visible anomaly. Confident-but-wrong auto-mapping contaminating a Power
  Point is hard to untangle; recovery needs either delete-a-data-period or full clear-and-re-import
  for that Power Point.

## 6. Open Questions Worth Resolving Next

- What confidence threshold determines auto-mapping prefill vs. falling back to manual pick/create?
- Should filename-pattern-history matching (stable filename minus embedded export-date, matched
  against previously-confirmed imports) be folded in later as a second/reinforcing mapping signal,
  and does it merge with the pattern-plausibility warning into one unified confidence-signal
  feature?
- How and when does the storage-grain rethink get evaluated — what would prove it "significant
  enough" to pursue vs. let die?
- Where does import time actually go today (parsing vs. DB writes vs. Power Point association) —
  worth measuring to validate the Configuration A bottleneck assumptions?
