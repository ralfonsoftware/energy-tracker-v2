---
title: Rubric-Walker Review — Energy Tracker v2 Architecture Spine
reviews: ARCHITECTURE-SPINE.md (energy-tracker-v2, 2026-08-09)
created: 2026-08-09
---

# Rubric-Walker Review: ARCHITECTURE-SPINE.md

Scope of this review: the "good-spine checklist" — divergence coverage, per-AD Rule enforceability, Deferred-section safety, tech currency, dimension completeness (esp. operational envelope), and leanness. Cross-checked against `prd.md`, `addendum.md`, and `.memlog.md` in the same artifact tree.

## Overall

The spine is well-structured and unusually disciplined for its scope — 18 ADs cover persistence, tenancy, concurrency, async processing, scheduling-vs-scale-to-zero, AI, parsing, historical integrity, audit, deployment topology, the core "no Residual" invariant, genericity, offline writes, session auth, and i18n, each with a Binds/Prevents/Rule triplet and most tied back to specific FRs. It correctly diagnoses and resolves several genuinely subtle conflicts (AD-7's live-compute-vs-no-retroactive-rewrite tension, AD-6's polling-vs-scale-to-zero tension). However, it has one missed divergence point that is arguably more dangerous than any it does catch (session cookies vs. scale-to-zero container restarts, see Finding 1), a couple of ADs whose Rule doesn't deliver the structural protection its own Prevents clause demands, and a genuinely silent dimension: operations/observability has no AD, no Consistency Convention row, and no Deferred entry anywhere in the document.

## Findings

### Finding 1 — HIGH: AD-17's session cookie will not survive the scale-to-zero cycles the rest of the spine is built around

AD-13/AD-6/AD-7 invest heavily in making the system correct under Azure Container Apps scale-to-zero (in-process job queue vs. external, live-compute vs. scheduled Status, polling vs. push). AD-17 picks ASP.NET Core cookie authentication chained to OIDC for "stays logged in on their phone" durability — but says nothing about Data Protection key persistence. By default, the keys ASP.NET Core uses to encrypt/decrypt the auth cookie are held in-memory (or on ephemeral container disk); on every scale-to-zero → cold-start cycle in a fresh container instance, those keys are regenerated, and every previously-issued session cookie becomes silently un-decryptable — forcing a full re-login. That's the exact failure AD-17 was written to bind against (UJ-1's "stays logged in on their phone"), and it would fire on a schedule the rest of the spine treats as a first-class concern. The fix is a one-line addition consistent with AD-2's own shared-DbContext model — persist the key ring externally (e.g. `PersistKeysToDbContext`, which works identically across both providers, or blob/file storage) — but as written, AD-17's Rule does not prevent its own stated divergence under the deployment model the spine elsewhere insists on.

**Recommendation:** Add a sentence to AD-17's Rule: Data Protection keys are persisted externally (DB-backed key ring, consistent with AD-2's shared DbContext) rather than left at the in-memory/local-disk default, specifically because AD-13's single-container/scale-to-zero model means the key ring cannot be assumed to survive a cold start.

### Finding 2 — MEDIUM: AD-3's tenant-isolation Rule doesn't cover the EF Core APIs that bypass global query filters

AD-3's Rule is "the DbContext is the single enforcement point" via `HasQueryFilter`. That's correct for ordinary LINQ queries, but EF Core's global query filters are well-documented to *not* apply to: `DbSet<T>.Find()`, raw SQL via `FromSqlRaw`/`FromSqlInterpolated` (unless manually recomposed with the filtered `IQueryable`), and any query with `.IgnoreQueryFilters()`. AD-3's Prevents clause is explicitly about a handler "forgetting to filter by Household" — but nothing in the Rule forbids or flags these three bypass paths, so a handler that reaches for `Find(id)` (a very natural thing to write) silently defeats the isolation guarantee the AD exists to provide, with no compiler or reviewer signal that anything is wrong.

**Recommendation:** Either add an explicit prohibition ("no application code calls `DbSet<T>.Find()`, raw SQL, or `.IgnoreQueryFilters()` against Household-scoped entities") or name the enforcement mechanism that would catch it (Roslyn analyzer / architecture test). As written the Rule states an intent, not a guarantee.

### Finding 3 — MEDIUM: AD-14 (the brief's single non-negotiable invariant) explicitly demands structural protection, then delivers a naming convention

AD-14's own Prevents clause says: "This is the brief's single named non-negotiable invariant; the spine must protect it structurally, not just by naming it." The Rule that follows is exactly a naming/discipline convention: "no domain or application code sums SmartPlugReading or Event data into a figure... there is no `Residual` type, field, or view anywhere in the system." Nothing enforces this beyond a developer remembering not to write that code — no architecture test forbidding a LINQ query that groups/sums `SmartPlugReading`/`Event` alongside `MeterReading`, no naming ban enforced by CI, nothing. For the one AD the spine itself flags as needing to be more than convention, it should either name a concrete enforcement mechanism (e.g., an architecture-test asserting no type/method whose signature returns a value compared against `MeterReading.Value` exists outside `Domain.Calculations`) or drop the "not just by naming it" framing, since as written that's precisely what it is.

