# ReachCommander update traceability and bounded execution design

**Status:** Approved for implementation
**Date:** 2026-08-27
**Scope:** Ubuntu installer-managed system updates

## Problem

An update can currently remain on the blocking update screen long after the host command's intended five-minute timeout. The updater launches the fixed `reachcommander update` command, streams its combined output on a background reader, kills only the wrapper process when the timeout expires, and then waits for the reader without a bound. A descendant such as the Docker client can retain the inherited output pipe, which leaves the reader and update worker alive and the durable journal in `applying` indefinitely.

The durable update journal records the latest logical stage but not the ordered command boundaries, activity timestamps, timeout handling, container identity verification, or elapsed time needed to diagnose a stall. Existing service logs are root-only and useful, but the browser cannot safely expose their raw Docker output, commands, paths, or application log content.

## Goals

- Make every installer-managed update end in `completed`, `rolledBack`, or `failed` within a bounded time.
- Record an ordered, timestamped trace showing where time was spent and which boundary failed.
- Show a bounded, sanitized version of the current operation trace to the authenticated administrator in the update overlay.
- Provide the detailed operational trace through `sudo reachcommander update-log` and `sudo reachcommander update-log --follow`.
- Retain the latest ten traces while enforcing a ten-megabyte total cap.
- Prove that the ReachCommander container was recreated with the expected image and became healthy; never restart the Docker daemon as part of an application update.
- Preserve exact protocol-v1 and protocol-v2 compatibility.

## Non-goals

- General Docker daemon monitoring or remediation.
- Browser access to raw process output, commands, host paths, registry credentials, environment variables, source configuration, authentication state, or application logs.
- Remote log export, telemetry collection, or a third-party logging dependency.
- Trace support for manual container, Windows, or macOS installations in this increment.
- Retrofitting a root-owned updater helper from inside the application container. The checksum-verified Ubuntu installer remains the authority that upgrades the helper and management command.

## Selected approach

Use a structured dual trace:

1. The root updater appends detailed, allowlisted events to a protected per-operation JSON Lines file.
2. The protected journal exposes a small sanitized trace projection through updater protocol v3.
3. The ASP.NET API validates and republishes only that fixed public projection.
4. Angular renders it under an expandable **Technical details** section with timestamps and elapsed time.
5. The management command reads the protected trace for root-only diagnosis.

Systemd-journal-only diagnostics were rejected because they do not explain progress in the browser and their retention depends on the host. Raw command-log exposure was rejected because it is noisy and can disclose sensitive deployment or application data.

## Host trace storage

Trace files live at:

```text
/opt/reachcommander/state/update-traces/<operation-id>.jsonl
```

The directory is root-owned mode `0700`; every trace is a regular, non-symlink root-owned file mode `0600`. Operation IDs are updater-generated UUIDs and filenames must match the exact UUID pattern. Reads and writes use no-follow, exclusive/append-safe file handling and reject unexpected file types.

Each event contains only a fixed schema:

- schema version;
- monotonically increasing sequence number;
- operation ID;
- UTC timestamp;
- elapsed milliseconds from operation start;
- allowlisted event code;
- optional allowlisted stage;
- outcome (`started`, `activity`, `succeeded`, `failed`, or `timedOut`);
- optional numeric exit code, timeout seconds, or bounded counters appropriate to that event;
- optional root-only diagnostic fields selected per event, such as the expected and running image identifiers.

Arbitrary environment values, command lines, filesystem paths, HTTP headers, registry output, source names, source contents, authentication data, and application logs are never written to these trace files. Unknown fields or event codes are rejected rather than passed through.

Trace persistence is observational: failure to append a non-terminal event must not change the update result. Failure to persist the terminal result remains visible in the root service journal. The updater always attempts a fixed service-journal warning without embedding raw values.

## Retention

The updater keeps at most ten operation traces in total, including the active trace, and at most ten megabytes across the trace directory. A single trace is independently bounded to one megabyte and a fixed maximum event count, so an active operation cannot consume the complete directory budget. Starting a new operation retains that active trace plus at most the nine newest terminal traces.

Pruning occurs at operation start and after terminal persistence. The active trace is never pruned. Only regular, non-symlink files matching the exact operation filename contract are candidates. Oldest terminal traces are removed first until both limits are satisfied. Unexpected entries stop pruning and produce a fixed root-service warning; they are never recursively deleted.

