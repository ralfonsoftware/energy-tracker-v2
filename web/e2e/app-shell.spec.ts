import { expect, test } from '@playwright/test'

test('the SPA shell loads', async ({ page }) => {
  await page.goto('/')

  await expect(page).toHaveTitle('Energy Tracker')
  await expect(page.getByRole('heading', { name: 'Energy Tracker' })).toBeVisible()
})
