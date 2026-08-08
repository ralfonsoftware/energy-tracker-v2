---
title: Product Brief: Energy Tracker v2
status: final
created: 2026-08-08
updated: 2026-08-08
---

# Product Brief: Energy Tracker v2

## Executive Summary

Energy Tracker is a self-hostable web app that tells you whether your household's electricity consumption is on track and whether your tariff is still worth staying on — read in seconds, not analyzed in a spreadsheet. Read the meter, log it from your phone in under a minute, and get back one trustworthy signal instead of a wall of numbers.

At its core is a gap-tolerant rolling baseline built from manual meter readings, sharpened by whatever smart-plug signals are available, culminating in a single glanceable status — ideally a zero-tap notification you can read and dismiss without opening the app. Around that core: a tariff radar that tells you whether switching would actually save money once switching bonuses are normalized out, and lightweight context capture for the appliances no smart plug can see, so a spike in the data can be explained by something you remember doing that week.

It's built for a tenant household with no smart main meter: readings arrive by hand on an irregular 1–2 day cadence, and the product is designed around that constraint rather than assuming it away. The project is developed in the open on a public GitHub repository — usable by, and built to make sense for, other self-hosters in the same situation, not hardcoded to one household.

## The Problem

Manual electricity tracking without a purpose-built tool means a spreadsheet: meter readings typed in by hand, tariff math done by formula, no connection to smart-plug data, no memory of what happened during a given week's spike. It works, technically. But it has two real weaknesses.

First, mobile entry friction: standing next to a meter, typing figures into spreadsheet cells on a phone is slow enough to discourage the very habit the tool depends on — and a habit skipped even a few times breaks the baseline the tool needs to be useful. Second, and more fundamentally: a spreadsheet can't integrate. It can't pull in smart-plug exports, can't correlate a consumption spike with something you remember doing that week, and can't tell you when your energy tariff has quietly become uncompetitive. It just holds numbers you already typed in — it never tells you anything you didn't already know.

The result is the thing every household on a fixed-price contract fears without quite tracking: the annual invoice arrives, and it's a surprise. Not because the information wasn't collectable, but because nothing was watching for the trend and saying so early enough to matter.

## From v1 to v2: What Changed and Why

v1 was built around **Decomposition**: attributing total flat consumption to individual rooms and devices, reconciled against a single manually-read Main Meter, with an explicit Residual for whatever couldn't be attributed. It was the crown-jewel feature — and it didn't hold up. Attribution against one unmetered main meter compounds smart-plug measurement error, unmeasured appliances, and estimation gaps into a Residual precise-looking enough to trust and unreliable enough not to. Worse, much of what it was estimating is inherently variable rather than fixed — a washing machine's 90° cycle draws differently than its 40° one, an LED strip's draw depends on brightness and color — so even a well-measured device rarely reduces to one dependable number. The feature asked for a confidence the underlying data couldn't supply.

v2 is a ground-up rebuild, not a migration: new architecture, new deployment model, no carried-over data — v1 will eventually be retired. But three things about v1 held up and carry forward *conceptually*, rethought rather than copied:

- **The reading habit loop** — meter reading in, cost visible in under a minute — remains the product's foundation.
- **The Room → Power Point → Device structure** survives as an organizing scaffold: it lets a spike or a manual annotation be tagged to a plug or room, and lets measured smart-plug data be viewed on its own terms. It is no longer an attribution system — nothing is required to reconcile to the Main Meter total.
- **Smart-plug imports** remain a real, valuable data source — just consumed differently: as a signal that sharpens the pattern baseline and gives correlation context, not as an input to a forced sum.

The one invariant v2 keeps absolute where v1 blurred it: **the Main Meter reading is the truth.** Everything else — structure, plug data, annotated context — exists to help interpret that number, never to override or be reconciled against it.

## The Solution

Energy Tracker is built around one glanceable status, with three supporting capabilities that feed it or extend it — not four competing features.

