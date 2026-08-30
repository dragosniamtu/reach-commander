import { expect, test } from "@playwright/test";

const storageKey = "reachcommander.theme.v1";

test.beforeEach(async ({ page }) => {
  await page.goto("/");
  await page.evaluate((key) => localStorage.removeItem(key), storageKey);
  await page.reload();
});

test("selects, persists, and resets the Norton theme", async ({
  page,
}, testInfo) => {
  const root = page.locator("html");
  const selector = page.getByTestId("theme-selector");
  const consoleErrors: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error") {
      consoleErrors.push(message.text());
    }
  });

  await expect(selector).toHaveValue("default");
  await selector.selectOption("norton");

  await expect(root).toHaveAttribute("data-theme", "norton");
  await expect(selector).toHaveValue("norton");
  expect(
    await root.evaluate((element) => {
      const styles = getComputedStyle(element);
      return {
        appBackground: styles.getPropertyValue("--app-bg").trim(),
        panelBackground: styles.getPropertyValue("--surface-1").trim(),
        frame: styles.getPropertyValue("--line-strong").trim(),
        selection: styles.getPropertyValue("--selection").trim(),
      };
    }),
  ).toEqual({
    appBackground: "#000080",
    panelBackground: "#0000aa",
    frame: "#00ffff",
    selection: "#55ffff",
  });
  expect(
    await page
      .getByTestId("left-panel")
      .locator("tbody tr.cursor")
      .evaluate((element) => {
        const styles = getComputedStyle(element);
        return {
          background: styles.backgroundColor,
          color: styles.color,
        };
      }),
  ).toEqual({
    background: "rgb(85, 255, 255)",
    color: "rgb(0, 0, 128)",
  });
  await page.screenshot({
    path: testInfo.outputPath("norton-theme-1440.png"),
    fullPage: true,
  });

  await page.reload();
  await expect(root).toHaveAttribute("data-theme", "norton");
  await expect(selector).toHaveValue("norton");
  expect(
    await page.evaluate((key) => localStorage.getItem(key), storageKey),
  ).toBe("norton");

  await selector.selectOption("default");
  await expect(root).not.toHaveAttribute("data-theme", "norton");
  await expect(selector).toHaveValue("default");
  expect(
    await page.evaluate((key) => localStorage.getItem(key), storageKey),
  ).toBeNull();

  await page.reload();
  await expect(root).not.toHaveAttribute("data-theme", "norton");
  expect(consoleErrors).toEqual([]);
});

test("keeps the Norton selector and dual-pane shell usable at compact width", async ({
  page,
}, testInfo) => {
  await page.setViewportSize({ width: 680, height: 800 });
  await page.reload();

  const selector = page.getByTestId("theme-selector");
  await expect(selector).toBeVisible();
  await selector.selectOption("norton");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "norton");
  await expect(page.getByTestId("left-panel")).toBeVisible();
  await expect(page.getByTestId("right-panel")).toBeVisible();

  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth - window.innerWidth,
    ),
  ).toBeLessThanOrEqual(1);
  await page.screenshot({
    path: testInfo.outputPath("norton-theme-680.png"),
    fullPage: true,
  });
});
