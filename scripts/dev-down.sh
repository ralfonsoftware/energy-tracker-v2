#!/usr/bin/env bash
# Stops everything scripts/dev-up.sh started: the Vite dev server, the API (including the child
# processes of `dotnet run`/`npm run dev`) and the PostgreSQL container. The Postgres data volume
# is kept unless --volumes is passed. Safe to run when nothing is running.
#
# Usage: scripts/dev-down.sh [--volumes]   (--volumes -> also DELETE the Postgres data volume)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

RUN_DIR="$REPO_ROOT/.dev-run"
WIPE_VOLUMES=0
for arg in "$@"; do
  case "$arg" in
    --volumes) WIPE_VOLUMES=1 ;;
    -h|--help) sed -n '2,6p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown argument: $arg" >&2; exit 2 ;;
  esac
done

# dev-up.sh starts each component as its own process-group leader, so signalling -PID reaches the
# whole tree. TERM first, KILL if it is still alive after ~10s.
stop_component() {
  local name="$1" pid_file="$2" pid i
  if [[ ! -f "$pid_file" ]]; then
    echo "$name: not started by dev-up.sh — nothing to stop."
    return 0
  fi
  pid="$(cat "$pid_file")"
  # Also bail out if the PID was reused by an unrelated process (e.g. after a reboot).
  if [[ -z "$pid" ]] || ! kill -0 "$pid" 2>/dev/null \
    || ! ps -o command= -p "$pid" 2>/dev/null | grep -Eq 'dotnet|npm|node|run-api'; then
    echo "$name: already stopped."
    rm -f "$pid_file"
    return 0
  fi
  kill -TERM -- "-$pid" 2>/dev/null || kill -TERM "$pid" 2>/dev/null || true
  for i in $(seq 1 20); do
    kill -0 "$pid" 2>/dev/null || break
    sleep 0.5
  done
  if kill -0 "$pid" 2>/dev/null; then
    kill -KILL -- "-$pid" 2>/dev/null || kill -KILL "$pid" 2>/dev/null || true
    echo "$name: did not exit on TERM, killed."
  else
    echo "$name: stopped."
  fi
  rm -f "$pid_file"
}

stop_component "Vite" "$RUN_DIR/web.pid"
stop_component "API" "$RUN_DIR/api.pid"

if ! docker info >/dev/null 2>&1; then
  echo "Docker is not running — skipping PostgreSQL (nothing to stop)."
  exit 0
fi

COMPOSE=(docker compose -f docker-compose.yml -f docker-compose.local.yml)
if (( WIPE_VOLUMES )); then
  echo "PostgreSQL: removing container AND data volume (--volumes)."
  "${COMPOSE[@]}" down -v
else
  "${COMPOSE[@]}" stop postgres
  echo "PostgreSQL: stopped (data volume kept)."
fi
