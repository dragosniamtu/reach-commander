# ReachCommander deployment tools

This directory contains the release-only Ubuntu bundle, its deterministic packaging tools, and the unprivileged Docker Desktop bootstrap at `macos/install.sh`. It is separate from the repository-root `compose.yaml`, which remains intended for local source builds.

The Ubuntu bundle is versioned, packaged, and checksum verified. The macOS bootstrap is intentionally a single self-contained Bash 3.2 script that downloads the shared hardened Compose template, resolves the published `stable` image to an immutable digest, and stores deployment state under `~/Library/Application Support/ReachCommander`. See the [macOS installation guide](../docs/deployment/macos.md) for the inspect-first flow and the security implications of executing the mutable `master` installer command.

The published Ubuntu installer archive contains:

- `install.sh`, the interactive root entry point;
- `reachcommander`, the fixed-path lifecycle command;
- `render_config.py`, the structured deployment renderer;
- `compose.release.yaml`, the hardened published-image template;
- `lib/common.sh`, shared validation and Docker primitives;
- `VERSION` and `LICENSE`.

The archive contains no credentials, source directories, generated authentication state, Compose files, or user configuration. `stable`, `edge`, and an exact `vX.Y.Z` are discovery channels; an installed deployment always persists the resolved immutable image digest.

The installer creates a dedicated mode-`0700` `data/auth` account directory and `data/keys` Data Protection key ring outside the container image. Reconfiguration preserves those bytes. Uninstall can retain the inactive data tree in place or copy every validated authentication file to a verified mode-`0600` backup before removing it.

To build a stable release bundle locally:

```bash
bash deploy/package-installer.sh v1.2.3 artifacts/installer
cd artifacts/installer
sha256sum --check SHA256SUMS
```

Only stable semantic versions are packaged. Prerelease images can be published but do not produce the stable installer asset.
