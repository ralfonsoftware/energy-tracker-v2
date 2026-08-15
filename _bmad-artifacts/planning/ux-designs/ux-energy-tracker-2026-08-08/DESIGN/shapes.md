# Shapes

Continuous, generous rounding throughout — the "liquid glass" material reads as soft-edged, not sharp-cornered. Four radii cover the product:

- **`{rounded.sm}`** (14px) — input fields, the Tariff Check prompt card.
- **`{rounded.md}`** (18px) — drill-down cards (Trend chart card, Room → Power Point → Device list card).
- **`{rounded.lg}`** (28px) — the Status card, its rear panel, and dialog-scale surfaces.
- **`{rounded.full}`** (9999px) — pill shapes only: the primary action button, status badges.

No sharp corners anywhere in the custom component layer. The Log Reading sheet is the one asymmetric case: `{rounded.lg}` on its top corners, flush (0px) on the bottom where it docks to the viewport edge — it's a panel sliding up from off-screen, not a floating card.
