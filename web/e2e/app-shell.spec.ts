import { expect, test } from '@playwright/test'

// Every route now sits behind /api/session (AC #1/#5) — the built SPA has no live backend under
// `vite preview`, so the session call is faked here to keep this a smoke test of the shell
// itself (not of auth), matching what it verified before Story 1.5 added the auth gate.
test('the SPA shell loads once authenticated with a household', async ({ page }) => {
  await page.route('**/api/session', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        hasHousehold: true,
        householdId: '11111111-1111-1111-1111-111111111111',
        locale: 'en-US',
        currency: 'USD',
      }),
    }),
  )

  await page.goto('/')

  await expect(page).toHaveTitle('Energy Tracker')
  await expect(page.getByRole('heading', { name: 'Energy Tracker' })).toBeVisible()
})

// Story 8.1/Task 7: jsdom-based component tests (nav-chrome.test.tsx) can only assert both markup
// variants exist with the right classes — they can't prove which one a real browser actually
// shows at a given viewport width, since jsdom never evaluates CSS media queries. This is the
// actual regression guard for AC #1/#2.
test('the nav chrome swaps between the bottom tab bar and the top nav at the 660px breakpoint', async ({ page }) => {
  await page.route('**/api/session', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        hasHousehold: true,
        householdId: '11111111-1111-1111-1111-111111111111',
        locale: 'en-US',
        currency: 'USD',
        supportsFederatedLogout: true,
        email: 'ralf@example.com',
      }),
    }),
  )
  await page.route('**/api/status', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }))
  await page.route('**/api/tariff-check', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }))
  await page.route('**/api/meter-regression-prompts/open', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }),
  )

  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Energy Tracker' })).toBeVisible()

  // Playwright's role queries exclude display:none elements from the accessibility tree — a
  // plain getByRole/text locator would silently re-resolve to whichever <nav> happens to be
  // visible at query time across a resize, rather than reliably tracking "the bottom one" vs.
  // "the top one". data-slot is a stable, visibility-independent hook (nav-chrome.tsx).
  const bottomTabBar = page.locator('nav[data-slot="nav-chrome-bottom"]')
  const topNav = page.locator('nav[data-slot="nav-chrome-top"]')

  await page.setViewportSize({ width: 375, height: 800 })
  await expect(bottomTabBar).toBeVisible()
  await expect(topNav).toBeHidden()

  await page.setViewportSize({ width: 900, height: 800 })
  await expect(bottomTabBar).toBeHidden()
  await expect(topNav).toBeVisible()
  await expect(topNav.getByRole('button', { name: 'Account menu' })).toBeVisible()
})

// Story 8.2/Task 3: same jsdom limitation as above — dashboard-page.test.tsx can only prove the
// wide:max-w-[900px] wrapper class and label spans exist in markup, never which one a real browser
// actually renders at a given viewport width (AC #1, #3, #4).
test('the Dashboard content column and header-icon-button labels swap at the 660px breakpoint', async ({ page }) => {
  await page.route('**/api/session', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        hasHousehold: true,
        householdId: '11111111-1111-1111-1111-111111111111',
        locale: 'en-US',
        currency: 'USD',
        supportsFederatedLogout: true,
        email: 'ralf@example.com',
      }),
    }),
  )
  await page.route('**/api/status', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }))
  await page.route('**/api/tariff-check', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }))
  await page.route('**/api/meter-regression-prompts/open', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }),
  )

  const eventButton = page.getByRole('button', { name: 'Log an Event' })
  const importButton = page.getByRole('button', { name: 'Import Smart Plug data' })
  // Tag-qualified so this locator can never collide with strict mode if `data-slot` is ever
  // reused on a different element (matches the sibling `nav[data-slot=...]` locators above).
  const contentColumn = page.locator('div[data-slot="dashboard-content"]')
  // The label spans are always in the DOM (just `hidden` below the breakpoint) — a textContent
  // check like toContainText wouldn't catch that, so assert visibility instead.
  const eventLabel = eventButton.getByText('Event')
  const importLabel = importButton.getByText('Import')

  await page.setViewportSize({ width: 500, height: 800 })
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Energy Tracker' })).toBeVisible()

  await expect(eventLabel).toBeHidden()
  await expect(importLabel).toBeHidden()
  const narrowBox = await eventButton.boundingBox()
  expect(narrowBox?.width).toBeLessThanOrEqual(48)
  const narrowColumnBox = await contentColumn.boundingBox()
  expect(narrowColumnBox?.width).toBeGreaterThan(400) // tracks the 500px viewport, not capped at 900px

  // Exact-boundary check: `wide:` is a `min-width: 660px` variant, so 659px must still be the
  // narrow/icon-only layout and 660px must already be the wide/labeled one — otherwise an
  // off-by-one drift in `--breakpoint-wide` would go undetected. (The `max-w-[900px]` cap is
  // pinned separately by the 800px and 1000px column-width checks below.)
  await page.setViewportSize({ width: 659, height: 800 })
  await expect(eventLabel).toBeHidden()
  await expect(importLabel).toBeHidden()

  await page.setViewportSize({ width: 660, height: 800 })
  await expect(eventLabel).toBeVisible()
  await expect(importLabel).toBeVisible()

  // 660-900px: the column fills the viewport (minus page padding) rather than pinning to a cap (AC #2).
  await page.setViewportSize({ width: 800, height: 800 })
  const midColumnBox = await contentColumn.boundingBox()
  expect(midColumnBox?.width).toBeCloseTo(768, 0) // 800px viewport minus 2x16px page padding

  await page.setViewportSize({ width: 1000, height: 800 })
  await expect(eventLabel).toBeVisible()
  await expect(importLabel).toBeVisible()
  const wideColumnBox = await contentColumn.boundingBox()
  expect(wideColumnBox?.width).toBeCloseTo(900, 0) // 1000px viewport is past the cap, so exactly max-w-[900px]
})

