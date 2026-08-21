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
    'shellcheck -x --source-path=SCRIPTDIR \\',
  ]) {
    assert.ok(content.includes(command), `missing acceptance command: ${command}`);
  }
  assert.match(
    content,
    /shellcheck -x --source-path=SCRIPTDIR[\s\S]*?deploy\/package-installer\.sh[\s\S]*?tests\/installer\/test_common\.sh[\s\S]*?tests\/installer\/test_install\.sh[\s\S]*?tests\/installer\/test_command\.sh[\s\S]*?tests\/installer\/test_package\.sh/,
  );
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

test('publication verifies a unique candidate before globally serialized promotion', async () => {
  const content = await workflow();
  assert.match(
    content,
    /concurrency:\s*\n\s+group: ci-\$\{\{ github\.workflow \}\}-\$\{\{ github\.ref \}\}\s*\n\s+cancel-in-progress: \$\{\{ !startsWith\(github\.ref, 'refs\/tags\/v'\) \}\}/,
  );
  const publishStart = content.indexOf('  container-publish:');
  assert.notEqual(publishStart, -1);
  const publish = content.slice(publishStart);
  assert.match(publish, /concurrency:\s*\n\s+group: reachcommander-container-publication\s*\n\s+cancel-in-progress: false/);
  assert.match(publish, /candidate-\$\{GITHUB_RUN_ID\}-\$\{GITHUB_RUN_ATTEMPT\}/);
  assert.match(publish, /tags: \$\{\{ steps\.candidate\.outputs\.tag \}\}/);
  assert.doesNotMatch(publish, /tags: \$\{\{ steps\.release\.outputs\.tags \}\}/);
  assert.match(publish, /resolve-promotion-tags/);
  assert.match(publish, /docker buildx imagetools create/);
  assert.match(publish, /id: publication_source/);
  assert.match(publish, /org\.opencontainers\.image\.revision/);
  assert.match(publish, /GITHUB_SHA/);
  assert.match(publish, /existing_digest/);
  assert.match(publish, /steps\.publication_source\.outputs\.digest/);
  assert.match(publish, /id: promotion_tags/);
  assert.match(publish, /REUSED_IMMUTABLE/);
  assert.match(
    publish,
    /Resolve retry-safe promotion tags[\s\S]*?git fetch --no-tags origin master[\s\S]*?git fetch --tags origin[\s\S]*?resolve-promotion-tags/,
  );
  assert.match(publish, /steps\.promotion_tags\.outputs\.tags/);
  assert.doesNotMatch(publish, /already exists; immutable version tags cannot be replaced/);

  const buildPosition = publish.indexOf('uses: docker/build-push-action@v6');
  const sourcePosition = publish.indexOf('Select retry-safe publication source');
  const promotionTagsPosition = publish.indexOf('Resolve retry-safe promotion tags');
  const verifyPosition = publish.indexOf('Verify publication source platforms and attestations');
  const promotePosition = publish.indexOf('Promote verified image tags');
  assert.ok(
    buildPosition >= 0 &&
      sourcePosition > buildPosition &&
      promotionTagsPosition > sourcePosition &&
      verifyPosition > promotionTagsPosition &&
      promotePosition > verifyPosition,
  );
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

test('container smoke uses the real rendered non-root configuration', async () => {
  const content = await workflow();
  const smokeStart = content.indexOf('  container-smoke:');
  const publishStart = content.indexOf('  container-publish:');
  assert.notEqual(smokeStart, -1);
  assert.notEqual(publishStart, -1);
  const smoke = content.slice(smokeStart, publishStart);
  assert.match(smoke, /render_config\.py[\s\S]*create-request/);
  assert.match(smoke, /render_config\.py[\s\S]*add-source/);
  assert.match(smoke, /render_config\.py[\s\S]*render/);
  assert.match(smoke, /--user 1000:1000/);
  assert.doesNotMatch(smoke, /cat >[^\n]*sources\.json/);
  assert.doesNotMatch(smoke, /chmod 0644[^\n]*sources\.json/);
});
