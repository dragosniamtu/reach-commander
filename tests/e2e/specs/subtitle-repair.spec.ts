import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { expect, test } from "@playwright/test";
import { openSubtitlesMenu, selectTheme } from "../support/application-menu";

test.use({ serviceWorkers: "block" });

for (const theme of ["default", "norton", "windows95"]) {
  test(`repairs legacy subtitle encoding and timing in ${theme} while preserving the original`, async ({
    page,
  }, testInfo) => {
    const ffmpegAvailable = spawnSync("ffmpeg", ["-version"]).status === 0;
    const ffprobeAvailable = spawnSync("ffprobe", ["-version"]).status === 0;
    test.skip(
      !ffmpegAvailable || !ffprobeAvailable,
      "Real media preview requires FFmpeg and FFprobe.",
    );

    const folderName = `Repair Lab ${theme}`;
    const folder = join(
      process.env["REACHCOMMANDER_E2E_MEDIA_ROOT"]!,
      folderName,
    );
    mkdirSync(folder, { recursive: true });
    const subtitlePath = join(folder, "Repair Movie.srt");
    const backupPath = join(folder, "Repair Movie_original.srt");
    const original = Buffer.concat([
      Buffer.from("1\r\n00:00:01,000 --> 00:00:02,000\r\n", "ascii"),
      Buffer.from([0xcf, 0xf0, 0xe8, 0xe2, 0xe5, 0xf2]),
      Buffer.from("\r\n", "ascii"),
    ]);
    writeFileSync(subtitlePath, original);
    const video = spawnSync(
      "ffmpeg",
      [
        "-nostdin",
        "-loglevel",
        "error",
        "-y",
        "-f",
        "lavfi",
        "-i",
        "color=c=black:s=96x64:r=15:d=3",
        "-f",
        "lavfi",
        "-i",
        "sine=frequency=440:duration=3",
        "-c:v",
        "libx264",
        "-pix_fmt",
        "yuv420p",
        "-c:a",
        "aac",
        "-movflags",
        "+faststart",
        join(folder, "Repair Movie.mp4"),
      ],
      { encoding: "utf8" },
    );
    expect(video.status, video.stderr).toBe(0);

    let capabilityChecks = 0;
    let starts = 0;
    let cancellations = 0;
    await page.route("**/api/media-previews/repair-capabilities", (route) => {
      capabilityChecks++;
      return route.fulfill({
        json:
          capabilityChecks === 1
            ? {
                enabled: true,
                available: false,
                reasonCode: "subtitle_repair_unavailable",
              }
            : { enabled: true, available: true },
      });
    });
    await page.route("**/api/media-previews/*/repair", (route) => {
      const method = route.request().method();
      if (method === "POST") starts++;
      if (method === "DELETE") {
        cancellations++;
        return route.fulfill({
          json: { state: "cancelled", stage: "Cancelled", percent: 0 },
        });
      }
      const completed = method === "GET" && starts === 2;
      return route.fulfill({
        json: completed
          ? {
              state: "completed",
              stage: "Timing estimate ready",
              percent: 100,
              offsetMilliseconds: 250,
              timeScale: 1.05,
            }
          : { state: "running", stage: "Analyzing audio", percent: 35 },
      });
    });
    await page.goto("/");
    await selectTheme(page, theme);
    const panel = page.getByTestId("right-panel");
    await panel.getByTestId("source-media").click();
    const folderRow = panel.locator(`tr[data-path="/${folderName}"]`);
    await folderRow.click();
    await openSubtitlesMenu(page);
    const menuRepair = page.getByTestId("toolbar-repair-subtitles");
    await expect(menuRepair).toBeDisabled();
    await page.getByTestId("application-menu-trigger").press("Escape");
    await folderRow.dblclick();
    await panel
      .locator(`tr[data-path="/${folderName}/Repair Movie.mp4"]`)
      .click();
    await openSubtitlesMenu(page);
    await menuRepair.click();

    const dialog = page.getByRole("dialog", { name: "Synchronize subtitles" });
    const repair = dialog.getByRole("button", {
      name: "Repair subtitles",
      exact: true,
    });
    await expect(dialog.getByText(/up-to-date subtitle worker/)).toBeVisible();
    await dialog.getByRole("button", { name: "Recheck", exact: true }).click();
    const selectEncoding = dialog.getByLabel("Source encoding");
    await selectEncoding.selectOption("windows1251");
    await expect(dialog.getByLabel("Decoded subtitle sample")).toContainText(
      "Привет",
    );
    await expect(repair).toBeDisabled();
    await expect(
      dialog.getByRole("button", { name: "Review save" }),
    ).toHaveCount(0);

    const confirmation = page.waitForRequest(
      (request) =>
        request.method() === "PUT" &&
        request.url().endsWith("/subtitle") &&
        request.postDataJSON()?.encodingReviewed === true,
    );
    await dialog
      .getByRole("button", { name: "Confirm decoded text", exact: true })
      .click();
    expect((await confirmation).postDataJSON()).toMatchObject({
      sourceEncoding: "windows1251",
    });
    await expect(repair).toBeEnabled();

    await repair.click();
    await expect(
      dialog.getByRole("progressbar", {
        name: "Automatic timing repair progress",
      }),
    ).toHaveAttribute("value", "35");
    await expect(dialog.getByTestId("offset-field")).toBeDisabled();
    await dialog.getByRole("button", { name: "Cancel repair" }).click();
    await expect(dialog.getByText("Timing repair cancelled.")).toBeVisible();
    await expect(dialog.getByTestId("offset-field")).toHaveValue("0");

    await repair.click();
    await expect(dialog.getByTestId("offset-field")).toHaveValue("250");
    await expect(dialog.getByText("1.050000×")).toBeVisible();
    await dialog.getByRole("button", { name: "+100 ms", exact: true }).click();
    await expect(dialog.getByTestId("offset-field")).toHaveValue("350");
    await testInfo.attach("subtitle-repair-preview", {
      body: await dialog.screenshot({
        path: testInfo.outputPath("subtitle-repair-preview.png"),
      }),
      contentType: "image/png",
    });
    expect(readFileSync(subtitlePath)).toEqual(original);
    expect(existsSync(backupPath)).toBe(false);

    await dialog.getByRole("button", { name: "Review save" }).click();
    await expect(
      dialog.getByText(`/${folderName}/Repair Movie_original.srt`, {
        exact: true,
      }),
    ).toBeVisible();
    await dialog
      .getByRole("button", { name: "Save corrected subtitle" })
      .click();
    await expect(dialog.getByText("Corrected subtitle saved")).toBeVisible();
    expect(readFileSync(backupPath)).toEqual(original);
    expect(readFileSync(subtitlePath, "utf8")).toContain(
      "00:00:01,400 --> 00:00:02,450",
    );
    expect(readFileSync(subtitlePath, "utf8")).toContain("Привет");
    expect(capabilityChecks).toBe(2);
    expect(cancellations).toBe(1);

    // Exercise repeat-save rebasing once; three complete flows share the real 20/minute limit.
    if (theme === "default") {
      const firstSave = readFileSync(subtitlePath);
      await expect(dialog.getByTestId("offset-field")).toHaveValue("0");
      await expect(dialog.getByText("1.050000×")).toHaveCount(0);
      await expect(
        dialog.getByText("Decoded as UTF-8. Saves use UTF-8."),
      ).toBeVisible();
      await dialog
        .getByRole("button", { name: "+100 ms", exact: true })
        .click();
      const player = dialog.locator("video");
      await player.evaluate((element: HTMLVideoElement) => {
        element.currentTime = 1.45;
        element.dispatchEvent(new Event("timeupdate"));
      });
      await expect(dialog.locator(".subtitle-overlay")).toHaveCount(0);
      await player.evaluate((element: HTMLVideoElement) => {
        element.currentTime = 1.55;
        element.dispatchEvent(new Event("timeupdate"));
      });
      await expect(dialog.locator(".subtitle-overlay")).toHaveText("Привет");

      await dialog.getByRole("button", { name: "Review save" }).click();
      const secondBackupName = "Repair Movie_original (2).srt";
      await expect(
        dialog.getByText(`/${folderName}/${secondBackupName}`, { exact: true }),
      ).toBeVisible();
      await dialog
        .getByRole("button", { name: "Save corrected subtitle" })
        .click();
      await expect(dialog.getByText("Corrected subtitle saved")).toBeVisible();
      await expect(dialog.getByTestId("offset-field")).toHaveValue("0");
      expect(readFileSync(subtitlePath, "utf8")).toContain(
        "00:00:01,500 --> 00:00:02,550",
      );
      expect(readFileSync(join(folder, secondBackupName))).toEqual(firstSave);
      expect(readFileSync(backupPath)).toEqual(original);
    }

    await dialog.getByRole("button", { name: "Close", exact: true }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByTestId("application-menu-trigger")).toBeFocused();
  });
}
