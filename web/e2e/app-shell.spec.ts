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
