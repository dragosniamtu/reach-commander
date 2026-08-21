# ReachCommander deployment bundle

This directory contains the release-only Ubuntu installer and its deterministic packaging tools. It is separate from the repository-root `compose.yaml`, which remains intended for local source builds.

The published installer archive contains:

- `install.sh`, the interactive root entry point;
- `reachcommander`, the fixed-path lifecycle command;
- `render_config.py`, the structured deployment renderer;
- `compose.release.yaml`, the hardened published-image template;
- `lib/common.sh`, shared validation and Docker primitives;
- `VERSION` and `LICENSE`.

The archive contains no credentials, source directories, generated Compose files, or user configuration. `stable`, `edge`, and an exact `vX.Y.Z` are discovery channels; an installed deployment always persists the resolved immutable image digest.

To build a stable release bundle locally:

```bash
bash deploy/package-installer.sh v1.2.3 artifacts/installer
cd artifacts/installer
sha256sum --check SHA256SUMS
```

Only stable semantic versions are packaged. Prerelease images can be published but do not produce the stable installer asset.
