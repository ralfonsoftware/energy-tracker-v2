# Adversarial Divergence Review — AD-21 (Azure SQL Entra-ID-only auth)

**Scope:** AD-21 in `ARCHITECTURE-SPINE/invariants-rules.md`, cross-checked against AD-2, AD-13,
AD-19, and the *actual current code* it binds to (`infra/main.bicep`,
`infra/modules/database-sqlserver.bicep`, `infra/modules/container-app.bicep`,
`.github/workflows/app-deploy.yml`, `infra/README.md`). AD-21 is prose-only today — none of its
Bicep/CI changes exist yet — which is precisely what makes it possible to find code that will
contradict it the moment two people implement "the rest" of it independently.

**Verdict:** AD-21's *cutover order* (Entra Admin → grant DB users → flip
`azureADOnlyAuthentication`) is internally sound, but the AD only governs the three sentences it
explicitly writes and leaves unowned every piece of *existing, already-merged* infrastructure code
that its own prose casually asserts is already true. Three of those gaps are load-bearing enough to
cause a real lockout, not a documentation nitpick.

---

## Critical

### C1 — The AD asserts a connection-string change that doesn't exist in Bicep, and doesn't assign anyone to make it

AD-21 states as fact: *"The Container App's connection string moves to
`Authentication=Active Directory Managed Identity`."* The actual code today:

- `infra/modules/database-sqlserver.bicep:66` — the module's only output is
  `Server=...;Database=...;User ID=${administratorLogin};Password=${administratorLoginPassword};...`
  — 100% SQL-auth, unconditionally.
- `infra/main.bicep:185` — `databaseConnectionString: databaseProvider == 'Postgres' ?
  databasePostgres.outputs.connectionString : databaseSqlServer.outputs.connectionString` — wires
  that SQL-auth string straight into the Container App secret, no branch for auth mode.
- `infra/modules/container-app.bicep:23-25,131-133,164-166` — takes `databaseConnectionString` as
  one opaque `@secure()` string and drops it into the `db-connection-string` secret / `ConnectionStrings__Default` env var verbatim.

Nothing in AD-21 says *which* file changes to make its own claim true, or in what order relative to
the cutover. Two independently-built stories can each honor AD-21 to the letter and still ship an
inconsistent system:

- **Story A** ("flip the switch"): implements the Entra Admin block + `azureADOnlyAuthentication:
  true` in `database-sqlserver.bicep`, runs the manual grant script, and considers AD-21 satisfied
  — because AD-21's own text is centered on that three-step cutover and never mentions the
  connection-string *output*.
- **Story B** ("wire up Managed Identity", built by someone else reading the same AD): assumes the
  `Authentication=Active Directory Managed Identity` string already flows from
  `database-sqlserver.bicep`'s output (AD-21 says it "moves to" that shape, in past-tense-sounding
  prose) and never touches the Bicep module.

If Story A ships first, the very next `infra-deploy.yml` run redeploys `container-app.bicep` with
the (now `azureADOnlyAuthentication: true`) SQL-auth connection string — a string that can no longer
authenticate at all. The Container App's live secret goes from "the one that works" to "the one that
can never work again," with no code anywhere having ever produced the Entra-mode string.

**Fix direction:** AD-21 (or a companion story) must explicitly own rewriting
`database-sqlserver.bicep`'s `connectionString` output (env-conditional: SQL-auth locally isn't
this module's concern, but Azure's output must become the AD-shape string) and `container-app.bicep`'s
parameter, and must state that this rewrite ships *in the same deploy* as the
`azureADOnlyAuthentication: true` flip — never before (would silently pass a currently-still-valid
SQL-auth string, harmless) and never after (would already be locked out, per C2).

### C2 — Nothing gates the flip on the manual grant script having actually run, and the flip is Bicep-deployed alongside the very thing that must precede it

