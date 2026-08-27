# ReachCommander Detailed Update Progress Design

**Status:** Approved design  
**Date:** 2026-08-27  
**Scope:** Truthful, host-reported progress details in the blocking Ubuntu system-update screen

## Summary

ReachCommander will extend its blocking system-update screen with an ordered progress list. The list will report actual updater work—connecting, downloading, installing, restarting, health checking, and activating—rather than rotating messages based on elapsed time. If the candidate is unhealthy, the screen will switch to explicit recovery stages for restoring, restarting, and verifying the previous version.

Detailed progress will use a backward-compatible updater protocol extension. New application containers continue to work with an existing protocol-v1 Ubuntu helper and show a safe generic applying state. A refreshed helper supports protocol v2 and supplies detailed stages while continuing to answer protocol-v1 requests with the exact existing response shape. Update progress is observational only: loss or rejection of a progress marker must never change the success, rollback, or failure outcome of the existing update transaction.

## Goals

- Show an ordered, accessible checklist beneath the existing updater explanation.
- Report only stages confirmed by the trusted host updater or directly observed by the client.
- Preserve updates across the application-container restart and browser reconnection.
- Keep protocol-v1 installations operational without forcing a coordinated host-helper upgrade.
- Preserve the existing update lock, mutation drain, health check, rollback, timeout, and terminal-result behavior.
- Prevent raw Docker output, host paths, commands, and untrusted error text from reaching the API or browser.
- Support both ReachCommander themes, compact PWA layouts, and reduced-motion preferences.

## Non-goals

- Byte-level download progress, percentage estimates, remaining-time estimates, or transfer-speed reporting.
- Streaming raw host logs to Angular.
- Making progress-reporting failures affect the update transaction.
- Automatically replacing root-owned updater binaries from inside the application container.
- Adding detailed progress to unsupported deployments or non-Ubuntu update paths.
- Changing unattended-update policy or removing administrator confirmation.

## Recommended architecture

The host updater protocol gains version 2. Protocol negotiation is explicit and asymmetric so either component can be upgraded first:

1. A new backend first sends a protocol-v2 request.
2. A new helper accepts v1 and v2. It returns the existing response schema for v1 and the detailed schema for v2.
3. An old helper rejects the v2 request using its existing protocol-incompatible response.
4. The new backend recognizes that bounded response and retries once with protocol v1.
5. An old backend continues sending v1 and therefore remains compatible with a new helper.

The v2 response adds one allow-listed logical field, `progressStage`, which is nullable outside an active update and when no detailed stage is known. It does not include progress text supplied by the shell. Each layer maps the enum-like token to its own fixed public copy.

The installed `reachcommander update` command emits fixed, structured progress markers separately from ordinary command output. The updater service consumes only exact allow-listed tokens, associates them with the current operation ID, and atomically updates the existing host journal. Raw command output remains bounded and host-local.

The backend operation monitor publishes validated intermediate snapshots to the coordinator as well as the terminal snapshot. The coordinator accepts progress only for the active operation, prevents backward transitions, and retains the last valid stage through transient updater failures. The new application container reads the same host journal after restart, so progress resumes rather than resetting.

## Components

### Installed management command

The Ubuntu management command emits markers at transactional boundaries that it already owns:

- `downloading`: immediately before resolving and pulling the configured trusted image;
- `installing`: after the trusted digest and display version are validated, before deployment state is changed;
- `restarting`: immediately before starting the candidate container;
- `healthChecking`: after the candidate is started, while waiting for its health result;
- `restoring`: before restoring the protected deployment backup;
- `restartingPrevious`: immediately before starting the restored container;
- `verifyingRecovery`: while waiting for the restored container to become healthy.

Markers use a fixed prefix and fixed tokens. They are not accepted from CLI arguments, environment-selected arbitrary text, Docker output, or browser input. Manual command execution may show human-readable progress, but the structured markers remain machine-parseable and contain no sensitive data.

### Host updater service and journal

The updater service runs the fixed management command with line-oriented output capture. It recognizes only exact progress-marker lines and sends valid tokens to the journal. All other output follows the existing bounded capture and sanitization rules.

The journal schema records the current `progressStage` and updates `updatedAt` atomically. Stage validation follows a transition table with two branches:

```text
downloading -> installing -> restarting -> healthChecking -> completed
                                  |                |
                                  +----------------+-> restoring -> restartingPrevious
                                                                  -> verifyingRecovery
                                                                  -> rolledBack | failed
```

Terminal update phases remain `completed`, `rolledBack`, and `failed`. The progress stage supplements the existing broad `applying` phase; it does not replace transactional phases or reason codes.

Unknown, malformed, repeated, or backward-moving markers are ignored and logged using fixed safe messages. A missing marker leaves the last valid stage unchanged. Progress persistence failure is recorded operationally but cannot terminate or roll back the update by itself.

### ASP.NET Core backend

The Unix updater gateway supports protocol-v2 negotiation with one bounded fallback to protocol v1. Protocol-v2 responses must contain the exact v2 field set and a valid stage/phase combination. Protocol-v1 parsing retains its current exact-schema validation.

The application model and API DTO add a nullable progress-stage value. The backend never forwards host-supplied display text. It maps tokens to fixed status data and preserves the current sanitized `detail` behavior.

While the updater reports `applying`, the operation monitor delivers each accepted snapshot to the coordinator. The coordinator updates cached state only when:

- protocol and schema validation succeeded;
- the operation ID equals the active operation;
- the broad phase is valid;
- the stage is a permitted forward transition or recovery transition.

Transient socket, discovery, or container-restart failures retain the current operation and last known progress. Existing terminal-result precedence prevents a late applying snapshot from replacing a completed, rolled-back, or failed result.

### Angular client

The Angular store adds a client-only `connecting` presentation before the Apply response returns. This describes the browser contacting the authenticated ReachCommander API; it is not presented as a host-reported installation step. Once a protocol-v2 snapshot arrives, the store uses only the API progress stage. During expected connection loss, `restarting` remains active and the supporting copy explains that automatic reconnection is in progress.

Protocol-v1 fallback is explicit. The UI shows a single `Applying trusted update` item instead of presenting unconfirmed detailed stages. This fallback does not block updates and does not ask the administrator to intervene during an active transaction.

## UX goal

The screen should reassure an administrator that the update is advancing, communicate why temporary unavailability is expected, and make automatic recovery visible without suggesting precision the updater does not possess.

## Screen structure

The existing full-screen overlay, animated rings, eyebrow, title, and explanatory paragraph remain. A compact ordered progress list appears immediately below the paragraph.

For a protocol-v2 healthy update, the user-facing sequence is:

1. Connecting to update service
2. Downloading verified image
3. Installing update
4. Restarting ReachCommander
5. Checking system health
6. Activating updated application

Completed items display a checkmark. The current item uses the theme accent and a small animated indicator. Future items remain visually muted. The active label is announced through a polite live region without re-announcing the entire modal on every poll.

`Activating updated application` is a client-observed final step: it starts only after the backend reports a healthy completed operation and while the PWA service activates the matching shell. It is not added to the host protocol.

If candidate restart or health checking fails, the standard list stops advancing and a recovery group becomes visible:

1. Restoring previous version
2. Restarting previous version
3. Verifying recovery

A successful recovery ends in the existing `Previous version restored` state. An unsuccessful recovery ends in the existing administrator-attention state with `reachcommander doctor` guidance.

The layout remains centered within the current width limit, uses normal document flow rather than fixed heights, and can scroll vertically on a short mobile viewport. Standard and Norton themes receive equivalent semantic states. Reduced-motion mode removes spinner and active-step animation without removing text or status indicators.

## Data flow

```text
Angular Apply
  -> client shows Connecting
  -> ASP.NET Core negotiates updater protocol
  -> host service starts fixed update command
  -> command emits allow-listed stage marker
  -> service validates and atomically journals stage
  -> backend monitor polls v2 snapshot
  -> coordinator accepts same-operation forward progress
  -> authenticated API returns progressStage
  -> Angular updates checklist
  -> container restarts and browser reconnects
  -> new backend resumes from the same host journal
  -> terminal result activates the new PWA shell or shows recovery guidance
```

Polling cadence, reconnect backoff, and the existing bounded operation-monitor timeout remain unchanged unless testing demonstrates that stage delivery needs a smaller internal poll interval. Browser count does not multiply GitHub, GHCR, or host update work.

## Error handling

