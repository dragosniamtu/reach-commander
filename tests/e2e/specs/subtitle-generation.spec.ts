import { openSubtitlesMenu } from '../support/application-menu';
import { expect, test, type Page } from "@playwright/test";
import type {
  SubtitleCapabilities,
  SubtitleJobRequest,
  SubtitleJobStatus,
} from "../../../client/reach-commander-ui/src/app/features/subtitles/subtitle-generation.models";

test.use({ serviceWorkers: "block" });

const ready: SubtitleCapabilities = {
  enabled: true,
  workerAvailable: true,
  englishAvailable: true,
  romanianAvailable: true,
  reasonCode: null,
  detail: null,
};

test("explains disabled generation, keeps history available, and does not keep polling", async ({
  page,
}) => {
  await page.clock.install();
  const subtitles = await routeSubtitles(page, {
    ...ready,
    enabled: false,
    reasonCode: "subtitle_generation_disabled",
  });
  subtitles.jobs = [job({ phase: "completed", currentPath: null, files: [] })];
  await openMovie(page);
  await openSubtitlesMenu(page);
  const generateAction = page.getByTestId("toolbar-subtitles");
  await expect(generateAction).toBeEnabled();
  await expect(generateAction.locator("..")).toHaveAttribute(
    "title",
    /disabled on this server/,
  );
  await openSubtitlesMenu(page);
  await generateAction.click();
  const dialog = page.getByRole("dialog", { name: "Generate Subtitles" });
  await expect(dialog).toContainText("disabled on this server");
  await expect(dialog).toContainText("administrator");
  await expect(dialog.getByTestId("subtitle-generate")).toBeDisabled();
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  await openSubtitlesMenu(page);
  await page.getByTestId("toolbar-subtitle-jobs").click();
  const history = page.getByRole("dialog", { name: "Subtitle jobs" });
  await expect(history.getByLabel("Subtitle job")).toContainText("Completed");
  await expect(
    history.getByRole("button", { name: "Refresh jobs" }),
  ).toBeEnabled();
  const reads = subtitles.historyReads;
  await page.clock.fastForward(30_000);
  await expect(history.getByLabel("Subtitle job")).toContainText("Completed");
  expect(subtitles.historyReads).toBe(reads);
  expect(subtitles.creates).toEqual([]);
});

for (const scenario of [
  {
    capabilities: {
      ...ready,
      workerAvailable: false,
      reasonCode: "subtitle_worker_unavailable",
    },
    explanation: "worker is offline",
  },
  {
    capabilities: {
      ...ready,
      englishAvailable: false,
      romanianAvailable: false,
      reasonCode: "subtitle_models_missing",
    },
    explanation: "No subtitle language models are ready",
  },
]) {
  test(`shows ${scenario.explanation} and recovers with Recheck`, async ({
    page,
  }) => {
    const subtitles = await routeSubtitles(page, scenario.capabilities);
    const row = await openMovie(page);
    await row.click({ button: "right" });
    const action = page.getByTestId("context-subtitles");
    await expect(action).toHaveAttribute(
      "title",
      new RegExp(scenario.explanation),
    );
    await action.click();
    const dialog = page.getByRole("dialog", { name: "Generate Subtitles" });
    await expect(dialog).toContainText(scenario.explanation);
    await expect(dialog.getByTestId("subtitle-generate")).toBeDisabled();
    subtitles.capabilities = ready;
    await dialog.getByRole("button", { name: "Recheck availability" }).click();
    await expect(dialog.getByTestId("subtitle-generate")).toBeEnabled();
    expect(subtitles.creates).toEqual([]);
  });
}

