import { expect, test, type Page } from '@playwright/test'

// Epic 8 retro action item #2: keyboard tab order vs visual order at >=660px (WCAG 2.4.3).
// Epic 8 reorders content visually (`wide:order-N` Settings sections,
// `wide:flex-row` field pairs, CSS grids) while the DOM stays put. For each reordered surface we
// tag its *groups* (a Settings section, a form field, a grid tile), press the real Tab key through
// the page, and compare the order groups are first reached in against their visual reading order
// (row by row, left to right, computed from layout). Groups, not single controls, because two
// side-by-side blocks of several fields each legitimately tab column by column.
//
// NavChrome is guarded too: its top variant is mounted first in <main> and its bottom variant last
// (Story 10.3), so at >=660px the nav is both visually first and first in tab order, and on phones
// the bottom bar is visually and tab-order last. No CSS `order` is involved.

type Screen = 'dashboard' | 'trendHistory' | 'tariffRadar' | 'settings'

const NAV_LABEL: Record<Screen, string | null> = {
  dashboard: null,
  trendHistory: 'Trend History',
  tariffRadar: 'Tariff Radar',
  settings: 'Settings',
}

const CONTENT_SLOT: Record<Screen, string> = {
  dashboard: 'dashboard-content',
  trendHistory: 'trend-history-content',
  tariffRadar: 'tariff-radar-content',
  settings: 'settings-content',
}

const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })

async function fakeApi(page: Page) {
  // Registered first so the specific routes below take precedence.
  await page.route('**/api/**', (route) => route.fulfill(json([])))
  await page.route('**/api/session', (route) =>
    route.fulfill(
      json({
        hasHousehold: true,
        householdId: '11111111-1111-1111-1111-111111111111',
        locale: 'en-US',
        currency: 'EUR',
        supportsFederatedLogout: true,
        email: 'ralf@example.com',
      }),
    ),
  )
  await page.route('**/api/status', (route) =>
    route.fulfill(json({ status: 'withinRange', paceToDateKwh: 1234.5, baselineToDateKwh: 1300, isLowConfidence: false })),
  )
  await page.route('**/api/tariff-check', (route) => route.fulfill(json({ isDue: true, gateOpensAtUtc: '2026-09-01T00:00:00+00:00' })))
  await page.route('**/api/meter-regression-prompts/open', (route) => route.fulfill(json(null)))
  await page.route('**/api/rooms', (route) =>
    route.fulfill(json([1, 2, 3].map((n) => ({ id: `33333333-3333-3333-3333-33333333333${n}`, name: `Room ${n}`, archivedAt: null })))),
  )
  await page.route('**/api/meter-readings?*', (route) =>
    route.fulfill(
      json({
        items: [1, 2, 3].map((n) => ({
          id: `11111111-1111-1111-1111-11111111111${n}`,
          kwhValue: 1000 * n + 0.5,
          readingTimestamp: `2026-08-1${n}T14:32:00+00:00`,
          version: 0,
          isPendingRegression: false,
          correctedFromKwhValue: null,
          correctedAtUtc: null,
        })),
        totalCount: 3,
        page: 1,
        pageSize: 20,
      }),
    ),
  )
  await page.route('**/api/events?*', (route) =>
    route.fulfill(
      json({
        items: [1, 2, 3].map((n) => ({
          id: `22222222-2222-2222-2222-22222222222${n}`,
          description: `event ${n}`,
          occurredAt: `2026-08-1${n}T10:00:00+00:00`,
          taggedEntityType: null,
          taggedEntityName: null,
          correlationDirection: null,
        })),
        totalCount: 3,
        page: 1,
        pageSize: 20,
      }),
    ),
  )
  await page.route('**/api/tariffs?*', (route) =>
    route.fulfill(
      json({
        items: [0, 1, 2].map((n) => ({
          id: `11111111-1111-1111-1111-11111111111${n + 1}`,
          monthlyBaseFee: 12 + n,
          pricePerKwh: 0.3,
          currency: 'EUR',
          contractStartDate: `${2026 - n}-01-15T00:00:00.000Z`,
          contractPeriodMonths: 12,
          version: 0,
          isCurrent: n === 0,
          effectiveUntil: n === 0 ? null : `${2027 - n}-01-14T00:00:00.000Z`,
          corrections: [],
        })),
        totalCount: 3,
        page: 1,
        pageSize: 20,
      }),
    ),
  )
}

