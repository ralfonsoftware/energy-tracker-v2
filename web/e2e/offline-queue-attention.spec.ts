import { expect, test, type Page, type Route } from '@playwright/test'

// Story 11.16 (audit C4): a reading the server rejects after an offline flush stays on the device and
// shows on the Dashboard until it is corrected and resent, or discarded. Component tests prove the
// logic; this proves the real browser path (an `online` event, IndexedDB, a reload, the layout).

const householdId = '11111111-1111-1111-1111-111111111111'
const futureReason = "Reading timestamp '2099-12-31T10:00:00+00:00' is too far in the future."

async function mockApp(page: Page, locale = 'en-US') {
  await page.route('**/api/session', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ hasHousehold: true, householdId, locale, currency: 'USD', supportsFederatedLogout: true, email: 'ralf@example.com' }),
    }),
  )
  await page.route('**/api/status', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }))
  await page.route('**/api/tariff-check', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }))
  await page.route('**/api/meter-regression-prompts/open', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }),
  )
}

// Answers every POST /api/meter-readings with the current handler and records the bodies.
function meterReadingPosts(page: Page) {
  const bodies: Record<string, unknown>[] = []
  let answer: (route: Route) => Promise<void> = (route) =>
    route.fulfill({ status: 400, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Bad Request', status: 400, detail: futureReason }) })
  void page.route('**/api/meter-readings', async (route) => {
    if (route.request().method() !== 'POST') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' })
    }
    bodies.push(route.request().postDataJSON())
    return answer(route)
  })
  return {
    bodies,
    answerOk: () => {
      answer = (route) =>
        route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: 'r1', kwhValue: 4800.5, readingTimestamp: new Date().toISOString() }) })
    },
  }
}

async function seedRejectedEntry(page: Page, key: string) {
  await page.evaluate(
    ([db, store, entry]) =>
      new Promise<void>((resolve, reject) => {
        const open = indexedDB.open(db as string)
        open.onerror = () => reject(open.error)
        open.onsuccess = () => {
          const tx = open.result.transaction(store as string, 'readwrite')
          tx.objectStore(store as string).put(entry)
          tx.oncomplete = () => resolve()
          tx.onerror = () => reject(tx.error)
        }
      }),
    ['energy-tracker-offline-queue', 'pending-meter-readings', {
      householdId,
      kwhValue: 4812,
      readingTimestamp: '2099-12-31T10:00:00.000Z',
      idempotencyKey: key,
      rejection: { status: 400, detail: 'Seeded reason.', rejectedAtUtc: new Date().toISOString() },
    }],
  )
}

async function logReadingOffline(page: Page) {
  await page.context().setOffline(true)
  await page.getByRole('button', { name: /Log reading/ }).first().click()
  await page.getByLabel('kWh').fill('4830.25')
  await page.getByLabel('Date & time').fill('2099-12-31T10:00')
  await page.getByRole('button', { name: 'Save reading' }).click()
  await expect(page.getByText("Saved — will sync when you're back online")).toBeVisible()
  await page.keyboard.press('Escape')
}

