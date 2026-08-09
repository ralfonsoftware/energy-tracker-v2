---
name: Energy Tracker v2
description: Self-hosted household electricity tracker. shadcn/ui on Tailwind CSS; this DESIGN.md specifies the brand/material-layer delta only — dark-mode-first "liquid glass" aesthetic with a green-eco palette that keeps brand chrome and Status semantics deliberately separate.
colors:
  # ---- Brand accent — chrome/interactive only (mark, wordmark accent, links,
  # active nav). Never used for a Status state. Light value is the base key;
  # "-dark" suffix is the dark-mode pair. Dark and Light are equal citizens
  # (no default mode) — both are first-class, not one derived from the other.
  brand-accent: '#1E7A61'
  brand-accent-dark: '#2FB397'
  brand-accent-foreground: '#FFFFFF'
  brand-accent-foreground-dark: '#06120D'
  brand-accent-bg: 'rgba(30,122,97,0.14)'
  brand-accent-bg-dark: 'rgba(47,179,151,0.16)'

  # ---- Status semantic triad — the three Pattern Detective states (FR-6).
  # Never used for brand chrome or decoration. Chosen to sit visibly apart
  # from brand-accent in hue+saturation so the two systems never collapse
  # into each other.
  status-within-range: '#6B8656'
  status-within-range-dark: '#9FBB8A'
  status-within-range-bg: 'rgba(107,134,86,0.14)'
  status-within-range-bg-dark: 'rgba(159,187,138,0.16)'

  status-below-baseline: '#2F9E52'
  status-below-baseline-dark: '#4FCA72'
  status-below-baseline-bg: 'rgba(47,158,82,0.15)'
  status-below-baseline-bg-dark: 'rgba(79,202,114,0.17)'

  status-trending: '#B87A1E'
  status-trending-dark: '#E2A542'
  status-trending-bg: 'rgba(184,122,30,0.16)'
  status-trending-bg-dark: 'rgba(226,165,66,0.18)'

  # ---- Status badge TEXT — a distinct, verified-AA set from the raw triad
  # above. The raw status-* hex fails 4.5:1 when set as small badge-label
  # text on its own `-bg` tint (measured 2.85–3.98:1) — normal-size text
  # needs a lighter/darker pull toward its own foreground than a dot,
  # underline, or chart line does, because those render on a larger area
  # with different contrast math. These are for badge LABEL TEXT only;
  # dots/lines/accents keep using the raw status-* triad above. Verified by
  # alpha-compositing each `-bg` tint over the status-card glass background
  # and checking WCAG contrast against the resulting composite.
  status-within-range-badge-text: '#41603A'
  status-within-range-badge-text-dark: '#C7DCBB'
  status-below-baseline-badge-text: '#1F7038'
  status-below-baseline-badge-text-dark: '#B9EFC7'
  status-trending-badge-text: '#8A5A14'
  status-trending-badge-text-dark: '#F5CE93'

  # ---- Attractiveness signal — the FR-13 two-way tariff-switch signal
  # (bonus-included / bonus-normalized rows, shown together, never
  # toggled). A deliberate 4th color system, not reused from the Status
  # triad (would give one hue three unrelated meanings — chrome,
  # consumption, tariff) and not reused from destructive/error-red (a
  # stay-put tariff verdict is a mild non-event, not a system error).
  # See Colors below for the full reasoning.
  # [ASSUMPTION] Light (base) values across this whole attractiveness-signal
  # block (main pair, supporting text, and the not-worth-it figure color
  # below) were not rendered this session — key-tariff-radar.html is
  # dark-only, matching this session's dark-only precedent for
  # motion-demo.html/density-trend-history.html. All were inferred by
  # applying the same dark→light degradation already used by the Status
  # triad (hue held constant, lightness cut ~29%, saturation roughly held);
  # see Elevation & Depth. Noted once here, not repeated below.
  attractiveness-worth-it: '#34B860'
  attractiveness-worth-it-dark: '#6FDB93'
  attractiveness-worth-it-bg: 'rgba(52,184,96,0.14)'
  attractiveness-worth-it-bg-dark: 'rgba(111,219,147,0.16)'
  attractiveness-not-worth-it: '#BB3627'
  attractiveness-not-worth-it-dark: '#E2685A'
  attractiveness-not-worth-it-bg: 'rgba(187,54,39,0.14)'
  attractiveness-not-worth-it-bg-dark: 'rgba(226,104,90,0.16)'

  # ---- Attractiveness signal — supporting text. The raw
  # attractiveness-not-worth-it hex, set directly as the figure/amount
  # text color on its own `-bg` tint, measures ~3.5:1 (fails AA) — same
  # "raw triad color as small text" problem as the Status badges above.
  # attractiveness-worth-it does not need a dedicated pair (its raw hex
  # already clears 4.5:1 as figure text on its own tint). Verified against
  # the actual rendered row background in key-tariff-radar.html.
  attractiveness-not-worth-it-text: '#9F2E21'
  attractiveness-not-worth-it-text-dark: '#EB958C'

  # ---- Attractiveness signal — supporting sentence text (`.signal-detail`
  # / `.signal-frame-label`): the plain-language explanatory copy under
  # each verdict badge. A plain text-secondary-dark alpha (0.62) falls just
  # short (~4.4:1) against the colored row tints; needs a slightly higher
  # alpha specifically for this colored-background context.
  attractiveness-signal-supporting-text: 'rgba(30,42,28,0.72)'
  attractiveness-signal-supporting-text-dark: 'rgba(234,245,238,0.7)'

  # ---- Error — reserved exclusively for genuine system errors/critical
  # messages, never for Status. [ASSUMPTION] No error swatch was rendered
  # or discussed in this session (mockups only cover brand + status triad);
  # per the shadcn-inheritance pattern this DESIGN.md is only supposed to
  # specify deltas, so error/destructive is left un-overridden and inherits
  # shadcn's default `destructive` token rather than inventing a hex here.

  # ---- Text
  text-primary: '#1E2A1C'
  text-primary-dark: '#EAF5EE'
  # text-secondary/-dark and text-quiet/-dark below are re-tuned from an
  # earlier low-alpha pass that measured 2.1–2.9:1 against their actual
  # composited backgrounds (surface-base → surface-quiet/-glass → text) —
  # well under the 4.5:1 AA floor for normal text. Values below are
  # verified by alpha-compositing over BOTH `{colors.surface-quiet}` and
  # `{colors.surface-glass}` (the two real grounds this text renders on)
  # in each mode; every combination clears 4.5:1 with margin.
  text-secondary: 'rgba(30,42,28,0.68)'
  text-secondary-dark: 'rgba(234,245,238,0.62)'
  text-quiet: 'rgba(30,42,28,0.65)'
  text-quiet-dark: 'rgba(234,245,238,0.55)'

  # ---- Surfaces (the glass system). Screen backgrounds render as a radial
  # gradient in the mockups; the value here is the gradient's dominant
  # mid-stop, documented as flat for token purposes.
  surface-base: '#F3F8ED'
  surface-base-dark: '#12201A'
  surface-panel-back: 'rgba(255,255,255,0.55)'
  surface-panel-back-dark: 'rgba(28,44,36,0.55)'
  surface-panel-back-border: 'rgba(40,70,50,0.10)'
  surface-panel-back-border-dark: 'rgba(210,235,220,0.10)'
  surface-glass: 'rgba(255,255,255,0.72)'
  surface-glass-dark: 'rgba(220,245,230,0.07)'
  surface-glass-border: 'rgba(255,255,255,0.85)'
  surface-glass-border-dark: 'rgba(210,235,220,0.16)'
  surface-quiet: 'rgba(255,255,255,0.4)'
  surface-quiet-dark: 'rgba(220,245,230,0.03)'

  # ---- Specular sweep highlight (the moving light-on-glass overlay)
  specular-sweep: 'rgba(255,255,255,0.6)'
  specular-sweep-dark: 'rgba(220,245,230,0.14)'

  # ---- Focus ring (WCAG 2.4.7/2.4.11). Dark value promotes the ad-hoc
  # `:focus-visible` outline already used in motion-demo.html's Log Reading
  # trigger (verified — 9.8:1 against surface-glass-dark, 11.8:1 against
  # surface-base-dark, both far past the 3:1 non-text-UI floor), so nothing
  # changes visually, it's just now canonical. Light value reuses
  # `{colors.brand-accent}`'s hex rather than inventing a new one — verified
  # at 5.1:1 against surface-glass and 4.9:1 against surface-base.
  focus-ring: '#1E7A61'
  focus-ring-dark: '#8FE9CE'
