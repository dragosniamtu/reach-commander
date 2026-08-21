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
for (const group of ngsw.assetGroups ?? []) {
  const urls = [...(group.urls ?? []), ...(group.patterns ?? [])];
  assert.equal(
    urls.some((url) => url.includes('/api/') || url.includes('api\\/')),
    false,
    `API responses must not appear in the ${group.name} asset group.`,
  );
}
const navigationRules = ngsw.navigationUrls ?? [];
assert.ok(navigationRules.some((entry) => !entry.positive && entry.regex.includes('api')));
const apiRule = navigationRules.find((entry) => !entry.positive && entry.regex.includes('api'));
assert.ok(apiRule, 'The generated worker must contain a negative API navigation rule.');
assert.match('/api/auth/session', new RegExp(apiRule.regex));
assert.match('/api/auth/antiforgery', new RegExp(apiRule.regex));
assert.ok(navigationRules.some((entry) => !entry.positive && entry.regex.includes('health')));
console.log('ReachCommander PWA build verified.');
