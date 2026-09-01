import { expect, test, type Page } from '@playwright/test';
import { routeMediaPreview } from '../support/media-preview-fixture';

test.use({ serviceWorkers: 'block' });

test('synchronizes an SRT while preserving the original mapping', async ({ page }) => {
  const media = await routeMediaPreview(page);
  const { panel, row } = await openMediaMovie(page, 'Family Movie.mp4');

  await row.dblclick();
  const dialog = page.getByRole('dialog', { name: 'Synchronize subtitles' });
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText('/Movies/Family Movie.srt');
  await dialog.getByRole('button', { name: '+1000 ms' }).click();
  await dialog.getByRole('button', { name: 'Review save' }).click();
  await expect(dialog).toContainText('/Movies/Family Movie_original.srt');
  await dialog.getByRole('button', { name: 'Save corrected subtitle' }).click();

  await expect(dialog).toContainText('Corrected subtitle saved');
  expect(media.offsets).toEqual([1_000]);
  expect(media.executeCount).toBe(1);
  expect(JSON.stringify(media.requests)).not.toMatch(/(?:[A-Z]:\\|\/srv\/|\/tmp\/)/i);

  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(row).toBeFocused();
  await expect(panel.locator('.path-status')).toHaveText('Media:/Movies');
});

test('opens from the keyboard, selects another same-directory SRT, and protects unsaved work', async ({ page }) => {
  const media = await routeMediaPreview(page);
  const { row } = await openMediaMovie(page, 'Family Movie.mp4');
  await row.click();
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog', { name: 'Synchronize subtitles' });

  const subtitlePicker = dialog.getByLabel('SRT file in this directory');
  await expect(subtitlePicker.locator('option')).toHaveText([
    'Alternate.srt',
    'Fallback Movie.srt',
    'Family Movie.srt',
  ]);
  await subtitlePicker.selectOption('/Movies/Alternate.srt');
  await expect(dialog).toContainText('/Movies/Alternate.srt');
  await dialog.getByRole('button', { name: '+500 ms' }).click();

  page.once('dialog', (confirmation) => confirmation.dismiss());
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(dialog).toBeVisible();
  page.once('dialog', (confirmation) => confirmation.accept());
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(media.subtitlePaths).toEqual(['/Movies/Alternate.srt']);
});

test('shows bounded MKV fallback preparation before the workspace becomes ready', async ({ page }) => {
  const media = await routeMediaPreview(page);
  const { row } = await openMediaMovie(page, 'Fallback Movie.mkv');

  await row.dblclick();
  const dialog = page.getByRole('dialog', { name: 'Synchronize subtitles' });
  await expect(dialog).toContainText('Waiting for preview worker');
  await expect(dialog).toContainText('Preparing browser-compatible video');
  await expect(dialog.getByText('Subtitle synchronization', { exact: true }))
    .toBeVisible({ timeout: 5_000 });
  expect(media.statusReads).toBeGreaterThan(1);
});

test('cancels a queued preview from the close control', async ({ page }) => {
  const media = await routeMediaPreview(page);
  const { row } = await openMediaMovie(page, 'Fallback Movie.mkv');

  await row.dblclick();
  const dialog = page.getByRole('dialog', { name: 'Synchronize subtitles' });
  await expect(dialog).toContainText('Waiting for preview worker');
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();

  await expect(dialog).toBeHidden();
  expect(media.requests).toContainEqual(expect.objectContaining({
    method: 'DELETE',
    path: '/api/media-previews/55555555-5555-4555-8555-555555555555',
  }));
});

test('offers explicit HLS fallback when direct browser playback fails', async ({ page }) => {
  const media = await routeMediaPreview(page, { failDirectContentOnce: true });
  const { row } = await openMediaMovie(page, 'Family Movie.mp4');
  await row.dblclick();
  const dialog = page.getByRole('dialog', { name: 'Synchronize subtitles' });

  const fallback = dialog.getByRole('button', { name: 'Prepare compatible preview' });
  await expect(fallback).toBeVisible({ timeout: 5_000 });
  await fallback.click();
  await expect(dialog).toContainText('Waiting for preview worker');
  await expect.poll(() => media.fallbackRequests).toBe(1);
});

test('keeps a read-only preview non-mutating and edge-to-edge on a phone viewport', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await routeMediaPreview(page);
  await page.goto('/');
  const panel = page.getByTestId('left-panel');
  await panel.getByTestId('source-archive').click();
  const row = panel.locator('tr[data-path="/Read Only Movie.mp4"]');
  await row.dblclick();
  const dialog = page.getByRole('dialog', { name: 'Synchronize subtitles' });

  await expect(dialog).toContainText('this source is read-only');
  await dialog.getByRole('button', { name: '+1000 ms' }).click();
  await expect(dialog.getByRole('button', { name: 'Review save' })).toHaveCount(0);
  const bounds = await dialog.boundingBox();
  expect(bounds).not.toBeNull();
  expect(bounds!.x).toBeLessThanOrEqual(1);
  expect(bounds!.y).toBeLessThanOrEqual(1);
  expect(bounds!.width).toBeGreaterThanOrEqual(389);
  expect(bounds!.height).toBeGreaterThanOrEqual(843);
});

async function openMediaMovie(page: Page, name: string) {
  await page.goto('/');
  const panel = page.getByTestId('right-panel');
  await panel.getByTestId('source-media').click();
  await panel.locator('tr[data-path="/Movies"]').dblclick();
  await expect(panel.locator('.path-status')).toHaveText('Media:/Movies');
  const row = panel.getByText(name, { exact: true }).locator('xpath=ancestor::tr');
  await expect(row).toBeVisible();
  return { panel, row };
}