// Story 8.3/Task 5: same jsdom limitation as above — trend-history-page.test.tsx can only prove
// the wide:max-w-[900px] wrapper class and Import label span exist in markup. This also proves the
// Task 3 table dead-space fix with a precise gap measurement (getBoundingClientRect, not a
// visual-only class-presence check), per Story 8.2's code-review-driven precedent (AC #1, #2).
test('the Trend History content column, Import label, and Meter Readings table dead-space fix behave correctly at the 660px breakpoint', async ({ page }) => {
  await page.route('**/api/session', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        hasHousehold: true,
        householdId: '11111111-1111-1111-1111-111111111111',
        locale: 'en-US',
        currency: 'USD',
        supportsFederatedLogout: true,
        email: 'ralf@example.com',
      }),
    }),
  )
  await page.route('**/api/status', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }))
  await page.route('**/api/tariff-check', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }))
  await page.route('**/api/meter-regression-prompts/open', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }),
  )
  await page.route('**/api/status/history', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }))
  // Seed three readings and three events so both the table (<660px) and a 3-column tile grid
  // (>=660px) are observable, not the empty state.
  await page.route('**/api/meter-readings?*', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        items: [1, 2, 3].map((n) => ({
          id: `11111111-1111-1111-1111-11111111111${n}`,
          kwhValue: 100 * n,
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
    }),
  )
  await page.route('**/api/events?*', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
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
    }),
  )
  await page.route('**/api/smart-plug-readings', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }))

  const contentColumn = page.locator('div[data-slot="trend-history-content"]')

  await page.setViewportSize({ width: 500, height: 800 })
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Energy Tracker' })).toBeVisible()
  await page.getByRole('button', { name: 'Trend History' }).first().click()
  await expect(page.getByRole('heading', { name: 'Trend History' })).toBeVisible()

  const importButton = page.getByRole('button', { name: 'Import Smart Plug data' })
  const importLabel = importButton.getByText('Import')

  await expect(importLabel).toBeHidden()
  const narrowColumnBox = await contentColumn.boundingBox()
  expect(narrowColumnBox?.width).toBeGreaterThan(400) // tracks the 500px viewport, not capped at 900px

  // Exact-boundary check, same as the Dashboard case above: 659px stays icon-only, 660px is labeled
  // (breakpoint only; the 900px cap is pinned by the 800px/1000px column-width checks below).
  await page.setViewportSize({ width: 659, height: 800 })
  await expect(importLabel).toBeHidden()

  // Task 3 (checked below 660px, where the table is still rendered — from 660px the same entries
  // are tiles, see the entry-grid checks at 1000px): no large blank gap between the rendered Timestamp text and the Edit button, at any
  // width (this fix is unconditional, not wide:-gated — contrast with the column/label assertions
  // above). Measured against the Timestamp *text*'s own rect (via a DOM Range, not the padded
  // <td> box) and the Edit *button*'s own rect (not its cell) — comparing the two cells' boundary
  // boxes instead would always read ~0px regardless of whether the w-px fix is present, since
  // adjacent table cells in the same row are always contiguous.
  await page.getByText('Meter Readings — 3 logged').click()
  const editButton = page.getByRole('button', { name: /Edit reading from/ }).first()
  await expect(editButton).toBeVisible()
  const row = page.locator('tr', { has: editButton })
  const timestampCell = row.locator('td').nth(1)
  const timestampTextRect = await timestampCell.evaluate((td) => {
    const range = document.createRange()
    range.selectNodeContents(td)
    return range.getBoundingClientRect().right
  })
  const editButtonBox = await editButton.boundingBox()
  expect(editButtonBox).not.toBeNull()
  const gap = editButtonBox!.x - timestampTextRect
  expect(gap).toBeLessThan(40)

  // Story 8.7: below 660px the entries stay a table and no tile grid exists.
  await expect(page.getByRole('table')).toHaveCount(1)
  await page.getByText('Events — 3 logged').click()
  await expect(page.getByRole('table')).toHaveCount(2)
  await expect(page.locator('[data-slot="entry-grid"]')).toHaveCount(0)

  await page.setViewportSize({ width: 660, height: 800 })
  await expect(importLabel).toBeVisible()
  // Exactly at 660px (min-width is inclusive) the disclosures already swap table -> grid.
  await expect(page.getByRole('table')).toHaveCount(0)
  await expect(page.locator('[data-slot="entry-grid"]')).toHaveCount(2)

  // 660-900px: the column fills the viewport (minus page padding) rather than pinning to a cap (AC #2).
  await page.setViewportSize({ width: 800, height: 800 })
  const midColumnBox = await contentColumn.boundingBox()
  expect(midColumnBox?.width).toBeCloseTo(768, 0) // 800px viewport minus 2x16px page padding

  await page.setViewportSize({ width: 1000, height: 800 })
  await expect(importLabel).toBeVisible()
  const wideColumnBox = await contentColumn.boundingBox()
  expect(wideColumnBox?.width).toBeCloseTo(900, 0) // 1000px viewport is past the cap, so exactly max-w-[900px]

  // Story 8.7 (AC #1): at >=660px both disclosures (still open across the resize) swap the table for
  // a tile grid. Inside the 900px column, three 260px-min tiles fit per row (3x260 + 2x12 = 804px).
  await expect(page.getByRole('table')).toHaveCount(0)
  const grids = page.locator('[data-slot="entry-grid"]')
  await expect(grids).toHaveCount(2)
  for (const grid of await grids.all()) {
    const tiles = grid.locator('li')
    await expect(tiles).toHaveCount(3)
    const boxes = await tiles.evaluateAll((els) => els.map((el) => el.getBoundingClientRect().toJSON()))
    // Exactly one row of three: same y, strictly increasing x, equal widths, each >= the 260px minimum.
    expect(boxes[0].y).toBeCloseTo(boxes[1].y, 0)
    expect(boxes[1].y).toBeCloseTo(boxes[2].y, 0)
    expect(boxes[1].x).toBeGreaterThan(boxes[0].x)
    expect(boxes[2].x).toBeGreaterThan(boxes[1].x)
    expect(boxes[1].width).toBeCloseTo(boxes[0].width, 0)
    expect(boxes[2].width).toBeCloseTo(boxes[0].width, 0)
    expect(boxes[0].width).toBeGreaterThanOrEqual(260)
    // Tiles are separated by exactly the 12px gap (gap-3) and the row's right edge stays inside the column.
    expect(boxes[1].x - (boxes[0].x + boxes[0].width)).toBeCloseTo(12, 0)
    expect(boxes[2].x - (boxes[1].x + boxes[1].width)).toBeCloseTo(12, 0)
    // A 4th tile would need >=4x260 + 3x12 = 1076px, more than the 900px column, so 3 is the exact count.
    expect(boxes[2].x + boxes[2].width).toBeLessThanOrEqual(wideColumnBox!.x + wideColumnBox!.width)
  }
  // AC #3: nothing else on the page is gridded by this story.
  await expect(page.locator('[data-slot="entry-grid"]')).toHaveCount(2)

  // Back across the boundary in the same page: the table returns without a reload.
  await page.setViewportSize({ width: 659, height: 800 })
  await expect(page.getByRole('table')).toHaveCount(2)
  await expect(page.locator('[data-slot="entry-grid"]')).toHaveCount(0)
})

