# ReachCommander Ubuntu System Update Design

**Status:** Approved design
**Date:** 2026-08-25
**Scope:** Automatic update discovery and administrator-triggered full-stack updates for installer-managed Ubuntu deployments

## Summary

ReachCommander will add an update control immediately beside the system-metrics control in the top toolbar. The backend automatically checks the deployment's configured update channel. The control remains disabled while the deployment is current and becomes enabled only after a newer trusted image has been resolved. Selecting it opens a confirmation dialog and, after confirmation, updates the complete ReachCommander container: ASP.NET Core backend, Angular frontend, archive worker, and packaged runtime assets.

The application container must not receive the Docker socket or general host-command access. A root-owned, narrowly scoped Ubuntu `systemd` updater service performs discovery and invokes the existing digest-pinned, health-checked update transaction. The service accepts only fixed `check` and `applyConfiguredChannel` operations over a protected Unix socket. It cannot run browser-supplied commands, images, tags, channels, paths, or URLs.

Update discovery is automatic. Applying an update is deliberately administrator-confirmed because it briefly restarts ReachCommander. Unattended installation is outside this version.

## Goals

- Check for complete ReachCommander updates automatically on supported Ubuntu installations.
- Follow the channel already selected by the installer or `reachcommander update` command.
- Enable the toolbar update control only when a newer verified target is available.
- Update the backend and bundled frontend together as one immutable container image.
- Reuse the existing deployment lock, exact-digest state, health check, backup, and rollback behavior.
- Survive the application-container restart and report the final update or rollback result after reconnection.
- Keep Docker control, host root privileges, and repository selection outside the web application.
- Preserve active file data, authentication state, configured sources, operation metadata, and source-local managed Trash.
- Provide deterministic tests for discovery, update lifecycle, failure recovery, security boundaries, UI states, and installer migration.

## Non-goals

- macOS, Windows development, native Windows, or manually composed Docker deployments.
- Mounting `/var/run/docker.sock` into the ReachCommander container.
- Accepting a version, image, channel, repository, URL, command, or filesystem path from Angular.
- Updating arbitrary host packages, Docker Engine, the operating system, configured source contents, or installer-owned updater binaries.
- Automatically installing updates without administrator confirmation.
- Switching channels from the browser. Channel changes remain an explicit host-administration action.
- Updating exact `vX.Y.Z` pins. A pinned deployment remains pinned until the administrator changes its channel from the host.

## Existing foundation

The Ubuntu installer already persists the configured channel and current immutable image under `/opt/reachcommander/state`. The installed `reachcommander update [stable|edge|vX.Y.Z]` command resolves only the fixed GHCR repository, records the exact digest, backs up deployment state, starts the candidate, waits for health, and restores the previous deployment if the candidate is unhealthy. A root-owned lock serializes install, update, reconfigure, and uninstall operations.

The new system builds on that transaction rather than duplicating image replacement in ASP.NET Core. The existing PWA update notice remains responsible for browser-only service-worker updates. A full-stack update coordinates with it only after the replacement backend is healthy.

## Supported deployment states

| Environment | Update-control behavior |
|---|---|
| Installer-managed Ubuntu, `stable` | Check GitHub's newest stable release and its GHCR digest. |
| Installer-managed Ubuntu, `edge` | Compare the published GHCR `edge` digest and revision. |
| Installer-managed Ubuntu, exact `vX.Y.Z` | Disabled: `Updates disabled while version-pinned.` |
| Ubuntu container without the updater service | Disabled with installation guidance. |
| Windows development or native process | Disabled: unsupported in this version. |
| Docker Desktop on Windows or macOS | Disabled: unsupported in this version. |
| Source-run Angular/API development | Disabled: unsupported in this version. |

Capability is detected through a successful versioned updater handshake, never through browser-supplied environment claims.

## Update identity and comparison

The installed state gains an atomic `state/current-version` record. A stable value is the validated release tag, such as `v1.4.0`. An edge value includes the immutable image revision suitable for display, such as `edge@1a2b3c4d5e6f`; the digest remains the authoritative identity.

