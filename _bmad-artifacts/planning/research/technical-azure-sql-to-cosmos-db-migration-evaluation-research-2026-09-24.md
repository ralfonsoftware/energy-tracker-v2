---
stepsCompleted: [1, 2, 3, 4, 5, 6]
inputDocuments: []
workflowType: 'research'
lastStep: 6
research_type: 'technical'
research_topic: 'Azure SQL to Cosmos DB Migration Evaluation for Energy Tracker'
research_goals: 'Evaluate whether switching from Azure SQL (Basic tier) to Cosmos DB improves latency/performance for the trend view, and compare cost, application impact, and self-hosting alternatives. Motivated by slow trend-view data loading caused by cost-optimized Basic tier. Consider the one-time free Cosmos DB tier. Ground findings in real Azure metrics pulled from energy-tracker-rg over the last month.'
user_name: 'Ralf'
date: '2026-09-24'
web_research_enabled: true
source_verification: true
---

# Research Report: technical

**Date:** 2026-09-24
**Author:** Ralf
**Research Type:** technical

---

## Research Overview

This research evaluates whether switching from Azure SQL (Basic tier) to Cosmos DB would address slow data loading observed in Energy Tracker's trend view, using real Azure metrics pulled live via Azure CLI/Monitor/Cost Management/Log Analytics from the `energy-tracker-rg` deployment (not synthetic benchmarks), cross-referenced against current Microsoft Learn documentation for Cosmos DB's data model, EF Core provider, pricing, and self-hosting options.

The headline finding reframes the question: the actual slow path is `GET /api/smart-plug-readings` (p95 44.6s, max 120s, individual SQL calls averaging 11+ seconds against a ~760MB database) — a query/indexing problem, not primarily a datastore-capacity problem. Azure SQL's Basic-tier 5-DTU ceiling does saturate for sustained 35–45 minute windows during real usage, which compounds the issue, but latencies of this magnitude are far beyond what DTU contention alone typically produces. A datastore swap would not fix an unindexed query. Cosmos DB's unused free tier (1000 RU/s + 25GB, lifetime) would likely run this workload at €0/month if adopted, but doing so means retiring **AD-2** (dual-provider relational persistence) as an executable architecture invariant for any entity that moves, since EF Core's Cosmos provider has no migrations, ignores schema/index constructs, and lacks cross-container transactions — and it means losing the self-hosting symmetry Postgres provides today, since Cosmos has no supported production self-host path.

