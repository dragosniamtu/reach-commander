import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const repositoryRoot = new URL('../../', import.meta.url);
const moduleUrl = new URL('../../deploy/release-tags.mjs', import.meta.url);
const image = 'ghcr.io/dragosniamtu/reach-commander';

async function loadPolicy() {
  try {
    return await import(moduleUrl.href + `?test=${Date.now()}`);
  } catch (error) {
    assert.fail(`deploy/release-tags.mjs must exist and load: ${error.message}`);
  }
}

test('master publishes edge only', async () => {
  const { tagsForRef } = await loadPolicy();
  assert.deepEqual(tagsForRef('refs/heads/master', image), [`${image}:edge`]);
});

test('stable semantic tag publishes deterministic promotion tags', async () => {
  const { tagsForRef } = await loadPolicy();
  assert.deepEqual(tagsForRef('refs/tags/v12.34.56', image), [
    `${image}:v12.34.56`,
    `${image}:v12.34`,
    `${image}:v12`,
    `${image}:stable`,
  ]);
});

test('prerelease publishes only its complete immutable version tag', async () => {
  const { tagsForRef } = await loadPolicy();
  assert.deepEqual(tagsForRef('refs/tags/v1.3.0-beta.1', image), [
    `${image}:v1.3.0-beta.1`,
  ]);
});

test('non-master branches and pull refs publish nothing', async () => {
  const { tagsForRef } = await loadPolicy();
  assert.deepEqual(tagsForRef('refs/heads/feature', image), []);
  assert.deepEqual(tagsForRef('refs/pull/12/merge', image), []);
});

test('malformed version refs are rejected', async () => {
  const { tagsForRef } = await loadPolicy();
  const malformed = [
    'refs/tags/v01.2.3',
    'refs/tags/v1.02.3',
    'refs/tags/v1.2.03',
    'refs/tags/v1.2',
    'refs/tags/v1.2.3-01',
    'refs/tags/v1.2.3-beta..1',
    'refs/tags/v1.2.3+build',
    'refs/tags/v1.2.3;echo',
    'refs/tags/v1.2.3\nnext',
  ];
  for (const ref of malformed) {
    assert.throws(() => tagsForRef(ref, image), /version tag/i, ref);
  }
});

test('image name cannot inject GitHub output lines', async () => {
  const { tagsForRef } = await loadPolicy();
  assert.throws(
    () => tagsForRef('refs/heads/master', `${image}\npackages=write`),
    /image/i,
  );
});

test('CLI writes escaped GitHub outputs for a stable release', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'reachcommander-tags-'));
  try {
    const output = join(directory, 'github-output');
    const result = spawnSync(
      process.execPath,
      [fileURLToPath(moduleUrl), 'refs/tags/v2.4.6', image, output],
      { cwd: repositoryRoot, encoding: 'utf8' },
    );
    assert.equal(result.status, 0, result.stderr);
    const content = await readFile(output, 'utf8');
    assert.match(content, /tags<<REACHCOMMANDER_TAGS\n/);
    assert.match(content, new RegExp(`${image}:v2\\.4\\.6`));
    assert.match(content, /stableRelease=true\n/);
    assert.match(content, /version=v2\.4\.6\n/);
    assert.doesNotMatch(content, /undefined|null/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
