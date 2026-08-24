# ReachCommander One-Command macOS Installer Design

**Date:** 2026-08-24

**Status:** Approved

**Target:** Public, self-hosted ReachCommander installation on macOS through Docker Desktop

## Problem

ReachCommander already publishes a Linux container for `linux/amd64` and `linux/arm64`, so Docker Desktop can run it on Intel and Apple Silicon Macs. The repository does not, however, give a Mac user a guided installation path. A user must currently understand Docker Compose, bind mounts, persistent application state, network binding, and ReachCommander source configuration.

The desired experience is one interactive command that installs and starts ReachCommander without cloning or building the repository. The installer must support broad drives and narrowly selected folders, preserve each source's read-only or read/write policy, and avoid pretending that the Linux container is a native macOS application.

## Goals

- Install and start ReachCommander from one command on supported Intel and Apple Silicon Macs.
- Reuse the existing published Linux container and Docker Desktop runtime.
- Require Docker Desktop and Docker Compose rather than installing or modifying them.
- Offer whole-drive and specific-folder source selection.
- Configure every selected source explicitly as read-only or read/write.
- Offer Mac-only and local-network access modes.
- Persist generated deployment configuration and ReachCommander account data outside the container.
- Make a repeated installation safe and idempotent.
- Preserve a working installation when an image update fails.
- Provide actionable prerequisite, path-permission, port, startup, and health-check errors.
- Keep the installer unprivileged and avoid protected macOS locations.

## Non-goals

- A native macOS `.app`, native .NET service, SwiftUI client, menu-bar application, or Finder extension.
- Native Mac hardware telemetry. Hardware data continues to describe the Linux environment visible inside Docker Desktop.
- Installing Docker Desktop automatically through Homebrew or another package manager.
- Requesting administrator privileges or modifying system-wide shell configuration.
- Code signing, notarization, the Mac App Store, or a commercial updater.
- Supporting iOS in this milestone.
- Configuring a router, public DNS, TLS, reverse proxy, VPN, or public-internet exposure.
- Mounting the macOS system volume root or protected system directories.

## Recommended architecture

The installer is a small macOS-specific deployment adapter around the existing container:

```text
one-line bootstrap
  -> deploy/macos/install.sh
  -> prerequisite and path validation
  -> generated per-user Compose deployment
  -> existing multi-architecture GHCR image
  -> existing Angular PWA + ASP.NET Core API
```

No application service, API, database, or frontend behavior is forked for macOS. Docker Desktop selects the correct `linux/amd64` or `linux/arm64` image variant automatically. The backend remains Linux-hosted inside Docker Desktop, while selected Mac directories are exposed only through explicit bind mounts.

The installer source lives under `deploy/macos/`. Installer tests live under `tests/installer/macos/`. macOS deployment documentation lives at `docs/deployment/macos.md` and is linked from the root README.

## Public installation command

The README presents this convenience command:

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/dragosniamtu/reach-commander/master/deploy/macos/install.sh)"
```

The script is interactive and never invokes `sudo`. Because the command executes a script from the network, the documentation also provides an inspect-first alternative that downloads the script to a temporary file, displays its repository location, and lets the user review it before execution. The convenience command is not described as cryptographically pinned.

The script verifies that it is running on macOS before doing anything persistent. It then verifies the Docker CLI, Docker Compose v2, and a responsive Docker Desktop engine. If Docker Desktop is absent or stopped, it prints the official installation/start instructions and exits without creating a partial deployment.

## Generated per-user deployment

Installer-owned state is stored under:

```text
~/Library/Application Support/ReachCommander/
├── .env
├── compose.yaml
├── config/
├── data/
├── state/
└── backups/
```

The exact container paths follow the existing release-container contract. Generated Compose and source configuration are derived from the same validated source model so mount permissions and ReachCommander source permissions cannot drift.

The directory contains operational configuration and application-owned state only. User-selected source folders remain in their original locations and are never copied, moved, recursively re-permissioned, or deleted by the installer.

The host `data/` directory is bind-mounted read/write at `/data`. It retains the salted account record at `data/auth/account.json` and the ASP.NET Core Data Protection key ring under `data/keys`. The generated `config/sources.json` is mounted read-only through `/config`. Recreating or updating the container therefore preserves the account and valid sessions. A fresh deployment with no account state continues to show ReachCommander's existing first-run account-creation flow. The installer never asks for, generates, logs, or embeds a username, password, password hash, or JWT secret in the container image.

Files are created with per-user permissions. The installer does not create a system-wide command, modify `.zshrc`, or add directories to `PATH`. At completion it prints copyable, correctly quoted Docker Compose commands for status, logs, start, stop, and update.

## Source-selection flow

The installer shows:

```text
What should ReachCommander access?