See the [Executive Summary and Recommendations](#executive-summary-and-recommendations) below for the full decision framework, roadmap, and risk assessment.

---

<!-- Content will be appended sequentially through research workflow steps -->

## Technical Research Scope Confirmation

**Research Topic:** Azure SQL to Cosmos DB Migration Evaluation for Energy Tracker
**Research Goals:** Evaluate whether switching from Azure SQL (Basic tier) to Cosmos DB improves latency/performance for the trend view, and compare cost, application impact, and self-hosting alternatives. Motivated by slow trend-view data loading caused by cost-optimized Basic tier. Consider the one-time free Cosmos DB tier. Ground findings in real Azure metrics pulled from energy-tracker-rg over the last month.

**Technical Research Scope:**

- Architecture Analysis - Cosmos DB data model vs. current dual-provider relational architecture (AD-2), and structural changes a migration would require
- Implementation Approaches - migration path impact on EF Core-centric invariants (AD-2 dual-provider portability, AD-3 tenant isolation via global query filter, AD-4 optimistic concurrency)
- Technology Stack - Cosmos DB APIs, .NET SDK/EF Core Cosmos provider maturity against .NET 10/EF Core 10, free-tier mechanics
- Integration Patterns - impact on ports & adapters, use-case classes, and consumer-facing API behavior
- Performance Considerations - root-causing the slow trend view (Basic-tier DTU throttling vs. query/indexing issues vs. genuine workload mismatch), latency/cost comparison grounded in real `energy-tracker-rg` metrics, self-hosting alternatives (Postgres tuning, self-hosted Cosmos DB emulator/alternatives)

**Research Methodology:**

- Current web data with rigorous source verification
- Multi-source validation for critical technical claims
- Confidence level framework for uncertain information
- Comprehensive technical coverage with architecture-specific insights
- Real usage/cost metrics pulled live via Azure CLI from `energy-tracker-rg`

**Scope Confirmed:** 2026-09-24

## Technology Stack Analysis

_Note: the generic template sections (IDE/editors, version control, testing frameworks) don't apply to a single-database-swap evaluation. Sections below are adapted to the actual decision: current environment baseline (from real Azure telemetry), Cosmos DB's data model/API, .NET/EF Core support, pricing models, and self-hosting alternatives._

### Current Environment Baseline (Real Azure Data — `energy-tracker-rg`, last 30 days)

Pulled live via Azure CLI/Monitor/Cost Management/Log Analytics against the actual deployed resources, not synthetic benchmarks.

**Azure SQL Database (`energytracker-prod-qvc6vfmtp5-sql/energytracker`):**
- SKU: **Basic tier, 5 DTU**, max size 2 GB, created 2026-08-12.
- 30-day DTU utilization: daily **average 0.81%**, but daily **maximum repeatedly hits 100%**.
- At 5-minute granularity on 2026-09-01, DTU sat at **97–100% continuously for ~35–45 minutes** in two separate windows (05:20–06:00 UTC and 19:55–20:45 UTC) — not brief spikes, sustained saturation of the entire 5-DTU ceiling.
- Storage: max 38% of 2 GB (~760 MB) — data volume is small.
- 30-day cost: **€4.14** for the database (from Cost Management, `ActualCost`, last 30 days).
- Zero connection failures, zero deadlocks recorded.

**Application-level evidence (Log Analytics workspace `energytracker-prod-law`, `AppRequests`/`AppDependencies` — Application Insights is workspace-based, data lives here, not in the classic App Insights query API which returned empty):**
- `GET /api/status/history` (the AD-7 Trend History endpoint) is **fast**: avg 193 ms, p95 392 ms. This is not where the perceived slowness lives.
- `GET /api/smart-plug-readings` (device-level readings feeding trend/detail views) is **catastrophically slow**: avg **26.0 seconds**, p95 **44.6 seconds**, max **120.3 seconds**, across 31 requests in 30 days.
- Two admin/import operations are even worse: `POST /api/smart-plug-imports/{id}/power-point-mapping` (avg 130 s, max 240 s) and `DELETE /api/smart-plug-import-jobs` (avg 77 s, max 240 s) — both look capped near 240 s, consistent with hitting a client/gateway timeout rather than completing naturally.
- Correlating `/api/smart-plug-readings` requests to their SQL dependency calls: ~3.8 SQL calls per request, but summed SQL duration (avg ~44.0 s) **exceeds** the request's own wall-clock duration (avg 26.0 s) — indicative of overlapping/retried SQL calls or a query executing outsized work per call (single queries averaging ~11+ seconds each), not a classic N+1 fan-out.
- 178 individual SQL dependency calls across the 30-day window exceeded 5 seconds; average 21.7 s, max **148.7 seconds**.

**Read on this evidence:** multi-minute latencies are far outside what DTU-5 contention alone typically produces for well-formed queries against a ~760 MB database — this pattern (huge per-query duration, small data volume, request timeouts capping near 240 s) is the signature of a missing index / full scan / inefficient query shape against `SmartPlugReading`, made dramatically worse by Basic tier's thin compute ceiling. **A store swap alone (to Cosmos DB or anything else) will not fix an inefficient query pattern** — query/indexing needs root-causing regardless of which datastore is chosen. This reframes the research question: is Cosmos DB being considered to fix a query-shape problem (wrong tool for that), or because DTU-5's compute ceiling itself is the bottleneck once queries are optimized (a real, evaluable tradeoff)?

**No existing Cosmos DB account exists in this subscription** (`az cosmosdb list` returns empty) — confirms the free tier is genuinely unused and available.

### Cosmos DB Data Model & APIs

_Cosmos DB is a multi-model NoSQL service; the relevant comparison point here is the **NoSQL (document) API**, its native mode._

- Document-oriented, schemaless JSON storage, partitioned by a required **partition key** chosen at container creation (not changeable later without a full data migration).
- No cross-partition ACID transactions; transactional batches are scoped to a single partition key. This differs fundamentally from Azure SQL's/EF Core's cross-table transactional guarantees.
- Indexing is automatic by default (all properties indexed) but tunable per-container — a different model than SQL Server's explicit index design.
- Multiple APIs exist (NoSQL, MongoDB, Cassandra, Gremlin, Table) — only the native **NoSQL API** has a real EF Core provider; the others would mean abandoning EF Core entirely for this data.

### .NET SDK / EF Core Support

- `Microsoft.EntityFrameworkCore.Cosmos` (current version 10.0.8, aligned with this project's EF Core 10 line) exists and is officially supported, but with well-documented structural limitations _(Source: [EF Core Cosmos provider limitations](https://learn.microsoft.com/en-us/ef/core/providers/cosmos/limitations), Microsoft Learn, updated 2026-02-10)_:
  - **No migrations** — Cosmos has no schema, so `scripts/add-migration.sh`'s entire dual-provider migration flow (AD-2) has no Cosmos equivalent.
  - **No scaffolding/reverse-engineering** from an existing database.
  - **EF model constructs like indexes and constraints are ignored** — they have no meaning against a schemaless store.
  - **No full ACID/relational-style transactions across containers** — only transactional batches within a single partition key.
- Conclusion: **AD-2's core invariant — "one shared `EnergyTrackerDbContext`, only the portable relational subset, migrations applied to both providers in the same commit" — cannot be preserved.** A Cosmos "third provider" would not be a drop-in third leg of the existing dual-provider pattern; it would require either a parallel non-EF-relational data-access path for Cosmos-backed entities, or abandoning the shared-DbContext model for whichever entities moved. This is an architecture-spine-level change, not a config-driven adapter swap.

### Pricing Models

_(Source: [Compare Provisioned Throughput and Serverless](https://learn.microsoft.com/en-us/azure/cosmos-db/throughput-serverless), [Serverless](https://learn.microsoft.com/en-us/azure/cosmos-db/serverless), [Lifetime Free Tier](https://learn.microsoft.com/en-us/azure/cosmos-db/free-tier), Microsoft Learn)_

- **Free tier:** first 1000 RU/s + 25 GB storage **free for the lifetime of the account** — one free-tier account per Azure subscription. Not a time-limited trial; it's a standing allocation. Confirmed available and unused in `energy-tracker-rg`'s subscription.
- **Serverless:** $0.25 per 1M RUs consumed, no minimum commitment, single-region only — well suited to this app's low, bursty request volume (7,357 total requests across all endpoints in 30 days) if it weren't for the free tier making provisioned throughput free up to 1000 RU/s anyway.
- **Provisioned (manual or autoscale):** ~$0.008 per 100 RU/s/hour; a 400 RU/s minimum provisioned container runs ≈ $24/month if paid — but this project's data volume (~760 MB) and request rate would comfortably fit inside the always-free 1000 RU/s / 25 GB tier, meaning the **Cosmos DB piece itself could run at €0/month**, cheaper than the current €4.14/month Azure SQL Basic cost.
- Caveat: free tier is single-account, single-region; production HA/multi-region would exceed it.

### Self-Hosting Alternatives

- Microsoft ships a **Linux-based Cosmos DB Emulator** (Docker container, `mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator`), explicitly positioned for **development and CI/testing only** — not a supported production deployment target.
- There is **no supported production self-hosted Cosmos DB** (no Azure Stack HCI offering, no on-prem Cosmos DB server product) — this breaks the symmetry this project currently has with Postgres (AD-2's actual self-host target). Moving `SmartPlugReading`/`Event` (or any entity) onto Cosmos DB would mean **losing the self-hosting alternative entirely** for that data, unless paired with a genuinely different self-hostable NoSQL/document engine (e.g., self-hosted MongoDB, or Postgres `jsonb` columns kept within the existing dual-provider Postgres/SQL Server split) as the "self-host" counterpart — which is a different, separate architectural decision from "adopt Cosmos DB."

**Key Technology Stack Findings:**

- Real telemetry shows the actual slow path is `/api/smart-plug-readings` (20–150+ second SQL calls against a 760 MB database) — a query/indexing problem, not primarily a DTU-ceiling problem; the Trend History endpoint itself (AD-7) is already fast.
- EF Core's Cosmos provider cannot preserve AD-2's shared-DbContext, migrations-in-both-providers invariant — adopting Cosmos for any entity is an architecture-spine change, not a config-driven swap.
- The free tier (1000 RU/s + 25 GB, lifetime) is unused and would likely make the Cosmos piece cost €0/month at this data volume — but Cosmos has **no supported production self-hosted equivalent**, unlike the current Postgres self-host path.

## Integration Patterns Analysis

_Note: the generic template (GraphQL, gRPC, message brokers, service mesh, OAuth) doesn't apply to a single-app database swap — this app's external API surface (REST over HTTP, cookie/OIDC auth) doesn't change regardless of datastore. Sections below are adapted to what actually changes at the data-access boundary: consistency model impact on correctness, a new throttling failure mode, and tenant-isolation/partition design._

### Data Access Pattern Impact (Ports & Adapters)

- Today, every `Application/Ports` interface is implemented once against `EnergyTrackerDbContext` and works unmodified against both Postgres and Azure SQL (AD-2). A Cosmos-backed entity would need either a **second, Cosmos-specific `DbContext`** (breaking "one shared context") or a **non-EF adapter using the Cosmos SDK directly** for that entity's port — either way, the "config-driven adapter selection, one code path" invariant that today applies uniformly across the whole domain would instead apply per-entity, with Cosmos as a structurally different third path, not a third value of the same config switch.
- **AD-10** (soft-delete, snapshot-by-value for `SmartPlugReading`/`Event`) actually fits Cosmos's document model well — snapshot-by-value data is naturally document-shaped and doesn't need relational joins, which is the strongest architectural argument *for* considering Cosmos, but only for that specific slice of data, not as a wholesale replacement.

### Consistency Model Impact on Correctness

_(Source: [Consistency levels — Azure Cosmos DB](https://learn.microsoft.com/en-us/azure/cosmos-db/consistency-levels), [Manage consistency levels](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-manage-consistency), Microsoft Learn)_

- Cosmos DB's default is **Session consistency** (5 levels total: Strong, Bounded Staleness, Session, Consistent Prefix, Eventual) — guarantees are per-client-session, not global, unlike Azure SQL/Postgres's transactional read-your-writes-everywhere guarantee.
- **Strong consistency** is available but **costs 2x RUs** (quorum read vs. single-replica read) and is blocked by default across regions >5,000 miles apart — not a concern for a single-region West Europe deployment, but a real cost multiplier if chosen.
- Relevance to **AD-4** (optimistic concurrency via `int Version`): Cosmos has its own native ETag-based optimistic concurrency (`if-match` headers), which the EF Core Cosmos provider maps to automatically — mechanically workable, but it's a **different concurrency primitive** than the current `DbUpdateConcurrencyException → HTTP 409` pattern shared uniformly across both existing providers. It would need its own tested code path, not reuse of the existing one.
- Relevance to **AD-3** (tenant isolation via global query filter): a global query filter can still be expressed in LINQ against the Cosmos provider, but without a **partition key on `HouseholdId` also present in the query**, EF Core issues a costly cross-partition **fan-out query across the whole container** rather than a single-partition point query — the isolation filter alone doesn't get you the same performance guarantee it does today via a SQL index.

### Throttling as a New Failure Mode (429s)

_(Source: [Troubleshoot Request Rate Too Large](https://learn.microsoft.com/en-us/azure/cosmos-db/troubleshoot-request-rate-too-large), [Performance Tips — .NET SDK v3](https://learn.microsoft.com/en-us/azure/cosmos-db/performance-tips-dotnet-sdk-v3), Microsoft Learn)_

- Where Azure SQL Basic tier degrades under load by slowing down (the DTU-saturation pattern seen in the real telemetry above), Cosmos DB **rejects** over-budget requests outright with HTTP 429 ("Request Rate Too Large") once provisioned/free-tier RU/s is exhausted.
- The .NET SDK retries 429s automatically (default up to 9 retries / 30s cumulative wait, configurable via `CosmosClientOptions.MaxRetryAttemptsOnRateLimitedRequests` / `MaxRetryWaitTimeOnRateLimitedRequests`), but this is a **new exception class and retry-policy surface** the application doesn't have today — worth an explicit test if any entity moves to Cosmos, especially since the free tier's 1000 RU/s ceiling is a hard wall, not a "just slower" degradation like DTU throttling.

### Tenant Isolation / Partition Key Design

_(Source: [Multitenancy and Azure Cosmos DB — Azure Architecture Center](https://learn.microsoft.com/en-us/azure/architecture/guide/multitenant/service/cosmos-db), [EF Core Cosmos modeling](https://learn.microsoft.com/en-us/ef/core/providers/cosmos/modeling), Microsoft Learn)_

- Best practice for this shape of multi-tenant data is a **hierarchical partition key** (EF Core 9+ supports up to 3 levels via `HasPartitionKey`) — e.g. `HouseholdId` as the top-level partition key, mirroring AD-3's tenant boundary almost exactly, with a natural fit for household-scoped queries staying single-partition.
- EF Core 9+ automatically extracts partition-key equality comparisons from LINQ `Where` clauses to route queries to the correct partition(s) — meaning a correctly-designed partition key **can** preserve most of AD-3's query-shape today, but this has to be deliberately modeled per entity; it isn't inherited "for free" the way the current global query filter is applied uniformly across every `DbSet`.
- **Partition key choice is fixed at container creation** — changing it later requires a full data migration to a new container, unlike adding an index or column to a SQL table.

**Key Integration Patterns Findings:**

- The app's external API surface (REST, cookie/OIDC auth) is unaffected either way — this is purely a data-access-layer decision.
- Cosmos's default Session consistency and ETag-based concurrency are workable but are a **different correctness model** than today's uniform cross-provider pattern, and would need their own dedicated test coverage rather than reusing existing dual-provider tests.
- 429 throttling is a **new hard-failure mode** (vs. Azure SQL's "just gets slower" DTU throttling) that the app has no existing handling for.
- A `HouseholdId`-based hierarchical partition key would map naturally onto AD-3's tenant boundary — the one place Cosmos's data model genuinely aligns well with this app's existing architecture, though it must be deliberately designed per entity.

**Ready to proceed to architectural patterns analysis?**
[C] Continue - Save this to document and proceed to architectural patterns

## Architectural Patterns and Design

_Note: the generic template (SOLID, GraphQL vs REST, load balancing, consensus algorithms) doesn't fit a single-datastore decision on an existing system. Sections below are adapted to what actually matters for adopting Cosmos DB here: full-migration vs. polyglot-persistence framing, testing architecture fit, security architecture fit, and backup/DR architecture._

### System Architecture Pattern: Full Migration vs. Polyglot Persistence

Two structurally different options exist, and they carry very different risk profiles against this project's spine invariants:

1. **Full migration** — replace Azure SQL/Postgres entirely with Cosmos DB as the single datastore. This would require **retiring AD-2 (dual-provider relational persistence) outright** — every entity, every migration, every relational query (joins, LINQ across `DbSet`s) would need a document-shaped redesign. Given the Architecture Tests already encode AD-2 as an executable invariant (`DomainHasNoExternalDependenciesTests` and friends in `EnergyTracker.Architecture.Tests`), this is a spine-level rewrite, not an incremental change — high blast radius for a problem currently isolated to one endpoint (`/api/smart-plug-readings`).
2. **Polyglot persistence (targeted)** — keep AD-2's relational core for tenant/household/tariff/meter-reading data (where relational joins and cross-provider portability matter), and move only high-volume, already-document-shaped, snapshot-by-value data (`SmartPlugReading`, `Event` — AD-10) to Cosmos DB via a dedicated non-EF adapter behind its existing `Application/Ports` interface. This is architecturally the smaller, more defensible change: it adds "Cosmos" as a genuinely new adapter type (not a new value of the existing DB-provider config switch), scoped to the one area of the schema that's both causing the pain and structurally suited to a document model.

**Given the real telemetry (query latency, not overall data volume, is the actual problem — see Technology Stack Analysis above), neither option addresses the root cause on its own.** Whichever path is chosen, root-causing the specific slow query/index first is the higher-leverage, lower-risk move — and doing so may make the whole migration question moot for the current pain point.

### Testing Architecture Fit

_(Source: [Testcontainers Azure Cosmos DB Module](https://testcontainers.com/modules/cosmodb/), [testcontainers-dotnet CosmosDb tests](https://github.com/testcontainers/testcontainers-dotnet/blob/develop/tests/Testcontainers.CosmosDb.Tests/CosmosDbContainerTest.cs), Microsoft/community sources)_

- A `Testcontainers.CosmosDb` module exists for .NET, spinning up the Linux-based Cosmos DB Emulator in a container per test run — this **fits the project's existing testing convention directly** (today: `Testcontainers.PostgreSql`/`Testcontainers.MsSql`, real-engine integration tests, not in-memory fakes). Adopting Cosmos would not require abandoning the Testcontainers-based integration test strategy.
- This is one of the few areas where Cosmos slots into the existing engineering conventions with minimal friction.

### Security Architecture Fit

_(Source: [Access account resources using Microsoft Entra ID](https://learn.microsoft.com/en-us/training/modules/implement-security-azure-cosmos-db-sql-api/5-access-account-resources-using-azure-active-directory), [Cosmos DB RBAC and passwordless authentication](https://cloudchronicles.blog/blog/Azure-Cosmos-DB-RBAC-and-passwordless-authentication/), Microsoft Learn/community)_

- Primary-key authentication grants full, unrestricted account access and is explicitly discouraged; **Entra ID data-plane RBAC with managed identity** is the recommended pattern, with `disableLocalAuth` available to reject key-based auth entirely.
- This aligns with the project's existing direction (managed-identity-based auth already used for ACR per `docs/local-vs-azure-deltas.md`) and with **AD-17**'s secrets posture (no committed/baked-in secrets) — a Cosmos adoption would extend the existing managed-identity pattern rather than introduce a new secret class, provided it's configured with Entra ID RBAC from the start rather than a connection-string key.

### Backup / Disaster-Recovery Architecture

_(Source: [Continuous Backup with Point-in-Time Restore](https://learn.microsoft.com/en-us/azure/cosmos-db/continuous-backup-restore-introduction), Microsoft Learn)_

- Cosmos DB's continuous backup offers 7-day point-in-time restore, often at no extra storage cost depending on region; 30/35-day retention adds **$0.20/GB/region/month** in backup storage plus a one-time restore charge.
- At this project's current data volume (~760 MB), backup costs are immaterial regardless of tier chosen — this is not a differentiator either way.

**Key Architectural Findings:**

- **Root-causing the slow query is the higher-leverage move regardless of datastore choice** — the real telemetry shows a query/indexing problem, not fundamentally a capacity problem.
- If a datastore change is still pursued, **targeted polyglot persistence** (Cosmos for `SmartPlugReading`/`Event` only, behind the existing ports/adapters boundary) is architecturally far cheaper than a full migration, which would require retiring AD-2 as an executable invariant.
- Testing (Testcontainers.CosmosDb) and security (Entra ID RBAC/managed identity) both fit the project's existing conventions well — these are not blockers.
- Backup/DR cost is immaterial at current data volume.

**Ready to proceed to implementation research?**
[C] Continue - Save this to the document and move to implementation research

## Implementation Approaches and Technology Adoption

_Note: the generic template (team org/skills, CI/CD tooling, general DevOps) is adapted to what's decision-relevant here: how to actually diagnose the root cause, the cheap zero-code-change lever (SQL tier bump), and, if Cosmos is still pursued, a safe migration pattern._

### Technology Adoption Strategies: Diagnose Before Migrating

_(Source: [Detectable types of query performance bottlenecks — Azure SQL Database](https://learn.microsoft.com/en-us/azure/azure-sql/database/identify-query-performance-issues?view=azuresql), Microsoft Learn)_

- Azure SQL Database's built-in **Query Store** + **Query Performance Insight** (Azure Portal, no extra cost, available on Basic tier) captures exactly the kind of evidence needed here: which query text is behind the 20–150+ second SQL calls seen against `/api/smart-plug-readings`, its execution plan, and whether a missing index is the cause. This is the concrete next diagnostic step, and it's free and immediate — no architecture change required to run it.
- Recommended sequence: **enable/inspect Query Store → identify the specific slow query plan → check for a missing/covering index on `SmartPlugReading` (likely on the household/date-range predicate the trend/readings view filters by) → re-measure.** This is a same-day diagnostic that the Cosmos DB question shouldn't be decided ahead of.

### Cost Optimization: The Cheap Lever You Haven't Tried

_(Source: [DTU-based purchasing model](https://learn.microsoft.com/en-us/azure/azure-sql/database/service-tiers-dtu?view=azuresql), pricing pages, Microsoft Learn/community)_

- Current Basic tier: 5 DTU, ~$4.90/month (matches the real €4.14 spend measured above).
- **Standard S0**: 10 DTU (2x), up to 250 GB, ~$14.72/month.
- A **zero-code-change tier bump** (Basic → S0 or S1) is reversible in minutes via the Azure Portal/CLI, costs a few euros more per month, and would directly test whether DTU ceiling (vs. query shape) is the dominant factor — this is a far cheaper experiment than a Cosmos migration, and should be tried (alongside the Query Store diagnosis) before committing to a datastore change.

### Migration Pattern (If Still Pursued): Strangler Fig, Not Dual-Write

_(Source: [Strangler Fig Pattern — Azure Architecture Center](https://learn.microsoft.com/en-us/azure/architecture/patterns/strangler-fig), Microsoft Learn/community)_

- If `SmartPlugReading`/`Event` are moved to Cosmos DB after all, the safe pattern is **strangler fig with a single authoritative writer at a time**, not naive dual-writes: dual writes from the application layer risk drift under partial failure (one store gets ahead with no record of which is correct). Since these entities are already append-mostly/snapshot-by-value (AD-10) rather than frequently updated, a **backfill-then-cutover** approach (bulk-copy existing history to Cosmos, then switch the write path behind the existing port in one deploy, keep SQL as read-only fallback briefly) is lower-risk than a live dual-write period for this specific data shape.
- This reinforces the polyglot-persistence framing from the Architectural Patterns section — a narrow, well-bounded migration for one or two entities, not a system-wide cutover.

### Risk Assessment and Mitigation

| Risk | Mitigation |
|---|---|
| Migrating without root-causing the actual slow query first — spend effort and get no relief | Run Query Store/QPI diagnosis and try the SQL tier bump before deciding |
| Losing AD-2's self-hosting symmetry for any entity moved to Cosmos | Explicit tradeoff to accept, or pair with a self-hostable NoSQL/JSONB alternative for parity |
| New 429 throttling failure mode with no existing handling | Configure `CosmosClientOptions` retry policy explicitly and add a dedicated integration test (Testcontainers.CosmosDb fits existing convention) |
| Partition key chosen poorly and needing a full re-migration to fix | Model `HouseholdId` as the (hierarchical) partition key deliberately before writing data, informed by AD-3's existing tenant boundary |
| AD-4 concurrency pattern not carried over | Design and test the ETag-based Cosmos concurrency path explicitly; don't assume the existing `DbUpdateConcurrencyException` tests cover it |

## Technical Research Recommendations

### Implementation Roadmap

1. **Immediate (same day, no architecture change):** Use Query Store / Query Performance Insight on the existing Azure SQL Basic database to find the exact query/plan behind `/api/smart-plug-readings`'s 20–150s latencies; check for a missing index.
2. **Cheap experiment (minutes, reversible):** Temporarily bump the SQL tier (Basic → Standard S0/S1) to test whether DTU ceiling is a material contributor, independent of the query-shape fix.
3. **If a genuine capacity/latency gap remains after query optimization,** evaluate **targeted Cosmos DB adoption** scoped to `SmartPlugReading`/`Event` only (not a full migration), using the free tier (1000 RU/s + 25 GB, confirmed unused and available), Entra ID RBAC, a `HouseholdId`-based partition key, and a backfill-then-cutover migration (not dual-write).
4. **Do not** treat this as a full AD-2 replacement — the relational core (Household, Tariff, MeterReading and their joins) has no evidence of needing to move, and doing so would be a much larger, higher-risk change than the actual problem warrants.

### Technology Stack Recommendations

- Keep AD-2's dual-provider relational core (Postgres self-host / Azure SQL) unchanged.
- If Cosmos is adopted: `Microsoft.EntityFrameworkCore.Cosmos` 10.0.8 (matches EF Core 10 line), NoSQL API only, Entra ID RBAC (not primary keys), `Testcontainers.CosmosDb` for integration tests.

### Skill Development Requirements

- Partition key / document modeling is a genuinely new skill area relative to this team's current relational EF Core expertise — budget time to model it correctly before writing production data, since it's expensive to change later.
- RU-based capacity planning and 429 retry-policy tuning are new operational concepts not present in the DTU model today.

### Success Metrics and KPIs

- `/api/smart-plug-readings` p95 latency (currently 44.6s) — target should be sub-second to low-single-digit seconds regardless of which fix is applied.
- DTU/RU utilization ceiling headroom during peak windows (currently pinned at ~99–100% DTU for 35–45 minute stretches).
- Monthly datastore cost (currently €4.14/month for SQL) — a Cosmos free-tier account at this data volume should stay at €0, making cost a non-blocker either way.

**Implementation Highlights:**

- The single highest-leverage, lowest-risk next step is diagnostic (Query Store) and a cheap reversible experiment (SQL tier bump) — both come before any Cosmos decision.
- If Cosmos is still warranted afterward, scope it narrowly (polyglot persistence for `SmartPlugReading`/`Event`), use strangler-fig backfill-then-cutover (not dual-write), and budget for the genuinely new skills (partition key design, RU capacity planning, 429 handling) this introduces.

**Technical research phases completed:**

- Step 1: Research scope confirmation
- Step 2: Technology stack analysis
- Step 3: Integration patterns analysis
- Step 4: Architectural patterns analysis
- Step 5: Implementation research (current step)

**Ready to proceed to the final synthesis step?**
[C] Continue - Save this to document and proceed to synthesis

---

# Azure SQL to Cosmos DB Migration Evaluation: Executive Summary and Recommendations

## Executive Summary and Recommendations

Energy Tracker's slow trend-view data loading is real, but the evidence — pulled live from `energy-tracker-rg`'s own Azure telemetry, not synthetic benchmarks — points somewhere different than expected. The AD-7 Trend History endpoint (`/api/status/history`) is already fast (avg 193ms). The actual bottleneck is `GET /api/smart-plug-readings`: avg 26.0s, p95 44.6s, max 120.3s, with individual SQL calls behind it averaging over 11 seconds each against a database that's only ~760MB. That magnitude of latency against that little data is the signature of a missing index or an inefficient query plan — not primarily a hardware-capacity ceiling. Azure SQL's Basic tier (5 DTU, €4.14/month over the last 30 days) does pin at 97–100% DTU for sustained 35–45 minute windows during real usage, which compounds the problem, but wouldn't on its own explain 120-second single requests.

**Cosmos DB would not fix an unindexed query.** Its data model is a poor match for "make this SQL query faster" as a goal. Where Cosmos does offer something real: its lifetime-free tier (1000 RU/s + 25GB storage, confirmed unused in this subscription) would likely run this app's actual data volume and request rate at €0/month, undercutting even the already-cheap €4.14/month Basic tier — but only if the migration itself doesn't cost more in engineering risk than it saves in hosting fees. And it does carry real cost: EF Core's Cosmos provider has no migrations, ignores EF's index/constraint model, and doesn't support cross-container transactions — adopting it for any entity means that entity exits **AD-2**'s dual-provider, shared-`DbContext`, portable-relational-subset invariant, which today is enforced as an executable architecture test, not just a convention. It also means losing the self-hosting symmetry Postgres currently provides, since Microsoft has no supported production self-hosted Cosmos DB — only a dev/CI-only Linux emulator.

**Recommendation, in order:**

1. **Diagnose first, same day, no architecture change:** run Azure SQL's built-in Query Store / Query Performance Insight (free, works on Basic tier) against `/api/smart-plug-readings`'s query pattern to find the actual missing index or bad plan.
2. **Cheap, reversible experiment:** temporarily bump Basic → Standard S0 (5→10 DTU, ~€4.90→~€14.72/month) to isolate whether DTU ceiling is a material independent factor, separate from the query-shape fix.
3. **Only if a genuine, unavoidable capacity/latency gap remains after query optimization**, adopt Cosmos DB — scoped narrowly to `SmartPlugReading`/`Event` (which are already snapshot-by-value/document-shaped per AD-10), as a new adapter type behind the existing `Application/Ports` interface, not a full AD-2 replacement. Use the free tier, Entra ID RBAC (not primary keys), a `HouseholdId`-based (hierarchical) partition key matching AD-3's tenant boundary, `Testcontainers.CosmosDb` for integration tests (fits the existing Testcontainers convention), and a backfill-then-cutover migration rather than a dual-write period.
4. **Do not** treat this as a wholesale relational-to-NoSQL migration. Nothing in the real telemetry suggests Household, Tariff, or MeterReading data — the relationally-joined core — has any performance problem or reason to move.

**Success metrics to track regardless of which path is taken:** `/api/smart-plug-readings` p95 latency (baseline: 44.6s; target: low single-digit seconds or better), DTU/RU utilization headroom during peak windows (baseline: pinned ~99–100% DTU for 35–45 minutes), and monthly datastore cost (baseline: €4.14/month).

## Technical Research Methodology and Source Verification

**Real environment data (primary source, highest confidence):** pulled live via Azure CLI against the actual `energy-tracker-rg` deployment on 2026-09-24 — `az sql db show`, `az monitor metrics list` (DTU/CPU/storage, daily and 5-minute granularity), the Cost Management REST API (30-day actual cost by resource), and `az monitor log-analytics query` against the workspace-based Application Insights tables (`AppRequests`, `AppDependencies`) in `energytracker-prod-law`. This is ground truth for this specific application, not an industry benchmark.

**Documentation sources (secondary, cross-verified against Microsoft Learn where possible):**
- [EF Core Cosmos provider limitations](https://learn.microsoft.com/en-us/ef/core/providers/cosmos/limitations) — Microsoft Learn, updated 2026-02-10
- [Lifetime Free Tier — Azure Cosmos DB](https://learn.microsoft.com/en-us/azure/cosmos-db/free-tier) — Microsoft Learn
- [Compare Provisioned Throughput and Serverless](https://learn.microsoft.com/en-us/azure/cosmos-db/throughput-serverless), [Serverless](https://learn.microsoft.com/en-us/azure/cosmos-db/serverless) — Microsoft Learn
- [Consistency levels](https://learn.microsoft.com/en-us/azure/cosmos-db/consistency-levels), [Manage consistency levels](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-manage-consistency) — Microsoft Learn
- [Troubleshoot Request Rate Too Large](https://learn.microsoft.com/en-us/azure/cosmos-db/troubleshoot-request-rate-too-large), [Performance Tips — .NET SDK v3](https://learn.microsoft.com/en-us/azure/cosmos-db/performance-tips-dotnet-sdk-v3) — Microsoft Learn
- [Multitenancy and Azure Cosmos DB](https://learn.microsoft.com/en-us/azure/architecture/guide/multitenant/service/cosmos-db), [EF Core Cosmos modeling](https://learn.microsoft.com/en-us/ef/core/providers/cosmos/modeling) — Microsoft Learn
- [Testcontainers Azure Cosmos DB Module](https://testcontainers.com/modules/cosmodb/), [testcontainers-dotnet CosmosDb tests](https://github.com/testcontainers/testcontainers-dotnet/blob/develop/tests/Testcontainers.CosmosDb.Tests/CosmosDbContainerTest.cs)
- [Access account resources using Microsoft Entra ID](https://learn.microsoft.com/en-us/training/modules/implement-security-azure-cosmos-db-sql-api/5-access-account-resources-using-azure-active-directory), [Cosmos DB RBAC and passwordless authentication](https://cloudchronicles.blog/blog/Azure-Cosmos-DB-RBAC-and-passwordless-authentication/)
- [Continuous Backup with Point-in-Time Restore](https://learn.microsoft.com/en-us/azure/cosmos-db/continuous-backup-restore-introduction) — Microsoft Learn
- [DTU-based purchasing model](https://learn.microsoft.com/en-us/azure/azure-sql/database/service-tiers-dtu?view=azuresql) — Microsoft Learn
- [Detectable types of query performance bottlenecks — Azure SQL Database](https://learn.microsoft.com/en-us/azure/azure-sql/database/identify-query-performance-issues?view=azuresql) — Microsoft Learn
- [Strangler Fig Pattern — Azure Architecture Center](https://learn.microsoft.com/en-us/azure/architecture/patterns/strangler-fig) — Microsoft Learn
- [Azure Cosmos DB Linux-based emulator](https://learn.microsoft.com/en-us/azure/cosmos-db/emulator-linux) — Microsoft Learn

**Limitations:** SQL query text/execution plans were not captured by the OTel SqlClient instrumentation currently deployed (only duration/target, not command text), so the specific missing index could not be identified directly from telemetry — the Query Store step in the recommendation above is what closes that gap. Cosmos DB RU consumption for this app's actual query patterns is estimated from documented pricing, not measured against a live trial account.

## Technical Research Conclusion

The trigger for this research — cost-saving Azure SQL Basic tier causing slow trend-view data — turned out, on inspection of real telemetry, to be a slow-query problem on a different endpoint (`/api/smart-plug-readings`) than the one assumed (Trend History, which is already fast), compounded by but not solely caused by the Basic tier's DTU ceiling. Cosmos DB's free tier is genuinely available and would be cost-neutral-to-positive at this data volume, but adopting it is an architecture-spine-level decision (retiring AD-2 for any entity that moves) that shouldn't be made before the cheaper, faster, reversible diagnostic and tier-bump steps have been tried. If those don't resolve it, a narrowly-scoped Cosmos adoption for `SmartPlugReading`/`Event` is the well-bounded path — not a full relational-to-NoSQL migration.

**Next steps:** run Query Store against the live database this week; if inconclusive, trial the Basic→S0 tier bump for a few days during normal usage and re-measure `/api/smart-plug-readings` latency before deciding on Cosmos DB.

---

**Technical Research Completion Date:** 2026-09-24
**Research Period:** Current analysis, grounded in 30-day real Azure telemetry (2026-08-25 to 2026-09-24)
**Document Length:** Scoped to the actual decision at hand, not padded to a fixed template
**Source Verification:** Real Azure metrics (primary) cross-verified against current Microsoft Learn documentation (secondary)
**Technical Confidence Level:** High for the root-cause diagnosis (based on direct telemetry); Medium-High for Cosmos DB cost/behavior projections (based on documented pricing/behavior, not a live trial measurement)

_This document serves as the decision record for Energy Tracker's Azure SQL vs. Cosmos DB evaluation, grounded in the project's own production telemetry rather than generic benchmarks._
