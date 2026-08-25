# ReachCommander deployment tools

This directory contains the release-only Ubuntu bundle, its deterministic packaging tools, and the unprivileged Docker Desktop bootstrap at `macos/install.sh`. It is separate from the repository-root `compose.yaml`, which remains intended for local source builds.

The Ubuntu bundle is versioned, packaged, and checksum verified. The macOS bootstrap is intentionally a single self-contained Bash 3.2 script that downloads the shared hardened Compose template, resolves the published `stable` image to an immutable digest, and stores deployment state under `~/Library/Application Support/ReachCommander`. See the [macOS installation guide](../docs/deployment/macos.md) for the inspect-first flow and the security implications of executing the mutable `master` installer command.

The published Ubuntu installer archive contains:

- `install.sh`, the interactive root entry point;
- `lan_address.py`, the local RFC1918 display-address helper;
- `reachcommander`, the fixed-path lifecycle command;
- `render_config.py`, the structured deployment renderer;
- `compose.release.yaml`, the hardened published-image template;
- `compose.updater.yaml`, the read-only `/run/reachcommander-updater` socket mount;
- `updater_service.py`, `updater_protocol.py`, and `systemd/reachcommander-updater.service`, the restricted root host boundary;
- `lib/common.sh`, shared validation and Docker primitives;
- `VERSION` and `LICENSE`.

The archive contains no credentials, source directories, generated authentication state, Compose files, or user configuration. `stable`, `edge`, and an exact `vX.Y.Z` are discovery channels; an installed deployment always persists the resolved immutable image digest.

Ubuntu installations default to a loopback-only HTTPS reverse-proxy upstream. The explicit **Direct HTTP on trusted LAN** mode publishes host port `8092` on all host interfaces and forwards it to container port `8080`; open `http://<server-lan-ip>:<port>`. It keeps authentication protections enabled but provides no transport encryption. Do not add router forwarding or public exposure; DHCP address changes do not require reconfiguration, and PWA installation requires HTTPS.

The installer creates a dedicated mode-`0700` `data/auth` account directory and `data/keys` Data Protection key ring outside the container image. Reconfiguration preserves those bytes. Uninstall can retain the inactive data tree in place or copy every validated authentication file to a verified mode-`0600` backup before removing it.

To build a stable release bundle locally:

```bash
bash deploy/package-installer.sh v1.2.3 artifacts/installer
cd artifacts/installer
sha256sum --check SHA256SUMS
```

Only stable semantic versions are packaged. Prerelease images can be published but do not produce the stable installer asset.

The Ubuntu installer-managed updater checks the fixed public repository/package at startup and every six hours. The application can request only target-free status, Check, and administrator-confirmed Apply actions over the Unix socket; exact version pins remain pinned. The application never mounts `/var/run/docker.sock`. Windows, macOS, and manual container deployments do not install this systemd helper. Existing installations must rerun a checksum-verified installer bundle once, and future changes to the root-owned updater helper require another installer refresh.