**Pattern Detective (the core).** A rolling consumption baseline, computed from manual meter readings and gap-tolerant by design — it doesn't break when a reading is a day late or three days early, because that's the honest cadence of a hand-read meter. Smart-plug imports (Eve Home, Meross) sharpen this baseline as an additional signal wherever they're available, without requiring full coverage to be useful. The output is a single status with three states: within range (nothing to do), below baseline (doing well), or trending past the felt "surprise invoice" threshold (worth a look) — designed to culminate in a zero-tap ambient notification like *"Quiet week, 240kWh under pace, Saturday's gaming session already absorbed."* No chart to read, no numbers to interpret, dismissed in one swipe. A deeper view remains available for anyone who wants it: trend history, and per-plug measured data organized by the Room → Power Point → Device structure it's tagged to — context, not a reconciled breakdown.

**Tariff Savings Radar.** Answers the second question that actually drives a decision: is my current contract still worth staying on? Shows projected annual savings against your current tariff for any comparison rate you enter, with a green/red attractiveness signal shown two ways — with and without a switching bonus factored in, since a bonus-inflated first period misrepresents the comparison if left in. Shares its bonus-decay and baseline math with Pattern Detective's pace threshold, so the two stay consistent rather than diverging.

**Context Capture.** Covers what no smart plug can see — the induction cooktop, the bathroom water heater — with fast, text/tap-first logging of events ("cooked 2h," "gaming session 3h," "away 2 weeks"). AI-assisted wattage plausibility gives an annotated event a rough correlation against the observed pattern (does a claimed 3-hour gaming session roughly match the bump the meter saw?), without claiming precise attribution. This is deliberately scoped to unmeasurable appliances — it's not a general life-logging feature, and it's not a replacement for smart-plug data where that data already exists.

**Extensible Platform.** Three scoped extension points, not a plugin marketplace: custom event/plausibility rules (so voice input, when it arrives, is just another input modality here — not a separate feature), generic data-source column mapping (so a new smart-plug brand's export format doesn't require a code change), and tunable threshold/spike settings. This is what makes the tool "reach" further than a spreadsheet without becoming a platform project in its own right.

Underneath all four: data export/import for disaster-recovery backup, and the household/locale flexibility that comes from being built for other self-hosters, not hardcoded to one flat.

## What Makes This Different

There's no defensible moat here, and this brief won't pretend otherwise — it's an open-source, self-hosted personal project competing with commercial energy apps that have product teams and marketing budgets. What differentiates it is a set of deliberate constraints most of those apps don't share:

**It's built for the no-smart-meter case, not around it.** Most polished consumer energy apps assume a smart meter or utility API integration and treat manual entry as a degraded fallback. Here, irregular manual readings are the primary design constraint, not an edge case — gap-tolerance is load-bearing, not a nice-to-have.

**It says less, on purpose.** Consumer energy apps generally compete on dashboard depth — more charts, more granularity, more to look at. This one's stated goal is a single trustworthy status you can dismiss without reading further. That's a harder discipline to hold onto than it sounds, especially once a drill-down view exists and it's tempting to make it the headline.

**It's honest about what it can't measure precisely.** v1's room/device-level precision claim wasn't earned (see "From v1 to v2" above); v2 doesn't repeat it — measured plug data is shown as measured, unmeasured context is shown as a correlated guess, and nothing is dressed up as more certain than the underlying data supports.

**Your data stays yours.** Self-hosted, exportable, no account with a third party required to see your own consumption history — which matters more for energy data than it sounds, since it's a fairly direct proxy for occupancy patterns.

## Who This Serves

**Primary: the tenant-developer running their own household's energy tracking.** No smart main meter, no landlord-installed monitoring — someone who reads a physical meter by hand every day or two and wants that effort to produce more than a spreadsheet row. They think in kWh and euros, not raw voltage curves; they want to open the app, see one number that tells them if this month is fine, and close it again. When something looks off, they want enough of a trail — a plug reading, a logged event — to have a plausible guess why, without needing to audit it. Success for this person is trust: believing the number enough to act on it, and catching a bad trend or a bad tariff weeks before the invoice would have told them anyway.

**Secondary: other self-hosters in the same situation.** Renters or owners without smart-meter access, comfortable running their own containerized services, who want the same glanceable trust without building it themselves. They're served by the same product without special-casing — configurable household presets and locale instead of hardcoded assumptions, and documentation that treats "I found this on GitHub" as a real onboarding path, not an afterthought to a single-user tool.