AD-21 puts the Entra Admin assignment and the `azureADOnlyAuthentication: true` flip in the *same*
Bicep resource type space ("via an inline `administrators` block, or the equivalent
`Microsoft.Sql/servers/azureADOnlyAuthentications` child resource") — both are IaC, both deploy via
`infra-deploy.yml` on push to `main`. The DB-user grant script in between is explicitly kept
*manual* and *out* of any pipeline, by the user's own direction this session.

Nothing stops a future PR from adding the Entra Admin block **and** `azureADOnlyAuthentication: true`
to `database-sqlserver.bicep` in one commit. `infra-deploy.yml` triggers automatically on any
`infra/**` push to `main` and would deploy both in one atomic run — with zero opportunity for a
human to have run `grant-entra-db-users.sql` in between, since there's no pipeline step, precondition
check, or Bicep-level guard verifying the two contained database users already exist before the flag
flips. AD-21's own text ("Reversing (2) and (3) locks every identity out before it has a database
user, with no SQL-auth fallback left to fix it from") describes exactly this failure — but the
constraint that prevents it lives only in prose discipline, not in the mechanism. A reviewer
following AD-21 "to the letter" (it says do 1, 2, 3, in order — it doesn't say split them across two
separate deploys with a manual step gated between) could merge a single PR that satisfies every
sentence of the AD and still self-lock the server.

**This recurs on every future full redeploy, not just the first cutover** (see C3) — once the Entra
Admin/flip resources are permanently encoded in `database-sqlserver.bicep`, *any* from-scratch
redeploy of the SQL server reproduces this exact race, because Bicep will re-declare
`azureADOnlyAuthentication: true` on the new server before the manual script has a chance to run
against it.

**Fix direction:** state explicitly that the Entra Admin resource and the
`azureADOnlyAuthentication` resource must never land in the same Bicep deploy/PR as each other on a
from-scratch server, or add an idempotent precondition (even a manual runbook checklist item
in `infra/README.md`, cross-referenced from AD-21) that blocks step 3's deploy until step 2 is
independently confirmed done.

### C3 — Redeploying the SQL server from scratch reproduces the exact lockout AD-21 exists to prevent, and this isn't flagged anywhere operational

AD-21's own text acknowledges the SQL server might be "redeployed from scratch" — that's the stated
reason the grant script is committed to source control at all. But once Entra Admin +
`azureADOnlyAuthentication: true` are baked into `database-sqlserver.bicep` as the steady-state
config (which they must be, per AD-21's intent), a full redeploy (disaster recovery, resource-group
rebuild, region move, or simply `az sql server delete` + re-running `infra-deploy.yml`) creates a
**brand-new server that is Entra-only from the instant it exists, with zero contained database
users** — not even the Entra Admin has a *contained* user need (the Admin authenticates directly),
but the Container App's and CI's identities have nothing. The app and CI are locked out immediately,
and — per AD-6/AD-7's own established failure pattern in this same spine (scale-to-zero cold start
masking a dead dependency) — this failure is **silent until the next cold start actually needs a new
connection**, which on a scale-to-zero Consumption app could be minutes to hours after the redeploy,
long after whoever ran it has stopped watching.

`infra/README.md` already documents one closely analogous, previously-*actually-happened* drift
risk in this exact file (`### Switching databaseProvider leaves the old DB server running — delete
it manually`) — proving this class of "Bicep incremental-mode gap that bit us once already" is a
real, recurring failure mode in this project, not a hypothetical. AD-21 introduces a second instance
of the same shape (a step that must happen outside Bicep, with nothing enforcing it) but, unlike the
provider-switch issue, it isn't written up anywhere operational: not in `Deferred.md`, not as a
`docs/local-vs-azure-deltas.md` entry (which AD-19 explicitly directs "check it at definition time
for any story touching auth, ingress, the database provider, or the deploy pipeline" — this is
precisely such a story), and not as a runbook step in `infra/README.md` alongside the provider-switch
warning it sits right next to in spirit.

**Fix direction:** add a `docs/local-vs-azure-deltas.md` entry and/or an `infra/README.md` runbook
note: "after any from-scratch SQL server (re)deploy, `grant-entra-db-users.sql` must be run before
the app/CI will be able to connect — the server comes up Entra-only with no contained users."
Cross-reference it from AD-21.

