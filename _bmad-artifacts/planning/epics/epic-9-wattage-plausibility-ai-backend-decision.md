# Epic 9: Wattage Plausibility AI Backend Decision

Decides and stands up the real AI backend behind Wattage Plausibility Correlation (FR-17) — the one thing Epic 6 built the full plumbing for (port, adapter, Household toggle, background job, graceful degradation — AD-8) but never actually exercised. Since Story 6.3, only `NoOpAiPlausibilityClient` has ever run in any environment; no `AiPlausibility:BaseUrl` has ever been configured. Delivers no new domain capability and no new FR — FR-17 already specifies the household-level backend choice ("a locally hosted model... or a cloud/external API") and NFR14 already constrains it (no paid third-party service required for a basic self-hosted instance). This epic closes the gap between that specification and a real, running backend.

**Origin:** Flagged as an open decision at Epic 6's retrospective (2026-09-21, Action Item #2) and carried, still undecided, through Epic 7. Escalated from a lingering retro action item to its own epic at Epic 7's retrospective (2026-09-26) at Ralf's explicit direction — see `_bmad-artifacts/implementation/epic-7-retro-2026-09-26.md`, "Significant Discovery."

**FRs covered:** none (no new FR) — operationalizes FR-17's already-specified household-level AI backend choice and NFR14's cost constraint; no product-facing behavior change beyond the correlation actually firing instead of always degrading.
**NFRs:** NFR1 (Tier 2 ≤30s, applies to the correlation job itself), NFR2 (hosting cost-efficiency — same deployment artifact serves self-host and cloud), NFR12 (privacy — self-hosted by default, no phone-home unless the household explicitly opts into a cloud backend), NFR14 (cost — no paid third-party service required for a basic self-hosted instance).
**Architecture:** AD-8 (`IAiPlausibilityClient` / `OpenAiCompatibleClient` — one HTTP client speaking the OpenAI-compatible shape, already covers both local LMStudio and cloud providers via base-URL/API-key config; resolves to a no-op when unset), AD-19 (secrets only via env vars/Container Apps secrets/`.env`, never committed).

Story sequencing is deliberate: two research-only stories before any production code ships, because the option space (Azure AI Foundry vs. self-hosted LMStudio vs. a plain cloud API) has never been narrowed, and AD-8's existing adapter already assumes an OpenAI-compatible HTTP shape — confirming or breaking that assumption per candidate is the spike's job, not something to discover mid-implementation.

## Story 9.1: AI Backend Spike — Narrow the Option Space

As Ralf (Project Lead),
I want a structured, time-boxed spike comparing Azure AI Foundry, self-hosted LMStudio, and a plain cloud API against cost/latency/privacy tradeoffs,
So that Epic 9's remaining stories build against a chosen direction instead of continuing to defer the decision.

**Acceptance Criteria:**

**Given** the three candidate backends (Azure AI Foundry, self-hosted LMStudio, a plain cloud API),
**When** the spike concludes,
**Then** each is scored against cost (NFR14), latency against the correlation job's Tier 2 budget (NFR1, ≤30s), and privacy (NFR12 — whether Event data ever leaves the deployment), with a written recommendation — not a vague pros/cons list.

**Given** AD-8's existing `IAiPlausibilityClient`/`OpenAiCompatibleClient` adapter (one HTTP client speaking the OpenAI-compatible shape),
**When** each candidate is evaluated,
**Then** compatibility with that existing adapter is explicitly confirmed or a new-adapter need is explicitly flagged — never silently assumed compatible.

**Given** the spike's conclusion,
**When** documented,
**Then** it names a recommended default backend for a self-hosted deployment (must remain NFR14-compliant — no paid third-party dependency for the basic instance) and a recommended backend for the Azure-hosted deployment, which may legitimately differ from each other.

**Given** this is a spike,
**When** the story is done,
**Then** the output is research + a written, disclosed recommendation — no production code ships from this story.