### Finding 4 — MEDIUM: Operations/observability is a silent dimension — no AD, no Consistency Convention, no Deferred entry

The spine decides deployment topology, DB provider selection, and job-queue adapters in detail, but says nothing anywhere about: structured logging, health/readiness probes (notably relevant given AD-6/AD-7's scale-to-zero cold-start concerns — Container Apps and Docker Compose both rely on health checks to know when the single container is actually ready), error/exception tracking, or CI/CD and image-publish-to-ACR. Secrets handling (OIDC client secret, DB connection string, AI API key — env vars vs. Key Vault vs. `.env` for self-host) is likewise unaddressed even though AD-2/AD-6/AD-8 all lean on "one config value at the composition root" without saying how that value's *secret* variants are supplied or protected. This is exactly the category the checklist calls out by name: the operational/environmental envelope is decided for topology but silent for operations. It doesn't need to be heavy, but it needs at least one AD or an explicit Deferred entry — right now a builder has no guidance and no signal that this was considered and postponed versus simply missed.

### Finding 5 — LOW/MEDIUM: Stack table and Deployment diagram disagree on whether "provider" and "environment" are the same axis

AD-2's ADOPTED rationale frames the choice as an environment pairing — "Postgres self-host, Azure SQL Basic DTU cloud" — and the Stack table reinforces this by listing "PostgreSQL (self-host)" and "Azure SQL Database (cloud)" as if each engine belongs to one environment. But the Deployment mermaid diagram shows Azure production choosing between "Azure Database for PostgreSQL Flexible Server, Burstable — or — Azure SQL Basic DTU, config-selected AD-2," i.e. treating provider as a free axis independent of environment, which is what AD-2's actual Rule ("provider is chosen once... never branched on elsewhere") technically permits. The two framings aren't contradictory at the Rule level, but the rationale/Stack-table framing and the Deployment diagram tell a builder two different stories about what's actually supported in Azure. Worth picking one story and making the other consistent with it.

### Finding 6 — LOW: named-version currency spot checks

- **PostgreSQL 17.x** is pinned as "self-host" (and implicitly the Azure Flexible Server option per the deployment diagram). Postgres ships a new major version annually each autumn; as of this document's stated date (2026-08-09), a newer major (18.x) would plausibly already be current for roughly a year. Not necessarily wrong to stay one version back for stability, but the doc doesn't say that's a deliberate choice vs. staleness — worth a one-line "intentionally N-1 for self-host ARM/Docker image maturity" if that's the reasoning.
- **Vite 6.x** — Vite's release cadence is fast (roughly one major per year); by mid-2026 a newer major is plausible. Same "flag, don't necessarily fix" note applies.
- **Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3** — pinning an exact patch version in an architecture spine is unusually precise for this altitude and will be stale the moment a patch ships; the other EF-adjacent row ("Microsoft.EntityFrameworkCore.SqlServer 10.x") correctly stays at major-version granularity. Recommend loosening to `10.x` for consistency and to avoid the spine needing edits for patch bumps.

### Finding 7 — LOW: Deferred "Local OIDC provider for dev/test" is closer to load-bearing than the entry admits

The entry reasons "not blocking, since self-hosters typically already run one." But every route requires authentication except the OIDC callback (Consistency Conventions, NFR), and the Structural Seed's `docker-compose.yml` is described as "local dev: api + postgres" — no OIDC container. Taken together, a from-scratch self-hoster following the compose file literally has no way to reach an authenticated route on day one without separately standing up an OIDC issuer first, which is a bootstrapping gap, not merely a dev-convenience nice-to-have. The judgment call (skip it, target audience already has this) is defensible, but the Deferred entry should say so explicitly rather than characterizing it as non-blocking dev convenience — it's closer to "assumed prerequisite infra," which is worth being honest about for the "clone and go" onboarding path SM-5 depends on.

## Checklist-by-checklist summary

| Checklist item | Verdict |
| --- | --- |
| Fixes real divergence points, misses none | Mostly — misses the session-cookie/scale-to-zero interaction (Finding 1), a real cross-AD divergence risk the rest of the doc's own logic should have surfaced. |
| Every Rule enforceable and actually prevents its Prevents | 16/18 solid. AD-3 (Finding 2) and AD-14 (Finding 3) state intent without a stated enforcement mechanism, and AD-14 is the one AD that explicitly promises more than that. |
| Nothing in Deferred is secretly load-bearing | Mostly clean. Local-OIDC-for-dev (Finding 7) undersells its own bootstrapping relevance but is a defensible, disclosed trade-off, not a hidden one. |
| Named tech verified-current | Mostly plausible; three spot-checks worth a look (Finding 6), none alarming. |
| Every owned dimension decided/deferred/open | Operations/observability/CI-secrets is genuinely silent (Finding 4) — the one clear "whole dimension missing" hit, and the checklist specifically warns to watch for this. |
| Spine is lean | Yes overall for an 18-AD, whole-product spine at 279 lines. A few Prevents clauses (AD-6, AD-7, AD-9) run long with rationale that borders on memlog content, but each is load-bearing enough to justify the length; not worth trimming at the cost of clarity. |