---

## High

### H1 — AD-21 only accounts for two identities; the spine's own Deferred.md already names a plausible third

Deferred.md: *"Worker/API process split... split the worker into its own Container App with a
queue-depth KEDA rule — same image, different entrypoint/command."* AD-21 binds only "the Container
App's system-assigned identity" (singular) and the CI identity. If/when that split ships, the new
worker Container App gets its **own** system-assigned identity, distinct from the API's — and
`grant-entra-db-users.sql` (which AD-21 describes as creating "the two contained database users")
has no third entry. This isn't a hypothetical stress-test; it's a feature the spine has already
pre-approved in writing, sitting one file away from AD-21, with no cross-reference either direction.
A dev implementing the worker split, obeying AD-6/AD-13's letter (same image, new container), would
plausibly reach for the fastest unblock — hardcoding a shared connection string, reusing the API's
identity by disabling system-assigned identity isolation, or (worse) re-enabling SQL auth for just
that one container — precisely the "ad hoc, divergent way of authenticating" AD-21 says it exists to
prevent.

Same risk, less concretely anticipated but still plausible per the task's own prompt: a local
developer needing direct Azure SQL access for debugging, a diagnostics/support script, or a second
CI job (e.g., a future scheduled backup/export job for FR-23). AD-21's Entra Admin note says
promoting the Admin to a group is "the right move if this project ever grows beyond a single
owner/admin — not needed now," which covers *admin-level* access growing, but says nothing about a
future *scoped, non-admin* identity needing the same `db_datareader`/`db_datawriter` treatment the
two current identities get. There's no stated rule that "any new identity needing DB access follows
this identical contained-user pattern, and SQL auth is never reintroduced" — that's implied by the
AD's stated purpose but never written as a durable, general rule, only demonstrated for exactly two
named identities.

**Fix direction:** generalize AD-21's rule ("every identity needing Azure SQL access — present or
future — gets a contained database user via this same script, least-privilege roles; SQL auth is
never reintroduced for any identity") and explicitly cross-reference the Deferred.md worker-split
item.

### H2 — Cold-start / token-acquisition latency at exactly the point AD-19 and AD-7 already worry about is unaddressed

