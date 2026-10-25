# Self-Hosting Energy Tracker

This guide takes you from a fresh clone of this repository to a running Energy
Tracker instance on your own hardware — a NAS, a Raspberry Pi / single-board
computer, or any machine with Docker. No cloud account, no third-party
service, and no support channel is required to complete these steps.

## Prerequisites

- [Docker](https://docs.docker.com/get-docker/) and Docker Compose (the
  `docker compose` CLI plugin, included with modern Docker Desktop and Docker
  Engine installs).
- Git, to clone the repository.
- An OIDC provider app registration you control — Entra ID, Auth0,
  Authentik, Keycloak, or any other standards-compliant OIDC provider. There
  is no bundled sign-in option: authenticating via your configured provider
  is how the very first Household gets created (no invite code, no manual
  database step). See step 2 below for the values you'll need from it.

No other software is required — the application itself (API + database) runs
entirely inside the containers started below.

## 1. Clone the repository

```bash
git clone <this-repository-url>
cd energy-tracker-v2
```

## 2. Configure your environment

Copy the example environment file and fill in your own values:

```bash
cp .env.example .env
```

Open `.env` in a text editor and set, at minimum:

- `POSTGRES_PASSWORD` — a password for the bundled Postgres database. Pick
  anything reasonably strong; this database is only reachable from inside
  the Docker network, not the internet.
- `OIDC_AUTHORITY` — your OIDC provider's Authority/issuer URL (e.g. an
  Entra ID tenant endpoint, an Auth0 domain, or your Authentik/Keycloak
  realm URL).
- `OIDC_CLIENT_ID` — the Client ID from your provider's app registration.
- `OIDC_CLIENT_SECRET` — the Client Secret from that same app registration.
  Keep this one actually secret; it's the only OIDC value that is.

Register a new "web application" / "confidential client" app in your OIDC
provider first, with a redirect URI of `https://<your-instance-host>/signin-oidc`
(or `http://localhost:8080/signin-oidc` while testing locally), then copy
its Authority, Client ID, and Client Secret into `.env`. Without these three
values set, the app still starts and `/health` still works, but sign-in is
unavailable — nobody can create the first Household — and `/login` answers
`503` with a short "Sign-in is not configured" message that names the three
settings.

(`/login` also accepts an optional `returnUrl` query parameter, used
internally by the Household member-invitation flow to return an invited
person to their `/join/{token}` link after signing in — no extra
configuration needed, informational only.)

#### Optional: an AI backend for wattage plausibility

The "AI wattage plausibility" Household toggle in Settings needs an
OpenAI-compatible server. It is off unless you set `AI_ENDPOINT` in `.env`;
leave all four lines blank and the toggle has no effect (Settings says no AI
backend is configured, and the startup log says so).

- `AI_ENDPOINT` — the **server root** of the API, for example
  `http://localhost:1234` for LM Studio or `https://api.openai.com` for OpenAI.
  The app appends `v1/chat/completions` itself, so do not include a path.
  Blank turns the feature off. A value that is set but is not an absolute
  `http`/`https` URL stops the app at startup with a message naming
  `Ai:Endpoint`. From inside Docker Compose the LM Studio on your host is
  `http://host.docker.internal:1234`, not `localhost`.
- `AI_API_KEY` — sent as `Authorization: Bearer <key>` when set. LM Studio
  needs none; OpenAI does.
- `AI_MODEL` — the model id to request. Blank means `default`, which LM Studio
  ignores; OpenAI and other cloud providers need a real id.
- `AI_BACKEND_LABEL` — an optional name shown in Settings, such as
  `Local (LM Studio)`.

`docker-compose.yml` passes these to the app as `Ai__Endpoint`, `Ai__ApiKey`,
`Ai__Model` and `Ai__BackendLabel` (the `Ai` configuration section). That
section is the only spelling: the older, never-documented `AiPlausibility__*`
names are no longer read, so rename them if you set them by hand.

