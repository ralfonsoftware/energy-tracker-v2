# Single-artifact deployment (AD-13): one image serves both the API and the built React SPA.
# The same image is used for local dev (docker-compose.yml) and self-hosting — no separate "cloud edition".

# --- Stage 1: build the frontend ---
FROM node:22-alpine AS frontend-build
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
COPY Directory.Packages.props ./
COPY src/ ./src/
RUN dotnet restore src/EnergyTracker.Api/EnergyTracker.Api.csproj
# The frontend build already wrote into src/EnergyTracker.Api/wwwroot on the host path scheme;
# reproduce that here from stage 1's output before publish picks up wwwroot content.
COPY --from=frontend-build /src/src/EnergyTracker.Api/wwwroot ./src/EnergyTracker.Api/wwwroot
RUN dotnet publish src/EnergyTracker.Api/EnergyTracker.Api.csproj -c Release -o /app/publish --no-restore

# --- Stage 3: runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=backend-build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "EnergyTracker.Api.dll"]
