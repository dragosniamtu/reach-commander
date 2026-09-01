# Install ReachCommander on Ubuntu

This is the production installation path for an Ubuntu host. It installs a small, root-owned deployment under `/opt/reachcommander`, exposes the `reachcommander` management command at `/usr/local/bin/reachcommander`, and stores uninstall backups outside the installation at `/var/backups/reachcommander`.

A root-owned coordination lock remains at `/opt/.reachcommander.lock`. It lives outside the replaceable deployment tree so install, update, and uninstall operations cannot accidentally switch lock inodes while another command is active.

> **Security boundary:** ReachCommander provides built-in single-administrator authentication, but it does not terminate TLS. The recommended default publishes only to `127.0.0.1` for an HTTPS reverse proxy. **Direct HTTP on trusted LAN** is an explicit unencrypted exception for a private network you control; never expose either application endpoint directly to the internet.

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

- one of two network modes: **Secure HTTPS reverse proxy (recommended)** on `127.0.0.1`, or **Direct HTTP on trusted LAN** on all host interfaces;
- the host port, which defaults to `8092` in either mode;
- a non-root numeric UID and GID for the container process;
- one or more existing host source directories, their labels and identifiers, and whether each is read-only or read-write;
- the default source for each pane;
- the exact mode-specific acknowledgement: `I have HTTPS` or `I understand LAN HTTP is unencrypted`.

The first installation always resolves `stable` and persists its exact image digest. After installation, `reachcommander update` can follow `stable`, switch to `edge`, or select an exact `vX.Y.Z` release.

Start with read-only sources. Enable read-write only for a narrow directory that genuinely needs Copy/Move destinations, MkDir, managed Trash/Restore, upload, rename, or extraction. The installer maps each host directory to an isolated container path; `config/sources.json` contains container paths, while the canonical host paths are retained only in root-owned `state/source-mounts.json` for validation and safe uninstall backup.

The configured UID/GID must already be able to access each source. Never recursively change source ownership with `chown` or permissions with `chmod` just to make the container work. Inspect the path one component at a time with `namei -l /your/source`, then grant only the minimum required access to the exact directory using deliberate ownership, group membership, or an ACL. The installer never creates, mounts, changes ownership of, or deletes source data. The authenticated application can mutate sources explicitly configured read-write through its reviewed file operations.

Read the [file operations and managed Trash runbook](../operations.md) before enabling writes. `/opt/reachcommander/data` holds durable queue metadata, while recoverable deleted payloads live in source-local `.reachcommander-trash`. Back up both separately when Trash recovery matters.

### Direct HTTP on trusted LAN

Choose **Direct HTTP on trusted LAN** only for a network you control. Docker publishes host port `8092` on all host interfaces and forwards it to ReachCommander's container port `8080`. Open `http://<server-lan-ip>:<port>` from another device; the default is `http://<server-lan-ip>:8092`.

This Radarr-style wildcard publication does not save one DHCP address, so a later DHCP change does not require ReachCommander reconfiguration. The installer detects RFC1918 addresses only to print convenient URLs. It does not configure Ubuntu firewall rules, router forwarding, DNS, certificates, or a private-interface boundary.

`Authentication__AllowInsecureHttp=true` changes cookie transport only. Administrator authentication remains enabled, authorization remains enabled, antiforgery remains enabled, and rate limiting remains enabled in Production. HTTP does not encrypt credentials, cookies, filenames, or file contents. Never forward this port from a router or expose it through a public host interface; use the default HTTPS reverse-proxy mode or a trusted VPN instead.

Ordinary browser access works over LAN HTTP. PWA installation requires HTTPS because production service workers require a secure context. Rerun the checksum-verified installer to change access mode or port; reconfiguration preserves the administrator account, Data Protection keys, sources, durable operations, and application state.

On success the installer creates:

```text
/opt/reachcommander/
  .env
  compose.yaml
  config/sources.json
  data/
    auth/
    keys/
  lib/common.sh
  state/channel
  state/current-image
  state/source-mounts.json
/usr/local/bin/reachcommander
```

