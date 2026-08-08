---
title: Energy Tracker v2 PRD — Addendum
created: 2026-08-08
updated: 2026-08-08
---

# Addendum

Depth and ideas volunteered during PRD authoring that don't belong in the PRD's main narrative are captured here so they aren't lost, without padding the PRD itself.

## Ideas to evaluate later (not committed FRs)

### Multiple threshold profiles for Pattern Detective / Tariff Radar
Raised while discussing FR-21 (Tunable Threshold/Spike Settings). Instead of a single global trending-threshold number (FR-6's default ~100 kWh), the system could support multiple threshold *profiles* — e.g. seasonal (winter heating load vs. summer baseline), or per-room/per-context thresholds. Genuinely useful direction, but too unspecified right now to commit as an FR — no agreement yet on:

- what a "profile" scopes to
- how it's selected or switched
- whether it composes with the single Yearly Baseline (FR-2), or needs its own baseline concept per profile

Revisit once FR-6/FR-21 are live and there's real signal on whether a single tunable threshold is actually insufficient in practice.

## Deployment/technical-how notes

### Candidate low-cost cloud deployment shape
Raised while scoping the hosting-target NFR. One concrete deployment shape the PM has in mind for keeping both self-hosted and cloud-hosted total cost low:

- **Frontend:** Azure Static Web App
- **Backend:** Azure Container App (scale-to-zero)
- **Data:** Azure SQL Basic SKU
- **Auth:** Auth0 or Entra ID as the OIDC provider

This is implementation-level, not a PRD commitment — the PRD's NFR states the capability (cost-efficient on modest self-hosted hardware AND low-tier/scale-to-zero cloud tiers, swappable OIDC provider via config) without naming a specific cloud vendor or service. Worth handing to `bmad-architecture` as a candidate shape to evaluate, not a locked decision.

### Smart Plug export file schema (v1 reference, carried forward for v2 parsing)
Referenced from FR-4 (Smart Plug File Import). v1's PRD documented these format specifics, which are still applicable since v2 imports the same source files:

- **Eve Home (`.xlsx`)**: single sheet named `Gesamtverbrauch`; device name in cell A1, room in cell A2; data rows at ~10-minute intervals in Wh; timestamps are local time, not UTC-converted on import (converting corrupts data across midnight boundaries — reproduce this behavior in v2).
- **Meross (`.csv`)**: UTF-8 encoded, optional BOM; tab-separated with a comma-value-prefix convention; device name is not reliably in the file body — parse it from the filename pattern `Power Monitor Day Data - {device} - {YYYYMMDD}.csv`.
- Both formats are file-upload only in v1 and v2 — no direct vendor API integration in scope.

This is parsing/schema detail for the architecture and implementation phases, not PRD-level FR content — FR-4's Consequences capture only the two behaviors with user-visible/correctness impact (local-time handling, filename-based device matching).