For existing installations that predate `current-version`, the updater inspects the trusted current image's OCI labels. If a display version cannot be derived safely, it reports `unknown` while continuing to compare immutable digests. The next successful update writes a complete version record.

Discovery follows these rules:

1. `stable` requests the latest non-draft, non-prerelease release from the fixed repository `dragosniamtu/reach-commander`.
2. The returned tag must match stable `vX.Y.Z` syntax.
3. The updater resolves only `ghcr.io/dragosniamtu/reach-commander:<validated-tag>` to an immutable digest.
4. `edge` resolves only `ghcr.io/dragosniamtu/reach-commander:edge` and reads its OCI revision label for display.
5. Availability is determined by target digest inequality, not by string ordering alone.
6. Exact version channels do not perform floating-target discovery.

GitHub supplies release metadata; GHCR supplies the deployable manifest. No GitHub credential is required for the public repository. Network failures produce an unavailable/error state and never enable Apply using unverified metadata.

## Host updater service

The Ubuntu bundle adds a minimal updater executable plus a `reachcommander-updater.service` unit. The installer creates a protected runtime directory and Unix socket that only root and the configured ReachCommander runtime identity can access. The socket is bind-mounted into the application container; the Docker socket is not.

The updater has a small, versioned, bounded JSON protocol:

- protocol version `1`;
- action `check` with a generated request identifier;
- action `applyConfiguredChannel` with a generated request identifier;
- bounded request and response sizes;
- fixed timeouts;
- one apply transaction at a time;
- no extensible command or argument field.

`check` returns capability, channel, current identity, target identity, availability, last successful check, current update operation, and a stable public error code when applicable. `applyConfiguredChannel` accepts no target details. It re-reads trusted installer state, repeats discovery, and invokes the installed management command from a fixed path with a sanitized environment.

The service journals update state atomically beneath `/opt/reachcommander/state`. The journal includes an opaque operation ID, phase, timestamps, current/target public identities, and sanitized outcome. It contains no administrator credentials, socket secrets, source paths, or command text. Because the journal and updater live on the host, they remain available while the old container stops and the new container starts.

The service uses the existing global coordination lock. Concurrent Apply requests return the current operation. Install, reconfigure, update, and uninstall remain mutually exclusive.

## Host hardening

The updater runs as root only because the existing update transaction needs Docker and installer-owned deployment access. Its authority is constrained by design and unit hardening:

- fixed executable and install-root paths;
- fixed repository allow-list;
- fixed actions and channel read from protected host state;
- no shell interpolation of request data;
- no browser-controlled environment values;
- restrictive socket and state permissions;
- bounded I/O, output capture, and timeouts;
- systemd filesystem and kernel hardening compatible with Docker access;
- sanitized logs that exclude physical source paths and account material;
- protocol-version negotiation with fail-closed behavior.

A compromised ReachCommander process could request an update of its configured trusted channel, but it could not select an arbitrary image or gain general Docker control. This is the intentionally narrow privilege delegated by the feature.

## Backend architecture

The API gains an `ISystemUpdateGateway` boundary implemented by a Unix-socket client on supported Ubuntu installations and by an unavailable implementation elsewhere. A `SystemUpdateCoordinator` owns cached public state, scheduled checks, Apply serialization, operation draining, and recovery after restart.

The backend checks:

- immediately after startup once authentication and background-operation recovery are ready;
- after authenticated application initialization when the cached result is missing or stale;
- every six hours after a successful check;
- with bounded retry backoff after a network or updater failure.

Only one check executes at a time. Browser count does not multiply GitHub or GHCR requests.

Authenticated endpoints:

```text
GET  /api/system-update
POST /api/system-update/check
POST /api/system-update/apply
```

`GET` returns cached state. `check` requests a fresh check and is rate limited. `apply` requires antiforgery validation and returns `202 Accepted` with the host operation ID before the container restart begins. All endpoints require the built-in administrator session.

The public response contains only:

- support and protocol compatibility;
- channel and pinned state;
- current and target display versions and digests shortened for display;
- update phase;
- availability and eligibility;
- last-check and operation timestamps;
- rollback outcome;
- stable public reason/error codes.

