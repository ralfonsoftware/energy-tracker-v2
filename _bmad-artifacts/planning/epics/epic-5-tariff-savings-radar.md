# Epic 5: Tariff Savings Radar

Answers the product's second core question — is the current Tariff still worth staying on — via bonus-decay-normalized comparison against a candidate tariff, shown as a two-way signal, with a proactive reminder gated to contract-end timing. Shares its normalization math with Epic 2's pace threshold. Uses Epic 2's consumption pace; otherwise standalone.

**FRs covered:** FR-10, FR-11, FR-12, FR-13, FR-14, FR-15
**NFRs:** NFR6 (currency), NFR8 (audit trail), NFR9 (recomputation policy), NFR10 (concurrency)
**Architecture:** AD-4, AD-5, AD-7, AD-11
**UX-DRs:** UX-DR5 (Tariff Check prompt card), UX-DR7 (Tariff comparison card), UX-DR10 (color-system discipline), UX-DR12 (IA)

## Story 5.1: Tariff Configuration

As a Household member,
I want to maintain my Household's current Tariff,
So that the app always knows my real electricity contract terms.

**Acceptance Criteria:**

**Given** the Tariff Configuration surface
**When** I enter base fee, price/kWh, currency, contract start date, and Contract Period
**Then** the entry is saved as the Household's current Tariff (FR-10)

**Given** an existing Tariff entry, with a new Tariff entry later added
**When** the new entry's start date arrives
**Then** history is retained — each entry covers until the next one's start date, never overwritten (FR-10)

**Given** a Tariff entry whose contract start date has passed
**When** I try to edit its price fields
**Then** they are locked, requiring an explicit override step rather than a silent overwrite (FR-10)

**Given** Tariff Configuration
**When** modeled
**Then** it supports only a flat base-fee-plus-price/kWh structure — tiered/time-of-use billing is out of scope (FR-10)

**Given** a Tariff entry
**When** saved
**Then** currency is a required field, never hardcoded to EUR, and displays as a fixed-decimal value, never floating-point (FR-10, NFR6)

**Given** an existing Tariff entry
**When** I edit its value
**Then** the original value is preserved and shown as a visible correction note, never silently overwritten, via the shared `AuditCorrection` mechanism (NFR8, AD-11)

**Given** two Household members editing the same Tariff entry concurrently
**When** both submit
**Then** the second writer receives a 409 conflict rather than silently overwriting the first (AD-4, NFR10)

## Story 5.2: Candidate Tariff Comparison & Bonus-Decay Normalized Savings

As a Household member,
I want to enter a candidate tariff and see an honest, bonus-normalized savings projection against my current one,
So that I can judge whether switching would actually save money.

**Acceptance Criteria:**

**Given** a candidate tariff (price/kWh, base fee, optional switching-bonus terms)
**When** I enter it
**Then** it's scratch/exploratory — it never alters my Household's actual current Tariff until I explicitly switch (FR-11)

**Given** a valid candidate entry
**When** the projection is computed
**Then** it uses my actual household consumption pace from Pattern Detective, never a generic/assumed usage figure (FR-12)

**Given** fewer than two Meter Readings exist (no usable pace yet)
**When** I request a comparison
**Then** it shows the same onboarding empty state as the Dashboard Status, not a zero or undefined projection (FR-12)

**Given** the projection's Bonus-Decay Normalization
**When** computed
**Then** it calls the single shared `Domain.Calculations.BonusDecayNormalizer` also used by Pattern Detective's pace threshold — never a locally reimplemented or adjusted formula (FR-14, AD-5)

**Given** the normalization formula changes
**When** redeployed
**Then** both Tariff Savings Radar and Pattern Detective's threshold reflect the change identically (FR-14)

## Story 5.3: Two-Way Attractiveness Signal

As a Household member,
I want to see the switch-worthiness signal shown both with and without the switching bonus,
So that I'm not misled by an inflated first-period offer.

**Acceptance Criteria:**

**Given** a computed comparison
**When** displayed
**Then** a green/red signal is shown twice — once with the Switching Bonus included, once normalized out — both shown together, never toggled (FR-13)

**Given** an exact breakeven (zero projected savings) on the normalized signal
**When** evaluated
**Then** it resolves to red/not-worth-switching — ties favor staying put (FR-13)

**Given** the Tariff comparison card
**When** rendered
**Then** both signal rows use the dedicated 4th attractiveness-signal color pair (never reused from the Status triad, brand-accent, or destructive/error red), each with its own AA-verified supporting-text/figure-text tokens and a plain-language verdict word ("Worth switching" / "Not worth it") — never color alone (UX-DR7, UX-DR10)

**Given** the current-vs-candidate tariff summary
**When** rendered
**Then** each is a stacked glass panel with label/value rows using tabular-nums figures (UX-DR7)

## Story 5.4: Tariff Check Reminder

As a Household member,
I want a proactive prompt to revisit my Tariff comparison at a sensible time,
So that I don't have to remember to check manually.

**Acceptance Criteria:**

**Given** the current Contract Period
**When** more than 3 months remain before it ends
**Then** no reminder fires (FR-15)

**Given** the 3-month gate has opened
**When** no custom cadence is set
**Then** the default recurring cadence is every 3 months, editable per household (FR-15)

**Given** no Tariff is configured yet
**When** the reminder logic evaluates
**Then** no reminder fires — there's nothing to compare against (FR-15)

**Given** the Contract Period represents a minimum term, not necessarily a hard end date
**When** the minimum term elapses
**Then** the reminder gate opens and stays open on the recurring cadence whether the tariff then ends outright or auto-continues on a rolling basis — no explicit contract end date is required (FR-15)

**Given** the contract start date or Contract Period is edited after the reminder schedule was computed
**When** saved
**Then** the gate and cadence recompute against the new dates going forward (FR-15)

**Given** the reminder's due-ness
**When** evaluated
**Then** it's a pure synchronous computation evaluated on every relevant read, never precomputed by a background schedule (AD-7)

**Given** the Tariff Check prompt card, when a check is due
**When** rendered
**Then** it appears at deliberately lower visual weight than the Status card; when nothing is due, it shows neutral "nothing due right now" microcopy at the same quiet weight, never a fabricated recommendation (UX-DR5)