`Authentication=Active Directory Managed Identity` requires a token fetch (IMDS round-trip) on
(re)connection — a cost SQL-auth connections don't pay. AD-19's `/health` endpoint is deliberately
liveness-only ("no DB/dependency check, so a slow Postgres/Azure SQL doesn't fail Container Apps'
probe") specifically to avoid a slow dependency causing restart loops — but that guard only protects
the *health probe*; it does nothing for the first real request after a scale-to-zero cold start,
which now pays both the existing cold-start cost (AD-7's whole reason for banning in-process timers)
**and** a new managed-identity token acquisition cost on top, the first time EF Core opens a pooled
connection. Neither AD-7, AD-19, nor AD-21 acknowledges this added latency layering at the exact
seam all three already treat as fragile. This is a UX-latency risk (first request after idle is
slower), not a lockout, but it's an interaction between three ADs that none of them individually
account for.

---

## Medium

### M1 — Token expiry vs. connection pooling isn't addressed

Entra ID access tokens obtained for `Authentication=Active Directory Managed Identity` connections
are time-limited (~60–90 min). `Microsoft.Data.SqlClient` re-acquires a token per new *physical*
connection, but a low-traffic, scale-to-zero, Basic-tier (5 DTU) app pooling a small number of
long-lived physical connections could, in principle, hold a connection whose underlying token has
expired between requests, depending on server-side idle timeout and pool recycling behavior. AD-21's
"[ADOPTED]" note verifies the *NuGet dependency* resolves correctly (`Azure.Identity` transitively
present) but doesn't verify or even mention runtime token-refresh behavior under this app's specific
low-traffic pooling profile. Likely fine in practice (SqlClient/Azure.Identity handle this
internally, and Basic-tier idle timeouts are generally short enough), but it's an unverified
assumption sitting directly under a "[ADOPTED]" claim that reads as if it settled the whole auth
question.

### M2 — AD-19's "DB connection string" secret classification goes stale for Azure SQL specifically, without being flagged as environment-conditional

AD-19 lists "DB connection string" as one of exactly three canonical secrets, unconditionally. Once
AD-21 lands, the *Azure SQL* connection string in Azure (`Authentication=Active Directory Managed
Identity`, system-assigned, no `User Id`) carries no secret material at all — server/DB names aren't
secret, and a system-assigned identity has no client secret to leak. It remains genuinely secret for:
Postgres in Azure (still password-based per AD-2/`database-postgres.bicep`), and local self-host SQL
Server (`sa`/password, per AD-21's own "local self-host is unaffected" clause). AD-19's blanket
claim is therefore now provider/environment-conditional and neither AD says so.

Functionally this is **not a bug** — continuing to store the non-secret Azure-SQL-Entra string as a
Container App `secretRef` costs nothing, and given AD-2's single shared composition-root code path,
branching the Bicep parameter's secret-vs-plain shape per provider would add real complexity for zero
benefit. But it is a genuine spine inconsistency: a reader relying on AD-19's literal enumeration
would believe Azure SQL's connection string must still be secret-shaped, and might build unnecessary
secret-rotation tooling around a value that no longer needs it, or assume the *absence* of a
password in that string is a bug to be fixed rather than the intended AD-21 end state.

**Fix direction:** a one-line footnote on AD-19 or AD-21 — "post-cutover, the Azure SQL variant of
`db-connection-string` carries no secret material; it stays a Container App secret anyway for
composition-root uniformity across providers, not because it's sensitive."

---

## Low

### L1 — Terminology collision: two unrelated things are both called "OIDC" in the same breath

AD-21 says the CI migration connection string "rides the `az login` OIDC session `app-deploy.yml`
already establishes." `infra/README.md` already has a dedicated section (*"OIDC_CLIENT_SECRET — a
second, unrelated 'OIDC'"*) warning that the workflow's own Azure-login OIDC (federated credential,
no secret) is a completely different mechanism from Story 1.5/AD-17's end-user sign-in OIDC. AD-21
reuses the term "OIDC" for the first meaning without the disambiguating note the README already
found necessary elsewhere — a future reader skimming AD-21 next to AD-17 could plausibly conflate
the two. Purely a readability/documentation risk, no functional impact.

### L2 — AD-2's boundary is clean in letter; the interaction is real but doesn't violate the AD

AD-2 governs the SqlServer adapter's *portable connection-string contract* at the query/schema
level (no provider-specific LINQ/SQL features). AD-21's environment-specific auth mode (Entra in
Azure, SQL auth locally) is a connection-establishment concern, not a query-portability concern —
the connection string was already 100% environment-supplied with zero code branching, exactly as
AD-21 itself states. No tension at the letter of AD-2. The only real interaction worth naming is
H2/M1 above (latency and token-refresh behavior), which are runtime-behavior questions AD-2 was
never scoped to answer — so this is confirmed as a non-issue for AD-2 specifically, with the caveat
that the *real* new surface area (H2/M1) needs a different AD or an operational note to own it.

---

## Cross-reference summary

| Finding | Probe(s) | Severity |
| --- | --- | --- |
| C1 — asserted connection-string change doesn't exist in code, unowned | 1, 4 | Critical |
| C2 — flip not gated on manual grant script having run; same-PR/same-deploy race | 1 | Critical |
| C3 — from-scratch redeploy reproduces the lockout, unflagged operationally | 1, 3 | Critical |
| H1 — third identity (worker split, already in Deferred.md) unaccounted for | 2, 6 | High |
| H2 — cold-start token-acquisition latency layering with AD-7/AD-19 | 6 | High |
| M1 — token expiry vs. connection pooling unverified | 6 | Medium |
| M2 — AD-19 secret classification goes stale for Azure SQL post-cutover | 5 | Medium |
| L1 — "OIDC" terminology collision with Story 1.5/AD-17 | 6 | Low |
| L2 — AD-2 boundary confirmed clean at the letter | 4 | Low (non-issue, informational) |
