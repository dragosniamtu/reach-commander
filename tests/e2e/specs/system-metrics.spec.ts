import { expect, test } from '@playwright/test';

test('opens live system metrics from the top-right control', async ({ page }) => {
  await page.goto('/');
  const trigger = page.getByTestId('system-metrics-trigger');
  await expect(trigger).toBeVisible();
  await expect(trigger).toHaveAttribute('aria-haspopup', 'dialog');
  await expect(trigger).toHaveAttribute('data-state', 'disabled');

  await trigger.click();
  const panel = page.getByRole('dialog', { name: 'System metrics' });
  await expect(panel).toBeVisible();
  await expect(panel.getByText('CPU', { exact: true })).toBeVisible();
  await expect(panel.getByText('Memory', { exact: true })).toBeVisible();

  await page.keyboard.press('Escape');
  await expect(panel).toBeHidden();
  await expect(trigger).toBeFocused();
});

test('keeps the compact trigger and details panel inside a phone viewport', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');

  const trigger = page.getByTestId('system-metrics-trigger');
  await expect(trigger).toBeVisible();
  await expect(trigger).toContainText('System');
  await trigger.click();

  const panel = page.getByRole('dialog', { name: 'System metrics' });
  await expect(panel).toBeVisible();
  const bounds = await panel.boundingBox();
  expect(bounds).not.toBeNull();
  expect(bounds!.x).toBeGreaterThanOrEqual(0);
  expect(bounds!.y).toBeGreaterThanOrEqual(0);
  expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(390);
  expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(844);
});
