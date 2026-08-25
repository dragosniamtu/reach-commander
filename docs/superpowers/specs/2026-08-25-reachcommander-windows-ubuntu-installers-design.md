# ReachCommander Windows and Ubuntu One-Command Installers Design

**Date:** 2026-08-25

**Status:** Approved

**Target:** Public, self-hosted ReachCommander installation on Windows through Docker Desktop and on Ubuntu through Docker Engine

## Problem

ReachCommander now has a tested, unprivileged macOS Docker Desktop installer and a hardened Ubuntu release bundle. The user experience is still uneven: Windows has no guided production installer, while Ubuntu's secure release flow requires operators to assemble several download, verification, extraction, and privilege-escalation steps themselves.

The desired result is an equivalent guided installation experience on both platforms without inventing a second runtime architecture. Windows users should receive a per-user PowerShell installer. Ubuntu server operators should retain the existing root-owned deployment and management command, with a small convenience bootstrap in front of the checksum-verified release bundle. Both platforms must offer whole eligible drives or mounts and specific folders, keep source access policies explicit, persist authentication outside the image, and recover safely from failed or interrupted changes.

## Goals

- Provide a concise latest-stable installation command for Windows and Ubuntu without requiring a repository clone or local build.
- Provide a version-pinned, checksum-verified, inspect-first alternative for both platforms.
- Reuse the published multi-architecture Linux container and the hardened shared Compose template.
- Verify Docker and Docker Compose without installing, starting, or reconfiguring Docker.
- Install Windows per user without Administrator privileges.
- Preserve the existing system-wide, root-owned Ubuntu server model.
- Offer whole eligible local drives or mounts and specific folders.
- Default every source to read-only and require an explicit choice for read-write access.
- Require exact-path confirmation for broad read-write sources.
- Offer loopback-only and explicit local-network binding.
- Resolve discovery channels to immutable image digests.
- Preserve account data and Data Protection keys across container replacement, reconfiguration, and updates.
- Make installation, reconfiguration, update, repair, and uninstall transactional and recoverable.
- Keep configured source data outside every installer-owned mutation and deletion allowlist.
- Add platform-native CI contracts and make them publication gates.

## Non-goals

- Installing Docker Desktop, Docker Engine, Docker Compose, WSL, PowerShell, a reverse proxy, TLS, or firewall rules.
- A Windows MSI/MSIX package, Windows service, system-tray application, Start-menu shortcut, or PATH modification.
- An Ubuntu `.deb`, Snap, systemd service, or unattended configuration-management role.
- A shared cross-platform installer runtime that forces PowerShell onto Ubuntu.
- A containerized installer with Docker socket and broad host-filesystem access.
- Code signing, Authenticode, commercial update infrastructure, or native macOS/iOS applications.
- Changes to Angular, ASP.NET Core APIs, authentication behavior, storage formats, or runtime hardware metrics.
- Public-internet exposure. The installers stop at local or trusted-LAN access and document HTTPS requirements.

## Decisions

- Build platform-native thin adapters around the existing release-container contract.
- Windows uses Windows PowerShell 5.1-compatible syntax and also runs under PowerShell 7.
- Windows installation is unprivileged and per-user under `%LOCALAPPDATA%\ReachCommander`.
- Ubuntu remains root-owned under `/opt/reachcommander`, with `/usr/local/bin/reachcommander` as the management command.
- Both installers verify Docker but never install or reconfigure it.
- Both installers offer whole eligible local drives or mounts and specific folders.
- Operating-system roots, virtual filesystems, installer-owned paths, and dynamically expanding parent mount directories are not selectable.
- Network shares are excluded from whole-drive discovery. An explicitly mounted or shared specific directory may be accepted only after canonicalization and a real Docker preflight.
- Latest-stable bootstrap commands are convenience paths and are identified as mutable network bootstrap code. Pinned release bundles and their checksums are the auditable path.

## Considered approaches

### Platform-native thin installers — selected

