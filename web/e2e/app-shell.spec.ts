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
// wide:max-w-[660px] wrapper class and label spans exist in markup, never which one a real browser
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
  const contentColumn = page.locator('[data-slot="dashboard-content"]')
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
  expect(narrowColumnBox?.width).toBeGreaterThan(400) // tracks the 500px viewport, not capped at 660px

  await page.setViewportSize({ width: 1000, height: 800 })
  await expect(eventLabel).toBeVisible()
  await expect(importLabel).toBeVisible()
  const wideColumnBox = await contentColumn.boundingBox()
  expect(wideColumnBox?.width).toBeLessThanOrEqual(660)
})
