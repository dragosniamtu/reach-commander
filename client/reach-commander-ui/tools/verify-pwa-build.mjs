import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const output = join(root, 'dist', 'reach-commander-ui', 'browser');
const required = [
  'ngsw-worker.js',
  'ngsw.json',
  'manifest.webmanifest',
  'icons/icon-192.png',
  'icons/icon-512.png',
  'icons/icon-maskable-192.png',
  'icons/icon-maskable-512.png',
  'icons/apple-touch-icon.png',
  'icons/favicon-32.png',
];

for (const path of required) {
  assert.ok(existsSync(join(output, path)), `Missing production PWA asset: ${path}`);
}

const ngsw = JSON.parse(readFileSync(join(output, 'ngsw.json'), 'utf8'));
assert.equal((ngsw.dataGroups ?? []).length, 0, 'API data groups must stay empty.');
const navigationRules = ngsw.navigationUrls ?? [];
assert.ok(navigationRules.some((entry) => !entry.positive && entry.regex.includes('api')));
assert.ok(navigationRules.some((entry) => !entry.positive && entry.regex.includes('health')));
console.log('ReachCommander PWA build verified.');