## Trace events

The root trace distinguishes these boundaries:

- operation accepted;
- trusted image resolution and download started;
- download activity heartbeat;
- trusted image resolved;
- deployment backup started and completed;
- deployment state installation started and completed;
- candidate container recreation started and completed;
- candidate running image verification started and succeeded/failed;
- candidate health check started, activity, and succeeded/failed;
- rollback state restoration started and completed/failed;
- previous container recreation started and completed/failed;
- previous running image verification succeeded/failed;
- recovery health check succeeded/failed;
- timeout termination requested;
- forced termination requested after the grace period;
- operation completed, rolled back, or failed.

Activity events are coalesced to at most one event every fifteen seconds. They report only that the fixed boundary produced activity and when; raw output is not copied. The UI can therefore distinguish an active download from a silent boundary without parsing Docker's presentation format.

## Bounded process supervision

The updater launches the fixed management command in a new Linux process session. The existing five-minute command deadline remains authoritative.

At the deadline:

1. Record `command_timeout`.
2. Send `SIGTERM` to the complete updater command process group.
3. Wait a fixed five-second grace period.
4. Send `SIGKILL` to the process group if anything remains.
5. Bound output-reader shutdown; close the parent pipe and continue even if a defective descendant retained another descriptor.
6. Persist the operation as `failed` with the fixed reason `update_command_timeout`.

The output reader can never control worker completion. All waits—process, termination grace, reader shutdown, Compose recreation, and health verification—have explicit bounds covered by tests.

Output-reader callbacks never write the main update journal. They advance the current stage in memory and append observational trace events; the worker alone persists the final journal, including the latest accepted stage. Protocol v3 derives live progress from the sanitized trace when necessary, and trace projection reads are non-blocking so stalled observational storage cannot delay terminal status.

The service must not kill the Docker daemon. Only the fixed updater command subtree is terminated. Docker Engine remains responsible for other host containers.

## Container restart and postconditions

The existing update command uses `docker compose up -d reachcommander`, which recreates the application container when the pinned image changes. Restarting Docker Engine is neither required nor allowed.

After Compose returns, the updater obtains:

- the local immutable image identity for the resolved digest; and
- the image identity of the running `reachcommander` container.

The identities must match exactly before health checking begins. The container must then reach Docker `healthy` within the existing bounded health window. A mismatch, missing container, unhealthy result, or timeout is a candidate failure and starts the existing rollback path. Rollback applies the same identity and health postconditions to the previous image. Success is impossible unless both the immutable image and health postconditions are proven.

## Protocol and API

Updater protocol v3 adds a bounded `trace` object to status responses while retaining all v2 fields:

- `startedAt`;
- `elapsedSeconds`;
- `lastActivityAt`;
- up to the latest 32 public events, each containing only sequence, timestamp, elapsed seconds, allowlisted public event code, stage, and outcome.

The updater continues to answer exact v1 and v2 requests with their existing field sets. The ASP.NET gateway attempts v3, then performs the existing strict compatibility fallbacks. The public ReachCommander API remains backward compatible by adding nullable trace data to the status DTO.

The gateway validates maximum sizes, strict field sets, sequence ordering, timestamps, enum values, operation identity consistency, and monotonic elapsed time. Invalid trace data makes the updater response incompatible; it is not partially trusted or displayed. The API never synthesizes host events and never forwards root-only diagnostic fields.

A protocol-v1 or protocol-v2 helper continues to update safely. The UI keeps the existing generic or stage checklist and explains that the checksum-verified Ubuntu installer must be refreshed to enable detailed diagnostics. A container update cannot replace the root-owned helper.

## Browser experience

The blocking update overlay keeps the existing ordered progress checklist and adds an expandable **Technical details** section. It contains:

- operation start time;
- total elapsed time, updated locally between polls;
- last confirmed host activity time;
- a chronological list of sanitized events with relative elapsed time;
- a fixed warning when the active boundary has reported no activity for sixty seconds;
- a fixed timeout/failure explanation and the root command to run when attention is required.

The trace is visible only through the existing authenticated administrator API. It is text, keyboard accessible, screen-reader labelled, usable at the supported compact viewport, compatible with both themes, and static under reduced-motion preferences. The timeline is bounded in height and scrolls inside the full-screen overlay.

No browser control can request an arbitrary operation ID, host file, command, or log range. The UI displays only the current operation trace supplied with system-update status.

