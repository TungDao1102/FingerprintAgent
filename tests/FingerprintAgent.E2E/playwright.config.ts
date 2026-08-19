import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright configuration for FingerprintAgent E2E tests.
 *
 * Why Playwright 1.55.1 is pinned:
 *   Chromium 142+ (Playwright 1.56.x) blocks public-origin fetches to private
 *   networks (localhost) by default and requires:
 *     - launchOptions.args: ['--ip-address-space-overrides=127.0.0.1:0=public']
 *     - use.permissions: ['local-network-access']
 *   1.55.1 ships Chromium 141, which does NOT prompt. Revisit when 1.56+ stabilizes.
 *
 * Agent assumption:
 *   Tests assume the FingerprintAgent Windows Service is already running on
 *   http://127.0.0.1:5043 with `scanner.mockMode: true`. The CI workflow
 *   (.github/workflows/e2e.yml) installs + starts the MSI before running tests.
 *   Local dev: run scripts/Service.ps1 start or scripts/Test-Capture.ps1.
 *
 * Mock backend:
 *   The webServer block below auto-starts fixtures/mock-backend.ts on port 8080
 *   during local development. CI starts it as a separate step (we still
 *   reuseExistingServer=true so local devs running their own backend works).
 */
export default defineConfig({
  testDir: './specs',
  timeout: 30_000,
  expect: { timeout: 10_000 },
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: process.env.CI
    ? [['github'], ['html', { open: 'never' }]]
    : [['list']],

  use: {
    baseURL: 'http://127.0.0.1:8080',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },

  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],

  // Auto-start the mock backend during `playwright test` for local development.
  // In CI the workflow runs it as a background step; reuseExistingServer=true
  // means a dev who started their own mock backend on 8080 will not conflict.
  webServer: {
    command: 'npx ts-node fixtures/mock-backend.ts',
    port: 8080,
    reuseExistingServer: !process.env.CI,
    timeout: 10_000,
    stdout: 'pipe',
    stderr: 'pipe',
  },
});
