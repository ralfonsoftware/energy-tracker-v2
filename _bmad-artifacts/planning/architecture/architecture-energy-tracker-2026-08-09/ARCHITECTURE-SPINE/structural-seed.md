# Structural Seed

```text
energy-tracker-v2/
  src/
    EnergyTracker.Domain/            # entities, value objects, Domain.Calculations (baseline math, Bonus-Decay Normalizer) — no external deps
    EnergyTracker.Application/       # use cases, ports (IBackgroundJobQueue, IAiPlausibilityClient, ISmartPlugParser, ICurrentHouseholdAccessor, repository interfaces)
    EnergyTracker.Infrastructure/    # EnergyTrackerDbContext, EF Core config, adapters (Postgres/SqlServer providers wired here, InProcessChannelJobQueue, AzureStorageQueueJobQueue, OpenAiCompatibleClient, EveHomeXlsxParser, MerossCsvParser)
    EnergyTracker.Infrastructure.Migrations.Postgres/    # migrations-only project (AD-2)
    EnergyTracker.Infrastructure.Migrations.SqlServer/   # migrations-only project (AD-2)
    EnergyTracker.Api/               # ASP.NET Core Minimal API host, composition root (Program.cs), serves built SPA from wwwroot/
  web/                                # React + Vite + shadcn/ui + Tailwind source; builds into EnergyTracker.Api/wwwroot
                                      # includes: service worker + IndexedDB offline queue for Meter Reading creation (AD-16), i18next locale catalogs (AD-18)
  scripts/
    add-migration.sh                 # adds a migration to both provider projects together (AD-2)
  docker-compose.yml                 # local dev: api + postgres (default provider)
  docker-compose.sqlserver.yml       # optional profile: swap in sqlserver to test that path locally
  Dockerfile                         # multi-stage: build web/ -> build .NET -> runtime image (single artifact, AD-13)
```

## Context

```mermaid
graph TB
  User["Household Member (phone/browser)"] -->|HTTPS| App["Energy Tracker (Api: API + SPA, single container)"]
  App -->|OIDC| OIDC["OIDC Provider (Entra ID / Auth0 / Authentik / Keycloak — config-selected)"]
  App -->|SQL| DB[("Postgres or Azure SQL — config-selected, AD-2")]
  App -->|enqueue/dequeue| Queue["Job Queue (in-process channel or Azure Storage Queue, AD-6)"]
  App -->|OpenAI-compatible HTTP, optional| AI["AI backend: local LMStudio or cloud API — AD-8"]
  User -->|uploads| Files["Smart Plug export files (Eve Home .xlsx, Meross .csv)"]
  Files --> App
```

## Core Entities (ERD)

```mermaid
erDiagram
  Household ||--o{ HouseholdMember : has
  Household ||--o{ MainMeter : has
  Household ||--o{ Room : has
  Household ||--o{ Tariff : "history of"
  Household ||--o{ Event : logs
  MainMeter ||--o{ MeterReading : has
  MainMeter ||--o{ MeterRegressionPrompt : "may raise"
  Household ||--o{ StatusSnapshot : "immutable history (AD-7)"
  Room ||--o{ PowerPoint : contains
  PowerPoint ||--o{ Device : has
  PowerPoint ||--o{ SmartPlugReading : "measured at (snapshot tag, AD-10)"
  Event }o--o| Room : "optional tag (snapshot, AD-10)"
  Event }o--o| PowerPoint : "optional tag (snapshot, AD-10)"
  Event }o--o| Device : "optional tag (snapshot, AD-10)"
  MeterReading ||--o{ AuditCorrection : "corrections (AD-11)"
  Tariff ||--o{ AuditCorrection : "corrections (AD-11)"
```

## Deployment & Environments

```mermaid
graph TB
  subgraph "Self-host (NAS / any Docker host)"
    A1["Container: energy-tracker (Api image)"] --- A2[("Container: postgres — the only provider viable on ARM NAS hardware, AD-2")]
  end
  subgraph "Azure (production)"
    B1["Container App: energy-tracker (same image, scale-to-zero, HTTP-triggered, AD-6/AD-7)"] --- B2[("Azure Database for PostgreSQL Flexible Server, Burstable — or — Azure SQL Basic DTU, config-selected AD-2")]
    B1 --- B3["Azure Storage Queue (job queue adapter, AD-6)"]
    B1 --- B4["Azure Container Registry (image source)"]
  end
```

Both environments run the **same container image** (AD-13); only configuration differs (DB provider, job queue adapter, OIDC issuer, AI backend endpoint). Local dev uses `docker-compose.yml` (api + postgres), which doubles as the self-host reference deployment.
