import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test, type Locator, type Page } from '@playwright/test';

const originalWindows1250 = Buffer.from([
  0x42, 0x75, 0x6e, 0xe3, 0x2c, 0x20, 0xba, 0x74, 0x69, 0x69,
  0x2c, 0x20, 0xfe, 0x61, 0x72, 0xe3, 0x0d, 0x0a,
]);

test.describe.serial('text encoding conversion', () => {
  test('previews legacy text and keeps byte-exact originals during batch conversion', async ({ page }) => {
    const left = await openEncodingLab(page);
    await selectRows(left, [
      '/Encoding Lab/romanian.srt',
      '/Encoding Lab/notes.txt',
    ]);

    await page.getByTestId('toolbar-text-encoding').click();
    const dialog = page.getByTestId('text-encoding-dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText('Windows-1250');
    await expect(dialog).toContainText('low');
    await expect(dialog).toContainText('Bună, ştii, ţară');
    await expect(dialog).toContainText('The legacy encoding is ambiguous');
    await dialog.getByLabel('Output encoding').selectOption('utf8');
    await dialog.getByRole('button', { name: 'Convert files', exact: true }).click();

    await expect(dialog).toContainText('Conversion completed', { timeout: 20_000 });
    await expect(dialog).toContainText('/Encoding Lab/romanian_original.srt');
    await expect(dialog).toContainText('/Encoding Lab/notes_original.txt');
    await dialog.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(dialog).toBeHidden();

    const downloadsRoot = process.env['REACHCOMMANDER_E2E_DOWNLOADS_ROOT'];
    expect(downloadsRoot).toBeTruthy();
    const lab = join(downloadsRoot!, 'Encoding Lab');
    expect(readFileSync(join(lab, 'romanian_original.srt'))).toEqual(originalWindows1250);
    expect(readFileSync(join(lab, 'romanian.srt'), 'utf8')).toContain('Bună, ştii, ţară');
    expect(readFileSync(join(lab, 'romanian.srt')).subarray(0, 3))
      .not.toEqual(Buffer.from([0xef, 0xbb, 0xbf]));
    expect(readFileSync(join(lab, 'notes_original.txt'), 'utf8'))
      .toBe('UTF-8 notes with diacritics: ăîâșț.\r\n');
  });

  test('explains unsupported selections and blocks binary conversion', async ({ page }) => {
    const left = await openEncodingLab(page);
    await left.locator('tr[data-path="/Encoding Lab/photo.jpg"]').click();

    const encoding = page.getByTestId('toolbar-text-encoding');
    await expect(encoding).toBeDisabled();
    await expect(encoding.locator('..')).toHaveAttribute(
      'title',
      'Select at least one supported text file.',
    );

    await left.locator('tr[data-path="/Encoding Lab/binary.sub"]').click();
    await expect(encoding).toBeEnabled();
    await encoding.click();
    const dialog = page.getByTestId('text-encoding-dialog');
    await expect(dialog).toContainText('Invalid');
    await expect(dialog).toContainText(/binary/i);
    await expect(dialog.getByRole('button', { name: 'Convert files', exact: true })).toBeDisabled();
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  });

  test('contains its table inside a phone viewport', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    const left = await openEncodingLab(page);
    await left.locator('tr[data-path="/Encoding Lab/notes.txt"]').click();
    await page.getByTestId('toolbar-text-encoding').dispatchEvent('click');
    await expect(page.getByTestId('text-encoding-dialog')).toBeVisible();

    const documentOverflow = await page.evaluate(
      () => document.documentElement.scrollWidth - window.innerWidth,
    );
    expect(documentOverflow).toBeLessThanOrEqual(1);
  });
});

async function openEncodingLab(page: Page): Promise<Locator> {
  await page.goto('/');
  const left = page.getByTestId('left-panel');
  await left.getByTestId('source-downloads').click();
  await left.locator('tr[data-path="/Encoding Lab"]').dblclick();
  await expect(left.locator('.path-status')).toHaveText('Downloads:/Encoding Lab');
  return left;
}

async function selectRows(panel: Locator, paths: readonly string[]): Promise<void> {
  await panel.locator(`tr[data-path="${paths[0]}"]`).click();
  for (const path of paths.slice(1)) {
    await panel.locator(`tr[data-path="${path}"]`).click({ modifiers: ['Control'] });
  }
  await expect(panel.locator('tbody tr[aria-selected="true"]')).toHaveCount(paths.length);
}
