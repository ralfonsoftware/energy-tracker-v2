---
title: Energy Tracker v2 — Solution Overview
companion_to: ARCHITECTURE-SPINE.md
created: '2026-08-09'
---

# Energy Tracker v2 — Solution Overview

This is the readable walkthrough of `ARCHITECTURE-SPINE.md` — the *why* behind each decision, for you six months from now, or for whoever else eventually reads this repo. The spine itself stays terse on purpose (it's the build contract); this doc is where the reasoning lives. Where the two disagree, the spine wins — it's the one downstream work is built against.

## The shape of the system, in one paragraph

Energy Tracker v2 is a C#/.NET 10 backend serving a React/shadcn frontend from the same container, built around **Ports & Adapters (Hexagonal architecture)**. The same container image runs two ways: as a Docker Compose stack on a self-hosted NAS, or as an Azure Container App that scales to zero when nobody's using it. Almost every "how does this talk to the outside world" decision — which database, which OIDC provider, which AI backend, how jobs get queued — is a single configuration value, not a code fork.

## Why Ports & Adapters

This wasn't a default pattern reached for out of habit — the PRD asked for the same shape four separate times before the architecture ever named it:

- The OIDC provider has to be swappable via config with no code change.
- The AI Wattage Plausibility backend has to work identically whether it's a local LMStudio instance or a cloud API, and has to degrade to "off" cleanly.
- You asked for the async job mechanism to be one abstraction with pluggable implementations, rather than committing to a single technology.
- The database itself ended up needing to be swappable too (see below).

Four unrelated requirements, one shape. Naming Ports & Adapters as the paradigm means a fifth swappable thing — and there will be one — gets the same treatment automatically instead of inventing a new one-off mechanism. Domain code (the actual business logic — Pattern Detective's baseline math, the Bonus-Decay Normalization shared between Pattern Detective and Tariff Radar) never imports EF Core, an Azure SDK, or a vendor's auth library. It only knows about interfaces (*ports*); Infrastructure provides the concrete implementations (*adapters*).

## The database: why two providers, and what it costs

You're a C#/.NET developer, so the backend language was never really in question. The database was a genuine three-way trade-off, though:

- **A single engine (Postgres or SQL Server) would have been simpler.** Postgres runs everywhere (including ARM NAS hardware) and is nearly free on Azure via Flexible Server's Burstable tier, which can even be stopped when idle. SQL Server is the more familiar .NET-ecosystem default, and Azure SQL's Basic DTU tier is close to free too — but its Linux container image is x86-only, so it flatly can't run on an ARM-based consumer NAS (Synology, QNAP — exactly the kind of box a "NAS in the next 12 months" plan is likely to land on).
- **You chose both, config-selected**, because Azure SQL Basic DTU is cheap enough that there's no reason not to use it in production, while Postgres is what actually works everywhere for self-hosting regardless of what NAS you end up buying.

That choice has a real, ongoing cost, and it's worth being honest about it here rather than just in the spine's terse AD-2: **EF Core's officially supported way to do this is one shared `DbContext`, but two separate migrations projects** — you'll run `dotnet ef migrations add` against both every time the schema changes. The spine's `scripts/add-migration.sh` wraps both invocations into one command specifically so this doesn't quietly become "I'll add the Postgres migration now and remember to do SQL Server later" (you won't remember). The other half of the cost is discipline: no LINQ query or column mapping can lean on a provider-specific trick — no Postgres `jsonb` operators, no SQL Server `rowversion` for concurrency (we use a portable `int Version` column instead, described below). AD-2 in the spine spells out the exact allowed subset.

Is this over-engineering for a personal project? Maybe a little. But you were explicit that the Azure cost and the self-host ARM story both mattered enough to justify it, and the actual mechanism (EF Core's multi-provider migrations support) turned out to be a well-trodden path, not something exotic — see [Microsoft's own docs on it](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/providers), which we checked before committing.

## Async work and the scale-to-zero trap

Azure Container Apps scaling to zero when idle is genuinely good news for a personal project's Azure bill — the PRD explicitly wants a deployment that costs nothing when nobody's using it. But it has a sharp edge that's easy to miss: **anything that relies on an in-process timer or a recurring background schedule silently stops working the moment the app scales down.** No error, no exception — the timer just never fires again until something else wakes the container up.

This mattered in two places:

1. **Smart Plug import** genuinely needs to run something in the background (parsing a file shouldn't block the upload response). The fix here is a small, deliberately boring abstraction: one `IBackgroundJobQueue` port, with an in-process adapter for self-host (zero extra containers — it's just a `Channel<T>` and a hosted service in the same process) and an Azure Storage Queue adapter for the cloud path. The client finds out a job finished by *polling* a status endpoint, not by holding a WebSocket open — a persistent connection would either block scale-to-zero from ever kicking in, or just break outright on a cold start.
2. **The Status card and the Tariff Check Reminder** looked at first like they might need a nightly recompute job. They don't, and this is worth understanding rather than just trusting the spine: re-reading the PRD's actual user journeys, the Status and the Reminder are both things a household sees *when they open the dashboard* — nothing in the PRD requires them to be pushed proactively. So both are computed live, on read, every time. That sidesteps the scale-to-zero timer trap entirely for the MVP. The one feature that genuinely does need a proactive nudge — FR-18's "anything unusual this week?" prompt — is explicitly a later, deferred feature, and when it gets built it'll need a real externally-triggered scheduler (Azure Container Apps' scheduled Jobs feature, or a KEDA cron rule), not a `Timer`.

There's a subtlety hiding inside "compute live" that's worth flagging because it would be an easy mistake to make while implementing FR-6/FR-7: *live* is right for the **current** Status, but the PRD is explicit that editing your Yearly Baseline or trending threshold must never rewrite **past** Status history. If Trend History were also computed live from current settings, that guarantee would break the moment someone tweaks their baseline. So there's a `StatusSnapshot` table that gets written once, at the moment Status is computed (on a new reading or a completed import) — current Status stays live, historical trend reads the frozen snapshots. One service owns writing those snapshots so the Meter-Reading path and the Smart-Plug-import path can't each build their own writer and end up with two different ideas of what a snapshot looks like.

## Frontend hosting: why not split it out

The PRD addendum's original candidate shape put the frontend on Azure Static Web Apps and the backend on its own Container App — a common, reasonable-looking split. This got re-examined explicitly and the single-container answer held up, for a reason that's worth spelling out because it's counter-intuitive: **splitting turned out to cost more, not less.**

Linking a Container App as a Static Web Apps backend ("bring your own API") requires the SWA **Standard** plan — a flat $9/month — because the Free tier only proxies to managed Azure Functions, not to your own container. Meanwhile, Container Apps' Consumption plan gives every subscription 180,000 free vCPU-seconds, 360,000 free GiB-seconds, and 2 million free requests *every month* before anything is billed. A personal household's traffic doesn't come close to that ceiling, so serving the built React bundle from the same container as the API costs nothing extra — you're nowhere near where request volume would start to matter. Net: single container is $0/month; the split is a guaranteed $9/month for capacity you're not using.

The one real technical argument for the split — Static Web Apps' global CDN means the app shell loads instantly even if the backend is cold — doesn't actually buy much here either. The shell loading fast doesn't matter if the dashboard's actual data (the Status card) still has to wait on the same cold-starting Container App either way; the perceived win is mostly cosmetic. And global edge distribution is solving a problem you don't have — this Azure deployment serves one household, not a geographically spread audience.

What actually settles it is the PRD's single-deployment-artifact requirement: Static Web Apps has no self-host equivalent, so splitting would mean self-host and Azure use genuinely different frontend-hosting mechanisms — exactly the divergence the PRD rules out. Single container is the only shape that's simultaneously cheapest, simplest to operate, and compliant with that NFR.

## Keeping the Main Meter honest

This is the one thing worth understanding even if you skip everything else in this doc, because it's the exact failure that killed v1's core feature. v1 tried to reconcile smart-plug measurements and manual annotations against the Main Meter total into a precise "Residual" — and it didn't hold up, because smart-plug measurement error, unmeasured appliances, and estimation gaps compound into a number that *looks* precise and isn't. v2's stated design principle is that the Main Meter reading is the only truth, and everything else — plug data, logged events — is context, never something reconciled against it.

The architecture's job is to make that structurally hard to accidentally undo, not just written down as a principle. AD-14 says no domain code, API response, or frontend view sums smart-plug or event data into a figure presented alongside the Main Meter total — there's no `Residual` type anywhere in the system, and that's checked at the whole-system level (including the frontend), not just the backend, because a chart that puts a "measured total" line on the same axis as the meter-derived pace would recreate the same false-precision problem even without a single line of backend code doing it.

## Data integrity: the boring-but-important stuff

A few mechanisms exist purely to satisfy PRD requirements that are easy to state and easy to get subtly wrong in implementation:

- **Tenant isolation** (every household only ever sees its own data) is enforced once, at the `DbContext` level, via EF Core's global query filters — not re-implemented in every handler. The one sharp edge: EF Core has a few APIs (`Find()`, raw SQL, `.IgnoreQueryFilters()`) that bypass global filters entirely, and background job processing has no HTTP request to resolve "which household" from in the first place. Both are called out explicitly in AD-3 so a future you (or a future contributor) doesn't reach for `IgnoreQueryFilters()` as the easy fix for a job-processing context and accidentally reopen the exact leak the filter exists to close.
- **Concurrent edits never silently lose data** (a real PRD requirement, not a nice-to-have) via a plain `int Version` column and optimistic concurrency — deliberately *not* SQL Server's `rowversion` or Postgres's `xmin`, because those are provider-specific and would fork behavior between the two database engines.
- **Corrections are auditable**: editing a Meter Reading or Tariff entry keeps the old value visible rather than silently overwriting it, through one shared `AuditCorrection` mechanism both features call into. A full data *restore* (FR-23) is explicitly not "an edit" — it's a wholesale replace with no partial-merge mode, so it doesn't go through this path, and that's a deliberate scope boundary rather than a gap.
- **Offline meter reading entry** — the PRD explicitly calls out that meter locations (basements, cupboards) often have weak signal, so reading entry has to work offline and sync later. That needs a client-generated idempotency key on every reading, because a flaky connection retrying a sync must never double-insert a reading, while a genuinely new second reading later the same day still has to go through.

## Staying logged in, safely

"Stays logged in on their phone" sounds like a small UX detail, but it drove a real security-relevant decision: sessions are server-side httpOnly cookies (chained to the OIDC handler), not a token the SPA stores itself — which avoids XSS token theft and matches how most production ASP.NET Core apps do this. The easy-to-miss gotcha, caught during review: ASP.NET Core's Data Protection system (which encrypts that cookie) regenerates its keys in memory by default — and on Azure Container Apps' scale-to-zero, that means a fresh key ring every cold start, silently logging everyone out every time the app spins back up from idle. The fix is persisting Data Protection keys to the same database the app already has (`PersistKeysToDbContext`), which needed no new infrastructure and works identically on both database providers.

## What's deliberately not decided yet

The full list is in the spine's Deferred section, but the shape of it is: things that are either genuinely low-confidence right now (the PRD itself flags FR-20's generic smart-plug column mapping as maybe-not-worth-building), things sequenced for later (FR-18's proactive recap, the broader Extensible Platform features), or things that don't matter yet at your expected scale (splitting the background worker into its own independently-scaled Container App — not worth the complexity until import volume actually justifies it).

## Where to go from here

The spine (`ARCHITECTURE-SPINE.md`) is the actual build contract — that's what a future epic/story breakdown should be checked against, not this doc. If you want, the natural next step is running `bmad-spec` to fold this architecture in as a companion to a formal spec, or going straight to `bmad-create-epics-and-stories` to start breaking the PRD's features into buildable epics against this architecture.
