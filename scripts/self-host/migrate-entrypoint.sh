#!/bin/sh
# Entrypoint of the compose `migrate` service (Dockerfile target `migrate`): run the EF Core migrations
# bundle and keep its exit code, so `service_completed_successfully` holds `api` back when it fails.
# The failure lines are quoted in docs/self-hosting.md ("Database migrations"); change both together.
set -u

# Run the bundle in the background and forward SIGTERM/SIGINT to it: this shell is PID 1, so without the trap
# `docker compose stop` would SIGKILL a migration after the timeout instead of letting it stop.
/app/efbundle &
pid=$!
trap 'kill -TERM "$pid" 2>/dev/null' TERM INT
wait "$pid"
status=$?
# A trapped signal ends the first wait early; wait again for the bundle's real exit code.
if kill -0 "$pid" 2>/dev/null; then
    wait "$pid"
    status=$?
fi

if [ "$status" -ne 0 ]; then
    echo "migrate: FAILED. The migration did not finish, so the api container was not started." >&2
    echo "migrate: likely cause: database unreachable, POSTGRES_PASSWORD in .env does not match the existing postgres-data volume, or a migration failed." >&2
    echo "migrate: read the cause with: docker compose logs migrate" >&2
    echo "migrate: after fixing it, run: docker compose up -d (details: docs/self-hosting.md, 'Database migrations')" >&2
    echo "migrate: exit code $status" >&2
    exit "$status"
fi

echo "migrate: database schema is current."
exit "$status"
