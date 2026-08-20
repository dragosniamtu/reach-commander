import { spawn, spawnSync } from 'node:child_process';
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';

interface SourceTemplate {
  id: string;
  path: string;
}

interface SourcesTemplate {
  sources: SourceTemplate[];
}

const healthUrl = 'http://127.0.0.1:8092/health';

async function waitForHealth(server: ReturnType<typeof spawn>): Promise<void> {
  const deadline = Date.now() + 60_000;

  while (Date.now() < deadline) {
    if (server.exitCode !== null) {
      throw new Error(`ReachCommander exited before becoming healthy (code ${server.exitCode}).`);
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

  const exited = new Promise<void>((resolveExit) => server.once('exit', () => resolveExit()));
  server.kill('SIGTERM');
  await Promise.race([exited, delay(5_000)]);

  if (server.exitCode === null) {
    server.kill('SIGKILL');
    await exited;
  }
}

export default async function seedFixtures(): Promise<() => Promise<void>> {
  const supportDirectory = dirname(fileURLToPath(import.meta.url));
  const e2eDirectory = resolve(supportDirectory, '..');
  const repositoryRoot = resolve(e2eDirectory, '..', '..');
  const angularOutput = join(
    repositoryRoot,
    'client',
    'reach-commander-ui',
    'dist',
    'reach-commander-ui',
    'browser',
    'index.html',
  );

  if (!existsSync(angularOutput)) {
    throw new Error('Angular production assets are missing. Run the client build before Playwright.');
  }

  const fixtureRoot = mkdtempSync(join(tmpdir(), 'reachcommander-e2e-'));
  const downloadsRoot = join(fixtureRoot, 'Downloads');
  const mediaRoot = join(fixtureRoot, 'Media');
  const usbRoot = join(fixtureRoot, 'USB-unmounted');
  const archiveRoot = join(fixtureRoot, 'Archive');

  mkdirSync(join(downloadsRoot, 'Complete', 'Project Hail Mary'), { recursive: true });
  mkdirSync(join(downloadsRoot, 'Incomplete'), { recursive: true });
  mkdirSync(join(downloadsRoot, 'Rename Lab', 'Drafts'), { recursive: true });
  mkdirSync(join(downloadsRoot, 'Conflict Lab'), { recursive: true });
  mkdirSync(join(mediaRoot, 'Movies'), { recursive: true });
  mkdirSync(join(mediaRoot, 'Kids'), { recursive: true });
  mkdirSync(join(mediaRoot, 'TV'), { recursive: true });
  mkdirSync(archiveRoot, { recursive: true });
  writeFileSync(join(downloadsRoot, 'Rename Lab', 'holiday-photo.jpg'), 'photo fixture\n');
  writeFileSync(join(downloadsRoot, 'Rename Lab', 'holiday-video.mp4'), 'video fixture\n');
  writeFileSync(join(downloadsRoot, 'Conflict Lab', 'one.txt'), 'one\n');
  writeFileSync(join(downloadsRoot, 'Conflict Lab', 'two.txt'), 'two\n');
  writeFileSync(join(downloadsRoot, 'existing.txt'), 'existing\n');
  writeFileSync(join(downloadsRoot, 'report-01.pdf'), 'two digit report\n');
  writeFileSync(join(downloadsRoot, 'report-1.pdf'), 'one digit report\n');
  writeFileSync(join(downloadsRoot, 'a+b[1].txt'), 'literal wildcard fixture\n');
  writeFileSync(join(mediaRoot, 'Movies', 'Gladiator II.mkv'), 'deterministic fixture\n');
  writeFileSync(join(archiveRoot, 'locked.txt'), 'read-only fixture\n');

  const configuration = JSON.parse(
    readFileSync(join(e2eDirectory, 'fixtures', 'sources.json'), 'utf8'),
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

  const configurationPath = join(fixtureRoot, 'sources.json');
  writeFileSync(configurationPath, JSON.stringify(configuration, null, 2));

  const publishDirectory = join(fixtureRoot, 'publish');
  const projectPath = join(repositoryRoot, 'src', 'ReachCommander.Api', 'ReachCommander.Api.csproj');
  const publish = spawnSync(
    'dotnet',
    [
      'publish',
      projectPath,
      '-c',
      'Release',
      '--no-restore',
      '-o',
      publishDirectory,
      '-p:BuildAngularOnPublish=false',
    ],
    { cwd: repositoryRoot, stdio: 'inherit' },
  );

  if (publish.status !== 0) {
    rmSync(fixtureRoot, { recursive: true, force: true });
    throw new Error(`Could not publish ReachCommander (exit code ${publish.status ?? 1}).`);
  }

  const server = spawn('dotnet', [join(publishDirectory, 'ReachCommander.Api.dll')], {
    cwd: publishDirectory,
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Production',
      ASPNETCORE_URLS: 'http://127.0.0.1:8092',
      HardwareMetrics__Enabled: 'false',
      ReachCommander__SourcesPath: configurationPath,
    },
    stdio: 'inherit',
  });

  try {
    await waitForHealth(server);
  } catch (error) {
    await stopServer(server);
    rmSync(fixtureRoot, { recursive: true, force: true });
    throw error;
  }

  return async () => {
    await stopServer(server);
    rmSync(fixtureRoot, { recursive: true, force: true });
  };
}
