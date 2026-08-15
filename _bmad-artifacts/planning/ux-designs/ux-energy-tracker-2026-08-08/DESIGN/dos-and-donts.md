# Do's and Don'ts

| Do | Don't |
|---|---|
| Use `{colors.brand-accent}` only for chrome — mark, links, active nav, primary action button | Use the brand-accent teal for any Status badge, dot, or headline, even when the status is "good" |
| Use the status triad (`{colors.status-within-range}`, `{colors.status-below-baseline}`, `{colors.status-trending}`) only for the Status/trend semantic states | Reuse a status-triad color for chrome, decoration, or a non-status badge |
| Reserve red/`destructive` exclusively for genuine system errors (failed save, broken import) | Use red for "trending," or for any Status/pace signal — the product stays calm, never alarmist |
| Gate all motion (specular sweep, card entrance, sheet slide, press compression) behind `prefers-reduced-motion: no-preference`, with an explicit settled/instant fallback | Ship an animation with no reduced-motion fallback, or treat reduced-motion as merely "slower" instead of instant |
| Keep the Status card the single highest-weight surface on the Dashboard — quiet treatment for Tariff Check, quiet-by-default for drill-down density | Let a drill-down view (Trend History, per-plug data) visually out-compete the Status card, or make checking it feel necessary to trust the headline (PRD Constraints — "says less, on purpose"; ties to counter-metric SM-C2) |
| Use `{rounded.full}` pills only for buttons and badges | Introduce a sharp-cornered surface anywhere in the custom component layer |
| Let Light mode fake glass depth with frosted-white translucency + soft tinted shadows | Try to replicate Dark mode's glow effect on a bright background — it reads as a lit bug, not a material |
| Reserve `{colors.attractiveness-worth-it}` / `{colors.attractiveness-not-worth-it}` exclusively for the FR-13 two-way tariff signal | Reuse the attractiveness green/red pair for Status triad meaning, or reuse Status/brand/error colors for the attractiveness signal — each color system in this product means exactly one thing |
| Use `{colors.focus-ring}` / `-dark` as the one canonical `:focus-visible` treatment on every interactive element | Ship a custom focus style per component, or rely on the browser default outline, which doesn't carry the product's material language |
