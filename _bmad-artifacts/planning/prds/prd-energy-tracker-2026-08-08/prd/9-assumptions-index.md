# 9. Assumptions Index

- §3 Glossary: Yearly Baseline is a distinct, user-set target figure, not derived from reading history. *Confirmed.*
- §3 Glossary: Tariff Check Reminder is in v2 scope, sequenced after the core Radar. *Confirmed.*
- §4.1 FR-2: Household-size Yearly Baseline presets reused from v1 (1p/2p/3p/4p kWh figures). *Confirmed.*
- §4.2 FR-10: v1's tariff price-locking-on-contract-start pattern reused for v2. *Confirmed.*
- §4.2 FR-15: 3-month default Tariff Check Reminder cadence (user-editable). *Confirmed.*
- §Constraints: AI-assisted Wattage Plausibility (FR-17) is optional/gracefully-degradable across the whole product, not a hard dependency. *Confirmed.*
- §4.6 FR-29: added directly to `epics/requirements-inventory.md` ahead of this PRD (2026-08-22 implementation-readiness check caught the drift) and scheduled as Story 1.10, extending Story 1.9's structure editor in Epic 1. *Confirmed, backfilled into this PRD the same day.*
- §4.1 FR-4: import entry point moves to both Dashboard and Trend History (not Settings); upload UI queues multiple files per action, each its own async job. *Confirmed with Ralf.*
- §4.1 FR-32: new Import Job Status & History view is Household-wide (not per-user-session); 30-day auto-delete removes only the job/audit record, never the imported `SmartPlugReading` data; *Needs Mapping* and *Flagged for Review* (Story 3.3's all-Gaps case) are each first-class statuses alongside Waiting/Processing/Success/Error — six states total, none folded into another. *Confirmed with Ralf.*
