import { expect, test, type Page } from '@playwright/test'

// Epic 8 retro action item #1: locale × theme × breakpoint sweep over the four main screens.
// Each cell asserts (a) the layout matches the width (659 narrow / 660+ wide), (b) no horizontal
// overflow or clipped text, (c) WCAG AA text contrast. Theme is forced through the stored device
// preference with the OS set to the *opposite* scheme, so a cell can never pass by accident on the
// OS default. Locale comes from the faked /api/session.

type Locale = 'de-DE' | 'en-US'
type Theme = 'light' | 'dark'
type Screen = 'dashboard' | 'trendHistory' | 'tariffRadar' | 'settings'

const LOCALES: Locale[] = ['de-DE', 'en-US']
const THEMES: Theme[] = ['light', 'dark']
const WIDTHS = [659, 660, 900]
const SCREENS: Screen[] = ['dashboard', 'trendHistory', 'tariffRadar', 'settings']

const NAV_LABEL: Record<Screen, Record<Locale, string | null>> = {
  dashboard: { 'de-DE': null, 'en-US': null },
  trendHistory: { 'de-DE': 'Verlauf', 'en-US': 'Trend History' },
  tariffRadar: { 'de-DE': 'Tarifradar', 'en-US': 'Tariff Radar' },
  settings: { 'de-DE': 'Einstellungen', 'en-US': 'Settings' },
}

const CONTENT_SLOT: Record<Screen, string> = {
  dashboard: 'dashboard-content',
  trendHistory: 'trend-history-content',
  tariffRadar: 'tariff-radar-content',
  settings: 'settings-content',
}

const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })

async function fakeApi(page: Page, locale: Locale) {
  // Registered first so the specific routes below take precedence.
  await page.route('**/api/**', (route) => route.fulfill(json([])))
  await page.route('**/api/session', (route) =>
    route.fulfill(
      json({
        hasHousehold: true,
        householdId: '11111111-1111-1111-1111-111111111111',
        locale,
        currency: 'EUR',
        supportsFederatedLogout: true,
        email: 'ralf@example.com',
      }),
    ),
  )
  await page.route('**/api/status', (route) =>
    route.fulfill(json({ status: 'withinRange', paceToDateKwh: 1234.5, baselineToDateKwh: 1300, isLowConfidence: false })),
  )
  await page.route('**/api/tariff-check', (route) =>
    route.fulfill(json({ isDue: true, gateOpensAtUtc: '2026-09-01T00:00:00+00:00' })),
  )
  await page.route('**/api/meter-regression-prompts/open', (route) => route.fulfill(json(null)))
  await page.route('**/api/status/history', (route) => route.fulfill(json([])))
  await page.route('**/api/smart-plug-readings', (route) => route.fulfill(json([])))
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
        items: [
          {
            id: '11111111-1111-1111-1111-111111111111',
            monthlyBaseFee: 20,
            pricePerKwh: 0.32,
            currency: 'EUR',
            contractStartDate: '2026-12-03T00:00:00.000Z',
            contractPeriodMonths: 12,
            version: 0,
            isCurrent: true,
            effectiveUntil: null,
            // Real-data shape that exposed the 659px de-DE table scroll in the live pass.
            corrections: [
              { fieldName: 'ContractStartDate', oldValue: '2026-01-01T00:00:00.000Z', newValue: '2026-12-03T00:00:00.000Z', correctedAtUtc: '2026-03-12T10:00:00.000Z' },
              { fieldName: 'MonthlyBaseFee', oldValue: '12.5', newValue: '20', correctedAtUtc: '2026-03-12T10:00:00.000Z' },
              { fieldName: 'PricePerKwh', oldValue: '0.3', newValue: '0.32', correctedAtUtc: '2026-03-12T10:00:00.000Z' },
            ],
          },
          // Older closed entries: "dd.mm.yyyy – dd.mm.yyyy" ranges are wider than "… – laufend".
          ...[0, 1].map((n) => ({
            id: `11111111-1111-1111-1111-11111111111${n + 2}`,
            monthlyBaseFee: 12 + n,
            pricePerKwh: 0.3,
            currency: 'EUR',
            contractStartDate: `${2025 - n}-01-15T00:00:00.000Z`,
            contractPeriodMonths: 12,
            version: 0,
            isCurrent: false,
            effectiveUntil: `${2026 - n}-12-02T00:00:00.000Z`,
            corrections: [],
          })),
        ],
        totalCount: 3,
        page: 1,
        pageSize: 20,
      }),
    ),
  )
  await page.route('**/api/tariffs/compare', (route) =>
    route.fulfill(
      json({
        currentMonthlyBaseFee: 12.5,
        currentPricePerKwh: 0.32,
        currency: 'EUR',
        candidateMonthlyBaseFee: 14.9,
        candidatePricePerKwh: 0.315,
        candidateSwitchingBonus: 350,
        annualPaceKwh: 3200,
        currentAnnualCost: 1174,
        candidateAnnualCostBonusNormalized: 1186.8,
        bonusNormalizedAnnualSavings: -12.8,
        candidateAnnualCostBonusIncluded: 836.8,
        bonusIncludedAnnualSavings: 337.2,
        isBonusIncludedWorthSwitching: true,
        isBonusNormalizedWorthSwitching: false,
        isLowConfidence: false,
      }),
    ),
  )
}

