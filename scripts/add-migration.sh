#!/usr/bin/env bash
# Adds an EF Core migration to both provider migration projects atomically (AD-2).
# A migration must never exist in only one provider's project.
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <MigrationName>" >&2
  exit 1
fi

MIGRATION_NAME="$1"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

POSTGRES_PROJECT="$REPO_ROOT/src/EnergyTracker.Infrastructure.Migrations.Postgres"
SQLSERVER_PROJECT="$REPO_ROOT/src/EnergyTracker.Infrastructure.Migrations.SqlServer"

cd "$REPO_ROOT"

echo "Adding migration '$MIGRATION_NAME' to Postgres migrations project..."
dotnet ef migrations add "$MIGRATION_NAME" \
  --project "$POSTGRES_PROJECT" \
  --startup-project "$POSTGRES_PROJECT" \
  --context EnergyTrackerDbContext \
  --output-dir Migrations

echo "Adding migration '$MIGRATION_NAME' to SqlServer migrations project..."
if ! dotnet ef migrations add "$MIGRATION_NAME" \
  --project "$SQLSERVER_PROJECT" \
  --startup-project "$SQLSERVER_PROJECT" \
  --context EnergyTrackerDbContext \
  --output-dir Migrations; then
  echo "SqlServer migration failed — rolling back the Postgres migration to keep both providers in sync." >&2
  if dotnet ef migrations remove \
    --project "$POSTGRES_PROJECT" \
    --startup-project "$POSTGRES_PROJECT" \
    --context EnergyTrackerDbContext \
    --force; then
    echo "Rollback succeeded — neither provider has migration '$MIGRATION_NAME'." >&2
  else
    echo "ROLLBACK FAILED — Postgres migrations project may still have '$MIGRATION_NAME' while SqlServer does not. Manual cleanup required: check $POSTGRES_PROJECT/Migrations/ before re-running." >&2
  fi
  exit 1
fi

echo "Migration '$MIGRATION_NAME' added to both providers."
