---
stepsCompleted: [1, 2, 3, 4, 5, 6]
inputDocuments: []
workflowType: 'research'
lastStep: 1
research_type: 'technical'
research_topic: 'Integrating External Tariff Comparison APIs into the Tariff Savings Radar'
research_goals: 'Investigate feasibility, candidate API providers/data sources for tariff comparison, integration approaches (sync fetch vs. scheduled ingest vs. webhook), auth/rate-limit/cost/data-freshness considerations, and how this would interact with the existing candidate-tariff-comparison flow (Story 5.2) and BonusDecayNormalizer normalization math.'
user_name: 'Ralf'
date: '2026-09-23'
web_research_enabled: true
source_verification: true
---

# Integrating External Tariff Comparison APIs into the Tariff Savings Radar: Technical Research

**Date:** 2026-09-23
**Author:** Ralf
**Research Type:** technical

---

## Research Overview

This research investigates whether the Tariff Savings Radar (Epic 5, fully shipped with manual tariff entry only) should be extended with a live external API lookup for candidate tariffs, and if so, how. Four web-search-grounded research passes were run: technology stack, integration patterns, architectural patterns, and implementation/adoption. The headline finding is that **no first-party API exists from the German market's recognizable comparison brands (Verivox, Check24)** — only unofficial scrapers do — but two newer pan-European aggregator APIs, **tounify** and **Prezio**, plausibly cover the German market (4,558 and 14,883+ tariffs respectively) behind a plain REST/JSON, bearer-token-authenticated interface. Neither has mature public technical documentation, so a direct vendor spike is the required next step before any implementation commitment.

Architecturally, this integration requires **no new pattern** for this codebase: it is a textbook Ports & Adapters outbound adapter (AD-1), directly analogous to `ISmartPlugParser` (AD-9), translating the provider's schema into the existing `CandidateTariff` concept Story 5.2 already defines — the `BonusDecayNormalizer` and comparison UI are unaffected. The recommended integration shape is **on-demand sync fetch** (not scheduled ingest, and not webhook — no provider offers one), wrapped in `Microsoft.Extensions.Http.Resilience` (retry, circuit breaker, rate limiter) with graceful degradation to the existing manual-entry flow when the API is slow or down — mirroring the project's existing AD-8 "absence is a normal state" principle. Full findings, source citations, and a concrete implementation roadmap follow below; see the Executive Summary and Technical Research Recommendations sections for the complete picture.

---

## Technical Research Scope Confirmation

**Research Topic:** Integrating External Tariff Comparison APIs into the Tariff Savings Radar
**Research Goals:** Investigate feasibility, candidate API providers/data sources for tariff comparison, integration approaches (sync fetch vs. scheduled ingest vs. webhook), auth/rate-limit/cost/data-freshness considerations, and how this would interact with the existing candidate-tariff-comparison flow (Story 5.2) and BonusDecayNormalizer normalization math.

**Technical Research Scope:**

- Architecture Analysis - design patterns, frameworks, system architecture
- Implementation Approaches - development methodologies, coding patterns
- Technology Stack - languages, frameworks, tools, platforms
- Integration Patterns - APIs, protocols, interoperability
- Performance Considerations - scalability, optimization, patterns

**Research Methodology:**

- Current web data with rigorous source verification
- Multi-source validation for critical technical claims
- Confidence level framework for uncertain information
- Comprehensive technical coverage with architecture-specific insights

**Scope Confirmed:** 2026-09-23

---

## Technology Stack Analysis

### Programming Languages