Generated configuration is root-owned. The mode-`0700` `data` tree is owned by the selected runtime UID/GID and mounted read-write at `/data`; every configured source retains its independent read-only/read-write policy. The runtime image is always saved as an immutable `ghcr.io/dragosniamtu/reach-commander@sha256:...` digest, even when `stable` or `edge` was selected for discovery.

Reconfiguration is journaled before any active file changes. If a signal, process crash, or host interruption leaves `state/install-transaction`, `reachcommander doctor` reports it. Rerun the same installer bundle to recover the verified previous configuration and healthy image before starting a new reconfiguration; do not delete the marker or `.reconfigure-transaction` backup manually.

## Create and operate the administrator account

ReachCommander protects the Angular UI and every file API with its built-in single-administrator authentication. Use this first-run sequence:

1. Complete installation in the recommended loopback mode and configure the HTTPS reverse proxy, or explicitly select trusted-LAN HTTP and accept its unencrypted-transport warning.
2. Read the active random first-run setup code with `sudo reachcommander logs`. A restart invalidates the previous code and emits a new one while setup is incomplete.
3. Open the HTTPS URL, or the printed trusted-LAN HTTP URL when that mode was explicitly selected, and enter the setup code, administrator username, and password. The setup code is consumed when the account is created.
4. On later visits, use the login screen. The non-persistent HttpOnly cookie has a 12-hour sliding session lifetime and no Remember Me option.
5. Use the account menu for password change or logout. Changing the password invalidates every older session.

The password is not stored inside the Docker image, Compose file, `.env`, or browser storage. `/opt/reachcommander/data/auth/account.json` contains only the versioned salted password hash and account security metadata. `/opt/reachcommander/data/keys` contains the Data Protection key ring that encrypts session cookies. Both remain in the dedicated host data mount across container replacement and installer reconfiguration.

Treat both paths and their backups as credentials. For a routine account-state backup, stop the service and archive the account record and complete key ring together:

```bash
(
set -Eeuo pipefail
AUTH_BACKUP_DIR="/root/reachcommander-auth-$(date -u +%Y%m%dT%H%M%SZ)"
sudo install -d -m 0700 "$AUTH_BACKUP_DIR"
sudo reachcommander stop
sudo tar --create \
  --file "$AUTH_BACKUP_DIR/authentication-data.tar" \
  --directory /opt/reachcommander/data \
  auth/account.json keys
sudo chmod 0600 "$AUTH_BACKUP_DIR/authentication-data.tar"
sudo sha256sum "$AUTH_BACKUP_DIR/authentication-data.tar" | \
  sudo tee "$AUTH_BACKUP_DIR/SHA256SUMS" >/dev/null
sudo chmod 0600 "$AUTH_BACKUP_DIR/SHA256SUMS"
(
  cd "$AUTH_BACKUP_DIR"
  sudo sha256sum --check --strict SHA256SUMS
)
sudo reachcommander start
)
```

Deleting only the key files under `/opt/reachcommander/data/keys` signs out current sessions after restart but retains the account and password. Do not use key deletion as an account reset.

For an intentional emergency account reset, preserve the old record, remove only that record while the service is stopped, and then start first-run setup again:

```bash
(
set -Eeuo pipefail
sudo reachcommander stop
RESET_BACKUP_DIR="/root/reachcommander-account-reset-$(date -u +%Y%m%dT%H%M%SZ)"
sudo install -d -m 0700 "$RESET_BACKUP_DIR"
sudo install -m 0600 \
  /opt/reachcommander/data/auth/account.json \
  "$RESET_BACKUP_DIR/account.json"
sudo cmp --silent \
  /opt/reachcommander/data/auth/account.json \
  "$RESET_BACKUP_DIR/account.json"
sudo rm -- /opt/reachcommander/data/auth/account.json
sudo reachcommander start
sudo reachcommander logs
)
```

