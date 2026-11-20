import { expect, test, type Page } from '@playwright/test';

function attachErrorCollectors(page: Page) {
  const consoleErrors: string[] = [];
  const pageErrors: string[] = [];
  const failedResponses: string[] = [];

  page.on('console', (msg) => {
    if (msg.type() === 'error') {
      consoleErrors.push(msg.text());
    }
  });

  page.on('pageerror', (err) => {
    pageErrors.push(err.message);
  });

  page.on('response', (res) => {
    if (res.status() >= 400) {
      failedResponses.push(`${res.status()} ${res.request().method()} ${res.url()}`);
    }
  });

  return { consoleErrors, pageErrors, failedResponses };
}

async function loginViaBreakGlass(page: Page) {
  await page.goto('/');

  const heading = page.locator('#signin-heading');
  await expect(heading).toBeVisible();
  const headingText = (await heading.textContent()) ?? '';

  if (/Initial Setup/i.test(headingText)) {
    await page.locator('#claim-username').fill('smokeowner');
    await page.locator('#claim-password').fill('SmokeTestPassword123!');
    await page.getByRole('button', { name: 'Create Owner Account & Claim' }).click();
  } else {
    await page.locator('#signin-username').fill('smokeadmin');
    await page.locator('#signin-password').fill('SmokeTestPassword123!');
    await page.getByRole('button', { name: 'Sign In', exact: true }).click();
  }

  await expect(page).toHaveURL(/\/library$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Library' })).toBeVisible();
  await expect(page).toHaveTitle('Library | Managerr');
}

test.describe('Managerr SPA Smoke E2E', () => {
  test('logs in via break-glass and navigates routes with reload and back/forward without console or API errors', async ({
    page,
  }) => {
    await loginViaBreakGlass(page);
    const { consoleErrors, pageErrors, failedResponses } = attachErrorCollectors(page);

    // Verify break-glass setup endpoint also succeeds with active CSRF session
    const setupStatus = await page.evaluate(async () => {
      const meRes = await fetch('/api/v1/auth/me', { credentials: 'include' });
      const me = (await meRes.json()) as { csrfToken: string };
      const res = await fetch('/api/v1/auth/breakglass/setup', {
        method: 'POST',
        credentials: 'include',
        headers: {
          'Content-Type': 'application/json',
          'X-Managerr-Csrf': me.csrfToken,
        },
        body: JSON.stringify({ newPassword: 'SmokeTestPassword123!' }),
      });
      return res.status;
    });
    expect(setupStatus).toBe(200);

    const routesToVisit: Array<{ path: string; heading: string; title: string }> = [
      { path: '/library', heading: 'Library', title: 'Library | Managerr' },
      { path: '/wanted/missing', heading: 'Wanted: Missing', title: 'Wanted: Missing | Managerr' },
      { path: '/activity/queue', heading: 'Activity Queue', title: 'Activity Queue | Managerr' },
      { path: '/settings/indexers', heading: 'Indexers', title: 'Indexers | Managerr' },
      { path: '/settings/downloaders', heading: 'Download Clients', title: 'Download Clients | Managerr' },
      { path: '/system/status', heading: 'System Status', title: 'System Status | Managerr' },
    ];

    for (const route of routesToVisit) {
      await page.goto(route.path);
      await expect(page).toHaveURL(new RegExp(`${route.path}$`));
      await expect(page.getByRole('heading', { level: 1, name: route.heading })).toBeVisible();
      await expect(page).toHaveTitle(route.title);
    }

    // Reload on /system/status preserves URL and heading
    await page.reload();
    await expect(page).toHaveURL(/\/system\/status$/);
    await expect(page.getByRole('heading', { level: 1, name: 'System Status' })).toBeVisible();
    await expect(page).toHaveTitle('System Status | Managerr');

    // Browser Back/Forward preserves URL and heading
    await page.goBack();
    await expect(page).toHaveURL(/\/settings\/downloaders$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Download Clients' })).toBeVisible();
    await expect(page).toHaveTitle('Download Clients | Managerr');

    await page.goForward();
    await expect(page).toHaveURL(/\/system\/status$/);
    await expect(page.getByRole('heading', { level: 1, name: 'System Status' })).toBeVisible();
    await expect(page).toHaveTitle('System Status | Managerr');

    expect(consoleErrors).toEqual([]);
    expect(pageErrors).toEqual([]);
    expect(failedResponses).toEqual([]);
  });

  test('navigates all Cleanerr routes when authenticated without console, page, or API errors', async ({
    page,
  }) => {
    await loginViaBreakGlass(page);
    const { consoleErrors, pageErrors, failedResponses } = attachErrorCollectors(page);

    const cleanerrRoutes: Array<{
      path: string;
      title: string;
      navLabel: string;
      readyCheck: () => Promise<void>;
    }> = [
      {
        path: '/cleanerr',
        title: 'Cleanerr Overview | Managerr',
        navLabel: 'Overview',
        readyCheck: async () => {
          await expect(page.getByText('Per-Library Disk Stats')).toBeVisible();
        },
      },
      {
        path: '/cleanerr/studio',
        title: 'Cleanerr Studio | Managerr',
        navLabel: 'Studio',
        readyCheck: async () => {
          await expect(page.getByRole('heading', { name: /Saved Workflows/i })).toBeVisible();
        },
      },
      {
        path: '/cleanerr/queue',
        title: 'Leaving Soon Queue | Managerr',
        navLabel: 'Leaving Soon',
        readyCheck: async () => {
          await expect(page.getByText('Leaving Soon & Deletion Queue')).toBeVisible();
        },
      },
      {
        path: '/cleanerr/media',
        title: 'Cleanerr Media Catalog | Managerr',
        navLabel: 'Media Catalog',
        readyCheck: async () => {
          await expect(page.getByRole('searchbox', { name: 'Search media library' })).toBeVisible();
        },
      },
      {
        path: '/cleanerr/exclusions',
        title: 'Cleanerr Exclusions | Managerr',
        navLabel: 'Exclusions',
        readyCheck: async () => {
          await expect(page.getByText('Create Exclusion Rule')).toBeVisible();
        },
      },
      {
        path: '/cleanerr/discord',
        title: 'Cleanerr Discord & Settings | Managerr',
        navLabel: 'Discord & Settings',
        readyCheck: async () => {
          await expect(page.getByText('Discord Bot Credentials & Discovery')).toBeVisible();
        },
      },
    ];

    for (const route of cleanerrRoutes) {
      await page.goto(route.path);
      await expect(page).toHaveURL(new RegExp(`${route.path}$`));
      await expect(page).toHaveTitle(route.title);
      const subNav = page.getByRole('navigation', { name: 'Cleanerr sections' });
      await expect(subNav).toBeVisible();
      await expect(subNav.getByRole('link', { name: route.navLabel })).toHaveAttribute(
        'aria-current',
        'page',
      );
      await route.readyCheck();
    }

    expect(consoleErrors).toEqual([]);
    expect(pageErrors).toEqual([]);
    expect(failedResponses).toEqual([]);
  });
});
