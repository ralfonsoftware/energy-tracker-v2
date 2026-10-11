#!/usr/bin/env bash
# Fail-fast permission preflight for the CI Azure identities (Story 11.5, audit S11, AC #5).
#
# Usage: scripts/ci/azure-preflight.sh <deploy-app|deploy-infra|pr>
#
# Run it right after the Azure login of a workflow, before any step that changes something. It
# checks the identity's rights BY BEHAVIOUR with harmless calls: every right the workflow needs
# must work (else the run would fail later, halfway through a deploy), and every right the
# identity must NOT have must be denied (else it can do more than its job). All probes run, so one
# failed run lists every problem. Exits non-zero if any probe failed. What each mode checks, and
# why, is the table in infra/README.md ("Workflow identities and their permissions"); keep the two
# in sync.
#
# Reads: AZURE_RESOURCE_GROUP_NAME (required). deploy-infra also reads the main.bicepparam
# environment variables and AZURE_AD_ONLY_AUTH_ENABLED (set by infra-deploy.yml).
# Tuning for local drills: PREFLIGHT_DB_ATTEMPTS (default 5), PREFLIGHT_DB_RETRY_SECONDS (30).
#
# Never prints secret values: stdout of every probe is discarded, only the tail of stderr is shown
# on failure. A denied probe must be an authorization failure; any other error (network,
# throttling) is reported as its own failure, never as a pass.
set -euo pipefail

MODE="${1:-}"
RG="${AZURE_RESOURCE_GROUP_NAME:-}"
README_REF='infra/README.md, section "Workflow identities and their permissions"'
# What an authorization denial looks like in az CLI stderr.
AUTH_DENIED_PATTERN='AuthorizationFailed|does not have authorization|AuthorizationPermissionMismatch|Forbidden|\(403\)|status code 403'
MIN_AZ_FOR_NO_RBAC='2.76.0'

FAILURES=0
ERR_FILE="$(mktemp)"
trap 'rm -f "$ERR_FILE"' EXIT

usage() {
  echo "Usage: $0 <deploy-app|deploy-infra|pr>" >&2
  echo "Requires AZURE_RESOURCE_GROUP_NAME and an active az login." >&2
  exit 2
}

ok() {
  echo "OK:   $1"
}

fail() {
  FAILURES=$((FAILURES + 1))
  echo "FAIL: $1 ($README_REF)"
  if [ "${GITHUB_ACTIONS:-}" = "true" ]; then
    echo "::error title=Preflight failed::$1"
  fi
}

# Last lines of the captured stderr, on one line, for failure messages.
err_tail() {
  tail -n 3 "$ERR_FILE" 2>/dev/null | tr '\n\r' '  ' | cut -c1-400
}

run_with_timeout() {
  local seconds="$1"
  shift
  if command -v timeout >/dev/null 2>&1; then
    timeout "$seconds" "$@"
  elif command -v gtimeout >/dev/null 2>&1; then
    gtimeout "$seconds" "$@"
  else
    "$@"
  fi
}

# probe_read <label> <missing-right> <command...>: the command must succeed.
probe_read() {
  local label="$1" right="$2"
  shift 2
  if "$@" >/dev/null 2>"$ERR_FILE"; then
    ok "$label"
  else
    fail "$label failed. Missing right or broken call: $right. Detail: $(err_tail)"
  fi
}

# probe_nonempty <label> <missing-right> <command...>: the command must succeed and print
# something (a name; the output itself is never shown).
probe_nonempty() {
  local label="$1" right="$2" out
  shift 2
  if out="$("$@" 2>"$ERR_FILE")" && [ -n "$out" ]; then
    ok "$label"
  else
    fail "$label returned nothing or failed. Missing right (or the resource does not exist): $right. Detail: $(err_tail)"
  fi
}

# probe_denied <label> <excess-right> <command...>: the command must be refused with an
# authorization error. Success means the identity has a right it should not have.
probe_denied() {
  local label="$1" right="$2"
  shift 2
  if "$@" >/dev/null 2>"$ERR_FILE"; then
    fail "$label was ALLOWED. Excess right: $right. Remove it from the identity."
  elif grep -Eiq "$AUTH_DENIED_PATTERN" "$ERR_FILE"; then
    ok "$label is denied, as intended"
  else
    fail "$label failed with an error that is not an authorization denial, so the right cannot be judged. Detail: $(err_tail)"
  fi
}

