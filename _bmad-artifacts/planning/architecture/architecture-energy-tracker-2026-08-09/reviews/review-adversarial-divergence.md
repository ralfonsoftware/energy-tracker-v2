---
name: 'Adversarial Divergence Review — Energy Tracker v2 Architecture Spine'
type: review
reviews: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE.md
lens: 'Construct two units one level down, each compliant with every AD to the letter, that still build incompatibly'
created: '2026-08-09'
---

# Adversarial Divergence Review — Architecture Spine

## Method

For each candidate hole, I named two concrete, independently-plausible implementation units (classes/handlers/features one level below the spine's abstraction), checked each against every AD's literal rule text (not just intent), and confirmed both pass. Then I traced the concrete collision: a shared table with two writers, a shared port with two incompatible payload assumptions, or a rule whose scope-word ("domain or application code", "Meter Reading edits", "creation") is narrower than the surface area a builder would naturally cover.

Findings are ordered by severity: **Critical** (silent cross-tenant data leak or data corruption), **High** (silent loss of a stated guarantee — audit trail, single source of truth, history integrity), **Medium** (inconsistent behavior across features, provider drift), **Low** (cosmetic/formatting inconsistency).

---

## Finding 1 — [Critical] Tenant isolation has no defined mechanism outside an HTTP request, and AD-6 guarantees a caller that has no HTTP request

**ADs in tension:** AD-3 (tenant isolation), AD-6 (async job processing)

**Unit A — any request-handling code**, e.g. `CreateMeterReadingHandler`. It never filters by household itself; it relies entirely on `EnergyTrackerDbContext`'s global query filter, which AD-3 says is sourced from `ICurrentHouseholdAccessor`, itself "resolved from the authenticated principal." This is airtight *inside* a request pipeline — `HttpContext.User` is always present.

**Unit B — `SmartPlugImportJobProcessor`**, the `IBackgroundJobQueue` consumer AD-6 mandates (`InProcessChannelJobQueue` "runs inside the same ASP.NET Core process via a hosted `BackgroundService`"). This processor runs **outside any HTTP request** — there is no authenticated principal in a `BackgroundService`'s DI scope. It still needs to write `SmartPlugReading` rows and read `Household`-scoped config, so it still needs the global query filter to resolve correctly.

**Why each is spine-compliant on its own:** AD-3's rule text only describes how the accessor is populated in the (implicit) request-scoped case; it never says the accessor must *only* ever be populated that way, nor does it say anything about job-processing scope at all. AD-6 says nothing about tenant context propagation into the job payload or the processor's DI scope. Neither AD forbids what Unit B does next.

**The clash:** Faced with `ICurrentHouseholdAccessor` throwing (no principal in scope) inside the job processor, a builder has two "reasonable, letter-compliant" fixes, and the spine disambiguates neither:

1. Register a **job-scoped implementation** of `ICurrentHouseholdAccessor` seeded from `HouseholdId` carried in the job payload — diverges from AD-3's literal "resolved from the authenticated principal" wording but keeps the single-enforcement-point property intact.
2. Reach for `dbContext.Set<SmartPlugReading>().IgnoreQueryFilters().Where(x => x.HouseholdId == job.HouseholdId)` — the "obvious" workaround for a DI resolution failure — which is **exactly** the per-handler household filter AD-3's rule text explicitly forbids ("No repository or handler applies its own household filter — the DbContext is the single enforcement point").

Nothing in the spine says option 2 is off-limits for background code specifically; AD-3's Prevents clause was written with only the request path in mind. A second, independently-built async feature (an AI-correlation batch job, a future FR-18 recap job) hitting the same DI failure could pick option 2 while Pattern Detective's job processor picked option 1 — and now the codebase has one path with real DbContext-level enforcement and one with hand-rolled filtering, silently reopening the exact cross-household leak AD-3 exists to close, with no test surface that would catch it (both paths "work" correctly for a single-household dev/test setup).

**Suggested fix:** A new AD (or a tightened AD-3) pinning: "`ICurrentHouseholdAccessor` has exactly two valid sources: the authenticated principal (request scope) and an explicit `HouseholdId` carried on the job payload and set at `Enqueue` time (background scope). `IgnoreQueryFilters` is banned outside integration-test setup, full stop, in both scopes."

---

## Finding 2 — StatusSnapshot's writer is not pinned to a single call site, and the Capability Map actively misdirects the team that owns one of its two triggers

**ADs in tension:** AD-7 (Status computed live, history persisted), Capability → Architecture Map

**Unit A — `Pattern Detective`'s `RecordMeterReadingHandler`.** Its row in the Capability Map lists AD-7 as a governing AD. Its author reads AD-7 in full, sees "every time Status is (re)computed — on a new Meter Reading **or a completed Smart Plug import**... the result is also written to an immutable `StatusSnapshot` row," and — reasonably, since they own the only capability row that cites AD-7 — builds the recompute-and-snapshot logic as a private step inside their own handler, with a `StatusSnapshot` schema (`Status`, `HeadlineSentence`, `ComputedAtUtc`, plus whatever fields their own read path needs) shaped for their own call site.

**Unit B — `SmartPlugImportJobProcessor`** (FR-4, `IBackgroundJobQueue` consumer). Its Capability Map row reads: `Smart Plug Import (FR-4, FR-24) | Infrastructure.Adapters parsers, IBackgroundJobQueue | AD-6, AD-9` — **AD-7 is not listed.** A builder using the map as the authoritative "what governs me" lookup (which is exactly what the map is for) has no textual pointer from their capability row to AD-7's obligation, and never opens it. They ship import-completion without ever writing a `StatusSnapshot` row.

**Why each is spine-compliant on its own:** Unit A does precisely what AD-7 says for the trigger it knows about. Unit B never breaks an AD it was told applies to it — the Capability Map, not AD-7's text, is what it consulted, and the map is silent.

**The clash:** This is a hole in both directions, and either resolution is bad:

- **As shipped, most likely:** Smart Plug import completion never triggers a `StatusSnapshot` write (the map-only reading), so FR-8's Trend History — which AD-7 says "reads persisted `StatusSnapshot` rows, never a live recomputation" — silently has **no history entries covering the periods around Smart Plug imports**, even though AD-7's own text names that as one of exactly two triggers. This is invisible until someone diffs FR-8's output against AD-7's prose.
- **If a later engineer reads AD-7 directly** (as intended) and retrofits the missing trigger into `SmartPlugImportJobProcessor` without coordinating with Pattern Detective's existing writer, you now have two independently-built call sites writing to the same table, with no shared "compute Status" use case pinned by any AD — AD-7 only pins the *outcome*, not that there is exactly one code path that produces it. If the two call sites assemble slightly different inputs (e.g. one recomputes off the very latest `MeterReading` + live config, the other off cached config captured at job-enqueue time, which could be stale by the time the job drains from a scaled-to-zero cold start), two `StatusSnapshot` rows for overlapping moments can disagree, and nothing (no AD-12-style "at most one" constraint, no dedup key) prevents both from landing.

**Suggested fix:** (a) Fix the Capability Map — add AD-7 to the Smart Plug Import row. (b) Tighten AD-7 (or add an AD) naming a single application-layer use case, e.g. `RecomputeAndSnapshotStatusUseCase`, as the only code path permitted to construct a `StatusSnapshot`, invoked from both trigger sites, so there is structurally one writer rather than a rule about outcomes that two writers could each separately satisfy.

---

## Finding 3 — AuditCorrection can be silently bypassed by Data Export/Import, again via a Capability Map omission

**ADs in tension:** AD-11 (shared audit-correction mechanism), Capability → Architecture Map

**Unit A — Meter Reading edit endpoint.** Its capability row (Pattern Detective) lists AD-11. Every update goes through `IAuditCorrectionRecorder`, per the rule.

**Unit B — FR-23 historical data re-import** (Data Export/Import). Its Capability Map row reads: `Data Export/Import (FR-22–FR-23) | Application use case over all repositories | AD-2, AD-3` — **no AD-11.** A household re-imports a corrected export (e.g. fixing bad historical readings by re-uploading), and the import use case does a straightforward bulk `UPDATE` over the repository — which the map tells its builder is governed only by dual-provider portability (AD-2) and tenant scoping (AD-3), nothing about audit trail.

**Why each is spine-compliant on its own:** AD-11's binds clause literally says "Meter Reading edits, Tariff edits" — a bulk import overwrite is arguably not "an edit" in the UI-interaction sense the AD's examples imply, and the Capability Map, the document meant to route obligations to builders, confirms that reading by omission.

**The clash:** The same entity (`MeterReading`) now has two update paths with divergent guarantees: one traceable (UI edit → `AuditCorrection` row), one silent (import overwrite → no audit trail at all). This directly undermines the "Cross-Cutting NFR: audit trail on corrections" AD-11 exists to satisfy universally, and it's the kind of gap that only surfaces when a household disputes a historical value and support goes looking for a correction record that was never written.

**Suggested fix:** Add AD-11 to the Data Export/Import capability row, and tighten AD-11's binds clause from "Meter Reading edits, Tariff edits" to "any write to an already-persisted Meter Reading or Tariff value, regardless of entry point (UI edit, bulk import, regression resolution)."

---

## Finding 4 — `IBackgroundJobQueue` pins two method names but no payload/dispatch contract; two job producers can assume incompatible shapes over the same port

**ADs in tension:** AD-6

**Unit A — `SmartPlugImportJobProcessor`** (first feature to use the queue, FR-4). Its author designs `IBackgroundJobQueue` as `Enqueue(JobEnvelope envelope)` / `Dequeue(): JobEnvelope`, where `JobEnvelope = { JobType: string, PayloadJson: string }`, and a single hosted `BackgroundService` loop that deserializes `PayloadJson` based on `JobType` via a switch/dispatch table. `InProcessChannelJobQueue` is implemented as `Channel<JobEnvelope>`.

**Unit B — a later Tier-3 async feature** (an AI-correlation batch pass for FR-17, or FR-18's recap once a real scheduler exists per AD-7's Deferred note). Built by someone who read only AD-6's prose — "one `IBackgroundJobQueue` port (`Enqueue`, `Dequeue`/subscribe)" — with no payload shape specified anywhere in the spine, and reasonably implements the port the other common way: `Enqueue(Func<IServiceProvider, CancellationToken, Task> work)`, a generic delegate/closure executor, because that's the simplest thing satisfying "one port, `Enqueue`/`Dequeue`" for a single one-off job type that doesn't need a discriminator.

**Why each is spine-compliant on its own:** AD-6's rule text names the port's two method names and the two adapters (`InProcessChannelJobQueue`, `AzureStorageQueueJobQueue`) and the selection mechanism — it never states the generic parameter, envelope shape, or a job-type discriminator convention. Both units satisfy every sentence of AD-6.

**The clash:** `IBackgroundJobQueue` cannot simultaneously be typed as `Channel<JobEnvelope>` and `Channel<Func<...>>` — whichever ships first "wins" the shared interface, and the second feature's code either doesn't compile against it or is forced into a second, ad hoc queue mechanism that quietly defeats AD-6's entire premise (one queue, one config-selected adapter pair, scale-to-zero-aware polling for all async work). Worse, if Unit B instead conforms by wrapping its delegate as `PayloadJson`-serialized state to fit Unit A's envelope, `AzureStorageQueueJobQueue` (the cloud adapter, which must serialize the message to Azure Storage Queue's wire format) cannot serialize a `Func` closure at all — a divergence that only surfaces in the cloud environment, never in self-host/dev where `InProcessChannelJobQueue` happily passes an in-memory delegate by reference. This is precisely a "looks fine locally, breaks in the other of the two required-parity deployment environments" bug, which AD-2/AD-6's whole design intent is to prevent for persistence and queueing respectively.

**Suggested fix:** A tightened AD-6 (or a companion AD) pinning: the port is `IBackgroundJobQueue` over a serializable `JobEnvelope { JobType: string, PayloadJson: string, HouseholdId: Guid }` (the `HouseholdId` field also closes Finding 1's tenant-context gap), one dispatch registry mapping `JobType` to a processor, and an explicit statement that closures/delegates are never a valid payload precisely because they must survive the cloud adapter's serialization boundary.

---

## Finding 5 — AD-16's idempotency guarantee is scoped to "creation" in its rule text but "writes" generically in the Stack table; offline-queued *edits* have no retry-safety story anywhere

**ADs in tension:** AD-16 (offline-safe idempotent writes), AD-11 (audit correction), Stack table

**Unit A — the online/offline create path**, built exactly to AD-16's letter: "Meter Reading **creation** carries a client-generated idempotency key... set at the moment of entry, before any network attempt... The API upserts by idempotency key." Scoped strictly to creates; edits are a separate, online-only PUT with no key.

**Unit B — an offline-capable edit feature**, built by someone reading the Stack table's entry instead of AD-16's rule text: "Frontend offline queue — IndexedDB-backed local write queue... for Meter Reading **writes** (AD-16)" (generic "writes," not "creates"). This is a reasonable UX ask too — a household member offline shouldn't be blocked from fixing a typo they just entered. Builder B extends the same IndexedDB queue and the same "generate a key before any network attempt, flush on reconnect" discipline to edit operations, routing the eventual flush through the AD-11 correction path (`IAuditCorrectionRecorder`).

**Why each is spine-compliant on its own:** Unit A matches AD-16's Rule text exactly. Unit B matches the Stack table's literal wording ("Meter Reading writes") and reuses AD-16's own idempotency discipline in good faith, extending it in the direction AD-16's Prevents clause explicitly cares about (retry-safety of offline writes).

**The clash:** AD-16's actual mechanism — "the API upserts by idempotency key" — is a create-vs-already-exists check against the `MeterReading` table. It has no analog for an edit: there is no "already-applied correction" table keyed by client-generated GUID for `AuditCorrection` to upsert against (AD-11 defines no idempotency key at all). So Builder B's flush-retry-on-lost-ack for an edit either (a) silently double-applies the correction if `IAuditCorrectionRecorder` is called twice with the same old/new values — producing two `AuditCorrection` rows for one logical correction, corrupting the audit trail AD-11 exists to keep authoritative — or (b) if the field values legitimately changed twice offline before reconnect (two genuine edits queued before any sync), an ack-loss-triggered retry of the *first* edit after the *second* has already landed can incorrectly reapply a stale old→new transition out of order, since nothing sequences or dedupes edit-flushes the way AD-16 sequences/dedupes create-flushes. Two features that both took a spine document's wording literally (one from the AD, one from the Stack table row referencing that same AD) built incompatible assumptions about what the offline queue covers.

**Suggested fix:** Either (a) narrow the Stack table row to say "Meter Reading **creates**" to match AD-16 exactly and explicitly state edits are online-only (simplest), or (b) extend AD-16 (or add an AD) giving `AuditCorrection` its own idempotency key with the same "generate before any network attempt, upsert-not-insert on retry" discipline, explicitly binding it to AD-11.

---

## Finding 6 — AD-2's "avoid provider-specific features" names no allowed portable pattern for semi-structured storage; two features can make opposite, individually-defensible portability judgment calls

**ADs in tension:** AD-2

**Unit A — FR-20's (deferred, but eventually built) generic Smart-Plug column-mapping config.** Needs to store an arbitrary, evolvable "which spreadsheet column means what" mapping per household/vendor. Builder A reasons: "AD-2 forbids provider-specific *features* — `jsonb` operators, `rowversion`/`xmin`. A plain `nvarchar(max)`/`text` column holding an app-serialized JSON string, read back with `System.Text.Json` and never queried at the SQL level, isn't a provider feature at all — it's just a string column." Fully portable; both migration projects generate the identical column type.

**Unit B — an AI-correlation raw-result store** (FR-17 adjacent), needing to persist structured, occasionally-queried correlation data. Builder B reasons: "Storing structured data as an opaque string and never being able to query into it is exactly the kind of feature-poverty AD-2 doesn't actually require — it only bans features that would *fork behavior between providers*. Postgres `jsonb` with a GIN index, and SQL Server's native `JSON` type, both let me query into the structure; I'll map the column with a `HasColumnType` override per provider so each gets its idiomatic native type." This is also a defensible reading: AD-2's Prevents clause targets "the two providers drifting into different schemas, or a feature landing that only works against one of them" — Builder B's column works against both, functionally.

**Why each is spine-compliant on its own:** AD-2's rule bans specific named features (jsonb *operators*, rowversion/xmin) and says "no LINQ query, raw SQL fragment, or column mapping may rely on a provider-specific feature" — but gives no worked example of what a compliant "structured storage" column looks like, so "provider-specific" is left to each builder's own judgment call, and the prompt's own framing anticipates this exact ambiguity.

**The clash:** Builder B's per-provider `HasColumnType` override is, structurally, precisely a provider-specific column mapping — SQL Server's `JSON` type and Postgres `jsonb` are different types with different query syntax (SQL Server's `JSON_VALUE`/`OPENJSON` vs Postgres's `->`/`->>`/`@>` operators), so any query written against one will not run against the other. This is the identical failure mode AD-2 names for `rowversion`/`xmin`, just recreated one layer up in a "column mapping," which AD-2's own sentence explicitly also covers ("no... column mapping may rely on a provider-specific feature") — yet Builder B could ship a working, tested feature against their default local dev provider (Postgres) and have it silently break the first time someone runs the SQL Server adapter, exactly the "a feature landing that only works against one of them" scenario AD-2's Prevents clause names. Meanwhile Builder A's fully-portable opaque-string pattern for a structurally similar problem (semi-structured, evolvable config) creates an inconsistent house style: one JSON-shaped column is queryable and native per-provider, a sibling one is an opaque blob — and nothing in the spine says which is the sanctioned default.

**Suggested fix:** Tighten AD-2 with a named, worked-out allowed pattern: "Semi-structured data is stored as a single portable column type (e.g. `nvarchar(max)`/`text` mapped via `HasConversion` to/from JSON) and filtered/queried only in application code, never via provider-native JSON operators or types, until/unless a future AD explicitly adopts one queryable JSON strategy validated against both providers."

---

## Finding 7 — AD-14's prohibition is scoped to "domain or application code"; a presentation-layer chart can reconstruct exactly the Residual comparison the AD exists to forbid

**ADs in tension:** AD-14 (Main Meter sole authority), FR-8 Trend History

**Unit A — the FR-8 Trend History API endpoint**, built to the letter: it returns persisted `StatusSnapshot` rows (per AD-7) plus, separately, raw `SmartPlugReading` context rows for the same window — two arrays, no cross-computed field, matching AD-14's "may only ever be surfaced as context/signal" and its "no `Residual` type, field, or view anywhere in the system."

**Unit B — the Trend History chart component** (frontend, `web/`, outside `Domain`/`Application`/`Infrastructure` entirely) consuming Unit A's two arrays. To make the "context, not reconciliation" framing legible, its author renders Main Meter pace as a line and per-device `SmartPlugReading`-derived kWh as a **stacked area beneath the same axis**, and — to answer the UX-obvious question "how much of the meter's total does this account for" — computes, purely inside a React `useMemo`, `gapToMeter = meterPaceForWindow - sum(smartPlugKwhForWindow)` for the tooltip. This value is never persisted, never in `Domain` or `Application`.

**Why each is spine-compliant on its own:** AD-14's rule text is explicit and narrow: "No **domain or application code** sums `SmartPlugReading` or `Event` data into a figure that is compared against, reconciled with, or presented alongside the Main Meter total." A `useMemo` in a React component is neither domain nor application code, and "there is no `Residual` type, field, or view anywhere in the system" is arguably about persisted schema, not an ephemeral chart-tooltip number that exists only in browser memory for the duration of a hover.

**The clash:** Functionally, `gapToMeter` **is** the Residual figure the brief names as its single non-negotiable thing-to-avoid ("a Residual/attribution figure that looks precise enough to trust and isn't") — computed, labeled, and shown to the user on the product's own trend chart, just relocated one layer outward from where AD-14's rule text happens to police. The household member sees the same "here's what's unaccounted for" number v1 failed on; whether the code computing it lives in `Application` or `web/` is invisible to them and irrelevant to the harm the AD exists to prevent. Two teams — one building the API (strictly domain/application-clean, per Unit A) and one building the chart (frontend, per Unit B) — can each pass a code review checked literally against AD-14's binds/rule text and still jointly ship the exact outcome AD-14 was written to prevent.

**Suggested fix:** Reword AD-14's rule to bind the *presentation* layer too: "No code anywhere in the system, including frontend view/chart logic, may compute or display a figure that sums, subtracts, or ratios `SmartPlugReading`/`Event` data against the Main Meter total." Consider also naming the allowed chart pattern explicitly (e.g. "SmartPlugReading may share a time axis but never a value axis/stacking with Main Meter pace") so "context, not reconciliation" has a concrete, checkable visual contract, not just a data-modeling one.

---

## Additional / lower-severity gaps

**8 — [Low-Medium] AD-12 regression resolution vs AD-11 audit correction ownership.** AD-12 governs *classifying* a `MeterRegressionPrompt` (reset vs. rollover) but never says whether resolving one — which can materially reinterpret or correct a reading's value — counts as a "Meter Reading edit" for AD-11's purposes. One builder could treat resolution as pure metadata (no `AuditCorrection` row); another could treat it as an implicit correction requiring one. Both are literally consistent with AD-11's binds clause ("Meter Reading edits, Tariff edits") since "edit" is undefined. Fix: state explicitly in AD-11 or AD-12 whether resolving a regression prompt routes through `IAuditCorrectionRecorder`.

**9 — [Low] AD-15 restricts hardcoding "a household-specific value," not a suggestion catalog.** FR-2's Yearly Baseline presets (AD-15's own named example) could be shipped as a hardcoded frontend literal array (defensible: it's a suggestion, not an applied default) while a sibling feature's analogous "suggested starting value" concept (e.g. a future trending-threshold suggestion) is built as a household-editable config table in the name of AD-15's stated genericity intent. Both readings are letter-compliant; the two sibling "preset" concepts end up architecturally inconsistent. Fix: AD-15 should state explicitly whether suggestion catalogs themselves must be config-driven, or are exempt.

**10 — [Low] Currency formatting has two independent inputs (`Household.Locale`, `Tariff.CurrencyCode`) that nothing states must be combined rather than one deriving the other.** A builder could reasonably (but incorrectly, for a German-locale household holding a USD-denominated tariff) derive the display currency symbol from `Household.Locale` via `Intl.NumberFormat` defaults instead of joining in `Tariff.CurrencyCode` explicitly. Fix: state in AD-18 or the Consistency Conventions table that currency formatting always takes `Tariff.CurrencyCode` as the currency and `Household.Locale` only as the number/date shape — two independent axes, never one derived from the other.

---

## Summary Table

| # | Finding | ADs | Severity |
| --- | --- | --- | --- |
| 1 | Tenant isolation undefined across the request/job boundary | AD-3, AD-6 | Critical |
| 2 | StatusSnapshot writer not pinned; Capability Map omits AD-7 for Smart Plug Import | AD-7, Map | High |
| 3 | AuditCorrection bypassable by import; Capability Map omits AD-11 for Data Export/Import | AD-11, Map | High |
| 4 | IBackgroundJobQueue payload/dispatch shape unpinned | AD-6 | High |
| 5 | Offline idempotency scoped to "creation," Stack table says "writes"; edits unguarded | AD-16, AD-11 | Medium-High |
| 6 | No sanctioned portable pattern for semi-structured storage | AD-2 | Medium |
| 7 | AD-14's prohibition doesn't reach presentation-layer reconciliation | AD-14 | Medium |
| 8 | Regression resolution's audit-trail status undefined | AD-11, AD-12 | Low-Medium |
| 9 | Suggestion catalogs exempt from AD-15's genericity rule by letter | AD-15 | Low |
| 10 | Currency-vs-Locale formatting axis unpinned | AD-18 | Low |
