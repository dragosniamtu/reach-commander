import { expect, test, type Page } from '@playwright/test';

test('renames one file with F4 and restores the renamed row', async ({ page }) => {
  const left = await openLabFolder(page, 'File Case');
  await left.getByText('draft.txt', { exact: true }).click();

  await page.keyboard.press('F4');
  const dialog = page.getByTestId('single-rename-dialog');
  await expect(dialog).toContainText('Rename file');
  await dialog.getByLabel('New name').fill('final.txt');
  await expect(dialog.getByTestId('single-rename-submit')).toBeEnabled();
  await dialog.getByTestId('single-rename-submit').click();

  await expect(dialog).toBeHidden();
  await expect(left.getByText('final.txt', { exact: true })).toBeVisible();
  await expect(left.getByText('draft.txt', { exact: true })).toHaveCount(0);
  await expect(left.locator('tr[data-path="/Single Rename Lab/File Case/final.txt"]'))
    .toHaveClass(/cursor/);
});

test('renames one folder with F4', async ({ page }) => {
  const left = await openLabFolder(page, 'Folder Case');
  await left.getByText('Old Folder', { exact: true }).click();

  await page.keyboard.press('F4');
  const dialog = page.getByTestId('single-rename-dialog');
  await expect(dialog).toContainText('Rename folder');
  await dialog.getByLabel('New name').fill('New Folder');
  await expect(dialog.getByTestId('single-rename-submit')).toBeEnabled();
  await dialog.getByTestId('single-rename-submit').click();

  await expect(dialog).toBeHidden();
  await expect(left.getByText('New Folder', { exact: true })).toBeVisible();
  await expect(left.getByText('Old Folder', { exact: true })).toHaveCount(0);
});

test('treats mask-looking characters as a literal single filename', async ({ page }) => {
  const left = await openLabFolder(page, 'Literal Case');
  await left.getByText('literal.txt', { exact: true }).click();

  await page.keyboard.press('F4');
  const dialog = page.getByTestId('single-rename-dialog');
  await dialog.getByLabel('New name').fill('[N]-a+b.txt');
  await expect(dialog.getByTestId('single-rename-submit')).toBeEnabled();
  await dialog.getByTestId('single-rename-submit').click();

  await expect(dialog).toBeHidden();
  await expect(left.getByText('[N]-a+b.txt', { exact: true })).toBeVisible();
});

test('blocks a destination conflict without changing either file', async ({ page }) => {
  const left = await openLabFolder(page, 'Conflict Case');
  await left.getByText('source.txt', { exact: true }).click();

  await page.keyboard.press('F4');
  const dialog = page.getByTestId('single-rename-dialog');
  await dialog.getByLabel('New name').fill('existing.txt');

  await expect(dialog).toContainText('The destination name is already in use.');
  await expect(dialog.getByTestId('single-rename-submit')).toBeDisabled();
  await dialog.getByRole('button', { name: 'Cancel' }).click();
  await expect(left.getByText('source.txt', { exact: true })).toBeVisible();
  await expect(left.getByText('existing.txt', { exact: true })).toBeVisible();
});

test('keeps F4 unavailable on a read-only source', async ({ page }) => {
  await page.goto('/');
  const left = page.getByTestId('left-panel');
  await left.getByTestId('source-archive').click();
  await left.getByText('locked.txt', { exact: true }).click();

  const rename = page.locator('[data-key="F4"]');
  await expect(rename).toBeDisabled();
  await expect(rename).toHaveAttribute('title', 'Archive is read-only.');
});

async function openLabFolder(page: Page, folder: string) {
  await page.goto('/');
  const left = page.getByTestId('left-panel');
  await left.getByText('Single Rename Lab', { exact: true }).dblclick();
  await left.getByText(folder, { exact: true }).dblclick();
  await expect(left.locator('.path-status')).toHaveText(`Downloads:/Single Rename Lab/${folder}`);
  return left;
}