# The identity must see exactly the target resource group. A subscription-wide role (or a second
# scope) shows up as extra groups.
probe_resource_group_scope() {
  local groups unexpected
  if ! groups="$(az group list --query '[].name' -o tsv 2>"$ERR_FILE")"; then
    fail "Listing visible resource groups failed. Detail: $(err_tail)"
    return
  fi
  groups="$(printf '%s\n' "$groups" | tr -d '\r' | tr '[:upper:]' '[:lower:]' | awk 'NF')"
  local want
  want="$(printf '%s' "$RG" | tr '[:upper:]' '[:lower:]')"
  if ! printf '%s\n' "$groups" | grep -qxF "$want"; then
    fail "The identity cannot see resource group '$RG'. Missing right: Reader (or more) on that resource group."
    return
  fi
  unexpected="$(printf '%s\n' "$groups" | grep -vxF "$want" || true)"
  if [ -n "$unexpected" ]; then
    fail "The identity can see resource groups other than '$RG': $(printf '%s' "$unexpected" | tr '\n' ' '). Excess right: a role at subscription scope or on another resource group."
  else
    ok "The identity sees exactly resource group '$RG'"
  fi
}

# deploy-app: read the production database the way the migration step does. A fresh firewall rule
# can take minutes to propagate, so retry like the migration step (5 x 30 s).
probe_database_read() {
  local server fqdn connection attempts delay attempt
  attempts="${PREFLIGHT_DB_ATTEMPTS:-5}"
  delay="${PREFLIGHT_DB_RETRY_SECONDS:-30}"

  if ! server="$(az sql server list --resource-group "$RG" --query '[0].name' -o tsv 2>"$ERR_FILE")" || [ -z "$server" ]; then
    fail "Cannot resolve the SQL server for the database probe. Missing right: Microsoft.Sql/servers/read. Detail: $(err_tail)"
    return
  fi
  if ! fqdn="$(az sql server show --name "$server" --resource-group "$RG" --query fullyQualifiedDomainName -o tsv 2>"$ERR_FILE")" || [ -z "$fqdn" ]; then
    fail "Cannot read the SQL server's fully qualified name. Missing right: Microsoft.Sql/servers/read. Detail: $(err_tail)"
    return
  fi
  # Same shape as the migration step (AD-21): Entra auth through the az login session, no secret.
  connection="Server=tcp:${fqdn},1433;Database=energytracker;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

  dotnet tool restore >/dev/null 2>"$ERR_FILE" || {
    fail "dotnet tool restore failed, so the database probe cannot run. Detail: $(err_tail)"
    return
  }

  # Build once, outside the retry loop and its per-attempt timeout: otherwise the first attempt can
  # spend most of its 180 s on a cold build and be counted as a failed (and retried) database read.
  run_with_timeout 600 dotnet build src/EnergyTracker.Infrastructure.Migrations.SqlServer --nologo -v q \
    >/dev/null 2>"$ERR_FILE" || {
    fail "Building the migrations project failed, so the database probe cannot run. Detail: $(err_tail)"
    return
  }

  for attempt in $(seq 1 "$attempts"); do
    if run_with_timeout 180 dotnet ef migrations list --no-build \
      --project src/EnergyTracker.Infrastructure.Migrations.SqlServer \
      --startup-project src/EnergyTracker.Infrastructure.Migrations.SqlServer \
      --context EnergyTrackerDbContext \
      --connection "$connection" >/dev/null 2>"$ERR_FILE"; then
      ok "Production database read (dotnet ef migrations list) works"
      return
    fi
    if [ "$attempt" -lt "$attempts" ]; then
      echo "      database probe attempt $attempt/$attempts failed, retrying in ${delay}s (firewall rule propagation can take minutes)"
      sleep "$delay"
    fi
  done
  fail "Production database read failed after $attempts attempts. Likely firewall propagation or the identity's contained database user is missing or lacks rights (infra/sql/grant-entra-db-users.sql). Detail: $(err_tail)"
}

