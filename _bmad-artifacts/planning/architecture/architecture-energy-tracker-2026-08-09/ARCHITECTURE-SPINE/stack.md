# Stack

| Name | Version |
| --- | --- |
| .NET | 10 (LTS, supported to Nov 2028) |
| ASP.NET Core | 10 — Minimal APIs |
| EF Core | 10 |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 |
| Microsoft.EntityFrameworkCore.SqlServer | 10.x (matching EF Core 10) |
| PostgreSQL (self-host) | 17.x |
| Azure SQL Database (cloud) | Basic tier, DTU purchasing model (~5 DTU, ~$5/mo flat — no auto-pause/scale-to-zero; the DTU model has no serverless tier, unlike Postgres Flexible Server's Burstable stop/start. User's explicit choice, accepted knowingly: cheap and simple beats elastic here.) |
| React | 19.x |
| Vite | 8.x |
| shadcn/ui + Tailwind CSS | v4 (per DESIGN.md) |
| Frontend i18n library | i18next (or equivalent additive-catalog library) — AD-18 |
| Frontend offline queue | IndexedDB-backed local write queue + service worker (background sync on reconnect) for Meter Reading **creation** (FR-1's offline NFR is scoped to entry, not edits — AD-16) |
| Docker / Docker Compose | current stable |
| Azure Container Apps | — (production host) |
| Azure Storage Queue | — (cloud job-queue adapter, AD-6) |
