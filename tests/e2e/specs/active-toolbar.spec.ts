import { openApplicationMenu, openSubtitlesMenu, selectTheme } from '../support/application-menu';
import { expect, test } from "@playwright/test";

test("tracks active-panel context and independent wildcard searches", async ({
  page,
}) => {
  await page.goto("/");

  const left = page.getByTestId("left-panel");
  const right = page.getByTestId("right-panel");
  const context = page.getByTestId("active-panel-context");
  const search = page.getByRole("searchbox", { name: "Search active panel" });

  await expect(context).toHaveAttribute(
    "aria-label",
    "left panel, Downloads, Downloads:/",
  );
  await page.keyboard.press("Control+F");
  await expect(search).toBeFocused();
  await search.fill("*.txt");
  await expect(left.getByText("existing.txt", { exact: true })).toBeVisible();
  await expect(left.getByText("Rename Lab", { exact: true })).toHaveCount(0);

  await search.fill("report-??.pdf");
  await expect(left.getByText("report-01.pdf", { exact: true })).toBeVisible();
  await expect(left.getByText("report-1.pdf", { exact: true })).toHaveCount(0);

  await search.fill("a+b[1].txt");
  await expect(left.getByText("a+b[1].txt", { exact: true })).toBeVisible();
  await page.getByTestId("toolbar-clear-search").click();
  await expect(search).toHaveValue("");

  await left.click();
  await page.keyboard.type("rename");
  await expect(search).toHaveValue("rename");
  await page.keyboard.press("Backspace");
  await expect(search).toHaveValue("renam");
  await page.keyboard.press("Escape");
  await expect(search).toHaveValue("");

  await search.fill("download");
  await right.click();
  await expect(context).toHaveAttribute("aria-label", "right panel, Media, Media:/");
  await expect(search).toHaveValue("");
  await search.fill("movie");
  await left.click();
  await expect(search).toHaveValue("download");
  await right.click();
  await expect(search).toHaveValue("movie");
});

test("shows accessible source policies and captures operation destinations", async ({
  page,
}) => {
  await page.goto("/");

  const left = page.getByTestId("left-panel");
  const right = page.getByTestId("right-panel");
  await expect(left.getByTestId("source-downloads")).toHaveAccessibleName(
    /read\/write/i,
  );
  await expect(right.getByTestId("source-media")).toHaveAccessibleName(
    /read\/write/i,
  );
  await expect(left.getByTestId("source-usb")).toHaveAccessibleName(
    /unavailable.*read-only/i,
  );

  await left.click();
  const addFiles = page.getByTestId("toolbar-add-files");
  const fileChooserPromise = page.waitForEvent("filechooser");
  await openApplicationMenu(page);
  await addFiles.click();
  const fileChooser = await fileChooserPromise;
  await fileChooser.setFiles({
    name: "captured.txt",
    mimeType: "text/plain",
    buffer: Buffer.from("captured"),
  });
  const uploadDialog = page.getByRole("dialog", { name: "Add files" });
  await expect(uploadDialog.locator(".destination")).toContainText("Downloads");
  await right.dispatchEvent("pointerdown");
  await expect(uploadDialog.locator(".destination")).toContainText("Downloads");
  await expect(page.getByTestId("active-panel-context")).toHaveAttribute(
    "aria-label",
    "right panel, Media, Media:/",
  );
  await left.dispatchEvent("pointerdown");
  await page.keyboard.press("Escape");
  await expect(uploadDialog).toBeHidden();
  await expect(page.getByTestId('application-menu-trigger')).toBeFocused();

  await left.click();
  await left.getByText("Rename Lab", { exact: true }).click();
  const rename = page.getByTestId("toolbar-multi-rename");
  await openApplicationMenu(page);
  await rename.click();
  let renameDialog = page.getByTestId("multi-rename-dialog");
  await expect(renameDialog).toContainText("Downloads · /");
  await renameDialog
    .getByRole("button", { name: "Close Multi-Rename" })
    .click();
  await expect(page.getByTestId('application-menu-trigger')).toBeFocused();

  await page.keyboard.press("Control+M");
  renameDialog = page.getByTestId("multi-rename-dialog");
  await expect(renameDialog).toBeVisible();
  await expect(renameDialog).toContainText("1 unchanged");
  await page.keyboard.press("Escape");
  await expect(renameDialog).toBeHidden();
});