Windows receives PowerShell scripts, Ubuntu keeps Bash and Python, and macOS keeps its Bash 3.2 installer. Shared configuration formats and behavioral contracts prevent drift without hiding path, permission, and privilege differences behind an abstraction that is difficult to audit.

### PowerShell 7 on Windows and Ubuntu — rejected

One language would reduce superficial duplication but would require PowerShell on an Ubuntu server and replace a mature, tested Bash installer. The dependency and migration risk are larger than the maintenance benefit.

### Installer container — rejected

A containerized installer would require the Docker socket and broad access to host paths to discover drives, write deployment files, and manage Compose. That expands the most sensitive trust boundary and makes native permissions and recovery harder to reason about.

## Recommended architecture

```text
Windows convenience or pinned bootstrap
  -> checksum-verified Windows release bundle
  -> deploy/windows/install.ps1
  -> per-user generated deployment
  -> shared Compose contract and immutable GHCR digest

Ubuntu convenience bootstrap or pinned release flow
  -> checksum-verified existing Ubuntu release bundle
  -> existing deploy/install.sh under sudo
  -> root-owned generated deployment and lifecycle command
  -> shared Compose contract and immutable GHCR digest
```

The platform scripts share these external contracts:

- `deploy/compose.release.yaml` as the hardened service template;
- the exact `.env` keys consumed by that template;
- application source configuration at `config/sources.json`;
- host-only mount metadata at `state/source-mounts.json`;
- `state/channel`, `state/current-image`, and `state/previous-image`;
- persistent `data/auth` and `data/keys` directories; and
- the published `ghcr.io/dragosniamtu/reach-commander@sha256:...` image format.

They do not share path-enumeration, permission, locking, or transaction internals. Those remain native and independently testable.

## Release and bootstrap model

### Windows

The release workflow publishes a Windows installer bundle containing the PowerShell installer, the shared release Compose template, license material, and version metadata. The bundle has a dedicated SHA-256 checksum asset so the existing Ubuntu `SHA256SUMS` contract does not become ambiguous.

The concise command downloads `deploy/windows/bootstrap.ps1` to a temporary file and executes it in a new process with `-NoProfile` and process-scoped `-ExecutionPolicy Bypass`. It does not alter the machine or user execution policy. The bootstrap runs without elevation, downloads the latest Windows release bundle and checksum, validates the exact expected checksum grammar, verifies the archive, extracts it to an installer-owned temporary directory, and invokes `install.ps1`.

The documentation labels this mutable bootstrap as a convenience path. The recommended audited path selects an exact `vX.Y.Z`, downloads the bundle and checksum separately, verifies both before extraction, lets the operator inspect `install.ps1`, and then executes it.

### Ubuntu

`deploy/ubuntu/bootstrap.sh` is a small unprivileged adapter. The concise command downloads it to a mode-`0700` temporary directory and executes it from disk rather than piping it into a shell. The bootstrap downloads the latest stable Ubuntu installer archive and `SHA256SUMS`, enforces the existing one-entry checksum grammar, validates the archive, extracts it, and only then invokes the verified `install.sh` with `sudo`.

The existing version-pinned flow remains the recommended audited server path. It downloads an exact release archive and checksum, verifies and extracts them, allows inspection, then grants root privileges only to the verified local installer. The bootstrap never installs Docker, changes apt sources, or performs privileged work before archive verification.

## Windows generated deployment

Installer-owned state is stored at:

```text
%LOCALAPPDATA%\ReachCommander\
  .env
  compose.yaml
  config\sources.json
  data\auth\
  data\keys\
  state\
  backups\
  bin\install.ps1
```

The installer requires an absolute local `%LOCALAPPDATA%` path and refuses an installer root that is a reparse point. It creates the root with an ACL scoped to the current user and `SYSTEM`, without requesting Administrator privileges. It does not modify `PATH`, the registry, PowerShell profiles, Start-menu entries, Windows services, or Windows Firewall.