async function openScreen(page: Page, screen: Screen, locale: Locale, theme: Theme, width: number) {
  // reducedMotion switches off every `motion-safe:` entrance animation, so measurements are
  // deterministic (no mid-fade opacity/transform) without a fixed sleep.
  await page.emulateMedia({ colorScheme: theme === 'dark' ? 'light' : 'dark', reducedMotion: 'reduce' })
  await page.addInitScript((value) => window.localStorage.setItem('energy-tracker-theme', value), theme)
  await fakeApi(page, locale)
  await page.setViewportSize({ width, height: 900 })
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Energy Tracker' })).toBeVisible()
  const label = NAV_LABEL[screen][locale]
  if (label) {
    await page.getByRole('button', { name: label, exact: true }).first().click()
  }
  if (screen === 'tariffRadar') {
    // Fill the compare form so the result frames ("Mit/Ohne Wechselbonus") are part of the cell.
    await page.getByLabel(locale === 'de-DE' ? /Monatliche Grundgebühr des Kandidaten/ : /Candidate.*base fee/i).fill('14.9')
    await page.getByLabel(locale === 'de-DE' ? /Preis pro kWh des Kandidaten/ : /Candidate.*price per kWh/i).fill('0.315')
    await page.getByLabel(locale === 'de-DE' ? /Wechselbonus/ : /switching bonus/i).first().fill('350')
    await page.getByRole('button', { name: locale === 'de-DE' ? 'Vergleichen' : 'Compare', exact: true }).click()
    await expect(page.getByText(locale === 'de-DE' ? 'Mit Wechselbonus' : 'With the switching bonus', { exact: true }).first()).toBeVisible()
  }
  // The click must really have switched screens — a silent miss would audit the Dashboard under
  // another screen's name.
  await expect(page.locator(`div[data-slot="${CONTENT_SLOT[screen]}"]`)).toBeVisible()
}

interface Offender {
  kind: string
  detail: string
}

