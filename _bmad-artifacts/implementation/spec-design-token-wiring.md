---
title: 'Wire DESIGN.md tokens into the live theme'
type: 'chore'
created: '2026-08-15'
status: 'done'
review_loop_iteration: 0
context: []
baseline_commit: '1bbc551017e0786b2a24c926f87ecca5f1e3e537'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `web/src/index.css` still ships stock shadcn grayscale defaults and a Geist webfont import. DESIGN.md (final, `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN.md`) specifies the real brand palette (green-eco, teal `brand-accent`), a system font stack (explicitly "no webfont dependency"), and a canonical focus ring — none of it reaches the running app. Separately, nothing anywhere applies the `.dark` class, so `index.css`'s `.dark` block is dead code and the app is pinned to light mode.

**Approach:** Remap only the shadcn CSS variables DESIGN.md explicitly claims as its brand delta (`--background`, `--foreground`, `--primary`, `--primary-foreground`, `--ring`) onto DESIGN.md's tokens in both `:root` and `.dark`; drop the Geist import for the system font stack; add a small `prefers-color-scheme` detector that toggles `.dark` on `<html>` automatically (no manual toggle). Existing Epic 1 UI (household creation/invite, settings, room/power-point/device mgmt) needs no direct edits — it already consumes these theme variables with zero hardcoded colors, confirmed by grep.

## Boundaries & Constraints

**Always:**
- Only override shadcn variables DESIGN.md names as its delta: `--background`/`--foreground` → `colors.surface-base`/`colors.text-primary` (+`-dark` pairs), `--primary`/`--primary-foreground` → `colors.brand-accent`/`colors.brand-accent-foreground`, `--ring` → `colors.focus-ring` (DESIGN.md: "the one canonical `:focus-visible` treatment on every interactive element").
- Leave `--card`, `--popover`, `--secondary`, `--muted`, `--accent`, `--destructive`, `--border`, `--input`, `--chart-*`, `--sidebar-*`, and `--radius` untouched — DESIGN.md does not name them as a delta; its `rounded.*` scale and glass system are scoped to named custom components (Status card, Trend chart, etc.) that don't exist yet (Epic 2+).
- `prefers-color-scheme` detection must run once on load and re-apply live on OS-level scheme changes (`matchMedia(...).addEventListener('change', …)`), with no manual toggle UI.
- Remove the now-unused `@fontsource-variable/geist` dependency via `npm uninstall` (updates `package.json` + `package-lock.json` together), not by hand-editing.

**Ask First:** None identified — both open questions (dark-mode activation strategy, webfont-vs-system-stack) were already resolved with Ralf before this spec was written.

**Never:** Do not touch the `mockups/*.html` reference files. Do not add a manual dark/light toggle control. Do not restyle individual Epic 1 components directly — the theme-variable remap is the entire delta.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| OS in light mode on load | `matchMedia('(prefers-color-scheme: dark)').matches === false` | `<html>` has no `.dark` class; `:root` light tokens render | N/A |
| OS in dark mode on load | `matches === true` | `.dark` class applied before first paint-relevant render; dark tokens render | N/A |
| OS scheme flips while app is open | `change` event fires on the media query | `.dark` class toggles live, no reload needed | N/A |

</frozen-after-approval>

## Code Map

- `web/src/index.css` -- `:root`/`.dark` variable blocks + `@import` list -- the theme surface being remapped
- `web/src/lib/color-scheme.ts` (new) -- houses the `prefers-color-scheme` detector
- `web/src/main.tsx` -- app entry point -- wires the detector in before first render
- `web/package.json` -- drops the unused Geist dependency

## Tasks & Acceptance

**Execution:**
- [x] `web/src/index.css` -- remove `@import "@fontsource-variable/geist";`; set `--font-sans: -apple-system, BlinkMacSystemFont, "SF Pro Display", "Segoe UI", Roboto, Helvetica, Arial, sans-serif;` -- matches DESIGN.md's system-stack rationale (no webfont dependency)
- [x] `web/src/index.css` -- in `:root`, set `--background` to DESIGN.md `colors.surface-base` (`#F3F8ED`), `--foreground` to `colors.text-primary` (`#1E2A1C`), `--primary`/`--primary-foreground` to `colors.brand-accent`/`-foreground` (`#1E7A61`/`#FFFFFF`), `--ring` to `colors.focus-ring` (`#1E7A61`) -- installs the light-mode brand delta
- [x] `web/src/index.css` -- in `.dark`, set the same five variables to DESIGN.md's `-dark` pairs (`surface-base-dark #12201A`, `text-primary-dark #EAF5EE`, `brand-accent-dark #2FB397`, `brand-accent-foreground-dark #06120D`, `focus-ring-dark #8FE9CE`) -- installs the dark-mode brand delta
- [x] `web/src/lib/color-scheme.ts` (new) -- export `initColorScheme()`: reads `window.matchMedia('(prefers-color-scheme: dark)')`, applies `.dark` on `document.documentElement` immediately, and subscribes to the query's `change` event to keep it live -- makes `.dark` reachable per Ralf's "detect, no manual toggle" decision
- [x] `web/src/main.tsx` -- call `initColorScheme()` before `createRoot(...).render(...)` -- activates the detector at startup
- [x] `web/package.json` -- run `npm uninstall @fontsource-variable/geist` from `web/` -- removes the dependency now that nothing imports it

