/// <reference types="node" />
// This file (unlike the rest of src/, which is browser-only per tsconfig.app.json's `types`) reads
// index.css straight off disk via Node's fs — Vite's `?raw` import suffix silently returns an
// empty string here because @tailwindcss/vite intercepts every .css module, `?raw` included, and
// resolves it through Tailwind's own compiler instead of Vite's raw-text loader.
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { describe, expect, it } from 'vitest'
import { compositeOver, contrastRatio, parseColor, type Rgba } from './lib/contrast'

const CSS_SOURCE = readFileSync(path.join(import.meta.dirname, 'index.css'), 'utf-8')

// Epic 5 retro action item #4 (escalated after three retro cycles deferring this as
// "pre-existing" — 2.2b, 5.3, 5.4): an automated WCAG AA (4.5:1, normal-size text) contrast check
// for every --color-* token pair that has actually been hand-verified in a story so far — the
// Status triad (2.5), --attractiveness-* (5.3), --tariff-check-card-* (5.4). Reads index.css
// directly (not copied literal values) so a future token edit is caught here instead of silently
// reintroducing the exact gap this action item exists to close.
//
// Composited backdrops mirror what each token actually renders on (see the components that
// consume these tokens), not a bare token-vs-token comparison:
// - Status badges and the attractiveness row both render inside a GlassCard, so their -bg tints
//   are composited over --surface-glass over --background (GlassCard's own front-surface stack,
//   see glass-card.tsx) — the same approximation Story 5.3's manual verification used.
// - TariffCheckCard is deliberately plain (no GlassCard, see tariff-check-card.tsx's own
//   comment), so its -bg tint composites directly over --background.
// Real backdrop-blur/backdrop-saturate can shift the true on-screen color slightly from this flat
// composite (it blurs actual page content, not just --background) — accepted here because it's
// the same approximation every prior manual verification in this codebase already used.

const AA_NORMAL_TEXT = 4.5