// Runs in the browser: overflow, clipping, and text contrast over every visible text-bearing element.
function audit(): { offenders: Offender[]; skippedContrast: number } {
  const offenders: Offender[] = []
  let skippedContrast = 0
  const describe = (el: Element) => {
    const text = (el.textContent ?? '').trim().replace(/\s+/g, ' ').slice(0, 40)
    return `<${el.tagName.toLowerCase()}${el.getAttribute('data-slot') ? ` data-slot=${el.getAttribute('data-slot')}` : ''}> "${text}"`
  }

  const doc = document.documentElement
  if (doc.scrollWidth > doc.clientWidth + 1) {
    offenders.push({ kind: 'page-overflow', detail: `scrollWidth ${doc.scrollWidth} > clientWidth ${doc.clientWidth}` })
  }

  // Computed colors come back as oklch()/color-mix()/etc. in modern Chrome — a 1×1 canvas resolves
  // any CSS color to sRGB bytes, so no per-syntax parsing is needed.
  const ctx = document.createElement('canvas').getContext('2d', { willReadFrequently: true })!
  ctx.canvas.width = ctx.canvas.height = 1
  const parse = (c: string): [number, number, number, number] => {
    ctx.clearRect(0, 0, 1, 1)
    ctx.fillStyle = '#000'
    ctx.fillStyle = c
    ctx.fillRect(0, 0, 1, 1)
    const [r, g, b, a] = ctx.getImageData(0, 0, 1, 1).data
    return [r, g, b, a / 255]
  }
  const lum = ([r, g, b]: number[]) => {
    const f = (v: number) => {
      const s = v / 255
      return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4
    }
    return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b)
  }
  const blend = (fg: number[], bg: number[]) => {
    const a = fg[3]
    return [fg[0] * a + bg[0] * (1 - a), fg[1] * a + bg[1] * (1 - a), fg[2] * a + bg[2] * (1 - a), 1]
  }
  // Effective background: stack ancestors' backgrounds bottom-up; bail (null) over images/gradients.
  const effectiveBg = (el: Element): number[] | null => {
    const chain: Element[] = []
    for (let n: Element | null = el; n; n = n.parentElement) chain.push(n)
    let bg = parse(getComputedStyle(document.body).backgroundColor)
    if (bg[3] === 0) bg = parse(getComputedStyle(doc).backgroundColor)
    if (bg[3] === 0) bg = [255, 255, 255, 1]
    for (const n of chain.reverse()) {
      const s = getComputedStyle(n)
      if (s.backgroundImage !== 'none') return null
      const c = parse(s.backgroundColor)
      if (c[3] > 0) bg = blend(c, bg)
    }
    return bg
  }

  for (const el of Array.from(document.body.querySelectorAll('*'))) {
    const style = getComputedStyle(el)
    if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity) === 0) continue
    const rect = el.getBoundingClientRect()
    if (rect.width === 0 || rect.height === 0) continue
    const hasText = Array.from(el.childNodes).some((n) => n.nodeType === Node.TEXT_NODE && (n.textContent ?? '').trim())
    const clipsX = style.overflowX !== 'visible'

    if (clipsX && el.scrollWidth > el.clientWidth + 1) {
      offenders.push({ kind: 'clipped', detail: `${describe(el)} scrollWidth ${el.scrollWidth} > clientWidth ${el.clientWidth}` })
    }
    if (hasText && (rect.right > window.innerWidth + 1 || rect.left < -1)) {
      offenders.push({ kind: 'off-viewport', detail: `${describe(el)} spans ${Math.round(rect.left)}..${Math.round(rect.right)} of ${window.innerWidth}` })
    }

    if (hasText && !(el as HTMLButtonElement).disabled) {
      const bg = effectiveBg(el)
      if (!bg) {
        skippedContrast++
        continue
      }
      const fg = blend(parse(style.color), bg)
      const l1 = lum(fg)
      const l2 = lum(bg)
      const ratio = (Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05)
      const size = parseFloat(style.fontSize)
      const large = size >= 24 || (size >= 18.66 && Number(style.fontWeight) >= 700)
      if (ratio < (large ? 3 : 4.5)) {
        offenders.push({ kind: 'contrast', detail: `${describe(el)} ratio ${ratio.toFixed(2)} (${style.color} on rgb(${bg.slice(0, 3).map(Math.round)}))` })
      }
    }
  }
  return { offenders, skippedContrast }
}

