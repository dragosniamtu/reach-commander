# ReachCommander deployment bundle

This directory contains the release-only Ubuntu installer and its deterministic packaging tools. It is separate from the repository-root `compose.yaml`, which remains intended for local source builds.

The published installer archive contains:

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