1. Whole drives
   Advanced — broad access can expose or modify many files.

2. Specific folders (Recommended)
   Select one or more folders such as ~/Pictures, ~/Movies,
   or /Volumes/Media.
```

### Whole drives

The installer enumerates only presently available, user-accessible choices:

- the current user's home as the internal Mac's user-data area; and
- each eligible external volume mounted directly under `/Volumes/<name>`.

It does not mount `/`, `/System`, `/Library`, `/private`, `/usr`, `/bin`, `/sbin`, `/dev`, Docker Desktop internals, or the Docker socket. It mounts each selected external volume independently rather than mounting `/Volumes`, so connecting a new drive later does not grant it access implicitly.

The internal whole-drive choice explicitly warns that the current user's home can contain hidden credentials and private application data. Whole-drive read/write access requires a second confirmation naming the exact canonical path. Specific folders remain the recommended option.

### Specific folders

The user can add multiple existing directories. The installer expands a leading `~`, resolves each directory to a canonical absolute path, rejects duplicate or nested duplicate sources that would create ambiguous roots, and preserves spaces and Unicode characters without shell evaluation.

For every whole drive or specific folder, the user must choose one access policy:

- **Read-only:** the Compose bind mount and ReachCommander source are both read-only.
- **Read/write:** the Compose bind mount and ReachCommander source are both writable.

The installer generates a stable source identifier and a human-readable display name. Name collisions are resolved interactively before configuration is written. Host paths are serialized as data and are never interpolated into executable shell fragments.

Docker Desktop can require explicit file-sharing or macOS privacy permission for a selected location. The installer validates actual container access before committing a new deployment and reports the rejected path with instructions for granting Docker Desktop access. It never attempts to bypass macOS privacy controls.

## Network-access flow

The installer shows:

```text
Who can access ReachCommander?

1. This Mac only (Recommended)
   http://localhost:8080

2. Devices on the local network
   Authentication is required.
