# ReachCommander installation index

Choose the platform guide for the machine that will run ReachCommander:

- [Ubuntu production installer](deployment/ubuntu.md) — recommended server deployment with Docker Engine, a loopback-bound upstream, HTTPS reverse proxy, digest-pinned updates, rollback, and safe uninstall.
- [macOS one-command installer](deployment/macos.md) — Docker Desktop deployment for Intel or Apple Silicon, with specific-folder or advanced broad-source choices.
- Windows development and source execution are documented in the [README](../README.md#local-development). Production Ubuntu remains the recommended self-hosted target.

Before enabling writes, read the [file operations and managed Trash runbook](operations.md). Back up the installer/native `/data` location for authentication and durable queue metadata. Separately back up `.reachcommander-trash` inside every writable source whose deleted items must remain recoverable; installers do not remove or include source-local Trash in ordinary uninstall backups.

ReachCommander terminates no TLS itself. Keep its upstream listener on loopback and publish it through an HTTPS reverse proxy. Start with narrowly scoped read-only sources, then opt individual destinations into `readOnly: false` only after verifying host permissions and backups.

The in-app full-stack update control is supported only by an Ubuntu installer-managed deployment. Existing installations must run the new checksum-verified installer once to add `reachcommander-updater.service` and the read-only `/run/reachcommander-updater` socket mount. Automatic discovery runs at startup and every six hours, but Apply always requires administrator confirmation. Exact version pins remain pinned; Windows, macOS, and manual container installations display a safe unsupported state.
