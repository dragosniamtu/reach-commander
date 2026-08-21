import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const readJson = (path) => JSON.parse(readFileSync(join(root, path), 'utf8'));

function pngSize(path) {
  const bytes = readFileSync(join(root, path));
  assert.deepEqual([...bytes.subarray(0, 8)], [137, 80, 78, 71, 13, 10, 26, 10]);
  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
}

test('declares installable ReachCommander branding with correctly sized icons', () => {
  const manifest = readJson('public/manifest.webmanifest');
  assert.equal(manifest.name, 'ReachCommander');
  assert.equal(manifest.short_name, 'ReachCommander');
  assert.equal(manifest.start_url, '/');
  assert.equal(manifest.scope, '/');
  assert.equal(manifest.display, 'standalone');
  assert.deepEqual(
    manifest.icons.map(({ src, sizes, purpose }) => ({ src, sizes, purpose })),
    [
      { src: 'icons/icon-192.png', sizes: '192x192', purpose: 'any' },
      { src: 'icons/icon-512.png', sizes: '512x512', purpose: 'any' },
      { src: 'icons/icon-maskable-192.png', sizes: '192x192', purpose: 'maskable' },
      { src: 'icons/icon-maskable-512.png', sizes: '512x512', purpose: 'maskable' },
    ],
  );

  for (const [path, size] of [
    ['public/icons/icon-192.png', 192],
    ['public/icons/icon-512.png', 512],
    ['public/icons/icon-maskable-192.png', 192],
    ['public/icons/icon-maskable-512.png', 512],
    ['public/icons/apple-touch-icon.png', 180],
    ['public/icons/favicon-32.png', 32],
  ]) {
    assert.deepEqual(pngSize(path), { width: size, height: size });
  }
});

test('enables only static production caching and excludes server endpoints', () => {
  const angular = readJson('angular.json');
  const build = angular.projects['reach-commander-ui'].architect.build;
  assert.equal(build.configurations.production.serviceWorker, 'ngsw-config.json');
  assert.equal(build.configurations.development.serviceWorker, false);

  const config = readJson('ngsw-config.json');
  assert.equal(config.index, '/index.html');
  assert.equal(Object.hasOwn(config, 'dataGroups'), false);
  const apiExclusion = config.navigationUrls.find((url) => url === '!/api/**');
  assert.ok(apiExclusion);
  const excludedPrefix = apiExclusion.slice(1, -2);
  assert.ok('/api/auth/session'.startsWith(excludedPrefix));
  assert.ok('/api/auth/antiforgery'.startsWith(excludedPrefix));
  assert.ok(config.navigationUrls.includes('!/health'));
});
