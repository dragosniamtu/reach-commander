import { expect, test } from "@playwright/test";

test("uploads a complete batch and rejects a conflicting batch atomically", async ({
  page,
}) => {
  await page.goto("/");

  const left = page.getByTestId("left-panel");
  await left.getByTestId("source-downloads").click();
  await left.click();

  const fileInput = page.locator('input[type="file"]');
  await fileInput.setInputFiles([
    { name: "new-one.txt", mimeType: "text/plain", buffer: Buffer.from("one") },
    {
      name: "new-two.bin",
      mimeType: "application/octet-stream",
      buffer: Buffer.from([0, 1, 2]),
    },
  ]);

  let dialog = page.getByRole("dialog", { name: "Add files" });
  await expect(dialog).toContainText("Downloads");
  await expect(dialog.locator(".destination code")).toHaveText("/");
  await expect(dialog.getByText("2 files", { exact: true })).toBeVisible();
  await dialog.getByTestId("upload-primary").click();
  await expect(dialog.getByText("Upload complete")).toBeVisible();
  await dialog.getByRole("button", { name: "Close", exact: true }).click();
  await expect(left.getByText("new-one.txt", { exact: true })).toBeVisible();
  await expect(left.getByText("new-two.bin", { exact: true })).toBeVisible();

  await fileInput.setInputFiles([
    {
      name: "existing.txt",
      mimeType: "text/plain",
      buffer: Buffer.from("replacement"),
    },
    {
      name: "another.txt",
      mimeType: "text/plain",
      buffer: Buffer.from("another"),
    },
  ]);
  dialog = page.getByRole("dialog", { name: "Add files" });
  await dialog.getByTestId("upload-primary").click();
  await expect(dialog.getByRole("alert")).toContainText("already exist");
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(left.getByText("existing.txt", { exact: true })).toBeVisible();
  await expect(left.getByText("another.txt", { exact: true })).toHaveCount(0);
});

test("read-only sources disable Add files with an explanation", async ({
  page,
}) => {
  await page.goto("/");

  const right = page.getByTestId("right-panel");
  await right.getByTestId("source-archive").click();
  await right.click();

  const addFiles = page.getByTestId("toolbar-add-files");
  await expect(addFiles).toBeDisabled();
  await expect(addFiles.locator("..")).toHaveAttribute(
    "title",
    "The active source is read-only.",
  );
});
