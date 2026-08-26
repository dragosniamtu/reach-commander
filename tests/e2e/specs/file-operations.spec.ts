import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test, type Locator, type Page } from '@playwright/test';

const permanentWarning =
  'This deletion is permanent, cannot be undone, and is unrecoverable.';

test.describe.serial('file operations', () => {
  test('copies multiple files, resolves a conflict with a unique name, and restores background progress', async ({ page }) => {
    const { left, right } = await openCommander(page);
    await openNested(left, ['File Ops', 'Copy Source'], 'Downloads');
    await openNested(right, ['File Ops', 'Copy Target'], 'Media');
    await selectRows(left, ['/File Ops/Copy Source/alpha.bin', '/File Ops/Copy Source/beta.bin']);

    await page.keyboard.press('F5');
    const confirmation = page.getByTestId('copy-move-dialog');
    await expect(confirmation).toContainText('2 items');
    await expect(confirmation).toContainText('media:/File Ops/Copy Target');
    await confirmation.getByLabel('Conflict action for alpha.bin').selectOption('createUniqueName');
    await confirmation.getByRole('button', { name: 'Start', exact: true }).click();

    const progress = page.getByTestId('transfer-progress-dialog');
    await progress.getByRole('button', { name: 'Background', exact: true }).click();
    const indicator = page.getByRole('button', { name: /Copy .*Open task details/i });
    await expect(indicator).toBeVisible();
    await indicator.click();
    await finishVisibleTask(page);

    await expect(right.locator('tr[data-path="/File Ops/Copy Target/alpha.bin"]')).toBeVisible();
    await expect(right.locator('tr[data-path="/File Ops/Copy Target/alpha (2).bin"]')).toBeVisible();
    await expect(right.locator('tr[data-path="/File Ops/Copy Target/beta.bin"]')).toBeVisible();
    await expect(left.getByText('copy-canary.txt', { exact: true })).toBeVisible();
  });

  test('moves one file, leaves its canary, and creates one directory with F7', async ({ page }) => {
    const { left, right } = await openCommander(page);
    await openNested(left, ['File Ops', 'Move Source'], 'Downloads');
    await openNested(right, ['File Ops', 'Move Target'], 'Media');
    await left.locator('tr[data-path="/File Ops/Move Source/move-me.iso"]').click();

    await page.keyboard.press('F6');
    await page.getByTestId('copy-move-dialog').getByRole('button', { name: 'Start', exact: true }).click();
    await finishVisibleTask(page);

    await expect(left.getByText('move-me.iso', { exact: true })).toHaveCount(0);
    await expect(left.getByText('move-canary.txt', { exact: true })).toBeVisible();
    await expect(right.getByText('move-me.iso', { exact: true })).toBeVisible();

    await left.focus();
    await page.keyboard.press('Backspace');
    await left.locator('tr[data-path="/File Ops/New Directory"]').dblclick();
    await page.keyboard.press('F7');
    const directoryDialog = page.getByRole('dialog', { name: 'New directory' });
    await directoryDialog.getByLabel('Directory name').fill('Family');
    await directoryDialog.getByLabel('Directory name').press('Enter');
    await expect(left.getByText('Family', { exact: true })).toBeVisible();
  });

  test('deletes to managed Trash and restores with Create Unique Name', async ({ page }) => {
    const { left } = await openCommander(page);
    await openNested(left, ['File Ops', 'Delete Source'], 'Downloads');
    await left.locator('tr[data-path="/File Ops/Delete Source/photo.jpg"]').click();
    await page.keyboard.press('F8');
    const deletion = page.getByRole('dialog', { name: 'Move to Trash' });
    await expect(deletion).not.toContainText(permanentWarning);
    await deletion.getByRole('button', { name: 'Move to Trash', exact: true }).click();
    await finishBackgroundTask(page, /Trash .*Open task details/i);
    await expect(left.getByText('photo.jpg', { exact: true })).toHaveCount(0);
    await expect(left.getByText('delete-canary.txt', { exact: true })).toBeVisible();

    const downloadsRoot = process.env['REACHCOMMANDER_E2E_DOWNLOADS_ROOT'];
    expect(downloadsRoot).toBeTruthy();
    writeFileSync(join(downloadsRoot!, 'File Ops', 'Delete Source', 'photo.jpg'), 'replacement photo\n');
    await left.focus();
    await page.keyboard.press('Control+R');
    await expect(left.getByText('photo.jpg', { exact: true })).toBeVisible();

    await page.getByTestId('toolbar-trash').click();
    const trash = page.getByRole('dialog', { name: 'Trash' });
    const row = trash.locator('.trash-row').filter({ hasText: 'photo.jpg' });
    await row.locator('input[type="checkbox"]').check();
    await trash.getByRole('button', { name: 'Restore selected' }).click();
    await trash.getByLabel(/Conflict action for .*photo\.jpg/).selectOption('createUniqueName');
    await trash.getByRole('button', { name: 'Restore now' }).click();
    await trash.getByRole('button', { name: 'Close', exact: true }).click();
    await finishBackgroundTask(page, /Restore .*Open task details/i);

    await expect(left.getByText('photo.jpg', { exact: true })).toBeVisible();
    await expect(left.getByText('photo (2).jpg', { exact: true })).toBeVisible();
    expect(readFileSync(join(downloadsRoot!, 'File Ops', 'Delete Source', 'photo.jpg'), 'utf8'))
      .toBe('replacement photo\n');
  });

  test('requires the exact warning before permanent deletion', async ({ page }) => {
    const { left } = await openCommander(page);
    await openNested(left, ['File Ops', 'Permanent Source'], 'Downloads');
    await left.locator('tr[data-path="/File Ops/Permanent Source/doomed.txt"]').click();
    await page.keyboard.press('F8');
    const deletion = page.locator('.delete-dialog');
    const permanent = deletion.getByLabel('Permanent delete');
    await permanent.check();
    await expect(deletion).toContainText(permanentWarning);
    await deletion.getByRole('button', { name: 'Delete forever' }).click();
    await finishBackgroundTask(page, /Delete .*Open task details/i);
    await expect(left.getByText('doomed.txt', { exact: true })).toHaveCount(0);
  });
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

async function openNested(panel: Locator, names: readonly string[], sourceName: string) {
  let path = '';
  for (const name of names) {
    path += `/${name}`;
    await panel.locator(`tr[data-path="${path}"]`).dblclick();
  }
  await expect(panel.locator('.path-status')).toHaveText(`${sourceName}:${path}`);
}

async function selectRows(panel: Locator, paths: readonly string[]) {
  await panel.locator(`tr[data-path="${paths[0]}"]`).click();
  for (const path of paths.slice(1)) {
    await panel.locator(`tr[data-path="${path}"]`).click({ modifiers: ['Control'] });
  }
  await expect(panel.locator('tbody tr[aria-selected="true"]')).toHaveCount(paths.length);
}

async function finishVisibleTask(page: Page) {
  const dialog = page.getByTestId('transfer-progress-dialog');
  await dialog.getByRole('button', { name: 'Close', exact: true }).waitFor({ timeout: 20_000 });
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(dialog).toBeHidden();
}

async function finishBackgroundTask(page: Page, label: RegExp) {
  const indicator = page.getByRole('button', { name: label });
  await expect(indicator).toBeVisible();
  await indicator.click();
  await finishVisibleTask(page);
}
