import { expect, Page, Route, test } from "@playwright/test";
import {
  SystemUpdateFixture,
  systemUpdateFixture,
} from "../support/seed-fixtures";

const endpoint = "**/api/system-update**";

// Page routes cannot observe fetches forwarded by a service worker. The PWA contract has its own
// real service-worker acceptance spec; this suite isolates the updater API boundary instead.
test.use({ serviceWorkers: "block" });

interface UpdateRoutes {
  publish(status: SystemUpdateFixture): void;
  applyWith(status: SystemUpdateFixture): void;
  disconnectApplyThen(status: SystemUpdateFixture): void;
  readonly applyBodies: unknown[];
}

async function routeSystemUpdates(
  page: Page,
  initial: SystemUpdateFixture,
): Promise<UpdateRoutes> {
  let current = initial;
  let applyResult: SystemUpdateFixture | null = null;
  let disconnectApply = false;
  const applyBodies: unknown[] = [];

  await page.route(endpoint, async (route: Route) => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;
    if (pathname === "/api/system-update/apply") {
      applyBodies.push(request.postDataJSON());
      if (disconnectApply) {
        disconnectApply = false;
        await route.abort("connectionreset");
        return;
      }

      current = applyResult ?? current;
      await route.fulfill({ json: current });
      return;
    }

    await route.fulfill({ json: current });
  });

  return {
    publish(status) {
      current = status;
    },
    applyWith(status) {
      applyResult = status;
    },
    disconnectApplyThen(status) {
      disconnectApply = true;
      current = status;
    },
    applyBodies,
  };
}

const available = () =>
  systemUpdateFixture({
    targetVersion: "v1.4.0",
    phase: "available",
    updateAvailable: true,
    canApply: true,
    reasonCode: "update_available",
    detail: "A verified ReachCommander update is available.",
  });

test("system update enables only a discovered update and recovers after restart", async ({
  page,
}) => {
  const routes = await routeSystemUpdates(page, systemUpdateFixture());
  await page.goto("/");
  const trigger = page.getByTestId("system-update-trigger");
  await expect(trigger).toBeDisabled();

  routes.publish(available());
  await page.reload();
  await expect(trigger).toBeEnabled();
  await trigger.click();
  routes.disconnectApplyThen(
    systemUpdateFixture({
      currentVersion: "v1.4.0",
      phase: "completed",
      reasonCode: "update_completed",
      detail: "ReachCommander was updated successfully.",
      operationId: "operation-success",
      updatedAt: "2026-08-25T10:05:00Z",
    }),
  );
  await page.getByRole("button", { name: "Update ReachCommander" }).click();

  await expect(page.getByText("Reconnecting to ReachCommander")).toBeVisible();
  await expect(page.getByText("Activating updated app")).toBeVisible({
    timeout: 15_000,
  });
  await expect
    .poll(() =>
      page.evaluate(() =>
        sessionStorage.getItem("reachcommander.systemUpdateRefreshed"),
      ),
    )
    .toBe("operation-success");
  // Service workers are blocked in this updater-boundary suite so Playwright can route API calls.
  // The PWA specs cover the real worker reload; simulate that reload after activation is requested.
  await page.reload();
  await expect(page.getByRole("main")).toBeVisible();
  await expect(trigger).toBeDisabled();
  await expect(page.getByText("Activating updated app")).toHaveCount(0);
  expect(routes.applyBodies).toEqual([null]);
});

test("system update confirmation is immutable, cancellable, and returns focus", async ({
  page,
}) => {
  const routes = await routeSystemUpdates(page, available());
  await page.goto("/");
  const trigger = page.getByTestId("system-update-trigger");
  await trigger.click();
  const dialog = page.getByRole("dialog", { name: "Update ReachCommander?" });
  await expect(dialog).toContainText("v1.3.0");
  await expect(dialog).toContainText("v1.4.0");

  routes.publish(systemUpdateFixture({ currentVersion: "v1.3.1" }));
  await expect(dialog).toContainText("v1.3.0");
  await expect(dialog.locator("input, select, textarea")).toHaveCount(0);
  await dialog.getByRole("button", { name: "Cancel" }).click();
  await expect(dialog).toBeHidden();
  await expect(trigger).toBeFocused();
  expect(routes.applyBodies).toEqual([]);
});

