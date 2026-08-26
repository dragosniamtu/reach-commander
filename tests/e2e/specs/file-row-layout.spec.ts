import { expect, test } from '@playwright/test';
import { longFileNameFixture } from '../support/fixture-names';

const themeStorageKey = 'reachcommander.theme.v1';

for (const norton of [false, true]) {
  test(`centers file rows and ellipsizes long names in ${norton ? 'Norton' : 'default'} theme`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 680, height: 800 });
    await page.goto('/');
    await page.evaluate((key) => localStorage.removeItem(key), themeStorageKey);
    await page.reload();

    if (norton) {
      await page.getByTestId('norton-theme-toggle').click();
      await expect(page.locator('html')).toHaveAttribute('data-theme', 'norton');
    } else {
      await expect(page.locator('html')).not.toHaveAttribute('data-theme', 'norton');
    }

    const panel = page.getByTestId('right-panel');
    await panel.getByTestId('source-media').click();
    await panel.locator('tr[data-path="/Movies"]').dblclick();

    const row = panel.locator(`tr[data-path="/Movies/${longFileNameFixture}"]`);
    const name = row.locator('.file-name');
    await expect(row).toBeVisible();
    await expect(name).toHaveText(longFileNameFixture);
    await expect(name).toHaveAttribute('title', longFileNameFixture);

    const layout = await row.evaluate((element) => {
      const rowRect = element.getBoundingClientRect();
      const nameElement = element.querySelector('.file-name') as HTMLElement;
      const nameContent = element.querySelector('.name-content') as HTMLElement | null;
      const contentRect = nameContent?.getBoundingClientRect();

      return {
        rowHeight: rowRect.height,
        truncated: nameElement.scrollWidth > nameElement.clientWidth,
        verticalAlignments: [...element.querySelectorAll('td')].map(
          (cell) => getComputedStyle(cell).verticalAlign,
        ),
        nameCenterDelta: contentRect
          ? Math.abs(
              contentRect.top + contentRect.height / 2 -
                (rowRect.top + rowRect.height / 2),
            )
          : Number.POSITIVE_INFINITY,
      };
    });

    expect(layout.rowHeight).toBeCloseTo(30, 0);
    expect(layout.truncated).toBe(true);
    expect(layout.verticalAlignments).toEqual(['middle', 'middle', 'middle', 'middle', 'middle']);
    expect(layout.nameCenterDelta).toBeLessThanOrEqual(1);
  });
}