The container runs as a non-root user and needs no volume permissions: it
writes nothing outside `/tmp`.

`.env` is listed in `.gitignore` and is never committed. Nothing in this
repository or in the built container image contains real secrets; every
credential is supplied at runtime through `.env`.

### Data Protection keys (required decision since this release)

Energy Tracker signs and encrypts your session cookie with a set of keys
that it stores in its own database (so you stay signed in across restarts).
If those keys sit in the database in the clear, anyone who can read the
database or a backup can forge a login for any member. So **outside
Development the app refuses to start unless you choose one of two options**
in `.env`:

**Option 1 — a certificate (recommended).** The keys are then stored
encrypted. Create a throw-away self-signed certificate once (the key pair is
only used to encrypt the keys; it does not need to be trusted by anything):

```bash
openssl req -x509 -newkey rsa:2048 -nodes -keyout key.pem -out cert.pem \
  -days 3650 -subj "/CN=energy-tracker-data-protection"
openssl pkcs12 -export -inkey key.pem -in cert.pem -out dp.pfx -passout pass:<choose-a-password>
base64 -i dp.pfx | tr -d '\n'      # macOS;  on Linux: base64 -w0 dp.pfx
rm key.pem cert.pem                  # keep dp.pfx somewhere safe
```

Put the printed base64 text and your password into `.env`:

```
DATA_PROTECTION_CERTIFICATE_BASE64=<the base64 text>
DATA_PROTECTION_CERTIFICATE_PASSWORD=<choose-a-password>
```

Keep `dp.pfx` and the password safe. If you lose them, everyone just signs in
again; no data is lost. (Replacing the certificate later does the same.)

**Option 2 — deliberately store the keys unprotected.** Only reasonable for a
single-user box whose database and backups nobody else can read. Set this
line in `.env` (the value must be exactly `true`; anything else still refuses
to start):

```
DATA_PROTECTION_ALLOW_UNPROTECTED_KEYS=true
```

If both are set, the certificate is used and the opt-out is ignored.

**Upgrading from an earlier release.** This is a breaking change. An install
that has neither option set no longer starts; the API container exits with an
error that names `DataProtection:CertificateBase64` and
`DataProtection:AllowUnprotectedKeys` (`docker compose logs api`). Add one of
the two options above and run `docker compose up -d` again. An install that
already used a certificate needs no change. Switching to a certificate
leaves the keys stored earlier unencrypted in the database until they expire
(Data Protection rotates keys about every 90 days). Retiring them sooner is
optional: delete the rows from the `DataProtectionKeys` table and restart the
`api` container; that signs every member out once. Old database backups keep
the old keys until they are deleted.

## 3. Start the stack

```bash
docker compose up -d
```

The first run builds the container image (compiles the frontend and the
.NET API, then packages both into one image) and starts two containers:

- `api` — the Energy Tracker application, listening on port `8080`.
- `postgres` — the database, used only by `api` and not exposed outside the
  Docker network.

Subsequent runs reuse the built image and start in a few seconds.

## 4. Verify it's running

Check that the API is healthy:

```bash
curl http://localhost:8080/health
```

A healthy instance responds with an empty `200 OK`. This check only confirms
the API process itself is alive — it does not depend on the database, so it
stays accurate even while Postgres is still starting up.

