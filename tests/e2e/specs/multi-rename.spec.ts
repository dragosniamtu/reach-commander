import { expect, test } from "@playwright/test";

test("previews complete names, renames mixed entries, and safely undoes", async ({
  page,
}) => {
  await page.goto("/");

  const left = page.getByTestId("left-panel");
  await left.getByText("Rename Lab", { exact: true }).dblclick();
  await expect(left.locator(".path-status")).toHaveText("Downloads:/Rename Lab");
  await expect(left.getByText("holiday-video.mp4", { exact: true })).toBeVisible();
  await left.click();
  await page.keyboard.press("Control+A");
  await page.keyboard.press("Control+M");

  const dialog = page.getByTestId("multi-rename-dialog");
  await expect(dialog).toBeVisible();
  await dialog.getByTestId("name-mask").fill("Archive-[C]");
  await dialog.getByLabel("Counter digits").fill("3");

  await expect(dialog.getByTestId("new-name")).toHaveText([
    "Archive-001",
    "Archive-002.jpg",
    "Archive-003.mp4",
  ]);
  await expect(dialog.getByTestId("rename-start")).toBeEnabled();
  await dialog.getByTestId("rename-start").click();
  await expect(dialog).toContainText("3 entries renamed");
  await expect(dialog.getByTestId("rename-undo")).toBeEnabled();

  await dialog.getByTestId("rename-undo").click();
  await expect(dialog).toContainText("Undo completed");
  await dialog.getByRole("button", { name: "Close", exact: true }).click();
  await expect(left.getByText("Drafts", { exact: true })).toBeVisible();
  await expect(
    left.getByText("holiday-photo.jpg", { exact: true }),
  ).toBeVisible();
  await expect(
    left.getByText("holiday-video.mp4", { exact: true }),
  ).toBeVisible();
});

test("one conflict blocks the complete rename batch", async ({ page }) => {
  await page.goto("/");

  const left = page.getByTestId("left-panel");
  await left.getByText("Conflict Lab", { exact: true }).dblclick();
  await expect(left.getByText("two.txt", { exact: true })).toBeVisible();
  await left.click();
  await page.keyboard.press("Control+A");
  await page.keyboard.press("Control+M");

  const dialog = page.getByTestId("multi-rename-dialog");
  await dialog.getByTestId("name-mask").fill("same");
  await dialog.getByTestId("extension-mask").fill("txt");

  await expect(dialog.locator('[data-status="conflict"]')).toHaveCount(2);
  await expect(dialog.getByTestId("rename-start")).toBeDisabled();
  await dialog.getByRole("button", { name: "Close", exact: true }).click();
  await expect(left.getByText("one.txt", { exact: true })).toBeVisible();
  await expect(left.getByText("two.txt", { exact: true })).toBeVisible();
});

test("read-only sources explain why Multi-Rename is unavailable", async ({
  page,
}) => {
  await page.goto("/");

  const left = page.getByTestId("left-panel");
  await left.getByTestId("source-archive").click();
  await left.getByText("locked.txt", { exact: true }).click();

  const renameWrapper = page.getByTestId("toolbar-multi-rename").locator("..");
  await expect(page.getByTestId("toolbar-multi-rename")).toBeDisabled();
  await expect(renameWrapper).toHaveAttribute(
    "title",
    "The active source is read-only.",
  );
});