Complete digests may be retained server-side but are not needed by Angular. Physical host paths, Docker output, stack traces, and raw GitHub responses are never returned.

## Operation draining and mutation safety

An update cannot start while Copy, Move, Trash, Restore, Empty Trash, long permanent Delete, or archive extraction is queued or active. The toolbar and dialog expose this reason before confirmation. The Apply endpoint revalidates it authoritatively.

After Apply is accepted, the backend enters a short maintenance-drain state before asking the host updater to replace the container. New mutating API requests receive a sanitized `503 update_in_progress`; read-only requests remain available until shutdown. In-flight request-scoped mutations must reach their existing atomic boundary before the host update begins. If draining cannot complete within the bounded timeout, Apply is cancelled before container replacement and normal API service resumes.

This gate closes the race between the UI eligibility check and the host update request. It does not cancel user file operations automatically.

## Angular state and toolbar behavior

A focused `SystemUpdateStore` consumes the cached API state and polls only while an update operation is active or the server is reconnecting. It does not talk to GitHub or GHCR directly.

The update control appears immediately to the left of `SystemMetricsWidget`. Its accessible states are:

- `Checking for updates` — disabled, progress treatment;
- `ReachCommander is up to date` — disabled;
- `Update available: <target>` — enabled with an accent indicator;
- `Updates disabled while version-pinned` — disabled;
- `System updates unavailable: <reason>` — disabled;
- `Update waiting for operations to finish` — disabled;
- `Updating ReachCommander` — disabled, progress treatment;
- `Previous version restored after update failure` — disabled error/rollback treatment;
- `Update requires administrator attention` — disabled critical treatment.

Because disabled buttons are not consistently focusable, the control uses an accessible wrapper that exposes its status and tooltip to keyboard and assistive-technology users. Both existing themes, the compact toolbar, narrow PWA layouts, reduced motion, and high-contrast focus treatment are preserved.

## Confirmation and restart experience

Selecting an available update opens a modal that shows:

- current version;
- target version;
- configured channel;
- a brief server-restart warning;
- confirmation that authentication data and source configuration remain mounted outside the image;
- confirmation that an unhealthy candidate triggers automatic rollback.

The dialog submits nothing when cancelled or closed. Apply is disabled if the authoritative state becomes stale or background work begins.

After acceptance, Angular shows a full-screen update state. The initial API request may disconnect when Docker stops the old container; that disconnect is expected. The browser retries a lightweight authenticated status request with bounded backoff. After the replacement backend is healthy, it reads the host journal through the updater service:

- `completed`: ask the existing Angular service worker to check and activate the matching shell, then reload exactly once;
- `rolledBack`: keep the restored application loaded and show the rollback result;
- `failedNeedsAttention`: stop automatic retries and show `reachcommander doctor`/logs guidance without exposing host output;
- still running: continue reconnect polling within the bounded window.

The administrator session normally survives because account data and Data Protection keys remain in `/data`. If the session cannot be restored, the login screen appears normally and update status is available after authentication.

## Error handling

Stable public error codes distinguish:

- updater unavailable or incompatible;
- version-pinned deployment;
- check rate limited;
- GitHub release unavailable or invalid;
- GHCR manifest unavailable or invalid;
- active operations block Apply;
- update already in progress;
- host coordination lock busy;
- candidate unhealthy and rollback completed;
- rollback failed and administrator attention required;
- stale or malformed updater journal.

Discovery errors disable Apply and retain the last successful check timestamp for diagnosis. Retries use bounded exponential backoff and never create concurrent checks. A failed candidate may be offered again only after a new successful check or explicit Check request; the UI never loops Apply automatically.

## Installer and upgrade migration

The Ubuntu release bundle adds the updater executable, systemd service definition, socket/runtime-directory setup, protected state files, and the Compose Unix-socket mount. Installation and reconfiguration validate all permissions and service health before committing.