Use the newly emitted first-run setup code to create the replacement administrator. The existing key ring can remain, but the replacement account's security stamp makes old cookies invalid. If `account.json` or another authentication file is malformed, `sudo reachcommander doctor` fails without printing its contents. Preserve the malformed bytes for recovery instead of deleting them casually. Account reset never requires changing or removing a configured source directory.

## Synchronize SRT subtitles

The release image includes pinned Alpine FFmpeg and FFprobe binaries. Press `Enter` or double-click an MP4, MKV, or AVI in a filesystem panel to open **Synchronize subtitles**. A same-name SRT in that directory loads automatically; SRT is the only supported subtitle format. H.264/AAC MP4 can stream directly with authenticated byte ranges. Other supported containers/codecs use a temporary browser-compatible HLS preview.

Temporary preview files are written only below `/data/media-previews`, inside the existing application-data mount. The UI reports queued and actively transcoding work separately and keeps Close enabled so either can be canceled. After the first segment becomes playable, the browser continues a status heartbeat while FFmpeg converts the remainder. Explicit close cancels the process tree immediately; queued or still-running work without a browser heartbeat is canceled and cleaned after two minutes. A completed ready session retains the normal 20-minute inactivity expiry. Do not add a broad source or separate media-preview host mount. Defaults allow one FFmpeg worker, an eight-item queue, two decoder/encoder threads, the `ultrafast` preset, best-effort below-normal process priority, 90 minutes and 8 GiB of temporary output per transcode, 4 MiB per SRT, 20,000 cues, and one constant offset up to ten minutes earlier or later.

The installer also writes `REACHCOMMANDER_CPU_LIMIT` into the root-only `.env` and applies it through Compose's `cpus` ceiling to the entire ReachCommander container. The default is `0.75` on a one-CPU host, `1.5` on two CPUs, `2.0` on three CPUs, and `3.0` on hosts with four or more logical CPUs. This keeps API and FFmpeg work inside one hard Docker scheduling boundary while leaving capacity for Ubuntu and other services. An image update supplies the FFmpeg limits immediately; an older installer-managed deployment must rerun the latest checksum-verified installer and choose reconfiguration once to receive the Compose ceiling.

`sudo reachcommander doctor` accepts only the generated media-preview root, lowercase 32-hex session directories, `index.m3u8`, and `segment-NNNNNN.ts`; other entries, symlinks, and mount points remain unsafe. The runtime applies owner-only permissions to generated directories and files. For a trace without exposing source physical paths, use `sudo docker logs --since 30m reachcommander 2>&1 | grep -E 'Media preview|FFmpeg|HLS cleanup'`. Logs include session IDs, safe filenames, FFmpeg process IDs, 30-second progress, lifecycle transitions, redacted bounded failure diagnostics, and cleanup failures.

Preview works on a read-only source, but Save does not. On a writable source, the review preserves `movie.srt` byte-for-byte as the first free backup—`movie_original.srt`, then `movie_original (2).srt`—and publishes corrected UTF-8 timing at `movie.srt`. The video is never modified. Back up the source normally; subtitle backups are not a version history or a replacement for filesystem backups.

The exact package and license/source offer are documented in the repository's `THIRD-PARTY-NOTICES-FFMPEG.md`. A normal installer-managed update replaces the complete image, including these tools; no host FFmpeg package is required.

## Put HTTPS in front

Use one of the checked-in examples as a starting point:

- [Nginx](nginx.conf) — host-native TLS, request streaming, six-hour transfer timeouts, and an optional Basic Authentication block;
- [Caddy](Caddyfile) — automatic HTTPS, optional proxy authentication, and a 50 GB request limit (the `request_body` limit requires Caddy 2.10.0 or newer);
- Traefik [static](traefik.static.yaml) and [dynamic](traefik.dynamic.yaml) files — six-hour client-facing timeouts, an ACME-backed HTTPS router, optional external password file, and an explicit 50 GiB buffering limit.

