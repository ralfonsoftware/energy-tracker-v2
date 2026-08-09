# Spine Pair Review — energy-tracker

## Overall verdict

The spine pair is mechanically clean — every `{token}` reference resolves, section order is canonical in both files, all three PRD UJs are covered verbatim with real climax beats, and the invented Micro-Flow section earns its place. It falls short of a clean pass on three fronts: the Meter Regression modal (a load-bearing, FR-25-invented flow) has a full behavioral spec in EXPERIENCE.md but no visual spec at all in DESIGN.md; a linked mockup (`key-smart-plug-import.html`) visibly violates DESIGN.md's own stated color-discipline rule and neither spine catches it; and several PRD-explicit, UI-relevant requirements (audit-trail correction notes, FR-17 no-correlation state, import-validation failure) have no State/Component Pattern representation. None of these require restructuring — they're additive fixes.

## 1. Flow coverage — strong
Checked UJ-1/2/3 (PRD §2.3) against EXPERIENCE.md Key Flows: all three present, names verbatim, protagonist (Sam) named, numbered steps, explicit **Climax:** beat, edge case given where the PRD source has one (UJ-1, UJ-2; UJ-3 has none in the source either, so its absence there is not a miss).
### Findings
- **low** No Key Flow walks a household member through Log Event → Wattage Plausibility correlation (FR-16/17) — it only appears as an IA row and one Voice-and-Tone copy line (EXPERIENCE.md:30, :48). Not one of the PRD's 3 named UJs, so not a hard miss, but it's the only Should-tier feature with zero flow-level walkthrough. *Fix:* either accept as intentionally out of Key-Flows scope, or add a short flow.

## 2. Token completeness — adequate
Extracted all frontmatter tokens (168 paths) and all `{path.to.token}` refs in both files (99 unique) via script; zero refs fail to resolve. Every color has both light/dark hex except `destructive`, which is deliberately left un-overridden (disclosed ASSUMPTION, consistent with the shadcn-delta pattern).
### Findings
- **medium** No load-bearing color pair states a quantified contrast ratio — only qualitative claims ("chosen to hold AA contrast", DESIGN.md:276; "confirmed this session", EXPERIENCE.md:109). `text-quiet` / `text-quiet-dark` (0.36/0.32 alpha, DESIGN.md:69-70) is the riskiest candidate and is unverified. *Fix:* run/record actual contrast numbers for text-quiet-on-surface-quiet and status-triad-on-surface-glass at minimum.
- **low** `typography.status-figure` (DESIGN.md:103-106) has no `fontSize` — valid per the spec's "any subset" allowance since it's reused at multiple sizes, but worth a one-line note confirming that's intentional, not an omission.
- **low** Attractiveness signal light-mode hex values (DESIGN.md:48-55) are explicitly flagged `[ASSUMPTION]` as inferred, not rendered — already disclosed in-document, listed here only because it's a real gap.

## 3. Component coverage — thin
Extracted every component name across DESIGN.md.Components and EXPERIENCE.md.Component Patterns and cross-checked both directions.
### Findings
- **high** Meter regression prompt / "Meter Reading Regression Classification" has a full behavioral spec and its own Micro-Flow section in EXPERIENCE.md (EXPERIENCE.md:68, :116-126) but **no entry anywhere in DESIGN.md** — not even an inherited-shadcn acknowledgment like the one given to Settings/Household/Log Event/Smart Plug Import (DESIGN.md:321). A downstream builder has zero visual anchor (color, radius, whether it borrows `destructive` red) for a modal the doc itself calls "real, unusual, load-bearing." *Fix:* add a Components row (even a one-liner: inherits shadcn Dialog, `destructive`-adjacent or neutral treatment — pick one and say why).
- **medium** `nav-chrome` has a full visual spec in DESIGN.md.Components (DESIGN.md:190-194, :315-317) but no row in EXPERIENCE.md.Component Patterns, and the navigation pattern itself (bottom tab bar vs. hamburger vs. persistent sidebar, given this is mobile-first) is never described anywhere in the IA section. *Fix:* add a Nav chrome behavioral row and state the mobile nav pattern in Foundation or IA.
- **medium** Wattage Plausibility / Event-correlation display (FR-17) has no Component Pattern or State Pattern row despite multiple PRD-testable UI states (correlation shown / no-deviation-found / many-to-one mapping) — only the IA mention and one Voice-and-Tone line noted above. *Fix:* add a Component Patterns row for the correlation display, at minimum covering the "no correlation found" case.
- **low** Room → Power Point → Device tree is named as its own row in EXPERIENCE.md.Component Patterns (EXPERIENCE.md:63) but has no dedicated DESIGN.md row — only an inline mention under Trend chart (DESIGN.md:305). Functionally fine (unmodified shadcn accordion) but not a clean 1:1 name match.

## 4. State coverage — thin
Walked every IA surface against State Patterns and against Cross-Cutting NFRs / FR consequences that imply a UI state.
### Findings
- **medium** The Cross-Cutting NFR "Audit trail on corrections" (editing a Meter Reading or Tariff entry preserves the original as a visible correction note, PRD Cross-Cutting NFRs) and FR-10's "price fields lock once contract start date has passed; editing requires an explicit override step" have **zero** State/Component Pattern representation in EXPERIENCE.md. Both are explicit, testable, UI-visible PRD requirements. *Fix:* add State Patterns rows for "editing a past Reading/Tariff entry" and "editing a locked Tariff price field."
- **medium** FR-23's "Import validates against the documented v2 export format and rejects/reports malformed data" has no matching State Pattern — only successful-import and Smart-Plug-gap states are covered (EXPERIENCE.md:70-85). *Fix:* add a Data Import failure/validation-error state row.
- **medium** FR-17's "no corresponding observable deviation — not flagged as wrong, shown without a correlation" state is absent from State Patterns (cross-referenced from Component coverage finding above).

