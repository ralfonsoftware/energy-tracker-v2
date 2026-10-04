#!/usr/bin/env bash
# Starts the whole local stack in the background: PostgreSQL (docker compose, host-published on
# 127.0.0.1:5432 via docker-compose.local.yml), the .NET API (scripts/run-api.sh, which maps .env
# onto ASP.NET Core config) and the Vite dev server (web/). PIDs and logs live in .dev-run/.
# Stop everything with scripts/dev-down.sh.
#
# Usage: scripts/dev-up.sh [--watch]   (--watch -> `dotnet watch run` for the API)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

RUN_DIR="$REPO_ROOT/.dev-run"
API_PID_FILE="$RUN_DIR/api.pid"
WEB_PID_FILE="$RUN_DIR/web.pid"
API_LOG="$RUN_DIR/api.log"
WEB_LOG="$RUN_DIR/web.log"

API_HEALTH_URLS=("http://localhost:5133/health" "https://localhost:7005/health")
WEB_URLS=("https://localhost:5173/" "http://localhost:5173/")
API_TIMEOUT=120 # first run includes a `dotnet build`
WEB_TIMEOUT=60

API_ARGS=()
for arg in "$@"; do
  case "$arg" in
    --watch) API_ARGS+=(--watch) ;;
    -h|--help) sed -n '2,7p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown argument: $arg" >&2; exit 2 ;;
  esac
done

compose() {
  docker compose -f docker-compose.yml -f docker-compose.local.yml "$@"
}

# A PID file only counts if the PID is alive AND still looks like one of our processes — after a
# reboot the PID may have been reused by something unrelated.
is_alive() {
  local pid_file="$1" pid
  [[ -f "$pid_file" ]] || return 1
  pid="$(cat "$pid_file")"
  [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null \
    && ps -o command= -p "$pid" 2>/dev/null | grep -Eq 'dotnet|npm|node|run-api'
}

any_url_up() {
  local url
  for url in "$@"; do
    if curl -sk -o /dev/null --max-time 2 "$url"; then
      return 0
    fi
  done
  return 1
}

# Starts "$@" in its own process group (so dev-down.sh can signal the whole tree: `dotnet run` and
# `npm run dev` both spawn children) with stdout/stderr going to the log file.
start_background() {
  local name="$1" pid_file="$2" log_file="$3" workdir="$4"
  shift 4
  if is_alive "$pid_file"; then
    echo "$name already running (PID $(cat "$pid_file")) — skipping."
    return 0
  fi
  rm -f "$pid_file"
  (
    set -m
    cd "$workdir"
    "$@" </dev/null >"$log_file" 2>&1 &
    echo $! >"$pid_file"
  )
  echo "Started $name (PID $(cat "$pid_file")), log: ${log_file#"$REPO_ROOT"/}"
}

# Waits until one of the URLs answers; fails fast if the process dies, warns on timeout.
wait_for() {
  local name="$1" pid_file="$2" log_file="$3" timeout="$4"
  shift 4
  local waited=0
  while ! any_url_up "$@"; do
    if ! is_alive "$pid_file"; then
      echo "$name exited during startup. Last log lines:" >&2
      tail -n 20 "$log_file" >&2 || true
      echo "Components already started keep running — scripts/dev-down.sh cleans up." >&2
      exit 1
    fi
    if (( waited >= timeout )); then
      echo "Warning: $name still not answering after ${timeout}s (process is alive) — check ${log_file#"$REPO_ROOT"/}." >&2
      return 0
    fi
    sleep 2
    waited=$((waited + 2))
  done
  echo "$name is up."
}

if [[ ! -f .env ]]; then
  echo "No .env found. Run: cp .env.example .env" >&2
  exit 1
fi

if ! docker info >/dev/null 2>&1; then
  echo "Docker is not running. Start Docker Desktop (or your Docker daemon) and retry." >&2
  exit 1
fi

mkdir -p "$RUN_DIR"

echo "Starting PostgreSQL..."
compose up postgres -d --wait --wait-timeout 90

start_background "API" "$API_PID_FILE" "$API_LOG" "$REPO_ROOT" \
  "$REPO_ROOT/scripts/run-api.sh" ${API_ARGS[@]+"${API_ARGS[@]}"}

if [[ ! -d web/node_modules ]]; then
  echo "web/node_modules missing — running npm install..."
  (cd web && npm install)
fi
start_background "Vite" "$WEB_PID_FILE" "$WEB_LOG" "$REPO_ROOT/web" npm run dev

wait_for "API" "$API_PID_FILE" "$API_LOG" "$API_TIMEOUT" "${API_HEALTH_URLS[@]}"
wait_for "Vite" "$WEB_PID_FILE" "$WEB_LOG" "$WEB_TIMEOUT" "${WEB_URLS[@]}"

cat <<EOF

Local stack is running:
  Web (Vite)   https://localhost:5173  (http if certs/vite-dev-cert.* are absent)
  API          http://localhost:5133 · https://localhost:7005
  PostgreSQL   127.0.0.1:5432
Logs: .dev-run/api.log, .dev-run/web.log
Stop: scripts/dev-down.sh
EOF
