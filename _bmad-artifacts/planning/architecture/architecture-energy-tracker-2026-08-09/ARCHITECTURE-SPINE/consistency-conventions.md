# Consistency Conventions

| Concern | Convention |
| --- | --- |
| Naming | Entities/DTOs: PascalCase C#, singular (`MeterReading`, not `MeterReadings`). API routes: kebab-case plural nouns (`/api/meter-readings`). Ports: `I{Capability}` in `Application/Ports`; adapters: `{Vendor}{Capability}` in `Infrastructure/Adapters`. |
| Config-driven adapter selection | Every swappable capability (DB provider, job queue, AI backend, OIDC) is selected by exactly one config value read once at the composition root (`Program.cs`). No feature code branches on environment or provider elsewhere. |
| Data & formats | All timestamps: `DateTimeOffset`, ISO 8601 with explicit offset on the wire. All money: `decimal`, never `double`/`float`. Currency: ISO 4217 code stored per Tariff entry. Background/scheduled work (what little exists per AD-7) runs in UTC regardless of display Locale. |
| Errors | API errors are RFC 7807 `ProblemDetails`. Concurrency conflicts (AD-4) return 409 with the current server state so the client can reconcile. |
| State & cross-cutting | Every route requires authentication except the OIDC callback (NFR). Tenant scoping is DbContext-level only (AD-3) — no per-handler filtering. Soft-delete, never hard-delete, for Room/PowerPoint/Device (AD-10). |
| Migrations | `scripts/add-migration.sh <Name>` adds a migration to both provider projects atomically — a migration is never added to just one (AD-2). |
| API surface shape | The Dashboard Status endpoint returns only the current Status value and its one headline/supporting sentence (FR-7) — drill-down data (Trend History, per-plug view) is always a separate endpoint, never merged into the Status response. Structural guard for the brief's "says less, on purpose" discipline: growth pressure lands on drill-down endpoints, not the one surface the product is judged by. |
| Async job status | Clients learn a background job (Smart Plug import) finished by polling `GET /api/jobs/{id}`, never via WebSocket/SSE (AD-6). |
| Auth persistence | Server-side httpOnly session cookie via ASP.NET Core cookie auth chained to OIDC (AD-17) — the SPA never reads or stores a token itself. |