The installed copy at `bin\install.ps1` is the local lifecycle entry point. Rerunning it detects the current state and offers update, reconfiguration, repair, uninstall, or exit. The bootstrap may be rerun later to use a newer installer implementation. Runtime image updates remain independently digest-pinned.

The `backups` directory inside the install root is reserved for bounded transaction and update rollback metadata. A destructive Windows uninstall backup is written outside the install root at `%LOCALAPPDATA%\ReachCommander Backups\<UTC timestamp>` with the same restricted ACL, then flushed and compared before original authentication files become removable. Both the install root and this external credential-backup root are masked or rejected when a selected broad source would expose them.

The Windows container identity is the fixed non-root UID/GID `1000:1000`. Docker Desktop translates Windows bind-mount access. Before commit, a temporary container verifies that `/data`, read-only sources, and read-write sources have the required effective access. Failure reports the affected host path and leaves Windows ACLs unchanged.

Generated JSON uses `ConvertTo-Json` with bounded depth and explicit UTF-8 output. Compose source mounts use long syntax and data-only serialization so drive letters, spaces, apostrophes, Unicode, and bracket characters cannot become PowerShell or YAML expressions.

## Ubuntu generated deployment

The existing locations and ownership model remain authoritative:

```text
/opt/reachcommander/
/usr/local/bin/reachcommander
/var/backups/reachcommander
/opt/.reachcommander.lock
```

The existing release installer, renderer, lifecycle command, root ownership, non-root container UID/GID selection, authentication allowlist, update rollback, doctor command, and uninstall behavior remain intact. The new work extends source collection with a whole-mount mode and adds the unprivileged bootstrap; it does not create a parallel Ubuntu installation format.

## Source selection

Both platforms show two choices:

```text
1. Whole eligible drives or mounts
   Advanced — broad access can expose or modify many files.

2. Specific folders (Recommended)
   Add one or more narrow folders.
```

Every selected source has a stable identifier, display name, canonical host path, isolated container path, and `RO` or `RW` access policy. Compose mount access and application source access are generated from the same in-memory model and must agree.

Read-only is the default. Read-write on a whole drive, mount, user-profile ancestor, or home ancestor requires typing the exact canonical path. The installer never changes source ownership, ACLs, Unix modes, or recursive permissions to make a preflight pass.

### Windows whole drives

PowerShell enumerates currently present local fixed and removable filesystem drives. It excludes the Windows system drive root, optical drives, disconnected drives, network-mapped drives, non-filesystem providers, Docker/WSL internals, and any root that overlaps installer-owned state. A drive is mounted independently; the installer never grants a dynamic parent that would include drives connected later.

### Ubuntu whole mounts

The installer uses `findmnt` from the already required `util-linux` package to discover currently mounted real filesystems. It excludes `/`, protected operating-system trees, pseudo-filesystems, Docker internals, `/opt/reachcommander`, the Docker socket, and a parent such as `/mnt` or `/media` that would implicitly grant future mounts. Each selected mount point is bound independently.

Local data mounts are eligible. Network filesystems are not offered in whole-mount mode. A network-backed directory may be entered explicitly in specific-folder mode and must pass the same canonical path, overlap, and container-access checks.

### Specific folders

The installer accepts only existing directories. It canonicalizes without wildcard expansion, rejects duplicate or nested duplicate roots, rejects installer-owned paths and children, and rejects a source whose path components traverse an unsafe symbolic link, junction, or reparse point. Spaces and Unicode are preserved as data.

If an approved broad source is an ancestor of installer-owned state, the rendered Compose model adds a nested exclusion mount over that descendant inside the source. The exclusion uses an installer-owned empty directory and is validated in `docker compose config` and the source preflight. If the platform/runtime cannot enforce the exclusion, the broad source is rejected.

## Network access

Loopback-only is the default:

- Windows: `127.0.0.1:<port>`
- Ubuntu: `127.0.0.1:<port>`

