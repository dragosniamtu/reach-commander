# ReachCommander deployment tools

This directory contains the release-only Ubuntu bundle, its deterministic packaging tools, and the unprivileged Docker Desktop bootstrap at `macos/install.sh`. It is separate from the repository-root `compose.yaml`, which remains intended for local source builds.

The Ubuntu bundle is versioned, packaged, and checksum verified. The macOS bootstrap is intentionally a single self-contained Bash 3.2 script that downloads the shared hardened Compose template, resolves the published `stable` image to an immutable digest, and stores deployment state under `~/Library/Application Support/ReachCommander`. See the [macOS installation guide](../docs/deployment/macos.md) for the inspect-first flow and the security implications of executing the mutable `master` installer command.

The published Ubuntu installer archive contains:

- `install.sh`, the interactive root entry point;
- `lan_address.py`, the local RFC1918 display-address helper;
- `reachcommander`, the fixed-path lifecycle command, including the structured `sudo reachcommander source add` fallback;
- `source_management.py`, the durable validated source transaction helper;
- `render_config.py`, the structured deployment renderer;
- `compose.release.yaml`, the hardened published-image template;
- `compose.updater.yaml`, the read-only `/run/reachcommander-updater` socket mount;
- `updater_service.py`, `updater_protocol.py`, and `systemd/reachcommander-updater.service`, the restricted root host boundary;
- `lib/common.sh`, shared validation and Docker primitives;
- `VERSION` and `LICENSE`.

The archive contains no credentials, source directories, generated authentication state, Compose files, or user configuration. `stable`, `edge`, and an exact `vX.Y.Z` are discovery channels; an installed deployment always persists the resolved immutable image digest.

Ubuntu installations default to a loopback-only HTTPS reverse-proxy upstream. The explicit **Direct HTTP on trusted LAN** mode publishes host port `8092` on all host interfaces and forwards it to container port `8080`; open `http://<server-lan-ip>:<port>`. It keeps authentication protections enabled but provides no transport encryption. Do not add router forwarding or public exposure; DHCP address changes do not require reconfiguration, and PWA installation requires HTTPS.

The installer keeps account state, Data Protection keys, and durable file-operation metadata under a mode-`0700` application-data tree outside the container image. Reconfiguration preserves those bytes and normalizes validated files to mode `0600`. Uninstall can retain the inactive data tree in place or copy every exactly allowlisted application-data file to a verified mode-`0600` backup before removing it.

To build a stable release bundle locally:

```bash
bash deploy/package-installer.sh v1.2.3 artifacts/installer
cd artifacts/installer
sha256sum --check SHA256SUMS
```

Only stable semantic versions are packaged. Prerelease images can be published but do not produce the stable installer asset.

The Ubuntu installer-managed updater checks the fixed public repository/package at startup and every six hours. The application can request only target-free update actions and bounded source-management actions over the Unix socket; exact version pins remain pinned. The application never mounts `/var/run/docker.sock`. Windows, macOS, and manual container deployments do not install this systemd helper.

Clean installations include the source helper, management command, restricted `/run/reachcommander-updater` socket runtime, and systemd unit. The authenticated **Add source** dialog accepts only a display name, one existing absolute Ubuntu host folder, and `readOnly` or `readWrite`. It requires a specific child directory such as `/srv/family-media`; every ancestor must be root-owned and not group- or world-writable, while the runtime-owned leaf may be writable. A normal `/home/user/...` path fails, so use a narrow root-controlled stable mount rather than weakening the home tree. The helper validates access as the configured runtime UID/GID and revalidates every configured source before changing state. The mapping remains read-only by default and requires read/write confirmation before ReachCommander can change or delete files in the host folder. The helper generates the source ID, validates and commits a durable Compose/config transaction, restarts only the ReachCommander application container, and the browser reconnects automatically before refreshing both selectors. Removing a source uses only its validated source ID, commits through the same rollback-capable transaction, preserves the host directory and every file, repairs removed defaults deterministically, and refuses to remove the last mapping.

Existing, older installations must rerun the latest checksum-verified installer once to add this capability; an image-only update cannot replace the root-owned helper. On rollback or timeout, preserve the protected transaction state and collect support diagnostics with `sudo reachcommander doctor`, `sudo reachcommander status`, and `sudo journalctl -u reachcommander-updater.service --since today`. The [Ubuntu guide](../docs/deployment/ubuntu.md#manage-sources-from-the-ui) contains prerequisite checks and the advanced `sudo reachcommander source add` CLI fallback.

The protocol-v3 Ubuntu helper in the current installer bundle adds a bounded, sanitized event trace to the existing download, install, restart, health-check, and rollback stages. The UI shows that trace under **Technical details**, including elapsed time and last confirmed host activity. Protocol-v2 remains compatible without the event trace, and protocol-v1 shows **Applying trusted update** instead of inventing unconfirmed steps; generic progress is not evidence that the update is stalled. Raw host logs, Docker output, paths, commands, digests, exit codes, and deadlines are intentionally excluded from the browser contract.

Refresh an existing installation by rerunning a checksum-verified installer bundle. The checksum-verified installer upgrades the deployment to the protocol-v3 helper and unit while preserving authentication data, Data Protection keys, source configuration, durable operation state, protected traces, and mounted source contents. Old stuck update events cannot be reconstructed; trace capture starts with the next update. An application-container update cannot upgrade the root-owned helper, so future helper changes require another verified installer refresh.

Apply recreates only the ReachCommander application container; Docker Engine is never restarted. For safe browser details use **Technical details**. For root-only diagnosis use `sudo reachcommander update-log`, follow an active trace with `sudo reachcommander update-log --follow`, inspect `sudo journalctl -u reachcommander-updater.service --since today`, and finish with `sudo reachcommander doctor`.