typography:
  # System font stack throughout — no webfont, fits the self-hosted/no-frills
  # ethos. Only one family exists in this product; roles below vary size/
  # weight, not typeface.
  font-family:
    note: '-apple-system, BlinkMacSystemFont, "SF Pro Display", "Segoe UI", Roboto, Helvetica, Arial, sans-serif — system stack, no webfont dependency'
  status-headline:
    fontFamily: '{typography.font-family}'
    fontSize: 27px
    fontWeight: '700'
    lineHeight: '1.16'
    letterSpacing: -0.3px
  status-figure:
    fontFamily: '{typography.font-family}'
    fontWeight: '700'
    note: 'tabular-nums applied — used specifically for Status/kWh numeric figures (Status card headline number, reading-entry field, trend-chart axis values) so digits hold a steady instrument-like rhythm. Not a separate typeface — same family/weight as surrounding text, tabular-nums is the only delta.'
  body:
    fontFamily: '{typography.font-family}'
    fontSize: 14px
    fontWeight: '400'
    lineHeight: '1.55'
  body-secondary:
    fontFamily: '{typography.font-family}'
    fontSize: 12px
    fontWeight: '400'
    lineHeight: '1.5'
  label-badge:
    fontFamily: '{typography.font-family}'
    fontSize: 11.5px
    fontWeight: '700'
    lineHeight: '1'
    letterSpacing: 1.1px
  wordmark:
    fontFamily: '{typography.font-family}'
    fontSize: 14px
    fontWeight: '700'
    letterSpacing: 0.2px
