import { expect, test } from "@playwright/test";
import {
  routeInstallerManagedSourceManagement,
  routeUnsupportedSourceManagement,
  sourceOperation,
} from "../support/source-management-fixture";

test.use({ serviceWorkers: "block" });

test("unsupported deployments explain why Add source is disabled", async ({ page }) => {
  await routeUnsupportedSourceManagement(page);
  await page.goto("/");

  const wrapper = page.getByTestId("toolbar-add-source").locator("..");
  await expect(page.getByTestId("toolbar-add-source")).toBeDisabled();
  await expect(wrapper).toHaveAttribute(
    "title",
    "Source management requires an Ubuntu installer-managed deployment.",
  );
});

test("adds a read-only host source, reconnects, and refreshes both source selectors", async ({ page }) => {
  const host = await routeInstallerManagedSourceManagement(page);
  await page.goto("/");
  await page.getByTestId("toolbar-add-source").click();

  const dialog = page.getByRole("dialog", { name: "Add host source" });
  await dialog.getByLabel("Display name").fill("Family media");
  await dialog.getByLabel("Absolute Ubuntu host folder").fill("/srv/media/family");
  await expect(dialog.getByRole("radio", { name: /Read only/ })).toBeChecked();
  await dialog.getByTestId("add-source-submit").click();

  expect(host.addRequests).toEqual([{
    displayName: "Family media",
    hostPath: "/srv/media/family",
    access: "readOnly",
  }]);
  await expect(page.getByTestId("toolbar-add-source")).toBeDisabled();

  host.disconnectNextOperationRead();
  await expect(page.getByText(/Reconnecting to ReachCommander/)).toBeVisible({ timeout: 5_000 });
  host.publish(sourceOperation({
    phase: "restarting",
    reasonCode: "source_restarting",
    detail: "ReachCommander is restarting with the staged source configuration.",
  }));
  await expect(page.getByRole("heading", { name: "Restarting ReachCommander" })).toBeVisible({
    timeout: 5_000,
  });

  host.publish(sourceOperation({
    sourceId: "family-media",
    phase: "completed",
    reasonCode: "source_added",
    detail: "The host source was added successfully.",
  }));
  await expect(page.getByText("Family media is now available in both panes.")).toBeVisible({
    timeout: 5_000,
  });
  const generatedSources = page.getByTestId("source-family-media");
  await expect(generatedSources).toHaveCount(2);
  await expect(generatedSources.first().locator('[data-access="read-only"]')).toBeVisible();
});

test("requires explicit read-write confirmation and sends only the narrow request", async ({ page }) => {
  const host = await routeInstallerManagedSourceManagement(page);
  await page.goto("/");
  await page.getByTestId("toolbar-add-source").click();

  const dialog = page.getByRole("dialog", { name: "Add host source" });
  await dialog.getByLabel("Display name").fill("Editing workspace");
  await dialog.getByLabel("Absolute Ubuntu host folder").fill("/srv/projects/editing");
  await dialog.getByRole("radio", { name: /Read\/write/ }).check();
  await expect(dialog.getByTestId("read-write-warning")).toContainText(
    "change or delete files",
  );
  await expect(dialog.getByTestId("add-source-submit")).toBeDisabled();
  await dialog.getByRole("checkbox").check();
  await dialog.getByTestId("add-source-submit").click();

  expect(host.addRequests).toEqual([{
    displayName: "Editing workspace",
    hostPath: "/srv/projects/editing",
    access: "readWrite",
  }]);
  expect(Object.keys(host.addRequests[0]!).sort()).toEqual([
    "access",
    "displayName",
    "hostPath",
  ]);

  host.publish(sourceOperation({
    displayName: "Editing workspace",
    sourceId: "editing-workspace",
    phase: "completed",
    reasonCode: "source_added",
    detail: "The host source was added successfully.",
  }));
  await expect(page.getByTestId("source-editing-workspace")).toHaveCount(2, { timeout: 5_000 });
  await expect(
    page.getByTestId("source-editing-workspace").first().locator('[data-access="writable"]'),
  ).toBeVisible();
});

test("blocks duplicate source submissions while the operation is active", async ({ page }) => {
  const host = await routeInstallerManagedSourceManagement(page);
  await page.goto("/");
  await page.getByTestId("toolbar-add-source").click();
  const dialog = page.getByRole("dialog", { name: "Add host source" });
  await dialog.getByLabel("Display name").fill("Backups");
  await dialog.getByLabel("Absolute Ubuntu host folder").fill("/mnt/storage/backups");

  const submit = dialog.getByTestId("add-source-submit");
  await submit.dblclick();
  await expect(page.getByRole("dialog", { name: "Configuring source" })).toBeVisible();
  expect(host.addRequests).toHaveLength(1);
  await expect(page.getByTestId("toolbar-add-source")).toBeDisabled();
});

test("reports rollback without activating the requested mapping", async ({ page }) => {
  const host = await routeInstallerManagedSourceManagement(page);
  await page.goto("/");
  await page.getByTestId("toolbar-add-source").click();
  const dialog = page.getByRole("dialog", { name: "Add host source" });
  await dialog.getByLabel("Display name").fill("Unsafe candidate");
  await dialog.getByLabel("Absolute Ubuntu host folder").fill("/srv/media/candidate");
  await dialog.getByTestId("add-source-submit").click();

  host.publish(sourceOperation({
    displayName: "Unsafe candidate",
    phase: "rolledBack",
    reasonCode: "health_check_failed",
    detail: "The previous source configuration was restored.",
  }));
  await expect(page.getByRole("heading", { name: "Previous configuration restored" })).toBeVisible({
    timeout: 5_000,
  });
  await expect(page.getByTestId("source-management-dialog")).toContainText(
    "no new mapping is active",
  );
  await expect(page.getByTestId("source-unsafe-candidate")).toHaveCount(0);
});

test("shows bounded failed-operation diagnostics without leaking host paths", async ({ page }) => {
  const host = await routeInstallerManagedSourceManagement(page);
  await page.goto("/");
  await page.getByTestId("toolbar-add-source").click();
  const dialog = page.getByRole("dialog", { name: "Add host source" });
  await dialog.getByLabel("Display name").fill("Cold storage");
  await dialog.getByLabel("Absolute Ubuntu host folder").fill("/mnt/archive/cold");
  await dialog.getByTestId("add-source-submit").click();

  host.publish(sourceOperation({
    displayName: "Cold storage",
    phase: "failed",
    reasonCode: "source_management_failed",
    detail: "The source-management operation could not be completed.",
  }));
  await expect(page.getByRole("heading", { name: "Source could not be added" })).toBeVisible({
    timeout: 5_000,
  });
  const operationDialog = page.getByTestId("source-management-dialog");
  await expect(operationDialog).toContainText("sudo reachcommander doctor");
  await expect(operationDialog).toContainText("support diagnostics");
  await expect(operationDialog).not.toContainText(/\/opt\/|\/srv\/|docker|compose|sha256:/i);
});