LAN mode binds to all host interfaces only after explicit selection. The installer prints a best-effort LAN URL and warns that ReachCommander authentication does not provide TLS. It does not open Windows Firewall, alter Ubuntu firewall rules, configure a router, or claim public-internet safety. Documentation recommends HTTPS through a trusted reverse proxy or VPN and explains that the PWA needs HTTPS away from localhost.

The selected unprivileged port is validated and checked for an existing listener. A conflict prompts for another port and never stops or reconfigures the existing process.

## Installation data flow

1. Validate the operating system, supported shell, architecture, Docker CLI, Compose v2, and responsive engine without persistent writes.
2. Detect a complete, partial, interrupted, or absent deployment.
3. Recover a valid interrupted installer transaction before offering another mutation.
4. Collect source mode, selected sources, access policy, network mode, and port.
5. Canonicalize paths and validate protected-path and overlap boundaries.
6. Resolve `stable` to an immutable image digest.
7. Render `.env`, Compose, application source JSON, and host-only mount metadata in an installer-owned temporary stage.
8. Validate JSON, validate `docker compose config`, and preflight `/data` and every source in a temporary container.
9. Journal the current generated allowlist and atomically commit the staged configuration.
10. Start ReachCommander and wait for the bounded health check.
11. Complete the transaction and print the URL, source policies, setup-code command, state location, and lifecycle commands without opening a browser.

The first run creates empty persistent authentication and key directories. ReachCommander's existing browser flow creates the administrator account. No installer asks for a username or password or stores credentials in the image, bootstrap, Compose environment, PowerShell history, or browser storage.

## Existing-installation lifecycle

Rerunning the installer recognizes a complete installation and offers:

- **Leave unchanged** — no writes and no container recreation.
- **Update** — resolve the saved channel or `stable`, stage the new digest, start it, and roll back automatically if unhealthy.
- **Reconfigure** — retain image and authentication state while replacing validated source/network configuration transactionally.
- **Repair** — recover an interrupted generated-file transaction or restart a validated stopped deployment; it never invents missing authentication state.
- **Uninstall** — remove only an explicit installer-owned allowlist after exact confirmation.

Ubuntu continues to expose routine `status`, `doctor`, `logs`, `start`, `stop`, `restart`, `update`, and `uninstall` operations through `reachcommander`. Windows prints equivalent Docker Compose commands and uses the installed PowerShell script for mutating lifecycle operations.

## Safety model

- Source directories are outside every mutation, backup-cleanup, rollback, and uninstall allowlist.
- No source path is recursively deleted, moved, copied, re-owned, re-ACLed, or re-permissioned.
- Windows path comparison is ordinal case-insensitive after canonicalization; Ubuntu comparison uses canonical absolute paths and filesystem/device validation.
- Windows rejects unsafe reparse points in installer-owned state and in the selected source's path chain. Ubuntu rejects symlinked installer paths and unsafe source traversal.
- Installer-owned authentication data is structurally allowlisted. Unexpected files, links, mounts, or malformed JSON fail closed without printing contents.
- Account data and the complete Data Protection key ring are preserved together across all non-uninstall operations.
- Runtime images are persisted only as exact GHCR digests. Channels are discovery metadata, not execution identifiers.
- The installer does not mount the Docker socket into ReachCommander or an installer container.
- Diagnostics are bounded and do not print passwords, stored setup state, account JSON, key contents, or filenames discovered inside sources.

## Transactions and failure behavior

Each platform uses a single-writer lock and an installer-owned transaction marker. The transaction contains only the generated-file allowlist, never source contents or authentication bytes.