- A v2 negotiation failure caused by an old helper triggers one v1 retry and generic progress.
- A malformed v2 response fails closed through the existing incompatible-updater behavior; it is not silently treated as v1.
- Progress loss retains the last valid stage and changes supporting copy to `Update still in progress.`
- Expected backend disconnection keeps the overlay blocking and explains automatic reconnection.
- An operation-ID mismatch, phase regression, or stage regression is ignored and cannot replace current state.
- The existing six-minute recovery timeout remains authoritative when no terminal result can be recovered.
- Candidate failure continues through the existing backup restoration and health verification transaction.
- Raw subprocess output, Docker logs, exception messages, physical paths, image digests, and stack traces never become progress labels or API details.

## What remains simple

- Progress is a small finite-state model, not a general event stream.
- The existing polling transport remains; no SignalR, Server-Sent Events, or WebSocket channel is introduced.
- No database is added. The existing protected atomic host journal remains the durable source of truth.
- No numeric percentage or timing estimate is synthesized.
- Only the Ubuntu installer-managed update path gains host-reported stages.

At substantially higher browser concurrency, clients still poll the cached backend state rather than the host service directly. The host operation count remains one, so this feature adds negligible scaling pressure. The hard-to-reverse decision is the protocol-v2 contract; its field set and stage tokens must therefore remain strict and versioned.

## Migration path

Existing protocol-v1 Ubuntu helpers remain supported. After a container update introduces this UI, they show generic progress. Detailed host stages become available after the administrator refreshes the Ubuntu installer bundle once, consistent with the existing system-update design's host-helper migration boundary.

Fresh installations from the new bundle receive protocol-v2 progress immediately. Refreshing the helper before updating the container is also safe because the new helper answers the old backend's protocol-v1 requests with the exact v1 response schema.

Documentation will explain that generic progress means the installed host helper predates detailed reporting; it does not mean the update is stalled.

## Test strategy

### Host and protocol tests

- Protocol-v1 requests receive the exact v1 response shape from the new helper.
- Protocol-v2 requests receive the exact v2 response shape and nullable stage.
- The new backend falls back once when an old helper rejects v2.
- Invalid protocol versions, response fields, stage tokens, phase/stage pairs, and oversized messages fail closed.
- Each management-command boundary emits its expected marker.
- The updater journal accepts valid forward and rollback transitions and rejects or ignores regressions safely.
- Malformed markers and ordinary Docker output cannot affect public progress.
- Progress write failures do not alter command exit-code mapping or rollback behavior.

### Backend tests

- Intermediate snapshots are published for the active operation only.
- Same-operation progress advances monotonically.
- Operation-ID mismatches, late applying snapshots, and stage regressions cannot overwrite authoritative state.
- Startup recovery reconstructs the latest detailed stage from the host journal.
- Transient updater failures retain applying state and the last valid stage.
- Protocol-v1 fallback maps to applying with no detailed progress stage.
- Terminal completion, rollback, failure, mutation-drain release, and monitoring timeout retain existing behavior.
- API serialization contains only the nullable logical stage and sanitized public data.

### Angular and browser tests

- The optimistic connecting item appears immediately after confirmation.
- Protocol-v2 stages mark completed, current, and pending items correctly.
- Reconnection keeps restart active and never regresses the list.
- Completion shows activation before the existing one-time PWA reload.
- Rollback reveals recovery stages and ends with restored-version guidance.
- Protocol-v1 status renders one generic applying item.
- Stale progress renders `Update still in progress` without implying failure.
- Standard and Norton themes, compact widths, short viewports, keyboard behavior, live-region announcements, and reduced motion remain usable.
- Existing successful, rollback, failure, and reconnect acceptance scenarios remain green.

## Acceptance criteria

- A clean installer-managed Ubuntu deployment shows real ordered update stages reported by the host.
- The screen never advances to downloading, installing, restarting, or health checking without the corresponding trusted boundary being reached.
- Progress survives the candidate-container restart and continues after browser reconnection.
- A rollback displays truthful recovery stages and retains the existing terminal guidance.
- A protocol-v1 helper can still perform updates with generic progress.
- An old application backend continues working with a protocol-v2-capable helper.
- Progress-reporting failure cannot change the update transaction's success, rollback, or failure result.
- No raw host output or sensitive host data is exposed through the API or UI.
- The progress list is accessible and usable in both themes and supported compact PWA layouts.