async function openScreen(page: Page, screen: Screen, width: number) {
  await page.emulateMedia({ reducedMotion: 'reduce' })
  await fakeApi(page)
  await page.setViewportSize({ width, height: 900 })
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Energy Tracker' })).toBeVisible()
  const label = NAV_LABEL[screen]
  if (label) await page.getByRole('button', { name: label, exact: true }).first().click()
  await expect(page.locator(`div[data-slot="${CONTENT_SLOT[screen]}"]`)).toBeVisible()
  // Faked API: let async sections finish rendering so Tab never walks a half-loaded page.
  await page.waitForLoadState('networkidle')
}

interface Rect {
  left: number
  right: number
  top: number
  bottom: number
}

interface Stop {
  name: string
  group: string | null
  groupRect: Rect | null
  nav: 'top' | 'bottom' | null
  rect: Rect
}

// Tags group wrappers so each Tab stop can report which group it belongs to. A spec either names
// Settings section labels (the wrapper is the label's parent) or form-field ids (the wrapper is the
// field's <label> parent). A silent miss (an unmatched entry) fails the test instead of passing
// vacuously.
type GroupSpec = { sectionLabels: string[] } | { fieldIds: string[] }

async function tagGroups(page: Page, spec: GroupSpec, names: string[]) {
  const missing = await page.evaluate(
    ({ spec, names }) => {
      const wrappers: (Element | null | undefined)[] =
        'sectionLabels' in spec
          ? spec.sectionLabels.map(
              (text) =>
                Array.from(document.querySelectorAll('h2')).find(
                  (h) => h.textContent?.trim() === text && h.className.includes('tracking-[0.6px]'),
                )?.parentElement,
            )
          : spec.fieldIds.map((id) => document.querySelector(`label[for="${id}"]`)?.parentElement)
      const missed: string[] = []
      wrappers.forEach((el, i) => (el ? el.setAttribute('data-tab-group', names[i]) : missed.push(names[i])))
      return missed
    },
    { spec, names },
  )
  expect(missing, 'every group wrapper must exist (a miss would make the check vacuous)').toEqual([])
}

