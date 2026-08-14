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

## 5. Stopping and restarting

```bash
docker compose down       # stop the stack, keep your data
docker compose up -d      # start it again later
docker compose down -v    # stop the stack AND delete the database volume
```

Your data lives in a Docker-managed volume (`postgres-data`) that survives
`docker compose down` (without `-v`) and container image rebuilds.

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