// Story 8.4/Task 5: same jsdom limitation as above — tariff-configuration-form.test.tsx and
// tariff-comparison-form.test.tsx can only prove the wide:flex-row/wide:flex-1 wrapper classes and
// data-slot="quiet-card"/"glass-card" markup exist, never which layout a real browser actually
// renders at a given viewport width, nor the real backdrop-filter value (AC #1, #2, #3, #4).
test('the Tariff Radar content column, paired form fields, and card tiers behave correctly at the 660px breakpoint', async ({ page }) => {
  await page.route('**/api/session', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        hasHousehold: true,
        householdId: '11111111-1111-1111-1111-111111111111',
        locale: 'en-US',
        currency: 'USD',
        supportsFederatedLogout: true,
        email: 'ralf@example.com',
      }),
    }),
  )
  await page.route('**/api/status', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }))
  await page.route('**/api/tariff-check', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }))
  await page.route('**/api/meter-regression-prompts/open', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }),
  )
  // Seed a current Tariff (so TariffComparisonForm actually renders — tariff-radar-page.tsx's
  // currentTariffCurrency gating) plus two older ones so the history has 3 entries.
  await page.route('**/api/tariffs?*', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        items: [
          {
            id: '11111111-1111-1111-1111-111111111111',
            monthlyBaseFee: 12.5,
            pricePerKwh: 0.32,
            currency: 'EUR',
            contractStartDate: '2026-01-15T00:00:00.000Z',
            contractPeriodMonths: 12,
            version: 0,
            isCurrent: true,
            effectiveUntil: null,
            corrections: [],
          },
          ...[2, 3].map((n) => ({
            id: `11111111-1111-1111-1111-11111111111${n}`,
            monthlyBaseFee: 10 + n,
            pricePerKwh: 0.3,
            currency: 'EUR',
            contractStartDate: `${2026 - n + 1}-01-15T00:00:00.000Z`,
            contractPeriodMonths: 12,
            version: 0,
            isCurrent: false,
            effectiveUntil: `${2026 - n + 2}-01-15T00:00:00.000Z`,
            corrections: [],
          })),
        ],
        totalCount: 3,
        page: 1,
        pageSize: 20,
      }),
    }),
  )
  // mockupWorkedExample shape (tariff-comparison-form.test.tsx) — same worked example reused here.
  await page.route('**/api/tariffs/compare', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
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
    }),
  )

  const contentColumn = page.locator('div[data-slot="tariff-radar-content"]')
  const monthlyBaseFeeField = page.getByLabel('Monthly base fee', { exact: true })
  const pricePerKwhField = page.getByLabel('Price per kWh', { exact: true })

  await page.setViewportSize({ width: 500, height: 800 })
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Energy Tracker' })).toBeVisible()
  await page.getByRole('button', { name: 'Tariff Radar' }).first().click()
  await expect(page.getByRole('heading', { name: 'Tariff Radar' })).toBeVisible()

  const narrowColumnBox = await contentColumn.boundingBox()
  expect(narrowColumnBox?.width).toBeGreaterThan(400) // tracks the 500px viewport, not capped at 900px

  // Below 660px: Monthly Base Fee/Price per kWh stack (same x, different y).
  let baseFeeBox = await monthlyBaseFeeField.boundingBox()
  let priceBox = await pricePerKwhField.boundingBox()
  expect(baseFeeBox).not.toBeNull()
  expect(priceBox).not.toBeNull()
  expect(Math.abs(baseFeeBox!.x - priceBox!.x)).toBeLessThan(2)
  expect(priceBox!.y).toBeGreaterThan(baseFeeBox!.y)

  // Story 8.7: the Tariff History list is a table below 660px (no tile grid).
  await expect(page.getByRole('table')).toHaveCount(1)
  await expect(page.locator('[data-slot="entry-grid"]')).toHaveCount(0)

  // Exact-boundary check, same precedent as Dashboard/Trend History: 659px still stacks.
  await page.setViewportSize({ width: 659, height: 800 })
  baseFeeBox = await monthlyBaseFeeField.boundingBox()
  priceBox = await pricePerKwhField.boundingBox()
  expect(Math.abs(baseFeeBox!.x - priceBox!.x)).toBeLessThan(2)

  // At 660px: fields sit side by side (same y, different x, roughly equal width).
  await page.setViewportSize({ width: 660, height: 800 })
  baseFeeBox = await monthlyBaseFeeField.boundingBox()
  priceBox = await pricePerKwhField.boundingBox()
  expect(Math.abs(baseFeeBox!.y - priceBox!.y)).toBeLessThan(2)
  expect(priceBox!.x).toBeGreaterThan(baseFeeBox!.x)
  expect(Math.abs(baseFeeBox!.width - priceBox!.width)).toBeLessThan(baseFeeBox!.width * 0.2)

  // 660-900px: the column fills the viewport (minus page padding) rather than pinning to a cap (AC #2).
  await page.setViewportSize({ width: 800, height: 800 })
  const midColumnBox = await contentColumn.boundingBox()
  expect(midColumnBox?.width).toBeCloseTo(768, 0) // 800px viewport minus 2x16px page padding

  await page.setViewportSize({ width: 1000, height: 800 })
  baseFeeBox = await monthlyBaseFeeField.boundingBox()
  priceBox = await pricePerKwhField.boundingBox()
  expect(Math.abs(baseFeeBox!.y - priceBox!.y)).toBeLessThan(2)
  expect(priceBox!.x).toBeGreaterThan(baseFeeBox!.x)

  const wideColumnBox = await contentColumn.boundingBox()
  expect(wideColumnBox?.width).toBeCloseTo(900, 0) // 1000px viewport is past the cap, so exactly max-w-[900px]

  // Story 8.7 (AC #1): at >=660px the history renders as a 3-column tile grid instead of a table.
  await expect(page.getByRole('table')).toHaveCount(0)
  const historyTiles = page.locator('[data-slot="entry-grid"] li')
  await expect(historyTiles).toHaveCount(3)
  const tileBoxes = await historyTiles.evaluateAll((els) => els.map((el) => el.getBoundingClientRect().toJSON()))
  expect(tileBoxes[0].y).toBeCloseTo(tileBoxes[1].y, 0)
  expect(tileBoxes[1].y).toBeCloseTo(tileBoxes[2].y, 0)
  expect(tileBoxes[1].x).toBeGreaterThan(tileBoxes[0].x)
  expect(tileBoxes[2].x).toBeGreaterThan(tileBoxes[1].x)
  expect(tileBoxes[1].width).toBeCloseTo(tileBoxes[0].width, 0)
  expect(tileBoxes[2].width).toBeCloseTo(tileBoxes[0].width, 0)
  expect(tileBoxes[0].width).toBeGreaterThanOrEqual(260)
  // The current tile shows its period range (start date + ongoing) and the Current badge.
  await expect(page.locator('[data-slot="entry-grid"] li').first()).toContainText('Current')

  // Submit the compare form and assert the candidate fields pair up + the card tiers (AC #2, #3).
  await page.getByLabel('Candidate monthly base fee').fill('14.90')
  await page.getByLabel('Candidate price per kWh').fill('0.3150')
  await page.getByLabel('Switching bonus (optional)').fill('350')

  const candidateBaseFeeBox = await page.getByLabel('Candidate monthly base fee').boundingBox()
  const candidatePriceBox = await page.getByLabel('Candidate price per kWh').boundingBox()
  expect(Math.abs(candidateBaseFeeBox!.y - candidatePriceBox!.y)).toBeLessThan(2)
  expect(candidatePriceBox!.x).toBeGreaterThan(candidateBaseFeeBox!.x)

  await page.getByRole('button', { name: 'Compare' }).click()
  await expect(page.getByText('Is it worth switching?')).toBeVisible()

  const quietCards = page.locator('[data-slot="quiet-card"]')
  await expect(quietCards).toHaveCount(2)
  // Explicit test id on the verdict card (not a `.last()` document-order heuristic) so this
  // assertion can't silently pick up the outer form-wrapping GlassCard instead.
  const glassCardBackdrop = await page
    .getByTestId('tariff-compare-verdict-card')
    .evaluate((el) => getComputedStyle(el).backdropFilter)
  const quietCardBackdrops = await quietCards.evaluateAll((els) => els.map((el) => getComputedStyle(el).backdropFilter))

  expect(glassCardBackdrop).not.toBe('none')
  for (const backdrop of quietCardBackdrops) {
    expect(backdrop).toBe('none')
  }

  // Story 8.9 (UX-DR31): the current/candidate summary panels pair side by side at >=660px and the
  // verdict card stays full width below them. Geometry is derived from the measured wrapper, not
  // hardcoded, so a changed padding token doesn't silently invalidate the exact-value checks.
  const cardRects = () => quietCards.evaluateAll((els) => els.map((el) => el.getBoundingClientRect().toJSON()))
  const pairWrapper = quietCards.first().locator('..')
  // Read the gap from the wrapper's computed style (gap-4) rather than hardcoding it.
  const pairGap = await pairWrapper.evaluate((el) => parseFloat(getComputedStyle(el).columnGap))
  expect(pairGap).toBeGreaterThan(0)
  const verdictCard = page.getByTestId('tariff-compare-verdict-card')

  let [currentRect, candidateRect] = await cardRects()
  expect(candidateRect.y).toBeCloseTo(currentRect.y, 0)
  expect(candidateRect.x).toBeGreaterThan(currentRect.x)
  expect(candidateRect.width).toBeCloseTo(currentRect.width, 0)
  expect(candidateRect.x).toBeCloseTo(currentRect.x + currentRect.width + pairGap, 0)
  // Equal-height stretch: the candidate panel has the extra Switching Bonus row.
  expect(candidateRect.height).toBeCloseTo(currentRect.height, 0)

  // The wrapper is measured on its own, so a wrapper wider/narrower than the cards is caught.
  const wrapperBox = (await pairWrapper.boundingBox())!
  expect(wrapperBox.x).toBeCloseTo(currentRect.x, 0)
  expect(wrapperBox.width).toBeCloseTo(candidateRect.x + candidateRect.width - currentRect.x, 0)
  const verdictBox = (await verdictCard.boundingBox())!
  expect(verdictBox.y).toBeGreaterThanOrEqual(currentRect.y + currentRect.height)
  expect(verdictBox.x).toBeCloseTo(wrapperBox.x, 0)
  expect(verdictBox.width).toBeCloseTo(wrapperBox.width, 0)

  // ~800px (column 768px): still side by side, neither panel overflows the column.
  await page.setViewportSize({ width: 800, height: 800 })
  ;[currentRect, candidateRect] = await cardRects()
  const midColumn = (await contentColumn.boundingBox())!
  expect(candidateRect.y).toBeCloseTo(currentRect.y, 0)
  expect(candidateRect.x).toBeGreaterThan(currentRect.x)
  expect(currentRect.x).toBeGreaterThanOrEqual(midColumn.x)
  expect(candidateRect.x + candidateRect.width).toBeLessThanOrEqual(midColumn.x + midColumn.width)
  expect(candidateRect.width).toBeCloseTo(currentRect.width, 0)

  // AC #3 exact boundary on the already-rendered result: 659px stacks, 660px pairs.
  await page.setViewportSize({ width: 659, height: 800 })
  ;[currentRect, candidateRect] = await cardRects()
  expect(candidateRect.x).toBeCloseTo(currentRect.x, 0)
  expect(candidateRect.y).toBeGreaterThanOrEqual(currentRect.y + currentRect.height)
  expect(candidateRect.y).toBeCloseTo(currentRect.y + currentRect.height + pairGap, 0)

  await page.setViewportSize({ width: 660, height: 800 })
  ;[currentRect, candidateRect] = await cardRects()
  expect(candidateRect.y).toBeCloseTo(currentRect.y, 0)
  expect(candidateRect.x).toBeGreaterThan(currentRect.x)
})

