# Install ReachCommander on macOS

ReachCommander runs on macOS through Docker Desktop. Docker selects the published `linux/amd64` image on an Intel Mac and the `linux/arm64` image on Apple Silicon. This is not a native macOS application: the API, filesystem access, and hardware metrics run inside Docker Desktop's Linux container/VM.

The installer is unprivileged. It does not use `sudo`, install Docker Desktop, open a browser, create a native `.app`, or configure public internet access.

## Prerequisites

Install and start Docker Desktop, then confirm that Docker Compose v2 is available:

```bash
docker info
docker compose version
```

Docker Desktop must be permitted to share every selected folder or external volume. If a path is outside the normally shared locations, add it under Docker Desktop file sharing settings before installing. macOS may also show privacy prompts for Documents, Downloads, removable media, or network volumes.

## One-command installation

Run:

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/dragosniamtu/reach-commander/master/deploy/macos/install.sh)"
```

This convenience command downloads and executes the current `master` installer. It is easy to use, but the script URL is mutable and is not cryptographically pinned. Review the repository and commit history before using it on a machine with sensitive data.

For an inspect-first flow, download the script to a private temporary directory, review it, and then execute it:

```bash
set -Eeuo pipefail
RC_INSPECT_ROOT="$(mktemp -d "${TMPDIR:?}/reachcommander-inspect.XXXXXX")"
chmod 0700 "$RC_INSPECT_ROOT"
RC_INSTALLER="$RC_INSPECT_ROOT/install.sh"
curl --fail --show-error --silent --location \
  --proto '=https' --tlsv1.2 \
  --output "$RC_INSTALLER" \
  https://raw.githubusercontent.com/dragosniamtu/reach-commander/master/deploy/macos/install.sh
less "$RC_INSTALLER"
/bin/bash "$RC_INSTALLER"
```

## Source choices

The installer asks:

```text
What should ReachCommander access?
1. Whole drives
2. Specific folders (Recommended)
```

`Specific folders` is the safer default. Add one or more existing folders, give each a display name, and select its policy:

- `Read-only` (`RO`) permits browsing but blocks rename, upload, and extraction destinations.
- `Read/write` (`RW`) enables controlled mutations when macOS and Docker permissions also allow writes.

Use the narrowest folder that fits the workflow. A source is mounted independently at `/sources/<id>`; ReachCommander never receives the Docker socket or the macOS system root.

`Whole drives` is an advanced option. Its internal-drive choice means the current user's home directory, not `/` or the protected macOS system volume. Each direct external directory under `/Volumes`, such as `/Volumes/Media`, is offered separately; the `/Volumes` parent is never mounted. Read/write access to a whole drive requires typing its exact canonical path as confirmation.

A whole home contains hidden and private data. When that broad source is selected, the installer-owned `~/Library/Application Support/ReachCommander` subtree is masked by an empty nested read-only bind. Authentication, keys, generated configuration, and transaction state therefore cannot be reached through ReachCommander's file APIs. A specific source equal to or inside that installer-owned directory is rejected.

Disconnecting a selected external drive makes its source unavailable. Reconnect it at the same `/Volumes/<name>` path and restart the service. The installer never mounts or reformats a device.

## Network choice

The installer then asks:

```text
Who can access ReachCommander?
1. This Mac only (Recommended)
2. Devices on the local network
```

`This Mac only` binds to `127.0.0.1`; the default endpoint is `http://127.0.0.1:8080`. Local-network mode explicitly binds to `0.0.0.0` and prints a LAN URL. It still requires ReachCommander's built-in authentication, but it is plain HTTP and does not provide TLS. Use only a trusted network, or place the service behind an HTTPS reverse proxy. The installer does not configure public internet access, router port forwarding, DNS, certificates, or firewall exceptions.

If the selected port is occupied, the installer asks for another port. During reconfiguration, the current ReachCommander port is allowed even though its existing container is listening on it.

## First run and authentication

After a healthy startup, the installer prints the endpoint and an exact Docker Compose logs command. Run that command to obtain the random first-run setup code, open the endpoint yourself, and enter the setup code with the administrator username and password you want to create.

The password is never written into the image, Compose file, installer output, or browser storage. Persistent security state lives below:

```text
~/Library/Application Support/ReachCommander/
  data/auth/account.json
  data/keys/
```

`data/auth/account.json` contains the salted account record. `data/keys` contains the ASP.NET Core Data Protection keys used for session cookies. The `data` tree also contains durable file-operation metadata. Back it up while ReachCommander is stopped, protect the backup like credentials, and restore it as one unit. Separately back up `.reachcommander-trash` inside every writable configured source when deleted files must remain recoverable; installer lifecycle backups do not copy source-local Trash. See the [file operations runbook](../operations.md). Deleting only `data/auth/account.json` starts account recovery on the next run; deleting `data/keys` signs out existing sessions but does not reset the account.

