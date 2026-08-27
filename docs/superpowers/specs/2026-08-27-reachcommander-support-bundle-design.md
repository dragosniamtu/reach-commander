# ReachCommander sanitized update support bundle design

**Status:** Approved for implementation
**Date:** 2026-08-27
**Scope:** Ubuntu installer-managed update and deployment diagnostics

## Problem

The updater now exposes a safe event timeline, but diagnosing a long-running or failed update still requires an administrator to run several commands and manually assemble evidence. That is slow when the update overlay blocks the application, and raw service or Docker logs can disclose host paths, filenames, network addresses, image digests, configuration values, or credentials.

## Goals

- Let the currently signed-in administrator download diagnostics directly from the blocking update overlay while an update is running, stale, rolled back, or failed.
- Provide the same diagnostic evidence from `sudo reachcommander support-bundle` when the web application is unavailable.
- Include update and deployment-health facts sufficient to classify common failures without including raw logs or user data.
- Produce a temporary, one-time ZIP for the browser and retain no server-side bundle.
- Remain useful when individual host checks time out or the updater service is unavailable.
- Keep authentication, antiforgery, rate limiting, source confinement, updater socket isolation, and non-root application execution enabled.

## Non-goals

- Uploading bundles to GitHub, a vendor service, or any remote destination.
- Collecting general application logs, file-operation details, hardware telemetry, source names, source paths, or filenames.
- Including raw `journalctl`, Docker, Compose, process, environment, or configuration output.
- Retaining a bundle history on the server.
- Supporting Windows, macOS Docker Desktop, or manual-container host diagnostics in this increment.

## Selected architecture

The root-owned updater helper collects a strict structured snapshot containing only allowlisted fields and enumerated results. It does not return raw command output. The ASP.NET API validates the complete snapshot, combines it with safe application metadata, creates a bounded ZIP in memory, and streams it once to the authenticated browser. The host management command uses the same Python collector and writes a ZIP with the same five-entry content contract to standard output.

The existing Unix socket remains the only host boundary. The application container does not receive the Docker socket, host filesystem mounts, shell access, or arbitrary diagnostic command selection.

## Bundle contents

The ZIP contains exactly these UTF-8 files:

- `manifest.json`: bundle schema version, UTC generation time, ReachCommander public version, updater protocol version, update channel, current operation ID, and whether the host snapshot is complete or partial.
- `update-trace.json`: the existing sanitized operation trace with event codes, stages, outcomes, timestamps, elapsed durations, timeout state, and terminal result.
- `deployment-health.json`: named allowlisted checks with `healthy`, `warning`, `failed`, `timedOut`, `unavailable`, or `notApplicable` status and a fixed public reason code.
- `summary.txt`: a deterministic human-readable classification and safe next commands.
- `README.txt`: bundle schema, privacy exclusions, and instructions for sharing the file.

The bundle never includes credentials, cookies, tokens, authentication data, encryption keys, source identifiers, source names, source paths, filenames, file contents, IP or MAC addresses, hostnames, environment values, command lines, command output, image digests, registry responses, container identifiers, or arbitrary exception text.

## Diagnostic checks

The host collector reports only fixed status and reason codes for:

- Docker Engine availability;
- Docker Compose v2 availability;
- updater service state and socket readiness;
- management command and required deployment-file presence;
- update transaction and installer-reconfiguration marker state;
- source configuration structural validity and aggregate source accessibility;
- application-data structure, ownership, mode, and runtime accessibility;
- saved channel and public version-state validity;
- environment/current image consistency without returning either digest;
- ReachCommander container existence and Docker health;
- available disk-space band for the Docker data root and ReachCommander install root, expressed only as `sufficient`, `low`, or `critical`.

Every external command has a two-second deadline. Any output needed to classify a result is bounded, parsed locally, and neither returned nor persisted. Collection has a ten-second total deadline. A failed or timed-out check becomes evidence in the snapshot and does not abort the remaining checks.

## Protocol and compatibility

Updater protocol v4 adds the `collectDiagnostics` action and a separate exact diagnostic response schema. Status and apply responses retain their v3 shape. The ASP.NET gateway continues the existing v3-to-v2-to-v1 fallback for status and apply. Diagnostics never fall back to an older action; an older helper produces a partial application-side bundle with `hostSnapshotUnavailable` and installer-refresh guidance.