Replace every example hostname, certificate path, and credential reference. ReachCommander's login is sufficient as the application authentication layer, so proxy authentication is optional defense in depth. Each example identifies the exact Basic Auth directives or middleware that can be omitted. If enabled, Basic Authentication is suitable only over HTTPS; an identity-aware or SSO proxy may replace it. Keep the upstream at `http://127.0.0.1:8092` when the proxy runs natively on the Ubuntu host.

The generated container configuration trusts only the container's exact network gateway address when processing `X-Forwarded-Proto`. This lets ReachCommander recognize the browser-facing HTTPS scheme and issue Secure authentication and antiforgery cookies without trusting forwarded headers from arbitrary clients or an entire private network. Keep your proxy configured to replace `X-Forwarded-Proto` with its authoritative request scheme.

Large files deserve special attention. ReachCommander streams requests, but every proxy can impose a smaller limit or timeout. The examples allow up to 50 GiB and use long timeouts. Nginx disables request buffering. Traefik's optional size-enforcement middleware buffers the body and may spill it to disk, so its temporary storage must have at least the upload capacity you intend to allow; omit that middleware and enforce the limit at an earlier trusted layer if streaming is more important.

The Traefik example assumes Traefik runs on the host. Install both example files, replace the ACME email and hostname, create `/var/lib/traefik/acme.json` with mode `0600`, and create `/etc/traefik/reachcommander.htpasswd` outside the repository. The `websecure` entry point extends client read, write, and idle timeouts to six hours; the dynamic router selects the `letsencrypt` certificate resolver. If you use a trusted certificate store instead, replace that resolver deliberately rather than accepting Traefik's generated default certificate.

Inside a normal container, `127.0.0.1` is the Traefik container—not the Ubuntu host—and it cannot reach a host port bound only to loopback. Either run Traefik with host networking or deliberately bind ReachCommander to a private host address, protect that address with the host firewall, and point Traefik at it. Never solve this by publishing ReachCommander on an unprotected public interface.

The installed PWA also needs this boundary. Production service workers require an HTTPS secure context, and ReachCommander's UI, service worker, manifest, and `/api` requests must remain on the same origin. Proxy the complete site at one hostname without moving `/api` to another origin.

After configuring the proxy:

```bash
curl --fail http://127.0.0.1:8092/health
curl --fail https://reachcommander.example.com/health
```

The first check is local; the second must traverse your real TLS policy. Supply credentials only if you enabled optional proxy authentication.

## Operate the installation

All mutating management commands require root:

```bash
sudo reachcommander status
sudo reachcommander doctor
sudo reachcommander logs
sudo reachcommander logs --follow
sudo reachcommander update-log
sudo reachcommander update-log --follow
sudo reachcommander support-bundle > reachcommander-support.zip
sudo reachcommander start
sudo reachcommander stop
sudo reachcommander restart
```

Run `sudo reachcommander doctor` after changing host mounts, permissions, the proxy bind address, CPU limit, or Docker. It validates the local deployment files, exact installer environment, Compose model, source metadata, the exact application-data allowlist and its host ownership/modes, authentication JSON, image state, port, CPU ceiling, and container health without changing the deployment. Read/write/traverse access is checked as the configured numeric runtime identity inside the running container at the fixed `/data` mount, which is where the application actually accesses the bind-mounted data. The root-owned `/opt/reachcommander` directory remains protected and does not need to be traversable by the container identity. The allowlist covers account state, Data Protection keys, and ReachCommander's durable file-operation plans and status records. A missing account is a warning that first-run setup mode is active; malformed account state is a failure whose contents are never printed.

### Manage sources from the UI

The current Ubuntu installer-managed deployment enables **Add source** in the authenticated top toolbar. This is for one existing absolute Ubuntu host folder at a time. Enter a specific child directory such as `/srv/family-media`; the UI workflow rejects `/`, protected system or installer paths, and broad roots such as `/home`, `/srv`, and `/mnt`. It never accepts a container path, Compose fragment, image reference, environment value, or command.

Each source chip also includes an × action. Its confirmation removes only the installer-managed mapping and bind-mount declaration; it never deletes, moves, changes, or recursively inspects the host folder contents. ReachCommander refuses to remove the final source. If the removed source was a left or right default, the first remaining configured source becomes that default, and live tabs that referenced the removed source are repaired after the restart.