rounded:
  sm: 14px
  md: 18px
  lg: 28px
  full: 9999px
spacing:
  # Tailwind's 4px base scale is inherited as-is for anything not named below.
  '1': 4px
  '2': 8px
  '3': 12px
  '4': 16px
  '5': 24px
  '6': 32px
  card-padding: 24px
  card-gap: 18px
  mobile-margin: 19px
components:
  status-card:
    radius: '{rounded.lg}'
    background: '{colors.surface-glass}'
    background-dark: '{colors.surface-glass-dark}'
    border: '{colors.surface-glass-border}'
    border-dark: '{colors.surface-glass-border-dark}'
    padding: '{spacing.card-padding}'
    row-gap: '{spacing.3}'
    badge-dot-gap: '{spacing.2}'
    headline-type: '{typography.status-headline}'
    headline-color: '{colors.text-primary}'
    headline-color-dark: '{colors.text-primary-dark}'
    figure-type: '{typography.status-figure}'
    body-color: '{colors.text-secondary}'
    body-color-dark: '{colors.text-secondary-dark}'
    backdrop-filter-dark: 'blur(28px) saturate(160%)'
    backdrop-filter: 'blur(20px) saturate(140%)'
    specular-overlay: '{colors.specular-sweep}'
    specular-overlay-dark: '{colors.specular-sweep-dark}'
    panel-back-offset: '{colors.surface-panel-back}'
    panel-back-offset-dark: '{colors.surface-panel-back-dark}'
    panel-back-border: '{colors.surface-panel-back-border}'
    panel-back-border-dark: '{colors.surface-panel-back-border-dark}'
    badge-bg-within-range: '{colors.status-within-range-bg}'
    badge-bg-within-range-dark: '{colors.status-within-range-bg-dark}'
    badge-bg-below-baseline: '{colors.status-below-baseline-bg}'
    badge-bg-below-baseline-dark: '{colors.status-below-baseline-bg-dark}'
    badge-bg-trending: '{colors.status-trending-bg}'
    badge-bg-trending-dark: '{colors.status-trending-bg-dark}'
    badge-text-within-range: '{colors.status-within-range-badge-text}'
    badge-text-within-range-dark: '{colors.status-within-range-badge-text-dark}'
    badge-text-below-baseline: '{colors.status-below-baseline-badge-text}'
    badge-text-below-baseline-dark: '{colors.status-below-baseline-badge-text-dark}'
    badge-text-trending: '{colors.status-trending-badge-text}'
    badge-text-trending-dark: '{colors.status-trending-badge-text-dark}'
  meter-regression-prompt:
    radius: '{rounded.lg}'
    background: '{colors.surface-glass}'
    background-dark: '{colors.surface-glass-dark}'
    border: '{colors.surface-glass-border}'
    border-dark: '{colors.surface-glass-border-dark}'
    padding: '{spacing.card-padding}'
    backdrop-filter-dark: '{components.status-card.backdrop-filter-dark}'
    backdrop-filter: '{components.status-card.backdrop-filter}'
  log-reading-sheet:
    radius-top: '{rounded.lg}'
    radius-bottom: 0px
    background-dark: 'rgba(24,38,31,0.86)'
    border-dark: '{colors.surface-glass-border-dark}'
    backdrop-filter-dark: 'blur(24px) saturate(150%)'
    field-type: '{typography.status-figure}'
    field-radius: '{rounded.sm}'
  tariff-check-card:
    radius: '{rounded.sm}'
    background: '{colors.surface-quiet}'
    background-dark: '{colors.surface-quiet-dark}'
    text: '{colors.text-quiet}'
    text-dark: '{colors.text-quiet-dark}'
    type: '{typography.body-secondary}'
    padding: '{spacing.4}'
  nav-chrome:
    active-bg: '{colors.brand-accent-bg}'
    active-bg-dark: '{colors.brand-accent-bg-dark}'
    active-foreground: '{colors.brand-accent}'
    active-foreground-dark: '{colors.brand-accent-dark}'
  trend-chart:
    radius: '{rounded.md}'
    background-dark: '{colors.surface-glass-dark}'
    line-within-range: '{colors.status-within-range}'
    line-within-range-dark: '{colors.status-within-range-dark}'
    line-trending: '{colors.status-trending}'
    line-trending-dark: '{colors.status-trending-dark}'
    gap-band: 'rgba(184,122,30,0.07)'
    gap-band-dark: 'rgba(226,165,66,0.07)'
  primary-action-button:
    radius: '{rounded.full}'
    background: 'linear-gradient(160deg, #6FE3C4, #2FB397 70%)'
    foreground: '{colors.brand-accent-foreground}'
    foreground-dark: '{colors.brand-accent-foreground-dark}'
    press-scale: 0.965
