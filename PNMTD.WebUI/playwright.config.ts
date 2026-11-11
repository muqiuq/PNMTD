import { defineConfig, devices } from '@playwright/test'

/**
 * The browser tests (docs/webui-migration-plan.md §8.6), run by tools/e2e.sh in the Playwright image
 * against a stack of their own. E2E_BASE_URL is the app as the browser container reaches it.
 */
export default defineConfig({
  testDir: 'e2e',
  outputDir: 'test-results/output',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  timeout: 60_000,
  expect: { timeout: 10_000 },
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:7398',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } }],
})
