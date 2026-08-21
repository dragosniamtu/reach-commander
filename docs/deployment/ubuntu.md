# Install ReachCommander on Ubuntu

This is the production installation path for an Ubuntu host. It installs a small, root-owned deployment under `/opt/reachcommander`, exposes the `reachcommander` management command at `/usr/local/bin/reachcommander`, and stores uninstall backups outside the installation at `/var/backups/reachcommander`.

A root-owned coordination lock remains at `/opt/.reachcommander.lock`. It lives outside the replaceable deployment tree so install, update, and uninstall operations cannot accidentally switch lock inodes while another command is active.

> **Security boundary:** ReachCommander has no built-in authentication, authorization, or TLS. Keep its published port on `127.0.0.1` and put an authenticated HTTPS reverse proxy in front of it. Do not expose the application port directly to the internet.

## Prerequisites

Install and start [Docker Engine for Ubuntu](https://docs.docker.com/engine/install/ubuntu/) and the [Docker Compose v2 plugin](https://docs.docker.com/compose/install/linux/). The ReachCommander installer verifies these prerequisites but deliberately does not install or reconfigure Docker for you.

You also need `curl`, `python3`, `tar`, and `sha256sum`. On a current Ubuntu release:

```bash
sudo apt-get update
sudo apt-get install --yes curl python3 tar coreutils util-linux
docker compose version
sudo docker info
```

The release workflow publishes images to GitHub Container Registry (GHCR). After the first image is published, a repository owner must perform the one-time package setting described in [Package access control and visibility](https://docs.github.com/en/packages/learn-github-packages/configuring-a-packages-access-control-and-visibility): open the ReachCommander container package settings and change its visibility to **Public**. Installation cannot pull an anonymously inaccessible package.

## Recommended: install a pinned release

Choose a stable release from the repository's Releases page and set that exact version. The checksum file authenticates the completed download against the assets attached to that release. It does not replace HTTPS or GitHub account security.

```bash
(
set -Eeuo pipefail
INSTALL_WORKDIR="$(mktemp -d /tmp/reachcommander-install.XXXXXX)"
chmod 0700 "$INSTALL_WORKDIR"
trap 'rm -rf -- "$INSTALL_WORKDIR"' EXIT
cd "$INSTALL_WORKDIR"

REACHCOMMANDER_VERSION=v1.0.0
REACHCOMMANDER_RELEASE_URL="https://github.com/dragosniamtu/reach-commander/releases/download/${REACHCOMMANDER_VERSION}"

curl --fail --location --output reachcommander-installer.tar.gz \
  "${REACHCOMMANDER_RELEASE_URL}/reachcommander-installer.tar.gz"
curl --fail --location --output SHA256SUMS \
  "${REACHCOMMANDER_RELEASE_URL}/SHA256SUMS"

[[ "$(wc -l <SHA256SUMS)" -eq 1 ]]
grep --extended-regexp --quiet \
  '^[0-9a-f]{64}  reachcommander-installer\.tar\.gz$' SHA256SUMS
sha256sum --check --strict SHA256SUMS
tar -xzf reachcommander-installer.tar.gz
cd reachcommander-installer
less install.sh
sudo ./install.sh
)
```

Inspect `install.sh` before granting it root access. Never pipe a downloaded installer directly into a privileged shell.

### Convenience: newest stable release

If reproducibility is less important than following the newest stable release, GitHub's `latest` URL still provides a separately downloaded checksum:

```bash
(
set -Eeuo pipefail
INSTALL_WORKDIR="$(mktemp -d /tmp/reachcommander-install.XXXXXX)"
chmod 0700 "$INSTALL_WORKDIR"
trap 'rm -rf -- "$INSTALL_WORKDIR"' EXIT
cd "$INSTALL_WORKDIR"

REACHCOMMANDER_RELEASE_URL="https://github.com/dragosniamtu/reach-commander/releases/latest/download"

curl --fail --location --output reachcommander-installer.tar.gz \
  "${REACHCOMMANDER_RELEASE_URL}/reachcommander-installer.tar.gz"
curl --fail --location --output SHA256SUMS \
  "${REACHCOMMANDER_RELEASE_URL}/SHA256SUMS"

[[ "$(wc -l <SHA256SUMS)" -eq 1 ]]
grep --extended-regexp --quiet \
  '^[0-9a-f]{64}  reachcommander-installer\.tar\.gz$' SHA256SUMS
sha256sum --check --strict SHA256SUMS
tar -xzf reachcommander-installer.tar.gz
cd reachcommander-installer
less install.sh
sudo ./install.sh
)
```

The version-pinned flow is recommended for a server because it is repeatable and auditable.

## Installer choices

The interactive installer asks for:

- the host bind address and port; accept `127.0.0.1:8092` when using a reverse proxy;
- a non-root numeric UID and GID for the container process;
- one or more existing host source directories, their labels and identifiers, and whether each is read-only or read-write;
- the default source for each pane;
- an exact acknowledgement before enabling any read-write source.

The first installation always resolves `stable` and persists its exact image digest. After installation, `reachcommander update` can follow `stable`, switch to `edge`, or select an exact `vX.Y.Z` release.

Start with read-only sources. Enable read-write only for a narrow directory that genuinely needs upload, rename, or extraction. The installer maps each host directory to an isolated container path; `config/sources.json` contains container paths, while the canonical host paths are retained only in root-owned `state/source-mounts.json` for validation and safe uninstall backup.

The configured UID/GID must already be able to access each source. Never recursively change source ownership with `chown` or permissions with `chmod` just to make the container work. Inspect the path one component at a time with `namei -l /your/source`, then grant only the minimum required access to the exact directory using deliberate ownership, group membership, or an ACL. ReachCommander never creates, mounts, changes ownership of, or deletes source data.

On success the installer creates:

```text
/opt/reachcommander/
  .env
  compose.yaml
  config/sources.json
  lib/common.sh
  state/channel
  state/current-image
  state/source-mounts.json
/usr/local/bin/reachcommander
```

Generated configuration is root-owned. The runtime image is always saved as an immutable `ghcr.io/dragosniamtu/reach-commander@sha256:...` digest, even when `stable` or `edge` was selected for discovery.

Reconfiguration is journaled before any active file changes. If a signal, process crash, or host interruption leaves `state/install-transaction`, `reachcommander doctor` reports it. Rerun the same installer bundle to recover the verified previous configuration and healthy image before starting a new reconfiguration; do not delete the marker or `.reconfigure-transaction` backup manually.

## Put HTTPS and authentication in front

Use one of the checked-in examples as a starting point:

- [Nginx](nginx.conf) — host-native Nginx, Basic Authentication, TLS, request streaming, and six-hour transfer timeouts;
- [Caddy](Caddyfile) — automatic HTTPS, a password hash supplied from the environment, and a 50 GB request limit (the `request_body` limit requires Caddy 2.10.0 or newer);
- Traefik [static](traefik.static.yaml) and [dynamic](traefik.dynamic.yaml) files — six-hour client-facing timeouts, an ACME-backed HTTPS router, external password file, and an explicit 50 GiB buffering limit.

Replace every example hostname, certificate path, and credential reference. Basic Authentication is suitable only over HTTPS; an identity-aware or SSO proxy may replace it. Keep the upstream at `http://127.0.0.1:8092` when the proxy runs natively on the Ubuntu host.

Large files deserve special attention. ReachCommander streams requests, but every proxy can impose a smaller limit or timeout. The examples allow up to 50 GiB and use long timeouts. Nginx disables request buffering. Traefik's optional size-enforcement middleware buffers the body and may spill it to disk, so its temporary storage must have at least the upload capacity you intend to allow; omit that middleware and enforce the limit at an earlier trusted layer if streaming is more important.

The Traefik example assumes Traefik runs on the host. Install both example files, replace the ACME email and hostname, create `/var/lib/traefik/acme.json` with mode `0600`, and create `/etc/traefik/reachcommander.htpasswd` outside the repository. The `websecure` entry point extends client read, write, and idle timeouts to six hours; the dynamic router selects the `letsencrypt` certificate resolver. If you use a trusted certificate store instead, replace that resolver deliberately rather than accepting Traefik's generated default certificate.

Inside a normal container, `127.0.0.1` is the Traefik container—not the Ubuntu host—and it cannot reach a host port bound only to loopback. Either run Traefik with host networking or deliberately bind ReachCommander to a private host address, protect that address with the host firewall, and point Traefik at it. Never solve this by publishing ReachCommander on an unprotected public interface.

The installed PWA also needs this boundary. Production service workers require an HTTPS secure context, and ReachCommander's UI, service worker, manifest, and `/api` requests must remain on the same origin. Proxy the complete site at one hostname without moving `/api` to another origin.

After configuring the proxy:

```bash
curl --fail http://127.0.0.1:8092/health
curl --fail https://reachcommander.example.com/health
```

The first check is local; the second must traverse your real TLS and authentication policy. Supply credentials as appropriate for the proxy you chose.

## Operate the installation

All mutating management commands require root:

```bash
sudo reachcommander status
sudo reachcommander doctor
sudo reachcommander logs
sudo reachcommander logs --follow
sudo reachcommander start
sudo reachcommander stop
sudo reachcommander restart
```

Run `sudo reachcommander doctor` after changing host mounts, permissions, the proxy bind address, or Docker. It validates the local deployment files, Compose model, source metadata, image state, port, and container health without changing the deployment.

### Updates, channels, and rollback

`stable` follows the newest stable semantic release. `edge` follows successful builds from `master`. An exact `vX.Y.Z` selects only that release. There is deliberately no floating `latest` container tag.

```bash
# Re-resolve the saved channel.
sudo reachcommander update

# Change the discovery channel.
sudo reachcommander update stable
sudo reachcommander update edge
sudo reachcommander update v1.2.3
```

An update resolves the requested channel to an exact digest, backs up the deployment state, starts that digest, and waits for the health check. If the candidate is unhealthy, ReachCommander automatically attempts rollback to the previously healthy digest. Exit status `2` means the update failed but rollback is healthy; exit status `3` means rollback also failed and manual recovery is required. Do not delete `/opt/reachcommander/backups` or `state/update-transaction` while diagnosing an interrupted update.

Run `sudo reachcommander status` to see the saved channel and exact running image. Use `sudo reachcommander doctor` and `sudo reachcommander logs` before retrying a failed update.

## Uninstall without touching source data

```bash
sudo reachcommander uninstall
```

The command requires the exact confirmation `uninstall ReachCommander`. It revalidates every recorded source path, writes and verifies a timestamped configuration backup beneath `/var/backups/reachcommander`, stops the Compose project without deleting volumes, and removes only the installer-owned allowlist. Source directories and their contents are never removed. If backup creation or Compose shutdown fails, uninstall stops and preserves the active deployment.

Keep the external backup until you have verified that you no longer need the generated source mapping or pinned image record.

## Troubleshooting

- **Package pull is denied:** confirm the GHCR package exists and its package visibility is Public; repository visibility alone may not be enough.
- **The service is healthy locally but unavailable remotely:** keep ReachCommander on loopback and check the reverse proxy listener, authentication, certificate, and firewall.
- **A source is unavailable:** verify the saved host path still exists, has not become a symlink, and is traversable by the configured UID/GID.
- **Writes are denied:** the source must be explicitly read-write in application policy, mounted read-write, and writable by the configured UID/GID. Do not weaken unrelated parent directories.
- **PWA installation is not offered:** use the authenticated HTTPS hostname, not a plain HTTP LAN address, and keep the application and API on the same origin.
- **An update was interrupted:** do not edit the transaction marker. Run `sudo reachcommander doctor`, inspect logs, and use the retained update backup for recovery.
