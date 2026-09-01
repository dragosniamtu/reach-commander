import { spawn, spawnSync } from "node:child_process";
import {
  existsSync,
  chmodSync,
  copyFileSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { fileURLToPath } from "node:url";
import { e2eAuthStatePath, e2eSetupCodePath } from "./authentication";
import { longFileNameFixture } from "./fixture-names";

interface SourceTemplate {
  id: string;
  path: string;
}

interface SourcesTemplate {
  sources: SourceTemplate[];
}

export type SystemUpdatePhase =
  | "unavailable"
  | "checking"
  | "current"
  | "available"
  | "blocked"
  | "applying"
  | "completed"
  | "rolledBack"
  | "failed";

export type SystemUpdateProgressStage =
  | "downloading"
  | "installing"
  | "restarting"
  | "healthChecking"
  | "restoring"
  | "restartingPrevious"
  | "verifyingRecovery";

export interface SystemUpdateTraceEventFixture {
  readonly sequence: number;
  readonly timestamp: string;
  readonly elapsedSeconds: number;
  readonly code: string;
  readonly stage: SystemUpdateProgressStage | null;
  readonly outcome:
    "started" | "activity" | "succeeded" | "failed" | "timedOut";
}

export interface SystemUpdateTraceFixture {
  readonly startedAt: string;
  readonly elapsedSeconds: number;
  readonly lastActivityAt: string | null;
  readonly events: readonly SystemUpdateTraceEventFixture[];
}

export interface SystemUpdateFixture {
  readonly protocolVersion: number;
  readonly supported: boolean;
  readonly channel: string | null;
  readonly currentVersion: string | null;
  readonly targetVersion: string | null;
  readonly phase: SystemUpdatePhase;
  readonly progressStage: SystemUpdateProgressStage | null;
  readonly updateAvailable: boolean;
  readonly canApply: boolean;
  readonly reasonCode: string | null;
  readonly detail: string | null;
  readonly operationId: string | null;
  readonly lastCheckedAt: string | null;
  readonly updatedAt: string;
  readonly trace: SystemUpdateTraceFixture | null;
}

export function systemUpdateFixture(
  overrides: Partial<SystemUpdateFixture> = {},
): SystemUpdateFixture {
  return {
    protocolVersion: 1,
    supported: true,
    channel: "stable",
    currentVersion: "v1.3.0",
    targetVersion: null,
    phase: "current",
    progressStage: null,
    updateAvailable: false,
    canApply: false,
    reasonCode: "already_current",
    detail: "ReachCommander is up to date.",
    operationId: null,
    lastCheckedAt: "2026-08-25T10:00:00Z",
    updatedAt: "2026-08-25T10:00:00Z",
    trace: null,
    ...overrides,
  };
}

const healthUrl = "http://127.0.0.1:8092/health";
const setupCodePattern =
  /ReachCommander first-run setup code:\s+([A-Za-z0-9_-]{40,})/;

function captureSetupCode(server: ReturnType<typeof spawn>): Promise<string> {
  return new Promise((resolveCode, rejectCode) => {
    let output = "";
    let captured = false;
    const mirror = (target: NodeJS.WriteStream) => (chunk: Buffer) => {
      target.write(chunk);
      output = (output + chunk.toString("utf8")).slice(-16_384);
      const match = setupCodePattern.exec(output);
      if (!captured && match?.[1]) {
        captured = true;
        resolveCode(match[1]);
      }
    };

    server.stdout?.on("data", mirror(process.stdout));
    server.stderr?.on("data", mirror(process.stderr));
    server.once("exit", (code) => {
      if (!captured) {
        rejectCode(
          new Error(
            `ReachCommander exited before emitting a setup code (${code}).`,
          ),
        );
      }
    });
  });
}

async function waitForHealth(server: ReturnType<typeof spawn>): Promise<void> {
  const deadline = Date.now() + 60_000;

  while (Date.now() < deadline) {
    if (server.exitCode !== null) {
      throw new Error(
        `ReachCommander exited before becoming healthy (code ${server.exitCode}).`,
      );
    }

    try {
      const response = await fetch(healthUrl);
      if (response.ok) {
        return;
      }
    } catch {
      // The port is not accepting requests yet.
    }

    await delay(250);
  }

  throw new Error(`ReachCommander did not become healthy at ${healthUrl}.`);
}

async function stopServer(server: ReturnType<typeof spawn>): Promise<void> {
  if (server.exitCode !== null) {
    return;
  }

  const exited = new Promise<void>((resolveExit) =>
    server.once("exit", () => resolveExit()),
  );
  server.kill("SIGTERM");
  await Promise.race([exited, delay(5_000)]);

  if (server.exitCode === null) {
    server.kill("SIGKILL");
    await exited;
  }
}

export default async function seedFixtures(): Promise<() => Promise<void>> {
  const supportDirectory = dirname(fileURLToPath(import.meta.url));
  const e2eDirectory = resolve(supportDirectory, "..");
  const repositoryRoot = resolve(e2eDirectory, "..", "..");
  const angularOutput = join(
    repositoryRoot,
    "client",
    "reach-commander-ui",
    "dist",
    "reach-commander-ui",
    "browser",
    "index.html",
  );

  if (!existsSync(angularOutput)) {
    throw new Error(
      "Angular production assets are missing. Run the client build before Playwright.",
    );
  }

  const fixtureRoot = mkdtempSync(join(tmpdir(), "reachcommander-e2e-"));
  const authenticationDataRoot = join(fixtureRoot, "auth-data");
  const downloadsRoot = join(fixtureRoot, "Downloads");
  const mediaRoot = join(fixtureRoot, "Media");
  const usbRoot = join(fixtureRoot, "USB-unmounted");
  const archiveRoot = join(fixtureRoot, "Archive");

  mkdirSync(join(downloadsRoot, "Complete", "Project Hail Mary"), {
    recursive: true,
  });
  mkdirSync(join(downloadsRoot, "Incomplete"), { recursive: true });
  mkdirSync(join(downloadsRoot, "Rename Lab", "Drafts"), { recursive: true });
  mkdirSync(join(downloadsRoot, "Conflict Lab"), { recursive: true });
  mkdirSync(join(downloadsRoot, "Single Rename Lab", "File Case"), {
    recursive: true,
  });
  mkdirSync(
    join(downloadsRoot, "Single Rename Lab", "Folder Case", "Old Folder"),
    { recursive: true },
  );
  mkdirSync(join(downloadsRoot, "Single Rename Lab", "Literal Case"), {
    recursive: true,
  });
  mkdirSync(join(downloadsRoot, "Single Rename Lab", "Conflict Case"), {
    recursive: true,
  });
  mkdirSync(join(downloadsRoot, "File Ops", "Copy Source"), {
    recursive: true,
  });
  mkdirSync(join(downloadsRoot, "File Ops", "Move Source"), {
    recursive: true,
  });
  mkdirSync(join(downloadsRoot, "File Ops", "Delete Source"), {
    recursive: true,
  });
  mkdirSync(join(downloadsRoot, "File Ops", "Permanent Source"), {
    recursive: true,
  });
  mkdirSync(join(downloadsRoot, "File Ops", "New Directory"), {
    recursive: true,
  });
  mkdirSync(join(mediaRoot, "Movies"), { recursive: true });
  mkdirSync(join(mediaRoot, "Kids"), { recursive: true });
  mkdirSync(join(mediaRoot, "TV"), { recursive: true });
  mkdirSync(join(mediaRoot, "Extracted"), { recursive: true });
  mkdirSync(join(mediaRoot, "Whole"), { recursive: true });
  mkdirSync(join(mediaRoot, "Conflicts", "Family"), { recursive: true });
  mkdirSync(join(mediaRoot, "File Ops", "Copy Target"), { recursive: true });
  mkdirSync(join(mediaRoot, "File Ops", "Move Target"), { recursive: true });
  mkdirSync(archiveRoot, { recursive: true });
  writeFileSync(
    join(downloadsRoot, "Rename Lab", "holiday-photo.jpg"),
    "photo fixture\n",
  );
  writeFileSync(
    join(downloadsRoot, "Rename Lab", "holiday-video.mp4"),
    "video fixture\n",
  );
  writeFileSync(join(downloadsRoot, "Conflict Lab", "one.txt"), "one\n");
  writeFileSync(join(downloadsRoot, "Conflict Lab", "two.txt"), "two\n");
  writeFileSync(
    join(downloadsRoot, "Single Rename Lab", "File Case", "draft.txt"),
    "draft\n",
  );
  writeFileSync(
    join(downloadsRoot, "Single Rename Lab", "Literal Case", "literal.txt"),
    "literal\n",
  );
  writeFileSync(
    join(downloadsRoot, "Single Rename Lab", "Conflict Case", "source.txt"),
    "source\n",
  );
  writeFileSync(
    join(downloadsRoot, "Single Rename Lab", "Conflict Case", "existing.txt"),
    "existing\n",
  );
  writeFileSync(join(downloadsRoot, "existing.txt"), "existing\n");
  writeFileSync(join(downloadsRoot, "report-01.pdf"), "two digit report\n");
  writeFileSync(join(downloadsRoot, "report-1.pdf"), "one digit report\n");
  writeFileSync(
    join(downloadsRoot, "a+b[1].txt"),
    "literal wildcard fixture\n",
  );
  writeFileSync(
    join(downloadsRoot, "File Ops", "Copy Source", "alpha.bin"),
    "new alpha payload\n",
  );
  writeFileSync(
    join(downloadsRoot, "File Ops", "Copy Source", "beta.bin"),
    "beta payload\n",
  );
  writeFileSync(
    join(downloadsRoot, "File Ops", "Copy Source", "copy-canary.txt"),
    "copy canary\n",
  );
  writeFileSync(
    join(downloadsRoot, "File Ops", "Move Source", "move-me.iso"),
    "move payload\n",
  );
  writeFileSync(
    join(downloadsRoot, "File Ops", "Move Source", "move-canary.txt"),
    "move canary\n",
  );
  writeFileSync(
    join(downloadsRoot, "File Ops", "Delete Source", "photo.jpg"),
    "trashed photo\n",
  );
  writeFileSync(
    join(downloadsRoot, "File Ops", "Delete Source", "delete-canary.txt"),
    "delete canary\n",
  );
  writeFileSync(
    join(downloadsRoot, "File Ops", "Permanent Source", "doomed.txt"),
    "permanent payload\n",
  );
  writeFileSync(
    join(mediaRoot, "File Ops", "Copy Target", "alpha.bin"),
    "existing alpha payload\n",
  );
  writeFileSync(
    join(mediaRoot, "Movies", "Gladiator II.mkv"),
    "deterministic fixture\n",
  );
  writeFileSync(join(mediaRoot, "Movies", "Family Movie.mp4"), "mocked video fixture\n");
  writeFileSync(
    join(mediaRoot, "Movies", "Family Movie.srt"),
    "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello family\r\n",
  );
  writeFileSync(
    join(mediaRoot, "Movies", "Alternate.srt"),
    "1\r\n00:00:03,000 --> 00:00:04,000\r\nAlternate cue\r\n",
  );
  writeFileSync(join(mediaRoot, "Movies", "Fallback Movie.mkv"), "mocked MKV fixture\n");
  writeFileSync(
    join(mediaRoot, "Movies", "Fallback Movie.srt"),
    "1\r\n00:00:01,000 --> 00:00:02,000\r\nFallback cue\r\n",
  );
  writeFileSync(
    join(mediaRoot, "Movies", longFileNameFixture),
    "long filename layout fixture\n",
  );
  writeFileSync(join(mediaRoot, "Conflicts", "root.txt"), "conflict fixture\n");
  writeFileSync(join(archiveRoot, "locked.txt"), "read-only fixture\n");
  writeFileSync(join(archiveRoot, "Read Only Movie.mp4"), "mocked read-only video\n");
  writeFileSync(
    join(archiveRoot, "Read Only Movie.srt"),
    "1\r\n00:00:01,000 --> 00:00:02,000\r\nRead-only cue\r\n",
  );

  const archiveFixtures = join(repositoryRoot, "tests", "fixtures", "archives");
  for (const name of ["nested.zip", "sample.7z"]) {
    copyFileSync(join(archiveFixtures, name), join(downloadsRoot, name));
  }
  copyFileSync(
    join(archiveFixtures, "nested.zip"),
    join(downloadsRoot, "stale.zip"),
  );
  for (let part = 1; part <= 6; part++) {
    const name = `Rar.multi.part${String(part).padStart(2, "0")}.rar`;
    copyFileSync(join(archiveFixtures, name), join(downloadsRoot, name));
  }
  process.env["REACHCOMMANDER_E2E_DOWNLOADS_ROOT"] = downloadsRoot;
  process.env["REACHCOMMANDER_E2E_MEDIA_ROOT"] = mediaRoot;

  const configuration = JSON.parse(
    readFileSync(join(e2eDirectory, "fixtures", "sources.json"), "utf8"),
  ) as SourcesTemplate;
  const sourcePaths: Record<string, string> = {
    downloads: downloadsRoot,
    media: mediaRoot,
    usb: usbRoot,
    archive: archiveRoot,
  };

  for (const source of configuration.sources) {
    source.path = sourcePaths[source.id] ?? source.path;
  }

  const configurationPath = join(fixtureRoot, "sources.json");
  writeFileSync(configurationPath, JSON.stringify(configuration, null, 2));

  const publishDirectory = join(fixtureRoot, "publish");
  const projectPath = join(
    repositoryRoot,
    "src",
    "ReachCommander.Api",
    "ReachCommander.Api.csproj",
  );
  const publish = spawnSync(
    "dotnet",
    [
      "publish",
      projectPath,
      "-c",
      "Release",
      "--no-restore",
      "-o",
      publishDirectory,
      "-p:BuildAngularOnPublish=false",
    ],
    { cwd: repositoryRoot, stdio: "inherit" },
  );

  if (publish.status !== 0) {
    rmSync(fixtureRoot, { recursive: true, force: true });
    throw new Error(
      `Could not publish ReachCommander (exit code ${publish.status ?? 1}).`,
    );
  }

  rmSync(e2eSetupCodePath, { force: true });
  rmSync(e2eAuthStatePath, { force: true });
  mkdirSync(dirname(e2eSetupCodePath), { recursive: true });

  const server = spawn(
    "dotnet",
    [join(publishDirectory, "ReachCommander.Api.dll")],
    {
      cwd: publishDirectory,
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: "Testing",
        ASPNETCORE_URLS: "http://127.0.0.1:8092",
        Authentication__DataPath: authenticationDataRoot,
        HardwareMetrics__Enabled: "false",
        Logging__EventLog__LogLevel__Default: "None",
        ReachCommander__SourcesPath: configurationPath,
      },
      stdio: ["ignore", "pipe", "pipe"],
    },
  );
  const setupCodePromise = captureSetupCode(server);

  try {
    const [, setupCode] = await Promise.all([
      waitForHealth(server),
      setupCodePromise,
    ]);
    writeFileSync(e2eSetupCodePath, `${setupCode}\n`, {
      encoding: "utf8",
      flag: "wx",
      mode: 0o600,
    });
    chmodSync(e2eSetupCodePath, 0o600);
  } catch (error) {
    await stopServer(server);
    rmSync(fixtureRoot, { recursive: true, force: true });
    rmSync(e2eSetupCodePath, { force: true });
    rmSync(e2eAuthStatePath, { force: true });
    throw error;
  }

  return async () => {
    await stopServer(server);
    rmSync(fixtureRoot, { recursive: true, force: true });
    rmSync(e2eSetupCodePath, { force: true });
    rmSync(e2eAuthStatePath, { force: true });
  };
}
