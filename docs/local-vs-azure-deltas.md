# Local vs. Azure: Known Behavior Deltas

**Audience:** anyone authoring or reviewing a story that touches auth,
ingress, the database provider, the deploy pipeline, or a new secret-shaped
config value. Check this list at story-definition time — every entry here
already caused a real incident or a real failed deployment once, precisely
because it only shows up on Azure and never in local dev/self-host.

**Why this list exists:** the Epic 1 retrospective (2026-08-15) named the
underlying pattern — local dev runs with no reverse proxy, no managed
identity, no ARM validation, and a database provider that isn't
region-restricted, so a whole class of Azure-only failure modes is
structurally invisible until something actually deploys to Azure. "Deploys
cleanly" and "passes review" are not evidence any of the deltas below are
handled correctly. See `_bmad-artifacts/implementation/epic-1-retro-2026-08-15.md`,
action item 2.

Each entry below is citable as `docs/local-vs-azure-deltas.md#D<n>` in a
story's Dev Notes, the same way stories cite an architecture spine `AD-n`.

---

## D1 — Forwarded headers / HTTPS termination

**Local:** the API is reached directly — `dotnet run` on `localhost`, or the
self-host `docker-compose.yml` container with its port published straight to
the host. There is no reverse proxy in front of it, so `Request.Scheme` is
whatever the client actually connected with; the `ForwardedHeadersMiddleware`
codepath is wired up but never exercises the interesting case.

**Azure:** Container Apps terminates TLS at its ingress and forwards plain
HTTP to the container, signaling the original scheme via `X-Forwarded-Proto`.
`ForwardedHeadersOptions.KnownIPNetworks`/`KnownProxies` default to
loopback-only, and Container Apps' internal ingress peer IP matches neither —
so by default the header is silently ignored and `Request.Scheme` stays
`http` even though the real request was `https`.

**Why it bites:** ASP.NET Core's OIDC handler derives `redirect_uri` from
`Request.Scheme`, so a scheme that's wrong only on Azure produces an
`http://.../signin-oidc` callback URL that the identity provider's
`https://`-only whitelist rejects — a login failure with no local repro.

**Fix in place:** `src/EnergyTracker.Api/Program.cs:203-219` explicitly
clears `KnownIPNetworks`/`KnownProxies` before calling
`UseForwardedHeaders`, so Container Apps' ingress is trusted unconditionally.
Safe because Container Apps' external ingress is the only path into the
container, and self-host exposes the port directly with no proxy either.

**Related, same root cause:** the OIDC/session cookies are
`SecurePolicy.Always` (`Program.cs:73`, plus the OIDC handler's own
correlation/nonce cookie defaults) — browsers only store and send those over
a connection they consider genuinely TLS. Chrome exempts `http://localhost`
from that rule; Safari (correctly) doesn't, matching production's real
behavior. `docs/local-development.md`'s "Testing sign-in in Safari" section
covers the local HTTPS dev-cert workaround.