test('offline reading rejected on reconnect: the card appears, survives a reload, resend keeps the key, discard removes', async ({ page }) => {
  await mockApp(page)
  const posts = meterReadingPosts(page)
  await page.goto('/')
  await expect(page.getByRole('button', { name: /Log reading/ }).first()).toBeVisible()
  await expect(page.getByText(/needs? your attention/)).toHaveCount(0)

  await logReadingOffline(page)
  expect(posts.bodies).toHaveLength(0)

  // (a) back online: the flush is rejected and the card shows without a reload.
  await page.context().setOffline(false)
  await expect(page.getByRole('heading', { name: '1 reading needs your attention' })).toBeVisible()
  await expect(page.getByText(futureReason)).toBeVisible()
  expect(posts.bodies).toHaveLength(1)
  const firstKey = posts.bodies[0].idempotencyKey

  // (b) durable: the card is still there after a reload, and the entry was not posted again.
  await page.reload()
  await expect(page.getByText(futureReason)).toBeVisible()
  expect(posts.bodies).toHaveLength(1)

  // (c) correct and resend: same key, the card goes.
  posts.answerOk()
  await page.getByRole('button', { name: /^Correct and resend the reading/ }).click()
  const dialog = page.getByRole('dialog', { name: 'Correct and resend' })
  await expect(dialog.getByText(futureReason)).toBeVisible()
  await dialog.getByLabel('kWh').fill('4800.5')
  await dialog.getByLabel('Date & time').fill('2026-10-28T09:30')
  await dialog.getByRole('button', { name: 'Resend' }).click()
  await expect(page.getByText(/needs? your attention/)).toHaveCount(0)
  expect(posts.bodies).toHaveLength(2)
  expect(posts.bodies[1].idempotencyKey).toBe(firstKey)
  expect(posts.bodies[1].kwhValue).toBe(4800.5)

  // (d) discard a second, seeded entry through the confirmation: nothing is sent.
  await seedRejectedEntry(page, 'seeded-key')
  await page.reload()
  await expect(page.getByText('Seeded reason.')).toBeVisible()
  await page.getByRole('button', { name: /^Discard the reading/ }).click()
  await expect(page.getByText("Discard this reading? It can't be recovered.")).toBeVisible()
  await page.getByRole('button', { name: 'Discard', exact: true }).click()
  await expect(page.getByText('Seeded reason.')).toHaveCount(0)
  expect(posts.bodies).toHaveLength(2)
})

test('the card fits a 400px viewport without horizontal overflow', async ({ page }) => {
  await page.setViewportSize({ width: 400, height: 900 })
  await mockApp(page)
  meterReadingPosts(page)
  await page.goto('/')
  await expect(page.getByRole('button', { name: /Log reading/ }).first()).toBeVisible()
  await seedRejectedEntry(page, 'seeded-1')
  await page.reload()

  const heading = page.getByRole('heading', { name: '1 reading needs your attention' })
  await expect(heading).toBeVisible()
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)
  expect(overflow).toBe(true)
  const box = await page.locator('[aria-labelledby="rejected-readings-title"]').boundingBox()
  expect(box).not.toBeNull()
  expect(box!.x).toBeGreaterThanOrEqual(0)
  expect(box!.x + box!.width).toBeLessThanOrEqual(400)
})

test('the wide layout keeps the actions on the row, inside the 900px column', async ({ page }) => {
  await page.setViewportSize({ width: 1000, height: 900 })
  await mockApp(page)
  meterReadingPosts(page)
  await page.goto('/')
  await expect(page.getByRole('button', { name: /Log reading/ }).first()).toBeVisible()
  await seedRejectedEntry(page, 'seeded-1')
  await page.reload()

  const card = page.locator('[aria-labelledby="rejected-readings-title"]')
  await expect(card).toBeVisible()
  const box = await card.boundingBox()
  expect(box!.width).toBeLessThanOrEqual(900)
  const row = card.getByRole('listitem').first()
  const rowBox = await row.boundingBox()
  const actionBox = await row.getByRole('button').first().boundingBox()
  expect(actionBox!.y + actionBox!.height).toBeLessThanOrEqual(rowBox!.y + rowBox!.height)
})

test('a German household sees the German labels; the server reason stays as sent', async ({ page }) => {
  await mockApp(page, 'de-DE')
  meterReadingPosts(page)
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Energy Tracker' })).toBeVisible()
  await seedRejectedEntry(page, 'seeded-1')
  await page.reload()

  await expect(page.getByRole('heading', { name: '1 Zählerstand braucht deine Aufmerksamkeit' })).toBeVisible()
  await expect(page.getByText('Seeded reason.')).toBeVisible()
  await expect(page.getByRole('button', { name: /korrigieren und erneut senden$/ })).toBeVisible()
})