sources:
  - _bmad-artifacts/planning/briefs/brief-energy-tracker-2026-08-08/brief.md
  - _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/index.md
status: final
created: 2026-08-08
updated: 2026-08-09
---

## Brand & Style

Energy Tracker v2 asks its household member to trust one number: the Status. Everything in this DESIGN.md exists to earn that trust without shouting for it. The aesthetic posture is **calm and trustworthy** — this is a household utility reporting a figure, not a dashboard competing for attention — expressed through Apple-"Liquid Glass"-inspired materials: translucent, layered panels with real z-depth, a slow specular sweep that catches like light on a lens, and continuous rounded shapes throughout. Bold (700-weight) headline type gives the Status its authority without resorting to loudness — confident, not decorative.

Dark and Light are **equal citizens**, not a default-plus-override pair — both received the full stacked-panel treatment, both were rendered and confirmed together. Dark is the mode most readings happen in (a phone at a dim meter cupboard); Light is the mode for calm-evening browsing on a brighter screen. Neither is the "real" version the other degrades from.

Color carries a second, quieter layer of meaning: green is used deliberately for two different reasons that never overlap (see Colors, below) — one green means "this is the product's own chrome," a different, separate set of greens/ambers means "this is what your consumption is doing." Less energy used is genuinely framed as both greener and cheaper, and the palette leans into that association without letting it collapse two unrelated color systems into one.

Foundation is **shadcn/ui on Tailwind CSS**. This document specifies only the brand/material-layer delta on top of shadcn's defaults — the glass-panel treatment, the green-eco palette, the type-scale additions (`status-headline`, `status-figure` with tabular-nums), and the handful of custom components (Status card, Log Reading sheet, Tariff Check card, Trend chart, primary action button). Everything not named here — form inputs, standard dialogs, dropdowns, toasts, tabs — inherits shadcn's defaults unchanged.

## Colors

There are two green systems in this product, and they are never the same color. Keeping them visually distinct was a deliberate, explicitly-confirmed decision — collapsing "below baseline" (good status news) into the brand's own chrome color would make it impossible to tell, at a glance, whether a green thing on screen is *the product* or *your consumption*.

→ See [mockups/direction-green-eco.html](mockups/direction-green-eco.html) for the finalized palette + structure reference (this direction superseded all other explorations). Spine wins on conflict with any mock.

- **Brand accent — Teal-Green (`{colors.brand-accent}` light / `{colors.brand-accent-dark}` dark)**. Used exclusively for chrome and interactive elements: the app mark/icon, wordmark accent, links, active nav state, and the primary action button (Log Reading trigger). **Never** used to represent a Status state, even when a Status happens to also be "good news." If it's clickable or it's the brand mark, it can be teal-green. If it's telling you about your consumption, it can't be.