**What this means for a story:** any change to `Program.cs`'s middleware
pipeline order, to `ForwardedHeadersOptions`, or to anything that reads
`Request.Scheme`/`Request.Host` cannot be verified as correct from local
dev alone — the loopback-default codepath will pass locally either way. A
regression test must fake a non-loopback connecting peer (see
`tests/EnergyTracker.Api.Tests/ForwardedHeadersTests.cs`'s
`FakeRemoteIpStartupFilter` technique) rather than relying on `TestServer`'s
default. Any story touching auth/ingress should still carry a real
post-deploy login check against Azure — this fix does not replace that
(see the retro's action item 1, the blocking "verified against the real
deployed environment" AC).

**Incident record:** production outage, 2026-08-13; fixed in Story 1.7.

---

## D2 — Postgres region restriction

**Local / self-host:** always run Postgres directly via the official Docker
image (`docker-compose.yml`) — no Azure region concept exists, so this delta
never surfaces at all locally.

**Azure:** Postgres Flexible Server provisioning is restricted per-region,
and the restriction is independent of the resource group's own region. The
project's resource group lives in `germanywestcentral`, which rejects
Postgres Flexible Server provisioning outright (`az postgres flexible-server
list-skus --location germanywestcentral` returns `"Provisioning is
restricted in this region"`) — confirmed only by a failed live deployment,
not by `bicep build`/lint, which stayed clean throughout.

**Fix in place:** `infra/main.bicepparam` pins `location = 'westeurope'`
explicitly rather than defaulting to `resourceGroup().location` — a
resource's location is independent of its containing resource group's
location in Azure, and `westeurope` is confirmed to support every resource
type this project deploys.

**Currently dormant:** the deployed environment runs Azure SQL (Basic DTU),
not Postgres — `databaseProvider = 'SqlServer'` in `infra/main.bicepparam`
(switched after Story 1.2, for cost reasons unrelated to this restriction).
This delta is inert today but re-activates the moment `databaseProvider`
switches back to `'Postgres'`, or a story stands up a from-scratch/DR
environment in a different resource group/region — check the target
region's SKU availability with `az postgres flexible-server list-skus
--location <region>` before assuming any region works.

**Incident record:** caught during Story 1.2's live verification, before it
became a real outage.

---

## D3 — ACR credential timing (fresh-environment ordering)

**Local / self-host:** `docker-compose.yml` runs the API from a locally
built or pulled image — there is no container registry, no managed
identity, and no credential to validate. This delta cannot occur locally
even in principle.

**Azure:** the Container App pulls its image from Azure Container Registry
using its own system-assigned managed identity, granted `AcrPull` via a
role assignment. That role assignment can only be created *after* the
Container App resource exists (it needs `containerApp.identity.principalId`
as an input) — so on a brand-new deployment there is a window where the
Container App exists but the role assignment doesn't yet. If the Bicep
template's `registries` array eagerly declares the ACR credential
(`identity: 'system'`) at Container App creation time, ARM eagerly validates
that credential before the role assignment exists, gets a `401`, and retries
every 1–3 minutes for ~20 minutes before failing the whole deployment with a
generic `Operation expired` — `az deployment operation` doesn't surface the
real cause; only the Container App's own `ContainerAppSystemLogs_CL` log
table in Log Analytics shows the actual `401`.

**Fix in place:** Story 1.2's first-ever deployment (which only pulls the
public placeholder image, never from ACR) omits the `registries` entry
entirely — the `AcrPull` role assignment is still created so it's ready
ahead of time, but nothing references it yet. Story 1.3, once a real
ACR-hosted image exists to pull, adds the `registries` entry back
(`infra/modules/container-app.bicep`) — by then the Container App and its
role assignment already exist from the prior deploy, so the ordering
problem doesn't recur.

**What this means for a story:** this is specifically a **from-scratch /
disaster-recovery redeploy** risk, not a steady-state one — re-running
`infra-deploy.yml` against the *existing* live environment is fine, because
the identity and role assignment already exist. A story that provisions a
new environment from zero (a second household's self-managed cloud copy, a
DR runbook, a new environment tier) needs to either sequence the deploy the
same way Story 1.2 did (bootstrap without `registries`, then add it back
once the identity is confirmed to have `AcrPull`), or explicitly verify the
role assignment lands before the Container App's first real image pull is
attempted. Flagged as a live risk in
`_bmad-artifacts/implementation/deferred-work.md` ("Deferred from: code
review of story-1-3...").

**Incident record:** caught during Story 1.2's live verification, before it
became a real outage.

---

## D4 — Empty-secret Container Apps validation

**Local / self-host:** unset config is just a blank environment variable —
`.env`/`.env.example` and `docker-compose.yml` accept `OIDC_CLIENT_SECRET=`,
`AI_API_KEY=`, etc. with no complaint. Blank means "not configured yet", and
the app is written to treat it that way (e.g. a blank OIDC `ClientId` is
read as "OIDC not configured", not a startup failure).

**Azure:** Container Apps rejects a `secrets` array entry that has an empty
`value` outright, with `ContainerAppSecretInvalid: value or keyVaultUrl and
identity should be provided` — ACA has no concept of a declared-but-empty
secret. This only surfaces via live template validation; `bicep build`/lint
stays clean, because the emptiness is a runtime parameter value, not a
static template shape.

**Fix in place:** config slots that are genuinely still unset at deploy time
(originally `Ai:ApiKey`/`OIDC:ClientSecret` in Story 1.2) are reserved as
plain **non-secret** environment variables with an empty value instead of
ACA `secrets` entries, until a real value exists. (A later review pass on
the same story changed course again for those two specific keys — seeding a
non-empty placeholder, e.g. `'unset'`, so they could be real `secretRef`-backed
secrets from day one instead. Either approach avoids the empty-value
rejection; picking between them is a judgment call per secret, not a fixed
rule — see `infra/modules/container-app.bicep`'s `aiApiKeySecretValue`/
`oidcClientSecretValue` parameters, defaulted to `'unset'`, for the pattern
currently in use.)

**What this means for a story:** any story that reserves a new
secret-shaped Container App config slot before a real value is available
(the same "reserve the shape, wire the value later" pattern Stories 1.1 and
1.2 used for `OIDC:ClientSecret`/`AI_API_KEY`) must not pass an empty
string into a Bicep `secrets:` array entry. Either give it a non-empty
placeholder default, or keep it a plain env var until a real value exists —
do not assume "empty secret" behaves the same on Azure as it does in
`.env` locally.

**Incident record:** caught during Story 1.2's live verification, before it
became a real outage.

---

## Quick-reference table

| # | Delta | Local/self-host behavior | Azure behavior | Governing story |
|---|---|---|---|---|
| D1 | Forwarded headers / HTTPS | No proxy; `Request.Scheme` is literal | TLS terminated at ingress; header trust must be explicit | 1.7 |
| D2 | Postgres region restriction | No region concept | Provisioning blocked in some regions (e.g. `germanywestcentral`) regardless of resource group's own region | 1.2 |
| D3 | ACR credential timing | No registry/identity involved | Fresh-deploy `AcrPull` role assignment race if `registries` is declared too early | 1.2 (bootstrap), 1.3 (re-added) |
| D4 | Empty-secret ACA validation | Blank env var is fine | ACA rejects a `secrets` entry with an empty value | 1.2 |
