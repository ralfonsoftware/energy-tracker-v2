---
title: v1 PRD -> v2 PRD Reconciliation (Reusable Reference Material)
created: 2026-08-08
purpose: >
  Compare the v1 PRD (sample-data/req-input.md, slimmed) against the newly
  drafted v2 PRD (prd.md, addendum.md) to find v1 technical/domain detail
  that was reference material for v2 but did not carry into v2's PRD.
  Decomposition / Residual / attribution math / main-meter reconciliation is
  intentionally dead in v2 and is explicitly EXCLUDED from this analysis —
  its absence is correct, not a gap.
---

# Part 1 — Verification of the prior extraction pass's 9 claimed carryovers

| # | Claimed reusable v1 item | Landed in v2 PRD? | Where | Notes |
|---|---|---|---|---|
| 1 | Flat/Household -> Room -> Power Point -> Device tagging mechanism | Yes | §3 Glossary (Household, Power Point, Device, "Room -> Power Point -> Device"); §6.1 MVP Scope | Explicitly reframed as "organizing/tagging scaffold... not an attribution system" — correct reframing, mechanism itself intact. |
| 2 | Eve Home (.xlsx)/Meross (.csv) file format specifics | **No — only the file types, not the specifics** | FR-4 ("Eve Home `.xlsx`, Meross `.csv`") | v1 FR-24/FR-25 carried concrete parsing detail: Eve Home sheet name `Gesamtverbrauch`, device name in cell A1/room in A2, ~10-min Wh rows, local-time-not-UTC timestamp handling, overlap dedup by timestamp; Meross UTF-8-BOM tab-separated-with-comma-prefix format, filename pattern `Power Monitor Day Data - {device} - {YYYYMMDD}.csv`. None of this appears anywhere in v2's prd.md or addendum.md. See Part 2, Gap A. |
| 3 | Locale-neutral storage with fixed-decimal currency | Yes | Cross-Cutting NFRs: "Currency handling" and "i18n/locale" bullets | ISO 8601 with explicit offset, decimal-point numbers, fixed-decimal currency all present, matching v1 NFR-3/NFR-2 almost verbatim. |
| 4 | Gap-definition logic | **Partially — redefined, not reused** | §3 Glossary "Gap" (meter-reading gap tolerance); FR-3 | v2's "Gap" is a new concept (missing *meter reading*, absorbed into rate calc). v1's gap logic was about missing *days in a Smart Plug daily timeline*, filled by linear interpolation capped at the 7-day-prior average, with a user-facing hint. That mechanism has no counterpart anywhere in v2's Smart Plug Import (FR-4/FR-5). See Part 2, Gap B. |
| 5 | Performance tiers | Yes | Cross-Cutting NFRs: "Performance tiers" bullet | Tier 1 ≤2s / Tier 2 ≤30s with UI hint / Tier 3 async — matches v1 NFR-1 structure. |
| 6 | Tenant isolation / OIDC auth | Yes | Cross-Cutting NFRs: "Auth" and "Tenant isolation" bullets | Swappable OIDC via config, data-access-layer isolation — matches v1 FR-1–3/NFR-2. |
| 7 | i18n/UTC conventions | Yes | Cross-Cutting NFRs: "i18n/locale" bullet | No hardcoded locale strings, locale-neutral storage, UTC scheduled jobs — matches v1 NFR-3. |
| 8 | Tariff price-locking pattern | Yes | FR-10; confirmed in §9 Assumptions Index | "Price fields lock once contract start date has passed... explicit override step" — matches v1 FR-11. |
| 9 | Audit-trail "preserve original as correction note" edit pattern | Yes | Cross-Cutting NFRs: "Audit trail on corrections" bullet | Matches v1 FR-48, extended to cover both Meter Reading and Tariff edits (v1 only specified Reading). |

**Result: 7 of 9 fully landed. 2 did not land as claimed** (file-format specifics, gap/interpolation logic) — detailed as gaps below.

---

# Part 2 — Gaps: v1 domain/technical detail that should have carried into v2's PRD but didn't