**Not built for:** households wanting real-time or near-real-time monitoring (this is a manual-read, file-upload-driven tool by design, not a live dashboard); anyone expecting a hosted service rather than self-deployment; anyone who wants the tool to do their room-by-room energy audit for them — that promise is the one v2 deliberately walked back.

## Success Criteria

**Primary — does it earn trust and act early enough to matter:**
- The spreadsheet is actually retired. Not "used alongside," retired — the strongest signal the tool is trusted enough to be the sole source of truth.
- The reading habit holds. Meter entries keep arriving at the honest 1–2 day cadence the product is designed around, rather than trailing off as logging fatigue sets in.
- At least one real instance where the pace/threshold signal flags a trend early enough to actually change behavior before the invoice arrives — not just log the deviation after the fact.
- A tariff decision (stay or switch) gets made with actual confidence in the bonus-normalized radar figure, at least once.

**Secondary — does it reach beyond one household:**
- At least one other self-hoster runs it against their own flat without needing to fork-and-hardcode their household's numbers first.
- New smart-plug export formats or event rules can be added via the extension points without a code change to the core.

**Counter-metrics — do not optimize for these:**
- Insight/notification volume. A status update that doesn't change what you'd do next is noise, not signal — the zero-tap notification's whole premise is that most weeks say nothing.
- Drill-down depth. The deeper per-plug view existing is fine; it becoming something you *have* to check to trust the headline status would mean the headline status failed at its one job.

## Scope

**Must — the v2 core:**
- Pattern Detective: gap-tolerant rolling baseline over manual Main Meter readings, culminating in a single glanceable status (target: zero-tap ambient notification).
- Real-world-constraints foundation: irregular 1–2 day manual reading cadence and no-smart-meter assumption built into the baseline math from day one, not bolted on later.
- Main Meter reading as the sole source of truth — nothing else is ever reconciled to override it.
- Generic by default: no household-specific values or assumptions hardcoded — config, presets, and locale support built for other self-hosters from the start.

**Should — ships close behind the core:**
- Tariff Savings Radar: bonus-decay-normalized projected savings vs. current contract, green/red signal shown with and without the bonus.
- Context Capture: text/tap-first event logging for unmeasurable appliances, with AI-assisted wattage-plausibility correlation.
- Room → Power Point → Device structure as an organizing scaffold, plus a per-plug measured-data drill-down view — explicitly not an audited attribution/Residual system.
- Data export/import for disaster-recovery backup (DB dump or a documented interchange format).

**Could — genuine value, not blocking:**
- Extensible Platform: the three scoped extension points (custom event/plausibility rules, generic data-source column mapping, tunable thresholds).
- Voice-mode input for event logging (dictation or end-of-day/week recap).

**Won't this round:**
- Market-percentile tariff ranking ("your tariff is top 10%") — needs a third-party tariff data feed; deliberate long-run deferral, not dismissed.
- Real-time or near-real-time monitoring — manual-read and file-upload driven by design.
- Native mobile app.
- Hosted/managed offering — self-deploy only, carried over from v1.
- Cross-user admin views — architecture may allow multiple users, but the product isn't designed around managing them, carried over from v1.

## Vision

Two to three years out, Energy Tracker is the tool a flat tenant without a smart meter reaches for instead of a spreadsheet or a tariff-comparison site — because it's built for their actual situation, not a generic one. Comparison portals optimize for the shiniest headline number; this tool optimizes for what a specific household, with its specific consumption pattern and its specific contract, would actually save or lose. It doesn't try to be the biggest tariff database; it tries to be the most honest one for the person using it.

The core promise stays exactly as narrow as it is today: one trustworthy status, read in seconds, that a tenant can act on without second-guessing it. What grows around that core is depth of trust, not surface area — more smart-plug formats supported through the extension points, a small but real community of self-hosters who've each configured it to their own flat without touching the code, and a track record, household by household, of catching a bad trend or a bad tariff before the invoice made it obvious. If it succeeds, it stays a small, honest tool that a specific kind of household actually relies on — not a platform that outgrew the constraint it was built to respect.
