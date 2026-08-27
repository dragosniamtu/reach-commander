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
    'sha256sum --check --strict SHA256SUMS',
    'sudo ./install.sh',
    '/opt/reachcommander',
    '/usr/local/bin/reachcommander',
    '/var/backups/reachcommander',
    '/opt/.reachcommander.lock',
    'The first installation always resolves `stable`',
    'Rerun the same installer bundle to recover',
  ]) {
    assert.ok(guide.includes(required), `Ubuntu guide is missing: ${required}`);
  }
  assert.match(guide, /inspect.*install\.sh/is);
  assert.ok((guide.match(/set -Eeuo pipefail/g) ?? []).length >= 2);
  assert.ok((guide.match(/mktemp -d/g) ?? []).length >= 2);
  assert.ok((guide.match(/chmod 0700/g) ?? []).length >= 2);
  assert.ok((guide.match(/sha256sum --check --strict SHA256SUMS/g) ?? []).length >= 2);
  assert.ok(guide.split('reachcommander-installer\\.tar\\.gz').length - 1 >= 2);
  assert.doesNotMatch(guide, /mkdir -p \/tmp\/reachcommander-install/);
});

test('Ubuntu guide documents the security and lifecycle contracts', async () => {
  const guide = await readRequired('docs/deployment/ubuntu.md');
  for (const required of [
    'built-in single-administrator authentication',
    'first-run setup code',
    '/opt/reachcommander/data/auth/account.json',
    '/opt/reachcommander/data/keys',
    '12-hour sliding session',
    'HTTPS reverse proxy',
    'optional proxy authentication',
    'password change',
    'account reset',
    'retain',
    'verified backup',
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

test('reverse-proxy examples enforce HTTPS, label proxy auth optional, and retain large-upload settings', async () => {
  const [nginx, caddy, traefik, traefikStatic] = await Promise.all([
    readRequired('docs/deployment/nginx.conf'),
    readRequired('docs/deployment/Caddyfile'),
    readRequired('docs/deployment/traefik.dynamic.yaml'),
    readRequired('docs/deployment/traefik.static.yaml'),
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
  assert.match(nginx, /optional defense in depth/i);

  for (const directive of ['basic_auth', 'request_body', 'max_size 50GB', 'reverse_proxy 127.0.0.1:8092']) {
    assert.ok(caddy.includes(directive), `Caddy example is missing: ${directive}`);
  }
  assert.match(caddy, /optional defense in depth/i);

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
  assert.match(traefik, /optional defense in depth/i);
  assert.match(traefik, /tls:\s*\n\s+certResolver: letsencrypt/);
  for (const directive of [
    'websecure:',
    'address: ":443"',
    'readTimeout: 6h',
    'writeTimeout: 6h',
    'idleTimeout: 6h',
    'certificatesResolvers:',
    'letsencrypt:',
    'storage: /var/lib/traefik/acme.json',
  ]) {
    assert.ok(traefikStatic.includes(directive), `Traefik static example is missing: ${directive}`);
  }
});

test('README points operators to the Ubuntu guide without replacing development setup', async () => {
  const readme = await readRequired('README.md');
  assert.match(readme, /Install on Ubuntu/i);
  assert.match(readme, /docs\/deployment\/ubuntu\.md/);
  assert.match(readme, /Local development/);
  assert.match(readme, /built-in single-administrator authentication/i);
  assert.doesNotMatch(readme, new RegExp(['no built-in', 'authentication'].join(' '), 'i'));
});

test('macOS guide documents the one-command Docker Desktop boundary', async () => {
  const guide = await readRequired('docs/deployment/macos.md');
  for (const required of [
    'Docker Desktop',
    'Intel',
    'Apple Silicon',
    '/bin/bash -c "$(curl -fsSL',
    '~/Library/Application Support/ReachCommander',
    'Whole drives',
    'Specific folders',
    'Read-only',
    'Read/write',
    'This Mac only',
    'local network',
    '127.0.0.1:8080',
    'first-run setup code',
    'data/auth/account.json',
    'data/keys',
    'stable',
    'digest',
    'rollback',
    'Docker Desktop file sharing',
    'does not configure public internet access',
    'Linux container/VM',
    'not a native macOS application',
  ]) {
    assert.ok(
      guide.toLowerCase().includes(required.toLowerCase()),
      `macOS guide is missing: ${required}`,
    );
  }
  assert.match(guide, /installer-owned[\s\S]*masked/i);
  assert.doesNotMatch(guide, /(?:curl|wget)[^\r\n|]*\|[^\r\n]*(?:sh|bash)/i);
});

test('README and security policy advertise macOS without weakening boundaries', async () => {
  const [readme, policy] = await Promise.all([
    readRequired('README.md'),
    readRequired('SECURITY.md'),
  ]);
  assert.match(readme, /Install on macOS/i);
  assert.match(readme, /docs\/deployment\/macos\.md/);
  assert.match(readme, /Docker Desktop/i);
  assert.match(policy, /installer-owned[\s\S]*mask/i);
  assert.match(policy, /local network[\s\S]*authentication/i);
});

test('security policy describes account persistence and secure deployment', async () => {
  const policy = await readRequired('SECURITY.md');
  for (const required of [
    'built-in single-administrator authentication',
    '/opt/reachcommander/data/auth/account.json',
    '/opt/reachcommander/data/keys',
    'HTTPS reverse proxy',
    'optional proxy authentication',
    'account reset',
    'verified backup',
  ]) {
    assert.ok(policy.toLowerCase().includes(required.toLowerCase()), `Security policy is missing: ${required}`);
  }
});

test('operator docs define the explicit trusted LAN HTTP boundary', async () => {
  const paths = [
    'README.md',
    'docs/INSTALL.md',
    'docs/deployment/ubuntu.md',
    'deploy/README.md',
    'SECURITY.md',
  ];
  const content = (await Promise.all(paths.map(readRequired))).join('\n');
  for (const required of [
    'Direct HTTP on trusted LAN',
    'http://<server-lan-ip>:<port>',
    '8092',
    '8080',
    'all host interfaces',
    'DHCP',
    'Authentication__AllowInsecureHttp',
    'authentication remains enabled',
    'antiforgery remains enabled',
    'rate limiting remains enabled',
    'router forwarding',
    'PWA installation requires HTTPS',
  ]) {
    assert.ok(content.includes(required), `trusted LAN docs are missing: ${required}`);
  }
});

test('published operator material never pipes downloaded code into a shell', async () => {
  const paths = [
    'README.md',
    'docs/deployment/ubuntu.md',
    'docs/deployment/macos.md',
    'docs/deployment/nginx.conf',
    'docs/deployment/Caddyfile',
    'docs/deployment/traefik.dynamic.yaml',
    'docs/deployment/traefik.static.yaml',
    'deploy/README.md',
  ];
  const content = (await Promise.all(paths.map(readRequired))).join('\n');
  assert.doesNotMatch(content, /(?:curl|wget)[^\r\n|]*\|[^\r\n]*(?:sh|bash)/i);
});

test('operator docs define the Ubuntu installer-managed system update boundary', async () => {
  const paths = [
    'README.md',
    'docs/INSTALL.md',
    'docs/deployment/ubuntu.md',
    'docs/operations.md',
    'deploy/README.md',
  ];
  const content = (await Promise.all(paths.map(readRequired))).join('\n');
  for (const required of [
    'reachcommander-updater.service',
    'startup and every six hours',
    'administrator confirmation',
    'exact version pins remain pinned',
    '/run/reachcommander-updater',
    'never mounts',
    '/var/run/docker.sock',
    'Ubuntu installer-managed',
    'manual container',
    'macOS',
    'Windows',
    'sudo systemctl status reachcommander-updater.service',
    'sudo journalctl -u reachcommander-updater.service --since today',
    'sudo reachcommander status',
    'sudo reachcommander doctor',
  ]) {
    assert.ok(content.toLowerCase().includes(required.toLowerCase()), `update docs are missing: ${required}`);
  }
  assert.match(content, /existing installations[\s\S]*run[\s\S]*checksum-verified installer/i);
  assert.match(content, /updater helper[\s\S]*future installer/i);
});

test('operator docs explain bounded update traces and container-only restart behavior', async () => {
  const paths = [
    'README.md',
    'docs/deployment/ubuntu.md',
    'deploy/README.md',
  ];
  const content = (await Promise.all(paths.map(readRequired))).join('\n');
  for (const required of [
    'protocol-v3',
    'Technical details',
    'sudo reachcommander update-log',
    'sudo reachcommander update-log --follow',
    'sudo journalctl -u reachcommander-updater.service --since today',
    'old stuck update events cannot be reconstructed',
    'only the ReachCommander application container is recreated',
    'Docker Engine is never restarted',
  ]) {
    assert.ok(
      content.toLowerCase().includes(required.toLowerCase()),
      `update trace docs are missing: ${required}`,
    );
  }
  assert.match(content, /checksum-verified installer[\s\S]*upgrades[\s\S]*protocol-v3 helper/i);
});
