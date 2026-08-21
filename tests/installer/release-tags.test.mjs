import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
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

test('only the newest stable repository tag may promote moving stable tags', async () => {
  const { assertStablePromotion, promotionTagsForRef } = await loadPolicy();
  assert.doesNotThrow(() =>
    assertStablePromotion('v12.4.0', [
      'v1.99.0',
      'v12.3.9',
      'v12.4.0-beta.1',
      'v12.4.0',
      'documentation-tag',
    ]),
  );
  assert.throws(
    () => assertStablePromotion('v12.3.9', ['v12.3.9', 'v12.4.0']),
    /newest stable/i,
  );
  assert.throws(
    () => assertStablePromotion('v12.4.0-beta.1', ['v12.4.0-beta.1']),
    /stable version/i,
  );
  assert.throws(
    () => assertStablePromotion('v12x4x0', ['v12x4x0']),
    /stable version/i,
  );
  assert.deepEqual(
    promotionTagsForRef(
      'refs/tags/v12.3.9',
      image,
      ['v12.3.9', 'v12.4.0'],
      true,
    ),
    [`${image}:v12.3.9`],
  );
  assert.deepEqual(
    promotionTagsForRef(
      'refs/tags/v12.4.0',
      image,
      ['v12.3.9', 'v12.4.0'],
      false,
    ),
    [
      `${image}:v12.4.0`,
      `${image}:v12.4`,
      `${image}:v12`,
      `${image}:stable`,
    ],
  );
  assert.deepEqual(
    promotionTagsForRef('refs/heads/master', image, [], false),
    [`${image}:edge`],
  );
  assert.throws(
    () =>
      promotionTagsForRef(
        'refs/tags/v12.3.9',
        image,
        ['v12.3.9', 'v12.4.0'],
        false,
      ),
    /newest stable/i,
  );
});

test('stable-promotion CLI validates tags read from a file', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'reachcommander-promotion-'));
  try {
    const tags = join(directory, 'tags.txt');
    await writeFile(tags, 'v2.0.0\nv2.1.0\nv2.1.0-beta.1\n', 'utf8');
    const accepted = spawnSync(
      process.execPath,
      [fileURLToPath(moduleUrl), 'assert-stable-promotion', 'v2.1.0', tags],
      { cwd: repositoryRoot, encoding: 'utf8' },
    );
    assert.equal(accepted.status, 0, accepted.stderr);
    const rejected = spawnSync(
      process.execPath,
      [fileURLToPath(moduleUrl), 'assert-stable-promotion', 'v2.0.0', tags],
      { cwd: repositoryRoot, encoding: 'utf8' },
    );
    assert.notEqual(rejected.status, 0);

    const promotionOutput = join(directory, 'promotion-output.txt');
    const resumed = spawnSync(
      process.execPath,
      [
        fileURLToPath(moduleUrl),
        'resolve-promotion-tags',
        'refs/tags/v2.0.0',
        image,
        tags,
        'true',
        promotionOutput,
      ],
      { cwd: repositoryRoot, encoding: 'utf8' },
    );
    assert.equal(resumed.status, 0, resumed.stderr);
    const promotionContent = await readFile(promotionOutput, 'utf8');
    assert.match(promotionContent, new RegExp(`${image}:v2\\.0\\.0`));
    assert.doesNotMatch(promotionContent, /:stable/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
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
