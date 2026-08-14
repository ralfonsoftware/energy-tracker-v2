# Epic 1: Foundation, Deployment & Household Access

Establishes the buildable/deployable skeleton (the architecture's Structural Seed) and lets the first Household come into existence: a fresh deployment routes an authenticated visitor into Household creation, existing members can invite others, and the Room → Power Point → Device tagging scaffold used by later epics is manageable. Every subsequent epic depends on this one; it depends on nothing.

**FRs covered:** FR-26, FR-27, FR-28
**NFRs:** NFR2 (hosting cost-efficiency), NFR3 (auth), NFR4 (tenant isolation), NFR5 (i18n), NFR11 (docs as onboarding path), NFR12 (privacy), NFR14 (cost)
**Architecture:** Structural Seed, AD-1, AD-2, AD-3, AD-10, AD-13, AD-15, AD-17, AD-18, AD-19, Consistency Conventions, Stack
**UX-DRs:** UX-DR1 (token foundation), UX-DR12 (Onboarding/Settings surfaces), UX-DR16 (accessibility baseline)

## Story 1.1: Deployable Application Skeleton (Local Dev & Self-Host)

As a Self-Hoster,
I want to bring up a running Energy Tracker instance from the repo via Docker Compose,
So that I have a working, healthy self-hosted deployment before any feature exists.

**Acceptance Criteria:**

**Given** a fresh clone of the repository
**When** I run `docker-compose up`
**Then** the API and Postgres containers start and the API's `/health` endpoint returns 200 (liveness only, no DB/dependency check)

**Given** the Docker Compose stack
**When** run on modest self-hosted hardware (e.g. a low-power NAS/single-board-computer)
**Then** it runs comfortably, using the same container image and Compose file that also serves as the self-host reference deployment — no separate "cloud edition" exists (NFR2)

**Given** the running instance
**When** inspected for outbound calls
**Then** it makes no telemetry/analytics phone-home by default, and requires no third-party account to view the Household's own data (NFR12)

**Given** the solution structure
**When** inspected
**Then** it matches the Architecture Spine's Structural Seed (`Domain`, `Application`, `Infrastructure`, `Infrastructure.Migrations.Postgres`, `Infrastructure.Migrations.SqlServer`, `Api`, `web/`), and `Domain` has zero external package references beyond the BCL (AD-1)

**Given** `scripts/add-migration.sh <Name>`
**When** run against a model change
**Then** a migration is generated in both provider migrations projects atomically, never just one (AD-2)

**Given** the repository and running containers
**When** inspected for secrets (DB connection string, OIDC client secret, AI API key)
**And** none are committed to source control or baked into the image
**Then** they are all supplied via environment variables or a self-host `.env` file (AD-19)

**Given** the written setup docs alone
**When** a new Self-Hoster follows them with no other support channel
**Then** they can go from clone to a running instance (NFR11)

**Given** the API process
**When** it logs anything
**Then** output is structured and goes to stdout/stderr only, with no environment-specific branching in logging code (AD-19)

## Story 1.2: Azure Infrastructure as Code & Resource Deployment Pipeline

As a platform operator,
I want the Azure resources Energy Tracker runs on defined as Bicep templates and deployed through a dedicated GitHub Actions workflow,
So that the cloud environment is reproducible, version-controlled, and provisioned without manual Azure Portal steps.

**Acceptance Criteria:**

**Given** Bicep templates checked into the repo (e.g. `infra/`)
**When** deployed
**Then** they provision the Container App (Consumption plan, scale-to-zero per AD-6/AD-7), Azure Container Registry, the config-selected database (Azure SQL Basic DTU or Postgres Flexible Server Burstable, AD-2), Azure Storage Queue (AD-6), and a Log Analytics workspace (AD-19) — with no resource requiring manual portal configuration afterward

**Given** a GitHub Actions workflow dedicated to infrastructure deployment
**When** triggered (push to `main` touching `infra/**`, or manual `workflow_dispatch`)
**Then** it authenticates to Azure via OIDC federated-credential login (`azure/login`, `id-token: write` permission) against the pre-configured App Registration client ID whose federated credential trusts the `main`-branch subject — no client secret is stored in GitHub

**Given** the same workflow
**When** run again against an already-provisioned environment
**Then** the deployment is idempotent (`az deployment group create`/`what-if` semantics) — re-running it does not duplicate or break existing resources

**Given** Bicep parameter files
**When** present
**Then** environment-specific values (region, SKU, resource naming) are separated from the templates, with no secret values committed in plaintext

**Given** the deployed Container App
**When** inspected
**Then** its adapter-selection config values (`Database:Provider`, job-queue adapter, AI backend endpoint — AD-2, AD-6, AD-8) are set via Container Apps configuration/secrets, never baked into the image

## Story 1.3: CI Build/Test & CD Deploy Pipeline (App to Azure)

As a developer,
I want a GitHub Actions workflow that builds, tests, and deploys the application container to Azure on merge to main,
So that every change to main reaches the running Azure environment without a manual build/push/deploy step.

**Acceptance Criteria:**

**Given** a push/merge to `main`
**When** the workflow runs
**Then** it restores/builds the .NET solution and the `web/` frontend, runs the full test suite, and fails the workflow (blocking deploy) on any test failure

**Given** a successful build and test pass
**When** the workflow continues
**Then** it builds the multi-stage Docker image (`web/` build → .NET build → runtime, AD-13) and pushes it to the Azure Container Registry provisioned by Story 1.2

**Given** the pushed image
**When** the workflow deploys
**Then** it updates the Azure Container App to a new revision referencing that image tag, authenticating via OIDC federated-credential login scoped to the `main`-branch subject (no stored client secret)

**Given** a completed deployment
**When** the workflow finishes
**Then** it checks the Container App's `/health` endpoint and fails/reports if it doesn't return 200 within a reasonable timeout

**Given** the image tagging strategy
**When** a deploy happens
**Then** the image is tagged traceably to the triggering commit SHA, so a deployed revision can be mapped back to source

## Story 1.4: Pull Request Review Workflow

As a contributor,
I want a GitHub Actions workflow that runs on every pull request,
So that build failures, test failures, or infrastructure drift are caught before merge, without deploying anything.

**Acceptance Criteria:**

**Given** a pull request opened or updated against `main`
**When** the workflow runs
**Then** it restores/builds the .NET solution and `web/` frontend, runs the full test suite, and reports pass/fail as a PR status check

**Given** the same PR
**When** linting is configured for the frontend/backend
**Then** lint failures are also reported as a status check

**Given** the PR workflow needs to validate infrastructure changes
**When** `infra/**` Bicep files changed in the PR
**Then** it authenticates via OIDC federated-credential login scoped to the `pull_request` subject — distinct from the `main`-branch credential used for real deploys — and runs a read-only `what-if`/validate operation only, never an actual deployment

**Given** a PR opened from a fork
**When** the workflow runs
**Then** no Azure login step executes with deploy-capable permissions, since the federated credential's trust policy does not cover fork-originated runs — avoiding a credential-leak/privilege-escalation path

**Given** the workflow's checks
**When** all pass
**Then** the PR is unblocked for merge per branch protection; when any check fails, the PR is blocked

## Story 1.5: Household Provisioning via OIDC

As the first person to access a fresh Energy Tracker deployment,
I want to authenticate via the configured OIDC provider and create my Household,
So that I can start using the product without any manual database step.

**Acceptance Criteria:**

**Given** a fresh deployment with no Household yet
**When** any visitor authenticates via the configured OIDC provider
**Then** they are routed into a Household-creation step, never a broken or empty dashboard

**Given** the Household-creation step
**When** completed
**Then** no second party, invite code, or manual database step was required (FR-26)

**Given** a successful authentication
**When** the session is established
**Then** it is a server-side httpOnly session cookie chained to the OIDC handler — never a token the browser-side app can read or store itself (AD-17)

**Given** the app has been idle and an Azure Container App instance cold-starts from scale-to-zero
**When** a previously authenticated household member returns
**Then** their session is still valid — Data Protection keys are persisted externally (`PersistKeysToDbContext`), not regenerated in memory on cold start (AD-17)

**Given** any route in the product except the OIDC callback
**When** accessed without authentication
**Then** the request is rejected (NFR3)

**Given** the OIDC provider is changed via configuration only
**When** the app restarts
**Then** authentication works against the new provider with no code change (NFR3)

**Given** Household creation
**When** the household member sets its Locale and currency
**Then** both are explicit choices made at creation time (from the launch Locales `de-DE`/`en-US`), never a silently-applied hardcoded default (AD-15, NFR5, NFR6)

**Given** the Household-creation UI
**When** rendered
**Then** it contains no hardcoded locale-specific strings or formats — all copy is sourced from the Locale-driven translation mechanism (AD-18)

## Story 1.6: CI/CD Deploy Idempotency — Container App Image Preservation

As a platform operator,
I want `infra-deploy.yml` to never revert the Container App to the placeholder image,
So that running an infrastructure-only change (e.g. rotating a secret) doesn't silently take production down.

**Acceptance Criteria:**

**Given** the Container App is currently running a real deployed image (not the placeholder)
**When** `infra-deploy.yml` runs (push to `infra/**` or manual `workflow_dispatch`)
**Then** it continues running that same image afterward — `infra-deploy.yml` never overwrites `properties.template.containers[0].image` back to `placeholderImage`

**Given** a brand-new environment with no image ever deployed yet
**When** `infra-deploy.yml` runs for the first time
**Then** it still provisions the Container App successfully using the placeholder image (Story 1.2's original bootstrap behavior is preserved)

**Given** `infra-deploy.yml` and `app-deploy.yml` are both idempotent
**When** either runs multiple times in sequence in any order
**Then** the final state is always: Container App running the most recently app-deployed image, with whatever secrets/config `infra-deploy.yml` most recently applied

## Story 1.7: OIDC Redirect URI Scheme Correctness Behind Container Apps Ingress

As anyone authenticating against a production deployment,
I want the app's OIDC `redirect_uri` to always use `https`, matching the identity provider's whitelisted callback URL,
So that login succeeds instead of failing with a callback URL mismatch.

**Acceptance Criteria:**

**Given** the app runs behind Azure Container Apps' ingress (TLS-terminating, forwards plain HTTP internally)
**When** the OIDC handler builds `redirect_uri` for the authorize request
**Then** it uses `https://`, matching exactly what's registered as the allowed callback URL with the OIDC provider

**Given** `ForwardedHeadersOptions` is configured for `X-Forwarded-Proto`/`X-Forwarded-For`
**When** the middleware evaluates an incoming request from Container Apps' ingress
**Then** it actually trusts and applies that header — not silently ignored due to default `KnownNetworks`/`KnownProxies` restrictions

**Given** a full login round trip against the real configured OIDC tenant in production
**When** a user visits `/login`
**Then** they reach the identity provider's own login page without a "Callback URL mismatch" error

## Story 1.8: Household Member Invitation

As an existing Household member,
I want to invite additional members to my Household,
So that everyone sharing this home in real life can also log readings and see the same Status.

**Acceptance Criteria:**

**Given** an existing Household member
**When** they send an invitation
**Then** the invited person can join the same Household after authenticating via the configured OIDC provider (FR-27)

**Given** a Household with multiple members
**When** any member accesses or modifies the Household's data
**Then** all members have equal, full access — there is no separate admin/owner role (FR-27)

**Given** a newly joined member
**When** they access any Household-scoped data
**Then** they see only this Household's data, enforced at the data-access layer (AD-3, NFR4)

## Story 1.9: Room, Power Point & Device Management

As a Household member,
I want to create, edit, and delete Rooms, Power Points, and Devices,
So that I have the tagging scaffold ready before Smart Plug data or Events need to reference it.

**Acceptance Criteria:**

**Given** the Room/Power Point/Device management surface (reached via Settings)
**When** I create a Room, then a Power Point within it, then a Device on that Power Point
**Then** each is created and scoped to my Household only (FR-28)

**Given** a Household member
**When** they edit or delete a Room, Power Point, or Device
**Then** the change applies only within their own Household's data (AD-3, NFR4)

**Given** a Power Point or Device that already has tagged historical data (from a later epic's imports or Events)
**When** it is deleted
**Then** it is soft-deleted (`ArchivedAt` set, never a hard delete) and the historical data stays valid and reassignable rather than being cascade-deleted (FR-28, AD-10)

**Given** the management list view
**When** displayed
**Then** archived items are excluded from active-selection pickers, while historical references to them still resolve correctly
