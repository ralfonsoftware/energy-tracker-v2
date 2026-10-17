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
unavailable — nobody can create the first Household.

(`/login` also accepts an optional `returnUrl` query parameter, used
internally by the Household member-invitation flow to return an invited
person to their `/join/{token}` link after signing in — no extra
configuration needed, informational only.)

`AI_API_KEY` in `.env` is reserved for a feature not yet built
(AI-assisted insights) — leave it blank for now.

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
container per database: the startup recovery assumes it is the only instance
and would fail a second instance's running jobs.

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
