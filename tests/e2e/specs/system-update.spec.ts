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
  failDiagnostics(): void;
  readonly applyBodies: unknown[];
  readonly diagnosticsRequests: unknown[];
}

async function routeSystemUpdates(
  page: Page,
  initial: SystemUpdateFixture,
): Promise<UpdateRoutes> {
  let current = initial;
  let applyResult: SystemUpdateFixture | null = null;
  let disconnectApply = false;
  const applyBodies: unknown[] = [];
  const diagnosticsRequests: unknown[] = [];
  let diagnosticsFail = false;

  await page.route(endpoint, async (route: Route) => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;
    if (pathname === "/api/system-update/support-bundle") {
      diagnosticsRequests.push(request.postDataJSON());
      if (diagnosticsFail) {
        await route.fulfill({
          status: 503,
          contentType: "application/problem+json",
          json: { code: "support_bundle_unavailable" },
        });
      } else {
        await route.fulfill({
          status: 200,
          headers: {
            "Content-Type": "application/zip",
            "Content-Disposition":
              'attachment; filename="reachcommander-support-20260827T120000Z.zip"',
            "Cache-Control": "no-store",
          },
          body: Buffer.from("PK sanitized support bundle"),
        });
      }
      return;
    }
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
    failDiagnostics() {
      diagnosticsFail = true;
    },
    applyBodies,
    diagnosticsRequests,
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

function tracedStatus(
  overrides: Partial<SystemUpdateFixture> = {},
): SystemUpdateFixture {
  const startedAt = new Date(Date.now() - 5_000).toISOString();
  return systemUpdateFixture({
    protocolVersion: 3,
    targetVersion: "v1.4.0",
    phase: "applying",
    progressStage: "downloading",
    updateAvailable: true,
    reasonCode: "update_applying",
    operationId: "operation-traced",
    updatedAt: new Date().toISOString(),
    trace: {
      startedAt,
      elapsedSeconds: 5,
      lastActivityAt: startedAt,
      events: [
        {
          sequence: 1,
          timestamp: startedAt,
          elapsedSeconds: 0,
          code: "operationAccepted",
          stage: null,
          outcome: "started",
        },
      ],
    },
    ...overrides,
  });
}

test("system update checks on demand, enables a discovered update, and recovers after restart", async ({
  page,
}) => {
  const routes = await routeSystemUpdates(page, systemUpdateFixture());
  await page.goto("/");
  const trigger = page.getByTestId("system-update-trigger");
  await expect(trigger).toBeEnabled();
  await expect(trigger).toHaveAccessibleName(
    "Check for updates. ReachCommander is up to date",
  );

  routes.publish(available());
  await trigger.click();
  await expect(trigger).toHaveAccessibleName("Update available: v1.4.0");
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

test("system update advances through host-confirmed stages and activates the app", async ({
  page,
}) => {
  const routes = await routeSystemUpdates(page, available());
  routes.applyWith(
    systemUpdateFixture({
      targetVersion: "v1.4.0",
      phase: "applying",
      progressStage: "downloading",
      updateAvailable: true,
      reasonCode: "update_applying",
      operationId: "operation-detailed",
      updatedAt: new Date().toISOString(),
    }),
  );
  await page.goto("/");
  await page.getByTestId("system-update-trigger").click();
  await page.getByRole("button", { name: "Update ReachCommander" }).click();

  const overlay = page.getByRole("alertdialog");
  await expect(overlay.locator('[data-step-state="active"]')).toContainText(
    "Downloading verified image",
  );

  for (const [progressStage, label, completedLabel] of [
    ["installing", "Installing update", "Downloading verified image"],
    ["restarting", "Restarting ReachCommander", "Installing update"],
    ["healthChecking", "Checking system health", "Restarting ReachCommander"],
  ] as const) {
    routes.publish(
      systemUpdateFixture({
        targetVersion: "v1.4.0",
        phase: "applying",
        progressStage,
        updateAvailable: true,
        reasonCode: "update_applying",
        operationId: "operation-detailed",
        updatedAt: new Date().toISOString(),
      }),
    );
    await expect(overlay.locator('[data-step-state="active"]')).toContainText(
      label,
      {
        timeout: 15_000,
      },
    );
    await expect(
      overlay.locator('[data-step-state="complete"]', {
        hasText: completedLabel,
      }),
    ).toBeVisible();
  }

  routes.publish(
    systemUpdateFixture({
      currentVersion: "v1.4.0",
      targetVersion: "v1.4.0",
      phase: "completed",
      progressStage: "healthChecking",
      reasonCode: "update_completed",
      operationId: "operation-detailed",
      updatedAt: new Date().toISOString(),
    }),
  );
  await expect(overlay.locator('[data-step-state="active"]')).toContainText(
    "Activating updated application",
    { timeout: 15_000 },
  );
  await expect
    .poll(() =>
      page.evaluate(() =>
        sessionStorage.getItem("reachcommander.systemUpdateRefreshed"),
      ),
    )
    .toBe("operation-detailed");
  expect(routes.applyBodies).toEqual([null]);
});

test("system update grows a sanitized technical timeline while polling", async ({
  page,
}) => {
  const initial = tracedStatus();
  const routes = await routeSystemUpdates(page, available());
  routes.applyWith(initial);
  await page.goto("/");
  await page.getByTestId("system-update-trigger").click();
  await page.getByRole("button", { name: "Update ReachCommander" }).click();

  const details = page.locator("details.technical-details");
  await details.locator("summary").click();
  await expect(details).toContainText("Update accepted");
  await expect(details).toContainText("Elapsed");

  const activityAt = new Date().toISOString();
  routes.publish(
    tracedStatus({
      trace: {
        ...initial.trace!,
        elapsedSeconds: 8,
        lastActivityAt: activityAt,
        events: [
          ...initial.trace!.events,
          {
            sequence: 2,
            timestamp: activityAt,
            elapsedSeconds: 8,
            code: "hostActivity",
            stage: "downloading",
            outcome: "activity",
          },
        ],
      },
    }),
  );

  await expect(details).toContainText("Host download activity confirmed", {
    timeout: 15_000,
  });
  await expect(
    details
      .getByRole("list", { name: "Sanitized update events" })
      .locator("li"),
  ).toHaveCount(2);
  await expect(details).not.toContainText(
    /docker|sha256:|\/opt\/|exitCode|timeoutSeconds/i,
  );
});

test("system update downloads sanitized diagnostics without closing the overlay", async ({
  page,
}) => {
  const routes = await routeSystemUpdates(page, available());
  routes.applyWith(tracedStatus());
  await page.goto("/");
  await page.getByTestId("system-update-trigger").click();
  await page.getByRole("button", { name: "Update ReachCommander" }).click();

  const overlay = page.getByRole("alertdialog");
  await overlay.locator("details.technical-details summary").click();
  const downloadPromise = page.waitForEvent("download");
  await overlay.getByRole("button", { name: "Download diagnostics" }).click();
  const download = await downloadPromise;

  expect(download.suggestedFilename()).toBe(
    "reachcommander-support-20260827T120000Z.zip",
  );
  expect(routes.diagnosticsRequests).toEqual([null]);
  await expect(overlay).toBeVisible();
  await expect(
    overlay.getByRole("button", { name: "Download diagnostics" }),
  ).toBeEnabled();

  routes.failDiagnostics();
  await overlay.getByRole("button", { name: "Download diagnostics" }).click();
  await expect(overlay).toContainText("sudo reachcommander support-bundle");
  await expect(overlay).not.toContainText(/token=|sha256:|\/srv\/|\/opt\//i);
});

test("system update auto-opens stale and timed-out technical details", async ({
  page,
}) => {
  const staleAt = "2000-01-01T00:00:00Z";
  const routes = await routeSystemUpdates(page, available());
  routes.applyWith(
    tracedStatus({
      progressStage: "downloading",
      trace: {
        startedAt: staleAt,
        elapsedSeconds: 120,
        lastActivityAt: null,
        events: [
          {
            sequence: 4,
            timestamp: staleAt,
            elapsedSeconds: 0,
            code: "operationAccepted",
            stage: null,
            outcome: "started",
          },
        ],
      },
    }),
  );

  await page.goto("/");
  await page.getByTestId("system-update-trigger").click();
  await page.getByRole("button", { name: "Update ReachCommander" }).click();

  const details = page.locator("details.technical-details");
  await expect(details).toHaveAttribute("open", "");
  await expect(
    details.getByRole("button", { name: "Download diagnostics" }),
  ).toBeVisible();
  await expect(
    page.getByText(/No host activity has been confirmed/),
  ).toBeVisible();

  routes.publish(
    tracedStatus({
      phase: "failed",
      progressStage: "downloading",
      reasonCode: "update_failed",
      detail: "The update requires administrator attention.",
      trace: {
        startedAt: staleAt,
        elapsedSeconds: 120,
        lastActivityAt: null,
        events: [
          {
            sequence: 5,
            timestamp: staleAt,
            elapsedSeconds: 120,
            code: "commandTimedOut",
            stage: "downloading",
            outcome: "timedOut",
          },
        ],
      },
    }),
  );
  await expect(details).toContainText("Update command timed out");
  await expect(details).toContainText("Timed out");
});

test("system update shows automatic recovery and the restored result", async ({
  page,
}) => {
  const routes = await routeSystemUpdates(page, available());
  routes.applyWith(
    systemUpdateFixture({
      targetVersion: "v1.4.0",
      phase: "applying",
      progressStage: "restoring",
      updateAvailable: true,
      reasonCode: "update_applying",
      operationId: "operation-recovery",
      updatedAt: new Date().toISOString(),
    }),
  );
  await page.goto("/");
  await page.getByTestId("system-update-trigger").click();
  await page.getByRole("button", { name: "Update ReachCommander" }).click();

  const overlay = page.getByRole("alertdialog");
  await expect(overlay).toContainText("Recovering previous version");
  await expect(
    overlay.getByRole("list", { name: "Recovery progress" }),
  ).toBeVisible();
  await expect(overlay.locator('[data-step-state="active"]')).toContainText(
    "Restoring previous version",
  );

  for (const [progressStage, label] of [
    ["restartingPrevious", "Restarting previous version"],
    ["verifyingRecovery", "Verifying recovery"],
  ] as const) {
    routes.publish(
      systemUpdateFixture({
        targetVersion: "v1.4.0",
        phase: "applying",
        progressStage,
        updateAvailable: true,
        reasonCode: "update_applying",
        operationId: "operation-recovery",
        updatedAt: new Date().toISOString(),
      }),
    );
    await expect(overlay.locator('[data-step-state="active"]')).toContainText(
      label,
      {
        timeout: 15_000,
      },
    );
  }

  routes.publish(
    systemUpdateFixture({
      targetVersion: "v1.4.0",
      phase: "rolledBack",
      progressStage: "verifyingRecovery",
      reasonCode: "candidate_rolled_back",
      operationId: "operation-recovery",
      updatedAt: new Date().toISOString(),
    }),
  );
  await expect(
    page.getByRole("alertdialog", { name: "Previous version restored" }),
  ).toBeVisible({ timeout: 15_000 });
  await expect(
    overlay.getByRole("button", { name: "Download diagnostics" }),
  ).toBeVisible();
  await expect(
    overlay
      .getByRole("list", { name: "Recovery progress" })
      .locator('[data-step-state="complete"]'),
  ).toHaveCount(3);
});

test("system update uses generic progress and refresh guidance with a protocol-v2 helper", async ({
  page,
}) => {
  const routes = await routeSystemUpdates(page, available());
  routes.applyWith(
    systemUpdateFixture({
      protocolVersion: 2,
      phase: "applying",
      operationId: "operation-v2",
      progressStage: null,
      reasonCode: "update_applying",
      updatedAt: new Date().toISOString(),
    }),
  );

  await page.goto("/");
  await page.getByTestId("system-update-trigger").click();
  await page.getByRole("button", { name: "Update ReachCommander" }).click();

  await expect(
    page
      .getByRole("list", { name: "Update progress" })
      .getByText("Applying trusted update"),
  ).toBeVisible();
  await expect(page.getByText("Downloading verified image")).toHaveCount(0);
  await page.locator("details.technical-details summary").click();
  await expect(
    page.getByText(/refresh the Ubuntu installer bundle/),
  ).toBeVisible();
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
    await expect(
      overlay.getByRole("button", { name: "Download diagnostics" }),
    ).toBeVisible();
    await overlay
      .getByRole("button", { name: "Return to ReachCommander" })
      .click();
    await expect(overlay).toBeHidden();
    await expect(page.getByRole("main")).toBeVisible();
  });
}

for (const norton of [false, true]) {
  test(`system update overlay stays usable in ${norton ? "Norton" : "default"} theme at compact width`, async ({
    page,
  }) => {
    const routes = await routeSystemUpdates(page, available());
    routes.applyWith(
      systemUpdateFixture({
        targetVersion: "v1.4.0",
        phase: "applying",
        progressStage: "healthChecking",
        updateAvailable: true,
        reasonCode: "update_applying",
        operationId: "operation-compact",
        updatedAt: new Date().toISOString(),
      }),
    );
    await page.setViewportSize({ width: 360, height: 560 });
    await page.goto("/");
    if (norton) {
      await page.getByTestId("theme-selector").selectOption("norton");
      await expect(page.locator("html")).toHaveAttribute(
        "data-theme",
        "norton",
      );
    }

    const trigger = page.getByTestId("system-update-trigger");
    await expect(trigger).toBeVisible();
    await trigger.click();
    await page.getByRole("button", { name: "Update ReachCommander" }).click();
    const overlay = page.getByRole("alertdialog");
    await expect(overlay).toBeVisible();
    await expect(
      overlay.getByRole("list", { name: "Update progress" }),
    ).toBeVisible();
    await overlay.locator("details.technical-details summary").click();
    await expect(
      overlay.getByRole("button", { name: "Download diagnostics" }),
    ).toBeVisible();
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth - innerWidth,
      ),
    ).toBeLessThanOrEqual(1);
    const scrolling = await overlay.evaluate((element) => {
      const style = getComputedStyle(element);
      return {
        overflowY: style.overflowY,
        clientHeight: element.clientHeight,
        scrollHeight: element.scrollHeight,
      };
    });
    expect(scrolling.overflowY).toBe("auto");
    expect(scrolling.scrollHeight).toBeGreaterThanOrEqual(
      scrolling.clientHeight,
    );
  });
}

for (const reducedMotion of ["no-preference", "reduce"] as const) {
  test.describe(`system update motion: ${reducedMotion}`, () => {
    test(`renders ${reducedMotion === "reduce" ? "static" : "counter-rotating"} progress rings`, async ({
      page,
    }) => {
      await page.emulateMedia({ reducedMotion });
      expect(
        await page.evaluate(
          () => matchMedia("(prefers-reduced-motion: reduce)").matches,
        ),
      ).toBe(reducedMotion === "reduce");
      const routes = await routeSystemUpdates(page, available());
      routes.applyWith(
        systemUpdateFixture({
          targetVersion: "v1.4.0",
          phase: "applying",
          progressStage: "installing",
          reasonCode: "update_applying",
          operationId: "operation-motion",
        }),
      );
      await page.goto("/");
      await page.getByTestId("system-update-trigger").click();
      await page.getByRole("button", { name: "Update ReachCommander" }).click();

      const overlay = page.getByRole("alertdialog", {
        name: "Updating ReachCommander",
      });
      await expect(overlay).toBeVisible();
      const styles = await overlay
        .locator(".spinner > i")
        .evaluateAll((rings) =>
          rings.map((ring) => {
            const style = getComputedStyle(ring);
            return {
              animationName: style.animationName,
              animationDuration: style.animationDuration,
            };
          }),
        );
      const stepAnimation = await overlay
        .locator('[data-step-state="active"] .step-indicator')
        .evaluate((indicator) => getComputedStyle(indicator).animationName);

      expect(styles).toHaveLength(2);
      if (reducedMotion === "reduce") {
        expect(styles.map((style) => style.animationName)).toEqual([
          "none",
          "none",
        ]);
        expect(stepAnimation).toBe("none");
      } else {
        expect(styles[0].animationName).toContain("update-spin-clockwise");
        expect(styles[1].animationName).toContain(
          "update-spin-counterclockwise",
        );
        expect(styles.map((style) => style.animationDuration)).toEqual([
          "1.15s",
          "0.9s",
        ]);
        expect(stepAnimation).toContain("update-step-pulse");
      }
      await expect(overlay).toContainText(
        "The trusted update is being applied and health checked",
      );
    });
  });
}