The host response is capped at 256 KiB, has a fixed maximum check count, uses exact field sets, and rejects duplicate or unknown fields. The .NET gateway revalidates every enum, timestamp, identifier, count, and string bound before packaging it.

Diagnostic collection is read-only, does not acquire the update mutation lock, and uses non-blocking trace reads. It must remain callable while an update worker is active or stalled.

## API and ZIP generation

`POST /api/system-update/support-bundle` accepts no body. It requires the existing authenticated administrator session and antiforgery header and receives a dedicated per-client rate limit. The endpoint always attempts to return a ZIP, including when the host helper is unavailable; in that case the ZIP is marked partial and contains the fixed gateway failure category.

The ZIP is created in bounded memory with deterministic entry names, no directory traversal, no external attributes, and a one-megabyte uncompressed-content limit. JSON uses the application's established camel-case enum contract. The response uses `application/zip`, `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, and a filename containing only a UTC timestamp. Memory is released when the response completes or the client disconnects; no bundle is written to host or container storage.

## CLI behavior

`sudo reachcommander support-bundle` emits ZIP bytes only to standard output. Progress and fixed errors use standard error. The command refuses to write binary bytes to an interactive terminal and prints this safe example:

```bash
sudo reachcommander support-bundle > reachcommander-support.zip
```

It accepts no path, operation ID, command, or log-range argument. The shell redirection is opened by the calling user, so the management command does not need to create or chown an output file. A collection with unavailable checks still exits successfully and records those statuses in the ZIP; only schema, safety, or ZIP-generation failures exit non-zero.

## Browser experience

The blocking update overlay shows **Download diagnostics** under Technical details. It remains enabled during applying, stale activity, rollback, failure, and administrator-attention states. Selecting it does not dismiss the overlay, cancel the update, reload the page, or wait for the update to finish.

The button shows a bounded `Preparing diagnostics…` state, prevents duplicate requests, and restores itself after success or failure. Success downloads the timestamped ZIP. Failure displays a concise message and the CLI fallback command. The control is keyboard accessible, screen-reader labelled, usable at the supported compact viewport, compatible with both themes, and static under reduced-motion preferences.

## Failure behavior

- A slow Docker, Compose, or systemd check is marked `timedOut`; the bundle still downloads.
- An unavailable or outdated helper yields a partial UI bundle plus installer-refresh guidance.
- A malformed helper response is discarded as untrusted and represented by a fixed protocol-incompatible status.
- Cancellation or browser disconnect stops packaging and retains no temporary artifact.
- Bundle collection never changes update state and never blocks update completion or rollback.
- CLI collection works directly from the installed host files even when the updater systemd service or application container is unavailable.

## Testing strategy

- Python tests cover exact schemas, allowlists, timeouts, non-blocking trace reads, partial results, malicious raw values, and the absence of prohibited data.
- Installer shell tests cover CLI arguments, TTY refusal, ZIP stdout purity, entry allowlist, permissions-independent redirection, packaging, and doctor/install preservation.
- .NET unit tests cover protocol-v4 request/response validation, bounds, malformed/untrusted snapshots, partial fallback, deterministic ZIP entries, maximum size, and cancellation.
- Integration tests cover authentication, antiforgery, empty-body enforcement, rate limiting, headers, valid ZIP content, and partial-bundle download.
- Angular and Playwright tests cover availability inside the blocking overlay, in-progress download, stale/failure states, duplicate suppression, success, failure guidance, accessibility, compact layout, and both themes.
- Full CI continues to cover Ubuntu ShellCheck, systemd verification, backend tests on Windows and Ubuntu, Angular unit/build/PWA tests, and browser acceptance.

## Acceptance criteria

1. A signed-in administrator stuck on the blocking update screen can download a diagnostic ZIP without waiting for, cancelling, or dismissing the update.
2. The bundle contains exactly the five documented entries and remains within one megabyte of uncompressed content.
3. Prohibited raw values do not appear in the host response, ZIP bytes, API errors, browser DOM, or CLI standard error.
4. An unavailable, outdated, slow, or partially failing host helper still results in a useful partial browser bundle.
5. `sudo reachcommander support-bundle > reachcommander-support.zip` creates the same schema without requiring the updater service or application container.
6. Collection is read-only, independently bounded, non-blocking with respect to the update worker, and leaves no retained bundle.
7. Existing updater protocol v1-v3 status/apply behavior and update rollback behavior remain unchanged.
8. The application container never receives Docker socket access or arbitrary host command execution.