```

Mac-only mode publishes the container port on `127.0.0.1`. Local-network mode publishes it on all Mac interfaces and prints both the localhost URL and a best-effort local IPv4 URL. The installer clearly states that local-network mode increases exposure and relies on ReachCommander's server-side authentication.

The default port is `8080`. Before writing configuration, the installer detects whether that host port is already listening. If occupied, it explains the conflict and prompts for another valid unprivileged port; it does not terminate or reconfigure the existing process.

The installer does not configure public-internet access. Documentation recommends a trusted HTTPS reverse proxy or VPN for any access beyond a trusted local network and reiterates that a PWA requires HTTPS outside localhost.

## Installation and update transaction

For a first installation, the script performs these steps in order:

1. Validate macOS and all prerequisites without persistent changes.
2. Collect source, access-policy, network-mode, and port selections.
3. Canonicalize and validate every selected source.
4. Generate configuration in an installer-owned temporary directory.
5. Validate the generated source data and run `docker compose config`.
6. Pull the default published `stable` image and resolve its immutable digest.
7. Preflight the selected bind mounts through Docker Desktop.
8. Move the validated deployment into the application-support directory.
9. Start ReachCommander and poll its bounded health check.
10. Print the endpoint and management commands without opening a browser.

If a deployment already exists, the installer recognizes it and offers update, reconfiguration, or exit rather than overwriting it silently. Update retains source and network configuration, resolves the newest `stable` digest, saves the previous digest, recreates the service, and waits for health. An unhealthy update restores the prior digest and recreates the previous service. Reconfiguration is staged and validated before it replaces the current generated files.

Backups are bounded and contain only installer-owned configuration and state metadata. Source contents are never included. Concurrent installer runs use an installer-owned lock inside the application-support directory.

## Failure behavior

- Unsupported operating systems stop immediately with no writes.
- Missing Docker, missing Compose, or a stopped engine stops before deployment creation.
- Invalid, missing, duplicate, dangerous, or inaccessible paths return to source selection with a precise message.
- A Docker Desktop sharing or privacy failure names the affected path and leaves source permissions unchanged.
- A port conflict prompts for a different port and never stops the conflicting process.
- Network or image-pull failure leaves an existing deployment untouched.
- Invalid generated Compose or source configuration is never installed.
- Initial unhealthy startup retains the validated configuration for diagnosis, removes only the failed installer-owned container, and prints bounded logs.
- Failed update or reconfiguration restores the last valid generated deployment and image digest.
- Interrupt handling removes installer-owned temporary files or restores a journaled replacement; it never operates recursively on a selected source.
- Error messages avoid account data, tokens, private filenames inside mounted sources, and unbounded container logs.

## User-facing completion output

Successful installation prints:

- the local URL and, when selected, the best-effort LAN URL;
- confirmation that first-run account creation happens in the browser;
- every configured source's display name and `RO` or `RW` policy;
- copyable status, logs, start, stop, and update commands;
- the persistent configuration directory;
- a reminder that the installer did not configure public HTTPS access; and
- Docker Desktop file-sharing guidance for removable drives.

The installer does not launch a browser or Docker Desktop automatically.

## Testing strategy

### Static and generation tests

- Run `bash -n` and ShellCheck on the installer and its shell fixtures.
- Validate strict shell mode, quoting, temporary-directory cleanup, and absence of `sudo`.
- Generate and validate Compose/source configuration for spaces, quotes, Unicode, multiple sources, and mixed `RO`/`RW` policies.
- Assert that user-controlled paths are serialized as data and cannot inject shell commands or Compose keys.

### macOS installer tests

A dependency-light test harness places fake `docker`, `curl`, networking, and volume-discovery commands first in `PATH`. A GitHub-hosted macOS runner covers:

- Intel and Apple Silicon architecture responses;
- missing and stopped Docker Desktop;
- whole-drive and specific-folder flows;
- current-user home and independently selected external volumes;
- read-only and read/write confirmations;
- Mac-only and LAN bindings;
- occupied and alternative ports;
- Docker file-sharing failures;
- first installation, repeated installation, update, reconfiguration, and rollback;
- interruption and lock handling; and
- paths containing spaces and Unicode characters.

The mocked macOS tests do not claim to run Docker Desktop on a hosted runner. Existing Linux container CI remains responsible for building and health-checking the real image and for verifying its `linux/amd64` and `linux/arm64` manifest. A Compose-generation contract test validates the emitted deployment with a real Docker Compose CLI where CI provides one.

### Manual release smoke test

Before advertising a new installer release, the documented smoke test runs on Docker Desktop and verifies:

1. a clean first installation;
2. first-run account creation and subsequent login;
3. one read-only and one read/write source;
4. a path containing spaces;
5. folder listing and a permitted write operation;
6. denial of a write operation on the read-only source;
7. container recreation with preserved account data;
8. Mac-only binding and optional LAN access; and
9. update followed by a healthy restart.

## Documentation

`docs/deployment/macos.md` documents prerequisites, the one-command and inspect-first installation paths, Docker Desktop folder-sharing permissions, both source modes, both network modes, first-run authentication, updates, logs, start/stop commands, configuration backup, and safe removal of installer-owned files.

The README describes macOS support accurately as **Docker Desktop deployment**, not a native Mac application. It also states that hardware metrics reflect the Linux container/VM view and that native Mac and iOS applications remain possible future products.

## Acceptance criteria

1. A Mac with Docker Desktop can install and start ReachCommander using one command without cloning or building the repository.
2. The same installer works on Intel and Apple Silicon by consuming the existing multi-architecture image.
3. Users can choose whole-drive or specific-folder access and explicitly set each source to read-only or read/write.
4. The installer never mounts `/`, protected macOS system paths, `/Volumes` as a dynamic parent, or the Docker socket.
5. Mac-only mode binds to loopback; LAN mode is explicit and retains server-side authentication.
6. Generated source permissions match Docker bind-mount permissions.
7. Account and configuration state survive container recreation and image updates.
8. Re-running the installer does not silently overwrite or reset an installation.
9. A failed update returns to the previous healthy image without changing source contents.
10. CI validates the installer logic on a macOS runner, while existing container CI validates the real image platforms and health.
11. No installation, error, update, or cleanup path deletes or recursively changes a user-selected source.
12. Documentation clearly distinguishes Docker-based macOS support from a future native commercial application.