test("offers English when Romanian is not installed and preserves source restrictions", async ({
  page,
}) => {
  const subtitles = await routeSubtitles(page, {
    ...ready,
    romanianAvailable: false,
    reasonCode: "subtitle_models_missing",
  });
  await openMovie(page);
  await openSubtitlesMenu(page);
  await page.getByTestId("toolbar-subtitles").click();
  const dialog = page.getByRole("dialog", { name: "Generate Subtitles" });
  await expect(dialog.getByLabel("English", { exact: true })).toBeChecked();
  await expect(
    dialog.getByLabel("Romanian", { exact: true }),
  ).not.toBeChecked();
  await expect(dialog.getByLabel("Romanian", { exact: true })).toBeDisabled();
  await expect(dialog).toContainText("Romanian is not set up");
  await expect(dialog.getByTestId("subtitle-generate")).toBeEnabled();
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  const panel = page.getByTestId("right-panel");
  await panel.getByTestId("source-archive").click();
  await panel.locator('tr[data-path="/Read Only Movie.mp4"]').click();
  await expect(page.getByTestId("toolbar-subtitles")).toBeDisabled();
  await expect(
    page.getByTestId("toolbar-subtitles").locator(".."),
  ).toHaveAttribute("title", /read-only/);
  await expect(page.getByTestId("toolbar-subtitle-jobs")).toBeEnabled();
  expect(subtitles.creates).toEqual([]);
});

test("generates selected languages, shows progress and completion, and refreshes visible files", async ({
  page,
}) => {
  const subtitles = await routeSubtitles(page);
  const fileReads: string[] = [];
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/files")
      fileReads.push(request.url());
  });
  const row = await openMovie(page);
  await openSubtitlesMenu(page);
  await page.getByTestId("toolbar-subtitles").click();
  const configure = page.getByRole("dialog", { name: "Generate Subtitles" });
  await configure.getByLabel("Romanian", { exact: true }).check();
  const skipExisting = configure.getByLabel("Skip successfully processed videos");
  await expect(skipExisting).toBeChecked();
  await skipExisting.uncheck();
  await expect(configure).toContainText("Existing subtitles are backed up.");
  await configure.getByTestId("subtitle-generate").click();
  const progress = page.getByRole("dialog", { name: "Subtitle jobs" });
  await expect(progress).toContainText("Queued");
  expect(subtitles.creates).toEqual([
    {
      sourceId: "media",
      path: "/Movies/Family Movie.mp4",
      english: true,
      romanian: true,
      skipExisting: false,
    },
  ]);
  subtitles.jobs = [job({ romanian: true, currentPercent: 45 })];
  await expect(progress.getByLabel("Current video progress")).toHaveAttribute(
    "value",
    "45",
  );
  await expect(progress).toContainText("English + Romanian");
  const beforeCompletion = fileReads.length;
  subtitles.jobs = [
    job({
      romanian: true,
      phase: "completed",
      currentPath: null,
      currentPercent: null,
      files: [
        { path: "/Movies/Family Movie.mp4", phase: "completed", error: null },
      ],
    }),
  ];
  await expect(progress).toContainText("1 of 1 files processed");
  await expect(progress.getByTestId("subtitle-cancel-job")).toHaveCount(0);
  await expect.poll(() => fileReads.length).toBeGreaterThan(beforeCompletion);
  await progress.getByRole("button", { name: "Close", exact: true }).click();
  await expect(row).toBeVisible();
  await openSubtitlesMenu(page);
  await page.getByTestId("toolbar-subtitles").click();
  await expect(configure.getByLabel("Skip successfully processed videos")).toBeChecked();
  await configure.getByRole("button", { name: "Cancel", exact: true }).click();
  await page.reload();
  await openSubtitlesMenu(page);
  await page.getByTestId("toolbar-subtitle-jobs").click();
  await expect(
    page.getByRole("dialog", { name: "Subtitle jobs" }),
  ).toContainText("1 completed");
});