## Story 9.2: Production Rollout & Self-Hosting Research

As Winston (Architect) / Ralf (Project Lead),
I want deeper research into what a real production rollout looks like for Story 9.1's recommended backend(s), including self-hosting options in depth,
So that Stories 9.3/9.4 execute against a concrete, de-risked plan instead of surfacing open questions mid-implementation.

**Acceptance Criteria:**

**Given** Story 9.1's Azure-hosted recommendation,
**When** researched,
**Then** the concrete deployment shape is documented: which Azure service, expected cost at Energy Tracker's household-scale volume, and how secrets/base-URL are provisioned via the existing Container Apps secrets pattern (AD-19) — never committed/baked into the image.

**Given** Story 9.1's self-hosted recommendation,
**When** researched,
**Then** the concrete self-hosting shape is documented: resource requirements, whether it fits the "modest self-hosted hardware" envelope (NFR2), and an explicit fallback for a household whose hardware can't run it — the fallback must not force a paid dependency (NFR14).

**Given** both paths are researched,
**When** compared,
**Then** the documentation confirms this remains a Household-level operational/config choice already supported today (AD-8, FR-17) — implementing both paths is not a code fork, only a deployment/config difference.

**Given** this is a research story,
**When** it's done,
**Then** the output is a spec/research doc — no production code ships from this story either.

## Story 9.3: Production Implementation — Azure-Hosted Path

As a Household member on the Azure-hosted deployment,
I want the real AI backend actually configured and running,
So that Wattage Plausibility correlation genuinely happens instead of always degrading gracefully to a no-op.

**Acceptance Criteria:**

**Given** Story 9.2's Azure-hosted rollout plan,
**When** implemented,
**Then** `AiPlausibilityBackendOptions.BaseUrl`/API key are provisioned via Container Apps secrets (AD-19) — never committed, never baked into the image.

**Given** a Household with `AiPlausibilityEnabled` toggled on and the real backend configured,
**When** an Event is logged near a real observed consumption deviation,
**Then** a genuine (non-no-op) correlation is computed and stored — live-verified via the Claude-in-Chrome extension per this project's live-verification gate (`project-context.md` — Process gates), not asserted from a unit/component test alone.

**Given** `AiPlausibility:BaseUrl` is unset (today's existing behavior),
**When** the correlation job runs,
**Then** it still gracefully no-ops exactly as it does today (AD-8) — this story must not regress the existing degradation path for any household that hasn't opted in.

**Given** the backend is live in the Azure environment,
**When** cost/latency are actually observed,
**Then** they're logged/documented against Story 9.1's projected estimate, closing the loop on the spike's assumptions.

## Story 9.4: Production Implementation — Self-Hosting Path

As a Self-Hoster household,
I want documented, working instructions (and any needed adapter/config change) to run the recommended self-hosted AI backend,
So that Wattage Plausibility correlation works without any paid third-party dependency.

**Acceptance Criteria:**

**Given** Story 9.2's self-hosting rollout plan,
**When** implemented,
**Then** `docs/self-hosting.md` gains a documented setup section for the AI backend, sufficient for a "found this on GitHub" Self-Hoster to follow with no direct support channel (NFR11).

**Given** the self-hosted backend running locally and `AiPlausibility:BaseUrl` pointing at it,
**When** `AiPlausibilityEnabled` is toggled on for that Household,
**Then** a genuine correlation is computed exactly as in the Azure path — same AD-8 adapter, no code fork — live-verified via the Claude-in-Chrome extension.

**Given** a self-hosted deployment where the toggle is on but `AiPlausibility:BaseUrl` is unset,
**When** the correlation job runs,
**Then** the existing no-op degradation still holds — must not regress.

**Given** the basic self-hosted case,
**When** the chosen backend from Stories 9.1/9.2 is deployed,
**Then** it requires no paid third-party service (NFR14) — verified by construction of the recommendation, not asserted after the fact.
