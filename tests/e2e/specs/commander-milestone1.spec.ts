import { expect, test } from '@playwright/test';

test('operates two independent panes and restores the commander workspace', async ({ page }) => {
  await page.goto('/');

  const left = page.getByTestId('left-panel');
  const right = page.getByTestId('right-panel');
  await expect(left).toBeVisible();
  await expect(right).toBeVisible();

  for (const panel of [left, right]) {
    await expect(panel.getByTestId('source-downloads')).toBeVisible();
    await expect(panel.getByTestId('source-media')).toBeVisible();
  }

  await left.getByTestId('source-downloads').click();
  await right.getByTestId('source-media').click();
  await expect(left.getByTestId('source-downloads')).toHaveAttribute('aria-pressed', 'true');
  await expect(right.getByTestId('source-media')).toHaveAttribute('aria-pressed', 'true');
  await expect(right.locator('tr[data-path="/Extracted"]')).toBeVisible();

  await right.click();
  await page.keyboard.press('ArrowDown');
  await expect(right.locator('tr[data-path="/Extracted"]')).toHaveClass(/cursor/);
  await right.locator('tr[data-path="/Movies"]').click();
  await page.keyboard.press('Enter');
  await expect(right.locator('.path-status')).toHaveText('Media:/Movies');
  await expect(right.getByText('Gladiator II.mkv')).toBeVisible();
  await expect(left.locator('.path-status')).toHaveText('Downloads:/');

  await page.keyboard.press('Backspace');
  await expect(right.locator('.path-status')).toHaveText('Media:/');
  await page.keyboard.press('Tab');
  await expect(left).toHaveClass(/active/);
  await expect(right).not.toHaveClass(/active/);

  await page.keyboard.press('Insert');
  await expect(left.locator('tr[data-path="/Complete"]')).toHaveAttribute('aria-selected', 'true');
  const selectableRowCount = await left.locator('tbody tr').count();
  await page.keyboard.press('Control+A');
  await expect(left.locator('tbody tr[aria-selected="true"]')).toHaveCount(selectableRowCount);

  const initialTabCount = await left.getByRole('tab').count();
  await page.keyboard.press('Control+T');
  await expect(left.getByRole('tab')).toHaveCount(initialTabCount + 1);
  await page.keyboard.press('Control+W');
  await expect(left.getByRole('tab')).toHaveCount(initialTabCount);

  await page.keyboard.type('inc');
  await expect(page.getByRole('searchbox', { name: 'Search active panel' })).toHaveValue('inc');
  await expect(left.locator('tbody tr')).toHaveCount(1);
  await expect(left.getByText('Incomplete', { exact: true })).toBeVisible();

  await right.click();
  await right.locator('tr[data-path="/Movies"]').click();
  await page.keyboard.press('Enter');
  await expect(right.locator('.path-status')).toHaveText('Media:/Movies');
  const persistedTabCount = await right.getByRole('tab').count();
  await page.keyboard.press('Control+T');
  await expect(right.getByRole('tab')).toHaveCount(persistedTabCount + 1);

  await page.reload();
  await expect(left.getByTestId('source-downloads')).toHaveAttribute('aria-pressed', 'true');
  await expect(right.getByTestId('source-media')).toHaveAttribute('aria-pressed', 'true');
  await left.click();
  await expect(page.getByRole('searchbox', { name: 'Search active panel' })).toHaveValue('inc');
  await expect(right.locator('.path-status')).toHaveText('Media:/Movies');
  await expect(right.getByRole('tab')).toHaveCount(persistedTabCount + 1);
  await expect(right.getByText('Gladiator II.mkv')).toBeVisible();
});

test('extends and shrinks Shift+Arrow selection in only the active pane', async ({ page }) => {
  await page.goto('/');

  const left = page.getByTestId('left-panel');
  const right = page.getByTestId('right-panel');
  await left.getByTestId('source-downloads').click();
  await right.getByTestId('source-media').click();
  await left.locator('tr[data-path="/Rename Lab"]').dblclick();
  await expect(left.locator('.path-status')).toHaveText('Downloads:/Rename Lab');

  const oppositeCursorPath = await right.locator('tr.cursor').getAttribute('data-path');
  await left.locator('tr.parent').click();
  await page.keyboard.press('Shift+ArrowDown');
  await page.keyboard.press('Shift+ArrowDown');

  await expect(left.locator('tr.parent')).toHaveAttribute('aria-selected', 'false');
  await expect(left.locator('tr[data-path="/Rename Lab/Drafts"]')).toHaveAttribute('aria-selected', 'true');
  await expect(left.locator('tr[data-path="/Rename Lab/holiday-photo.jpg"]')).toHaveAttribute('aria-selected', 'true');
  await expect(right.locator('tr[aria-selected="true"]')).toHaveCount(0);
  await expect(right.locator('tr.cursor')).toHaveAttribute('data-path', oppositeCursorPath);

  await page.keyboard.press('Shift+ArrowUp');
  await expect(left.locator('tr[data-path="/Rename Lab/Drafts"]')).toHaveAttribute('aria-selected', 'true');
  await expect(left.locator('tr[data-path="/Rename Lab/holiday-photo.jpg"]')).toHaveAttribute('aria-selected', 'false');

  await expect(page.locator('.shortcut-hint')).toContainText('Shift+↑/↓ range select');
  await page.keyboard.press('F9');
  await expect(page.getByRole('dialog', { name: 'Commander commands' })).toContainText('Shift+↑/↓');
});