An existing ReachCommander installation must run the new release installer once to install this host component. Merely pulling a newer application image cannot safely create a root-owned systemd service. Until migration is complete, the toolbar reports that host update support is unavailable and links to the Ubuntu upgrade instructions.

The updater protocol is versioned because application containers can advance independently of the installed host helper. An incompatible helper fails closed and instructs the administrator to refresh the installer bundle. The initial version does not self-update the host helper.

Uninstall stops and removes the updater service, socket, and updater-owned state while preserving the existing documented source and managed-Trash boundaries. Interrupted updater transactions remain subject to the existing doctor and recovery contracts.

## Documentation

The README and Ubuntu deployment guide will document:

- automatic checks versus administrator-confirmed Apply;
- supported and unsupported environments;
- stable, edge, and exact-version behavior;
- the one-time migration required for existing installations;
- service status and logs;
- network requirements for GitHub and GHCR;
- update/rollback outcomes and `reachcommander doctor` guidance;
- the Unix-socket privilege boundary and absence of a Docker-socket mount;
- authentication/data preservation and backup expectations.

## Test strategy

### Host and installer contracts

- Installer packages and installs the updater files with exact ownership and modes.
- Existing installations migrate atomically and roll back if the service/socket cannot start.
- The updater rejects unknown protocol versions, actions, extra target fields, oversized messages, malformed JSON, concurrent Apply, untrusted state, and symlinked state paths.
- Stable discovery validates GitHub release metadata and the matching GHCR digest.
- Edge discovery compares digest/revision; exact versions remain pinned.
- Fixed paths, sanitized environment, coordination lock, atomic journal, success, unhealthy candidate rollback, rollback failure, and interrupted recovery are deterministic under fake Docker/GitHub fixtures.
- ShellCheck and Ubuntu workflow contracts cover all installed shell components.

### Backend tests

- Unsupported gateways report explicit disabled states on Windows, macOS, Docker Desktop, and source development.
- Startup/six-hour/retry scheduling uses `TimeProvider` and coalesces concurrent callers.
- All API routes require authentication; Check and Apply require antiforgery and rate limiting.
- No request can provide a channel, image, repository, URL, command, or host path.
- Active file/archive operations block Apply; the maintenance gate rejects new mutations and drains safely.
- Restart recovery maps host journal phases into sanitized API state.
- No socket payload, API response, or normal log exposes physical paths or sensitive data.

### Angular tests

- Every toolbar state has the correct enabled state, accessible name, tooltip, and compact rendering.
- The button is immediately beside telemetry in both themes.
- Confirmation shows immutable current/target capture and rollback/restart information.
- Cancel and Escape submit nothing; stale/blocked state disables Apply.
- Expected disconnect, reconnect backoff, successful service-worker activation/reload, rollback, login recovery, and terminal failure each render correctly.
- Full Angular, production PWA, and Playwright suites remain green.

### CI and acceptance

- Ubuntu installer contracts run with fake GitHub/GHCR/Docker/systemd boundaries.
- Authenticated browser acceptance covers disabled/current/available/applying/reconnect/success/rollback states through a deterministic fake updater.
- Hardened container smoke verifies only the updater Unix socket is mounted and explicitly rejects a Docker-socket mount.
- Release publication remains gated on backend Windows/Ubuntu tests, frontend/PWA acceptance, installer contracts, and hardened container health.

## Acceptance criteria

- A supported `stable` or `edge` Ubuntu deployment discovers a different trusted digest without browser involvement.
- The toolbar update control is enabled only for a successfully resolved newer target.
- Exact version pins and unsupported environments remain visibly disabled with specific reasons.
- Only an authenticated administrator with valid antiforgery state can confirm Apply.
- The browser cannot select the channel, image, repository, command, URL, or path.
- Active operations prevent Apply; new mutations cannot race container shutdown.
- The application container never receives the Docker socket.
- A healthy update replaces backend and frontend together and reloads the matching PWA shell once.
- An unhealthy candidate restores the previous digest and reports rollback after reconnection.
- Authentication state, source configuration, file-operation metadata, and source-local Trash survive the update.
- Existing installations without the host helper fail closed and receive migration instructions.