- Unsupported prerequisites fail before deployment creation.
- Download or checksum failure leaves no release code eligible for privileged execution.
- Invalid source, drive, mount, port, JSON, Compose, or Docker-access validation fails before active-file replacement.
- Failure before commit leaves the active deployment byte-identical.
- Failure after commit restores the last generated configuration and healthy image digest.
- Initial unhealthy startup stops and removes only the failed container, retains validated generated configuration for diagnosis, and prints bounded logs.
- An interrupted operation is detected on the next run and recovered before a new action is accepted.
- Lock recovery validates stale ownership rather than deleting an active writer's lock.
- Update failure reports whether rollback is healthy; rollback failure is a distinct manual-recovery state.

Uninstall defaults to retaining authentication state in place. An optional destructive authentication removal requires a verified external backup and an exact confirmation. Ubuntu uses `/var/backups/reachcommander/<UTC timestamp>`; Windows uses `%LOCALAPPDATA%\ReachCommander Backups\<UTC timestamp>`. If validation, stop, backup, compare, flush, Compose teardown, or allowlisted removal fails, uninstall stops and preserves or attempts to restart the previous deployment. Configured sources are never uninstall targets.

## User-facing completion output

Successful installation prints:

- the localhost URL and, for LAN mode, the best-effort LAN URL;
- the command that shows the one-time first-run setup code;
- every source display name, canonical host path, and `RO` or `RW` policy;
- status, logs, start, stop, update, reconfiguration, and uninstall commands;
- the persistent installer-owned state location;
- Docker sharing guidance for unavailable paths; and
- a reminder that public HTTPS and firewall policy were not configured.

No installer launches a browser or Docker Desktop automatically.

## Testing strategy

### Windows contracts

Tests run on `windows-latest` in both Windows PowerShell 5.1 and PowerShell 7 where the contract depends on shell behavior. Explicit test-only roots prevent writes to real `%LOCALAPPDATA%`. Fake Docker, download, networking, listener, and drive-discovery commands cover:

- missing or stopped Docker Desktop;
- fixed/removable drive filtering and system/network-drive exclusion;
- specific folders, spaces, apostrophes, Unicode, drive-letter case, and reparse-point rejection;
- mixed read-only/read-write source rendering;
- exact broad read-write confirmation;
- loopback and LAN bindings and occupied ports;
- Compose and Docker source-preflight failures;
- first installation, no-op rerun, update, reconfiguration, repair, rollback, and uninstall;
- lock, interruption, stale-transaction, and partial-install handling;
- authentication and key preservation; and
- source canaries remaining byte-identical on every path.

PowerShell parsing and static analysis run independently of lifecycle tests. Release-contract tests verify that the Windows bundle is deterministic, excludes source/test/user data, and has an exact checksum asset.

GitHub-hosted Windows tests do not claim to run Linux containers through Docker Desktop. The shared real container and Compose template remain covered by the hardened Linux smoke job; Windows CI validates native path, serialization, transaction, and command behavior with controlled doubles.

### Ubuntu contracts

Existing installer suites remain authoritative and gain coverage for:

- convenience bootstrap download boundaries and exact release verification;
- no privileged execution before checksum success;
- whole local mount discovery and protected/pseudo/network mount exclusion;
- independently mounted selections rather than dynamic parents;
- whole-mount read-write confirmation;
- explicit mounted network directories in specific-folder mode;
- source masking when installer-owned state is below a broad source; and
- all existing install, reconfiguration, doctor, update, rollback, backup, and uninstall invariants.

The real Ubuntu container smoke continues to build the published runtime configuration, run it non-root, exercise health and authentication persistence, and verify the release image. ShellCheck, Bash syntax checks, Python tests, and release/documentation contracts remain required.

### CI publication gates

The publication dependency graph requires:

1. backend tests on Ubuntu and Windows;
2. frontend build, PWA, and browser acceptance;
3. macOS installer contracts;
4. Windows installer contracts;
5. Ubuntu installer and bootstrap contracts; and
6. hardened real-container smoke.

Only then may the workflow publish and verify the `linux/amd64` and `linux/arm64` manifest, SBOM, provenance, installer assets, and checksums.

## Documentation

