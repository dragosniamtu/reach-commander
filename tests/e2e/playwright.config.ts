import { defineConfig, devices } from '@playwright/test';
import { dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const configDirectory = dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  testDir: './specs',
  outputDir: '../../artifacts/playwright-results',
  fullyParallel: false,
  retries: 0,
  reporter: [['list'], ['html', { outputFolder: '../../artifacts/playwright-report', open: 'never' }]],
  use: {
    baseURL: 'http://127.0.0.1:8092',
    locale: 'en-US',
    timezoneId: 'UTC',
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
    video: 'off',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } },
    },
  ],
  webServer: {
    command: 'npx tsx support/seed-fixtures.ts',
    cwd: configDirectory,
    url: 'http://127.0.0.1:8092/health',
    reuseExistingServer: false,
    timeout: 120_000,
  },
});