### Gap A — Eve Home / Meross file-format parsing specifics dropped to a bare file-type mention
v1 FR-24/FR-25 encode format knowledge that took real effort to discover (sheet name, cell-based device/room extraction, local-time handling, dedup-by-timestamp, Meross's odd tab+comma-prefix CSV shape, BOM stripping, filename-derived device name). v2's FR-4 says only "Eve Home `.xlsx`, Meross `.csv`" with a generic "parses it" — none of the above survives. Since v2 still needs to import these exact same file formats (Smart Plug import is unchanged in source data, only the downstream use — sharpening vs. attribution — changed), this is reusable parsing knowledge, not decomposition-specific, and its loss risks re-discovering the same file quirks from scratch. Even if full parsing detail belongs in an architecture doc rather than a PRD, v2's PRD is the only place that would flag "these formats have known parsing gotchas" for whoever writes that architecture doc — currently it doesn't.

### Gap B — Smart Plug data gap/interpolation handling has no v2 counterpart
v1 FR-26 defines concrete behavior for missing dates inside an import's covered range: detect, notify with affected date range, linear-interpolate between anchors, cap interpolated value at the prior-7-day average, mark interpolated values and hint wherever shown. This logic is orthogonal to Decomposition/Residual — it's about producing a trustworthy per-plug daily timeline, which v2 still needs for FR-5 (Baseline Sharpening from Smart Plug Signal) and FR-9 (Per-Plug Measured Data View). v2's FR-4/FR-5 say nothing about how gaps *within* an imported plug's timeline are handled, only that the household-level Yearly Baseline computation itself is gap-tolerant (a different, reading-level concept — §3 "Gap"). Without this, it's undefined whether a plug's own missing days are silently skipped, zero-filled, or interpolated before they "sharpen" the baseline — a real ambiguity for implementation.

### Gap C — Meter-reading regression (meter reset/replacement) handling is missing
v1 FR-8 (entry-time warning: "Lower than your last reading — is this correct?") and FR-56 (trend-chart meter-reset visual indicator, taking precedence over spike styling) handle the case where a new Meter Reading is *lower* than the previous one — meter replacement or reset, not a data-entry error. This is squarely relevant to v2's Pattern Detective, which computes its baseline as a **rate between reading pairs** (FR-3) — a decreasing reading would produce a nonsensical or negative rate unless explicitly handled. v2's FR-1 only addresses out-of-order *timestamps* ("earlier timestamp than the most recent one is accepted... backfill case"), not a lower *value* at a later timestamp. This is a genuine technical gap: v1 already solved a problem v2's core mechanism (rate-based baseline) is arguably more exposed to than v1's was, and the solution wasn't carried over.

### Gap D — Concrete supported-locale list and formatting conventions dropped
v1 FR-40/FR-41 commit to specific launch locales (`de-DE`, `en-US`) with concrete formatting rules for each (currency symbol placement/decimal separator, 24h vs 12h time, date order) and specify the `Accept-Language`-derived default with a server-stored override. v2's Glossary only defines "Locale" abstractly ("a Household's language+region setting... underlying data always stored locale-neutral") with no launch-locale commitment and no formatting examples anywhere in prd.md or addendum.md. Given v2 explicitly targets external self-hosters (SM-5, "Self-Hoster" persona) as a *stronger* i18n concern than v1 had, dropping the concrete locale scope look like an oversight rather than a deliberate cut.

---

# Items reviewed and judged correctly excluded (not gaps)

- Decomposition, Residual, main-meter reconciliation, attribution math, Smart Power Strip/Strip Outlet proportional-share formulas (v1 FR-21/FR-32/FR-27) — per task framing, intentionally dead in v2.
- Actionable Insights detectors (standby offender, replacement candidate, budget pressure alert, invoice deviation) — superseded by Pattern Detective's Status + Tariff Savings Radar; the *pattern* of "insight de-dup / dismiss-reactivate" (v1 FR-51/FR-55) has no obvious v2 target since v2 has one Status, not a growing insight feed — reasonable to drop.
- Multi-Flat management (switcher, cascade delete with typed-name confirmation, default room template) — v2's Household model explicitly deprioritizes multi-entity management by design; not flagged.
- Onboarding as a formal gate (v1 FR-4, blocking all main features until complete) — v2 mentions onboarding only in passing (FR-2). Lower-confidence item: could be considered a minor process-flow gap, but it's UX-flow rather than technical/domain detail, so left out of the primary gap list.

---

# Summary

7 of 9 previously-claimed carryovers verified as landed correctly. 4 gaps identified, all technical/domain detail orthogonal to the deliberately-dropped Decomposition feature:

- **Gap A** — Eve Home/Meross file-format parsing specifics (sheet/cell layout, CSV quirks, filename pattern) dropped to a bare file-type mention.
- **Gap B** — Smart Plug per-plug timeline gap/interpolation handling has no v2 counterpart, even though v2 still consumes these files.
- **Gap C** — Meter-reading regression (reset/replacement) handling missing, despite v2's rate-based baseline being more exposed to it than v1's was.
- **Gap D** — Concrete supported-locale list (`de-DE`/`en-US`) and formatting conventions dropped to an abstract locale mention, despite v2 having a stronger external-self-hoster i18n motivation than v1.
