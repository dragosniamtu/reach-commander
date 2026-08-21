import { expect, test } from '@playwright/test';

test('registers the production shell, keeps API data out of caches, and reloads offline', async ({
  context,
  page,
}) => {
  test.setTimeout(60_000);
  await page.goto('/');
  await expect(page.getByText('ReachCommander', { exact: true })).toBeVisible();

  await page.evaluate(async () => {
    await navigator.serviceWorker.ready;
  });
  await page.reload();
  await expect
    .poll(() => page.evaluate(() => Boolean(navigator.serviceWorker.controller)))
    .toBe(true);

  expect(await page.evaluate(async () => Boolean(await caches.match('/api/sources')))).toBe(false);

  await context.setOffline(true);
  await page.reload({ waitUntil: 'domcontentloaded' });
  await expect(page.getByText('ReachCommander', { exact: true })).toBeVisible();
  await expect(page.getByTestId('connection-notice')).toContainText(
    /offline|server is unavailable/i,
  );
  await context.setOffline(false);
});