**Acceptance Criteria:**
- Given the OS is set to dark mode, when the app loads, then `<html>` carries the `.dark` class and the rendered background/text/primary-button colors match DESIGN.md's `-dark` token values.
- Given the OS is set to light mode, when the app loads, then no `.dark` class is present and colors match DESIGN.md's light token values.
- Given the app is open, when the OS scheme is switched, then the theme updates live without a page reload.
- Given any Epic 1 screen (household creation, invite, settings, room/power-point/device mgmt), when rendered after this change, then no visual regression beyond the intended palette/font swap (structure, spacing, and copy unchanged).

## Spec Change Log

## Design Notes

`--card`, `--popover`, `--border`, `--input`, etc. are deliberately left on shadcn's neutral defaults. DESIGN.md's glass system (`surface-glass`, `surface-panel-back`, backdrop-filter blur) is defined for specific not-yet-built components (Status card, Trend chart, Tariff comparison card) — applying it to generic `Card`/`Dialog`/`Input` now would invent an unspecified look DESIGN.md never approved for those elements. When Epic 2+ builds those components, they'll consume the glass tokens directly rather than through the generic shadcn variables touched here.

## Verification

**Commands:**
- `cd web && npm run build` -- expected: TypeScript + Vite build succeeds with no errors
- `cd web && npm run lint` -- expected: oxlint passes clean
- `cd web && npm run test` -- expected: existing Vitest suite (`App.test.tsx`, `tagging-scaffold-manager.test.tsx`) still passes

**Manual checks (if no CLI):**
- Load the app in a browser with OS dark mode on, then light mode, confirm the palette swaps and matches `mockups/direction-green-eco.html` in each mode.

## Suggested Review Order

**Brand token remap (the core intent)**

- The five shadcn variables DESIGN.md claims as its delta, remapped in `:root` — background/foreground to `surface-base`/`text-primary`, primary to `brand-accent`, ring to `focus-ring`.
  [`index.css:50`](../../web/src/index.css#L50)

- Same five remapped for `.dark` to DESIGN.md's `-dark` pairs — Dark and Light as equal citizens, not a derived fallback.
  [`index.css:91`](../../web/src/index.css#L91)

- Webfont import dropped, `--font-sans` switched to DESIGN.md's system stack per its explicit "no webfont dependency" rationale.
  [`index.css:9`](../../web/src/index.css#L9)

**Dark-mode activation (review-driven addition — no manual toggle yet)**

- `initColorScheme()`: applies `.dark` from `prefers-color-scheme` on load, keeps it live on OS `change` events.
  [`color-scheme.ts:8`](../../web/src/lib/color-scheme.ts#L8)

- Synchronous inline check in `<head>`, ahead of first paint, so the OS-dark case never flashes light before `color-scheme.ts` takes over — added after Blind Hunter review flagged the FOUC risk in the original mount-time-only approach.
  [`index.html:14`](../../web/index.html#L14)

- `theme-color` meta pair so mobile browser chrome (status bar) matches the active palette instead of staying default — added from the same review pass.
  [`index.html:7`](../../web/index.html#L7)

- `color-scheme` CSS property set per mode so native form controls/scrollbars follow the applied theme rather than guessing from the OS — same review pass.
  [`index.css:53`](../../web/src/index.css#L53)

- Detector wired in before first render.
  [`main.tsx:8`](../../web/src/main.tsx#L8)

**Peripherals**

- Unit coverage for the detector: initial light/dark state and the live `change` listener.
  [`color-scheme.test.ts:1`](../../web/src/lib/color-scheme.test.ts#L1)

- Unused `@fontsource-variable/geist` dependency removed now that nothing imports it.
  [`package.json`](../../web/package.json)