// Presses the real Tab key through the whole page (from document start) until focus leaves the
// document or repeats, collecting page-absolute geometry for every stop.
async function tabThrough(page: Page, max = 250): Promise<Stop[]> {
  await page.evaluate(() => {
    ;(document.activeElement as HTMLElement | null)?.blur()
    window.scrollTo(0, 0)
  })
  const stops: Stop[] = []
  const seen = new Set<string>()
  let sameElementRun = 0
  let ended = false
  for (let i = 0; i < max; i++) {
    await page.keyboard.press('Tab')
    const stop = await page.evaluate((): (Stop & { key: string }) | null => {
      const el = document.activeElement as HTMLElement | null
      if (!el || el === document.body || el === document.documentElement) return null
      const r = el.getBoundingClientRect()
      const labelFor = el.id ? document.querySelector(`label[for="${el.id}"]`) : null
      const name = (el.getAttribute('aria-label') ?? labelFor?.textContent ?? el.textContent ?? '').trim().replace(/\s+/g, ' ').slice(0, 40) || `#${el.id || '(no id)'}`
      const navEl = el.closest('nav[data-slot]')
      const nav = navEl?.getAttribute('data-slot') === 'nav-chrome-top' ? 'top' : navEl?.getAttribute('data-slot') === 'nav-chrome-bottom' ? 'bottom' : null
      let path = ''
      for (let n: Element | null = el; n && n !== document.body; n = n.parentElement) {
        path = `${Array.from(n.parentElement?.children ?? []).indexOf(n)}/${path}`
      }
      const groupEl = el.closest('[data-tab-group]')
      const g = groupEl?.getBoundingClientRect()
      return {
        key: path,
        name: `${el.tagName.toLowerCase()}${el.getAttribute('role') ? `[${el.getAttribute('role')}]` : ''} "${name}"`,
        group: groupEl?.getAttribute('data-tab-group') ?? null,
        groupRect: g ? { left: g.left + scrollX, right: g.right + scrollX, top: g.top + scrollY, bottom: g.bottom + scrollY } : null,
        nav,
        rect: { left: r.left + scrollX, right: r.right + scrollX, top: r.top + scrollY, bottom: r.bottom + scrollY },
      }
    })
    if (!stop) {
      ended = true
      break
    }
    // A multi-part control (type=date segments) keeps focus on one element across several Tabs;
    // more than a handful of presses on one element is a focus trap, not segments.
    if (stop.key === stops.at(-1)?.key) {
      expect(++sameElementRun, `focus is stuck on ${stop.name}`).toBeLessThanOrEqual(8)
      continue
    }
    sameElementRun = 0
    if (seen.has(stop.key)) {
      ended = true
      break
    }
    seen.add(stop.key)
    stops.push(stop)
  }
  expect(ended, `Tab walk hit the ${max}-press cap without leaving the page; stops after ${stops.at(-1)?.name} were not checked`).toBe(true)
  expect(stops.length, 'Tab should reach at least one control').toBeGreaterThan(0)
  return stops
}

// Groups in the order Tab first reaches them; each group's stops must be contiguous, so a group
// interleaved with another (a real focus-order jump) is reported rather than hidden.
function tabOrderOfGroups(stops: Stop[]): string[] {
  const order: string[] = []
  let current: string | null = null
  for (const s of stops) {
    if (!s.group) {
      current = null // an ungrouped stop in between still counts as leaving the group
      continue
    }
    if (s.group !== current) {
      expect(order, `group "${s.group}" is split: Tab left it and came back (after "${order.at(-1)}")`).not.toContain(s.group)
      order.push(s.group)
      current = s.group
    }
  }
  return order
}

// Groups in visual reading order: row by row (vertically overlapping boxes are one row), left to right.
function visualOrderOfGroups(stops: Stop[], names: string[]): string[] {
  const boxes = names.map((name) => {
    const rect = stops.find((s) => s.group === name)?.groupRect
    expect(rect, `group "${name}" has a measured box`).toBeTruthy()
    return { name, rect: rect! }
  })
  // Cluster into rows by top edge (a box joins the current row if it overlaps the row's first box
  // vertically), then read each row left to right. Deterministic, unlike a pairwise-overlap sort.
  const sameRow = (a: Rect, b: Rect) => Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top) > 4
  const rows: typeof boxes[] = []
  for (const box of [...boxes].sort((a, b) => a.rect.top - b.rect.top || a.rect.left - b.rect.left)) {
    const row = rows.at(-1)
    if (row && sameRow(row[0].rect, box.rect)) row.push(box)
    else rows.push([box])
  }
  return rows.flatMap((row) => row.sort((a, b) => a.rect.left - b.rect.left)).map((b) => b.name)
}

function expectTabOrderMatchesVisual(stops: Stop[], expected: string[]) {
  const tab = tabOrderOfGroups(stops)
  expect([...tab].sort(), 'every tagged group must contain a Tab stop (otherwise the check is vacuous)').toEqual([...expected].sort())
  const visual = visualOrderOfGroups(stops, expected)
  expect(tab, `Tab order ${tab.join(' → ')} must match visual order ${visual.join(' → ')}`).toEqual(visual)
}

const WIDE_WIDTHS = [660, 900]

// --- Settings: wide:order-1..4 wrappers, the AI Plausibility + Household pair ---------------------
// Wide only: the section labels used to tag groups are `hidden wide:block`, and below 660px the
// wrappers are plain DOM-order stacks with no reordering to verify.

