# Capability → Architecture Map

| Capability / Area | Lives in | Governed by |
| --- | --- | --- |
| Pattern Detective (FR-1–FR-9, FR-24–FR-25) | `Domain.Calculations` (baseline math), `Application` use cases, `Api` endpoints | AD-1, AD-4, AD-5, AD-7, AD-10, AD-11, AD-12, AD-14, AD-16 |
| Tariff Savings Radar (FR-10–FR-15) | `Domain.Calculations.BonusDecayNormalizer` (shared with Pattern Detective), `Application`, `Api` | AD-5, AD-4, AD-7, AD-11 |
| Context Capture (FR-16–FR-18) | `Application`, `Infrastructure.Adapters.OpenAiCompatibleClient` | AD-8, AD-10, AD-14 |
| Extensible Platform (FR-19–FR-21) | `Application.Ports` (`ISmartPlugParser` today; event-rule and threshold ports Deferred) | AD-9, AD-15, Deferred |
| Data Export/Import (FR-22–FR-23) | `Application` use case over all repositories | AD-2, AD-3 |
| Household & Access (FR-26–FR-28) | `Infrastructure` (OIDC handler), `Application.ICurrentHouseholdAccessor` | AD-3, AD-10, AD-15, AD-17, Consistency Conventions (auth) |
| Smart Plug Import (FR-4, FR-24) | `Infrastructure.Adapters` parsers, `IBackgroundJobQueue` | AD-6, AD-9, AD-7 (StatusSnapshot trigger), AD-3 (job-context isolation), AD-22 (watermark corruption detection), AD-23 (bulk-write) |
| i18n / Locale (SM-5) | `Household.Locale`, frontend catalogs, backend `IStringLocalizer` | AD-18, AD-15 |
| Operations (deployment envelope) | `/health` endpoint, structured logging, env-based secrets, OTel traces/metrics (+logs locally), ingestion cap + alerting | AD-19 |