- **Status semantic triad** — three colors, one per Pattern Detective state (FR-6), chosen to sit visibly apart from the brand teal in both hue and saturation:
  - **Within range — Sage (`{colors.status-within-range}` light / `{colors.status-within-range-dark}` dark)**. Cool, deliberately desaturated. Nothing to do; the calm, neutral default state.
  - **Below baseline — Emerald (`{colors.status-below-baseline}` light / `{colors.status-below-baseline-dark}` dark)**. The most saturated, most purely "green" color anywhere in the product — earned, because it's the actual good news (using less than expected). It is still visibly distinct from `{colors.brand-accent}` in hue.
  - **Trending — Amber (`{colors.status-trending}` light / `{colors.status-trending-dark}` dark)**. Warm, not alarming. Confirmed sufficient on its own as a "worth a look" signal — a calm hint, not a siren.
  - None of the three triad colors is ever reused for brand chrome, and `{colors.brand-accent}` is never reused for a Status badge, dot, or headline.
  - The raw triad hex above is for dots, chart lines, and larger accents only. Status **badge label text** uses a separate, verified-AA set — `{colors.status-within-range-badge-text}`, `{colors.status-below-baseline-badge-text}`, `{colors.status-trending-badge-text}` and their `-dark` pairs (see Components → Status card) — because the raw triad fails 4.5:1 at small badge-text size against its own `-bg` tint.
  - All three states are now rendered distinctly in [mockups/direction-green-eco.html](mockups/direction-green-eco.html) (an earlier pass mistakenly rendered the "within range" frame in the below-baseline emerald hex and never showed "below baseline" at all — corrected).

- **Red — reserved exclusively for genuine system errors and critical messages.** Never used for Status, never for "trending," even though trending is the closest thing this product has to a warning. The product's tone stays calm rather than alarmist throughout the three-state trend palette; red is held back entirely so that when it *does* appear (a failed save, a broken import), it actually means something went wrong with the system — not "your electricity bill might be high." [ASSUMPTION] No specific error hex was rendered or confirmed this session; DESIGN.md leaves `destructive` un-overridden and inherits shadcn's default rather than inventing one, consistent with the shadcn-delta pattern this document follows.

- **Attractiveness signal — Mint/Clay (`{colors.attractiveness-worth-it}` / `{colors.attractiveness-not-worth-it}`, light and dark pairs)**. A 4th, deliberately separate color system, used exclusively for FR-13's two-way tariff-switch signal (bonus-included / bonus-normalized, always shown together, never toggled). It reuses neither of the two greens already in the product nor destructive/error-red: reusing `{colors.status-below-baseline}` would conflate a tariff verdict with a consumption-status signal on a screen a household can view in the same session as the Dashboard; reusing `{colors.brand-accent}` would conflate it with chrome instead; reusing red/`destructive` would violate the "red means a genuine system error" rule for what is actually a mild stay-put verdict, not a fault. Both signal rows also carry a plain-language verdict word in the badge text ("Worth switching" / "Not worth it"), never color alone, matching the same discipline the Status triad uses. The verdict pill itself passes AA directly off the raw pair, but the supporting explanatory sentence (`.signal-detail`/`.signal-frame-label`) and the "not worth it" figure needed their own verified tokens — `{colors.attractiveness-signal-supporting-text}` / `-dark` and `{colors.attractiveness-not-worth-it-text}` / `-dark` — since the raw pair falls short of 4.5:1 at that text weight against its own row tint. → See [mockups/key-tariff-radar.html](mockups/key-tariff-radar.html) for the two-way signal states and the full color-token reasoning.

- **Text and surface tokens** (`{colors.text-primary}`, `{colors.text-secondary}`, `{colors.text-quiet}`, `{colors.surface-base}`, `{colors.surface-glass}`, etc.) exist purely to render the glass material and typography hierarchy — see Elevation & Depth and Typography below for how they're used.

**Never used for:** brand chrome carrying status meaning, status triad colors appearing in navigation/marks/links, gradients or gradients-as-decoration outside the documented glass-panel system, saturated color competing with the Status card for attention anywhere else on a surface.

## Typography

System font stack throughout (`{typography.font-family}`) — no webfont dependency, consistent with the self-hosted/no-frills ethos (nothing to fetch, nothing to license, nothing to fail to load). There is exactly one typeface family in this product; every role below is a size/weight/spacing variation of it, not a second typeface.

