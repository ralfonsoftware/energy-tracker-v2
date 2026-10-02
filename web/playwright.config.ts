import fs from 'node:fs'
import path from 'node:path'
import { defineConfig, devices } from '@playwright/test'

// `vite preview` inherits `server.https` from vite.config.ts: it serves HTTPS whenever the local
// dev cert exists (certs/ is gitignored) and plain HTTP otherwise (CI). Mirror the same check so the
// base URL and the webServer readiness probe always match the scheme the preview actually uses.
const useHttps =
  fs.existsSync(path.resolve(import.meta.dirname, '../certs/vite-dev-cert.pem')) &&
  fs.existsSync(path.resolve(import.meta.dirname, '../certs/vite-dev-cert.key'))
const baseURL = `${useHttps ? 'https' : 'http'}://localhost:4173`

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  reporter: 'list',
  use: {
    baseURL,
    // The local dev cert is trusted by the OS keychain but not by Playwright's bundled Chromium.
    ignoreHTTPSErrors: useHttps,
    trace: 'on-first-retry',
  },
  webServer: {
    command: 'npm run preview',
    url: baseURL,
    ignoreHTTPSErrors: useHttps,
    reuseExistingServer: !process.env.CI,
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
})
