# Validation Report — energy-tracker

- **DESIGN.md:** `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN.md`
- **EXPERIENCE.md:** `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md`
- **Run at:** 2026-08-09T00:00:00Z

## Overall verdict

The spine pair is mechanically clean — every `{token}` reference resolves, section order is canonical in both files, all three PRD user journeys are covered verbatim with real climax beats, and the invented Micro-Flow section for meter regression earns its place. It falls short of a clean pass on three structural fronts: the Meter Regression modal has a full behavioral spec but no visual spec anywhere in DESIGN.md; a linked mockup visibly violates DESIGN.md's own color-discipline rule and neither spine catches it; and several PRD-explicit, UI-relevant requirements (audit-trail correction notes, FR-17's no-correlation state, import-validation failure) have no State/Component Pattern representation.

The accessibility lens shifts the picture in one important way: this isn't just a documentation-completeness gap. The color system has a systemic, measured contrast problem — secondary/quiet text tokens fail AA by a wide margin (2.1–2.9:1 against a 4.5:1 requirement) in real functional copy, and the Status badge text as literally specified in DESIGN.md also fails AA, while the rendered mockups quietly substitute different, undocumented, compliant colors instead. Since the spine is stated to win on conflict with any mock, a developer building strictly from the documented tokens would ship the failing colors, not the working ones. None of this requires a redesign — it requires re-tuning specific token values against measured, composited contrast before status is set to final.

## Category verdicts
- Flow coverage — strong
- Token completeness — adequate
- Component coverage — thin
- State coverage — thin
- Visual reference coverage — thin
- Bloat & overspecification — strong
- Inheritance discipline — strong
- Shape fit — strong

## Findings by severity

### Critical (3)
**Visual reference coverage** — Smart Plug Import mock reuses Status-triad colors for non-status UI (mockups/key-smart-plug-import.html:157-163,187-191 vs. DESIGN.md:328)
`.processing-pill`/`.complete-check` use status-triad hex for a non-status badge/checkmark, violating DESIGN.md's own Do's/Don'ts rule; DESIGN.md never links the mock so the conflict is invisible to a spine-only reader.
Fix: Recolor the mock to a neutral/brand-chrome treatment, or add an explicit exception to the rule.

**Accessibility** — Secondary/quiet body text fails AA systemically in both modes (DESIGN.md Colors: text-quiet/text-secondary; mockups/direction-green-eco.html)
Measured against actual composited backgrounds: light ≈2.1–2.5:1, dark ≈2.7–2.9:1, vs. the 4.5:1 requirement — in real functional copy (Tariff Check line, footers), not decoration.
Fix: Re-tune lightness/alpha against actual composited backgrounds until every real-copy use clears 4.5:1.

**Accessibility** — Status badge text fails AA as specified; mockups quietly use different, undocumented colors (DESIGN.md Components → status-card; mockups/direction-green-eco.html:196-238)
Documented raw status-triad hex measures 2.85–3.98:1 as badge text; the mockup instead uses undocumented, compliant hex (4.86–7.72:1). A dev building strictly from the spine would ship the failing colors.
Fix: Add explicit `status-*-badge-text`/`-dark` tokens capturing the mockup's actual working values.

### High (2)
**Component coverage** — Meter Regression prompt has full behavioral spec, zero visual spec (EXPERIENCE.md:68,116-126 / DESIGN.md absent)
A modal the doc itself calls "load-bearing" has no DESIGN.md entry at all — no color, radius, or destructive-red decision documented.
Fix: Add a Components row, even a one-liner.

**Accessibility** — "Within range" frame renders using the "below baseline" hex (mockups/direction-green-eco.html vs. DESIGN.md Colors)
The documented sage `status-within-range` token never appears in any rendered mockup; "below baseline" is never shown at all. Risks wrong-hue-to-wrong-state implementation.
Fix: Render/spec-correct an explicit "below baseline" frame using the sage token.

### Medium (9)
**Token completeness** — No quantified contrast ratio stated for any load-bearing color pair (DESIGN.md:276; EXPERIENCE.md:109). Fix: record measured contrast numbers for the riskiest pairs.
**Component coverage** — `nav-chrome` has visual spec but no behavioral row; mobile nav pattern (tab bar/hamburger/sidebar) never described (DESIGN.md:190-194,315-317). Fix: add behavioral row + state the pattern in Foundation/IA.
**Component coverage** — Wattage Plausibility correlation display has no Component/State Pattern row (EXPERIENCE.md absent). Fix: add a row covering at minimum the "no correlation found" case.
**State coverage** — Audit-trail correction notes and locked-tariff-field editing have zero UI representation (EXPERIENCE.md absent). Fix: add State Patterns rows for both.
**State coverage** — Import validation-failure state (FR-23) is missing (EXPERIENCE.md:70-85). Fix: add a Data Import failure/validation-error row.
**State coverage** — FR-17 "no observable deviation" state is absent (EXPERIENCE.md absent). Fix: add alongside the new Wattage Plausibility row.
**Visual reference coverage** — key-smart-plug-import.html never linked from DESIGN.md despite real visual decisions inside it. Fix: add a DESIGN.md link.
**Visual reference coverage** — direction-deep-warm-hybrid.html's influence on the final palette is untraceable in either spine (.memlog.md:11 credits it; spines: no mention). Fix: one sentence of lineage credit.
**Accessibility** — FR-13's explanatory copy falls short of AA in the one rendered state (mockups/key-tariff-radar.html .signal-detail/.signal-frame-label/.signal-amount). Fix: lighten/darken per-mode, re-check against actual composited background.

### Low (7)
**Flow coverage** — No Key Flow walkthrough for Log Event → Wattage Plausibility correlation (EXPERIENCE.md:30,48). Fix: accept as out of scope, or add a short flow.
**Token completeness** — `typography.status-figure` has no `fontSize` (DESIGN.md:103-106). Fix: one-line confirmation it's intentional.
**Token completeness** — Attractiveness-signal light-mode hex values are inferred, already disclosed as `[ASSUMPTION]` (DESIGN.md:48-55). Fix: render/confirm when convenient.
**Component coverage** — Room → Power Point → Device tree has no dedicated DESIGN.md row (EXPERIENCE.md:63 / DESIGN.md:305). Fix: optional, add matching named row.
**Bloat** — "Spine wins on conflict" stated three times across both files. Fix: optional consolidation.
**Inheritance discipline** — FR-25 flow named three slightly different ways across the pair. Fix: optional naming consolidation.
**Accessibility** — Trend chart has no structured text alternative for its trajectory (EXPERIENCE.md Component Patterns → Trend chart). Fix: labelled `role="img"` + aria-label, or a collapsed text-table equivalent.
**Accessibility** — No documented focus-indicator visual token (DESIGN.md absent; motion-demo.html one-off outline). Fix: promote a canonical `focus-ring`/`focus-ring-dark` token.
**Accessibility** — i18n/locale NFR not referenced in either spine (PRD Cross-Cutting NFRs). Fix: add a line to Accessibility Floor re: de-DE label-length headroom.
**Accessibility** — Primary action button renders a hair under its own stated 44×44pt minimum, still clears WCAG 2.2's actual 24×24px minimum. Fix: optional padding bump.

## Reviewer files
- `review-rubric.md`
- `review-accessibility.md`
