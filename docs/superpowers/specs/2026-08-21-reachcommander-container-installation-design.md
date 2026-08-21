# ReachCommander Container Distribution and Ubuntu Installer Design

**Date:** 2026-08-21

**Status:** Approved

**Target:** Public, self-hosted Ubuntu installation using Docker Compose

## Problem

ReachCommander already has a production Dockerfile and hardened local Compose deployment, but the checked-in Compose file builds `reachcommander:local` from a cloned repository. An Ubuntu administrator must therefore install build dependencies, clone the source, understand the relationship between bind mounts and `sources.json`, and build every update locally.

The desired experience is the pull-and-run model used by FileBrowser Quantum: a public image, a small persistent deployment directory, explicit storage mounts, and simple start/update commands. ReachCommander must preserve its stricter filesystem boundary and must not imply that it has authentication, TLS, or safe public exposure when those features do not exist.

## Goals

- Publish one public GHCR image for `linux/amd64` and `linux/arm64`.
- Publish `edge` from verified `master` commits.
- Publish semantic version tags and move `stable` only for verified, non-prerelease version tags.
- Let an Ubuntu administrator install without cloning the repository or building the application.
- Generate Docker mounts and ReachCommander source configuration from one interactive input flow so they cannot drift.
- Bind to loopback by default for an existing authenticated HTTPS reverse proxy.
- Provide safe status, logs, restart, update, version rollback, diagnostics, and uninstall commands.
- Preserve the existing unprivileged, read-only-root, capability-free container posture.
- Make installation and updates transactional and health-checked.
- Ship versioned installer assets with checksums, image SBOM, and build provenance.

## Non-goals

- Installing Docker Engine or Docker Compose automatically.
- Adding ReachCommander authentication, authorization, TLS termination, or user management.
- Replacing an administrator's reverse proxy.
- Changing permissions or ownership recursively on mounted storage.
- Mounting devices, removable storage, Docker sockets, or broad host filesystem roots automatically.
- Automatically configuring GPU access or vendor container runtimes.
- Packaging a native `.deb`, Snap, systemd-hosted .NET application, or Kubernetes chart.
- Creating a database or persistent application-data volume; this release has no server-side database.
- Providing unattended destructive uninstall or deleting any configured source directory.

## Recommended architecture

The repository remains a modular monolith and retains one application image. Distribution adds four bounded units:

1. **Verified container publication** extends GitHub Actions after the existing backend and acceptance jobs.
2. **Versioned installer bundle** contains the interactive installer, management command, and published-image Compose template.
3. **Generated deployment** lives under `/opt/reachcommander` and contains only configuration and deployment metadata.
4. **Administrator command** at `/usr/local/bin/reachcommander` manages the generated deployment without becoming a second application runtime.

No service, database, agent, or privileged helper is added. Docker Compose remains the runtime supervisor.

## Release channels and image naming

The image name is:

```text
ghcr.io/dragosniamtu/reach-commander
```

Tags follow these rules:

| Source event | Published tags |
|---|---|
| Verified push to `master` | `edge` |
| Verified tag `v1.2.3` | `v1.2.3`, `v1.2`, `v1`, `stable` |
| Verified tag `v1.3.0-beta.1` | `v1.3.0-beta.1` only |
| Pull request | No image publication |

Version tags must point to a commit contained in `master`. A release job rejects malformed tags and refuses to move `stable` for any tag containing a prerelease suffix. There is no mutable `latest` tag, preventing ambiguity between stable and development channels.

The first published GHCR package must be connected to the public repository and made public through GitHub's package settings. The image carries OCI title, description, source repository, revision, version, license, and documentation labels.

## CI and publication flow

The existing CI workflow gains tag triggers and a publication job that depends on successful backend and frontend/browser acceptance jobs:

```text
commit or tag
  -> 477 .NET tests on Windows and Ubuntu
  -> 198 Angular tests + 2 PWA contract tests + production build
  -> API publish validation + 19 Chromium scenarios
  -> amd64 container smoke test and /health check
  -> amd64/arm64 Buildx publication
  -> manifest, tag-policy, SBOM, and provenance verification
  -> versioned installer release assets for stable version tags
```

