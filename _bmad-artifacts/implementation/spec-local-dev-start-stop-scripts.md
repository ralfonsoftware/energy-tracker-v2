---
title: 'Local dev start/stop scripts'
type: 'chore'
created: '2026-10-04'
status: 'done'
baseline_commit: 'd0ffc7029e6cc5a38162a007574b14ac6a9e8df2'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-artifacts/project-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Running the local stack takes three manual steps in three terminals (Postgres via docker compose, `scripts/run-api.sh`, `npm run dev` in `web/`), and shutting it down means hunting for each one.

**Approach:** Add `scripts/dev-up.sh` to start Postgres, the API and Vite in the background and `scripts/dev-down.sh` to stop all three. API startup reuses `run-api.sh` so its env mapping stays single-sourced.

## Boundaries & Constraints

**Always:** Reuse `scripts/run-api.sh` for the API (never duplicate its env mapping). Postgres starts with `-f docker-compose.yml -f docker-compose.local.yml up postgres -d` (host-published 127.0.0.1:5432) and the script waits until it is healthy before starting the API. Background PIDs and logs live in a gitignored `.dev-run/`. Scripts use `set -euo pipefail`, resolve the repo root like `run-api.sh`, and work on macOS bash 3.2 (no `mapfile`, no associative arrays). `dev-up.sh --watch` passes `--watch` to `run-api.sh`. `dev-down.sh` stops the process trees (dotnet and node children included) and runs `docker compose stop postgres`, preserving the data volume.

**Ask First:** Any change to `run-api.sh`, `docker-compose*.yml`, `vite.config.ts` or ports.

**Never:** Delete the Postgres volume by default (`dev-down.sh --volumes` is the only path that does, and it must say so). Start the Aspire dashboard or the containerised API. Commit `.env`. Touch the SQL Server compose file.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Cold start | Nothing running, `.env` present | Postgres healthy, API and Vite running in background, URLs and log paths printed, script exits 0 | N/A |
| Already running | PID file points to a live process | That component is skipped with a notice, no duplicate started | N/A |
| Stale PID file | PID file points to a dead process | File removed, component started fresh | N/A |
| Missing `.env` | No `.env` | Exit 1 with `cp .env.example .env` hint before anything starts | Message on stderr |
| Docker not running | `docker info` fails | Exit 1 with a clear message, nothing started | Message on stderr |
| Component dies at startup | API or Vite exits within the startup check | Exit 1, last lines of its log shown, already-started components left running (`dev-down.sh` cleans up) | Message on stderr |
| Missing deps | `web/node_modules` absent | `npm install` runs before Vite starts | npm failure aborts |
| Stop all | Any mix of running and stopped components | Everything stopped, idempotent, exit 0 even if nothing was running | N/A |
| Stop with wipe | `dev-down.sh --volumes` | Same as stop, then `docker compose down -v` for postgres | N/A |

</frozen-after-approval>

## Code Map

- `scripts/run-api.sh` -- API launcher (.env mapping, https launch profile, `--watch`); invoked, not modified
- `docker-compose.yml` -- postgres service with `pg_isready` healthcheck
- `docker-compose.local.yml` -- publishes Postgres to 127.0.0.1:5432
- `web/package.json` / `web/vite.config.ts` -- `npm run dev`, port 5173, proxies to the API
- `.gitignore` -- needs a `.dev-run/` entry
- `docs/local-development.md` -- local-dev guide; gets a short section on the new scripts

## Tasks & Acceptance

**Execution:**
- [x] `scripts/dev-up.sh` -- create, executable -- preflight (.env, docker), postgres up and wait healthy, start API via `run-api.sh` and Vite in the background (own process group), PID/log files in `.dev-run/`, startup liveness check, print URLs
- [x] `scripts/dev-down.sh` -- create, executable -- kill each component's process group from the PID files, remove them, `docker compose stop postgres`, optional `--volumes`
- [x] `.gitignore` -- add `.dev-run/`
- [x] `docs/local-development.md` -- add a short "One-command start/stop" section

**Acceptance Criteria:**
- Given a clean machine state with `.env` present, when `scripts/dev-up.sh` runs, then Postgres is healthy, `https://localhost:7005/health` (or `http://localhost:5133/health`) responds, and Vite answers on port 5173.
- Given the stack is up, when `scripts/dev-down.sh` runs, then no process listens on 5133, 7005 or 5173 and the postgres container is stopped but its volume remains.
- Given the stack is up, when `scripts/dev-up.sh` runs again, then no second API or Vite process starts.

## Design Notes

Background processes are started with `set -m` (or `setsid` fallback) so each gets its own process group; the PID file stores the group leader and `dev-down.sh` signals `-PGID` so `dotnet run` and `npm run dev` children die too. Escalate TERM to KILL after a few seconds.

## Verification

**Commands:**
- `bash -n scripts/dev-up.sh scripts/dev-down.sh` -- expected: no syntax errors
- `shellcheck scripts/dev-up.sh scripts/dev-down.sh` (if installed) -- expected: no warnings
- `scripts/dev-up.sh && curl -sk -o /dev/null -w '%{http_code}' https://localhost:5173/ ; scripts/dev-down.sh` -- expected: 200, then clean stop

**Manual checks (if no CLI):**
- After `dev-down.sh`, `lsof -i :5133 -i :7005 -i :5173` prints nothing and `docker compose ps` shows no running postgres.

## Suggested Review Order

**Start sequence**

- Entry point: Postgres first, healthy-wait via compose `--wait`, then API and Vite.
  [`dev-up.sh:111`](../../scripts/dev-up.sh#L111)

- Each component gets its own process group so the whole tree can be signalled later.
  [`dev-up.sh:59`](../../scripts/dev-up.sh#L59)

- PID file counts only if the process is alive and looks like ours (PID-reuse guard).
  [`dev-up.sh:39`](../../scripts/dev-up.sh#L39)

- Fails fast if a component dies; only warns on slow-but-alive startup.
  [`dev-up.sh:77`](../../scripts/dev-up.sh#L77)

**Stop sequence**

- TERM the process group, escalate to KILL, with the same PID-reuse guard.
  [`dev-down.sh:24`](../../scripts/dev-down.sh#L24)

- Volume survives by default; only `--volumes` runs `down -v`.
  [`dev-down.sh:63`](../../scripts/dev-down.sh#L63)

**Peripherals**

- Runtime state directory kept out of git.
  [`.gitignore:451`](../../.gitignore#L451)

- Docs point at the new scripts, manual steps remain documented.
  [`local-development.md:65`](../../docs/local-development.md#L65)
