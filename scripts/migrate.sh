#!/usr/bin/env bash
# Applies pending EF Core migrations to the local dev database (fresh clone, or after
# `docker compose down -v` wipes the Postgres volume). Companion to add-migration.sh, which
# creates migrations; this one applies them. Defaults to the Postgres provider — the one
# docker-compose.local.yml publishes to the host, matching run-api.sh/local-development.md's
# default local-dev path.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

PROVIDER="postgres"
if [[ "${1:-}" == "--provider" ]]; then
  PROVIDER="${2:-}"
  shift 2
fi

if [[ ! -f .env ]]; then
  echo "No .env found. Run: cp .env.example .env" >&2
  exit 1
fi

set -a
# shellcheck disable=SC1091
source .env
set +a

case "$PROVIDER" in
  postgres)
    MIGRATIONS_PROJECT="src/EnergyTracker.Infrastructure.Migrations.Postgres"
    # Points at localhost, not the "postgres" hostname docker-compose.yml's api service uses —
    # this runs on the host, not in the compose network. Requires
    # `docker compose -f docker-compose.yml -f docker-compose.local.yml up postgres -d` first,
    # which is what publishes the port to 127.0.0.1 (see run-api.sh).
    export ConnectionStrings__Default="Host=localhost;Database=${POSTGRES_DB:-energytracker};Username=${POSTGRES_USER:-energytracker};Password=${POSTGRES_PASSWORD:-change-me}"
    ;;
  sqlserver)
    MIGRATIONS_PROJECT="src/EnergyTracker.Infrastructure.Migrations.SqlServer"
    # Unlike postgres, docker-compose.sqlserver.yml doesn't publish a host port for sqlserver
    # (docs/local-development.md's "Switching to the SQL Server provider locally" section) — set
    # ConnectionStrings__Default yourself before calling this script if you've published one.
    if [[ -z "${ConnectionStrings__Default:-}" ]]; then
      echo "ConnectionStrings__Default is not set. docker-compose.sqlserver.yml doesn't publish" >&2
      echo "a host port for sqlserver by default — export ConnectionStrings__Default yourself" >&2
      echo "(see docker-compose.sqlserver.yml for the shape) before running: $0 --provider sqlserver" >&2
      exit 1
    fi
    ;;
  *)
    echo "Unknown provider '$PROVIDER'. Use 'postgres' (default) or 'sqlserver'." >&2
    exit 1
    ;;
esac

echo "Applying $PROVIDER migrations..."
dotnet ef database update \
  --project "$MIGRATIONS_PROJECT" \
  --startup-project "$MIGRATIONS_PROJECT" \
  --context EnergyTrackerDbContext \
  "$@"
