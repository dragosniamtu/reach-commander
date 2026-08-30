import { expect, test, type Page } from '@playwright/test';
import { systemUpdateFixture } from '../support/seed-fixtures';

const storageKey = 'reachcommander.theme.v1';
const systemUpdateEndpoint = '**/api/system-update**';

test.use({ serviceWorkers: 'block' });

function captureConsoleErrors(page: Page): string[] {
  const consoleErrors: string[] = [];
  page.on('console', (message) => {
    if (message.type() === 'error') {
      consoleErrors.push(message.text());
    }
  });
  return consoleErrors;
}

async function resetTheme(page: Page): Promise<void> {
  await page.goto('/');
  await page.evaluate((key) => localStorage.removeItem(key), storageKey);
  await page.reload();
}

function contrastRatio(foreground: string, background: string): number {
  const luminance = (color: string): number => {
    const channels = color.match(/\d+/g)?.slice(0, 3).map(Number);
    if (!channels || channels.length !== 3) {
      throw new Error(`Expected an RGB color, received ${color}`);
    }
    const [red, green, blue] = channels.map((channel) => {
      const normalized = channel / 255;
      return normalized <= 0.03928
        ? normalized / 12.92
        : ((normalized + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
  };
  const foregroundLuminance = luminance(foreground);
  const backgroundLuminance = luminance(background);
  return (
    (Math.max(foregroundLuminance, backgroundLuminance) + 0.05) /
    (Math.min(foregroundLuminance, backgroundLuminance) + 0.05)
  );
}

test('applies and persists Windows 95 chrome across the commander and authentication screen', async ({
  page,
}, testInfo) => {
  const consoleErrors = captureConsoleErrors(page);
  await resetTheme(page);

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
  const authEyebrowStyles = await authCard.locator('.eyebrow').evaluate((element) => {
    const eyebrow = getComputedStyle(element);
    const card = getComputedStyle(element.closest('.auth-card')!);
    return { color: eyebrow.color, background: card.backgroundColor };
  });
  expect(authEyebrowStyles.color).toBe('rgb(0, 0, 128)');
  expect(contrastRatio(authEyebrowStyles.color, authEyebrowStyles.background)).toBeGreaterThanOrEqual(
    4.5,
  );
  await page.screenshot({
    path: testInfo.outputPath('windows95-authentication.png'),
    fullPage: true,
  });
  expect(consoleErrors).toEqual([]);
});

test('keeps raised Windows 95 header controls and chips dark on gray', async ({ page }) => {
  const consoleErrors = captureConsoleErrors(page);
  await resetTheme(page);
  await page.getByTestId('theme-selector').selectOption('windows95');

  const toolbarButtonStyles = await page
    .getByTestId('toolbar-trash')
    .evaluate((element) => {
      const styles = getComputedStyle(element);
      return {
        radius: styles.borderRadius,
        borderTop: styles.borderTopColor,
        borderRight: styles.borderRightColor,
        borderBottom: styles.borderBottomColor,
        borderLeft: styles.borderLeftColor,
        boxShadow: styles.boxShadow,
        color: styles.color,
        background: styles.backgroundColor,
      };
    });
  expect(toolbarButtonStyles).toMatchObject({
    radius: '0px',
    borderTop: 'rgb(255, 255, 255)',
    borderRight: 'rgb(0, 0, 0)',
    borderBottom: 'rgb(0, 0, 0)',
    borderLeft: 'rgb(255, 255, 255)',
    color: 'rgb(0, 0, 0)',
    background: 'rgb(192, 192, 192)',
  });
  expect(toolbarButtonStyles.boxShadow).toContain('rgb(223, 223, 223)');
  expect(toolbarButtonStyles.boxShadow).toContain('rgb(128, 128, 128)');

  const chipStyles = await page.evaluate(() => {
    const readColor = (selector: string): string => {
      const element = document.querySelector(selector);
      if (!(element instanceof HTMLElement)) {
        throw new Error(`Missing representative Windows 95 element: ${selector}`);
      }
      return getComputedStyle(element).color;
    };
    return {
      title: readColor('[data-testid="active-panel-context"] strong'),
      path: readColor('[data-testid="active-panel-context"] code'),
    };
  });
  expect(chipStyles).toEqual({
    title: 'rgb(0, 0, 0)',
    path: 'rgb(32, 32, 32)',
  });

  const themeLabelStyles = await page.locator('.theme-selector > span').evaluate((element) => {
    const label = getComputedStyle(element);
    const topbar = getComputedStyle(element.closest('.topbar')!);
    return { color: label.color, background: topbar.backgroundColor };
  });
  expect(themeLabelStyles.color).toBe('rgb(255, 255, 255)');
  expect(contrastRatio(themeLabelStyles.color, themeLabelStyles.background)).toBeGreaterThanOrEqual(
    4.5,
  );

  await page.getByTestId('toolbar-trash').focus();
  expect(
    await page.getByTestId('toolbar-trash').evaluate((element) => {
      const styles = getComputedStyle(element);
      return {
        outlineStyle: styles.outlineStyle,
        outlineWidth: styles.outlineWidth,
        outlineColor: styles.outlineColor,
      };
    }),
  ).toEqual({
    outlineStyle: 'dotted',
    outlineWidth: '1px',
    outlineColor: 'rgb(0, 0, 0)',
  });
  expect(consoleErrors).toEqual([]);
});

test('uses common square beveled shells and navy title strips for Windows 95 dialogs', async ({
  page,
}) => {
  const consoleErrors = captureConsoleErrors(page);
  await resetTheme(page);
  await page.getByTestId('theme-selector').selectOption('windows95');

  await page.getByTestId('account-menu-trigger').click();
  await page.getByTestId('change-password').click();
  const passwordDialog = page.getByTestId('change-password-dialog');
  await expect(passwordDialog).toHaveClass(/theme-dialog-shell/);
  await expect(passwordDialog.locator(':scope > header')).toHaveClass(/theme-dialog-titlebar/);
  expect(
    await passwordDialog.evaluate((element) => {
      const shell = getComputedStyle(element);
      const titlebar = getComputedStyle(element.querySelector(':scope > header')!);
      const title = getComputedStyle(element.querySelector(':scope > header h2')!);
      return {
        radius: shell.borderRadius,
        background: shell.backgroundColor,
        borderTop: shell.borderTopColor,
        borderRight: shell.borderRightColor,
        titleBackground: titlebar.backgroundColor,
        titleColor: title.color,
      };
    }),
  ).toEqual({
    radius: '0px',
    background: 'rgb(192, 192, 192)',
    borderTop: 'rgb(255, 255, 255)',
    borderRight: 'rgb(0, 0, 0)',
    titleBackground: 'rgb(0, 0, 128)',
    titleColor: 'rgb(255, 255, 255)',
  });
  await passwordDialog.getByRole('button', { name: 'Close change password dialog' }).click();

  await page.getByTestId('left-panel').focus();
  await page.keyboard.press('F7');
  const directoryDialog = page.getByRole('dialog', { name: 'New directory' });
  await expect(directoryDialog).toHaveClass(/theme-dialog-shell/);
  await expect(directoryDialog.locator(':scope > header')).toHaveClass(/theme-dialog-titlebar/);
  const directoryLabelStyles = await directoryDialog
    .locator('.dialog-body > label')
    .evaluate((element) => {
      const label = getComputedStyle(element);
      const shell = getComputedStyle(element.closest('.theme-dialog-shell')!);
      return { color: label.color, background: shell.backgroundColor };
    });
  expect(contrastRatio(directoryLabelStyles.color, directoryLabelStyles.background)).toBeGreaterThanOrEqual(
    4.5,
  );
  expect(consoleErrors).toEqual([]);
});

test('keeps Windows 95 small status and semantic text readable on gray', async ({ page }) => {
  const consoleErrors = captureConsoleErrors(page);
  await resetTheme(page);
  await page.getByTestId('theme-selector').selectOption('windows95');

  expect(
    await page.locator('html').evaluate((element) => {
      const styles = getComputedStyle(element);
      return {
        mutedToken: styles.getPropertyValue('--text-4').trim(),
        success: styles.getPropertyValue('--success').trim(),
        warning: styles.getPropertyValue('--warning').trim(),
      };
    }),
  ).toEqual({
    mutedToken: '#606060',
    success: '#005a00',
    warning: '#5a4a00',
  });

  const statusStyles = await page.evaluate(() => {
    const read = (selector: string) => {
      const element = document.querySelector(selector);
      if (!(element instanceof HTMLElement)) {
        throw new Error(`Missing representative Windows 95 element: ${selector}`);
      }
      const styles = getComputedStyle(element);
      return { color: styles.color, background: styles.backgroundColor };
    };
    return {
      panelStatus: read('[data-testid="left-panel"] .panel-status'),
      writablePolicy: read('[data-testid="left-panel"] [data-testid="source-downloads"] .writable'),
      readOnlyPolicy: read('[data-testid="left-panel"] [data-testid="source-archive"] .read-only'),
    };
  });
  expect(
    contrastRatio(statusStyles.panelStatus.color, statusStyles.panelStatus.background),
  ).toBeGreaterThanOrEqual(4.5);
  expect(statusStyles.writablePolicy.color).toBe('rgb(0, 90, 0)');
  expect(
    contrastRatio(statusStyles.writablePolicy.color, statusStyles.writablePolicy.background),
  ).toBeGreaterThanOrEqual(4.5);
  expect(statusStyles.readOnlyPolicy.color).toBe('rgb(90, 74, 0)');
  expect(
    contrastRatio(statusStyles.readOnlyPolicy.color, statusStyles.readOnlyPolicy.background),
  ).toBeGreaterThanOrEqual(4.5);

  await page.getByTestId('system-metrics-trigger').click();
  const metricsPanel = page.getByRole('dialog', { name: 'System metrics' });
  await expect(metricsPanel).toBeVisible();
  const metricsLabelStyles = await metricsPanel.locator('dt').first().evaluate((element) => {
    const label = getComputedStyle(element);
    const section = getComputedStyle(element.closest('section')!);
    return { color: label.color, background: section.backgroundColor };
  });
  expect(metricsLabelStyles.color).toBe('rgb(64, 64, 64)');
  expect(contrastRatio(metricsLabelStyles.color, metricsLabelStyles.background)).toBeGreaterThanOrEqual(
    4.5,
  );
  expect(consoleErrors).toEqual([]);
});

test('keeps both Windows 95 panes usable at compact width', async ({ page }, testInfo) => {
  const consoleErrors = captureConsoleErrors(page);

  await page.setViewportSize({ width: 680, height: 800 });
  await resetTheme(page);
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

test('uses square gray Windows 95 chrome for the system update overlay', async (
  { page },
  testInfo,
) => {
  const consoleErrors = captureConsoleErrors(page);
  let current = systemUpdateFixture({
    targetVersion: 'v1.4.0',
    phase: 'available',
    updateAvailable: true,
    canApply: true,
    reasonCode: 'update_available',
    detail: 'A verified ReachCommander update is available.',
  });
  await page.route(systemUpdateEndpoint, async (route) => {
    if (new URL(route.request().url()).pathname === '/api/system-update/apply') {
      current = systemUpdateFixture({
        targetVersion: 'v1.4.0',
        phase: 'applying',
        progressStage: 'downloading',
        updateAvailable: true,
        reasonCode: 'update_applying',
        operationId: 'operation-windows95',
        updatedAt: new Date().toISOString(),
      });
    }
    await route.fulfill({ json: current });
  });

  await resetTheme(page);
  await page.getByTestId('theme-selector').selectOption('windows95');
  await page.getByTestId('system-update-trigger').click();
  const confirmation = page.getByRole('dialog', { name: 'Update ReachCommander?' });
  await expect(confirmation).toHaveClass(/theme-dialog-shell/);
  await expect(confirmation.locator(':scope > header')).toHaveClass(/theme-dialog-titlebar/);
  const confirmationLabelStyles = await confirmation
    .locator('.version-flow span')
    .first()
    .evaluate((element) => {
      const label = getComputedStyle(element);
      const section = getComputedStyle(element.closest('section')!);
      return { color: label.color, background: section.backgroundColor };
    });
  expect(confirmationLabelStyles.color).toBe('rgb(64, 64, 64)');
  expect(
    contrastRatio(confirmationLabelStyles.color, confirmationLabelStyles.background),
  ).toBeGreaterThanOrEqual(4.5);
  await page.getByRole('button', { name: 'Update ReachCommander' }).click();

  const overlay = page.locator('.system-update-overlay');
  await expect(overlay).toBeVisible();
  expect(
    await overlay.evaluate((element) => {
      const styles = getComputedStyle(element);
      const state = getComputedStyle(element.querySelector('.update-state')!);
      const heading = getComputedStyle(element.querySelector('.update-state > div')!);
      const title = getComputedStyle(element.querySelector('h2')!);
      const progressCopy = getComputedStyle(element.querySelector('.progress-copy')!);
      const details = getComputedStyle(element.querySelector('.technical-details')!);
      const spinnerRing = getComputedStyle(element.querySelector('.spinner i')!);
      return {
        radius: styles.borderRadius,
        background: styles.backgroundColor,
        stateBackground: state.backgroundColor,
        headingBackground: heading.backgroundColor,
        titleColor: title.color,
        progressColor: progressCopy.color,
        detailsRadius: details.borderRadius,
        detailsBackground: details.backgroundColor,
        spinnerShadow: spinnerRing.boxShadow,
      };
    }),
  ).toEqual({
    radius: '0px',
    background: 'rgb(192, 192, 192)',
    stateBackground: 'rgb(192, 192, 192)',
    headingBackground: 'rgb(0, 0, 128)',
    titleColor: 'rgb(255, 255, 255)',
    progressColor: 'rgb(0, 0, 128)',
    detailsRadius: '0px',
    detailsBackground: 'rgb(192, 192, 192)',
    spinnerShadow: 'none',
  });
  await page.screenshot({
    path: testInfo.outputPath('windows95-system-update-overlay.png'),
    fullPage: true,
  });
  expect(consoleErrors).toEqual([]);
});