const SETTINGS_SECTIONS = ['Yearly Baseline', 'AI Plausibility Check', 'Household', 'Rooms, Power Points & Devices', 'Data']

for (const width of WIDE_WIDTHS) {
  test(`Settings sections tab in visual order at ${width}px`, async ({ page }) => {
    await openScreen(page, 'settings', width)
    await page.getByText('Room 1', { exact: true }).first().waitFor()
    await tagGroups(page, { sectionLabels: SETTINGS_SECTIONS }, SETTINGS_SECTIONS)
    const stops = await tabThrough(page)
    expectTabOrderMatchesVisual(stops, SETTINGS_SECTIONS)

    // The pair really sits side by side (otherwise a stacked layout would pass for the wrong reason).
    const ai = stops.find((s) => s.group === 'AI Plausibility Check')!.groupRect!
    const household = stops.find((s) => s.group === 'Household')!.groupRect!
    expect(Math.abs(ai.top - household.top), 'AI Plausibility and Household share a row at >=660px').toBeLessThan(8)
    expect(household.left, 'Household sits to the right of AI Plausibility').toBeGreaterThan(ai.right - 1)
  })
}

// --- Tariff Radar: paired fields (add-tariff + compare) and the tariff history grid ----------------

const TARIFF_FIELDS = [
  ['tariff-monthly-base-fee', 'Add: base fee'],
  ['tariff-price-per-kwh', 'Add: price per kWh'],
  ['tariff-currency', 'Add: currency'],
  ['tariff-contract-start-date', 'Add: contract start'],
  ['tariff-contract-period', 'Add: contract period'],
  ['tariff-compare-monthly-base-fee', 'Compare: candidate base fee'],
  ['tariff-compare-price-per-kwh', 'Compare: candidate price per kWh'],
  ['tariff-compare-switching-bonus', 'Compare: switching bonus'],
] as const

// 659px is included: stacked, visual order equals DOM order, and the fields must not be reordered by a stray `wide:` class.
for (const width of [659, ...WIDE_WIDTHS]) {
  test(`Tariff Radar paired fields tab in visual order at ${width}px`, async ({ page }) => {
    await openScreen(page, 'tariffRadar', width)
    await expect(page.locator('#tariff-compare-switching-bonus')).toBeVisible()
    const names = TARIFF_FIELDS.map(([, name]) => name)
    await tagGroups(page, { fieldIds: TARIFF_FIELDS.map(([id]) => id) }, names)
    const stops = await tabThrough(page)
    expectTabOrderMatchesVisual(stops, names)

    // Fee and price are really a side-by-side pair at >=660px (stacked below it).
    const fee = stops.find((s) => s.group === 'Add: base fee')!.groupRect!
    const price = stops.find((s) => s.group === 'Add: price per kWh')!.groupRect!
    if (width >= 660) {
      expect(price.left, 'price field sits to the right of the fee field').toBeGreaterThan(fee.right - 1)
      expect(Math.abs(price.top - fee.top), 'fee and price share a row').toBeLessThan(8)
    } else {
      expect(price.top, 'price field is stacked below the fee field').toBeGreaterThan(fee.bottom - 1)
    }
  })
}

// --- Entry grids (Trend History readings + events, Tariff Radar history) --------------------------

const GRIDS: { screen: Screen; wait: string; minTiles: number }[] = [
  { screen: 'trendHistory', wait: '1,000.5 kWh', minTiles: 3 },
  { screen: 'tariffRadar', wait: 'Tariff history', minTiles: 3 },
]