// Story 8.5/Task 5: same jsdom limitation as above — settings-page.test.tsx and
// tagging-scaffold-manager.test.tsx can only prove the wide:max-w-[900px] wrapper class, the
// section-label headings, and the dual Add-button markup all exist, never which one a real
// browser actually renders at a given viewport width, nor the wide:order-N visual-reorder
// sequence (AC #1, #2, #3).
test('the Settings content column, labeled sections, and Room/Power Point tree controls behave correctly at the 660px breakpoint', async ({ page }) => {
  const householdId = '11111111-1111-1111-1111-111111111111'

  await page.route('**/api/session', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        hasHousehold: true,
        householdId,
        locale: 'en-US',
        currency: 'USD',
        supportsFederatedLogout: true,
        email: 'ralf@example.com',
      }),
    }),
  )
  await page.route('**/api/status', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }))
  await page.route('**/api/tariff-check', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }))
  await page.route('**/api/meter-regression-prompts/open', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }),
  )
  await page.route('**/api/rooms', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      // Story 8.8: >=4 Rooms with DISTINCT Power Point counts (4/2/3/0) so the count text used by the
      // locators below is unambiguous, and multi-column is observable.
      body: JSON.stringify([
        { id: 'r1', name: 'Living Room', archivedAt: null },
        { id: 'r2', name: 'Bedroom', archivedAt: null },
        { id: 'r3', name: 'Office', archivedAt: null },
        { id: 'r4', name: 'Garage', archivedAt: null },
      ]),
    }),
  )
  await page.route('**/api/power-points', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { id: 'p1', roomId: 'r1', name: 'Wall outlet', archivedAt: null },
        { id: 'p1b', roomId: 'r1', name: 'TV outlet', archivedAt: null },
        { id: 'p1c', roomId: 'r1', name: 'Lamp outlet', archivedAt: null },
        { id: 'p1d', roomId: 'r1', name: 'Window outlet', archivedAt: null },
        { id: 'p2a', roomId: 'r2', name: 'Bed outlet', archivedAt: null },
        { id: 'p2b', roomId: 'r2', name: 'Desk outlet', archivedAt: null },
        { id: 'p3a', roomId: 'r3', name: 'PC outlet', archivedAt: null },
        { id: 'p3b', roomId: 'r3', name: 'Printer outlet', archivedAt: null },
        { id: 'p3c', roomId: 'r3', name: 'Monitor outlet', archivedAt: null },
      ]),
    }),
  )
  await page.route('**/api/devices', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { id: 'd1', powerPointId: 'p1', name: 'Kettle', archivedAt: null },
        { id: 'd2', powerPointId: 'p1', name: 'Toaster', archivedAt: null },
        { id: 'd3', powerPointId: 'p1', name: 'Blender', archivedAt: null },
      ]),
    }),
  )
  await page.route(`**/api/households/${householdId}`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ id: householdId, locale: 'en-US', currency: 'USD', yearlyBaselineKwh: null, version: 0 }),
    }),
  )
  await page.route(`**/api/households/${householdId}/ai-plausibility`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ enabled: false, backendConfigured: false, backendLabel: null, version: 0 }),
    }),
  )

  const contentColumn = page.locator('div[data-slot="settings-content"]')

  await page.setViewportSize({ width: 500, height: 800 })
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Energy Tracker' })).toBeVisible()
  await page.getByRole('button', { name: 'Settings' }).first().click()
  await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible()

  const householdSectionLabel = page.getByRole('heading', { name: 'Household', level: 2 })

  // Below 660px: content tracks the viewport (not capped), no section labels, flat layout.
  const narrowColumnBox = await contentColumn.boundingBox()
  expect(narrowColumnBox?.width).toBeGreaterThan(400)
  await expect(householdSectionLabel).toBeHidden()

  // Exact-boundary check, same precedent as Dashboard/Trend History/Tariff Radar.
  await page.setViewportSize({ width: 659, height: 800 })
  await expect(householdSectionLabel).toBeHidden()

  await page.setViewportSize({ width: 660, height: 800 })
  await expect(householdSectionLabel).toBeVisible()

  // 660-900px: the column fills the viewport (minus page padding) rather than pinning to a cap (AC #2).
  await page.setViewportSize({ width: 800, height: 800 })
  const midColumnBox = await contentColumn.boundingBox()
  expect(midColumnBox?.width).toBeCloseTo(768, 0) // 800px viewport minus 2x16px page padding

  await page.setViewportSize({ width: 1000, height: 800 })

  const wideColumnBox = await contentColumn.boundingBox()
  expect(wideColumnBox?.width).toBeCloseTo(900, 0) // 1000px viewport is past the cap, so exactly max-w-[900px]
  // Centered: roughly equal left/right gaps against the viewport.
  const leftGap = wideColumnBox!.x
  const rightGap = 1000 - (wideColumnBox!.x + wideColumnBox!.width)
  expect(Math.abs(leftGap - rightGap)).toBeLessThan(4)

  // All 5 section-label headings are visible, in the correct top-to-bottom visual order — the
  // wide:order-N mechanism is exactly the kind of thing a class-presence-only check would miss a
  // regression in, so this compares actual y-positions.
  const yearlyBaselineLabel = page.getByRole('heading', { name: 'Yearly Baseline', level: 2 })
  const aiPlausibilityLabel = page.getByRole('heading', { name: 'AI Plausibility Check', level: 2 })
  const roomsLabel = page.getByRole('heading', { name: 'Rooms, Power Points & Devices', level: 2 })
  // exact: true — "Data" would otherwise substring-match DataExportPanel's "Data Export" and
  // DataImportPanel's "Data Import / Restore" headings.
  const dataLabel = page.getByRole('heading', { name: 'Data', level: 2, exact: true })

  await expect(yearlyBaselineLabel).toBeVisible()
  await expect(aiPlausibilityLabel).toBeVisible()
  await expect(householdSectionLabel).toBeVisible()
  await expect(roomsLabel).toBeVisible()
  await expect(dataLabel).toBeVisible()

  const yPositions = await Promise.all(
    [yearlyBaselineLabel, aiPlausibilityLabel, householdSectionLabel, roomsLabel, dataLabel].map(
      async (label) => (await label.boundingBox())!.y,
    ),
  )
  expect(yPositions).toEqual([...yPositions].sort((a, b) => a - b))

  // Story 8.8 (AC #4): at 1000px (column exactly 900px) AI Plausibility and Household sit side by
  // side — same y, different x, together spanning the column — while Yearly Baseline and Rooms
  // stay full width.
  const columnBox = (await contentColumn.boundingBox())!
  const aiBox = (await aiPlausibilityLabel.boundingBox())!
  const householdBox = (await householdSectionLabel.boundingBox())!
  expect(Math.abs(aiBox.y - householdBox.y)).toBeLessThan(1)
  expect(householdBox.x).toBeGreaterThan(aiBox.x)
  const aiCard = (await page.locator('div:has(> h2:text-is("AI Plausibility Check")):not(:has(h2:text-is("Household")))').last().boundingBox())!
  const householdCard = (await page.locator('div:has(> h2:text-is("Household"))').last().boundingBox())!
  expect(aiCard.x).toBeCloseTo(columnBox.x, 0)
  expect(householdCard.x + householdCard.width).toBeCloseTo(columnBox.x + columnBox.width, 0)
  expect(aiCard.width).toBeCloseTo(householdCard.width, 0)
  expect(aiCard.width + householdCard.width + (householdCard.x - (aiCard.x + aiCard.width))).toBeCloseTo(columnBox.width, 0)
  const baselineCard = (await page.locator('div:has(> h2:text-is("Yearly Baseline"))').last().boundingBox())!
  const roomsSection = (await page.locator('div:has(> h2:text-is("Rooms, Power Points & Devices"))').last().boundingBox())!
  expect(baselineCard.width).toBeCloseTo(columnBox.width, 0)
  expect(roomsSection.width).toBeCloseTo(columnBox.width, 0)

  // Story 8.8 (AC #1): collapsed Rooms flow into an auto-fill grid. Expected column count is derived
  // from the grid container's real content width, not hard-coded.
  const roomsGrid = page.locator('details', { hasText: 'Living Room' }).locator('xpath=..')
  const gridMetrics = await roomsGrid.evaluate((el) => {
    const cs = getComputedStyle(el)
    const inner = el.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight)
    return { display: cs.display, inner, gap: parseFloat(cs.columnGap) }
  })
  expect(gridMetrics.display).toBe('grid')
  const expectedCols = Math.floor((gridMetrics.inner + gridMetrics.gap) / (220 + gridMetrics.gap))
  const expectedTileWidth = (gridMetrics.inner - gridMetrics.gap * (expectedCols - 1)) / expectedCols
  expect(expectedCols).toBeGreaterThanOrEqual(2)
  const roomTiles = roomsGrid.locator('> details')
  await expect(roomTiles).toHaveCount(4)
  const tileBoxes = await Promise.all([0, 1, 2, 3].map(async (i) => (await roomTiles.nth(i).boundingBox())!))
  const firstRowY = tileBoxes[0].y
  const firstRow = tileBoxes.filter((b) => Math.abs(b.y - firstRowY) < 1)
  expect(firstRow.length).toBe(Math.min(expectedCols, 4))
  expect(new Set(firstRow.map((b) => Math.round(b.x))).size).toBe(firstRow.length)
  for (const b of firstRow) expect(b.width).toBeCloseTo(expectedTileWidth, 0)

  // "Invite a member" opens a working invite-generation dialog from inside the Household section.
  await page.route('**/api/household-invites', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ token: 'abcd1234', expiresAtUtc: '2026-08-21T00:00:00Z' }),
    }),
  )
  await page.getByRole('button', { name: 'Invite a member' }).last().click()
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.getByRole('dialog').getByRole('button', { name: 'Invite a member' }).click()
  await expect(page.getByLabel('Invite link')).toBeVisible()
  await page.keyboard.press('Escape')

  // Expand the Living Room and its Wall outlet; the count text and text-link Add controls are the
  // visible ones at this width (not the filled buttons). Locators are scoped to the Room's <details>
  // because the richer Story 8.8 data makes page-wide text locators ambiguous.
  const livingRoom = page.locator('details', { hasText: 'Living Room' })
  await livingRoom.locator('> summary').click()
  await expect(livingRoom.getByText(/4 Power Points/)).toBeVisible()
  const addPowerPointLink = livingRoom.getByRole('button', { name: 'Add Power Point' }).last()
  await expect(addPowerPointLink).toBeVisible()

  // Story 8.8 (AC #2): the open Room spans the whole grid row, alone on it; the next tile is below.
  const roomsGridBox = (await roomsGrid.boundingBox())!
  const openRoomBox = (await livingRoom.boundingBox())!
  expect(openRoomBox.width).toBeCloseTo(gridMetrics.inner, 0)
  const bedroomBox = (await roomTiles.nth(1).boundingBox())!
  expect(bedroomBox.y).toBeGreaterThan(openRoomBox.y + openRoomBox.height - 1)
  expect(roomsGridBox.width).toBeGreaterThan(openRoomBox.width)

  // Nested Power Point grid (AC #1): collapsed Power Points inside the open Room tile are multi-column.
  const ppGrid = livingRoom.locator('> div').filter({ has: page.locator('> details') })
  const ppMetrics = await ppGrid.evaluate((el) => {
    const cs = getComputedStyle(el)
    const inner = el.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight)
    return { display: cs.display, inner, gap: parseFloat(cs.columnGap) }
  })
  expect(ppMetrics.display).toBe('grid')
  const expectedPpCols = Math.floor((ppMetrics.inner + ppMetrics.gap) / (220 + ppMetrics.gap))
  const ppTiles = ppGrid.locator('> details')
  await expect(ppTiles).toHaveCount(4)
  const ppBoxes = await Promise.all([0, 1, 2, 3].map(async (i) => (await ppTiles.nth(i).boundingBox())!))
  expect(ppBoxes.filter((b) => Math.abs(b.y - ppBoxes[0].y) < 1).length).toBe(Math.min(expectedPpCols, 4))

  // Opening a Power Point: spans the nested grid width and shows a single-column Device list (AC #2, #3).
  const wallOutlet = livingRoom.locator('details', { hasText: 'Wall outlet' })
  await wallOutlet.locator('> summary').click()
  await expect(wallOutlet.getByText(/3 Devices/)).toBeVisible()
  const openPpBox = (await wallOutlet.boundingBox())!
  expect(openPpBox.width).toBeCloseTo(ppMetrics.inner, 0)
  const deviceBoxes = await Promise.all(
    ['Kettle', 'Toaster', 'Blender'].map(async (name) => (await wallOutlet.getByText(name, { exact: true }).boundingBox())!),
  )
  expect(new Set(deviceBoxes.map((b) => Math.round(b.x))).size).toBe(1)
  expect(deviceBoxes[0].y).toBeLessThan(deviceBoxes[1].y)
  expect(deviceBoxes[1].y).toBeLessThan(deviceBoxes[2].y)
  const addDeviceLink = wallOutlet.getByRole('button', { name: 'Add Device' }).last()
  await expect(addDeviceLink).toBeVisible()

  // Story 8.7 (AC #3): with the Room -> Power Point -> Device tree fully expanded at a wide
  // viewport, no entry-grid exists anywhere on the page — the tree is not gridded by this story.
  expect((page.viewportSize()?.width ?? 0)).toBeGreaterThanOrEqual(660)
  await expect(page.locator('[data-slot="entry-grid"]')).toHaveCount(0)

  // Story 8.8 (AC #5): at 659px the pair stacks and the tree is the flat Story 8.5 bordered-row list.
  await page.setViewportSize({ width: 659, height: 800 })
  await expect(householdSectionLabel).toBeHidden()
  // The SectionLabel <h2>s (class `hidden`) are display:none here; their parent wrappers are the sections.
  const aiNarrow = (await page.locator('h2.hidden', { hasText: 'AI Plausibility Check' }).locator('xpath=..').boundingBox())!
  const householdNarrow = (await page.locator('h2.hidden', { hasText: 'Household' }).locator('xpath=..').boundingBox())!
  expect(householdNarrow.y).toBeGreaterThan(aiNarrow.y)
  expect(Math.abs(householdNarrow.x - aiNarrow.x)).toBeLessThan(1)
  expect(Math.abs(householdNarrow.width - aiNarrow.width)).toBeLessThan(1)
  expect(await roomsGrid.evaluate((el) => getComputedStyle(el).display)).not.toBe('grid')
  expect(await ppGrid.evaluate((el) => getComputedStyle(el).display)).not.toBe('grid')
  const narrowRooms = await Promise.all([0, 1, 2, 3].map(async (i) => (await roomTiles.nth(i).boundingBox())!))
  expect(new Set(narrowRooms.map((b) => Math.round(b.x))).size).toBe(1)
  for (let i = 1; i < narrowRooms.length; i++) expect(narrowRooms[i].y).toBeGreaterThan(narrowRooms[i - 1].y)
})