## Root management command

`sudo reachcommander update-log` prints the most recent trace in a stable, human-readable form. `--follow` follows the active trace until a terminal event. The command supports only those two forms; arbitrary paths, operation IDs, tail counts, and shell fragments are rejected.

Output includes exact timestamps, elapsed time, event codes, stage outcomes, exit codes, deadlines, and image-verification results that were already written to the protected trace. It does not invoke user-selected commands or print environment variables, source configuration, authentication files, or application logs.

When no trace exists, the command exits successfully with a clear message. Malformed or unsafe trace storage exits non-zero without printing file contents and directs the operator to `sudo reachcommander doctor`. The doctor command validates trace directory ownership, modes, filename shape, file type, per-file bound, count, and total size without mutating trace state.

## Failure and recovery behavior

- A five-minute updater-command timeout terminates the complete process subtree and becomes a terminal `failed` result.
- Loss of the browser connection does not stop the host update; polling resumes against the durable trace after the backend returns.
- Host service restart while a worker is active retains the existing interrupted-operation behavior and records a fixed interruption event when safe.
- Candidate identity or health failure enters rollback.
- Previous-image identity or health failure becomes terminal `failed` and requires administrator recovery.
- Trace write failure never fabricates success or suppresses the durable update result.
- Invalid public trace data is rejected at the host/API boundary and never rendered.

For a currently stuck installation that predates protocol v3, the available evidence remains:

```bash
sudo journalctl -u reachcommander-updater.service --since today
sudo reachcommander doctor
```

Installing the new checksum-verified bundle upgrades the root-owned helper and enables future structured traces; it cannot reconstruct events for an operation that already occurred.

## Testing strategy

### Python updater tests

- Red test reproducing a timed-out wrapper whose descendant retains the output pipe.
- Process-group `SIGTERM`, grace, `SIGKILL`, and bounded reader completion.
- Ordered trace events, coalesced activity, timestamps, elapsed time, and terminal events.
- Trace schema rejection, atomic/append-safe permissions, symlink rejection, per-file bound, count retention, and ten-megabyte pruning.
- Protocol-v3 strict response and exact v1/v2 response compatibility.
- Container image identity success, mismatch, missing container, and rollback verification.

### Installer command tests

- `update-log` and `update-log --follow` argument contracts.
- Safe no-trace behavior and malformed-storage failure.
- Doctor validation of the protected trace tree.
- Packaging and installer upgrade of the v3 helper without altering account, key, source, or durable file-operation state.

### .NET tests

- Strict protocol-v3 parsing and v3-to-v2-to-v1 fallback.
- Rejection of oversized, unordered, inconsistent, or unknown public trace data.
- Monotonic trace publication only for the matching operation.
- Authenticated API serialization without root-only fields.

### Angular and browser tests

- Elapsed time, last activity, event ordering, stale warning, terminal timeout, and legacy-helper copy.
- Technical details expansion, accessibility, compact viewport, both themes, reduced motion, reconnection, and completed-shell activation.
- No raw command, digest, path, registry output, or diagnostic fields appear in the DOM.

### Full verification

- Python installer suites.
- Bash installer and management-command suites on Ubuntu.
- .NET unit and integration suites on Windows and Ubuntu.
- Angular unit, production build, PWA, and Playwright acceptance suites.
- ShellCheck and `systemd-analyze verify` in Ubuntu CI.

## Acceptance criteria

1. An updater wrapper with a descendant retaining the output pipe cannot leave the journal in `applying` beyond the command deadline plus termination grace and bounded persistence time.
2. The browser shows the active boundary, operation elapsed time, last confirmed activity, and sanitized ordered events without exposing raw host data.
3. `sudo reachcommander update-log` provides a timestamped root-only trace for the latest operation; `--follow` follows an active operation.
4. Retention never exceeds ten traces or ten megabytes, and unsafe filesystem entries are never followed or deleted.
5. Candidate success requires the running container image identity to match the resolved immutable image and Docker health to be `healthy`.
6. Rollback success requires the same identity and health proof for the previous image.
7. The updater never restarts Docker Engine.
8. Protocol-v1 and protocol-v2 helpers remain functional, with clear UI guidance that detailed traces require the refreshed installer helper.
9. Authentication, antiforgery, rate limiting, source confinement, updater socket restrictions, and non-root application execution remain enabled.