function extractBlock(css: string, selector: string): string {
  const withoutComments = css.replace(/\/\*[\s\S]*?\*\//g, '')
  const selectorIndex = withoutComments.indexOf(`${selector} {`)
  if (selectorIndex === -1) {
    throw new Error(`Could not find a "${selector} {" block in index.css`)
  }
  const braceStart = withoutComments.indexOf('{', selectorIndex)
  let depth = 0
  for (let i = braceStart; i < withoutComments.length; i++) {
    if (withoutComments[i] === '{') depth++
    if (withoutComments[i] === '}') {
      depth--
      if (depth === 0) return withoutComments.slice(braceStart + 1, i)
    }
  }
  throw new Error(`Unbalanced braces while scanning the "${selector}" block`)
}

function parseTokens(block: string): Map<string, string> {
  const tokens = new Map<string, string>()
  const declarationPattern = /--([a-z0-9-]+)\s*:\s*([^;]+);/gi
  for (const match of block.matchAll(declarationPattern)) {
    tokens.set(match[1], match[2].trim())
  }
  return tokens
}

const lightTokens = parseTokens(extractBlock(CSS_SOURCE, ':root'))
const darkTokens = parseTokens(extractBlock(CSS_SOURCE, '.dark'))

function rawValue(tokens: Map<string, string>, name: string): string {
  const value = tokens.get(name)
  if (value === undefined) {
    throw new Error(`Token --${name} was not found in index.css — has it been renamed?`)
  }
  return value
}

function color(tokens: Map<string, string>, name: string): Rgba {
  return parseColor(rawValue(tokens, name))
}

// Composites token names bottom-to-top (first must be opaque) into a single flat opaque color.
function compositeStack(tokens: Map<string, string>, tokenNamesBottomToTop: string[]): Rgba {
  const [first, ...rest] = tokenNamesBottomToTop
  let result = color(tokens, first)
  if (result.a !== 1) {
    throw new Error(`Bottom of a composite stack must be opaque: --${first}`)
  }
  for (const name of rest) {
    result = compositeOver(color(tokens, name), result)
  }
  return result
}

// --attractiveness-signal-supporting-text is itself semi-transparent (rgba(..., 0.82/0.7)) — its
// on-screen color, and therefore its true contrast ratio, only exists once it's composited over
// whatever it renders on. compositeOver is a no-op for an already-opaque text color, so this is
// safe to call unconditionally for every text token checked below.
function contrastAgainstBg(textColor: Rgba, bgColor: Rgba): number {
  return contrastRatio(compositeOver(textColor, bgColor), bgColor)
}

const GLASS_CARD_BACKDROP = ['background', 'surface-glass']
const PLAIN_PAGE_BACKDROP = ['background']

type Theme = { name: string; tokens: Map<string, string> }
const THEMES: Theme[] = [
  { name: 'light', tokens: lightTokens },
  { name: 'dark', tokens: darkTokens },
]

describe.each(THEMES)('$name theme color-token contrast (WCAG AA, 4.5:1)', ({ tokens }) => {
  describe('Status triad badges (status-card.tsx)', () => {
    it.each(['within-range', 'below-baseline', 'trending'])('%s badge text clears AA against its own badge-bg over the glass card surface', (status) => {
      const textColor = color(tokens, `status-${status}-badge-text`)
      const bgColor = compositeStack(tokens, [...GLASS_CARD_BACKDROP, `status-${status}-badge-bg`])
      expect(contrastAgainstBg(textColor, bgColor)).toBeGreaterThanOrEqual(AA_NORMAL_TEXT)
    })
  })

  describe('Attractiveness signal (tariff-comparison-form.tsx)', () => {
    it.each(['worth-it', 'not-worth-it'])('%s badge text clears AA against its own solid badge fill', (variant) => {
      const textColor = color(tokens, `attractiveness-${variant}-badge-text`)
      const bgColor = color(tokens, `attractiveness-${variant}`)
      expect(contrastAgainstBg(textColor, bgColor)).toBeGreaterThanOrEqual(AA_NORMAL_TEXT)
    })

    it('worth-it figure text (raw token) clears AA against the worth-it row tint over the glass card surface', () => {
      const textColor = color(tokens, 'attractiveness-worth-it')
      const bgColor = compositeStack(tokens, [...GLASS_CARD_BACKDROP, 'attractiveness-worth-it-bg'])
      expect(contrastAgainstBg(textColor, bgColor)).toBeGreaterThanOrEqual(AA_NORMAL_TEXT)
    })

    it('not-worth-it figure text (dedicated -text token) clears AA against the not-worth-it row tint over the glass card surface', () => {
      const textColor = color(tokens, 'attractiveness-not-worth-it-text')
      const bgColor = compositeStack(tokens, [...GLASS_CARD_BACKDROP, 'attractiveness-not-worth-it-bg'])
      expect(contrastAgainstBg(textColor, bgColor)).toBeGreaterThanOrEqual(AA_NORMAL_TEXT)
    })

    it.each(['worth-it', 'not-worth-it'])('signal-supporting-text clears AA against the %s row tint (it renders in both row variants)', (variant) => {
      const textColor = color(tokens, 'attractiveness-signal-supporting-text')
      const bgColor = compositeStack(tokens, [...GLASS_CARD_BACKDROP, `attractiveness-${variant}-bg`])
      expect(contrastAgainstBg(textColor, bgColor)).toBeGreaterThanOrEqual(AA_NORMAL_TEXT)
    })
  })

  describe('Tariff check card (tariff-check-card.tsx, deliberately plain — no GlassCard)', () => {
    it('text-muted-foreground clears AA against the card bg tint over the bare page background', () => {
      const textColor = color(tokens, 'muted-foreground')
      const bgColor = compositeStack(tokens, [...PLAIN_PAGE_BACKDROP, 'tariff-check-card-bg'])
      expect(contrastAgainstBg(textColor, bgColor)).toBeGreaterThanOrEqual(AA_NORMAL_TEXT)
    })
  })
})