// Story 8.10/Task 7: the pre-paint inline script, live OS-follow, and <meta theme-color> are
// browser-only behavior jsdom can't prove (AC #3-#5). Stored choice must beat the OS with no flash.
test('the Profile menu theme toggle overrides the OS, survives reload with no flash, and System follows the OS', async ({
  page,
}) => {
  await page.route('**/api/session', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        hasHousehold: true,
        householdId: '11111111-1111-1111-1111-111111111111',
        locale: 'en-US',
        currency: 'USD',
        supportsFederatedLogout: true,
        email: 'ralf@example.com',
      }),
    }),
  )
  await page.route('**/api/status', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }))
  await page.route('**/api/tariff-check', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }))
  await page.route('**/api/meter-regression-prompts/open', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }),
  )

  const html = page.locator('html')
  const themeColor = page.locator('meta[name="theme-color"]')
  const LIGHT = '#F3F8ED'
  const DARK = '#12201A'

  await page.emulateMedia({ colorScheme: 'light' })
  await page.setViewportSize({ width: 1000, height: 800 })
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Energy Tracker' })).toBeVisible()
  await expect(html).not.toHaveClass(/dark/)
  await expect(themeColor).toHaveAttribute('content', LIGHT)

  // Pick Dark on a light OS: applies immediately, menu stays open.
  await page.getByRole('button', { name: 'Account menu' }).click()
  await page.getByRole('radio', { name: 'Dark', exact: true }).click()
  await expect(html).toHaveClass(/dark/)
  await expect(themeColor).toHaveAttribute('content', DARK)
  await expect(page.getByRole('radio', { name: 'Dark', exact: true })).toBeChecked()
  await expect(page.getByRole('menu')).toBeVisible()

  // Reload on the light OS: the stored choice wins, applied before React mounts (no flash).
  await page.reload({ waitUntil: 'commit' })
  await page.waitForFunction(() => document.readyState !== 'loading')
  expect(await page.evaluate(() => document.documentElement.classList.contains('dark'))).toBe(true)
  expect(await page.evaluate(() => document.querySelector('meta[name="theme-color"]')?.getAttribute('content'))).toBe(DARK)

  // Light chosen while the OS is dark: the theme-color meta follows the effective (light) theme.
  await expect(page.getByRole('heading', { name: 'Energy Tracker' })).toBeVisible()
  await page.getByRole('button', { name: 'Account menu' }).click()
  await page.getByRole('radio', { name: 'Light', exact: true }).click()
  await page.emulateMedia({ colorScheme: 'dark' })
  await expect(html).not.toHaveClass(/dark/)
  await expect(themeColor).toHaveAttribute('content', LIGHT)

  // Back to System: follows the OS live.
  await page.getByRole('radio', { name: /^System/ }).click()
  await expect(html).toHaveClass(/dark/)
  await page.emulateMedia({ colorScheme: 'light' })
  await expect(html).not.toHaveClass(/dark/)
  await expect(themeColor).toHaveAttribute('content', LIGHT)
})