- **`{typography.status-headline}`** — the Status card's headline sentence ("Quiet week." / "Worth a look."). 700-weight, tight tracking, largest text in the product outside of a sheet's own field.
- **`{typography.status-figure}`** — applies `tabular-nums` specifically to Status and kWh figures (the Status card's supporting number, the Log Reading kWh field, Trend chart axis values, per-device kWh figures). This is the one deliberate typographic delta beyond size/weight: it keeps digits from jittering in width as they update, a steady "instrument" rhythm appropriate to a number a household is meant to trust. It is not a separate typeface — same family and weight as body text, tabular-nums is the only rule.
- **`{typography.body}`** / **`{typography.body-secondary}`** — standard running text and secondary/quiet copy (status body sentence, tariff-check microcopy, footer text).
- **`{typography.label-badge}`** — the uppercase status badge label ("WITHIN RANGE", "TRENDING") and similar small caps labels.
- **`{typography.wordmark}`** — the app wordmark in the top bar.

Everything else (form labels, settings rows, dialog titles) inherits shadcn's standard type scale unmodified.

## Layout & Spacing

Mobile-first, responsive web — the primary authored surface is a phone-width screen, because meter-side reading entry (UJ-1) is the product's highest-frequency, highest-stakes interaction and it happens standing at a meter with a phone in hand. Browser/tablet width is the secondary authored surface, used for the calm-evening trend-browsing context (UJ-3) where a wider frame legitimately helps (trend chart, Room → Power Point → Device tree).

Spacing reflects "says less, on purpose": generous, calm gaps rather than a dense grid. `{spacing.card-padding}` (24px) is the standard interior padding for a glass card; `{spacing.card-gap}` (18px) separates stacked panel elements; `{spacing.mobile-margin}` (19px) is the phone-screen edge margin; `{spacing.5}` (24px) is the gap between major sections on a surface (e.g., between the Status card and the Tariff Check card on Dashboard). Tailwind's base 4px scale (`{spacing.1}`–`{spacing.6}`) is inherited as-is for anything not named above — no exotic scale, just used generously rather than packed tight. The app background itself sits on `{colors.surface-base}` / `{colors.surface-base-dark}`, one shade back from every glass panel so the panel stack has somewhere to cast its depth against.

Single-column layout on phone width. The drill-down surfaces (Trend History) widen to a browser/tablet frame but stay single-column-of-cards internally — no dense multi-column dashboard grid; that would work against the product's discipline of the Status card as the one thing that matters most.

## Elevation & Depth

The glass-panel system is the product's signature visual device: a **rear panel** (`{colors.surface-panel-back}` / `{colors.surface-panel-back-dark}`) sits offset behind a **front glass card** (`{colors.surface-glass}` / `{colors.surface-glass-dark}`), creating real z-depth through stacking rather than a flat drop-shadow. The front card is genuinely translucent (`backdrop-filter: blur(28px) saturate(160%)` in dark, `blur(20px) saturate(140%)` in light) — it shows a hint of what's behind it, reinforcing "layered" rather than "flat."

A **specular sweep** — a soft diagonal highlight (`{colors.specular-sweep}` / `{colors.specular-sweep-dark}`) — drifts once across the settled glass panel on each Status card entrance, like light catching a lens. This is a motion behavior, not a static decoration; its timing and the `prefers-reduced-motion` contract are specified in `EXPERIENCE.md`'s Interaction Primitives — this document only owns what it looks like when it plays. → See [mockups/motion-demo.html](mockups/motion-demo.html) for the rendered sweep.

**Light mode does not glow.** Backdrop blur over a bright ground doesn't read as a lit panel the way it does over near-black — there's no dark void for a glow to bloom into. So Light trades glowing highlights for **frosted-white translucency + soft, green-tinted drop shadows**: the same rear/front panel stack and the same specular sweep survive, but as a gentle sheen and a soft shadow "lift" under status dots instead of a halo. Every status/brand color is darkened and desaturated from its dark-mode value specifically to hold AA contrast against a cream/white ground. This is a deliberate, confirmed degradation path, not an oversight — Dark and Light achieve the same depth language through different physical means.

This dark/light structural approach — same panel-stack, same specular sweep, glow-in-dark vs. frosted-shadow-in-light — was established during exploration in [.working/direction-deep-warm-hybrid.html](.working/direction-deep-warm-hybrid.html) (superseded by the final green-eco palette, but its dark/light structural resolution is what the final direction adopted wholesale).

## Shapes

Continuous, generous rounding throughout — the "liquid glass" material reads as soft-edged, not sharp-cornered. Four radii cover the product:

- **`{rounded.sm}`** (14px) — input fields, the Tariff Check prompt card.
- **`{rounded.md}`** (18px) — drill-down cards (Trend chart card, Room → Power Point → Device list card).
- **`{rounded.lg}`** (28px) — the Status card, its rear panel, and dialog-scale surfaces.
- **`{rounded.full}`** (9999px) — pill shapes only: the primary action button, status badges.

No sharp corners anywhere in the custom component layer. The Log Reading sheet is the one asymmetric case: `{rounded.lg}` on its top corners, flush (0px) on the bottom where it docks to the viewport edge — it's a panel sliding up from off-screen, not a floating card.

## Components

### Status card (the hero)

The single most important surface in the product (FR-7) — must never share visual weight with anything else on the Dashboard. Rendered as the front card in the rear/front glass stack: `{components.status-card.radius}` corners, `{components.status-card.background}` / `{components.status-card.background-dark}` fill at `{components.status-card.backdrop-filter}` / `{components.status-card.backdrop-filter-dark}`, `{spacing.card-padding}` interior padding, `{components.status-card.row-gap}` between internal rows. The rear panel behind it uses `{components.status-card.panel-back-offset}` / `-dark` fill with a `{components.status-card.panel-back-border}` / `-dark` hairline. Contents, top to bottom: a status dot + uppercase badge, a headline sentence, and a supporting sentence. The badge (`{typography.label-badge}`) takes its background from `{components.status-card.badge-bg-within-range}`, `{components.status-card.badge-bg-below-baseline}`, or `{components.status-card.badge-bg-trending}` and their `-dark` pairs, depending on state, never the brand accent. Its **label text** pulls from the dedicated `{components.status-card.badge-text-within-range}` / `-below-baseline` / `-trending` tokens and their `-dark` pairs — **not** the raw status triad hex, which fails AA at badge-text size against its own `-bg` tint (measured 2.85–3.98:1). The dot itself still uses the raw `{colors.status-within-range}` / `-below-baseline` / `-trending` triad, since a larger solid dot doesn't have the same small-text contrast problem. The headline sentence is set in `{components.status-card.headline-type}` colored `{components.status-card.headline-color}` / `-dark`; the supporting sentence is set in `{typography.body}` colored `{components.status-card.body-color}` / `-dark`, with any figures in `{components.status-card.figure-type}` (tabular-nums). The specular-sweep overlay (`{components.status-card.specular-overlay}` / `-dark`) plays once on entrance per the motion contract in `EXPERIENCE.md`.

### Log Reading sheet

A sheet/modal presented **over** the Dashboard, not a separate route — the meter-side entry path stays fast (FR-1). `{components.log-reading-sheet.radius-top}` top corners, flush bottom, `{components.log-reading-sheet.background-dark}` fill (dark; light-mode sheet fill was not separately rendered this session — [ASSUMPTION] it follows the same frosted-white/soft-shadow degradation documented under Elevation & Depth for the rest of the glass system), `{components.log-reading-sheet.backdrop-filter-dark}` on the sheet itself, with a backdrop blur behind it over the dashboard. The kWh field uses `{components.log-reading-sheet.field-type}` (tabular-nums) at `{components.log-reading-sheet.field-radius}` corners; date/time is pre-selected to now but editable. Single primary "Save reading" action, `{components.primary-action-button.radius}` shape, matching the pill button system.

### Meter regression prompt

Modal, one level deep — supersedes rather than stacks on an open Log Reading sheet (see `EXPERIENCE.md`'s Micro-Flow: Meter Reading Regression Classification, FR-25). **Neutral/informational treatment, not `destructive`/error-adjacent**: a shadcn Dialog with the same glass-panel language as the rest of the product (`{components.meter-regression-prompt.radius}` corners, `{components.meter-regression-prompt.background}` / `-dark` fill, `{components.meter-regression-prompt.backdrop-filter}` / `-dark`), *not* red — a meter rollover or a fresh meter install is a normal, expected classification step the household resolves in one tap, not a system error being reported to them. Contains the *reset*/*rollover* choice as two clearly-labeled actions (shadcn Button, no custom styling) — no status triad or destructive color anywhere in this component.

### Tariff Check prompt card

Deliberately lower visual weight than the Status card — it must never compete with it. `{components.tariff-check-card.radius}` corners, `{components.tariff-check-card.padding}` interior padding, `{components.tariff-check-card.background}` / `-dark` fill (near-transparent, no glass-blur treatment, no glow, no border emphasis), text in `{components.tariff-check-card.text}` / `-dark` (the quietest text tone in the product) set in `{components.tariff-check-card.type}`. Shown only when a Tariff Check is actually due (FR-15); otherwise the space renders neutral/empty microcopy at the same quiet weight rather than disappearing or being replaced by a fabricated recommendation.

### Trend chart

The Moderate-density treatment is the only shipped default (confirmed this session — no Minimal/Dense toggle). `{components.trend-chart.radius}` card, `{components.trend-chart.background-dark}` fill. The trend line renders in `{components.trend-chart.line-within-range}` / `-dark` for in-range segments and `{components.trend-chart.line-trending}` / `-dark` for segments that crossed into trending — never a fourth chart-specific color. Gaps in the underlying Meter Reading history render as a visible break with a labeled `{components.trend-chart.gap-band}` / `-dark` tint band (never an interpolated line — FR-8). Below the chart, an expandable Room → Power Point → Device list (shadcn `details`/accordion pattern, no custom override) starts collapsed. → See [mockups/density-trend-history.html](mockups/density-trend-history.html) for the Moderate-density decision reference. A Smart Plug import gap (FR-24) is a distinct data path but deliberately reuses this same `{components.trend-chart.gap-band}` / `{colors.status-trending}` "flagged/uncertain" visual vocabulary rather than inventing a new one — see [mockups/key-smart-plug-import.html](mockups/key-smart-plug-import.html) for the async-upload and gap-flagged states.

### Tariff comparison card

Renders the current-vs-candidate tariff summary and the FR-13 two-way attractiveness signal on Tariff Radar. Two stacked `.card` panels (same derived `{rounded.md}` / `{colors.surface-glass}` / `-dark` glass treatment as the Trend chart card — no dedicated surface token, reused deliberately rather than inventing a new one) hold the current tariff's fields and the candidate comparison's fields respectively, each row a label/value pair with `{typography.status-figure}` (tabular-nums) on the value. Beneath them, the signal card renders both attractiveness rows together, never toggled: a "bonus included" row on `{colors.attractiveness-worth-it-bg}` / `-dark` and a "bonus normalized" row on `{colors.attractiveness-not-worth-it-bg}` / `-dark` (or the reverse pairing, depending on the verdict — colors always follow the verdict, not fixed row order), each with a `{rounded.full}` verdict badge in `{colors.attractiveness-worth-it}` / `{colors.attractiveness-not-worth-it}` and a plain-language amount sentence, never color alone. The badge itself passes AA directly off the raw pair; the supporting detail sentence under each badge uses `{colors.attractiveness-signal-supporting-text}` / `-dark`, and the "not worth it" row's amount figure specifically uses `{colors.attractiveness-not-worth-it-text}` / `-dark` rather than the raw `{colors.attractiveness-not-worth-it}` (which falls short of 4.5:1 at that text weight against its own row tint). A comparison candidate is explicitly scratch/exploratory (FR-11) — entering one never changes the real Tariff until an explicit switch action. → See [mockups/key-tariff-radar.html](mockups/key-tariff-radar.html) for both signal states rendered.

### Primary action button (Log Reading trigger)

Pill-shaped (`{components.primary-action-button.radius}`), `{components.primary-action-button.background}` gradient fill, `{components.primary-action-button.foreground}` / `-dark` text — the one place brand-accent green is allowed to be a solid, prominent fill, because it's chrome (an action trigger), not a status. Press state compresses to `{components.primary-action-button.press-scale}` scale with the shadow pulling in — glass being pushed down into the stack, never a color flash. See `EXPERIENCE.md` Interaction Primitives for the full press/motion contract.

### Nav chrome

Active nav item / link state uses `{components.nav-chrome.active-bg}` / `-dark` background with `{components.nav-chrome.active-foreground}` / `-dark` text — brand-accent-tinted chrome, same discipline as everywhere else: this is navigation, never a status signal.

### Everything else

Standard shadcn components (Dialog, Input, Button secondary/outline/ghost variants, Toast, Tabs, `details`/accordion, Table) are used unmodified for Settings, Household/Onboarding, Log Event, and Smart Plug Import surfaces. The brand-layer discipline is: don't customize what doesn't need customizing.

## Do's and Don'ts

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
