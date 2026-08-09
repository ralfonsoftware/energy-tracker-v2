---
name: 'Version Currency Review — Energy Tracker v2 Architecture Spine'
type: review
purpose: adversarial-verification
altitude: initiative
scope: 'Web-research spot-check of every named library/framework version in ARCHITECTURE-SPINE.md Stack table'
target: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE.md
status: complete
created: '2026-08-09'
---

# Version Currency Review — Energy Tracker v2 Architecture Spine

**Lens applied:** every committed technology decision must be web-researched or reality-checked, not asserted from training data. This review spot-checks the Stack table entries most likely to be stale as of **2026-08-09**, using live web search, npm registry queries, and Microsoft Learn documentation. Per the requester's scope, `.NET 10`, `EF Core 10`, `Npgsql.EntityFrameworkCore.PostgreSQL`, and `PostgreSQL 17/18`/Azure Flexible Server were already verified earlier in this session and are **not** re-litigated here, beyond a light confirmation of the Npgsql package version.

## Summary Verdict

Of the two claims flagged as highest-risk by the requester, one is **wrong** (Vite) and one is **correct** (React). One additional claim not explicitly flagged — the Azure SQL Basic/DTU "near-zero cost" framing — is **technically true but rhetorically overstated** in a way that could mislead a reader comparing it to the Postgres path. Everything else checked (Tailwind v4, shadcn/ui, i18next, Npgsql 10.0.3) is current and was evidently reality-checked rather than guessed.

---

## Finding 1 — Vite pinned at "6.x" is two majors behind current (HIGH)

**Claim in spine:** `Vite | 6.x`

**Reality (verified via npm registry, `npm view vite dist-tags`, and vite.dev):**
- `npm view vite dist-tags --json` → `{"latest": "8.2.1", "previous": "7.3.6", ...}`
- Vite 7.0 shipped June 24, 2025 (dropped Node 18 support, requires Node 20.19+/22.12+).
- Vite 8.0 shipped March 12, 2026 — a genuinely major architectural shift (Rolldown/Rust-based bundler replacing esbuild+Rollup as the default, with reported 10–30x build-speed gains and a new plugin registry).
- As of the spine's own creation date (2026-08-09), **Vite 8.2.1** is current stable — Vite 6 is two majors and roughly 14 months of releases old.

**Why it matters:** this is a greenfield project with no existing Vite investment to preserve. There is no compatibility reason visible in the spine to pin two majors back (shadcn/ui, Tailwind v4, and React 19 all work fine on Vite 7/8). This reads as an unresearched carry-over from training data rather than a deliberate, justified pin. It should be corrected to "Vite 8.x (current stable; verify against shadcn/ui + Tailwind v4 template compatibility at build time)" or similar — and whoever scaffolds the `web/` project should run `npm create vite@latest` fresh rather than following a Vite-6-era tutorial.

**Severity:** High for a stack-table accuracy standpoint, but low blast-radius on the architecture's actual invariants (AD-1 through AD-18 don't depend on Vite's major version) — it's a stale fact, not a structural flaw.

---

## Finding 2 — React 19.x is correct; no React 20 exists yet (verified, not a discrepancy)

**Claim in spine:** `React | 19.x`

**Reality (verified via npm registry):**
- `react` package `latest` dist-tag = `19.2.8` as of this review.
- No React 20 has shipped or been announced with a release date; React 19 (launched December 2024) remains the current major as of August 2026, with regular 19.x patch/minor releases continuing.

**Verdict:** the spine's claim is accurate and does not need correction. Flagging this explicitly because the requester's brief treated it as equally suspect to Vite — it isn't. This is one data point suggesting the Stack table wasn't uniformly fabricated; some entries (React, Tailwind, Npgsql) look genuinely checked, while others (Vite) look asserted.

---

## Finding 3 — Azure SQL Basic/DTU tier: available for new creation (confirmed), but "near-zero cost" framing is generous (MEDIUM)

**Claim in spine (AD-2):** "Azure SQL Basic tier DTU purchasing model... accepting the added migration-maintenance cost for **near-zero Azure cost** and self-host ARM compatibility."