test("cancels a restored job and refreshes retained history while generation is disabled", async ({
  page,
}) => {
  const subtitles = await routeSubtitles(page, {
    ...ready,
    enabled: false,
    reasonCode: "subtitle_generation_disabled",
  });
  subtitles.jobs = [job()];
  await page.goto("/");
  await openSubtitlesMenu(page);
  await page.getByTestId("toolbar-subtitle-jobs").click();
  const dialog = page.getByRole("dialog", { name: "Subtitle jobs" });
  await expect(dialog).toContainText("Running");
  await dialog.getByTestId("subtitle-cancel-job").click();
  await expect(dialog).toContainText("Cancelled");
  expect(subtitles.cancels).toBe(1);
  await expect(dialog.getByTestId("subtitle-cancel-job")).toHaveCount(0);
  subtitles.jobs = [
    job({
      phase: "failed",
      currentPath: null,
      error: "No audio stream.",
      files: [
        {
          path: "/Movies/Family Movie.mp4",
          phase: "failed",
          error: "No audio stream.",
        },
      ],
    }),
  ];
  await dialog.getByRole("button", { name: "Refresh jobs" }).click();
  await expect(dialog.getByLabel("Subtitle file results")).toContainText(
    "No audio stream.",
  );
  await expect(dialog).toContainText("1 failed");
});

test("keeps file navigation usable during readiness loading and supports retry after a check fails", async ({
  page,
}) => {
  const subtitles = await routeSubtitles(page);
  let finishCheck!: () => void;
  subtitles.capabilitiesGate = new Promise<void>((resolve) => {
    finishCheck = resolve;
  });
  subtitles.capabilitiesStatus = 503;
  await openMovie(page);
  await openSubtitlesMenu(page);
  await page.getByTestId("toolbar-subtitles").click();
  const dialog = page.getByRole("dialog", { name: "Generate Subtitles" });
  await expect(dialog).toContainText("Checking subtitle availability");
  await expect(dialog.getByTestId("subtitle-generate")).toBeDisabled();
  finishCheck();
  await expect(dialog).toContainText("readiness could not be checked");
  subtitles.capabilitiesStatus = 200;
  await dialog.getByRole("button", { name: "Recheck availability" }).click();
  await expect(dialog.getByTestId("subtitle-generate")).toBeEnabled();
});

for (const entryPoint of ['menu', 'context menu'] as const) {
  test(`generates a selected batch through the ${entryPoint} and restores its full progress`, async ({ page }, testInfo) => {
    const subtitles = await routeSubtitles(page);
    const first = await openMovie(page);
    const panel = page.getByTestId('right-panel');
    const second = panel.locator('tr[data-path="/Movies/Gladiator II.mkv"]');
    await second.click({ modifiers: ['Control'] });
    await expect(panel.locator('tr[aria-selected="true"]')).toHaveCount(2);
    if (entryPoint === 'context menu') {
      await first.click({ button: 'right' });
      await page.getByTestId('context-subtitles').click();
      await page.setViewportSize({ width: 390, height: 844 });
    } else {
      await openSubtitlesMenu(page);
      await page.getByTestId('toolbar-subtitles').click();
    }
    const configure = page.getByRole('dialog', { name: 'Generate Subtitles' });
    const paths = ['/Movies/Family Movie.mp4', '/Movies/Gladiator II.mkv'];
    await expect(configure).toContainText('2 videos selected');
    await expect(configure.getByRole('list', { name: 'Selected videos' }).locator('li'))
      .toHaveText(paths);
    await expect(configure).toContainText('one at a time');
    await expect(configure.getByTestId('subtitle-generate')).toBeEnabled();
    if (entryPoint === 'context menu') {
      const bounds = await configure.boundingBox();
      expect(bounds!.x).toBeGreaterThanOrEqual(0);
      expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(390);
      await page.screenshot({ path: testInfo.outputPath('batch-subtitles-phone.png') });
    }
    await configure.getByLabel('Romanian', { exact: true }).check();
    await configure.getByTestId('subtitle-generate').click();
    const progress = page.getByRole('dialog', { name: 'Subtitle jobs' });
    await expect(progress).toContainText('0 of 2 files processed');
    expect(subtitles.creates).toEqual([{
      sourceId: 'media', path: paths[0], paths,
      english: true, romanian: true, skipExisting: true,
    }]);
    const files = paths.map(path => ({ path, phase: 'pending' as const, error: null }));
    subtitles.jobs = [job({ romanian: true, currentPath: paths[0], files: [
      { ...files[0], phase: 'running' }, files[1],
    ] })];
    await expect(progress.getByRole('list', { name: 'Subtitle file results' }).locator('li').first())
      .toContainText('Running');
    subtitles.jobs = [job({ romanian: true, currentPath: paths[1], files: [
      { ...files[0], phase: 'completed' }, { ...files[1], phase: 'running' },
    ] })];
    await expect(progress).toContainText('1 of 2 files processed');
    await progress.getByRole('button', { name: 'Close', exact: true }).click();
    await page.reload();
    await openSubtitlesMenu(page);
    await page.getByTestId('toolbar-subtitle-jobs').click();
    await expect(progress).toContainText('1 of 2 files processed');
    await expect(progress.getByRole('list', { name: 'Subtitle file results' }).locator('li'))
      .toHaveCount(2);
    subtitles.jobs = [job({ romanian: true, phase: 'completed', currentPath: null, files: [
      { ...files[0], phase: 'completed' }, { ...files[1], phase: 'completed' },
    ] })];
    await expect(progress).toContainText('2 of 2 files processed');
    expect(subtitles.creates).toHaveLength(1);
  });
}