Before opening the UI, create or choose the folder and verify its permissions as the exact runtime UID/GID saved in `/opt/reachcommander/.env`. Every ancestor, up to and including `/`, must be a directory owned by root and not group- or world-writable. The source folder itself is the leaf: it may be owned by the runtime UID/GID and may be writable. This prevents an unprivileged account from replacing a persisted path between validation and container activation.

For a direct child such as `/srv/family-media`, first verify that the existing `/srv` parent is root-owned and mode `0755`, then create only the leaf for runtime UID/GID `1000:1000`:

```bash
sudo stat -c 'owner=%u mode=%a path=%n' / /srv
sudo install -d -o 1000 -g 1000 -m 0750 /srv/family-media
```

If a dedicated parent is needed for stable mount points, create the parent as root-owned `0755` and each runtime-owned leaf separately:

```bash
sudo install -d -o root -g root -m 0755 /srv/reachcommander-sources
sudo install -d -o 1000 -g 1000 -m 0750 /srv/reachcommander-sources/family-media
```

Mount or bind only the intended storage at the leaf; keep the stable parent under root control. A normal `/home/user/...` path fails because `/home/user` is usually user-owned and writable. Do not broadly `chmod` or `chown` `/home`, a user home, `/srv`, or another existing tree to make the check pass; use a narrow root-controlled stable mount below `/srv` instead.

Read-only is selected by default and needs read plus traverse access. Read/write also needs write access and an explicit read/write confirmation because ReachCommander can change or delete files in that host folder. Verify the complete ancestry and runtime access, for example:

```bash
namei -l /srv/family-media
sudo setpriv --reuid 1000 --regid 1000 --clear-groups test -r /srv/family-media
sudo setpriv --reuid 1000 --regid 1000 --clear-groups test -x /srv/family-media
# Run this third check only for a requested read/write source.
sudo setpriv --reuid 1000 --regid 1000 --clear-groups test -w /srv/family-media
```

Do not recursively `chown` or `chmod` an existing media tree. Grant only the intended leaf the minimum ownership, group, or ACL access, then rerun the checks. The host repeats canonical-path, trusted-ancestry, overlap, count, and runtime-identity validation authoritatively. Each Add source transaction also revalidates every existing configured source before staging any change. If an older mapping has an unsafe ancestor, the new add is rejected and the current deployment is left unchanged; prepare a safe stable mount and reconfigure that mapping through the checksum-verified installer before retrying.

After acceptance, the host writes a durable transaction, validates staged Compose state, and restarts only the ReachCommander application container; Docker Engine and unrelated containers keep running. Keep the blocking dialog open. The browser reconnects automatically, retrieves the durable operation result, and refreshes the catalog so the generated source ID appears in both pane selectors. While the operation is active, duplicate submissions and competing update/file-operation restarts are blocked. If activation or health verification fails, the helper restores the previous files and container configuration before reporting rollback.

For an advanced CLI fallback, send the same bounded versioned request to the fixed management command. This is primarily useful when the UI is unavailable; replace the name and absolute path, retain the generated UUID, and choose only `readOnly` or `readWrite`:

```bash
python3 - <<'PY' | sudo reachcommander source add
import json
import uuid

print(json.dumps({
    "protocolVersion": 6,
    "requestId": str(uuid.uuid4()),
    "action": "addSource",
    "displayName": "Family media",
    "hostPath": "/srv/family-media",
    "access": "readOnly",
}, separators=(",", ":")))
PY
```

The command returns only the generated source ID and display name; it does not echo the host path. Run `sudo reachcommander doctor` afterward. If the UI reports a timeout, rollback, or failure, do not edit `compose.yaml`, `config/sources.json`, `state/source-mounts.json`, `state/source-operation.json`, or the transaction backup. Use these root-only support diagnostics first:

```bash
sudo reachcommander doctor
sudo reachcommander status
sudo systemctl status reachcommander-updater.service
sudo journalctl -u reachcommander-updater.service --since today
```