for (const screen of SCREENS) {
  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      for (const width of WIDTHS) {
        test(`sweep: ${screen} · ${locale} · ${theme} · ${width}px`, async ({ page }) => {
          await openScreen(page, screen, locale, theme, width)

          // Layout identity: a cell must never silently run in the wrong layout.
          const wide = width >= 660
          const topNav = page.locator('nav[data-slot="nav-chrome-top"]')
          const bottomNav = page.locator('nav[data-slot="nav-chrome-bottom"]')
          await expect(topNav, `${width}px should be the ${wide ? 'wide' : 'narrow'} layout`).toBeVisible({ visible: wide })
          await expect(bottomNav).toBeVisible({ visible: !wide })

          expect(await page.evaluate(() => document.documentElement.classList.contains('dark'))).toBe(theme === 'dark')
          expect(await page.evaluate(() => document.documentElement.lang)).toBe(locale)

          const { offenders, skippedContrast } = await page.evaluate(audit)
          test.info().annotations.push({ type: 'contrast-skipped', description: String(skippedContrast) })

          expect(offenders, offenders.map((o) => `${o.kind}: ${o.detail}`).join('\n')).toEqual([])
        })
      }
    }
  }
}

// Long German labels (retro: Import pill, "Wechselbonus", paired tariff fields). The Import pill
// is icon-only below 660px (44px hit area) and shows "Import" from 660px (must fit).
for (const width of [659, 660]) {
  test(`Import pill (de-DE) is usable at ${width}px`, async ({ page }) => {
    await openScreen(page, 'trendHistory', 'de-DE', 'dark', width)
    const importPill = page.getByRole('button', { name: 'Smart-Plug-Daten importieren' })
    await expect(importPill).toBeVisible()
    const box = await importPill.boundingBox()
    expect(box, 'Import pill must have a layout box').not.toBeNull()
    expect(box!.x + box!.width).toBeLessThanOrEqual(width)
    await expect(importPill.getByText('Import', { exact: true })).toBeVisible({ visible: width >= 660 })

    if (width < 660) {
      // Icon-only: visible box is exactly 40px. NOTE: there is no ::before hit-area extension on this
      // button, so the touch target is 40px, not 44px (deferred-work.md "Import/Event icon buttons
      // below 660px are 40px"). Pinned here so a change to the size is noticed either way.
      expect(box!.width).toBeCloseTo(40, 0)
      expect(box!.height).toBeCloseTo(40, 0)
    } else {
      expect(box!.height).toBeCloseTo(32, 0) // labeled desktop pill
    }
  })
}

test('long German tariff labels fit at 659px: Wechselbonus and paired fields', async ({ page }) => {
  await openScreen(page, 'tariffRadar', 'de-DE', 'dark', 659)
  const bonus = page.getByLabel(/Wechselbonus/).first()
  await expect(bonus).toBeVisible()
  const labelFits = await bonus.evaluate((input) => {
    const label = document.querySelector(`label[for="${input.id}"]`) as HTMLElement | null
    return label ? label.scrollWidth <= label.clientWidth + 1 : null
  })
  expect(labelFits, 'Wechselbonus label must exist and fit its container').toBe(true)

  const fee = page.getByLabel(/Monatliche Grundgebühr des Kandidaten/)
  const price = page.getByLabel(/Preis pro kWh des Kandidaten/)
  const feeBox = await fee.boundingBox()
  const priceBox = await price.boundingBox()
  expect(feeBox, 'base fee field must have a layout box').not.toBeNull()
  expect(priceBox, 'price field must have a layout box').not.toBeNull()
  const overlaps =
    feeBox!.x < priceBox!.x + priceBox!.width &&
    priceBox!.x < feeBox!.x + feeBox!.width &&
    feeBox!.y < priceBox!.y + priceBox!.height &&
    priceBox!.y < feeBox!.y + feeBox!.height
  expect(overlaps, 'paired fields must not overlap (side by side or stacked)').toBe(false)
  const viewportWidth = page.viewportSize()!.width
  expect(Math.max(feeBox!.x + feeBox!.width, priceBox!.x + priceBox!.width)).toBeLessThanOrEqual(viewportWidth)
})
