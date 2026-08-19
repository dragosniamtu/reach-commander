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
import { fileURLToPath } from 'node:url';

interface SourceTemplate {
  id: string;
  path: string;
}

interface SourcesTemplate {
  sources: SourceTemplate[];
}

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

mkdirSync(join(downloadsRoot, 'Complete', 'Project Hail Mary'), { recursive: true });
mkdirSync(join(downloadsRoot, 'Incomplete'), { recursive: true });
mkdirSync(join(mediaRoot, 'Movies'), { recursive: true });
mkdirSync(join(mediaRoot, 'Kids'), { recursive: true });
mkdirSync(join(mediaRoot, 'TV'), { recursive: true });
writeFileSync(join(mediaRoot, 'Movies', 'Gladiator II.mkv'), 'deterministic fixture\n');

const configuration = JSON.parse(
  readFileSync(join(e2eDirectory, 'fixtures', 'sources.json'), 'utf8'),
) as SourcesTemplate;
const sourcePaths: Record<string, string> = {
  downloads: downloadsRoot,
  media: mediaRoot,
  usb: usbRoot,
};

for (const source of configuration.sources) {
  source.path = sourcePaths[source.id] ?? source.path;
}

const configurationPath = join(fixtureRoot, 'sources.json');
writeFileSync(configurationPath, JSON.stringify(configuration, null, 2));

const publishDirectory = join(repositoryRoot, 'artifacts', 'e2e-publish');
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
  process.exit(publish.status ?? 1);
}

const server = spawn('dotnet', [join(publishDirectory, 'ReachCommander.Api.dll')], {
  cwd: publishDirectory,
  env: {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: 'Production',
    ASPNETCORE_URLS: 'http://127.0.0.1:8092',
    ReachCommander__SourcesPath: configurationPath,
  },
  stdio: 'inherit',
});

let stopping = false;
const cleanUp = (): void => {
  rmSync(fixtureRoot, { recursive: true, force: true });
};

const stop = (): void => {
  if (stopping) {
    return;
  }

  stopping = true;
  if (server.exitCode === null) {
    server.kill('SIGTERM');
    return;
  }

  cleanUp();
  process.exit(0);
};

process.on('SIGINT', stop);
process.on('SIGTERM', stop);
process.on('exit', cleanUp);

server.on('exit', (code) => {
  cleanUp();
  process.exit(stopping ? 0 : code ?? 1);
});

server.on('error', (error) => {
  console.error(`Could not start ReachCommander: ${error.message}`);
  cleanUp();
  process.exit(1);
});