Existing, older installations whose helper predates source management must rerun the latest checksum-verified installer once. An image-only update changes the application container but cannot replace the root-owned helper, CLI, or systemd unit. Reinstallation preserves the administrator account, Data Protection keys, existing sources, durable operation records, update channel, port, and mounted data. Clean installations include the compatible source helper and restricted socket service from first startup.

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

### Automatic system update control

The installer also deploys the root-owned `reachcommander-updater.service`. ReachCommander's backend checks the fixed `dragosniamtu/reach-commander` GitHub repository and matching GHCR package at startup and every six hours. It enables the toolbar button only when the trusted helper reports a different immutable digest. Discovery is automatic, but Apply always requires administrator confirmation and is refused while durable file or archive operations are active. Exact version pins remain pinned and do not follow new releases.

The application is never given Docker control. Its container mounts the restricted Unix socket directory `/run/reachcommander-updater` read-only and never mounts `/var/run/docker.sock`. Browser and API requests contain no channel, image, digest, executable, arguments, or target version. The root helper owns discovery, backup, Compose activation, health validation, rollback, and its durable result journal.

The protocol-v3 helper in the current installer bundle adds a bounded, sanitized trace to the fixed logical stages for downloading the verified image, installing deployment state, restarting ReachCommander, verifying the selected container, checking health, handling timeouts, and recovering the previous version. Open **Technical details** to see elapsed time, last confirmed host activity, and ordered safe events. The section opens automatically after 60 seconds without a confirmed event or when a terminal result needs attention. **Download diagnostics** uses the protocol-v4 diagnostic action to save a five-entry ZIP on the administrator's device. The ZIP is generated in memory, retained nowhere on the server, and uploaded nowhere automatically. It contains fixed status/reason codes and the public trace only; raw host logs, credentials, tokens, source names or paths, filenames, addresses, hostnames, environment values, commands and output, digests, container identifiers, and file contents are excluded. Protocol-v1 through v3 status and apply requests remain compatible.

Existing installations must run the new checksum-verified installer once, using the same inspect, `SHA256SUMS`, and `sha256sum --check --strict SHA256SUMS` process described above. The checksum-verified installer upgrades the deployment to the protocol-v4 diagnostic helper, management CLI, systemd unit, and Compose socket override without moving or replacing the configured sources, authentication record, Data Protection keys, durable operation state, protected traces, or mounted source contents. Old stuck update events cannot be reconstructed; structured capture starts with the next update after the refresh. If the helper is missing or too old, the UI produces a partial ZIP with fixed installer-refresh guidance instead of exposing an error. Future root-helper changes still require another verified installer refresh; updating the application image cannot replace the host helper.

Inspect the boundary and its journal without changing state:

```bash
sudo systemctl status reachcommander-updater.service
sudo journalctl -u reachcommander-updater.service --since today
sudo reachcommander status
sudo reachcommander update-log
sudo reachcommander update-log --follow
sudo reachcommander support-bundle > reachcommander-support.zip
sudo reachcommander doctor
```

During Apply, ReachCommander temporarily drains new mutations and waits for active work to finish. Only the ReachCommander application container is recreated; Docker Engine is never restarted. Keep the browser tab open: it reconnects automatically, activates the matching PWA shell, and reloads once. If the candidate fails its identity or health check, the helper restores the prior digest and verifies the recovered container. Start with **Download diagnostics** or `sudo reachcommander support-bundle > reachcommander-support.zip`; review the ZIP before sharing it. Preserve `/opt/reachcommander/state/system-update.json`, `/opt/reachcommander/state/update-traces`, and the normal update backups when investigating a failure. The root-only `update-log`, `journalctl`, and `doctor` outputs are advanced follow-up evidence and are intentionally not copied into the shareable bundle.

The in-app control is supported only for Ubuntu installer-managed deployments. It remains safely disabled for Windows development, macOS Docker Desktop, and manual container deployments because those environments do not have this restricted systemd boundary.

## Uninstall without touching source data