`docs/deployment/windows.md` documents Docker Desktop prerequisites, the convenience and pinned installation paths, source sharing, both source modes, both network modes, first-run authentication, lifecycle actions, state backup, recovery, and uninstall.

`docs/deployment/ubuntu.md` keeps the existing pinned release path and adds the concise bootstrap, whole-mount selection, privilege boundary, and updated recovery behavior. The README and `deploy/README.md` compare the three Docker-based platform paths accurately and do not describe any of them as native desktop applications.

Documentation contracts require an inspectable saved-file flow and prohibit piping downloaded code directly into an elevated shell.

## What remains simple

- The application remains one containerized modular monolith with one deployment model.
- No installer service, telemetry backend, package repository, or updater daemon is added.
- Platform adapters share file formats and outcomes but not fragile internal abstractions.
- Scaling to more sources affects generated configuration size and preflight duration linearly; it does not change application architecture.
- At much larger operational scale, configuration management or native packages may become worthwhile, but they are deferred until there is actual demand.

## Risks and mitigations

- **Docker Desktop path translation differs from Linux:** use long-syntax mounts, JSON data serialization, native Windows tests, and real container preflight before commit.
- **Broad source access can expose private files:** default to specific folders and read-only, exclude system roots, and require exact confirmation for broad writes.
- **Mutable convenience bootstraps are less auditable:** keep them unprivileged until a release checksum passes and publish pinned inspect-first alternatives prominently.
- **Hosted Windows CI cannot validate Docker Desktop end to end:** keep the runtime contract shared, verify it through Linux smoke, and document a manual Docker Desktop release smoke checklist.
- **Platform implementations can drift:** enforce common schema, state, documentation, and lifecycle acceptance contracts in the publication graph.

## Manual release smoke

Before advertising a new installer release, the release checklist validates on Windows Docker Desktop and an Ubuntu Docker Engine host:

1. clean first installation;
2. first-run administrator creation and later login;
3. one read-only and one read-write specific source;
4. one eligible whole drive or mount;
5. a source path with spaces and Unicode;
6. denied writes on the read-only source and permitted writes on the read-write source;
7. container recreation with preserved account data and sessions;
8. loopback binding and optional LAN binding;
9. healthy update and forced unhealthy rollback;
10. interrupted-operation recovery; and
11. uninstall with source canaries unchanged.

## Acceptance criteria

1. A Windows user with Docker Desktop can install and start ReachCommander from one PowerShell command without Administrator privileges, cloning, or building.
2. An Ubuntu operator with Docker Engine can invoke one convenience command whose only privileged installer execution occurs after release checksum verification.
3. Both platforms provide a pinned, checksum-verified, inspect-first installation path.
4. Missing or stopped Docker produces actionable guidance and no partial deployment.
5. Windows state persists under `%LOCALAPPDATA%\ReachCommander`; Ubuntu retains the existing root-owned `/opt/reachcommander` model.
6. Both platforms offer whole eligible drives or mounts and specific folders.
7. System roots, virtual filesystems, network shares in broad mode, installer state, Docker internals, and the Docker socket are excluded.
8. Read-only is the default, and broad read-write access requires exact canonical-path confirmation.
9. Application source policy matches the corresponding Compose bind-mount policy.
10. Loopback is the default; LAN binding is explicit and does not alter firewall or TLS configuration.
11. Every installed image is persisted as an immutable GHCR digest.
12. Reconfiguration and updates preserve account data and Data Protection keys byte-for-byte.
13. A failed update automatically restores the last healthy digest and generated configuration when possible.
14. Interrupted operations are detected and recovered before a new mutation.
15. Uninstall removes only installer-owned allowlisted paths and defaults to retaining authentication data.
16. No install, error, rollback, backup, or uninstall path deletes or recursively changes a configured source.
17. Windows and Ubuntu installer contracts are required gates before release assets and container manifests are published.
18. Documentation distinguishes Docker-based support from future native applications and provides complete prerequisite, recovery, and security guidance.
