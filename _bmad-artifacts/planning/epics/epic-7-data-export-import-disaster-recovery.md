# Epic 7: Data Export & Import (Disaster Recovery)

Full-household backup/restore in a documented format, covering every entity type introduced by Epics 1–6 (Readings, Tariff history, Events, Smart Plug data, settings) — the safety net underneath the whole product, sequenced last so it captures the complete data model rather than growing piecemeal alongside each feature epic.

**FRs covered:** FR-22, FR-23
**NFRs:** NFR11 (docs as onboarding path), NFR13 (data ownership)
**Architecture:** AD-2, AD-3, AD-11
**UX-DRs:** UX-DR12 (Settings surface), UX-DR14 (import-validation-failure state)

## Story 7.1: Full Data Export

As a Household member,
I want to export all of my Household's data in a documented format,
So that I have a disaster-recovery backup and am never locked into the product to access my own data.

**Acceptance Criteria:**

**Given** a Household with data across all features (Meter Readings, Tariff history, Events, Smart Plug data, settings)
**When** I trigger an export
**Then** all of it is included in a single documented export format (FR-22)

**Given** the export format
**When** published
**Then** it is documented — the data is always readable back out through the format itself, not locked into the product (FR-22, NFR13)

**Given** the export
**When** generated
**Then** it only includes data scoped to my Household (AD-3, NFR4)

**Given** the export
**When** it runs
**Then** it covers data written under either database provider identically — export/import behavior is provider-agnostic (AD-2)

## Story 7.2: Full Data Import (Restore / Migration)

As a Household member,
I want to import a previously exported v2 dataset,
So that I can restore my instance after a disaster or move it to new hosting.

**Acceptance Criteria:**

**Given** a previously exported v2 dataset
**When** I import it
**Then** the import validates against the documented v2 export format (FR-23)

**Given** malformed data in the import file
**When** validation runs
**Then** it's rejected and reported with what failed — never partially applied (FR-23, UX-DR14)

**Given** the import mechanism
**When** used
**Then** it only supports v2-to-v2 restore/migration — it does not read or convert v1 data (FR-23)

**Given** a Household that already has data
**When** an import is attempted
**Then** it's blocked by default, requiring an explicit "replace all data" confirmation step — there is no partial-merge import mode (FR-23)

**Given** a successful "replace all data" import
**When** it completes
**Then** it's treated as a wholesale replace, not an edit — it does not go through the `IAuditCorrectionRecorder` mechanism used for regular Meter Reading/Tariff edits (AD-11)

**Given** the import surface
**When** reached
**Then** it's accessible from Settings (UX-DR12)