test("keeps labeled menu actions inside the viewport in every theme", async ({ page }, testInfo) => {
  for (const width of [1440, 680]) {
    await page.setViewportSize({ width, height: 800 });
    await page.goto('/');
    for (const theme of ['default', 'norton', 'windows95']) {
      await selectTheme(page, theme);
      await openSubtitlesMenu(page);
      const menu = page.getByRole('dialog', { name: 'Application menu', exact: true });
      await expect(menu.getByText('File actions', { exact: true })).toBeVisible();
      for (const id of ['toolbar-add-source', 'toolbar-extract', 'toolbar-multi-rename',
        'toolbar-text-encoding', 'toolbar-subtitles-menu', 'toolbar-subtitles',
        'toolbar-repair-subtitles', 'toolbar-subtitle-jobs',
        'toolbar-add-files', 'toolbar-trash', 'system-update-trigger', 'system-metrics-trigger',
        'change-password', 'logout']) {
        const action = menu.getByTestId(id);
        await expect(action).toBeVisible();
        await expect(action.locator('svg').first()).toBeVisible();
        expect((await action.innerText()).trim().length).toBeGreaterThan(2);
      }
      const bounds = await menu.boundingBox();
      expect(bounds!.x).toBeGreaterThanOrEqual(0);
      expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width);
      expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(800);
      expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
      await page.screenshot({ path: testInfo.outputPath(`menu-${theme}-${width}.png`), fullPage: true });
      await page.keyboard.press('Escape');
      await expect(page.getByTestId('application-menu-trigger')).toBeFocused();
    }
  }
});

test("keeps only Menu, folder context and search in the action toolbar", async ({ page }, testInfo) => {
  for (const width of [1920, 1440, 1120, 1024, 680]) {
    await page.setViewportSize({ width, height: 800 });
    await page.goto('/');
    const search = page.getByRole('searchbox', { name: 'Search active panel' });
    const menu = page.getByTestId('application-menu-trigger');
    await expect(menu).toBeVisible();
    await expect(page.getByTestId('toolbar-subtitles')).toBeHidden();
    await expect(page.getByTestId('system-metrics-trigger')).toBeHidden();
    await search.fill('*.txt');
    const searchBounds = await search.boundingBox();
    const menuBounds = await menu.boundingBox();
    expect(searchBounds!.x + searchBounds!.width).toBeLessThanOrEqual(menuBounds!.x);
    await page.getByTestId('toolbar-clear-search').click();
    await expect(search).toHaveValue('');
    await page.screenshot({ path: testInfo.outputPath(`header-${width}.png`), fullPage: true });
  }
});

test("isolates menu keyboard navigation and restores focus after application dialogs", async ({ page }) => {
  await page.goto('/');
  const menu = page.getByTestId('application-menu-trigger');
  const cursor = page.getByTestId('left-panel').locator('tr.cursor');
  const previousCursor = await cursor.getAttribute('data-path');
  await menu.focus();
  await menu.press('ArrowDown');
  await expect(menu).toHaveAttribute('aria-expanded', 'true');
  const subtitles = page.getByTestId('toolbar-subtitles-menu');
  await expect(subtitles).toHaveAttribute('aria-expanded', 'false');
  await expect(page.getByTestId('toolbar-subtitles')).toBeHidden();
  await expect(page.getByTestId('toolbar-repair-subtitles')).toBeHidden();
  await subtitles.focus();
  await subtitles.press('ArrowDown');
  await expect(page.getByTestId('toolbar-add-files')).toBeFocused();
  await subtitles.press('Enter');
  await expect(subtitles).toHaveAttribute('aria-expanded', 'true');
  await expect(menu).toHaveAttribute('aria-expanded', 'true');
  await expect(page.getByTestId('toolbar-subtitles')).toBeVisible();
  await expect(page.getByTestId('toolbar-repair-subtitles')).toBeVisible();
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('End');
  await expect(page.getByTestId('logout')).toBeFocused();
  await expect(cursor).toHaveAttribute('data-path', previousCursor!);
  await page.keyboard.press('Escape');
  await expect(menu).toBeFocused();

  for (const [id, name] of [['system-metrics-trigger', 'System metrics'],
    ['toolbar-subtitle-jobs', 'Subtitle jobs'], ['change-password', 'Change password']]) {
    if (id === 'toolbar-subtitle-jobs') await openSubtitlesMenu(page);
    else await openApplicationMenu(page);
    await page.getByTestId(id).click();
    const dialog = page.getByRole('dialog', { name, exact: true });
    await expect(dialog).toBeVisible();
    await expect(menu).toHaveAttribute('aria-expanded', 'false');
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    await expect(menu).toBeFocused();
  }
  await openApplicationMenu(page);
  await page.getByRole('searchbox', { name: 'Search active panel' }).click();
  await expect(menu).toHaveAttribute('aria-expanded', 'false');
});