## Installation state and lifecycle

The complete unprivileged deployment is stored at `~/Library/Application Support/ReachCommander`:

```text
.env
compose.yaml
config/sources.json
data/auth/
data/keys/
excluded/
state/source-mounts.json
state/channel
state/current-image
state/previous-image
backups/
```

The installer resolves the `stable` image tag to an immutable digest and persists that digest. Recreating the container keeps the account and Data Protection keys because `data` is not part of generated-file replacement. It also detects the Mac's logical CPU count and applies a Docker Compose ceiling of `0.75`, `1.5`, `2.0`, or `3.0` CPUs for hosts with one, two, three, or four-or-more logical CPUs. This bounds both the API and FFmpeg preview process while leaving scheduler headroom for macOS and Docker Desktop. Rerun the installer and choose reconfiguration once after upgrading an older deployment to add this generated setting.

Rerunning the one-command installer shows:

```text
ReachCommander is already installed.
1. Update (Recommended)
2. Reconfigure
3. Exit
```

Update discovers the current `stable` digest. A changed digest is started and health checked; an unhealthy update triggers automatic rollback to the previous digest. Reconfigure stages and validates new source/network settings against the current digest before replacing generated files. Authentication data is never part of either transaction.

Useful commands are:

```bash
RC_MAC_ROOT="$HOME/Library/Application Support/ReachCommander"
docker compose --project-name reachcommander \
  --project-directory "$RC_MAC_ROOT" \
  --file "$RC_MAC_ROOT/compose.yaml" ps
docker compose --project-name reachcommander \
  --project-directory "$RC_MAC_ROOT" \
  --file "$RC_MAC_ROOT/compose.yaml" logs --tail 200 reachcommander
docker compose --project-name reachcommander \
  --project-directory "$RC_MAC_ROOT" \
  --file "$RC_MAC_ROOT/compose.yaml" up -d reachcommander
docker compose --project-name reachcommander \
  --project-directory "$RC_MAC_ROOT" \
  --file "$RC_MAC_ROOT/compose.yaml" down
```

Do not use a raw `docker compose pull` as the update procedure; it bypasses digest discovery, health validation, and rollback.

## Troubleshooting

- **Docker is missing or stopped:** start Docker Desktop and wait for `docker info` to succeed, then rerun the installer.
- **Image pull fails:** confirm the Mac can reach `ghcr.io` and that the ReachCommander package is public. If package visibility changes, authenticate Docker to GHCR before rerunning.
- **Port conflict:** choose a free port when prompted. Only an unchanged current deployment port is exempt during reconfiguration.
- **Folder is denied:** add the exact path to Docker Desktop file sharing and approve relevant macOS privacy access. The installer permission probe never creates a file in a source.
- **External volume is unavailable:** reconnect it at its original `/Volumes/<name>` location before starting ReachCommander.
- **Setup code is unavailable:** confirm no account already exists, then use the printed `logs --tail 200 reachcommander` command. Setup codes and authorization values are redacted from failure diagnostics.
- **An update is unhealthy:** the installer restores the previous generated configuration and digest. Review the bounded redacted diagnostics and container logs before retrying.
- **PWA installation on another LAN device is unavailable:** plain HTTP on a LAN address is not a browser secure context. Use an HTTPS reverse proxy for full installable-PWA behavior away from localhost.
- **Temperatures, fans, or GPU data look different:** Docker Desktop exposes the Linux container/VM view, not complete native Mac hardware sensors. Unsupported values remain unavailable.

## Safe removal

Stop the service, then move the installer-owned directory to a dated backup. Do not remove or move any configured source:

```bash
set -Eeuo pipefail
RC_MAC_ROOT="$HOME/Library/Application Support/ReachCommander"
docker compose --project-name reachcommander \
  --project-directory "$RC_MAC_ROOT" \
  --file "$RC_MAC_ROOT/compose.yaml" down
RC_REMOVED="$HOME/ReachCommander-removed-$(date +%Y%m%d-%H%M%S)"
mv "$RC_MAC_ROOT" "$RC_REMOVED"
printf 'ReachCommander state retained at %s\n' "$RC_REMOVED"
```

Verify the retained account, key, operation metadata, and configuration backup before deleting it manually. Source folders, external volumes, and their source-local `.reachcommander-trash` directories are outside the installer-owned directory and are never removal targets.
