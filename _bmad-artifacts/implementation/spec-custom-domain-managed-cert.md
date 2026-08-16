---
title: 'Custom domain / managed-certificate Bicep groundwork'
type: 'feature'
created: '2026-08-16'
status: 'done'
review_loop_iteration: 0
context: ['{project-root}/docs/local-vs-azure-deltas.md']
baseline_commit: 'af1f6ff5b4a074de2b344295a66b4fb070dc7609'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Binding a custom domain to the Container App requires a managed certificate, which requires DNS records that only exist once the app itself exists — and the DNS provider is not Azure DNS, so record creation is a manual, out-of-band step. Nothing in `infra/` currently exposes what's needed to do that, or has a place to bind the domain once DNS is ready.

**Approach:** Add the always-safe verification-ID output now (Phase 1, no DNS dependency). Add the managed-certificate resource and custom-domain binding as Bicep constructs gated behind a `customDomainName` parameter that defaults to `''` (Phase 2, dormant until a human manually verifies DNS and supplies a real value at deploy time — never committed to `main.bicepparam`). Document the DNS-timing failure mode as `docs/local-vs-azure-deltas.md#D5`, matching the existing D1–D4 entries.

## Boundaries & Constraints

**Always:**
- `customDomainName` defaults to `''` at every layer (module params and `main.bicep`) — blank means the feature is fully dormant, matching the existing `oidcAuthority`/`otelAlertNotificationEmail` idiom in `infra/main.bicep`.
- The `managedCertificates` resource is a child of the Container Apps **environment** (`containerAppsEnvironment`), not the Container App — parented directly since `container-apps-environment.bicep` owns that resource symbol.
- `ingress.customDomains` on the Container App uses `bindingType: 'SniEnabled'`.
- New Bicep params use `@description(...)` decorators, matching this codebase's existing style.
- `customDomainName` is **not** added to `infra/main.bicepparam` — it stays unset there; a real value is only ever supplied as a one-off CLI parameter override after DNS is confirmed resolving publicly.

**Ask First:** None — approach fully agreed with the human this session.

**Never:**
- Do not attempt to automate the DNS record creation itself (provider is not Azure DNS — out of scope by definition).
- Do not add a non-empty default anywhere for `customDomainName`.
- Do not touch `infra/main.bicepparam`.

</frozen-after-approval>

## Code Map

- `infra/modules/container-app.bicep` — Container App resource (API version `2026-01-01`); `ingress` block ~L75-80; `output fqdn` at L208. Add `customDomainName`/`managedCertificateId` params, `ingress.customDomains` entry, `output customDomainVerificationId`.
- `infra/modules/container-apps-environment.bicep` — owns `containerAppsEnvironment 'Microsoft.App/managedEnvironments@2026-01-01'`; currently outputs only `id`/`name`. Add `customDomainName` param, conditional `managedCertificates` child resource, `output managedCertificateId`.
- `infra/main.bicep` — orchestrator; existing blank-default pattern at `oidcAuthority` (L60) and `otelAlertNotificationEmail` (L28, gates `monitorAlert` module). Wires `containerAppsEnvironment` (L115) and `containerApp` (L168) modules together; `output containerAppFqdn` at L189.
- `docs/local-vs-azure-deltas.md` — D1–D4 entries + quick-reference table at the end. Add `D5`.

## Tasks & Acceptance

**Execution:**
- [x] `infra/modules/container-app.bicep` -- add `output customDomainVerificationId string = containerApp.properties.customDomainVerificationId` next to `output fqdn` -- exposes the verification value with zero DNS dependency, usable immediately after any deploy
- [x] `infra/modules/container-app.bicep` -- add `param customDomainName string = ''` and `param managedCertificateId string = ''` (with `@description`); add `customDomains: !empty(customDomainName) ? [{ name: customDomainName, certificateId: managedCertificateId, bindingType: 'SniEnabled' }] : []` to the `ingress` block -- binds the domain only when a real value is supplied
- [x] `infra/modules/container-apps-environment.bicep` -- add `param customDomainName string = ''` (with `@description`); add `managedCertificates` resource conditional on `!empty(customDomainName)`, parented to `containerAppsEnvironment`, `properties: { subjectName: customDomainName, domainControlValidation: 'CNAME' }`; add `output managedCertificateId string = !empty(customDomainName) ? managedCert.id : ''` -- creates the cert only once DNS is externally verified and the param is explicitly overridden
- [x] `infra/main.bicep` -- add top-level `param customDomainName string = ''` (with `@description`); thread into `containerAppsEnvironment` module call; thread `customDomainName` and the module's `managedCertificateId` output into `containerApp` module call; add `output customDomainVerificationId string = containerApp.outputs.customDomainVerificationId` alongside `output containerAppFqdn` -- wires Phase 1 and Phase 2 end-to-end without changing default behavior
- [x] `docs/local-vs-azure-deltas.md` -- add `## D5 — Custom domain / managed certificate DNS timing` following the exact D1–D4 structure (Local/self-host, Azure, Why it bites, Fix in place, What this means for a story, Incident record: "N/A — proactive, not yet incident-caused"); add a row to the quick-reference table -- documents that cert creation must not be defaulted live in `main.bicepparam` before DNS is manually confirmed, and that CNAME/CAA quirks aren't caught by `bicep build`/lint

