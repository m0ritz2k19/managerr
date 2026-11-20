import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: 0,
  workers: 1,
  reporter: [
    ['list'],
    ['html', { open: 'never', outputFolder: 'playwright-report' }],
  ],
  use: {
    baseURL: 'http://127.0.0.1:5189',
    trace: 'retain-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  webServer: {
    command:
      'sh -c \'TMP_CFG="/tmp/managerr-pw-smoke-$$"; rm -rf "$TMP_CFG" && mkdir -p "$TMP_CFG" && trap "rm -rf \\"$TMP_CFG\\"" EXIT; npm run build && Managerr__ConfigDirectory="$TMP_CFG" Managerr__TestbedBootstrap__Enabled=true Managerr__TestbedBootstrap__Username=smokeadmin Managerr__TestbedBootstrap__Password="SmokeTestPassword123!" dotnet run --project ../../src/Managerr.Host/Managerr.Host.csproj --urls http://127.0.0.1:5189 -p:AllowMissingPrunePackageData=true\'',
    url: 'http://127.0.0.1:5189/api/v1/auth/status',
    reuseExistingServer: false,
    timeout: 180_000,
  },
});