The project's language is already fixed by `_bmad-artifacts/project-context.md` (.NET 10 / C# backend, React 19 / TypeScript frontend) — this research does not reopen that decision. Every external tariff-API integration option surveyed below is a plain REST/JSON HTTP API, so it is consumable from C# via `HttpClient` without any language-specific SDK requirement.
_Popular Languages: REST/JSON is the universal interface across all surveyed providers — no provider requires a non-.NET runtime._
_Emerging Languages: N/A — provider APIs are language-agnostic._
_Language Evolution: N/A._
_Performance Characteristics: JSON (de)serialization via `System.Text.Json`, already the project standard, is sufficient — no protocol (gRPC/GraphQL) forces a different approach except Octopus Energy, which optionally offers GraphQL alongside REST._
_Source: [Octopus Energy REST API docs](https://docs.octopus.energy/rest/guides/endpoints/)_

### Development Frameworks and Libraries

[Frameworks analysis with source citations]
_Major Frameworks: No dedicated German/EU tariff-comparison .NET SDK exists as an official, vendor-maintained package. An unofficial community NuGet package (`OctopusEnergy.Client`) wraps the UK-market Octopus Energy API; nothing equivalent exists for Verivox/Check24/tounify/Prezio. Integration would go through a hand-written adapter behind this project's existing `I{Capability}` port pattern (AD-1), consistent with how `ISmartPlugParser`/`IAiPlausibilityClient` are already structured._
_Micro-frameworks: `Microsoft.Extensions.Http` + Polly (already idiomatic in ASP.NET Core 10) covers retry/circuit-breaker needs for a flaky third-party tariff API — no heavier framework required._
_Evolution Trends: The market is consolidating around unified aggregator APIs (tounify, Prezio, Flatpeak) that normalize dozens of national providers behind one schema, rather than each household app integrating individual utilities directly._
_Ecosystem Maturity: Aggregator APIs are young (tounify/Prezio/Flatpeak all show 2025–2026-era product pages, thin public docs, "book a demo" gating for technical specifics) — expect direct engineering contact to confirm production-grade SLAs before committing._
_Source: [tounify.io](https://tounify.io/), [Prezio](https://www.prezio.eu/), [Flatpeak](https://flatpeak.com/), [octopus-energy-dotnet PR #79](https://github.com/markheydon/octopus-energy-dotnet/pull/79)_

### Database and Storage Technologies

[Database analysis with source citations]
_Relational Databases: No new database technology is needed. Fetched candidate-tariff snapshots are portable relational data (provider, price/kWh, base fee, currency, fetched-at timestamp) and fit directly into the existing dual-provider `EnergyTrackerDbContext` (AD-2) as a new entity — no `jsonb`/provider-specific column required if the response is flattened to scalar fields at the adapter boundary._
_NoSQL Databases: Not applicable — none of the surveyed providers return data volumes or shapes that would justify a document store._
_In-Memory Databases: Given tariff data is stated by every surveyed provider to update at most daily, a short-lived in-memory or `IMemoryCache` layer at the adapter is sufficient to avoid paying per-call API costs (tounify explicitly meters and prices per call) on repeated user page loads — no Redis/dedicated cache tier is justified by the data volume._
_Data Warehousing: Not applicable at this scale._
_Source: [tounify.io pricing](https://tounify.io/), [Prezio](https://www.prezio.eu/)_

### Development Tools and Platforms

[Tools and platforms analysis with source citations]
_IDE and Editors: No change — existing .NET/React tooling applies._
_Version Control: No change._
_Build Systems: No change — no new build tooling required for a REST adapter._
_Testing Frameworks: The existing `.NET` test stack (xUnit v3, NSubstitute, Testcontainers) covers unit/integration needs; a new external HTTP dependency needs a recorded-response/contract test approach (e.g. WireMock.Net or committed fixture JSON) so `.NET` tests don't depend on live third-party availability or consume metered API calls — this is a new-to-the-project testing pattern worth deciding deliberately, not an existing convention to reuse as-is._
_Source: project-context.md (existing test stack); general REST-adapter testing practice._

### Cloud Infrastructure and Deployment

[Cloud platforms analysis with source citations]
_Major Cloud Providers: No change — Azure Container Apps remains the deployment target (AD-13/AD-19). A new external egress dependency (outbound HTTPS to the chosen tariff API) needs to be confirmed against the project's existing Azure networking/egress rules — see `docs/local-vs-azure-deltas.md` per the project's own "Performance/operational gotchas" guidance._
_Container Technologies: No change._
_Serverless Platforms: If tariff data is fetched proactively rather than on-demand (e.g. nightly refresh of cached candidate tariffs), the existing `IBackgroundJobQueue` (AD-6) is the natural fit — no new async infrastructure needed, consistent with how other scheduled/background work is already modeled._
_CDN and Edge Computing: Not applicable._
_Source: project-context.md AD-6/AD-13/AD-19._

### Technology Adoption Trends

[Adoption trends analysis with source citations]
_Migration Patterns: The German retail electricity-tariff market has no major public, self-serve API from the dominant consumer-facing comparison portals (Verivox, Check24/Tarifcheck24) — search turned up only unofficial scraping services (e.g. an Apify-hosted Verivox scraper) standing in for a real API. This is a meaningful constraint: the "obvious" incumbents are not directly integrable without scraping, which carries ToS/reliability risk._
_Emerging Technologies: A newer wave of pan-European aggregator APIs (tounify: 23 countries/4,558 German tariffs; Prezio: 17 countries including Germany, 14,883+ tariffs) purport to normalize exactly this kind of retail-tariff data behind one schema — these are the most promising fit for a "candidate tariff" lookup feature, pending direct verification of German coverage depth, auth, and pricing at production volume._
_Legacy Technology: Wholesale/day-ahead spot-price APIs (aWATTar, Tibber, Octopus Agile-style, the free keyless German SMARD-based InfraNode API) are mature and well-documented, but they price *wholesale* electricity, not *retail* household tariffs — not a fit for Story 5.2's candidate-tariff-comparison use case, which compares retail contract terms (base fee + price/kWh + switching bonus), not spot prices. Worth ruling out explicitly so it isn't mistaken for a shortcut._
_Community Trends: Community/open-source tooling (evcc, TeslaMateAgile) integrates dynamic/wholesale tariffs for EV/solar optimization, not retail tariff-switching comparison — a different problem from Tariff Savings Radar's._
_Source: [tounify.io](https://tounify.io/), [Prezio](https://www.prezio.eu/), [Verivox Scraper on Apify](https://apify.com/studio-amba/verivox-scraper/api/openapi), [InfraNode Germany Electricity Price API](https://infranode.dev/en/data/electricity-price-api/), [evcc tariff docs](https://docs.evcc.io/en/docs/tariffs)_

---

## Integration Patterns Analysis

### API Design Patterns

[API design patterns analysis with source citations]
_RESTful APIs: Every surveyed provider (tounify, Prezio, Selectra, Flatpeak, Octopus Energy) exposes a plain REST/JSON API — no GraphQL-only or RPC-only provider was found in this market segment. This confirms Step 2's finding that no protocol-driven language/tooling change is required._
_GraphQL APIs: Octopus Energy optionally offers GraphQL alongside REST (UK market only, not directly applicable to a German-market candidate-tariff feature, but relevant if the same adapter shape is later reused for a UK household)._
_RPC and gRPC: None found — not a fit for this integration class (low request volume, human-triggered lookups, not high-throughput service-to-service traffic)._
_Webhook Patterns: None of the retail-tariff-comparison providers surveyed (tounify, Prezio, Selectra, Flatpeak) advertise webhook/push support for tariff-change notifications — consistent with the general finding below that only ~11% of SaaS APIs support webhooks natively. Candidate-tariff lookup is a pull, not a push, integration by necessity._
_Source: [Octopus Energy REST API docs](https://docs.octopus.energy/rest/guides/endpoints/), [tounify.io](https://tounify.io/), general webhook-adoption finding below_

### Communication Protocols

[Communication protocols analysis with source citations]
_HTTP/HTTPS Protocols: Standard HTTPS REST calls, no persistent connection required — a household member entering a candidate tariff is an infrequent, human-paced action (Story 5.2 is exploratory/scratch, not continuous), so simple request/response is sufficient._
_WebSocket Protocols: Not applicable — no real-time streaming use case exists in this feature._
_Message Queue Protocols: Not needed for the external call itself, but the project's existing `IBackgroundJobQueue` (AD-6, plain JSON `JobEnvelope<TPayload>`) is the right fit if a proactive/scheduled refresh pattern is chosen over pure on-demand fetch (see Data Formats/System Interoperability below)._
_grpc and Protocol Buffers: Not applicable — see RPC/gRPC above._
_Source: project-context.md AD-6_

### Data Formats and Standards

[Data formats analysis with source citations]
_JSON and XML: All surveyed providers respond in JSON — matches the project's existing `System.Text.Json` standard, no new serialization library needed._
_Protobuf and MessagePack: Not applicable — no provider offers a binary protocol._
_CSV and Flat Files: Not applicable for live lookups. (Note: the *Verivox scraper* found in Step 2 returns scraped HTML/structured data via Apify, not a first-party JSON contract — a materially higher-risk integration shape than a real API and should be weighted accordingly if considered.)_
_Custom Data Formats: Each aggregator (tounify, Prezio) defines its own tariff schema (base fee, price/kWh, provider, postal-code coverage, effective date) — none publish an open/shared schema (e.g. no evidence of Open Charge Point Protocol-style standardization in retail tariff data). An adapter-side DTO→domain mapping is required regardless of which provider is chosen, reinforcing the Ports & Adapters approach already used elsewhere in the codebase (AD-1)._
_Source: [tounify.io](https://tounify.io/), [Prezio](https://www.prezio.eu/)_

### System Interoperability Approaches

[Interoperability analysis with source citations]
_Point-to-Point Integration: The natural shape here — direct `HttpClient`-based adapter (e.g. `TounifyTariffLookupAdapter`) implementing a new `ITariffLookupProvider` port, mirroring `ISmartPlugParser`'s one-port/one-adapter-per-vendor structure (AD-9) so a future provider swap or multi-provider fallback doesn't require a rewrite._
_API Gateway Patterns: Not needed at this scale — a single external dependency behind one port doesn't justify a gateway layer._
_Service Mesh: Not applicable — single-service monolith-style API project (AD-13), no internal service-to-service mesh exists or is warranted for one outbound integration._
_Enterprise Service Bus: Not applicable — out of proportion to the problem size._
_Source: project-context.md AD-1, AD-9, AD-13_

### Sync Fetch vs. Scheduled Ingest vs. Webhook (explicit research-goal comparison)

_On-demand sync fetch (call the provider API at the moment a household member requests a candidate-tariff lookup):_
- **Pros:** Always current at the point of use; no stale-cache risk; simplest to reason about; no background job infrastructure needed for the MVP path.
- **Cons:** User-facing latency tied to third-party API response time; every lookup costs metered API calls (tounify: €0.001–€0.10 per call depending on tier) with no batching discount; a provider outage directly blocks the feature at the moment of use.
- **Fit:** Best match for Story 5.2's actual usage pattern — infrequent, human-triggered, exploratory lookups, not a background/continuous need. Matches the general finding that polling/pull APIs "make sense... for user-initiated searches."

_Scheduled ingest (nightly/periodic background refresh of a cached tariff set per postal code via `IBackgroundJobQueue`):_
- **Pros:** Removes per-lookup latency and live-outage risk from the user-facing path; can batch/amortize metered API costs; fits the project's existing async-job pattern (AD-6) with zero new infrastructure.
- **Cons:** Introduces a staleness window (matches provider-stated "daily" refresh cadence, so a nightly job aligns naturally with actual data freshness — no fresher signal is being discarded); adds a new entity/table + job wiring; needs an explicit policy for what happens when a household's postal code has no cached data yet (first-run/cold-start case).
- **Fit:** Worth it only if usage volume or per-call cost makes on-demand fetch materially expensive — not clearly justified until real usage data exists.

_Webhook (provider pushes tariff-change events):_
- **Not available** from any surveyed retail-tariff-comparison provider (tounify, Prezio, Selectra, Flatpeak) — ruled out as an option for this feature, not a design choice being declined.

**Recommendation to carry into architecture:** start with on-demand sync fetch behind a resilience pipeline (retry + circuit breaker + short-TTL cache to blunt duplicate calls within a single session), since it best matches actual usage shape and avoids speculative background-job complexity; revisit scheduled ingest only if metered-call cost or latency becomes a real problem post-launch. This mirrors AD-7's "compute-at-request-time, not precomputed by schedule" pattern already applied to Status/Reminder elsewhere in the codebase — the same reasoning applies here.
_Source: [Webhook vs. Polling: Pick the Right Data Sync Pattern](https://codenicely.in/blog/businesses/saas/webhook-vs-polling-data-sync-pattern), [The Developer's Guide to API Integration Patterns](https://embeddedblog.wpengine.com/blog/api-integration-patterns/), [tounify.io pricing](https://tounify.io/), project-context.md AD-6/AD-7_

### Microservices Integration Patterns

[Microservices integration analysis with source citations]
_API Gateway Pattern: Not applicable — see System Interoperability above._
_Service Discovery: Not applicable — single external, fixed-URL dependency, not a dynamic service mesh member._
_Circuit Breaker Pattern: Directly applicable and recommended. `Microsoft.Extensions.Http.Resilience` (built on Polly v8, first-class ASP.NET Core 10 `HttpClient` integration) provides retry-with-exponential-backoff-and-jitter for transient failures, a circuit breaker to stop calling during a sustained provider outage, and a client-side rate limiter to stay under the provider's metered/rate-limited plan — the documented "three-layer" production pattern for external API calls._
_Saga Pattern: Not applicable — a single external read call, no distributed transaction/multi-step compensation involved._
_Source: [Polly v8 / Microsoft.Extensions.Resilience](https://www.pollydocs.org/), [Resilience Engineering in .NET 8: Polly Pipelines in Practice](https://hackernoon.com/resilience-engineering-in-net-8-polly-pipelines-in-practice)_

### Event-Driven Integration

[Event-driven analysis with source citations]
_Publish-Subscribe Patterns: Not applicable — no eventing surface exposed by any surveyed provider._
_Event Sourcing: Not applicable to the external integration itself; the project's existing `AuditCorrection` mechanism (AD-11) already covers the internal audit-trail need if a fetched candidate tariff is later edited or promoted to the household's actual Tariff._
_Message Broker Patterns: Not applicable — no Kafka/RabbitMQ-class need at this integration's scale; `IBackgroundJobQueue` already covers the one legitimate async use case (optional scheduled refresh) without adding a new broker._
_CQRS Patterns: Not applicable beyond the project's existing use-case-class convention (one `ExecuteAsync` per use case, AD-1) — no read/write model split is warranted for a tariff lookup._
_Source: project-context.md AD-1, AD-6, AD-11_

### Integration Security Patterns

[Security patterns analysis with source citations]
_OAuth 2.0 and JWT: Not required by the surveyed retail-tariff providers — tounify documents plain bearer-token (static API key) authentication, not a full OAuth 2.0 flow. Octopus Energy (reference/UK market) uses HTTP Basic auth with the API key as username. No provider in this segment requires OAuth's added complexity._
_API Key Management: The relevant pattern here — a single long-lived API key per provider, which per project-context.md's existing rule must come from env vars/Container Apps secrets/`.env`, never committed or baked into the image (same rule already applied to the OIDC client secret and DB connection string)._
_Mutual TLS: Not required/offered by any surveyed provider._
_Data Encryption: Standard HTTPS in transit is what every provider offers; no provider-specific encryption requirement found. Fetched tariff data is not personal/sensitive data on its own (public pricing information), so no additional at-rest encryption beyond the project's existing database-level protections is indicated._
_Source: [tounify.io](https://tounify.io/), [Octopus Energy REST API docs](https://docs.octopus.energy/rest/guides/endpoints/), project-context.md Security rules_

---

## Architectural Patterns and Design

### System Architecture Patterns

Hexagonal architecture (Ports and Adapters, Alistair Cockburn) places core business logic at the center, isolated from external systems — databases, UIs, third-party APIs — through a consistent boundary of ports and adapters. This is not a new pattern to introduce; it is the pattern this codebase already uses (AD-1: Domain has zero external package refs, Application defines ports only, Infrastructure holds adapters). A `TariffLookupProvider` third-party integration is a textbook outbound/driven adapter in this same shape — no architectural precedent needs to be established, only followed.
_Source: [Hexagonal Architecture guide](https://chakray.com/hexagonal-architecture-a-complete-guide-to-robust-and-testable-software-design/), [domain-driven-hexagon](https://github.com/sairyss/domain-driven-hexagon)_

### Design Principles and Best Practices

The **Anti-Corruption Layer** (ACL) pattern from Eric Evans' DDD is the specific principle at play for an external tariff API: the adapter translates the external provider's schema (tounify/Prezio's own tariff JSON shape) into this project's own `CandidateTariff` domain concept at the boundary, so a provider's field names, units, or schema quirks never leak into domain/application code. This directly answers the "how would this interact with the existing candidate-tariff-comparison flow" research goal: Story 5.2's `CandidateTariff` remains the single internal shape; the new adapter's only job is mapping an external response onto it, exactly as `ISmartPlugParser` adapters already do per-vendor (AD-9) for Eve Home/Meross file formats.
_Source: [The right boundary - Hexagonal Architecture](https://jmgarridopaz.github.io/content/therightboundary.html), project-context.md AD-9_

### Scalability and Performance Patterns

At this feature's actual scale (one household-initiated lookup at a time, not a high-throughput service), scalability is not the binding constraint — latency and third-party cost are. The relevant patterns from Step 3's resilience findings apply here directly: retry-with-backoff, circuit breaker, and a short-TTL cache-aside layer to avoid re-paying a metered API call for the same postal-code lookup within one session. No horizontal-scaling or load-balancing concern is introduced by this integration.
_Source: [Building Resilient REST API Integrations: Cache-Aside, Stale Fallback, and Background Refresh](https://medium.com/@oshiryaeva/building-resilient-rest-api-integrations-cache-aside-stale-fallback-and-background-refresh-9028e5497dfb)_

### Integration and Communication Patterns

Covered in full in the dedicated Integration Patterns Analysis section above (API design, protocols, sync/scheduled/webhook comparison, resilience layering) — not repeated here to avoid duplication.

### Security Architecture Patterns

Covered in the Integration Security Patterns subsection above (API key via env vars/Container Apps secrets, no OAuth/mTLS required by any surveyed provider) — not repeated here.

### Data Architecture Patterns

**Graceful degradation is the key data-architecture principle to adopt.** The established pattern for a third-party pricing/pricing-adjacent API: every external call gets a timeout, and every timeout gets a fallback — when the tariff-lookup API is unavailable or times out after retries, the application should degrade (e.g. show "candidate tariff data temporarily unavailable, try again shortly") rather than let the failure bubble into an unrelated page error. This mirrors AD-8's existing rule for `IAiPlausibilityClient` (a no-op when unconfigured, never a hard branch) — the same "absence is a normal, first-class state" principle applies to a third-party tariff API being down, not just to it being unconfigured.

For versioning/history, if a scheduled-ingest fallback (Step 3) is ever adopted: bulk external data should be published with immutable, versioned snapshots (dataset-layer best practice) rather than overwritten in place — this is naturally satisfied by this project's existing `AuditCorrection`-style "never silently overwrite, keep history" convention (AD-11) if a fetched candidate tariff is later promoted to the household's actual Tariff.
_Source: [Graceful degradation in practice](https://dev.to/alexcasalboni/graceful-degradation-in-practice-how-featureops-builds-real-resilience-1p4i), [Building Resilient REST API Integrations: Graceful Degradation](https://medium.com/@oshiryaeva/building-resilient-rest-api-integrations-graceful-degradation-and-combining-patterns-e8352d8e29c0), project-context.md AD-8, AD-11_

### Deployment and Operations Architecture

No deployment-topology change is required — the adapter lives inside the existing single-artifact `Api` project (AD-13). The only new operational surface is: (1) a new outbound-egress allowance to the chosen provider's domain, to verify against existing Azure Container Apps networking rules per `docs/local-vs-azure-deltas.md`; (2) a new secret (the provider API key) provisioned the same way existing secrets are (env vars/Container Apps secrets); (3) if a metered-per-call provider (tounify) is chosen, a cost-monitoring consideration belongs in ops runbooks, since API cost scales with usage in a way none of this project's current dependencies do.
_Source: project-context.md AD-13, AD-19, "Performance/operational gotchas"_

---

## Implementation Approaches and Technology Adoption

### Technology Adoption Strategies

No migration strategy is needed in the usual sense — this is a net-new, additive capability behind a new port, not a replacement of an existing system. The relevant adoption question is vendor selection, not technology migration: given none of the surveyed aggregator APIs (tounify, Prezio) have mature, self-serve technical documentation yet, a **paid pilot/spike with the top 1–2 candidates** (rather than a blind commit) is the appropriate gradual-adoption approach before wiring a production adapter.
_Source: Step 2/3 findings on provider documentation maturity._

### Development Workflows and Tooling

No new CI/CD or workflow tooling is required. The one concrete new practice: contract/stub testing against the chosen provider using **WireMock.Net**, so `.NET` tests exercise the adapter's request/response mapping and failure-handling paths without depending on live third-party availability or consuming metered calls during CI runs — directly extending, not replacing, the existing xUnit v3/NSubstitute/Testcontainers stack (project-context.md's testing rules) with one more tool for the one new dependency type (outbound third-party HTTP) that stack doesn't currently need to cover.
_Source: [WireMock.Net GitHub](https://github.com/wiremock/WireMock.Net), [WireMock and .NET](https://wiremock.org/docs/solutions/dotnet/)_

### Testing and Quality Assurance

Three testing layers for the new adapter, mapped onto the existing test-stack conventions:
1. **Unit tests** (adapter's DTO→domain mapping logic) — xUnit v3 + Shouldly, no network.
2. **Contract/stub tests** (adapter against a WireMock.Net-simulated provider) — verifies request shape, auth header, response parsing, and failure-mode handling (timeout, 429, 5xx) without hitting the real API.
3. **A small number of genuinely live-verified checks** — per this project's own hard process gate for browser-dependent/live-verified stories, an external-API integration touching real money-relevant data (candidate tariff pricing shown to users) warrants at least one deliberate live call against the real provider before the story reaches `done`, not unit/contract tests alone. This mirrors the project's existing OIDC live-verification gate's underlying principle (a mocked/stubbed pass is not proof the real integration works) even though it isn't itself an OIDC/browser story.
_Source: project-context.md Critical Don't-Miss Rules (Process gates)_

### Deployment and Operations Practices

Covered in the Deployment and Operations Architecture subsection above (egress rule, secret provisioning, cost-monitoring for metered usage) — not repeated here.

### Team Organization and Skills

No new skill area is required beyond what the team already has (C#/.NET HTTP client work, Ports & Adapters). The only genuinely new skill-adjacent task is evaluating and negotiating with an external data vendor (reading SLA terms, understanding metered pricing tiers) — a product/ops task more than an engineering one.

### Cost Optimization and Resource Management

Given tounify's disclosed metered pricing (€89/mo for 1,000 calls up to €0.001/call at the highest tier) and Prezio's undisclosed-but-likely-comparable model, cost is a first-class design input, not an afterthought: the on-demand-fetch-with-short-TTL-cache recommendation from Step 3 is also the cost-optimal starting point, since it avoids paying for repeat lookups of the same postal code within a session without committing to scheduled-ingest infrastructure before real usage data justifies it.
_Source: [tounify.io pricing](https://tounify.io/)_

### Risk Assessment and Mitigation

**Primary risk: none of the leading candidate providers (tounify, Prezio) have public, complete technical documentation** — both require a demo/direct contact to confirm auth details, exact schema, and SLA. This must be resolved via direct vendor contact before committing engineering time to a specific integration, not assumed from marketing pages.

**Secondary risk: single-vendor lock-in.** Standard mitigation — keep the external-provider dependency behind the `ITariffLookupProvider` port (already the plan per Steps 3–4), so a provider swap is a new adapter + config change, never a domain/application-layer change. This is the same abstraction-layer principle the general vendor-lock-in research confirms (swap by config, not by code, when a provider changes terms or goes down) and is already how this project treats every other swappable dependency (AD-1's config-driven adapter selection).

**Tertiary risk: no first-party API exists for the two providers a German user would recognize** (Verivox, Check24) — meaning the "credible/branded" market leaders are not directly integrable without scraping (ToS risk, fragility). This should be surfaced explicitly to product/stakeholders: the realistic provider set is aggregator services the end user has likely never heard of, which may affect trust/credibility framing in the UI (e.g. sourcing/attribution copy), not just a backend concern.
_Source: Step 2 findings; [Vendor lock-in mitigation strategies](https://konghq.com/blog/learning-center/vendor-lock-in)_

## Technical Research Recommendations

### Implementation Roadmap

1. **Vendor spike (pre-story):** Contact tounify and Prezio directly to obtain real API docs, confirm German coverage depth/accuracy for representative postal codes, exact auth flow, and production pricing at expected volume. Do not proceed to implementation based on marketing-page claims alone.
2. **Define `ITariffLookupProvider` port** in `Application/Ports`, returning the same `CandidateTariff`-shaped data Story 5.2 already consumes — no change to `BonusDecayNormalizer` or the comparison UI is implied by this integration.
3. **Build one vendor adapter** (`Infrastructure/Adapters`) using `Microsoft.Extensions.Http.Resilience` (retry + circuit breaker + rate limiter) and a short-TTL in-memory cache.
4. **Add WireMock.Net-based contract tests** for the adapter's request/response/failure-mode handling.
5. **Wire the new secret** (provider API key) via the existing Container Apps secrets pattern; confirm outbound egress against `docs/local-vs-azure-deltas.md`.
6. **Perform a genuine live-provider verification call** before marking the story `done`, per the project's existing live-verification process gate principle.
7. **Ship as an optional enhancement to the existing manual-entry flow**, not a replacement — a household member can still hand-enter a candidate tariff if no API result is available (graceful degradation, not a hard dependency).

### Technology Stack Recommendations

- Keep the existing .NET 10/C# stack — no new language/runtime.
- `Microsoft.Extensions.Http.Resilience` (Polly v8) for resilience.
- `System.Text.Json` for deserialization (already standard).
- `WireMock.Net` added as a new test-only dependency for contract testing the adapter.
- No new database technology — extend the existing `EnergyTrackerDbContext` if any caching/snapshot persistence beyond in-memory TTL cache is later needed.

### Skill Development Requirements

None beyond the team's existing C#/.NET and Ports & Adapters proficiency. The gap is vendor evaluation/negotiation, not a technical skill.

### Success Metrics and KPIs

- **Coverage:** % of household postal codes for which the chosen provider returns at least one candidate tariff.
- **Latency:** p95 response time for a candidate-tariff lookup, end to end (should stay within normal page-interaction expectations, not feel like a background job).
- **Cost:** actual €/lookup at real usage volume vs. the provider's advertised tier pricing, to validate the on-demand-fetch cost assumption from Step 3/5.
- **Degradation correctness:** zero unhandled failures reaching the user as a raw error when the provider is slow/unavailable — always resolves to either data or a clear "temporarily unavailable" state.

**Technical research phases completed:**

- Step 1: Research scope confirmation
- Step 2: Technology stack analysis
- Step 3: Integration patterns analysis
- Step 4: Architectural patterns analysis
- Step 5: Implementation research

---

## Executive Summary

Extending the Tariff Savings Radar with a live external tariff-lookup API is technically straightforward and architecturally low-risk for this codebase — it fits the existing Ports & Adapters pattern with no new invariant needed — but the *market* is the real constraint, not the technology. Germany's recognizable comparison brands (Verivox, Check24) publish no developer API; the credible options are newer, thinly-documented pan-European aggregators (tounify, Prezio) whose real German coverage, pricing at volume, and production reliability cannot be confirmed from public marketing pages alone. The right next step is a direct vendor spike, not an implementation sprint.

**Key Technical Findings:**

- No public API exists from Verivox/Check24 — only unofficial scraping services were found, which carry ToS/reliability risk unsuitable for a production feature.
- **tounify** (23 countries, 4,558 German tariffs, bearer-token auth, metered pricing from €89/mo) and **Prezio** (17 countries incl. Germany, 14,883+ tariffs, pricing undisclosed) are the most promising candidates, both REST/JSON, both requiring direct vendor contact to confirm technical details.
- No provider in this market segment offers webhooks — push-based integration is not an option; the choice is between on-demand fetch and scheduled polling.
- The integration maps cleanly onto this project's existing architecture: a new `ITariffLookupProvider` port + adapter (AD-1/AD-9 pattern), translating external schema into the existing `CandidateTariff` shape Story 5.2 already consumes — no change to `BonusDecayNormalizer` or the comparison UI.
- Wholesale/spot-price APIs (aWATTar, Tibber, SMARD) were explicitly ruled out — they price wholesale electricity, not retail contract terms, and are not a substitute for a tariff-comparison feature.

**Technical Recommendations:**

1. Run a direct vendor spike with tounify and Prezio before writing any adapter code — confirm real German coverage, auth, and production pricing.
2. Build the integration as a new `ITariffLookupProvider` port/adapter pair, following the existing `ISmartPlugParser` precedent exactly.
3. Use on-demand sync fetch (not scheduled ingest or webhook) with `Microsoft.Extensions.Http.Resilience` for retry/circuit-breaker/rate-limiting and a short-TTL cache to blunt duplicate calls.
4. Treat provider unavailability as a first-class, gracefully-degraded state (mirroring AD-8) — the existing manual-entry flow remains the fallback, never a hard dependency.
5. Add WireMock.Net-based contract tests for the adapter, and perform at least one genuine live-provider verification call before the implementing story reaches `done`, consistent with this project's existing live-verification process gate.

## Table of Contents

1. [Technical Research Scope Confirmation](#technical-research-scope-confirmation)
2. [Technology Stack Analysis](#technology-stack-analysis)
3. [Integration Patterns Analysis](#integration-patterns-analysis) (incl. Sync vs. Scheduled vs. Webhook comparison)
4. [Architectural Patterns and Design](#architectural-patterns-and-design)
5. [Implementation Approaches and Technology Adoption](#implementation-approaches-and-technology-adoption)
6. [Technical Research Recommendations](#technical-research-recommendations) (Roadmap, Stack, Success Metrics)
7. Technical Research Conclusion (below)
8. Source Documentation (below)

_Sections 1–6 above are the full research body, each with inline source citations; this synthesis cross-references rather than duplicates them._

---

## Future Technical Outlook

**Near-term (this integration):** the aggregator-API market (tounify, Prezio, Flatpeak) is young — expect documentation and self-serve onboarding to mature over the next 12–18 months as these products move past demo-gated sales motions, which may lower the vendor-spike friction found in this research if revisited later.

**Adjacent pattern worth tracking:** some UK-market providers (e.g. Switchd, "sign up once and we keep you switched to the best tariff forever") go beyond comparison into fully automated switching. This is a materially larger scope than Story 5.2's exploratory comparison and is explicitly out of scope for this research, but worth naming as a possible future direction if the Radar's ambition grows beyond "show me if switching is worth it" toward "switch me automatically."

**Utility-published open APIs:** at least one utility (EDF, UK) publishes its own open tariff API directly rather than going through a comparison aggregator. No equivalent was found for German utilities in this research pass — worth a follow-up check if a specific target utility becomes relevant (e.g. if the product later narrows to specific providers rather than broad comparison).
_Source: [EDF's Open Tariff APIs](https://www.edfenergy.com/energywise/edfs-open-tariff-apis), [Switchd](https://api.switchd.co.uk/api_quote)_

## Technical Research Conclusion

### Summary of Key Technical Findings

This feature is technically low-risk and architecturally native to the codebase, but commercially/vendor-wise unresolved: the two viable candidate APIs (tounify, Prezio) require direct engineering contact before their fitness can be confirmed, and the market's best-known brands (Verivox, Check24) are not directly integrable at all. No integration pattern beyond plain on-demand REST/JSON with standard resilience wrapping is needed — webhooks don't exist in this space, and scheduled ingest is a defensible but not currently justified alternative.

### Strategic Technical Impact Assessment

If a suitable provider is confirmed, this closes a real gap in Story 5.2's value proposition (today, a household member must manually source and enter a candidate tariff's terms) without touching the normalization math, UI, or audit-trail mechanisms already proven in Epic 5. The primary strategic risk is not technical but market-structural: committing to a young, thinly-documented aggregator introduces a dependency this project's other integrations don't currently have (metered, per-call cost with a vendor whose long-term product maturity is unproven).

### Next Steps Technical Recommendations

1. Ralf/product to decide whether the vendor spike (direct contact with tounify and Prezio) is worth pursuing now, or whether this stays a documented option until Epic 5 usage data suggests manual entry is a real friction point.
2. If pursued: treat the vendor spike as its own small, time-boxed research task — not folded into a development story — since its outcome (does either provider actually work for Germany at acceptable cost/quality) gates whether implementation proceeds at all.
3. If a provider is confirmed viable: this research's Implementation Roadmap (above) is ready to hand to `bmad-create-story`/`bmad-architecture` as-is.

---

## Source Documentation

**Primary technical sources used across this research:**

- [tounify.io](https://tounify.io/) — pan-European tariff aggregator API (pricing, coverage, auth)
- [Prezio](https://www.prezio.eu/) — pan-European tariff aggregator API (coverage, update cadence)
- [Octopus Energy REST API docs](https://docs.octopus.energy/rest/guides/endpoints/) — reference REST/auth pattern from an adjacent, better-documented market
- [Verivox Scraper on Apify](https://apify.com/studio-amba/verivox-scraper/api/openapi) — evidence that Verivox has no first-party API
- [WireMock.Net](https://github.com/wiremock/WireMock.Net) — recommended contract-testing tool
- [Polly / Microsoft.Extensions.Resilience](https://www.pollydocs.org/) — recommended resilience library
- project-context.md — this project's own architecture invariants (AD-1, AD-6, AD-8, AD-9, AD-11, AD-13, AD-19) used throughout to ground every recommendation in existing, proven patterns rather than proposing new ones

**Representative search queries run:** German tariff-comparison API access; electricity tariff comparison API Germany developer REST; open energy tariff APIs (aWATTar/Tibber/Octopus); .NET SDK options; hexagonal architecture/anti-corruption layer for third-party APIs; graceful degradation and cache-fallback patterns; sync-fetch vs. scheduled-ingest vs. webhook best practices; Polly resilience patterns; WireMock.Net contract testing; API vendor evaluation criteria; vendor lock-in mitigation; tariff-switching automation significance.

**Confidence levels:** High confidence on architectural fit (directly grounded in this project's own documented invariants) and on the absence of a Verivox/Check24 public API (consistent across multiple independent searches). Medium confidence on tounify/Prezio's exact German coverage depth, pricing at production volume, and reliability — both require direct vendor verification before being treated as fact, as stated throughout.

---

**Technical Research Completion Date:** 2026-09-23
**Source Verification:** All technical claims cited with sources above; unverifiable/vendor-gated claims explicitly flagged as requiring direct follow-up rather than presented as fact.
**Technical Confidence Level:** High on architectural recommendations; Medium on specific vendor claims pending direct vendor contact.

<!-- Content will be appended sequentially through research workflow steps -->
