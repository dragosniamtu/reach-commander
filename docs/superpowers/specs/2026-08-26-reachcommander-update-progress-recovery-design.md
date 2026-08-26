# ReachCommander Update Progress Recovery Design

**Status:** Approved design
**Date:** 2026-08-26
**Scope:** Recover full-stack Ubuntu updates from stale `applying` state and make the full-screen update animation visibly active

## Summary

ReachCommander will make the backend, rather than the browser, responsible for following an accepted host update through a terminal result. A lifecycle-managed operation monitor will reconcile the host updater journal until the update completes, rolls back, fails, or exceeds a bounded recovery deadline. This prevents the full-screen **Updating ReachCommander** state and the mutation drain from remaining active indefinitely when the old application container stays alive or a replacement container starts while the host updater is still working.

The two rings in the full-screen update overlay will receive a clearer counter-rotating animation. The animation will continue to respect the operating system's reduced-motion preference.

## Problem statement and root cause

The host updater bounds its update command to five minutes and writes an atomic terminal journal entry for success, rollback, interruption, or failure. The application currently caches the `applying` response returned by `applyConfiguredChannel`, but `GET /api/system-update` only returns that cached state. It does not reconcile the updater journal while applying.

This creates two failure paths:

1. If the host update fails before Docker replaces the old container, that container remains alive and returns its stale in-memory `applying` state forever, even after the host journal records failure.
2. If Docker starts a replacement container while the host updater is still applying, the replacement can discover `applying` during startup and then wait for the normal six-hour discovery interval before checking again.

The Angular store receives successful `applying` responses in both cases, so its reconnect-attempt protection never fires. The overlay remains on **Updating ReachCommander**, and the original process can also leave the mutation gate drained.

The existing overlay rings already have basic CSS rotation, but their narrow border arcs can be visually indistinct. They intentionally stop under `prefers-reduced-motion: reduce`.

## Goals

- Make the backend authoritative for following an accepted update to a terminal state.
- Resume monitoring automatically after an application-container restart.
- Release the mutation drain exactly once when the operation terminates or recovery reaches its deadline.
- Tolerate temporary updater-socket loss during container replacement.
- Prevent `applying` from remaining indefinitely when the updater becomes unreachable.
- Preserve the updater's existing privilege boundary, fixed protocol, journal, health check, and rollback behavior.
- Make the two full-screen progress rings clearly animate under normal motion settings.
- Preserve reduced-motion accessibility and existing safe, sanitized user messages.

## Non-goals

- Adding a percentage progress estimate; the updater does not expose reliable granular progress.
- Allowing the browser to cancel, select, or modify an update operation.
- Changing the small toolbar update icon animation.
- Changing update channels, release discovery, image verification, installer ownership, or Docker orchestration.
- Expanding system updates beyond installer-managed Ubuntu deployments.
- Exposing updater logs, Docker output, host paths, or internal exception messages in the API or UI.

## Considered approaches

### Frontend watchdog only

The browser could replace `applying` with a local failure after a deadline. This would unstick the visible overlay but would leave the backend's cached state and mutation drain unchanged. It is not an authoritative recovery mechanism.

### Request-driven backend reconciliation

`GET /api/system-update` could call the host updater whenever the cached state is `applying`. This is a small change, but recovery and drain release would depend on an authenticated browser continuing to poll. Browser count would also drive host-socket traffic.

### Backend-owned operation monitor — selected

The coordinator owns one monitor for the active operation. It begins after an accepted Apply and is also started when normal startup discovery observes `applying`. The browser remains a consumer of cached public state. This works when the browser closes, coalesces host checks, and gives the backend a single place to release the mutation drain.

## Backend design

### Monitor lifecycle

`SystemUpdateCoordinator` will own at most one lifecycle-managed monitor task for an active operation ID.

- When `ApplyAsync` maps the host response to `applying`, it retains the mutation drain and starts or joins the monitor for that operation.
- When startup or scheduled discovery maps a host snapshot to `applying`, it starts or joins the same monitor path. This covers a new container that starts mid-update.
- Repeated API reads, checks, or duplicate applying snapshots do not create duplicate monitor tasks.
- The monitor is tied to the hosted service lifetime and stops cleanly during application shutdown.
- Normal `GET /api/system-update` remains a cached, read-only operation and does not multiply host checks with browser count.

The monitor requests the fixed `check` action from the existing `ISystemUpdaterGateway` approximately once per second. A snapshot for the active operation is mapped through the existing protocol validation and public-detail sanitization. Terminal snapshots are `completed`, `rolledBack`, or `failed`.

### Operation identity and concurrency

The operation ID returned by Apply is the identity of the monitor. Applying or terminal snapshots with the same operation ID advance the operation. A snapshot for a different operation must not silently complete the active operation; it is treated as an inconsistent/transient observation and retried within the recovery window. Existing Apply serialization continues to reject concurrent update requests.

Status replacement and monitor ownership will be synchronized so that an older monitor cannot overwrite a newer operation or a newer discovery result. Terminal handling is idempotent. The mutation gate's `CancelDrain` action is invoked exactly once for the drain owned by the current process.

### Recovery timing

