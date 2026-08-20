import { rmSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test, type Locator, type Page } from '@playwright/test';

test('browses a ZIP as a retained read-only archive tab with wildcard search', async ({ page }) => {
  const { left } = await openCommander(page);

  await left.locator('tr[data-path="/nested.zip"]').dblclick();
  await expect(left.locator('.mode')).toHaveText('Archive · RO');
  await expect(left.locator('.path-status')).toHaveText('Downloads:/nested.zip!/');
  await expect(left.getByRole('tab', { name: /nested\.zip/i })).toBeVisible();

  await left.locator('tr[data-path="/Family"]').dblclick();
  await left.locator('tr[data-path="/Family/2025"]').dblclick();
  const search = page.getByRole('searchbox', { name: 'Search active panel' });
  await search.fill('*.txt');
  await expect(left.locator('tbody tr[data-path="/Family/2025/photo.txt"]')).toBeVisible();
  await expect(left.locator('tbody tr[data-path="/Family/2025/nested.zip"]')).toHaveCount(0);

  await search.fill('');
  await left.focus();
  await page.keyboard.press('Backspace');
  await page.keyboard.press('Backspace');
  await expect(left.locator('.path-status')).toHaveText('Downloads:/nested.zip!/');
  await expect(left.getByRole('tab', { name: /nested\.zip/i })).toBeVisible();
});

test('extracts one selected archive entry into the captured opposite folder', async ({ page }) => {
  const { left, right } = await openCommander(page);
  await openRightFolder(right, '/Extracted');
  await left.locator('tr[data-path="/nested.zip"]').dblclick();
  await left.locator('tr[data-path="/Family"]').dblclick();
  await left.locator('tr[data-path="/Family/2025"]').dblclick();
  await left.locator('tr[data-path="/Family/2025/photo.txt"]').click();
  await page.keyboard.press('Insert');
  await page.keyboard.press('F5');

  const dialog = page.getByRole('dialog', { name: 'Extract archive' });
  await expect(dialog).toContainText('Downloads:/nested.zip!/Family/2025');
  await expect(dialog).toContainText('Media:/Extracted');
  await expect(dialog).toContainText('photo.txt');
  await dialog.getByRole('button', { name: 'Extract', exact: true }).click();
  await expect(dialog).toContainText('Extraction completed', { timeout: 15_000 });
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();

  await expect(right.locator('tr[data-path="/Extracted/photo.txt"]')).toBeVisible();
});

test('extracts a focused unopened archive without adding a wrapper directory', async ({ page }) => {
  const { left, right } = await openCommander(page);
  await openRightFolder(right, '/Whole');
  await left.locator('tr[data-path="/nested.zip"]').click();
  await page.keyboard.press('F5');

  const dialog = page.getByRole('dialog', { name: 'Extract archive' });
  await expect(dialog).toContainText('3');
  await expect(dialog).toContainText('Media:/Whole');
  await dialog.getByRole('button', { name: 'Extract', exact: true }).click();
  await expect(dialog).toContainText('Extraction completed', { timeout: 15_000 });
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();

  await expect(right.locator('tr[data-path="/Whole/root.txt"]')).toBeVisible();
  await expect(right.locator('tr[data-path="/Whole/Family"]')).toBeVisible();
  await expect(right.locator('tr[data-path="/Whole/nested"]')).toHaveCount(0);
});

test('blocks destination conflicts without exposing partial final names', async ({ page, request }) => {
  const { left, right } = await openCommander(page);
  await openRightFolder(right, '/Conflicts');
  await left.locator('tr[data-path="/nested.zip"]').click();
  await page.keyboard.press('F5');

  const dialog = page.getByRole('dialog', { name: 'Extract archive' });
  await expect(dialog).toContainText('Extraction is blocked');
  await expect(dialog.getByRole('button', { name: 'Extract', exact: true })).toBeDisabled();
  const listing = await request.get('/api/files?sourceId=media&path=%2FConflicts');
  expect(listing.ok()).toBe(true);
  const payload = await listing.json() as Array<{ name: string; size: number }>;
  const conflicts = payload.filter(entry => entry.name === 'root.txt');
  expect(conflicts).toHaveLength(1);
  expect(conflicts[0]).toMatchObject({ name: 'root.txt', size: 17 });
  expect(payload.some(entry => entry.name.includes('.reachcommander-extract-'))).toBe(false);
});

test('guides a secondary RAR volume to its primary part', async ({ page }) => {
  const { left } = await openCommander(page);

  await left.locator('tr[data-path="/Rar.multi.part02.rar"]').dblclick();

  await expect(left).toContainText("Open the primary archive volume '/Rar.multi.part01.rar'.");
  await expect(left.locator('.path-status')).toHaveText('Downloads:/');
});

test('restores a missing persisted archive tab with a safe return action', async ({ page }) => {
  const { left } = await openCommander(page);
  await left.locator('tr[data-path="/stale.zip"]').dblclick();
  await expect(left.locator('.mode')).toHaveText('Archive · RO');

  const downloadsRoot = process.env['REACHCOMMANDER_E2E_DOWNLOADS_ROOT'];
  expect(downloadsRoot).toBeTruthy();
  rmSync(join(downloadsRoot!, 'stale.zip'));
  await page.reload();

  await expect(left.getByRole('button', { name: 'Return to parent folder' })).toBeVisible();
  await expect(left).toContainText('archive');
  await left.getByRole('button', { name: 'Return to parent folder' }).click();
  await expect(left.locator('.path-status')).toHaveText('Downloads:/');
  await expect(left.locator('.mode')).not.toHaveText('Archive · RO');
});

async function openCommander(page: Page) {
  await page.goto('/');
  const left = page.getByTestId('left-panel');
  const right = page.getByTestId('right-panel');
  await left.getByTestId('source-downloads').click();
  await right.getByTestId('source-media').click();
  await expect(left.locator('.path-status')).toHaveText('Downloads:/');
  await expect(right.locator('.path-status')).toHaveText('Media:/');
  return { left, right };
}

async function openRightFolder(right: Locator, path: string) {
  await right.locator(`tr[data-path="${path}"]`).dblclick();
  await expect(right.locator('.path-status')).toHaveText(`Media:${path}`);
}