# deploy-infra: the only harmless call that exercises deploy-class write rights (including
# role-assignment write, which the template needs) is a what-if at the DEFAULT validation level.
probe_deploy_class_what_if() {
  local params=(infra/main.bicepparam)
  if [ -n "${AZURE_AD_ONLY_AUTH_ENABLED:-}" ]; then
    params+=("azureADOnlyAuthenticationEnabled=${AZURE_AD_ONLY_AUTH_ENABLED}")
  fi
  probe_read "Deployment what-if at the default validation level" \
    "deploy-class rights on the resource group (Owner, or Contributor plus User Access Administrator)" \
    az deployment group what-if \
      --name "preflight-${GITHUB_RUN_ID:-local}" \
      --resource-group "$RG" \
      --template-file infra/main.bicep \
      --parameters "${params[@]}" \
      --no-pretty-print
}

check_az_supports_no_rbac() {
  local version
  version="$(az version --query '"azure-cli"' -o tsv 2>/dev/null || echo 0)"
  if [ "$(printf '%s\n%s\n' "$MIN_AZ_FOR_NO_RBAC" "$version" | sort -V | head -n 1)" = "$MIN_AZ_FOR_NO_RBAC" ]; then
    ok "Azure CLI $version supports --validation-level ProviderNoRbac (needs $MIN_AZ_FOR_NO_RBAC or newer)"
  else
    fail "Azure CLI $version is too old for --validation-level ProviderNoRbac (needs $MIN_AZ_FOR_NO_RBAC or newer)"
  fi
}

case "$MODE" in
  deploy-app | deploy-infra | pr) ;;
  *) usage ;;
esac
if [ -z "$RG" ]; then
  echo "AZURE_RESOURCE_GROUP_NAME is not set." >&2
  usage
fi
command -v az >/dev/null 2>&1 || {
  echo "az CLI not found." >&2
  exit 2
}

cd "$(dirname "$0")/../.."
echo "Azure preflight, mode '$MODE', resource group '$RG'"

# The resource-group scope in the harmless write probe, resolved from the active subscription.
SUBSCRIPTION_ID="${AZURE_SUBSCRIPTION_ID:-$(az account show --query id -o tsv 2>/dev/null || true)}"

case "$MODE" in
  pr)
    probe_read "Read the resource group" "Reader on the resource group" az group show --name "$RG"
    probe_read "List container apps (what the PR what-if reads)" "Reader on the resource group" az containerapp list --resource-group "$RG"
    # Deleting a tag key that does not exist changes nothing when allowed, so the probe is harmless.
    probe_denied "Tag write on the resource group" \
      "any write right (Contributor/Owner/Tag Contributor); the PR identity must be read-only" \
      az tag update \
        --resource-id "/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RG}" \
        --operation Delete --tags energy-tracker-preflight
    probe_resource_group_scope
    check_az_supports_no_rbac
    ;;
  deploy-app)
    probe_nonempty "Read the SQL server" "Microsoft.Sql/servers/read on the resource group" az sql server list --resource-group "$RG" --query '[0].name' -o tsv
    probe_nonempty "Read the container registry" "Microsoft.ContainerRegistry/registries/read on the resource group" az acr list --resource-group "$RG" --query '[0].name' -o tsv
    probe_nonempty "Read the container app" "Microsoft.App/containerApps/read on the resource group" az containerapp list --resource-group "$RG" --query '[0].name' -o tsv
    probe_resource_group_scope
    probe_database_read
    ;;
  deploy-infra)
    probe_read "Read the resource group" "Owner on the resource group" az group show --name "$RG"
    probe_read "List role assignments on the resource group" "Microsoft.Authorization/roleAssignments/read (the template creates role assignments)" az role assignment list --resource-group "$RG"
    probe_resource_group_scope
    probe_deploy_class_what_if
    ;;
esac

if [ "$FAILURES" -gt 0 ]; then
  echo ""
  echo "Preflight FAILED: $FAILURES probe(s). Nothing was changed. Azure CLI: $(az version --query '"azure-cli"' -o tsv 2>/dev/null || echo unknown)"
  exit 1
fi
echo ""
echo "Preflight passed (mode '$MODE')."
