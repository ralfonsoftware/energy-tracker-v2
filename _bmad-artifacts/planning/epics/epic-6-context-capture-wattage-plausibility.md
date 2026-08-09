# Epic 6: Context Capture & Wattage Plausibility

Lets a household explain a spike or dip the Status surfaced, by logging fast text/tap-first Events for unmeasurable appliances and getting a rough, honest AI-assisted correlation against the deviation Epic 2 already computed — optional and gracefully degradable throughout. FR-18 (Proactive Weekly Recap) is deferred (see Epic List) pending an Architecture-flagged scheduler/notification-channel decision.

**FRs covered:** FR-16, FR-17
**NFRs:** NFR14 (graceful degradation)
**Architecture:** AD-8, AD-10
**UX-DRs:** UX-DR12 (Log Event surface), UX-DR14 (no-deviation state), UX-DR17 (correlation copy voice/tone)

## Story 6.1: Event Logging

As a Household member,
I want to log a short text/tap-first Event with an optional tag,
So that I have a fast way to note the unmeasurable things — like the induction cooktop or a long trip — that might explain a consumption change.

**Acceptance Criteria:**

**Given** the Log Event surface
**When** I enter a short text/tap-first Event (e.g. "cooked 2h," "away 2 weeks")
**Then** logging it takes comparable effort to a Meter Reading entry — not a form (FR-16)

**Given** the Event entry
**When** I optionally tag it to a Room, Power Point, or Device
**Then** the tag is recorded alongside the Event

**Given** a past date/time
**When** I log an Event for it
**Then** it's accepted as a backfill, same as a Meter Reading (FR-16)

**Given** a Room/Power Point/Device tagged on an Event
**When** the tagged item is later soft-deleted (AD-10)
**Then** the Event's historical tag remains inert display text rather than a broken reference (FR-16)

**Given** a household's use of Events over time
**When** observed
**Then** each Event is a single dated occurrence — there is no recurring/pattern-event mechanism in v2 (FR-16, Out of Scope)

## Story 6.2: Wattage Plausibility Correlation

As a Household member,
I want a logged Event to show a rough correlation against any consumption deviation Pattern Detective observed around that time,
So that I get a plausible, honest explanation without a false sense of precision.

**Acceptance Criteria:**

**Given** a logged Event
**When** a correlation is computed
**Then** it's shown as a rough/approximate signal (e.g. "roughly matches the bump seen") — never false precision or a verified attribution claim (FR-17, UX-DR17)

**Given** an Event expected to raise consumption (e.g. "gaming session 3h")
**When** correlated
**Then** it's checked against a bump; an Event expected to lower consumption (e.g. "away 2 weeks") is checked against a dip — direction is inferred from the Event, never assumed to always be a bump (FR-17)

**Given** an Event with no corresponding observable deviation
**When** displayed
**Then** it's shown without a correlation — never flagged as wrong (FR-17, UX-DR14)

**Given** two Events logged in the same window as one observed deviation
**When** correlated
**Then** both receive the correlation — the mapping is many-to-one, not first-match-wins (FR-17)

**Given** the AI backend
**When** configured
**Then** the choice between a local model (e.g. LMStudio) and a cloud/external API is a Household-level setting, always visible and under the household's control (FR-17, AD-8)

**Given** the AI backend is unset or disabled
**When** an Event is logged
**Then** the rest of the product functions fully — the correlation is simply absent, and nothing else in the product branches on whether AI is enabled (FR-17, AD-8, NFR14)

**Given** the correlation is computed
**When** shown
**Then** it's rendered inline with the Event, not as a separate step the household has to trigger (FR-17)