GitHub Container Registry authentication uses the job-scoped `GITHUB_TOKEN` with `packages: write`. Image publication uses official Docker setup, metadata, login, and build/push actions. Buildx publishes `linux/amd64` and `linux/arm64` as one manifest, with GitHub Actions caching, `mode=max` provenance, and an SBOM attestation. No registry password or personal access token is stored.

Before a multi-platform push, CI builds and runs an amd64 image, waits for its container health check, requests `/health`, and removes the temporary test container. After publication, CI inspects the remote manifest and asserts that both required runnable platforms exist and that the emitted tags match the channel policy. Additional non-runnable attestation descriptors are allowed.

For a non-prerelease semantic tag, the workflow creates or updates the corresponding GitHub Release and uploads:

```text
reachcommander-installer.tar.gz
SHA256SUMS
```

The archive contains the installer, management command, Compose template, license, and a version/channel metadata file. It contains no credentials, storage paths, or generated user configuration.

## Repository layout

Deployment sources are isolated from the existing local-development Compose files:

```text
deploy/
├── compose.release.yaml
├── install.sh
├── reachcommander
├── render_config.py
├── release-tags.mjs
├── package-installer.sh
├── lib/
│   └── common.sh
└── README.md
tests/
└── installer/
    ├── fixtures/
    └── test-installer.sh
```

The root `compose.yaml` continues to build `reachcommander:local` for contributors. The release template under `deploy/` always consumes an already-published image.

## Generated Ubuntu deployment

Installation defaults to:

```text
/opt/reachcommander/
├── .env
├── compose.yaml
├── bin/
│   └── render_config.py
├── config/
│   └── sources.json
├── state/
│   ├── channel
│   ├── current-image
│   ├── previous-image
│   └── command.lock
├── lib/
│   └── common.sh
└── backups/
```

The installer also places the management command at `/usr/local/bin/reachcommander`. Files are created with a restrictive umask. The configuration directory is mounted read-only into the container.

`.env` records operational values rather than source definitions:

```text
REACHCOMMANDER_BIND_ADDRESS=127.0.0.1
REACHCOMMANDER_PORT=8092
REACHCOMMANDER_UID=1000
REACHCOMMANDER_GID=1000
REACHCOMMANDER_IMAGE=ghcr.io/dragosniamtu/reach-commander@sha256:<verified-digest>
```

The selected update channel is stored separately in `state/channel`, initially `stable`. Compose runs the immutable digest in `.env`; the moving channel is used only to discover an update. This makes health-check rollback deterministic even after `stable` moves.

The generated Compose service preserves the existing controls:

- user-selected non-root UID/GID;
- loopback-only port publishing by default;
- read-only root filesystem;
- bounded `/tmp` tmpfs;
- all Linux capabilities dropped;
- `no-new-privileges` enabled;
- no Docker socket, privileged mode, or host PID namespace;
- container health check on port 8080;
- `restart: unless-stopped`;
- explicit per-source bind mounts only.

## Interactive installation flow

The installer requires root only to write `/opt/reachcommander`, install the management command, and invoke Docker. When called through `sudo`, it defaults the container UID/GID to `SUDO_UID:SUDO_GID`, not root.

The flow is:

1. Verify Ubuntu-compatible shell tools, Python 3, Docker Engine, and Docker Compose v2.
2. Detect an existing ReachCommander deployment and offer reconfiguration or exit; never overwrite silently.
3. Ask for bind address and port, defaulting to `127.0.0.1:8092`.
4. Ask for the runtime UID/GID, defaulting to the invoking non-root user.
5. Collect one or more sources: display name, unique normalized ID, absolute host path, and `RO` or `RW` policy.
6. Resolve and validate each host path, reject duplicates and dangerous roots, and confirm filesystem access without changing permissions.
7. Ask which source opens by default in the left pane and which opens in the right pane.
8. Require explicit confirmation that an authenticated HTTPS reverse proxy will protect the loopback service.
9. Generate JSON and Compose in a temporary directory using structured serialization, not string interpolation.
10. Validate JSON, run `docker compose config`, pull `stable`, resolve its digest, and write the digest-pinned `.env`.
11. Atomically install the validated files, start the service, and poll Docker's health status with a bounded timeout.
12. Print the local endpoint, container status, reverse-proxy documentation links, and management commands.

