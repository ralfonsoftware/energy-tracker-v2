/// <reference types="node" />
// Epic 8 retro action item #3: the 660px breakpoint and 900px column live in several unrelated
// copies (CSS token, hook, test stubs, Playwright boundary widths, page wrapper classes). This
// reads them off disk and fails naming the stale copy if any drifts. `--breakpoint-wide` in
// index.css is the breakpoint's single source; the column has no CSS source, so its copies must
// agree with each other. Reads via fs (not `?raw`) for the reason given in color-tokens.contrast.test.ts.
import { readdirSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { describe, expect, it } from 'vitest'

const WEB_ROOT = path.join(import.meta.dirname, '..')
const SRC_ROOT = path.join(WEB_ROOT, 'src')
const E2E_ROOT = path.join(WEB_ROOT, 'e2e')

function listFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name)
    return entry.isDirectory() ? listFiles(full) : [full]
  })
}

const rel = (file: string) => path.relative(WEB_ROOT, file)
const isTest = (file: string) => /\.(test|spec)\.tsx?$/.test(file)
const read = (file: string) => readFileSync(file, 'utf-8')
// Prose like "659px" in e2e comments (line or block) must not count as a literal.
const stripLineComments = (source: string) =>
  source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:])\/\/.*$/gm, '$1')

const srcFiles = listFiles(SRC_ROOT).filter((f) => /\.(tsx?|css)$/.test(f))
const e2eFiles = listFiles(E2E_ROOT).filter((f) => f.endsWith('.spec.ts'))
const ownPath = path.join(import.meta.dirname, 'layout-constants.drift.test.ts')

function matches(files: string[], pattern: RegExp, prepare: (s: string) => string = (s) => s) {
  return files.flatMap((file) =>
    [...prepare(read(file)).matchAll(pattern)].map((m) => ({ file: rel(file), value: Number(m[1]) })),
  )
}

const breakpointMatch = read(path.join(SRC_ROOT, 'index.css')).match(/--breakpoint-wide:\s*(\d+)px/)
if (!breakpointMatch) throw new Error('no --breakpoint-wide: Npx found in web/src/index.css')
const N = Number(breakpointMatch[1])

describe('wide breakpoint (--breakpoint-wide in index.css is the source)', () => {
  it('matches the hook query and every min-width media query in src (stubs and assertions included)', () => {
    const found = matches(srcFiles.filter((f) => f !== ownPath), /min-width:\s*(\d+)px/g)
    expect(found.map((m) => m.file), 'expected min-width: Npx in the hook and the matchMedia stub').toEqual(
      expect.arrayContaining([
        rel(path.join(SRC_ROOT, 'hooks/use-wide-breakpoint.ts')),
        rel(path.join(SRC_ROOT, 'test/wide-viewport.ts')),
      ]),
    )
    const stale = found.filter((m) => m.value !== N)
    expect(stale, `min-width copies differ from --breakpoint-wide (${N}px)`).toEqual([])
  })

  it('has e2e boundary literals only at N-1 or N, with both pinned in app-shell.spec.ts', () => {
    expect(e2eFiles.map(rel), 'e2e/app-shell.spec.ts not found').toContain('e2e/app-shell.spec.ts')
    const literals = matches(e2eFiles, /\b(6\d\d)\b/g, stripLineComments)
    const stale = literals.filter((m) => m.value !== N - 1 && m.value !== N)
    expect(stale, `e2e literals 600-699 that are neither ${N - 1} nor ${N} (stale breakpoint copy?)`).toEqual([])

    const appShell = literals.filter((m) => m.file === 'e2e/app-shell.spec.ts').map((m) => m.value)
    expect(appShell, `app-shell.spec.ts must exercise the exact boundary (${N - 1}px and ${N}px)`).toEqual(
      expect.arrayContaining([N - 1, N]),
    )
  })
})

describe('content column width (all copies must agree)', () => {
  const wrappers = matches(
    srcFiles.filter((f) => !isTest(f)),
    /wide:max-w-\[(\d+)px\]/g,
  )
  // Most common value, so one odd page is the one blamed rather than whichever sorts first.
  const counts = new Map<number, number>()
  for (const m of wrappers) counts.set(m.value, (counts.get(m.value) ?? 0) + 1)
  const C = [...counts.entries()].sort((a, b) => b[1] - a[1])[0]?.[0]

  it('is set on at least the four page wrappers with one value', () => {
    const files = [...new Set(wrappers.map((m) => m.file))]
    expect(files.length, `expected >=4 pages with wide:max-w-[Cpx], found: ${files.join(', ')}`).toBeGreaterThanOrEqual(4)
    const differing = wrappers.filter((m) => m.value !== C)
    expect(differing, `page wrappers disagree with the majority column width (${C}px)`).toEqual([])
  })

  it('matches the e2e column-width assertions', () => {
    const asserted = matches(e2eFiles, /wideColumnBox\?\.width\)\.toBeCloseTo\((\d+)/g, stripLineComments).filter(
      (m) => m.file === 'e2e/app-shell.spec.ts',
    )
    expect(asserted.length, 'no wideColumnBox width toBeCloseTo(C) assertions found in app-shell.spec.ts').toBeGreaterThan(0)
    expect(asserted.filter((m) => m.value !== C), `e2e column assertions differ from wrappers (${C}px)`).toEqual([])
  })
})