## 5. Visual reference coverage — thin
Listed all files: `mockups/` (5, all promoted per `.memlog.md`), `.working/` (5, superseded explorations), `imports/` (empty). Checked inline linkage from both spines.
### Findings
- **critical** `mockups/key-smart-plug-import.html`'s `.processing-pill` and `.complete-check` styles use `#9FBB8A` (`status-within-range-dark`) and `#6FDB93` (`status-below-baseline-dark`) — the Status semantic triad — for an upload-processing badge and a generic completion checkmark (key-smart-plug-import.html:157-163, 187-191), neither of which is a Pattern Detective Status. This directly violates DESIGN.md's own Do's/Don'ts row: "Reuse a status-triad color for chrome, decoration, or a non-status badge" is listed as a **Don't** (DESIGN.md:328). Neither spine flags or reconciles the conflict, and DESIGN.md never links this mock at all, so a spine-only reader has no way to catch it. *Fix:* either recolor the mock to a neutral/brand-chrome treatment, or add an explicit exception to the Do's/Don'ts rule if this reuse is actually intended.
- **medium** `mockups/key-smart-plug-import.html` is linked from EXPERIENCE.md (IA, Component Patterns ×2) but never from DESIGN.md, despite containing real visual decisions (gap-card styling reusing `trend-chart.gap-band` and `status-trending`, per the mock's own inline comments). *Fix:* add a DESIGN.md link/anchor, likely alongside the Trend chart or a new Smart Plug Import components note.
- **medium** `.working/direction-deep-warm-hybrid.html` is credited by `.memlog.md` (line 11) with informing the final green-eco palette's dark/light structural approach, but this lineage is untraceable in either spine — zero mention of it anywhere in DESIGN.md or EXPERIENCE.md. The palette's dark/light degradation logic (DESIGN.md:276) reads as if it landed from nowhere. *Fix:* one sentence of lineage credit in Colors or Elevation & Depth would close this.
- All 5 `mockups/` files are otherwise linked at a relevant section with a specific "what it illustrates" description; no orphans. The other 4 `.working/` files (rejected directions) carry no lineage claim and need none — consistent with being purely superseded.

## 6. Bloat & overspecification — strong
No meaningful bloat. EXPERIENCE.md prose stays behavioral throughout; DESIGN.md carries editorial voice appropriately (per spec allowance) without drifting into decorative narrative untied to a decision. Tables are used where a table earns its place (Do's/Don'ts, Voice and Tone, IA, State/Component Patterns).
### Findings
- **low** "Spine wins on conflict with any mock" is stated three times across both files (EXPERIENCE.md:14, :37; DESIGN.md:232) rather than once. Harmless redundancy, not a real defect.

## 7. Inheritance discipline — strong
`sources` frontmatter resolves in both files to the real brief/PRD paths. UJ names are verbatim from PRD §2.3. Glossary is not restated in either spine (inherited by reference, avoiding drift risk) and spot-checked terms (Household, Meter Reading, Yearly Baseline, Room → Power Point → Device, Tariff Check) are used consistently with PRD definitions. Zero broken `{token}` references (verified programmatically against the full frontmatter tree).
### Findings
- **low** The FR-25 flow is named three slightly different ways across the pair: "Meter Reading Regression Detection" (PRD FR-25 title), "Meter regression prompt" (EXPERIENCE.md Component Patterns row), "Meter Reading Regression Classification" (EXPERIENCE.md Micro-Flow heading). Same concept, no risk of confusion in context, but not a clean verbatim match.

## 8. Shape fit — strong
DESIGN.md section order matches the canonical sequence exactly (Brand & Style → Colors → Typography → Layout & Spacing → Elevation & Depth → Shapes → Components → Do's and Don'ts). EXPERIENCE.md has all required defaults present and in order (Foundation, IA, Voice and Tone, Component Patterns, State Patterns, Interaction Primitives, Accessibility Floor, Key Flows). Dropped optional sections from the example shape (Responsive & Platform breakpoint table, Inspiration & Anti-patterns) are not in the required-defaults list, so their absence is defensible. The invented "Micro-Flow: Meter Reading Regression Classification" section is well-justified by its own opening line (doesn't fit Component or State Patterns) and earns its place — a genuine strength, not overspecification.
### Findings
(none)

## Mechanical notes

- Zero broken cross-refs: 99 unique `{path.to.token}` references across both files, all resolve against DESIGN.md's 168 frontmatter paths (verified by script, not spot-check).
- `imports/` directory exists and is empty — no orphans possible there.
- No Mermaid diagrams present in either spine.
- Frontmatter is complete and well-formed in both files (`name`, `sources`, `status`, `created`, `updated` all present); DESIGN.md additionally carries `description`, matching the spec.
- Component-name casing/naming is otherwise consistent between the two files' Components/Component Patterns tables (Status card, Log Reading sheet, Tariff Check prompt card, Trend chart, Tariff comparison card, Primary action button all match exactly) — the exceptions are called out above (§3, §7).
