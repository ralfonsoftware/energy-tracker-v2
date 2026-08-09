# Local Development

This guide is for developers working on Energy Tracker itself — running the
API and frontend as two live, hot-reloading processes with full debugger
support, rather than as a single built container image. If you just want to
run a finished instance, see [self-hosting.md](self-hosting.md) instead.

Assumes: VS Code, and a terminal (all steps also work from the CLI alone —
VS Code is optional but the debugging instructions assume it).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 22+](https://nodejs.org/) and npm
- [Docker](https://docs.docker.com/get-docker/) (used only to run Postgres locally)
- [VS Code](https://code.visualstudio.com/) with the recommended extensions
  (VS Code will prompt you to install these on first open — see
  `.vscode/extensions.json`):
  - **C# Dev Kit** (`ms-dotnettools.csdevkit`) — backend debugging
  - **Playwright Test for VSCode** (`ms-playwright.playwright`) — e2e test runner UI
  - **Tailwind CSS IntelliSense** (`bradlc.vscode-tailwindcss`)

## First-time setup

```bash
git clone <this-repository-url>
cd energy-tracker-v2
cp .env.example .env        # set POSTGRES_PASSWORD to anything for local dev
dotnet tool restore         # installs dotnet-ef, pinned in .config/dotnet-tools.json
npm --prefix web install
```

## Running the stack for development

Local development runs three things side by side, each independently
restartable:

1. **Postgres** — via Docker (you don't need a local Postgres install)
2. **The API** — via `dotnet run` / `dotnet watch run`, not the Docker image
3. **The frontend** — via the Vite dev server, not the built static files

This gives you hot reload on both sides and full debugger support, which the
single Docker image build (used for self-hosting) doesn't.

### 1. Start Postgres only

```bash
docker compose -f docker-compose.yml -f docker-compose.local.yml up postgres -d
```

This starts just the `postgres` service from `docker-compose.yml` — not the
`api` container. The `docker-compose.local.yml` override additionally
publishes Postgres to `127.0.0.1:5432` so the API, running natively via
`dotnet run` below (not in a container), can reach it; this override is
local-dev-only and is never used for self-hosting, so the self-host
reference deployment (`docker-compose.yml` alone) keeps Postgres unreachable
outside the Docker network by default. Leave the container running; you
only need to repeat this after a reboot or `docker compose down`.

### 2. Run the API

```bash
dotnet run --project src/EnergyTracker.Api
# or, for auto-restart on file changes:
dotnet watch run --project src/EnergyTracker.Api
```

This uses the `http` launch profile in
`src/EnergyTracker.Api/Properties/launchSettings.json`
(`ASPNETCORE_ENVIRONMENT=Development`, listening on `http://localhost:5133`).
The `Database:Provider`/`ConnectionStrings:Default` values fall back to
`Postgres` / `localhost` in code when unset — matching the container started
in step 1 — so no extra configuration is needed for the default path.

Verify it's up: `curl http://localhost:5133/health` → `200`.

### 3. Run the frontend

```bash
npm --prefix web run dev
```

This starts the Vite dev server at `http://localhost:5173` with hot module
reload. `web/vite.config.ts` proxies `/health` and `/api/*` requests to
`http://localhost:5133` (the API from step 2), so the frontend can call the
API from the browser without CORS issues, exactly as it will once the two
are served together in production. Add new backend routes under `/api/` to
pick up this proxy automatically.

Open `http://localhost:5173` in a browser — **not** `http://localhost:5133`;
the API doesn't serve `wwwroot` content usefully in this mode since the
frontend isn't built to disk during `npm run dev`.

## Running it all from VS Code (F5)

The repo ships `.vscode/launch.json` and `.vscode/tasks.json` with three
launch configurations (Run and Debug panel, or `F5`):

- **.NET: Launch API** — builds and starts the API with the debugger
  attached (breakpoints, watch, call stack, the works). Requires Postgres
  already running (step 1 above) — VS Code doesn't start it automatically.
- **Frontend: Launch Chrome against Vite dev server** — starts the Vite dev
  server as a background task, then opens it in a debuggable Chrome instance
  with source maps wired up, so breakpoints set in `.tsx`/`.ts` files in VS
  Code hit directly (no need for browser DevTools).
- **Full stack: API + Frontend** (compound) — runs both of the above
  together. This is the one you want most of the time.

Start Postgres first (`docker compose -f docker-compose.yml -f docker-compose.local.yml up postgres -d`,
or run the `postgres-up` task from the Command Palette → "Tasks: Run Task"),
then press `F5` and pick **Full stack: API + Frontend**.

## Debugging tips

- **Backend breakpoints**: set them anywhere in `src/EnergyTracker.Api`,
  `Infrastructure`, etc. — the C# Dev Kit debugger (`coreclr`) stops there
  when the request path is hit. `dotnet watch run` (CLI-only, no debugger)
  restarts the process on save instead; use the VS Code launch config when
  you need breakpoints.
- **Frontend breakpoints**: set them directly in `.tsx`/`.ts` source in VS
  Code — the launch config maps compiled/HMR'd code back to source via
  source maps. You can also just use browser DevTools (F12) against
  `http://localhost:5173` if you're not using the VS Code launch config.
- **Database**: connect a DB client (e.g. the VS Code PostgreSQL extension,
  `psql`, or TablePlus/DBeaver) to `localhost:5432`, database/user/password
  from your `.env`, to inspect data while debugging. This only works because
  `docker-compose.local.yml` (step 1) publishes the port to `127.0.0.1` for
  local dev — the self-host reference deployment (`docker-compose.yml`
  alone) keeps Postgres unreachable outside the Docker network by default.
- **Switching to the SQL Server provider locally**: run
  `docker compose -f docker-compose.yml -f docker-compose.sqlserver.yml up sqlserver -d`
  instead of step 1, then set `Database__Provider=SqlServer` and
  `ConnectionStrings__Default` (see `docker-compose.sqlserver.yml` for the
  shape) as environment variables before `dotnet run`, or add a
  `appsettings.Local.json` (git-ignored, not currently scaffolded) if you do
  this often.

## Running tests locally

```bash
# Backend — from repo root
dotnet test EnergyTracker.sln

# Frontend unit/component tests
npm --prefix web run test          # single run
npm --prefix web run test:watch    # watch mode

# Frontend e2e (Playwright) — builds nothing extra, spins up its own preview server
npm --prefix web run test:e2e
```

The Playwright VS Code extension also gives you a Test Explorer view and
per-test "Run"/"Debug" buttons if you'd rather not use the CLI for e2e tests.

## Common issues

- **Port already in use**: another process is bound to `5133` (API),
  `5173` (Vite), or `5432` (Postgres, published locally via
  `docker-compose.local.yml`). Stop the conflicting process, or change the
  port in `launchSettings.json` / `vite.config.ts` / `docker-compose.local.yml`
  respectively.
- **`/health` returns nothing / connection refused from the frontend**:
  the API (step 2) isn't running, or isn't on `5133`. The Vite proxy target
  is hardcoded to `http://localhost:5133` — if you changed the API's port,
  update `web/vite.config.ts` too.
- **EF Core can't connect**: confirm `docker compose ps` shows `postgres`
  as `healthy`, and that `.env`'s `POSTGRES_PASSWORD` matches what the API
  is using (the API falls back to `energytracker`/`change-me` — the
  `.env.example` defaults — if `ConnectionStrings:Default` isn't set;
  override it via environment variable if your `.env` password differs).
