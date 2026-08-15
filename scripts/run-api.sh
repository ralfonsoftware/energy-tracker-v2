#!/usr/bin/env bash
# Runs the API locally via `dotnet run`/`dotnet watch run`, loading Oidc/DataProtection/Ai
# secrets from .env — mirrors the mapping docker-compose.yml does for the container (single
# underscore .env keys -> double-underscore ASP.NET Core config keys), so the same .env used for
# `docker compose up postgres` also drives `dotnet run` without retyping secrets each session.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

if [[ ! -f .env ]]; then
  echo "No .env found. Run: cp .env.example .env" >&2
  exit 1
fi

set -a
# shellcheck disable=SC1091
source .env
set +a

export Oidc__Authority="${OIDC_AUTHORITY:-}"
export Oidc__ClientId="${OIDC_CLIENT_ID:-}"
export Oidc__ClientSecret="${OIDC_CLIENT_SECRET:-}"
export Otel__Exporter="${OTEL_EXPORTER:-Otel}"
export Otel__OtlpEndpoint="${OTEL_OTLP_ENDPOINT:-}"
export DataProtection__CertificateBase64="${DATA_PROTECTION_CERTIFICATE_BASE64:-}"
export DataProtection__CertificatePassword="${DATA_PROTECTION_CERTIFICATE_PASSWORD:-}"
export Ai__ApiKey="${AI_API_KEY:-}"

# Program.cs's fallback connection string (no ConnectionStrings:Default set) hardcodes
# Password=change-me — only correct if .env's POSTGRES_PASSWORD was never changed from
# .env.example. Setting this from .env here keeps `docker compose up postgres` and `dotnet run`
# in sync regardless, and points at localhost since the API runs natively, not in the compose
# network (docs/local-development.md, "EF Core can't connect").
export ConnectionStrings__Default="Host=localhost;Database=${POSTGRES_DB:-energytracker};Username=${POSTGRES_USER:-energytracker};Password=${POSTGRES_PASSWORD:-change-me}"

RUN_CMD=(dotnet run --project src/EnergyTracker.Api)
if [[ "${1:-}" == "--watch" ]]; then
  RUN_CMD=(dotnet watch run --project src/EnergyTracker.Api)
  shift
fi

# The "https" launchSettings.json profile binds https://localhost:7005 IN ADDITION TO
# http://localhost:5133 (not instead of) — Auth0 posts the OIDC callback straight to whichever
# scheme/port the API itself computed as redirect_uri, bypassing Vite's proxy entirely for that
# hop, so the API must actually terminate HTTPS itself for Safari to accept the Secure
# correlation/session cookies on the way back (docs/local-development.md, "Testing sign-in in
# Safari"). Binding both costs nothing for the plain-http workflow, which keeps working unchanged.
RUN_CMD+=(--launch-profile https)

exec "${RUN_CMD[@]}" "$@"