for (const { screen, wait, minTiles } of GRIDS) {
  // Wide only: below 660px these lists render as plain stacks, not `entry-grid`.
  for (const width of WIDE_WIDTHS) {
    test(`${screen} entry-grid tiles tab in visual order at ${width}px`, async ({ page }) => {
      await openScreen(page, screen, width)
      // Trend History's readings/events lists are collapsed <details>; expand them as a user would.
      await page.evaluate(() => document.querySelectorAll('details').forEach((d) => (d.open = true)))
      await expect(page.getByText(wait).first()).toBeVisible()
      const tileCount = await page.locator('[data-slot="entry-grid"] > li').count()
      expect(tileCount, 'grid rendered its tiles').toBeGreaterThanOrEqual(minTiles)
      // Tile i of grid g; only tiles that contain a focusable control take part.
      await page.evaluate(() => {
        document.querySelectorAll('[data-slot="entry-grid"]').forEach((grid, g) => {
          Array.from(grid.children).forEach((tile, i) => tile.setAttribute('data-tab-group', `grid${g}-tile${i}`))
        })
      })
      const stops = await tabThrough(page)
      const names = [...new Set(stops.map((s) => s.group).filter((g): g is string => !!g))]
      // Grid 0 is the one with tabbable tiles (readings Edit buttons / tariff history actions): every
      // seeded tile must be reachable, so a grid that silently drops out cannot pass. (Events tiles
      // carry no controls, so that grid has no tab order to check.)
      expect(names.filter((n) => n.startsWith('grid0-')).length, 'every seeded tile in the first grid is keyboard-reachable').toBeGreaterThanOrEqual(minTiles)
      for (const grid of new Set(names.map((n) => n.split('-')[0]))) {
        const inGrid = names.filter((n) => n.startsWith(`${grid}-`))
        const tab = tabOrderOfGroups(stops).filter((n) => inGrid.includes(n))
        const visual = visualOrderOfGroups(stops, inGrid)
        expect(tab, `${grid}: Tab order ${tab.join(' → ')} must match visual order ${visual.join(' → ')}`).toEqual(visual)
      }
      if (width >= 660) {
        // Wide layout is a real multi-column grid (otherwise the row check proves nothing).
        const firstRowCols = await page.locator('[data-slot="entry-grid"]').first().evaluate((grid) => {
          const tops = Array.from(grid.children).map((c) => Math.round(c.getBoundingClientRect().top))
          return tops.filter((t) => t === tops[0]).length
        })
        expect(firstRowCols, 'wide grid has more than one column').toBeGreaterThan(1)
      }
    })
  }
}

// --- NavChrome: tab order equals visual order ------------------------------------------------------

const SCREENS: Screen[] = ['dashboard', 'trendHistory', 'tariffRadar', 'settings']

for (const screen of SCREENS) {
  for (const width of [659, 660, 900]) {
    test(`NavChrome tab position on ${screen} at ${width}px (${width >= 660 ? 'nav first' : 'bottom bar last'}, equals visual order)`, async ({ page }) => {
      await openScreen(page, screen, width)
      const wide = width >= 660
      const stops = await tabThrough(page)
      const navStops = stops.filter((s) => s.nav !== null)
      const contentStops = stops.filter((s) => s.nav === null)
      // Wide: skip link (WCAG 2.4.1) + 4 links + the account menu; phone: the 4 bottom-bar entries.
      expect(navStops.map((s) => s.name), `NavChrome contributes ${wide ? 'a skip link, 4 links and the account menu' : '4 links'}`).toHaveLength(wide ? 6 : 4)
      if (wide) expect(navStops[0].name, 'the skip link is the very first Tab stop').toContain('Skip to main content')
      else expect(stops.map((s) => s.name).join('|'), 'no skip link below 660px').not.toContain('Skip to main content')
      expect(contentStops.length, 'page has content Tab stops').toBeGreaterThan(0)
      expect(new Set(navStops.map((s) => s.nav)), `only the ${wide ? 'top' : 'bottom'} nav is tabbable`).toEqual(new Set([wide ? 'top' : 'bottom']))

      const observed = stops.map((s) => s.name).join(' | ')
      if (wide) {
        // Nav stops come first in the real Tab sequence, then content.
        expect(
          stops.slice(0, navStops.length).every((s) => s.nav === 'top'),
          `the first ${navStops.length} Tab stops must be the top nav (observed: ${observed})`,
        ).toBe(true)
        // Diagnostic against a pure CSS reorder: the nav is also visually above every content stop.
        const navMaxTop = Math.max(...navStops.map((s) => s.rect.top))
        const contentTop = Math.min(...contentStops.map((s) => s.rect.top))
        expect(navMaxTop, `top nav stops must be visually above all content stops (observed: ${observed})`).toBeLessThanOrEqual(contentTop)
      } else {
        expect(stops[0].nav, `first Tab stop at <660px is page content (observed: ${observed})`).toBeNull()
        const firstNav = stops.findIndex((s) => s.nav !== null)
        expect(
          stops.slice(firstNav).every((s) => s.nav === 'bottom'),
          `every stop after the first nav stop must be the bottom bar (observed: ${observed})`,
        ).toBe(true)
        const contentBottom = Math.max(...contentStops.map((s) => s.rect.bottom))
        const navTop = Math.min(...navStops.map((s) => s.rect.top))
        expect(navTop, 'bottom bar is visually below the page content').toBeGreaterThan(contentBottom - 1)
      }

      // Within the nav: left to right, avatar (wide only) last.
      const navLefts = navStops.map((s) => s.rect.left)
      expect(navLefts, 'nav links tab left to right').toEqual([...navLefts].sort((a, b) => a - b))
      if (wide) expect(navStops.at(-1)!.name).toContain('Account menu')
    })
  }
}

