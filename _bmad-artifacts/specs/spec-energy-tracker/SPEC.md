---
id: SPEC-energy-tracker
companions:
  - ../../planning/prds/prd-energy-tracker-2026-08-08/prd/index.md
  - ../../planning/prds/prd-energy-tracker-2026-08-08/addendum.md
  - ../../planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN.md
  - ../../planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md
  - ../../planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE.md
  - ../../planning/architecture/architecture-energy-tracker-2026-08-09/SOLUTION-OVERVIEW.md
sources:
  - ../../planning/briefs/brief-energy-tracker-2026-08-08/brief.md
---

> **Canonical contract.** This SPEC and the files in `companions:` are the complete, preservation-validated contract for what to build, test, and validate. Source documents listed in frontmatter are for traceability only — consult them only if you need narrative rationale or prose color this contract intentionally omits.

# Energy Tracker v2

## Why

A tenant household with no smart main meter has no purpose-built way to know, in seconds, whether this month's electricity use is on track or whether their tariff has quietly become uncompetitive — a spreadsheet holds the numbers but never watches for the trend, so the annual invoice arrives as a surprise nobody was warned about. This is both a pain to solve (mobile entry friction breaks the reading habit a baseline depends on) and a mandate to meet on the product's own terms: v1's room/device attribution promise didn't hold up — measurement error and inherently variable appliance draw compounded into a "Residual" precise-looking enough to trust and unreliable enough not to. v2 is a ground-up rebuild that keeps the Main Meter reading as the one invariant truth and rebuilds everything else — pattern detection, tariff comparison, event context — around honestly representing what can and can't be known from irregular, gap-tolerant manual readings. It is built in the open for one tenant-developer household first, generically enough that other self-hosters in the same no-smart-meter situation can run it unforked.

## Capabilities

- **CAP-1 — Meter Reading Entry & Habit Loop**
  - **intent:** A household member can log a Main Meter reading (kWh + timestamp) from their phone in under a minute, backfill a late reading, and have entry work even with no signal at the meter.
  - **success:** Save-to-confirmation completes in under a minute on the default (pre-selected, no-edit) path. A second same-day reading with a different timestamp is accepted as a distinct entry, never rejected or overwritten. An offline entry queues locally and syncs on reconnect without ever duplicating or losing a reading (idempotency key).

- **CAP-2 — Pattern Detective: Gap-Tolerant Baseline & Status**
  - **intent:** The system computes a single glanceable Status (within range / below baseline / trending) from a gap-tolerant rolling consumption baseline over irregularly-spaced Meter Readings, measured against a household-set Yearly Baseline, and surfaces low-confidence gaps and meter regressions before they corrupt the pace math.
  - **success:** A multi-day gap between readings is absorbed into the rate calculation, never breaking or resetting the baseline. Status recomputes only on a new reading or a completed Smart Plug import, never on a fixed schedule. A reading lower than its chronological predecessor opens a reset/rollover classification prompt and is excluded from pace computation until resolved. Editing the Yearly Baseline or threshold never rewrites already-computed historical Status.

- **CAP-3 — Smart Plug Import & Baseline Sharpening**
  - **intent:** A household member can upload Eve Home / Meross export files to sharpen Pattern Detective's baseline and browse measured data by Room → Power Point → Device, without that data ever being required for, or reconciled against, the Main Meter total.
  - **success:** A household with zero Smart Plug coverage still gets a fully functional Status. Import processing is fully asynchronous with a completion notification — the UI never blocks on parsing. A gap within an import's covered range is capped/flagged as interpolated, never silently treated as zero consumption. Retagging a Power Point after data was imported leaves that historical data attributed to the tag active at import time.

- **CAP-4 — Tariff Savings Radar**
  - **intent:** A household member can compare their current Tariff against a candidate rate they enter and see whether switching would actually save money, with any switching bonus normalized out so an inflated first period can't misrepresent the comparison, and get reminded to re-check only once a contract-exit window opens.
  - **success:** The bonus-included and bonus-normalized attractiveness signals are always shown together, never toggled. An exact-breakeven comparison resolves to "not worth switching," never to a false positive. The Tariff Check Reminder never fires more than 3 months before the current contract period ends, and never fires at all with no Tariff configured.

- **CAP-5 — Context Capture & Wattage Plausibility**
  - **intent:** A household member can log a short text/tap event for an appliance no Smart Plug can see, and get a rough, never-falsely-precise correlation against the consumption deviation Pattern Detective observed around that time — backed by an AI service the household can point locally, point to the cloud, or turn off.
  - **success:** Logging an Event takes comparable effort to a Meter Reading entry. An Event with no corresponding observable deviation is shown without a correlation rather than flagged as wrong. Disabling the AI backend leaves the rest of the product fully functional — plausibility correlation is never a hard dependency.

- **CAP-6 — Extensible Platform**
  - **intent:** A household member can extend the system past its built-in behavior — custom event/plausibility rules, a new Smart Plug export format, tunable thresholds — without a code change to the core.
  - **success:** Threshold and spike-detection settings are adjustable through the product's UI, not by editing deployment config files. The event/plausibility-rule seam exists so a future voice-input modality plugs in as just another Event source, not a separate feature.

