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
  // off-by-one drift in `--breakpoint-wide` or the `max-w-[900px]` literal would go undetected.
  await page.setViewportSize({ width: 659, height: 800 })
  await expect(eventLabel).toBeHidden()
  await expect(importLabel).toBeHidden()

  await page.setViewportSize({ width: 660, height: 800 })
  await expect(eventLabel).toBeVisible()
  await expect(importLabel).toBeVisible()

  // 660-900px: the column fills the viewport (minus page padding) rather than pinning to a cap (AC #2).
  await page.setViewportSize({ width: 800, height: 800 })
  const midColumnBox = await contentColumn.boundingBox()
  expect(midColumnBox?.width).toBeGreaterThan(750)
  expect(midColumnBox?.width).toBeLessThan(800)

  await page.setViewportSize({ width: 1000, height: 800 })
  await expect(eventLabel).toBeVisible()
  await expect(importLabel).toBeVisible()
  const wideColumnBox = await contentColumn.boundingBox()
  expect(wideColumnBox?.width).toBeGreaterThan(800) // must actually be a ~900px column, not collapsed
  expect(wideColumnBox?.width).toBeLessThanOrEqual(900)
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
  // Seed one reading so the table actually renders, not the empty state (Task 5's own instruction).
  await page.route('**/api/meter-readings?*', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        items: [
          {
            id: '11111111-1111-1111-1111-111111111111',
            kwhValue: 100,
            readingTimestamp: '2026-08-15T14:32:00+00:00',
            version: 0,
            isPendingRegression: false,
            correctedFromKwhValue: null,
            correctedAtUtc: null,
          },
        ],
        totalCount: 1,
        page: 1,
        pageSize: 20,
      }),
    }),
  )
  await page.route('**/api/events?*', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], totalCount: 0, page: 1, pageSize: 20 }) }),
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

  // Exact-boundary check, same as the Dashboard case above: 659px stays icon-only, 660px is labeled.
  await page.setViewportSize({ width: 659, height: 800 })
  await expect(importLabel).toBeHidden()

  await page.setViewportSize({ width: 660, height: 800 })
  await expect(importLabel).toBeVisible()

  // 660-900px: the column fills the viewport (minus page padding) rather than pinning to a cap (AC #2).
  await page.setViewportSize({ width: 800, height: 800 })
  const midColumnBox = await contentColumn.boundingBox()
  expect(midColumnBox?.width).toBeGreaterThan(750)
  expect(midColumnBox?.width).toBeLessThan(800)

  await page.setViewportSize({ width: 1000, height: 800 })
  await expect(importLabel).toBeVisible()
  const wideColumnBox = await contentColumn.boundingBox()
  expect(wideColumnBox?.width).toBeGreaterThan(800) // must actually be a ~900px column, not collapsed
  expect(wideColumnBox?.width).toBeLessThanOrEqual(900)

  // Task 3: no large blank gap between the rendered Timestamp text and the Edit button, at any
  // width (this fix is unconditional, not wide:-gated — contrast with the column/label assertions
  // above). Measured against the Timestamp *text*'s own rect (via a DOM Range, not the padded
  // <td> box) and the Edit *button*'s own rect (not its cell) — comparing the two cells' boundary
  // boxes instead would always read ~0px regardless of whether the w-px fix is present, since
  // adjacent table cells in the same row are always contiguous.
  await page.getByText('Meter Readings — 1 logged').click()
  const editButton = page.getByRole('button', { name: /Edit reading from/ })
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
  // Seed one current Tariff so TariffComparisonForm actually renders (tariff-radar-page.tsx's
  // currentTariffCurrency gating).
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
        ],
        totalCount: 1,
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
  expect(midColumnBox?.width).toBeGreaterThan(750)
  expect(midColumnBox?.width).toBeLessThan(800)

  await page.setViewportSize({ width: 1000, height: 800 })
  baseFeeBox = await monthlyBaseFeeField.boundingBox()
  priceBox = await pricePerKwhField.boundingBox()
  expect(Math.abs(baseFeeBox!.y - priceBox!.y)).toBeLessThan(2)
  expect(priceBox!.x).toBeGreaterThan(baseFeeBox!.x)

  const wideColumnBox = await contentColumn.boundingBox()
  expect(wideColumnBox?.width).toBeGreaterThan(800) // must actually be a ~900px column, not collapsed
  expect(wideColumnBox?.width).toBeLessThanOrEqual(900)

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
      body: JSON.stringify([{ id: 'r1', name: 'Living Room', archivedAt: null }]),
    }),
  )
  await page.route('**/api/power-points', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ id: 'p1', roomId: 'r1', name: 'Wall outlet', archivedAt: null }]),
    }),
  )
  await page.route('**/api/devices', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { id: 'd1', powerPointId: 'p1', name: 'Kettle', archivedAt: null },
        { id: 'd2', powerPointId: 'p1', name: 'Toaster', archivedAt: null },
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
  expect(midColumnBox?.width).toBeGreaterThan(750)
  expect(midColumnBox?.width).toBeLessThan(800)

  await page.setViewportSize({ width: 1000, height: 800 })

  const wideColumnBox = await contentColumn.boundingBox()
  expect(wideColumnBox?.width).toBeGreaterThan(800) // must actually be a ~900px column, not collapsed
  expect(wideColumnBox?.width).toBeLessThanOrEqual(900)
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

  // Expand the seeded Room and Power Point; the count text and text-link Add controls are the
  // visible ones at this width (not the filled buttons).
  await page.getByText('Living Room', { exact: false }).click()
  await expect(page.getByText(/1 Power Point\b/)).toBeVisible()
  const addPowerPointLink = page.getByRole('button', { name: 'Add Power Point' }).last()
  await expect(addPowerPointLink).toBeVisible()

  await page.getByText('Wall outlet', { exact: false }).click()
  await expect(page.getByText(/2 Devices/)).toBeVisible()
  const addDeviceLink = page.getByRole('button', { name: 'Add Device' }).last()
  await expect(addDeviceLink).toBeVisible()
})
