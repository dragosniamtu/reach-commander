import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const repositoryRoot = new URL('../../', import.meta.url);

async function readRequired(path) {
  try {
    return await readFile(new URL(path, repositoryRoot), 'utf8');
  } catch (error) {
    assert.fail(`${path} must exist and be readable: ${error.message}`);
  }
}

test('Ubuntu guide uses a version-pinned, checksum-verified installation flow', async () => {
  const guide = await readRequired('docs/deployment/ubuntu.md');
  for (const required of [
    'Docker Engine',
    'Docker Compose v2',
    'REACHCOMMANDER_VERSION=v1.0.0',
    'reachcommander-installer.tar.gz',
    'SHA256SUMS',
    'sha256sum --check SHA256SUMS',
    'sudo ./install.sh',
    '/opt/reachcommander',
    '/usr/local/bin/reachcommander',
    '/var/backups/reachcommander',
  ]) {
    assert.ok(guide.includes(required), `Ubuntu guide is missing: ${required}`);
  }
  assert.match(guide, /inspect.*install\.sh/is);
});

test('Ubuntu guide documents the security and lifecycle contracts', async () => {
  const guide = await readRequired('docs/deployment/ubuntu.md');
  for (const required of [
    'no built-in authentication',
    'authenticated HTTPS reverse proxy',
    '127.0.0.1',
    'read-only',
    'read-write',
    'stable',
    'edge',
    'digest',
    'reachcommander update',
    'reachcommander doctor',
    'reachcommander uninstall',
    'rollback',
    'secure context',
    'same origin',
    'public',
  ]) {
    assert.ok(guide.toLowerCase().includes(required.toLowerCase()), `Ubuntu guide is missing: ${required}`);
  }
  assert.match(guide, /never recursively (?:change|run).*ch(?:mod|own)/i);
  assert.match(guide, /GHCR[\s\S]*package[\s\S]*public/i);
});

test('reverse-proxy examples enforce HTTPS authentication and large-upload settings', async () => {
  const [nginx, caddy, traefik] = await Promise.all([
    readRequired('docs/deployment/nginx.conf'),
    readRequired('docs/deployment/Caddyfile'),
    readRequired('docs/deployment/traefik.dynamic.yaml'),
  ]);

  for (const directive of [
    'listen 443 ssl',
    'auth_basic',
    'client_max_body_size 50G',
    'proxy_request_buffering off',
    'proxy_read_timeout 6h',
    'proxy_send_timeout 6h',
    'proxy_pass http://127.0.0.1:8092',
    'X-Forwarded-Proto',
  ]) {
    assert.ok(nginx.includes(directive), `Nginx example is missing: ${directive}`);
  }

  for (const directive of ['basic_auth', 'request_body', 'max_size 50GB', 'reverse_proxy 127.0.0.1:8092']) {
    assert.ok(caddy.includes(directive), `Caddy example is missing: ${directive}`);
  }

  for (const directive of [
    'entryPoints:',
    'websecure',
    'tls:',
    'basicAuth:',
    'buffering:',
    'maxRequestBodyBytes: 53687091200',
    'url: http://127.0.0.1:8092',
  ]) {
    assert.ok(traefik.includes(directive), `Traefik example is missing: ${directive}`);
  }
});

test('README points operators to the Ubuntu guide without replacing development setup', async () => {
  const readme = await readRequired('README.md');
  assert.match(readme, /Install on Ubuntu/i);
  assert.match(readme, /docs\/deployment\/ubuntu\.md/);
  assert.match(readme, /Local development/);
  assert.match(readme, /no built-in authentication/i);
});

test('published operator material never pipes downloaded code into a shell', async () => {
  const paths = [
    'README.md',
    'docs/deployment/ubuntu.md',
    'docs/deployment/nginx.conf',
    'docs/deployment/Caddyfile',
    'docs/deployment/traefik.dynamic.yaml',
    'deploy/README.md',
  ];
  const content = (await Promise.all(paths.map(readRequired))).join('\n');
  assert.doesNotMatch(content, /(?:curl|wget)[^\r\n|]*\|[^\r\n]*(?:sh|bash)/i);
});
