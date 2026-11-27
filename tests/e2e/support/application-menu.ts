import { expect, Page } from '@playwright/test';

export async function openApplicationMenu(page: Page): Promise<void> {
  const trigger = page.getByTestId('application-menu-trigger');
  await expect(trigger).toBeVisible();
  await expect(page.getByRole('dialog').and(page.locator(':not(.application-menu-panel)'))).toHaveCount(0);
  await trigger.press('ArrowDown');
  await expect(page.getByRole('dialog', { name: 'Application menu', exact: true })).toBeVisible();
}

export async function selectTheme(page: Page, theme: string): Promise<void> {
  await openApplicationMenu(page);
  await page.getByTestId('theme-selector').selectOption(theme);
  await page.getByTestId('theme-selector').press('Escape');
  await expect(page.getByTestId('application-menu-trigger')).toHaveAttribute('aria-expanded', 'false');
}

export async function openSubtitlesMenu(page: Page): Promise<void> {
  await openApplicationMenu(page);
  const subtitles = page.getByTestId('toolbar-subtitles-menu');
  if (await subtitles.getAttribute('aria-expanded') !== 'true') await subtitles.click();
  await expect(page.getByRole('group', { name: 'Subtitles', exact: true })).toBeVisible();
}