```bash
sudo reachcommander uninstall
```

The command first asks what to do with application data:

- `retain` is the default. It removes the container, command, and generated deployment files but leaves the inactive application-data tree at `/opt/reachcommander/data` and prints that exact path.
- `backup` stops the service, copies the generated deployment plus every allowlisted application-data file to a timestamped directory under `/var/backups/reachcommander`, sets those backup files to mode `0600`, flushes them, compares every copy byte-for-byte, and only then removes the original data tree.

After that selection, the command requires the exact confirmation `uninstall ReachCommander`. It revalidates every recorded source path and the application-data tree, stops the application before its final validation or backup, tears down Compose without deleting volumes, and removes only the installer-owned allowlist. Source directories and their contents are never removed by the uninstaller. This includes source-local `.reachcommander-trash`, which is not copied into the installer backup and must be retained or backed up with its source. If final validation or verified backup creation fails, uninstall preserves the deployment and attempts to restart the previously healthy service.

Keep a verified backup until you have confirmed that you no longer need the account, cookie keys, file-operation history, generated source mapping, or pinned image record.

## Troubleshooting

- **Package pull is denied:** confirm the GHCR package exists and its package visibility is Public; repository visibility alone may not be enough.
- **The service is healthy locally but unavailable remotely:** keep ReachCommander on loopback and check the reverse proxy listener, certificate, firewall, and optional proxy-authentication policy.
- **A source is unavailable:** verify the saved host path still exists, has not become a symlink, and is traversable by the configured UID/GID.
- **Writes are denied:** the source must be explicitly read-write in application policy, mounted read-write, and writable by the configured UID/GID. Do not weaken unrelated parent directories.
- **PWA installation is not offered:** use the HTTPS hostname, not a plain HTTP LAN address, and keep the application and API on the same origin.
- **The login screen requests first-run setup unexpectedly:** run `sudo reachcommander doctor`, confirm `/opt/reachcommander/data/auth/account.json` is present and valid, and restore the account plus `/opt/reachcommander/data/keys` together if they were lost. Do not create a replacement account until the missing state is understood.
- **An update was interrupted:** do not edit the transaction marker or protected trace. Run `sudo reachcommander update-log`, `sudo journalctl -u reachcommander-updater.service --since today`, and `sudo reachcommander doctor`; use the retained update backup for recovery.
- **Add source is disabled:** unsupported platforms remain read-only. On an older installer-managed Ubuntu host, rerun the latest checksum-verified installer once; an image-only update cannot upgrade the root-owned source helper.
- **A source add is rejected:** enter an existing absolute Ubuntu host folder below a specific child directory, check every parent with `namei -l`, and confirm each ancestor is root-owned and not group- or world-writable. Then test read/traverse/write access as the configured runtime UID/GID. `/home/user/...` normally fails; prepare a narrow root-controlled stable mount below `/srv` instead of weakening a broad parent directory. An unsafe existing configured source also blocks a new add until that mapping is safely reconfigured.
- **A source add timed out or rolled back:** keep the protected operation/transaction files intact, then collect the `doctor`, `status`, systemd status, and updater journal support diagnostics shown above before retrying.
- **Media preview reports unavailable or probing/queued/transcoding fails:** confirm the running image is the current official release, then run `sudo docker exec reachcommander ffmpeg -version` and `sudo docker exec reachcommander ffprobe -version`. Both must report 6.1.2. Collect `sudo docker logs --since 30m reachcommander`, `sudo reachcommander logs`, and `sudo reachcommander doctor`; session lifecycle logs reveal whether work is waiting, active, playable, completed, canceled, or failed. Do not install host codecs into the running container or expose physical paths in a support report.
- **Temporary media previews consume unexpected space:** close abandoned browser workspaces and wait up to the two-minute heartbeat timeout plus one cleanup interval. Confirm `/opt/reachcommander/data/media-previews` is inside the existing data tree and not a separate mount. If files remain after sessions expire, preserve logs and run `sudo reachcommander doctor` before manual cleanup.