Then open [http://localhost:8080](http://localhost:8080) in a browser — you
should see the Energy Tracker application shell load.

## Put a reverse proxy in front

The quick start above talks to port `8080` directly, which is fine for trying
the app on your own machine. For anything reachable by other people, put a
TLS-terminating reverse proxy (Caddy, nginx, Traefik, ...) in front of the
`api` container and expose only the proxy:

- The proxy must set `X-Forwarded-Proto` and `X-Forwarded-For`, and must
  **overwrite** (or append the real peer address to) any `X-Forwarded-For`
  a client sends. The app trusts those headers from any caller.
- If clients can reach port `8080` directly, a client can choose its own IP
  address and so get around the sign-in rate limit (`/login`).
- Production responses carry a Content-Security-Policy, `nosniff` and
  `Referrer-Policy: same-origin`, plus `Strict-Transport-Security` over HTTPS.
  A proxy must not strip or replace them.
- Requests above the rate limits (sign-in, uploads, export, invites) get an
  HTTP `429` with a `Retry-After` header. Normal household use stays far
  below them.

## Run exactly one instance

Energy Tracker is built to run as **one active instance per database**. That
is a decision, not an oversight (AD-24): at household scale a second instance
buys you nothing, and several parts of the app keep their state in the memory
or on the disk of the one process that serves you.

- **On Azure**, `maxReplicas` stays `1`. Leave it alone.
- **Self-hosted**, run one `api` container per database. Never
  `docker compose up --scale api=2` (or any other number), and never start a
  second stack against the same database.

"One instance" means one replica at a time, **not** one long-lived process.
Azure Container Apps scales to zero when idle and replaces the replica on every
deploy, so the replica's disk is not stable: a file in its temp directory can be
gone by the next request. The app is built for that, but the parts below are why
a second instance would break it.

| Part | What it does | With a second instance |
|---|---|---|
| Upload registry (`MemoryHouseholdImportUploadRegistry`) | Remembers a validated restore file until you confirm it. | The confirm request can reach the other instance, which has never heard of the token and answers "no pending import found". You upload again. |
| Recompute lock (`HouseholdRecomputeLock`) | Lets one Status recomputation per household run at a time. | Two instances can recompute the same household at once and their writes can interleave (duplicate or out-of-order status snapshots). Smart Plug writes are guarded separately by database locks and are not affected. |
| Temp-file payloads (`SmartPlugImportEndpoints`, `HouseholdImportEndpoints`) | Uploaded files wait on local disk for the job that reads them. | The instance that picks up a job may not hold its file. The job ends as failed with "upload again" and is not retried. |
| In-process job queue (`InProcessChannelJobQueue`) | The default queue of a self-hosted install, held in memory. | A job queued on one instance only runs there. Harmless alone, unsafe together with the startup sweep below. |
| Startup job sweep (`InProcessJobStartupSweep`) | A starting instance fails every queued or running job left over from before it started (`job-interrupted`). | A second instance starting up fails the jobs the first one is running right now. |
| Upload expiry sweep (inside `MemoryHouseholdImportUploadRegistry`) | Removes the temp file of an upload nobody confirmed. | Each instance only cleans up its own uploads, so an upload abandoned on one instance is never cleaned up by the other. |
| In-process rate limiter (`HttpRateLimiting`) | Throttles sign-in, uploads, export, invites and member removal. | Limits are counted per instance, so the effective limit is the configured one times the number of instances. |

**Deploys.** A deploy replaces the running instance. On Azure with scale to
zero (`minReplicas = 0`) the old instance is stopped within about a second of
the new revision being created, usually before the new one has started; with a
higher minimum, or on another platform, the two can run side by side for a short
while. Either way, an upload, a restore confirmation or an import that is in
flight at the moment of the replacement can fail with "upload again" or
`job-interrupted`. Start it again; your stored data is not affected.

**The warning.** Every minute each instance writes a small heartbeat row
(table `AppInstances`) and logs a warning if another instance has been alive
next to it for more than 15 minutes. A shorter overlap, such as a deploy, logs
nothing. The check never blocks startup and never fails `/health`. The line
reads:

```text
Instance <id> has been running alongside instance <other id> (started <time>) for <n> minutes.
Energy Tracker supports one active instance (AD-24); see docs/self-hosting.md, 'Run exactly one instance'.
```

Self-hosted, look for it with `docker compose logs api`. On Azure, search the
container app's console log in Log Analytics for `supports one active
instance`. If you see it, go back to one: stop the extra container, or set the
replica count to `1` and deactivate the extra revision. The heartbeat does not
keep a scaled-to-zero replica awake (Container Apps scales on HTTP requests,
not on timers) and costs a few small database statements a minute.

**The queue adapter changes nothing.** The Azure Storage Queue job adapter
exists so a queued job survives a restart. It does not make several replicas
supported: temp files, the upload registry, the recompute lock and the rate
limiter are still per process.

**Lifting the rule.** Running more than one instance needs shared state first:
uploads in blob storage, the upload registry and the lock in the database, job
leases instead of the startup sweep, and a shared rate-limit store. That list is
the "Multi-instance shared state (lifting AD-24)" entry in the architecture's
`deferred.md`. It is a prerequisite, not a how-to; raising `maxReplicas` is an
architecture decision, not a config change.

## Members and access

- Every member of a household has the same rights. There are no roles and no
  owner: **any member can remove any other member**, and any member can leave.
  Removing a member also cancels every open invite link of the household.
  Both are done in **Settings → Household**.
- The last remaining member cannot leave or be removed through the app.
- A removed person keeps their sign-in but lands in the household set-up flow
  on their next request; the household's data stays with the household.
- Invite links are single use, expire after 7 days, and can be revoked in
  Settings. A link is shown once, when it is created: the server stores only a
  hash of it, so a lost link cannot be shown again (create a new one).
- **Recovering a household whose members are all gone is an operator task.**
  The app has no path for it. Either restore the database from a backup taken
  before the removal, or insert a row into the `HouseholdMembers` table for the
  person who should regain access: a new `Id`, the household's `HouseholdId`,
  that person's `ExternalIssuer` and `ExternalSubjectId` (the OIDC issuer and
  `sub` of their sign-in), an optional `DisplayName` and `CreatedAtUtc`. There
  is no script for this.

## Sessions and scripts that call the API

- A session ends at the latest **90 days after sign-in**, however often you
  use the app (it also ends after 14 days without a request). After that the
  app sends you to sign in again; if your identity provider's own session is
  still alive, that may happen without showing a login page. The first
  release with this limit signs everybody out once, because older session
  cookies carry no sign-in date.
- Any script or integration that calls `/api` with a mutating verb (`POST`,
  `PUT`, `PATCH`, `DELETE`) must send an `X-Requested-With` header. The value
  does not matter, only that it is present; without it the request is
  answered `400`. `GET` requests need nothing extra.
- Signing out is a `POST /logout` (with the same header), not a link.

## 5. Stopping and restarting

```bash
docker compose down       # stop the stack, keep your data
docker compose up -d      # start it again later
docker compose down -v    # stop the stack AND delete the database volume
```

Your data lives in a Docker-managed volume (`postgres-data`) that survives
`docker compose down` (without `-v`) and container image rebuilds.

Stopping or restarting the stack while an import, restore or history cleanup
is running ends that job as failed the next time the app starts, and it has
to be started again; your stored data is not affected. Run exactly one app
container per database ([why](#run-exactly-one-instance)): the startup recovery
assumes it is the only instance and would fail a second instance's running jobs.

## Running on modest hardware

This is the same Compose file and container image used for local
development — there is no separate "cloud edition." It's designed to run
comfortably on low-power hardware such as a NAS or a Raspberry Pi.

## Using SQL Server instead of Postgres (optional)

Postgres is the default and recommended provider. If you specifically want
to test the SQL Server code path locally, an optional override is provided:

```bash
docker compose -f docker-compose.yml -f docker-compose.sqlserver.yml up -d
```

This requires an `MSSQL_SA_PASSWORD` in your `.env` file (see
`.env.example`). This is intended for local testing of that provider path,
not as a second production deployment option.

## Privacy

Energy Tracker makes no telemetry or analytics calls to any third party by
default, and viewing your own household's data never requires a third-party
account.
