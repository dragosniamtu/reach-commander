import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const workflowUrl = new URL('../../.github/workflows/ci.yml', import.meta.url);
const dockerignoreUrl = new URL('../../.dockerignore', import.meta.url);

async function workflow() {
  return readFile(workflowUrl, 'utf8');
}

test('push triggers include master and semantic-looking version tags', async () => {
  const content = await workflow();
  assert.match(content, /push:\s*\n\s+branches:\s*\[master\]/);
  assert.match(content, /tags:\s*\['v\*'\]/);
  assert.match(content, /pull_request:\s*\n\s+branches:\s*\[master\]/);
});

test('installer verification runs inside acceptance before publication', async () => {
  const content = await workflow();
  for (const command of [
    'python3 -m unittest tests/installer/test_render_config.py -v',
    'bash tests/installer/test_common.sh',
    'bash tests/installer/test_install.sh',
    'bash tests/installer/test_command.sh',
    'bash tests/installer/test_package.sh',
    'node --test tests/installer/release-tags.test.mjs tests/installer/workflow-contract.test.mjs',
    'shellcheck deploy/install.sh deploy/reachcommander deploy/lib/common.sh deploy/package-installer.sh',
  ]) {
    assert.ok(content.includes(command), `missing acceptance command: ${command}`);
  }
  assert.match(content, /sudo apt-get install[^\n]*shellcheck/);
});

test('smoke and publication jobs depend on all required gates', async () => {
  const content = await workflow();
  assert.match(content, /container-smoke:[\s\S]*?needs:\s*\[backend, acceptance\]/);
  assert.match(
    content,
    /container-publish:[\s\S]*?needs:\s*\[backend, acceptance, container-smoke\]/,
  );
  assert.match(content, /container-smoke:[\s\S]*?if:\s*github\.event_name == 'push'/);
  assert.match(content, /container-publish:[\s\S]*?if:\s*github\.event_name == 'push'/);
});

test('write permissions are scoped only to the publication job', async () => {
  const content = await workflow();
  const publishStart = content.indexOf('  container-publish:');
  assert.notEqual(publishStart, -1);
  const beforePublish = content.slice(0, publishStart);
  const publish = content.slice(publishStart);
  for (const permission of [
    'contents: write',
    'packages: write',
    'attestations: write',
    'id-token: write',
  ]) {
    assert.doesNotMatch(beforePublish, new RegExp(permission));
    assert.match(publish, new RegExp(permission));
  }
  assert.match(content, /^permissions:\s*\n\s+contents: read/m);
});

test('multi-platform publication enables SBOM and maximum provenance', async () => {
  const content = await workflow();
  assert.match(content, /docker\/setup-qemu-action@v3/);
  assert.match(content, /docker\/setup-buildx-action@v3/);
  assert.match(content, /docker\/login-action@v3/);
  assert.match(content, /docker\/build-push-action@v6/);
  assert.match(content, /platforms:\s*linux\/amd64,linux\/arm64/);
  assert.match(content, /sbom:\s*true/);
  assert.match(content, /provenance:\s*mode=max/);
  assert.match(content, /cache-from:\s*type=gha/);
  assert.match(content, /cache-to:\s*type=gha,mode=max/);
});

test('tag builds must belong to master and stable assets stay stable-only', async () => {
  const content = await workflow();
  assert.match(content, /git fetch[^\n]*origin master/);
  assert.match(content, /git merge-base --is-ancestor "\$GITHUB_SHA" origin\/master/);
  assert.match(content, /steps\.release\.outputs\.stableRelease == 'true'/);
  assert.match(content, /gh release upload[^\n]*--clobber/);
  assert.match(content, /reachcommander-installer\.tar\.gz/);
  assert.match(content, /SHA256SUMS/);
});

test('manifest verification requires runnable platforms and attestations', async () => {
  const content = await workflow();
  assert.match(content, /docker buildx imagetools inspect/);
  assert.match(content, /linux\/amd64/);
  assert.match(content, /linux\/arm64/);
  assert.match(content, /attestation-manifest/);
  assert.match(content, /unknown/);
});

test('runtime image context excludes release-only and test content', async () => {
  const content = await readFile(dockerignoreUrl, 'utf8');
  for (const entry of ['deploy', 'docs', 'tests', '.github']) {
    assert.match(content, new RegExp(`^${entry}$`, 'm'));
  }
  assert.doesNotMatch(content, /^src$/m);
  assert.doesNotMatch(content, /^client$/m);
  assert.doesNotMatch(content, /^global\.json$/m);
});