**Acceptance Criteria:**
- Given a deploy with `customDomainName` left at its default `''`, when `main.bicep` is applied, then no `managedCertificates` resource is created and `ingress.customDomains` is empty — behavior is identical to today.
- Given `customDomainName` is supplied as a real value, when `main.bicep` is applied, then a `managedCertificates` resource is created under the environment and the Container App's `ingress.customDomains` references its `certificateId`.
- Given the repo's committed `infra/main.bicepparam`, when inspected, then it contains no `customDomainName` entry.

## Verification

**Commands:**
- `az bicep build --file infra/main.bicep --stdout > /dev/null` -- expected: exits 0, no compile errors
- `grep -n customDomainName infra/main.bicepparam` -- expected: no match (param not committed there)

**Manual checks (if no CLI):**
- Read the diff on `infra/modules/container-app.bicep`, `infra/modules/container-apps-environment.bicep`, `infra/main.bicep` to confirm every new resource/array entry is behind `!empty(customDomainName)` and every new param defaults to `''`.

## Suggested Review Order

**Certificate resource — the core new construct**

- Entry point: the managed certificate is created only when a domain is supplied, and its name is a fixed literal (not the dotted hostname) to avoid an ARM name-validation failure.
  [`container-apps-environment.bicep:51`](../../infra/modules/container-apps-environment.bicep#L51)

- Whitespace-only overrides are trimmed before the emptiness check, so a stray `' '` can't create a garbage-subject certificate.
  [`container-apps-environment.bicep:21`](../../infra/modules/container-apps-environment.bicep#L21)

- Output is `''` unless the cert exists — the only path `container-app.bicep` can get a real `certificateId` from.
  [`container-apps-environment.bicep:63`](../../infra/modules/container-apps-environment.bicep#L63)

**Ingress binding — wires the cert to the Container App**

- `customDomains` is an empty array by default; the trimmed hostname and binding type only apply once a cert ID exists.
  [`container-app.bicep:93`](../../infra/modules/container-app.bicep#L93)

- Same trim guard as the environment module, applied independently to the same source value.
  [`container-app.bicep:69`](../../infra/modules/container-app.bicep#L69)

- `customDomainVerificationId` is exposed unconditionally — the one value needed before any DNS work can start.
  [`container-app.bicep:232`](../../infra/modules/container-app.bicep#L232)

**Orchestration — main.bicep threads the param through**

- Single top-level param drives both modules; `main.bicepparam` deliberately does not set it (see D5).
  [`main.bicep:65`](../../infra/main.bicep#L65)

- `managedCertificateId` flows from the environment module's output into the container-app module's input.
  [`main.bicep:190`](../../infra/main.bicep#L190)

**Documentation — the operational gotchas this groundwork exists to warn about**

- D5 entry: DNS-timing failure mode, the two silent-failure DNS quirks (indirect CNAME, missing CAA), and why none of it is caught by `bicep build`.
  [`local-vs-azure-deltas.md:196`](../../docs/local-vs-azure-deltas.md#L196)

- The CLI override isn't durable — the next unrelated `infra-deploy.yml` run silently reverts the binding.
  [`local-vs-azure-deltas.md:235`](../../docs/local-vs-azure-deltas.md#L235)

- Incremental deploy mode orphans the old certificate resource if the domain is later changed or reverted.
  [`local-vs-azure-deltas.md:243`](../../docs/local-vs-azure-deltas.md#L243)