// --- Profile menu: Tab cycles Appearance → Language → Log off ------------------------------------

test('Profile menu: keyboard open, Tab cycles strips then Log off, Shift+Tab reverses, Escape returns focus', async ({ page }) => {
  await openScreen(page, 'dashboard', 900)
  const avatar = page.getByRole('button', { name: 'Account menu' })
  await avatar.focus()
  await page.keyboard.press('Enter')
  const menu = page.getByRole('menu')
  await expect(menu).toBeVisible()

  const focused = async () =>
    page.evaluate(() => {
      const el = document.activeElement as HTMLElement | null
      const group = el?.closest('[role="radiogroup"]')?.getAttribute('data-testid')
      return group ?? (el?.getAttribute('role') === 'menuitem' ? `menuitem:${el.textContent?.trim()}` : (el?.tagName ?? 'none'))
    })

  // Four Tabs from wherever Radix put focus on open: three distinct stops, then back to the first.
  const sequence: string[] = []
  for (let i = 0; i < 4; i++) {
    await page.keyboard.press('Tab')
    sequence.push(await focused())
  }
  const known = ['appearance-strip', 'language-strip', 'menuitem:Log off']
  expect(sequence.every((v) => known.includes(v)), `unexpected focus stop in ${sequence.join(' → ')}`).toBe(true)
  const cycle = sequence.slice(0, 3)
  expect(sequence[3], 'Tab wraps to the start of the cycle').toBe(cycle[0])
  // Whatever stop focus started on, the cycle runs Appearance → Language → Log off.
  const start = cycle.indexOf('appearance-strip')
  expect(start, 'Appearance strip is a Tab stop').toBeGreaterThanOrEqual(0)
  expect([...cycle.slice(start), ...cycle.slice(0, start)]).toEqual(known)

  // Visual order of the three stops matches (top to bottom).
  const tops = await page.evaluate(() =>
    ['[data-testid="appearance-strip"]', '[data-testid="language-strip"]', '[role="menuitem"]:not([data-disabled])'].map(
      (sel) => document.querySelector(sel)!.getBoundingClientRect().top,
    ),
  )
  expect(tops, 'strips and Log off are stacked in Tab order').toEqual([...tops].sort((a, b) => a - b))

  // The 4th Tab left focus at the cycle start, so Shift+Tab here is the reverse wrap to the last stop.
  await page.keyboard.press('Shift+Tab')
  expect(await focused(), 'Shift+Tab from the cycle start wraps to the last stop').toBe(cycle[2])

  await page.keyboard.press('Escape')
  await expect(menu).toBeHidden()
  await expect(avatar, 'Escape returns focus to the avatar').toBeFocused()
})