Source IDs use lowercase ASCII letters, digits, hyphens, and underscores and must be unique. Container targets are generated as `/sources/<source-id>`. Host paths may contain spaces because the generator writes structured, correctly quoted YAML.

The installer rejects `/`, `/proc`, `/sys`, `/dev`, `/run`, `/var/run`, the Docker socket, and any path that resolves into those locations. It warns and requires an additional confirmation for broad paths such as `/home` or `/srv`, but it does not reject appropriately narrow subdirectories. A read-only source is mounted `:ro` and marked `readOnly: true`; a writable source is mounted `:rw` and marked `readOnly: false`.

The installer checks apparent read/write access for the selected UID/GID and reports failures. It never uses recursive `chmod` or `chown`; the administrator must deliberately correct host permissions.

## Management command

`/usr/local/bin/reachcommander` has a small, explicit command surface:

```text
reachcommander status
reachcommander logs [--follow]
reachcommander start
reachcommander stop
reachcommander restart
reachcommander doctor
reachcommander update [stable|edge|vX.Y.Z]
reachcommander uninstall
```

Every command resolves the fixed deployment directory and uses `docker compose --project-directory /opt/reachcommander`. It does not accept an arbitrary Compose path from environment variables when run with elevated privileges.

`doctor` validates Docker/Compose availability, JSON readability, Compose rendering, bind-source existence, UID/GID access, loopback binding, container health, and the configured image/channel. It prints actionable failures without mutating the deployment.

## Update and rollback flow

`reachcommander update` uses the saved channel unless an explicit channel or semantic version is supplied:

1. Validate the requested channel/version syntax.
2. Pull `ghcr.io/dragosniamtu/reach-commander:<requested>`.
3. Resolve the pulled reference to an immutable repository digest.
4. If the digest equals the deployed digest, report that no update is needed.
5. Back up `.env`, source configuration, current channel, and current digest.
6. Write the new digest and channel atomically.
7. Recreate only the ReachCommander service.
8. Wait for Docker health to become `healthy` within the configured deadline.
9. On failure, restore the previous digest/channel and recreate the previous healthy service.
10. Retain bounded diagnostic output and the failed digest for troubleshooting.

User source directories remain external bind mounts throughout. Rollback changes only the application image reference and generated deployment metadata.

## Uninstall safety

Uninstall first displays the exact container, network, management-command path, and deployment directory that it will remove. It requires an interactive confirmation. The default operation:

- stops and removes the ReachCommander container and Compose network;
- removes `/usr/local/bin/reachcommander`;
- moves generated configuration to `/var/backups/reachcommander/<UTC-timestamp>/` by default, or leaves the inactive deployment directory in place when explicitly requested;
- never invokes recursive deletion on a configured source path;
- never follows source symlinks;
- never runs `docker compose down -v` against unrelated volumes.

Purging configuration is a separate, explicit confirmation. Tests treat deletion of a configured source or any ancestor of it as a release-blocking failure.

## Reverse-proxy boundary

ReachCommander has no built-in authentication. The installer therefore binds to loopback and requires the administrator to acknowledge that an authenticated HTTPS reverse proxy is responsible for access control and TLS. Documentation supplies Nginx, Caddy, and Traefik examples.

Proxy examples must preserve streaming behavior for large uploads, avoid request buffering where supported, set an explicit maximum request size compatible with ReachCommander's upload limits, use adequate upstream timeouts for long operations, forward standard proxy headers, and expose the application at one origin. The documentation states that HTTPS is also required for installing the PWA outside `localhost`.

The installer does not request proxy credentials or modify existing proxy configuration.

## Failure behavior