async function openMovie(page: Page) {
  await page.goto("/");
  const panel = page.getByTestId("right-panel");
  await panel.getByTestId("source-media").click();
  await panel.locator('tr[data-path="/Movies"]').dblclick();
  const row = panel.locator('tr[data-path="/Movies/Family Movie.mp4"]');
  await row.click();
  return row;
}

function job(changes: Partial<SubtitleJobStatus> = {}): SubtitleJobStatus {
  return {
    jobId: "11111111-1111-4111-8111-111111111111",
    sourceId: "media",
    path: "/Movies/Family Movie.mp4",
    english: true,
    romanian: false,
    phase: "running",
    stage: "transcribing",
    currentPath: "/Movies/Family Movie.mp4",
    currentPercent: 30,
    files: [
      { path: "/Movies/Family Movie.mp4", phase: "running", error: null },
    ],
    createdAt: "2026-11-24T12:00:00Z",
    updatedAt: "2026-11-24T12:01:00Z",
    error: null,
    ...changes,
  };
}

async function routeSubtitles(
  page: Page,
  capabilities: SubtitleCapabilities = ready,
) {
  const state = {
    capabilities,
    capabilitiesGate: Promise.resolve(),
    capabilitiesStatus: 200,
    jobs: [] as SubtitleJobStatus[],
    creates: [] as SubtitleJobRequest[],
    cancels: 0,
    historyReads: 0,
  };
  await page.route("**/api/subtitle-jobs**", async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path.endsWith("/capabilities")) {
      await state.capabilitiesGate;
      await route.fulfill({
        status: state.capabilitiesStatus,
        json: state.capabilities,
      });
    } else if (path.endsWith("/cancel")) {
      state.cancels++;
      state.jobs = [
        job({
          phase: "cancelled",
          currentPath: null,
          currentPercent: null,
          files: [
            {
              path: "/Movies/Family Movie.mp4",
              phase: "cancelled",
              error: null,
            },
          ],
        }),
      ];
      await route.fulfill({ json: state.jobs[0] });
    } else if (request.method() === "POST") {
      const body = request.postDataJSON() as SubtitleJobRequest;
      state.creates.push(body);
      state.jobs = [
        job({
          ...body,
          phase: "queued",
          currentPath: null,
          currentPercent: null,
          files: (body.paths ?? [body.path]).map(path => ({ path, phase: "pending", error: null })),
        }),
      ];
      await route.fulfill({ status: 202, json: state.jobs[0] });
    } else {
      state.historyReads++;
      await route.fulfill({ json: state.jobs });
    }
  });
  return state;
}