- **CAP-7 — Data Export/Import**
  - **intent:** A household member can export their full Household dataset for disaster-recovery backup and import a previously exported dataset to restore or migrate a v2 instance.
  - **success:** Import validates against the documented v2 export format and rejects/reports malformed data rather than partially applying it. Importing into a Household that already has data is blocked by default, requiring an explicit "replace all data" confirmation — there is no partial-merge mode.

- **CAP-8 — Household & Access**
  - **intent:** The first person to reach a fresh deployment can create the Household and become its first member; existing members can invite more; everyone authenticates via a config-swappable OIDC provider and stays logged in durably on their phone.
  - **success:** A fresh deployment with no Household routes any authenticated visitor into Household creation, never a broken or empty dashboard. All Household members have equal, full access — no separate admin/owner role. A session survives both app restarts and a scaled-to-zero cold start.

## Constraints

- The Main Meter reading is the sole source of truth for total consumption. No domain code, API response, or view may sum or reconcile Smart Plug or Event data against it — there is no `Residual` concept anywhere in the system, backend or frontend.
- Every swappable capability (database provider, OIDC provider, AI backend, job queue) is selected by a single config value, never a code fork; the same container image serves both self-host and cloud deployment — no separate frontend/backend split.
- No third-party account or phone-home telemetry is required to use the product; a Household's data is always exportable in a documented format; no paid third-party service is required for a basic self-hosted instance, and optional integrations (AI plausibility) degrade gracefully rather than becoming a hard dependency.
- Every route requires authentication except the OIDC callback; all data is isolated per Household, enforced at the data-access layer rather than by per-handler convention.
- Monetary values are always fixed-decimal, never floating-point; timestamps are stored ISO 8601 with explicit offset; no locale, currency, or household-specific value is hardcoded — presets are offered as suggestions, never silently-applied defaults; a new display Locale is a translation-resource addition, never a code change.
- Meter Reading entry must work offline — it queues locally and syncs on reconnect — because meter locations are frequently signal-weak; a retried sync must never double-insert a reading.
- Edits to configuration inputs (Yearly Baseline, trending threshold, normalization formula) affect calculations going forward only; historical computed values are never silently rewritten by a later settings change.
- Concurrent writes to the same Household's data never silently lose an update — conflicts are rejected for the client to reload and retry, never resolved by last-write-wins or a silent merge.
- Editing a Meter Reading or Tariff entry preserves the original value as a visible correction note rather than a silent overwrite.
- The dashboard Status is the product's headline surface; drill-down views (trend history, per-plug data) are allowed to exist but must never become a precondition for trusting the Status — the product is judged by saying less, not by depth of detail.

## Non-goals

- Real-time or near-real-time monitoring — manual-read and file-upload driven by design, not a live dashboard.
- A hosted/managed offering — self-deploy only.
- An automated room-by-room energy-audit or attribution tool — the precision claim v1 walked back is not repeated in v2.
- A native mobile app; a cross-user admin/management platform — multi-user is technically possible but the product is not designed around managing users.
- A market-percentile tariff ranking service (needs a third-party data feed); a general plugin marketplace — the three Extension Points are scoped, not an open-ended system.
- A general life-logging tool — Context Capture is scoped strictly to unmeasurable-appliance energy events.
- A general billing engine — Tariff Configuration models flat base-fee-plus-price/kWh contracts only; tiered or time-of-use billing is out of scope.
- Multi-Main-Meter UI/logic in v2 — the data model allows more than one Main Meter per Household, but v2's Pattern Detective and dashboard operate on a single one.
- A v1-to-v2 data migration path — this is a ground-up rebuild; v1 and v2 are separate, data-incompatible deployments.

## Success signal

The household actually retires its spreadsheet — Energy Tracker becomes the sole source of truth, and the honest 1–2 day reading habit holds over time rather than trailing off. Demonstrated concretely by at least one real instance where the Status flags a trend early enough to change behavior before the invoice arrives, and at least one tariff stay/switch decision made with real confidence in the bonus-normalized Radar figure. Secondarily, at least one other self-hoster runs it against their own household without forking or hardcoding, and a new Smart Plug format or event rule is addable through the Extension Points without touching core code. Rising notification volume or growing drill-down time-in-app is a regression signal, not progress — the product is meant to be checked less, not more.

## Assumptions

- Capability boundaries (CAP-1–CAP-8) are this spec's own structuring of the PRD's six Features plus Household & Access into kernel-shape capabilities; FR-level acceptance detail (FR-1–FR-28 and their testable consequences) is preserved in full in the adopted PRD companion (sharded under `prd/`) rather than restated here.

## Open Questions

- Is generic Smart-Plug column mapping (FR-20) actually achievable with reasonable engineering effort given real-world export-format variance, or should it be dropped entirely? Needs a feasibility spike before committing.
- What delivery channel(s) will ambient/push Status notification use once built (native push, email, ntfy/webhook, etc.), and when does it get prioritized relative to the other Could-have Extension Platform items?
- Should Pattern Detective / Tariff Radar eventually support multiple threshold profiles (seasonal, per-room) instead of one tunable number? Deliberately deferred pending real usage signal once the single-threshold and broader tunable-settings capabilities (CAP-2, CAP-6) are live.