test("system update explains pinned, unsupported, and active-operation states", async ({
  page,
}) => {
  const routes = await routeSystemUpdates(
    page,
    systemUpdateFixture({
      channel: "v1.3.0",
      supported: true,
      phase: "unavailable",
      reasonCode: "version_pinned",
      detail: "Exact version pins do not update automatically.",
    }),
  );
  await page.goto("/");
  const control = page.locator(".update-control");
  await expect(control).toHaveAccessibleName(
    "Updates disabled while version-pinned",
  );

  routes.publish(
    systemUpdateFixture({
      supported: false,
      channel: null,
      phase: "unavailable",
      reasonCode: "unsupported_installation",
      detail: "System updates require an Ubuntu installer-managed deployment.",
    }),
  );
  await page.reload();
  await expect(control).toHaveAccessibleName(
    /Ubuntu installer-managed deployment/,
  );

  routes.publish(
    systemUpdateFixture({
      targetVersion: "v1.4.0",
      phase: "blocked",
      updateAvailable: true,
      reasonCode: "active_operations",
      detail: "Wait for active file operations to finish.",
    }),
  );
  await page.reload();
  await expect(control).toHaveAccessibleName(
    "Update waiting for operations to finish",
  );
  await expect(page.getByTestId("system-update-trigger")).toBeDisabled();
});

for (const terminal of [
  {
    phase: "rolledBack" as const,
    title: "Previous version restored",
    reasonCode: "health_check_failed",
    detail: "The candidate failed its health check.",
    safeCopy: "previous version was restored",
  },
  {
    phase: "failed" as const,
    title: "Update requires attention",
    reasonCode: "update_failed",
    detail: "The update requires host administrator attention.",
    safeCopy: "reachcommander doctor",
  },
]) {
  test(`system update reports ${terminal.phase} without hiding the commander`, async ({
    page,
  }) => {
    const routes = await routeSystemUpdates(page, available());
    routes.applyWith(
      systemUpdateFixture({
        targetVersion: "v1.4.0",
        phase: terminal.phase,
        reasonCode: terminal.reasonCode,
        detail: terminal.detail,
        operationId: `operation-${terminal.phase}`,
      }),
    );
    await page.goto("/");
    await page.getByTestId("system-update-trigger").click();
    await page.getByRole("button", { name: "Update ReachCommander" }).click();
    const overlay = page.getByRole("alertdialog", { name: terminal.title });
    await expect(overlay).toBeVisible();
    await expect(overlay).toContainText(terminal.safeCopy);
    await overlay
      .getByRole("button", { name: "Return to ReachCommander" })
      .click();
    await expect(overlay).toBeHidden();
    await expect(page.getByRole("main")).toBeVisible();
  });
}

for (const norton of [false, true]) {
  test(`system update stays usable in ${norton ? "Norton" : "default"} theme at compact width`, async ({
    page,
  }) => {
    await routeSystemUpdates(page, available());
    await page.setViewportSize({ width: 680, height: 800 });
    await page.goto("/");
    if (norton) {
      await page.getByTestId("norton-theme-toggle").click();
      await expect(page.locator("html")).toHaveAttribute(
        "data-theme",
        "norton",
      );
    }

    const trigger = page.getByTestId("system-update-trigger");
    await expect(trigger).toBeVisible();
    await trigger.click();
    await expect(
      page.getByRole("dialog", { name: "Update ReachCommander?" }),
    ).toBeVisible();
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth - innerWidth,
      ),
    ).toBeLessThanOrEqual(1);
  });
}
