import { expect, test } from '@playwright/test';

const storageKey = 'reachcommander.theme.v1';

test.beforeEach(async ({ page }) => {
  await page.goto('/');
  await page.evaluate((key) => localStorage.removeItem(key), storageKey);
  await page.reload();
});

test('applies and persists Windows 95 chrome across the commander and authentication screen', async ({
  page,
}, testInfo) => {
  const consoleErrors: string[] = [];
  page.on('console', (message) => {
    if (message.type() === 'error') {
      consoleErrors.push(message.text());
    }
  });

  const root = page.locator('html');
  const selector = page.getByTestId('theme-selector');
  await selector.selectOption('windows95');

  await expect(root).toHaveAttribute('data-theme', 'windows95');
  expect(
    await root.evaluate((element) => {
      const styles = getComputedStyle(element);
      return {
        appBackground: styles.getPropertyValue('--app-bg').trim(),
        surface: styles.getPropertyValue('--surface-1').trim(),
        title: styles.getPropertyValue('--title-bar').trim(),
        selection: styles.getPropertyValue('--selection').trim(),
      };
    }),
  ).toEqual({
    appBackground: '#008080',
    surface: '#c0c0c0',
    title: '#000080',
    selection: '#000080',
  });

  const toolbarButtonStyles = await page
    .locator('app-active-panel-toolbar .toolbar button')
    .first()
    .evaluate((element) => {
      const styles = getComputedStyle(element);
      return {
        radius: styles.borderRadius,
        borderTop: styles.borderTopColor,
        borderRight: styles.borderRightColor,
        borderBottom: styles.borderBottomColor,
        borderLeft: styles.borderLeftColor,
        boxShadow: styles.boxShadow,
      };
    });
  expect(toolbarButtonStyles).toMatchObject({
    radius: '0px',
    borderTop: 'rgb(255, 255, 255)',
    borderRight: 'rgb(0, 0, 0)',
    borderBottom: 'rgb(0, 0, 0)',
    borderLeft: 'rgb(255, 255, 255)',
  });
  expect(toolbarButtonStyles.boxShadow).toContain('rgb(223, 223, 223)');
  expect(toolbarButtonStyles.boxShadow).toContain('rgb(128, 128, 128)');

  expect(
    await page
      .getByTestId('left-panel')
      .locator('tbody tr.cursor')
      .evaluate((element) => {
        const styles = getComputedStyle(element);
        return {
          background: styles.backgroundColor,
          color: styles.color,
        };
      }),
  ).toEqual({
    background: 'rgb(0, 0, 128)',
    color: 'rgb(255, 255, 255)',
  });

  await page.screenshot({
    path: testInfo.outputPath('windows95-theme-1440.png'),
    fullPage: true,
  });

  await page.reload();
  await expect(root).toHaveAttribute('data-theme', 'windows95');
  await expect(selector).toHaveValue('windows95');
  expect(await page.evaluate((key) => localStorage.getItem(key), storageKey)).toBe(
    'windows95',
  );

  await page.getByTestId('account-menu-trigger').click();
  await page.getByTestId('logout').click();
  const authCard = page.locator('.auth-card');
  await expect(authCard).toBeVisible();
  expect(await authCard.evaluate((element) => getComputedStyle(element).backgroundColor)).toBe(
    'rgb(192, 192, 192)',
  );
  await page.screenshot({
    path: testInfo.outputPath('windows95-authentication.png'),
    fullPage: true,
  });
  expect(consoleErrors).toEqual([]);
});

test('keeps both Windows 95 panes usable at compact width', async ({ page }, testInfo) => {
  const consoleErrors: string[] = [];
  page.on('console', (message) => {
    if (message.type() === 'error') {
      consoleErrors.push(message.text());
    }
  });

  await page.setViewportSize({ width: 680, height: 800 });
  await page.reload();
  await page.getByTestId('theme-selector').selectOption('windows95');

  await expect(page.locator('html')).toHaveAttribute('data-theme', 'windows95');
  await expect(page.getByTestId('left-panel')).toBeVisible();
  await expect(page.getByTestId('right-panel')).toBeVisible();
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth),
  ).toBeLessThanOrEqual(1);

  await page.screenshot({
    path: testInfo.outputPath('windows95-theme-680.png'),
    fullPage: true,
  });
  await page.getByTestId('right-panel').screenshot({
    path: testInfo.outputPath('windows95-theme-680-right-panel.png'),
  });
  expect(consoleErrors).toEqual([]);
});
