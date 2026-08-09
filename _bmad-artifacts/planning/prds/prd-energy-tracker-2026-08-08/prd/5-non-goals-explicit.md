# 5. Non-Goals (Explicit)

- Not real-time or near-real-time monitoring — manual-read and file-upload driven by design, not a live dashboard.
- Not a hosted/managed offering — self-deploy only.
- Not a room-by-room energy audit tool — the precision claim v1 walked back is not repeated in v2.
- Not a native mobile app.
- Not a cross-user admin/management platform — multi-user may be technically possible, but the product isn't designed around managing users.
- Not a market-percentile tariff ranking service — needs a third-party data feed; deliberate long-run deferral, not dismissed.
- Not a plugin marketplace or general platform — the three Extension Points (FR-19–21) are scoped, not an open-ended system.
- Not a general life-logging tool — Context Capture is scoped to unmeasurable-appliance energy events only.
- AI-assisted features (FR-17) are never a hard dependency — the product functions fully with them disabled.
- Not a general billing engine — Tariff Configuration models flat base-fee-plus-price/kWh contracts only; tiered or time-of-use billing structures are out of scope for v2. This is a known limitation for markets where tiered billing is the norm; not solved in v2.
- Not multi-meter household support in v2's UI/logic — the data model allows more than one Main Meter per Household from day one (avoiding a schema rework later), but Pattern Detective and the dashboard operate on a single Main Meter per Household in v2; multi-meter flows are deferred.
- v2 is a ground-up rebuild, not a migration — there is no v1-to-v2 data migration path. v1 and v2 are separate, data-incompatible deployments; a v1 instance is retired, not upgraded in place.
