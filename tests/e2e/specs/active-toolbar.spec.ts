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
  await expect(addFiles).toBeFocused();

  await left.click();
  await left.getByText("Rename Lab", { exact: true }).click();
  const rename = page.getByTestId("toolbar-multi-rename");
  await rename.click();
  let renameDialog = page.getByTestId("multi-rename-dialog");
  await expect(renameDialog).toContainText("Downloads · /");
  await renameDialog
    .getByRole("button", { name: "Close Multi-Rename" })
    .click();
  await expect(left).toBeFocused();

  await page.keyboard.press("Control+M");
  renameDialog = page.getByTestId("multi-rename-dialog");
  await expect(renameDialog).toBeVisible();
  await expect(renameDialog).toContainText("1 unchanged");
  await page.keyboard.press("Escape");
  await expect(renameDialog).toBeHidden();
});

test("keeps toolbar and metrics usable at supported compact width", async ({
  page,
}, testInfo) => {
  await page.setViewportSize({ width: 680, height: 800 });
  await page.goto("/");

  const toolbar = page.getByRole("toolbar", { name: "Active panel tools" });
  const metrics = page.getByTestId("system-metrics-trigger");
  await expect(toolbar).toBeVisible();
  await expect(metrics).toBeVisible();
  await metrics.click();
  await expect(
    page.getByRole("dialog", { name: "System metrics" }),
  ).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(metrics).toBeFocused();

  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - window.innerWidth,
  );
  expect(overflow).toBeLessThanOrEqual(1);
  await page.screenshot({
    path: testInfo.outputPath("toolbar-680.png"),
    fullPage: true,
  });
});

test("collapses brand copy before topbar controls compete for space", async ({ page }) => {
  const brandCopy = page.locator(".brand-block > div");

  await page.setViewportSize({ width: 1121, height: 800 });
  await page.goto("/");
  await expect(brandCopy).toBeVisible();

  await page.setViewportSize({ width: 1120, height: 800 });
  await expect(brandCopy).toBeHidden();
});

test("keeps the toolbar hierarchy clear at desktop widths", async ({
  page,
}, testInfo) => {
  for (const viewport of [
    { width: 1440, height: 900 },
    { width: 1200, height: 800 },
    { width: 1024, height: 768 },
  ]) {
    await page.setViewportSize(viewport);
    await page.goto("/");

    const toolbar = page.getByRole("toolbar", { name: "Active panel tools" });
    const search = page.getByRole("searchbox", { name: "Search active panel" });
    await search.fill("*.txt");
    const clearSearch = page.getByTestId("toolbar-clear-search");
    const topActions = page.locator(".top-actions");
    const metrics = page.getByTestId("system-metrics-trigger");
    if (viewport.width === 1024) {
      await topActions.evaluate((element) => {
        (element as HTMLElement).style.minWidth = "510px";
      });
    }
    const toolbarBounds = await toolbar.boundingBox();
    const clearSearchBounds = await clearSearch.boundingBox();
    const topActionsBounds = await topActions.boundingBox();
    const metricsBounds = await metrics.boundingBox();
    expect(toolbarBounds).not.toBeNull();
    expect(clearSearchBounds).not.toBeNull();
    expect(topActionsBounds).not.toBeNull();
    expect(metricsBounds).not.toBeNull();
    expect(toolbarBounds!.x + toolbarBounds!.width).toBeLessThanOrEqual(
      metricsBounds!.x,
    );
    expect(clearSearchBounds!.x + clearSearchBounds!.width).toBeLessThanOrEqual(
      topActionsBounds!.x,
    );
    await clearSearch.click();
    await expect(search).toHaveValue("");
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth - window.innerWidth,
      ),
    ).toBeLessThanOrEqual(1);
    await page.screenshot({
      path: testInfo.outputPath(`toolbar-${viewport.width}.png`),
      fullPage: true,
    });
  }

  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto("/");
  const left = page.getByTestId("left-panel");
  await left.getByText("Rename Lab", { exact: true }).click();
  await page.getByTestId("toolbar-multi-rename").click();
  const dialog = page.getByTestId("multi-rename-dialog");
  await expect(dialog).toContainText("1 unchanged");
  await page.screenshot({
    path: testInfo.outputPath("multi-rename-1440.png"),
    fullPage: true,
  });
});