The host update command has a five-minute timeout. Backend monitoring uses a six-minute overall deadline, providing one minute for startup, journal publication, socket interruption, and reconnection.

- Temporary socket, response-timeout, or protocol-availability errors retain the last safe `applying` state and retry.
- A valid terminal host snapshot ends monitoring immediately.
- If no valid terminal snapshot can be obtained by the six-minute deadline, the coordinator publishes a sanitized `failed` state and releases the mutation drain.
- The failure detail instructs the administrator to run `reachcommander doctor` without exposing host output or internal exception text.

The deadline is a recovery bound, not a command cancellation mechanism. The browser receives no capability to terminate the privileged host transaction.

### Background scheduling

The normal startup/six-hour discovery loop remains responsible for update availability. When it observes `applying`, it starts the short-interval monitor instead of allowing that status to sleep until the next six-hour check. An accepted Apply explicitly wakes monitoring even if the normal discovery loop is currently waiting.

The existing Unix-socket connect and response timeouts remain in force for every check, so one monitor request cannot block indefinitely.

## Angular and overlay behavior

The Angular store continues to poll the cached status endpoint while the public phase is `applying`. It does not become responsible for the host operation or its deadline.

State transitions remain:

- `applying`, API reachable: **Updating ReachCommander**;
- `applying`, API temporarily unreachable: **Reconnecting to ReachCommander**;
- `completed`: activate the matching PWA service-worker shell and reload once;
- `rolledBack`: explain that the previous version was restored and offer **Return to ReachCommander**;
- `failed`, including the recovery deadline: show safe administrator guidance and offer **Return to ReachCommander**.

The overlay remains blocking while `applying`. It will not display a fabricated percentage or provide a cancel button.

## Ring animation

Only the two rings in `SystemUpdateOverlayComponent` are changed.

- The outer and inner rings use stronger, clearly visible arcs.
- The rings rotate in opposite directions at distinct, steady speeds.
- Rotation uses centered transforms and compositor-friendly properties.
- The animation remains decorative and hidden from assistive technology; the alert-dialog heading and text communicate the semantic state.
- Under `prefers-reduced-motion: reduce`, rotation is disabled and the rings remain as high-contrast static progress symbols. The live text continues to state that the update is running.
- Both the default and Norton themes retain sufficient contrast by deriving colors from the existing accent and warning tokens.

## Error handling and security

- Transient gateway errors are logged using exception types only and are not exposed to Angular.
- Protocol-incompatible or malformed responses cannot advance the active operation.
- The existing fixed updater actions and Unix-socket privilege boundary remain unchanged.
- The application still never receives the Docker socket, arbitrary host commands, image selectors, channels, URLs, or paths from the browser.
- An operation-monitor timeout produces a public failure state only; it does not claim the host transaction succeeded or rolled back.
- Terminal-state and drain-release behavior is idempotent across concurrent status reads and hosted-service activity.

## Test strategy

### Backend unit tests

- Accepted Apply starts one monitor and preserves the mutation drain while applying.
- `applying -> completed`, `applying -> rolledBack`, and `applying -> failed` update cached state automatically.
- Every terminal path releases the process-owned mutation drain exactly once.
- Temporary updater unavailability retries while retaining applying state.
- Permanent unavailability reaches the six-minute bounded failure state and releases the drain.
- Startup discovery of an existing applying journal resumes monitoring.
- Repeated reads/checks and repeated applying snapshots do not create duplicate monitors.
- Mismatched operation IDs cannot complete the active operation.
- Hosted-service shutdown cancels monitoring without publishing a fabricated result.
- Existing public-detail sanitization and Apply serialization remain intact.

Tests will use the existing injected delay/time provider and deterministic fake gateways; they will not wait on wall-clock minutes.

### Frontend and browser tests

- The update overlay renders both progress rings during applying and reconnecting states.
- Normal-motion browser acceptance verifies non-`none` counter-rotating animation names/directions.
- Reduced-motion browser acceptance verifies that ring animation is disabled while progress text remains present.
- Completed state still triggers PWA activation/reload only once.
- Rollback and bounded failure show the correct terminal guidance and dismissal action.

### Verification

- Targeted `SystemUpdateCoordinatorTests` and related backend integration tests.
- Complete backend test suites on supported local targets.
- Angular unit tests and production build.
- Targeted Playwright system-update acceptance, including normal and reduced motion.
- Existing CI remains the final cross-platform and Ubuntu-browser gate.

## Acceptance criteria

- An accepted update is reconciled by the backend without requiring a browser to remain open.
- A replacement container that starts while the journal is applying resumes monitoring automatically.
- A terminal host journal result appears in the API and UI promptly.
- Temporary updater disconnection does not prematurely fail an otherwise active operation.
- The UI cannot remain on **Updating ReachCommander** beyond the bounded recovery window when no valid terminal status can be recovered.
- The mutation drain is released exactly once after completion, rollback, failure, or recovery timeout.
- Browser polling does not multiply updater-journal checks.
- The two full-screen rings visibly counter-rotate under normal motion settings.
- Reduced-motion settings disable rotation without removing progress communication.
- No additional host authority, browser-controlled updater input, or sensitive diagnostic disclosure is introduced.