**Reality (verified via Microsoft Learn):**
- The Basic/Standard/Premium DTU tiers are **still fully available for new database and new logical-server creation** today — not legacy-only, not deprecated for new provisioning. Microsoft Learn's purchasing-models doc labels vCore "(recommended)" over DTU, which is a *preference* signal, not a retirement notice. So the spine's implicit claim that this tier can be selected for a new Azure SQL Database today is **correct**.
- However: the DTU-based purchasing model **only offers the provisioned compute tier — there is no serverless/auto-pause option for DTU tiers** (confirmed via Microsoft Learn "Serverless compute tier for Azure SQL Database": serverless support is vCore-only, General Purpose/Hyperscale). Basic tier runs continuously and bills continuously regardless of load.
- Current published price: **~$4.90–$4.96/month** (5 DTU × $0.0068/hr × ~730 hr), with a 2 GB storage cap (which the spine's own "Deferred" section correctly cites — that part is internally consistent).

**Why it matters:** ~$5/month flat, always-on is cheap, but calling it "near-zero" and pairing it in the same architecture with a Container App that's explicitly designed to **scale to zero** (AD-6, AD-7) is a slight rhetorical mismatch worth tightening: the compute half of the cloud deployment can genuinely go to $0 when idle; the DTU database half structurally cannot — it has no auto-pause mechanism at any DTU tier. (Azure Database for PostgreSQL Flexible Server Burstable, the other config-selected option per AD-2, is similarly always-on and billed continuously — so this isn't a case where Postgres was secretly cheaper; both self-host-adjacent cloud DB options have a non-zero cost floor. The point is narrower: "near-zero" slightly overstates it for Azure SQL specifically, where a true $0-when-idle serverless option exists one tier over — in vCore General Purpose serverless — and was apparently not compared against.)

**Recommendation:** either (a) soften "near-zero Azure cost" to "low, flat Azure cost (~$5/month, DTU Basic)" and note it does not auto-pause, or (b) if $0-at-idle really matters to the cost goals, re-open AD-2 to compare against Azure SQL's vCore General Purpose **serverless** tier (which does auto-pause) before committing to DTU Basic. This is a documentation-precision finding, not a "the tier doesn't exist" finding — DTU Basic is real, current, and creatable today.

**Severity:** Medium — doesn't invalidate the architecture decision, but the stated rationale ("near-zero") isn't fully reality-checked against the one Azure SQL option that actually delivers near-zero (serverless vCore).

---

## Finding 4 — Everything else in the Stack table checks out (confirmed current, LOW/INFO)

Verified via web search and npm/registry checks, no corrections needed:

- **shadcn/ui + Tailwind CSS v4** — confirmed current. Tailwind's latest stable is 4.3.3 (July 2026); no v5 exists. shadcn/ui has supported the Tailwind v4 CSS-variable/OKLCH model since March 2025 and is the documented default path today.
- **Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3** — confirmed listed and current on NuGet (out of the review's required scope per the requester, but cross-checked incidentally while researching EF Core-adjacent packages; consistent with "already verified earlier this session").
- **i18next** — spine doesn't pin a version ("i18next (or equivalent additive-catalog library)"), which is itself the right level of commitment for a library chosen for its pattern (additive resource catalogs) rather than a specific API surface. Current i18next is in the 26.x line; no currency claim to falsify since none was made.
- **Docker/Docker Compose "current stable"** and **Azure Container Apps / Azure Storage Queue** (unversioned platform services) — appropriately left unpinned; nothing to fact-check against a specific number, and no evidence either was asserted incorrectly.

---

## What was NOT independently re-verified (per explicit scope instruction)

`.NET 10 (LTS)`, `ASP.NET Core 10 Minimal APIs`, `EF Core 10`, `Microsoft.EntityFrameworkCore.SqlServer 10.x`, `PostgreSQL 17.x (self-host)`, and Azure Database for PostgreSQL Flexible Server — stated by the task owner to have already been confirmed against Microsoft Learn earlier in this session. No new evidence contradicting them surfaced incidentally during this review.

## Recommended edits to ARCHITECTURE-SPINE.md

1. Stack table: change `Vite | 6.x` → `Vite | 8.x (current stable as of 2026-08; verify against shadcn/ui + Tailwind v4 scaffold compatibility)`.
2. AD-2's `[ADOPTED]` rationale: soften "near-zero Azure cost" to something like "low flat-rate Azure cost (~US$5/month, DTU Basic, always-on — no auto-pause exists at any DTU tier)" so a future reader doesn't assume the cloud DB path scales to $0 the way the Container App compute does.
3. No change needed for React, Tailwind, shadcn/ui, or Npgsql entries — confirmed current as stated.
