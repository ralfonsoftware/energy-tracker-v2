# Single-artifact deployment (AD-13): one image serves both the API and the built React SPA.
# The same image is used for local dev (docker-compose.yml) and self-hosting — no separate "cloud edition".

# --- Stage 1: build the frontend ---
FROM node:26-alpine AS frontend-build
WORKDIR /src
COPY web/package.json web/package-lock.json ./web/
RUN npm --prefix web ci --no-audit --no-fund
COPY web/ ./web/
RUN npm --prefix web run build

# --- Stage 2: build and publish the .NET solution ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
# No outbound phone-home (NFR12): disable the dotnet CLI's own first-run telemetry ping during build.
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_NOLOGO=1
WORKDIR /src
# The three root MSBuild/SDK files, before src/ so a source change does not invalidate the restore layer
# more than it did. Directory.Build.props is the NuGet-audit gate (NuGetAuditLevel=moderate, NU1902-NU1904 as
# errors) and global.json is the SDK pin: the image must be built the way CI verified the code, so an advisory
# that fails CI now fails the image build too, on purpose (Story 11.11, audit S5). MSBuild finds
# Directory.Build.props by walking up from /src/src/EnergyTracker.Api, so it has to sit at /src.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ ./src/
RUN dotnet restore src/EnergyTracker.Api/EnergyTracker.Api.csproj
# The frontend build already wrote into src/EnergyTracker.Api/wwwroot on the host path scheme;
# reproduce that here from stage 1's output before publish picks up wwwroot content.
COPY --from=frontend-build /src/src/EnergyTracker.Api/wwwroot ./src/EnergyTracker.Api/wwwroot
RUN dotnet publish src/EnergyTracker.Api/EnergyTracker.Api.csproj -c Release -o /app/publish --no-restore

# --- Stage 3: EF migrations bundle (docker-compose.yml `migrate` service only) ---
# Self-hosters have no SDK, so the Postgres migrations are compiled into one self-contained executable here
# and run by the one-shot `migrate` service before `api` starts (Story 11.21). Built per TARGETARCH (BuildKit)
# because a bundle is per runtime identifier; Azure migrates in CI and never builds this target.
FROM backend-build AS migrate-build
ARG TARGETARCH
COPY .config ./.config
RUN dotnet tool restore \
    && dotnet restore src/EnergyTracker.Infrastructure.Migrations.Postgres/EnergyTracker.Infrastructure.Migrations.Postgres.csproj
RUN case "$TARGETARCH" in \
        amd64) RID=linux-x64 ;; \
        arm64) RID=linux-arm64 ;; \
        *) echo "Unsupported TARGETARCH '$TARGETARCH' for the migrations bundle" >&2; exit 1 ;; \
    esac \
    && dotnet ef migrations bundle \
        --project src/EnergyTracker.Infrastructure.Migrations.Postgres \
        --startup-project src/EnergyTracker.Infrastructure.Migrations.Postgres \
        --context EnergyTrackerDbContext \
        --self-contained -r "$RID" -o /app/efbundle

# --- Stage 4: migrate (run once by docker-compose.yml, not part of the default image) ---
# Non-root like the runtime stage; the bundle reads ConnectionStrings__Default from the environment.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS migrate
WORKDIR /app
COPY --from=migrate-build /app/efbundle ./efbundle
COPY scripts/self-host/migrate-entrypoint.sh ./migrate-entrypoint.sh
USER $APP_UID
ENTRYPOINT ["/app/migrate-entrypoint.sh"]

# --- Stage 5: runtime (last, so a plain `docker build .` still produces the API image) ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=backend-build /app/publish .
# Not root (audit S5). The aspnet image ships the `app` user as APP_UID (1654); a numeric id also lets an
# orchestrator's runAsNonRoot check pass. Port 8080 needs no privilege, and the process reads /app and writes
# only Path.GetTempPath() (/tmp, world-writable). The files stay root-owned and read-only to the process on
# purpose, so there is no --chown.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "EnergyTracker.Api.dll"]