- Missing prerequisites stop before any persistent change.
- Invalid or dangerous source paths are rejected before Compose generation.
- JSON/YAML serialization or validation failure leaves only the temporary staging directory, which is reported and safely removable.
- Image-pull failure leaves the existing deployment untouched.
- Port conflicts and unhealthy startup trigger rollback during updates.
- Initial-install startup failure leaves validated configuration and diagnostics but removes the failed container; source paths remain untouched.
- Interrupt handling removes only installer-owned temporary files and never a source path.
- Concurrent management commands are serialized with an installer-owned lock under `/opt/reachcommander/state`.

## Testing strategy

### Static checks

- ShellCheck all shipped shell scripts.
- Verify executable modes, shebangs, strict-mode setup, and absence of unexpanded release placeholders.
- Parse generated JSON and run `docker compose config` against representative source names and paths, including spaces.

### Installer behavior

A dependency-free shell test harness places fake `docker`, `curl`, and filesystem commands at the front of `PATH` and uses a temporary install root. Tests cover:

- first installation with mixed RO/RW sources;
- source-ID normalization and collisions;
- dangerous-path rejection, including symlink resolution;
- UID/GID defaults under `sudo`;
- invalid port and occupied-port behavior;
- Compose/JSON generation equivalence;
- prerequisite, pull, startup, and health-check failures;
- update no-op, successful update, explicit version rollback, and automatic rollback;
- management-command locking;
- uninstall preservation of every source directory and ancestor.

Test-only path overrides are accepted only by unprivileged test entry points and are not honored by the installed root command.

### Container and release checks

- Keep all existing .NET, Angular, PWA, publish-layout, and Playwright checks.
- Build and run an amd64 release image and verify `/health` before publication.
- Inspect the pushed manifest for the required `linux/amd64` and `linux/arm64` runnable platforms while allowing non-runnable attestation descriptors.
- Verify `edge`, stable, semantic, and prerelease tag policy from event fixtures.
- Verify release archive contents and `SHA256SUMS`.
- Verify image source/version labels, SBOM, and provenance attestations.

## Documentation

The README gains a short install path and links to a dedicated Ubuntu deployment guide. The guide covers prerequisites, checksum verification, interactive installation, existing reverse-proxy integration, RO/RW permissions, updates, rollback, diagnostics, uninstall, GHCR channels, and recovery from an unhealthy update.

The project must show both the safe download flow and a clearly marked convenience command. The recommended flow downloads the release archive and `SHA256SUMS`, verifies with `sha256sum --check`, extracts it, allows inspection of `install.sh`, and then runs it with `sudo`. Documentation must not recommend piping an unversioned script directly into a root shell.

## Operational limits and scaling

At 10x the current installation volume, GHCR and GitHub-hosted CI absorb distribution load; the main cost is multi-platform build time. Buildx caching is sufficient. At 100x, supportability depends more on versioned diagnostics and upgrade compatibility than on installer throughput. The installer remains local and stateless, so no control-plane service is required.

The hard-to-reverse decisions are the public image name, tag semantics, install directory, management command name, and generated configuration contract. They are fixed in this design. Automatic Docker installation, a package repository, remote administration, and built-in authentication remain deferred because each would materially expand the trust and maintenance boundary.

## Acceptance criteria

The design is complete when all of the following are demonstrated:

1. A clean Ubuntu amd64 or arm64 host with Docker and Compose can install ReachCommander without cloning or building the repository.
2. The generated service binds to loopback, becomes healthy, and exposes configured sources with correct RO/RW policy.
3. A stable release publishes the documented semantic tags, an amd64/arm64 manifest, SBOM, provenance, installer archive, and checksums.
4. A master build publishes only `edge`; a prerelease never moves `stable`.
5. Updates use digest-pinned deployments and automatically return to the previous healthy digest on failure.
6. Re-running the installer does not silently overwrite an existing deployment.
7. Uninstall and every failure path leave mounted source directories unchanged.
8. Documentation explains the required authenticated HTTPS reverse proxy and PWA secure-context boundary.
